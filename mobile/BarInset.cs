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
	/// The bar of main buttons along the bottom edge reaches from one side of the screen to the other, and a phone's
	/// screen has round corners: the first and the last button lie under them. Here the bar begins and ends further in,
	/// by as much as the corner covers a quarter of the bar's height above the bottom edge (so the faces of the outer
	/// buttons are whole from there up; with this phone's corners of 169 pixels that is 97 pixels, 52 units, a side).
	/// The buttons share the remaining width as the game shares the whole one; nothing else moves. The two ends of
	/// the bar are filled with the face of the buttons, so that the bar still reaches from side to side.
	///
	/// Done inside MainButtonsRoot.DoButtons: the two places where it reads the screen's width get the shorter width,
	/// and the buttons are drawn in a group that begins at the inset.
	/// mobile.txt: barInset=N sets the inset in interface units (0 leaves the bar as it is); cornerRadius=N the corner's
	/// radius in pixels, from which the inset is worked out (default: 13.3 % of the screen's short side, which is what
	/// this phone reports; the system knows the true figure, but only Java can ask for it).
	/// </summary>
	internal static class BarInset
	{
		private const float BarHeight = 35f;
		private static float insetUnits = -1f, cornerPixels = -1f;

		public static void Start(Harmony harmony)
		{
			insetUnits = Number("barInset", -1f);
			cornerPixels = Number("cornerRadius", -1f);
			if (insetUnits == 0f)
			{
				return;
			}
			try
			{
				MethodInfo method = AccessTools.Method(typeof(MainButtonsRoot), "DoButtons");
				harmony.Patch(method, prefix: new HarmonyMethod(typeof(BarInset), nameof(Begin)), finalizer: new HarmonyMethod(typeof(BarInset), nameof(End)), transpiler: new HarmonyMethod(typeof(BarInset), nameof(Widths)));
				Console.WriteLine("[mobile] bar inset: the main buttons keep " + Inset() + " units clear of each side");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] bar inset: the main buttons are left as they are: " + e.Message);
			}
		}

		private static float Number(string key, float fallback)
		{
			string text = Settings.Get(key, null);
			return text != null && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) && value >= 0f ? value : fallback;
		}

		/// <summary>How far in the bar begins, in interface units.</summary>
		private static int Inset()
		{
			if (insetUnits >= 0f)
			{
				return Mathf.RoundToInt(insetUnits);
			}
			float scale = Prefs.UIScale > 0f ? Prefs.UIScale : 1f;
			float radius = cornerPixels >= 0f ? cornerPixels : 0.133f * Mathf.Min(Screen.width, Screen.height);
			// A quarter of the bar's height above the bottom edge, the corner's circle is this far in from the side.
			float above = Mathf.Min(radius, 0.25f * BarHeight * scale);
			float pixels = radius - Mathf.Sqrt(radius * radius - (radius - above) * (radius - above));
			return Mathf.Max(0, Mathf.RoundToInt(pixels / scale));
		}

		/// <summary>The width the buttons share: the screen's, less the inset on both sides.</summary>
		public static int Width()
		{
			return UI.screenWidth - 2 * Inset();
		}

		public static void Begin(out bool __state)
		{
			int inset = Inset();
			__state = inset > 0;
			if (__state)
			{
				if (Event.current.type == EventType.Repaint)
				{
					// The two ends get the face of the buttons, reaching a little under the outer buttons so that no edge shows between them.
					Color colour = GUI.color;
					GUI.color = Color.white;
					float top = UI.screenHeight - BarHeight;
					Widgets.DrawAtlas(new Rect(0f, top, inset + 6f, BarHeight + 1f), Widgets.ButtonSubtleAtlas);
					Widgets.DrawAtlas(new Rect(UI.screenWidth - inset - 6f, top, inset + 6f, BarHeight + 1f), Widgets.ButtonSubtleAtlas);
					GUI.color = colour;
				}
				GUI.BeginGroup(new Rect(inset, 0f, Width(), UI.screenHeight));
			}
		}

		public static void End(bool __state)
		{
			if (__state)
			{
				GUI.EndGroup();
			}
		}

		public static IEnumerable<CodeInstruction> Widths(IEnumerable<CodeInstruction> instructions)
		{
			FieldInfo screenWidth = AccessTools.Field(typeof(UI), nameof(UI.screenWidth));
			MethodInfo width = AccessTools.Method(typeof(BarInset), nameof(Width));
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, screenWidth))
				{
					instruction.opcode = OpCodes.Call;
					instruction.operand = width;
				}
				yield return instruction;
			}
		}
	}
}
