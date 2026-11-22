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
				ApplySaved();
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] resolution: the game's list is left: " + e.Message);
			}
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
