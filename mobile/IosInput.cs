using System;
using System.Reflection;
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

		private static object engineWindow;
		private static double windowScale = 1;
		private static MethodInfo pointerMove, pointerButton, pointerScroll, scrollLines;

		private static Type logicalType, pointerIdType, pointerKindType, pointerButtonType;
		private static object mouseId, mouseKind, touchKind;

		private static MethodInfo foundationCount, foundationObjectAt;

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
			return false;
		}

		/// <summary>Returns false for finger-only sets, whose engine handling is now Touch's injected mouse.</summary>
		private static bool BeforeHandleTouches(object __instance, object[] __args)
		{
			if (touchHookFailed || __args == null || __args.Length < 3)
			{
				return true;
			}

			bool hasMouse = false;
			try
			{
				if (foundationCount == null)
				{
					Type foundation = AccessTools.TypeByName("Waylith.Windowing.Shared.Apple.Interop.Foundation");
					foundationCount = AccessTools.Method(foundation, "Count");
					foundationObjectAt = AccessTools.Method(foundation, "ObjectAt");
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
					int touchType = (int)Convert.ToInt64(touch.GetType().GetProperty("Type").GetValue(touch));
					if (touchType == 3)
					{
						hasMouse = true;
						continue;
					}

					object point = touch.GetType().GetMethod("LocationInView").Invoke(touch, new[] { view });
					float x = (float)Number(point, "X");
					float y = (float)Number(point, "Y");
					Vector2 pixels = new Vector2((float)(x * scale), (float)(y * scale));
					int id = (int)(handle.ToInt64() & 0x7fffffff);
					if (Interlocked.Exchange(ref firstRawTouchLogged, 1) == 0)
					{
						Console.WriteLine("[mobile] ios touch bridge: first touch at " + pixels + " (scale " +
							scale.ToString("0.0") + ")");
					}
					Touch.Apply(kind, id, pixels);
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
			object position = Activator.CreateInstance(logicalType, x / windowScale, y / windowScale);
			pointerMove.Invoke(engineWindow, new[] { position, mouseKind, mouseId, null, null });
		}

		public static void Button(int button, bool down)
		{
			if (!EngineReady())
			{
				return;
			}
			object nativeButton = Enum.ToObject(pointerButtonType, button);
			pointerButton.Invoke(engineWindow, new[] { nativeButton, down, mouseKind, mouseId, null, null });
		}

		public static void Scroll(double x, double y)
		{
			if (!EngineReady())
			{
				return;
			}
			object delta = scrollLines.Invoke(null, new object[] { x, y });
			pointerScroll.Invoke(engineWindow, new[] { delta, null });
		}
	}
}
