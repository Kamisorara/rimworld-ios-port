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
	/// The research tree scrolls down as well as across.
	///
	/// The game lays the tree out at a fixed 100 units a row and scrolls it only across: the area it scrolls in is as
	/// high as the window (MainTabWindow_Research.DrawRightRect: rightOutRect.height - 16), and the window is made high
	/// enough for the highest tab's tree, but no higher than the screen. On a PC at 1080 lines that is about ten rows. The
	/// phone is 687 units high at its interface scale of 1.85, and with mods' tabs taking two rows at the top, five rows
	/// show: the projects below them were cut off and could not be reached.
	///
	/// Here the area is made as high as the tab's tree (the game's own ViewSize, the height the window would have taken),
	/// whenever that is more. Nothing else changes where the tree fits. A tree that does not fit is then a list that
	/// scrolls both ways, which a finger drags in any direction (Interface.FingerScroll); the game already draws only the
	/// projects inside the scrolled view. When the window opens, the view goes to the selected project down as well as
	/// across (the game armed its ScrollPositioner for that, but asked it for the horizontal part only).
	/// </summary>
	internal static class ResearchTree
	{
		private static MethodInfo viewSize;
		private static ResearchTabDef measuredTab;
		private static float measuredHeight;
		private static bool saidTaller;

		public static void Start(Harmony harmony)
		{
			try
			{
				viewSize = AccessTools.Method(typeof(MainTabWindow_Research), "ViewSize");
				MethodInfo drawRightRect = AccessTools.Method(typeof(MainTabWindow_Research), "DrawRightRect");
				if (viewSize == null || drawRightRect == null)
				{
					Console.WriteLine("[mobile] research tree: the game's methods are not there, left alone");
					return;
				}
				harmony.Patch(drawRightRect, transpiler: new HarmonyMethod(typeof(ResearchTree), nameof(ScrollDownToo)));
				harmony.Patch(AccessTools.Method(typeof(MainTabWindow_Research), nameof(MainTabWindow_Research.PreOpen)), postfix: new HarmonyMethod(typeof(ResearchTree), nameof(Forget)));
				Console.WriteLine("[mobile] research tree: scrolls down where it does not fit");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] research tree: not patched: " + e);
			}
		}

		/// <summary>
		/// Transpiler of DrawRightRect. The view's height (rightOutRect.height - 16, the last argument of the first Rect
		/// made after that subtraction) goes through ViewHeight; the window's call to ScrollPositioner.ScrollHorizontally
		/// becomes ScrollToSelected.
		/// </summary>
		public static IEnumerable<CodeInstruction> ScrollDownToo(IEnumerable<CodeInstruction> instructions)
		{
			var codes = new List<CodeInstruction>(instructions);
			ConstructorInfo rectCtor = AccessTools.Constructor(typeof(Rect), new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
			MethodInfo scrollHorizontally = AccessTools.Method(typeof(ScrollPositioner), nameof(ScrollPositioner.ScrollHorizontally));
			bool heightDone = false, scrollDone = false;
			for (int i = 0; i < codes.Count; i++)
			{
				if (!heightDone && i + 2 < codes.Count && codes[i].opcode == OpCodes.Ldc_R4 && codes[i].operand is float f && f == 16f && codes[i + 1].opcode == OpCodes.Sub && codes[i + 2].operand is ConstructorInfo c && c == rectCtor)
				{
					codes.InsertRange(i + 2, new[]
					{
						new CodeInstruction(OpCodes.Ldarg_0),
						new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ResearchTree), nameof(ViewHeight))),
					});
					heightDone = true;
					i += 3;
					continue;
				}
				if (!scrollDone && codes[i].operand is MethodInfo m && m == scrollHorizontally)
				{
					codes[i].opcode = OpCodes.Call;
					codes[i].operand = AccessTools.Method(typeof(ResearchTree), nameof(ScrollToSelected));
					scrollDone = true;
				}
			}
			if (!heightDone || !scrollDone)
			{
				Console.WriteLine("[mobile] research tree: DrawRightRect is not as expected (height " + heightDone + ", scroll " + scrollDone + ")");
			}
			return codes;
		}

		/// <summary>The height of the area the tree scrolls in: the game's, or the tree's when that is more.</summary>
		public static float ViewHeight(float height, MainTabWindow_Research window)
		{
			ResearchTabDef tab = window.CurTab;
			if (tab != measuredTab)
			{
				measuredTab = tab;
				measuredHeight = 0f;
				// ViewSize sets the font for its measuring: whatever is being drawn keeps its own.
				GameFont font = Text.Font;
				try
				{
					// The height the game would have made the window for this tab: its rows and the margins around them.
					measuredHeight = ((Vector2)viewSize.Invoke(window, new object[] { tab })).y + 24f;
				}
				catch (Exception e)
				{
					Console.WriteLine("[mobile] research tree: no size for " + tab?.defName + ": " + e.GetType().Name);
				}
				finally
				{
					Text.Font = font;
				}
				if (measuredHeight > height && !saidTaller)
				{
					saidTaller = true;
					Console.WriteLine("[mobile] research tree: " + tab?.defName + " is " + measuredHeight.ToString("F0") + " high, the window has room for " + height.ToString("F0") + ": it scrolls down");
				}
			}
			return Mathf.Max(height, measuredHeight);
		}

		/// <summary>Stands in for the window's ScrollPositioner.ScrollHorizontally: the selected project is brought into view both ways.</summary>
		public static void ScrollToSelected(ScrollPositioner positioner, ref Vector2 scrollPosition, Vector2 outRectSize)
		{
			positioner.Scroll(ref scrollPosition, outRectSize, scrollHorizontally: true, scrollVertically: true);
		}

		/// <summary>Postfix of PreOpen: which projects show may have changed (difficulty, mods' conditions), so the trees are measured again.</summary>
		public static void Forget()
		{
			measuredTab = null;
		}
	}
}
