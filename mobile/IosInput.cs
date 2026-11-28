using System;
using System.Collections.Generic;
using System.Reflection;
		using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace PortMobile
{
	/// <summary>
	/// Waylith's IOS backend receives UIKit touches in IOSWindow.HandleTouches. PortMobile's touch
	/// layer normally drains the GLFW Android queue, so on the IOS backend those raw events would
	/// bypass every gesture rule. Take the raw events over, feed them to Touch, and send the mouse
	/// actions Touch decides on back through WindowBase's injection entry points.
/// The same for keys and text (the key wheel, the latches, the soft keyboard): the shim's GLFW event queue has
	/// no reader under the IOS backend, so keys go through the platform's input injector, and the launcher's hidden
	/// keyboard field moves into the engine's view (glfwIosEngineView): its commits come back through the touch queue; the
/// engine's deleteBackward is patched into a Backspace key (the original reads IME state the port never feeds).
	/// </summary>
	internal static class IosInput
	{
		private const int Down = 1, Move = 2, Up = 3, Cancel = 4;
		private const int MouseKind = 1, TouchKind = 2;

		private static Harmony harmony;
		private static bool injectionReady;
		private static bool injectionFailed;
		private static bool rawTouchHooked;
		private static bool touchHookFailed;
		private static int firstRawTouchLogged;
		private static int rawEvents;

		private static object engineWindow;
		private static double windowScale = 1;
		private static MethodInfo pointerMove, pointerButton, pointerScroll, scrollLines;

		private static Type logicalType, pointerIdType, pointerKindType, pointerButtonType;
		private static object mouseId, mouseKind, touchKind;

		private static MethodInfo foundationCount, foundationObjectAt;

		// ---- keys and the soft keyboard ----
		private const int GlfwBackspace = 259;
		private static bool keyInjectionFailed;
		private static object inputInjector;
		private static MethodInfo injectorKeyDown, injectorKeyUp, injectorTypeText;
		private static Type physicalKeyType;
		private static readonly Dictionary<int, object> physicalKeys = new Dictionary<int, object>();
		private static readonly Dictionary<int, string> physicalKeyNames = new Dictionary<int, string>();
		private static readonly HashSet<int> unknownKeys = new HashSet<int>();
		private static int keysInjected, charsInjected;
		[DllImport("glfw")] private static extern void glfwIosEngineView(IntPtr view);
		private static Type uiTouchType;
		private static ConstructorInfo uiTouchConstructor;
		private static PropertyInfo uiTouchTypeProperty;
		private static MethodInfo uiTouchLocation;

		public static void Start(Harmony source)
		{
			if (!OperatingSystem.IsIOS())
			{
				return;
			}

			harmony = source;
			PatchRawTouches();
			if (rawTouchHooked)
			{
				PatchNativeGestures("HandleScroll");
				PatchNativeGestures("HandlePinch");
				PatchNativeGestures("HandleRotate");
				PatchNativeGestures("HandleSmartZoom");
			}
			PatchDeleteBackward();
			RefreshEngineWindow();
		}

		private static void PatchRawTouches()
		{
			try
			{
				Type owner = AccessTools.TypeByName("Waylith.Windowing.Backends.IOS.IOSWindow");
				MethodInfo raw = AccessTools.Method(owner, "HandleTouches");
				if (raw == null)
				{
					throw new MissingMethodException(owner?.FullName ?? "IOSWindow", "HandleTouches");
				}
				harmony.Patch(raw, prefix: new HarmonyMethod(typeof(IosInput), nameof(BeforeHandleTouches)));
				rawTouchHooked = true;
				Console.WriteLine("[mobile] ios touch bridge: HandleTouches patched");
			}
			catch (Exception e)
			{
				touchHookFailed = true;
				Console.WriteLine("[mobile] ios touch bridge failed: " + e.Message);
			}
		}

		private static void PatchNativeGestures(string name)
		{
			try
			{
				Type owner = AccessTools.TypeByName("Waylith.Windowing.Backends.IOS.IOSWindow");
				MethodInfo method = AccessTools.Method(owner, name);
				if (method == null)
				{
					throw new MissingMethodException(owner?.FullName ?? "IOSWindow", name);
				}
				harmony.Patch(method, prefix: new HarmonyMethod(typeof(IosInput), nameof(Skip)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] ios touch bridge: native " + name + " left on (" + e.Message + ")");
			}
		}

		private static bool Skip()
		{
			return !touchHookFailed && !injectionFailed && injectionReady;
		}

		/// <summary>
		/// The engine's deleteBackward reads IME state (SurroundingText and friends) the port never feeds, so the
		/// soft keyboard's backspace is swallowed (0.4.53: letters arrived, deletion never did). Patch it into a
		/// Backspace key for the game — the 0.4.52 prefix of the same method proved the injection reaches it. While
		/// the bridge is not up, let the original run instead of eating the deletion.
		/// </summary>
		private static void PatchDeleteBackward()
		{
			try
			{
				Type owner = AccessTools.TypeByName("Waylith.Windowing.Backends.IOS.IOSWindow");
				MethodInfo method = AccessTools.Method(owner, "HandleDeleteBackward");
				if (method == null)
				{
					throw new MissingMethodException(owner?.FullName ?? "IOSWindow", "HandleDeleteBackward");
				}
				harmony.Patch(method, prefix: new HarmonyMethod(typeof(IosInput), nameof(BeforeDeleteBackward)));
				Console.WriteLine("[mobile] ios text bridge: deleteBackward patched (a Backspace key for the game)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] ios text bridge: deleteBackward left to the engine (" + e.Message + ")");
			}
		}

		private static bool BeforeDeleteBackward()
		{
			if (!EngineReady() || keyInjectionFailed || inputInjector == null)
			{
				return true; // the bridge is not up; let the engine try
			}
			Backspace();
			return false;
		}

		/// <summary>Returns false for finger-only sets, whose engine handling is now Touch's injected mouse.</summary>
		private static bool BeforeHandleTouches(object __instance, object[] __args)
		{
			if (touchHookFailed || injectionFailed || __args == null || __args.Length < 3 || !EngineReady())
			{
				return true;
			}
			int events = ++rawEvents;
			if (events % 100 == 0)
			{
				Console.WriteLine("[mobile] ios touch bridge: " + events + " raw events consumed");
			}

			bool hasMouse = false;
			var queued = new List<(int kind, int id, Vector2 pos)>();
			try
			{
				if (foundationCount == null)
				{
					Type foundation = AccessTools.TypeByName("Waylith.Windowing.Shared.Apple.Interop.Foundation");
					foundationCount = AccessTools.Method(foundation, "Count");
					foundationObjectAt = AccessTools.Method(foundation, "ObjectAt");
					uiTouchType = AccessTools.TypeByName("Waylith.Windowing.Shared.Apple.Bindings.UITouch");
					uiTouchConstructor = AccessTools.Constructor(uiTouchType, new[] { typeof(nint) });
					if (uiTouchConstructor == null)
					{
						throw new MissingMethodException(uiTouchType?.FullName ?? "UITouch", ".ctor(nint)");
					}
					uiTouchTypeProperty = uiTouchType.GetProperty("Type");
					uiTouchLocation = uiTouchType.GetMethod("LocationInView");
				}

				object set = __args[0];
				object array = set.GetType().GetProperty("AllObjects").GetValue(set);
				int count = (int)foundationCount.Invoke(null, new[] { array });
				object view = AccessTools.Field(__instance.GetType(), "_view").GetValue(__instance);
				double scale = ScaleOf(__instance);
				int phase = Convert.ToInt32(__args[2]);
				int kind = phase == 0 ? Down : phase == 1 ? Move : phase == 2 ? Up : Cancel;

				for (int i = 0; i < count; i++)
				{
					object touch = foundationObjectAt.Invoke(null, new[] { array, i });
					nint handle = (nint)touch.GetType().GetProperty("Handle").GetValue(touch);
					object nativeTouch = uiTouchConstructor.Invoke(new object[] { handle });
					// IntPtr (nint) does not implement IConvertible, so Convert.ToInt64(object)
					// throws InvalidCastException on the boxed UITouch.Type value; unbox directly.
					long nativeTouchType = (long)(nint)uiTouchTypeProperty.GetValue(nativeTouch);
					if (nativeTouchType == 3)
					{
						hasMouse = true;
						continue;
					}

					object point = uiTouchLocation.Invoke(nativeTouch, new[] { view });
					float x = (float)Number(point, "X");
					float y = (float)Number(point, "Y");
					Vector2 pixels = new Vector2((float)(x * scale), (float)(y * scale));
					int id = (int)(handle.ToInt64() & 0x7fffffff);
					queued.Add((kind, id, pixels));
					if (Interlocked.Exchange(ref firstRawTouchLogged, 1) == 0)
					{
						Console.WriteLine("[mobile] ios touch bridge: first touch at " + pixels + " (scale " +
							scale.ToString("0.0") + ")");
					}
				}

				foreach (var touch in queued)
				{
					// Outside Drain: the next frame must know the fingers moved, or its mouse stops following them.
					Touch.ApplyOutsideDrain(touch.kind, touch.id, touch.pos);
				}
			}
			catch (Exception e)
			{
				if (!touchHookFailed)
				{
					touchHookFailed = true;
					Console.WriteLine("[mobile] ios touch bridge: raw touch conversion failed: " + e);
				}
				return true;
			}
			return hasMouse;
		}

		/// <summary>The engine's key injector, once the window is known; a failure here leaves the touch bridge alone.</summary>
		private static void ResolveKeys(Type platformType, object platform)
		{
			try
			{
				PropertyInfo injectorProperty = FindProperty(platformType, "InputInjector");
				inputInjector = injectorProperty?.GetValue(platform);
				if (inputInjector == null)
				{
					throw new MissingMemberException("IOSPlatform", "InputInjector");
				}
				foreach (MethodInfo method in inputInjector.GetType().GetMethods(AccessTools.all))
				{
					ParameterInfo[] parameters = method.GetParameters();
					if (parameters.Length != 2 || parameters[1].ParameterType != typeof(TimeSpan?))
					{
						continue;
					}
					if (method.Name == "KeyDown")
					{
						injectorKeyDown = method;
						physicalKeyType = parameters[0].ParameterType;
					}
					else if (method.Name == "KeyUp")
					{
						injectorKeyUp = method;
					}
				}
				injectorTypeText = AccessTools.Method(inputInjector.GetType(), "TypeText");
				if (injectorKeyDown == null || injectorKeyUp == null || injectorTypeText == null)
				{
					throw new MissingMemberException("IInputInjector", "KeyDown/KeyUp(PhysicalKey, TimeSpan?)/TypeText(string)");
				}
				BuildPhysicalKeys();
				Console.WriteLine("[mobile] ios key bridge ready (" + physicalKeys.Count + " keys, text " + injectorTypeText.Name + ")");
			}
			catch (Exception e)
			{
				keyInjectionFailed = true;
				Console.WriteLine("[mobile] ios key bridge not ready: " + (e.InnerException?.Message ?? e.Message));
			}
		}

		/// <summary>
		/// The engine's own view takes the launcher's hidden keyboard field (a real UITextField: marked text, so a Chinese
		/// keyboard's candidates work) and lends its safe area to the shim's cutout; from then on the keyboard is shown and
		/// hidden by the shim exactly as on Android.
		/// </summary>
		private static void AttachEngineView(object window)
		{
			try
			{
				object view = AccessTools.Field(window.GetType(), "_view")?.GetValue(window);
				PropertyInfo handle = view?.GetType().GetProperty("Handle");
				if (view == null || handle == null)
				{
					throw new MissingMemberException(window.GetType().Name, "_view.Handle");
				}
				glfwIosEngineView((IntPtr)handle.GetValue(view));
				Console.WriteLine("[mobile] ios keyboard: the engine's view handed to the shim (its log tells whether a field moved)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] ios keyboard: the engine's view not attached: " + (e.InnerException?.Message ?? e.Message));
			}
		}

		/// <summary>GLFW key codes to the engine's physical keys, by the engine's names (W3C codes: KeyA, Digit0, F1 …).</summary>
		private static void BuildPhysicalKeys()
		{
			var names = new List<(int glfw, string name)>();
			for (int i = 0; i < 26; i++)
			{
				names.Add((65 + i, "Key" + (char)('A' + i)));
			}
			for (int i = 0; i < 10; i++)
			{
				names.Add((48 + i, "Digit" + i));
			}
			for (int i = 0; i < 25; i++)
			{
				names.Add((290 + i, "F" + (i + 1)));
			}
			for (int i = 0; i < 10; i++)
			{
				names.Add((320 + i, "Numpad" + i));
			}
			names.AddRange(new[]
			{
				(32, "Space"), (39, "Quote"), (44, "Comma"), (45, "Minus"), (46, "Period"), (47, "Slash"), (59, "Semicolon"),
				(61, "Equal"), (91, "BracketLeft"), (92, "Backslash"), (93, "BracketRight"), (96, "Backquote"),
				(256, "Escape"), (257, "Enter"), (258, "Tab"), (259, "Backspace"), (260, "Insert"), (261, "Delete"),
				(262, "ArrowRight"), (263, "ArrowLeft"), (264, "ArrowDown"), (265, "ArrowUp"), (266, "PageUp"), (267, "PageDown"),
				(268, "Home"), (269, "End"), (280, "CapsLock"), (281, "ScrollLock"), (282, "NumLock"), (283, "PrintScreen"), (284, "Pause"),
				(330, "NumpadDecimal"), (331, "NumpadDivide"), (332, "NumpadMultiply"), (333, "NumpadSubtract"), (334, "NumpadAdd"),
				(335, "NumpadEnter"), (336, "NumpadEqual"),
				(340, "ShiftLeft"), (341, "ControlLeft"), (342, "AltLeft"), (343, "MetaLeft"), (344, "ShiftRight"),
				(345, "ControlRight"), (346, "AltRight"), (347, "MetaRight"),
			});
			foreach ((int glfw, string name) in names)
			{
				FieldInfo member = physicalKeyType.GetField(name, AccessTools.all);
				if (member == null)
				{
					continue;
				}
				physicalKeys[glfw] = member.GetValue(null);
				physicalKeyNames[glfw] = name;
			}
		}

		private static PropertyInfo FindProperty(Type type, string name)
		{
			for (Type t = type; t != null; t = t.BaseType)
			{
				PropertyInfo property = t.GetProperty(name, AccessTools.all);
				if (property != null)
				{
					return property;
				}
			}
			return null;
		}

		/// <summary>A key down or up for the game, by GLFW key code (what the key wheel and the latches carry).</summary>
		public static void Key(int glfwKey, bool down)
		{
			if (!EngineReady() || keyInjectionFailed || inputInjector == null)
			{
				return;
			}
			try
			{
				if (!physicalKeys.TryGetValue(glfwKey, out object key))
				{
					if (unknownKeys.Add(glfwKey))
					{
						Console.WriteLine("[mobile] ios key bridge: no engine key for glfw " + glfwKey);
					}
					return;
				}
				if (down)
				{
					injectorKeyDown.Invoke(inputInjector, new object[] { key, null });
				}
				else
				{
					injectorKeyUp.Invoke(inputInjector, new object[] { key, null });
				}
				keysInjected++;
				if (keysInjected <= 12 || keysInjected % 50 == 0)
				{
					Console.WriteLine("[mobile] ios key bridge: " + physicalKeyNames[glfwKey] + (down ? " down" : " up") + " (" + keysInjected + " so far)");
				}
			}
			catch (Exception e)
			{
				keyInjectionFailed = true;
				Console.WriteLine("[mobile] ios key bridge failed: " + (e.InnerException?.Message ?? e.Message));
			}
		}

		/// <summary>
		/// <summary>The soft keyboard's backspace (through the touch queue): a Backspace key for the game.</summary>
		public static void Backspace()
		{
			Key(GlfwBackspace, true);
			Key(GlfwBackspace, false);
		}

		/// <summary>A character the soft keyboard committed (a code point, through the touch queue), typed into the game.</summary>
		public static void Char(int codepoint)
		{
			if (!EngineReady() || keyInjectionFailed || inputInjector == null || codepoint < 0 || codepoint > 0x10FFFF)
			{
				return;
			}
			try
			{
				string text = char.ConvertFromUtf32(codepoint);
				injectorTypeText.Invoke(inputInjector, new object[] { text });
				charsInjected += text.Length;
				if (charsInjected <= 20 || charsInjected % 100 < text.Length)
				{
					Console.WriteLine("[mobile] ios text bridge: " + charsInjected + " chars so far (control " + GUIUtility.keyboardControl + ")");
				}
			}
			catch (Exception e)
			{
				keyInjectionFailed = true;
				Console.WriteLine("[mobile] ios text bridge failed: " + (e.InnerException?.Message ?? e.Message));
			}
		}

		private static double Number(object value, string name)
		{
			PropertyInfo property = value.GetType().GetProperty(name);
			if (property != null)
			{
				return Convert.ToDouble(property.GetValue(value));
			}
			return Convert.ToDouble(value.GetType().GetField(name).GetValue(value));
		}

		private static double ScaleOf(object window)
		{
			PropertyInfo property = window.GetType().GetProperty("ScaleFactor");
			if (property != null)
			{
				return Convert.ToDouble(property.GetValue(window));
			}
			return Convert.ToDouble(AccessTools.Field(window.GetType(), "_scaleFactor").GetValue(window));
		}

		private static void RefreshEngineWindow()
		{
			if (injectionFailed)
			{
				return;
			}
			try
			{
				Type platformType = AccessTools.TypeByName("Waylith.Windowing.Backends.IOS.IOSPlatform");
				PropertyInfo current = AccessTools.Property(platformType, "Current");
				object platform = current.GetValue(null);
				if (platform == null)
				{
					return;
				}
				object window = AccessTools.Field(platformType, "_window").GetValue(platform);
				if (window == null)
				{
					return;
				}

				Type windowBase = AccessTools.TypeByName("Waylith.Windowing.Core.WindowBase");
				pointerMove = AccessTools.Method(windowBase, "InjectPointerMove");
				pointerButton = AccessTools.Method(windowBase, "InjectButton");
				pointerScroll = AccessTools.Method(windowBase, "InjectScroll");
				if (pointerMove == null || pointerButton == null || pointerScroll == null)
				{
					throw new MissingMemberException("WindowBase", "InjectPointerMove/InjectButton/InjectScroll");
				}

				logicalType = pointerMove.GetParameters()[0].ParameterType;
				pointerKindType = pointerMove.GetParameters()[1].ParameterType;
				pointerIdType = pointerMove.GetParameters()[2].ParameterType;
				pointerButtonType = pointerButton.GetParameters()[0].ParameterType;
				mouseKind = Enum.ToObject(pointerKindType, MouseKind);
				touchKind = Enum.ToObject(pointerKindType, TouchKind);
				mouseId = AccessTools.Property(pointerIdType, "Mouse").GetValue(null);

				Type scrollDeltaType = pointerScroll.GetParameters()[0].ParameterType;
				scrollLines = AccessTools.Method(scrollDeltaType, "Lines");
				engineWindow = window;
				windowScale = ScaleOf(window);
				injectionReady = true;
				Console.WriteLine("[mobile] ios engine input bridge ready (window " + window.GetType().Name +
					", scale " + windowScale.ToString("0.0") + ")");
				ResolveKeys(platformType, platform);
				AttachEngineView(window);
			}
			catch (Exception e)
			{
				injectionFailed = true;
				Console.WriteLine("[mobile] ios engine input bridge failed: " + e.Message);
			}
		}

		private static bool EngineReady()
		{
			if (!injectionReady || engineWindow == null)
			{
				RefreshEngineWindow();
			}
			return injectionReady && engineWindow != null;
		}

		public static void Cursor(double x, double y)
		{
			if (!EngineReady())
			{
				return;
			}
			try
			{
				object position = Activator.CreateInstance(logicalType, x / windowScale, y / windowScale);
				Invoke(pointerMove, new[] { position, mouseKind, mouseId, null, null });
			}
			catch (Exception e)
			{
				InjectionFailed("pointer move", e);
			}
		}

		public static void Button(int button, bool down)
		{
			if (!EngineReady())
			{
				return;
			}
			try
			{
				object nativeButton = Enum.ToObject(pointerButtonType, button);
				Invoke(pointerButton, new[] { nativeButton, down, mouseKind, mouseId, null, null });
			}
			catch (Exception e)
			{
				InjectionFailed("button", e);
			}
		}

		public static void Scroll(double x, double y)
		{
			if (!EngineReady())
			{
				return;
			}
			try
			{
				object delta = scrollLines.Invoke(null, new object[] { x, y });
				Invoke(pointerScroll, new[] { delta, null });
			}
			catch (Exception e)
			{
				InjectionFailed("scroll", e);
			}
		}

		private static void Invoke(MethodInfo method, object[] arguments)
		{
			method.Invoke(engineWindow, arguments);
		}

		private static void InjectionFailed(string operation, Exception exception)
		{
			injectionFailed = true;
			Console.WriteLine("[mobile] ios engine input bridge: " + operation + " failed: " +
				(exception.InnerException?.Message ?? exception.Message));
		}
	}
}
