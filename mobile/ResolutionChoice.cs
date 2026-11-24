using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The options' Resolution (视频 → 分辨率) on a phone: the screen's own, 1080p and 720p (the user, 2026-10-25). The game
	/// lists Screen.resolutions, which on the phone is the screen alone, and drops anything under 1024 x 768. Here the list
	/// is the screen and those two heights at the screen's shape, all kept; a choice goes the game's way (SafeSetResolution,
	/// the confirmation with its countdown, Prefs.ScreenWidth / ScreenHeight). The GLFW shim makes the game's surface that
	/// size for the engine and scales the fingers to it, the Vulkan interposer makes it the surface's current extent and
	/// Android scales the smaller swapchain to the screen; at the next start ApplySaved sets it again from the prefs. The interface scale is the player's: the menu's
	/// labels and a message say which scale keeps the interface as large as it was.
	/// </summary>
	internal static class ResolutionChoice
	{
		[System.Runtime.InteropServices.DllImport("glfw")] private static extern void glfwAndroidViewSize(out int width, out int height);

		private static readonly int[] Heights = { 1080, 720 };
		private static bool available;
		private static bool sizeApplied;

		// The engine's own layer (IOSWindow), not the host launcher's: UIApplicationMain makes a new view and
		// CAMetalLayer when the engine takes the scene, so the GLFW shim's g_layer is not the one on the screen.
		private static Type iosWindowType;
		private static FieldInfo iosViewField, iosLayerField;
		private static MethodInfo iosBoundsGetter, iosScaleGetter, iosSetScale, iosSetContentsScale, iosHandleLayout;
		private static MethodInfo iosRectSizeGetter, iosSizeHeightGetter;

		/// <summary>The screen's pixels for one of the game's (the agent's "phone px"): 1 at the screen's own resolution.</summary>
		public static float ToScreen
		{
			get
			{
				if (!available || Screen.width <= 0)
				{
					return 1f;
				}
				glfwAndroidViewSize(out int width, out _);
				return width > 0 ? (float)width / Screen.width : 1f;
			}
		}

		public static void Start(Harmony harmony)
		{
			try
			{
				glfwAndroidViewSize(out int w, out int h);
				available = w > 0 && h > 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] resolution: an older APK without the view size (" + e.GetType().Name + "); the game's list is left");
				return;
			}
			try
			{
				harmony.Patch(AccessTools.Method(typeof(Dialog_Options), "DoVideoOptions"), transpiler: new HarmonyMethod(typeof(ResolutionChoice), nameof(ListTranspiler)));
				int filters = 0;
				foreach (Type nested in typeof(Dialog_Options).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
				{
					foreach (MethodInfo method in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
					{
						ParameterInfo[] parameters = method.GetParameters();
						if (method.ReturnType == typeof(bool) && parameters.Length == 1 && parameters[0].ParameterType == typeof(Resolution))
						{
							harmony.Patch(method, prefix: new HarmonyMethod(typeof(ResolutionChoice), nameof(KeepEvery)));
							filters++;
						}
					}
				}
				harmony.Patch(AccessTools.Method(typeof(Dialog_Options), "ResToString", new[] { typeof(int), typeof(int) }), postfix: new HarmonyMethod(typeof(ResolutionChoice), nameof(AfterResToString)));
				harmony.Patch(AccessTools.Method(typeof(ResolutionUtility), nameof(ResolutionUtility.SafeSetResolution)), prefix: new HarmonyMethod(typeof(ResolutionChoice), nameof(BeforeSet)), postfix: new HarmonyMethod(typeof(ResolutionChoice), nameof(AfterSet)));
				Console.WriteLine("[mobile] resolution: the screen's own, 1080p and 720p offered (" + filters + " filter)");
				PatchEngineLayout(harmony);
				ApplySaved();
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] resolution: the game's list is left: " + e.Message);
			}
			ApplyMobileRenderSize();
		}

		/// <summary>
		/// The window's size is not known yet when Start runs (the GLFW shim is attached later), so the size asked for
		/// above is kept until the first frame that has it. Until then ApplyMobileRenderSize would return silently and
		/// leave the panel at its full size (0.4.21 on the iPad).
		/// </summary>
		public static void Tick()
		{
			if (sizeApplied)
			{
				return;
			}
			glfwAndroidViewSize(out int width, out int height);
			if (width <= 0 || height <= 0)
			{
				return;
			}
			if (Waylith.EngineApplication.Instance?.Window == null)
			{
				return;
			}
			sizeApplied = true;
			available = true;
			Console.WriteLine("[mobile] resolution: the window is " + width + "x" + height + " px");
			ApplySaved();
			ApplyMobileRenderSize();
		}

		/// <summary>
		/// iOS's window cannot be resized, so the Waylith IOS backend drops Screen.SetResolution. The GLFW shim can
		/// not reach the engine's layer (UIApplicationMain makes its own), so the engine's view is given a smaller
		/// content scale factor instead: IOSWindow.HandleLayout then makes the Metal drawable smaller and reports the
		/// smaller surface, while UIKit scales it to the same view. mobile.txt: renderHeight=1080 or 720; 0 leaves
		/// the screen's own resolution.
		/// </summary>
		private static void ApplyMobileRenderSize()
		{
			glfwAndroidViewSize(out int screenWidth, out int screenHeight);
			// A missing mobile.txt should not leave a high-resolution iPad at its full panel size; an explicit
			// renderHeight=0 still asks for the screen's own resolution.
			int fallback = OperatingSystem.IsIOS() && screenHeight > 1200 ? 1080 : 0;
			int height = Settings.Get("renderHeight", fallback);
			if (screenWidth <= 0 || screenHeight <= 0 || height <= 0 || height >= screenHeight)
			{
				return;
			}
			int width = Mathf.RoundToInt(screenWidth * (float)height / screenHeight / 2f) * 2;
			ApplyRenderScale(height);
			Prefs.ScreenWidth = width;
			Prefs.ScreenHeight = height;
			Console.WriteLine("[mobile] resolution: rendering at " + width + "x" + height + " (mobile.txt renderHeight=" + height + ", screen " + screenWidth + "x" + screenHeight + ")");
		}

		/// <summary>The engine's layer, through IOSWindow.HandleLayout: the view keeps its points, the drawable shrinks.</summary>
		private static void PatchEngineLayout(Harmony harmony)
		{
			try
			{
				iosWindowType = AccessTools.TypeByName("Waylith.Windowing.Backends.IOS.IOSWindow");
				if (iosWindowType != null)
				{
					Console.WriteLine("[mobile] resolution: IOSWindow type found via TypeByName");
				}
				else
				{
					foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
					{
						iosWindowType = assembly.GetType("Waylith.Windowing.Backends.IOS.IOSWindow");
						if (iosWindowType != null)
						{
							Console.WriteLine("[mobile] resolution: IOSWindow type found via assembly scan");
							break;
						}
					}
				}
				if (iosWindowType == null)
				{
					Console.WriteLine("[mobile] resolution: Waylith.Windowing.Backends.IOS.IOSWindow not found; the drawable is left at the screen's size");
					return;
				}
				iosViewField = AccessTools.Field(iosWindowType, "_view");
				iosLayerField = AccessTools.Field(iosWindowType, "_layer");
				iosHandleLayout = AccessTools.Method(iosWindowType, "HandleLayout");
				if (iosViewField == null || iosLayerField == null || iosHandleLayout == null)
				{
					Console.WriteLine("[mobile] resolution: IOSWindow's view or layout not found; the drawable is left at the screen's size");
					return;
				}
				Console.WriteLine("[mobile] resolution: IOSWindow's fields and layout found");
				Type viewType = iosViewField.FieldType, layerType = iosLayerField.FieldType;
				Console.WriteLine("[mobile] resolution: view type " + viewType.FullName + ", layer type " + layerType.FullName);
				iosBoundsGetter = AccessTools.PropertyGetter(viewType, "Bounds");
				if (iosBoundsGetter == null)
				{
					Console.WriteLine("[mobile] resolution: view Bounds property not found; the drawable is left at the screen's size");
					return;
				}
				iosScaleGetter = AccessTools.PropertyGetter(viewType, "ContentScaleFactor");
				if (iosScaleGetter == null)
				{
					Console.WriteLine("[mobile] resolution: view ContentScaleFactor property not found; the drawable is left at the screen's size");
					return;
				}
				iosSetScale = AccessTools.Method(viewType, "SetContentScaleFactor");
				if (iosSetScale == null)
				{
					Console.WriteLine("[mobile] resolution: view SetContentScaleFactor method not found; the drawable is left at the screen's size");
					return;
				}
				iosSetContentsScale = AccessTools.Method(layerType, "SetContentsScale");
				if (iosSetContentsScale == null)
				{
					Console.WriteLine("[mobile] resolution: layer SetContentsScale method not found; the drawable is left at the screen's size");
					return;
				}
				// NSRect/NSSize are record structs: Size and Height are properties, not fields.
				iosRectSizeGetter = AccessTools.PropertyGetter(iosBoundsGetter.ReturnType, "Size");
				if (iosRectSizeGetter == null)
				{
					Console.WriteLine("[mobile] resolution: bounds Size field not found; the drawable is left at the screen's size");
					return;
				}
				iosSizeHeightGetter = AccessTools.PropertyGetter(iosRectSizeGetter.ReturnType, "Height");
				if (iosSizeHeightGetter == null)
				{
					Console.WriteLine("[mobile] resolution: size Height field not found; the drawable is left at the screen's size");
					return;
				}
				harmony.Patch(iosHandleLayout, prefix: new HarmonyMethod(typeof(ResolutionChoice), nameof(BeforeHandleLayout)));
				Console.WriteLine("[mobile] resolution: IOSWindow.HandleLayout patched");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] resolution: the engine's layer not patched: " + e.Message);
			}
		}

		/// <summary>Prefix of IOSWindow.HandleLayout: the view's scale is the render height over its points.</summary>
		public static void BeforeHandleLayout(object __instance)
		{
			if (__instance == null)
			{
				return;
			}
			int height = Settings.Get("renderHeight", OperatingSystem.IsIOS() ? 1080 : 0);
			if (height > 0)
			{
				SetRenderScale(__instance, height);
			}
		}

		private static void SetRenderScale(object window, int height)
		{
			if (iosViewField == null || iosLayerField == null || iosRectSizeGetter == null || iosSizeHeightGetter == null)
			{
				return;
			}
			object view = iosViewField.GetValue(window);
			object layer = iosLayerField.GetValue(window);
			if (view == null || layer == null)
			{
				return;
			}
			object bounds = iosBoundsGetter.Invoke(view, null);
			object size = iosRectSizeGetter.Invoke(bounds, null);
			double points = (double)iosSizeHeightGetter.Invoke(size, null);
			if (points <= 0)
			{
				return;
			}
			double current = (double)iosScaleGetter.Invoke(view, null);
			double wanted = height / points;
			if (wanted <= 0 || wanted >= current - 0.001)
			{
				return;
			}
			iosSetScale.Invoke(view, new object[] { wanted });
			iosSetContentsScale.Invoke(layer, new object[] { wanted });
			Console.WriteLine("[mobile] resolution: the engine's layer scaled " + current.ToString("0.###") + " -> " + wanted.ToString("0.###") + " (" + height + "p)");
		}

		/// <summary>After the window exists: scale its view and let HandleLayout report the smaller surface.</summary>
		private static void ApplyRenderScale(int height)
		{
			object window = Waylith.EngineApplication.Instance?.Window;
			if (window == null || iosWindowType == null || !iosWindowType.IsInstanceOfType(window) || iosHandleLayout == null)
			{
				Console.WriteLine("[mobile] resolution: the engine's window is not an IOSWindow yet; the drawable is left at the screen's size");
				return;
			}
			SetRenderScale(window, height);
			iosHandleLayout.Invoke(window, null);
		}

		/// <summary>
		/// At the start: the resolution kept in the prefs (the game makes its window at the screen's size on the phone), when it
		/// is a smaller one of the screen's shape.
		/// </summary>
		private static void ApplySaved()
		{
			glfwAndroidViewSize(out int screenWidth, out int screenHeight);
			int width = Prefs.ScreenWidth, height = Prefs.ScreenHeight;
			if (screenWidth <= 0 || screenHeight <= 0 || width <= 0 || height <= 0 || width >= screenWidth || height >= screenHeight)
			{
				return;
			}
			float shape = (float)width / height, screen = (float)screenWidth / screenHeight;
			if (shape < screen * 0.98f || shape > screen * 1.02f)
			{
				return;
			}
			ResolutionUtility.SetResolutionRaw(width, height, Screen.fullScreen);
			Console.WriteLine("[mobile] resolution: rendering at " + width + "x" + height + " as the prefs say (the screen is " + screenWidth + "x" + screenHeight + ")");
		}

		/// <summary>The screen's size, and the smaller heights at its shape (widths even).</summary>
		public static List<Resolution> Choices()
		{
			var list = new List<Resolution>();
			glfwAndroidViewSize(out int width, out int height);
			if (width <= 0 || height <= 0)
			{
				return UnityGUIBugsFixer.ScreenResolutionsWithoutDuplicates;
			}
			list.Add(new Resolution { width = width, height = height });
			foreach (int h in Heights)
			{
				if (h < height)
				{
					int w = Mathf.RoundToInt(width * (float)h / height / 2f) * 2;
					list.Add(new Resolution { width = w, height = h });
				}
			}
			return list;
		}

		/// <summary>DoVideoOptions takes its list from Choices instead of UnityGUIBugsFixer.ScreenResolutionsWithoutDuplicates.</summary>
		public static IEnumerable<CodeInstruction> ListTranspiler(IEnumerable<CodeInstruction> instructions)
		{
			MethodInfo screen = AccessTools.PropertyGetter(typeof(UnityGUIBugsFixer), nameof(UnityGUIBugsFixer.ScreenResolutionsWithoutDuplicates));
			MethodInfo ours = AccessTools.Method(typeof(ResolutionChoice), nameof(Choices));
			int replaced = 0;
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.Calls(screen))
				{
					instruction.operand = ours;
					replaced++;
				}
				yield return instruction;
			}
			if (replaced == 0)
			{
				Console.WriteLine("[mobile] resolution: the options' list was not found");
			}
		}

		/// <summary>Prefix of the options' filter (at least 1024 x 768): every choice here is kept, 720p as well.</summary>
		public static bool KeepEvery(ref bool __result)
		{
			if (!available)
			{
				return true;
			}
			__result = true;
			return false;
		}

		/// <summary>Postfix of Dialog_Options.ResToString: which choice it is, and the interface scale that keeps the interface's size.</summary>
		public static void AfterResToString(int width, int height, ref string __result)
		{
			if (!available)
			{
				return;
			}
			glfwAndroidViewSize(out int screenWidth, out int screenHeight);
			if (screenHeight <= 0)
			{
				return;
			}
			string which = width == screenWidth && height == screenHeight ? (Texts.Chinese ? "原生" : "native") : Array.IndexOf(Heights, height) >= 0 ? height + "p" : null;
			if (which == null)
			{
				return;
			}
			__result += " (" + which + ")";
		}

		private static int heightBefore;

		public static void BeforeSet()
		{
			heightBefore = Screen.height;
		}

		/// <summary>Postfix of ResolutionUtility.SafeSetResolution: the scale that keeps the interface as large as it was.</summary>
		public static void AfterSet(Resolution res)
		{
			if (!available || heightBefore <= 0 || res.height == heightBefore)
			{
				return;
			}
			float keeps = Mathf.Round(Prefs.UIScale * res.height / heightBefore * 10f) / 10f;
			if (Mathf.Abs(keeps - Prefs.UIScale) < 0.05f)
			{
				return;
			}
			Messages.Message(Texts.Chinese
				? "界面缩放还是 " + Prefs.UIScale.ToString("0.##") + "，界面会" + (keeps < Prefs.UIScale ? "变大" : "变小") + "。想和原来一样大，把界面缩放改成 " + keeps.ToString("0.#") + "。"
				: "The interface scale is still " + Prefs.UIScale.ToString("0.##") + ": the interface gets " + (keeps < Prefs.UIScale ? "larger" : "smaller") + ". For the size it had, set the interface scale to " + keeps.ToString("0.#") + ".",
				MessageTypeDefOf.SilentInput, false);
		}
	}
}
