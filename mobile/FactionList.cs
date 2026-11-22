using System;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The faction list of "创建世界" (WorldFactionsUIUtility): its scroll view's content is as wide as the view, so
	/// once the list scrolls, the vertical scrollbar lies over the rows' right end, where the delete buttons are (half
	/// covered, and a tap there takes the scrollbar). On a PC the list rarely scrolls; on the phone's short screen it
	/// does with a dozen factions (a tester's screenshot, 17 of them, 2026-10-21). While the list scrolls, each row is
	/// drawn narrower by the scrollbar's width, so that its delete button sits beside the scrollbar.
	/// </summary>
	internal static class FactionList
	{
		private const float ScrollbarWidth = 16f;

		private static FieldInfo listingHeight, warningHeight;
		private static bool scrolling;

		public static void Start(Harmony harmony)
		{
			try
			{
				listingHeight = AccessTools.Field(typeof(WorldFactionsUIUtility), "listingHeight");
				warningHeight = AccessTools.Field(typeof(WorldFactionsUIUtility), "warningHeight");
				MethodInfo contents = AccessTools.Method(typeof(WorldFactionsUIUtility), nameof(WorldFactionsUIUtility.DoWindowContents));
				MethodInfo row = AccessTools.Method(typeof(WorldFactionsUIUtility), nameof(WorldFactionsUIUtility.DoRow));
				if (listingHeight == null || warningHeight == null || contents == null || row == null)
				{
					Console.WriteLine("[mobile] faction list: not as expected; left as it is");
					return;
				}
				harmony.Patch(contents, prefix: new HarmonyMethod(typeof(FactionList), nameof(BeforeContents)), postfix: new HarmonyMethod(typeof(FactionList), nameof(AfterContents)));
				harmony.Patch(row, prefix: new HarmonyMethod(typeof(FactionList), nameof(BeforeRow)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] faction list not installed: " + e.Message);
			}
		}

		/// <summary>Whether the list scrolls this time: the rows' height (from the last drawing, as the game itself takes it) against the view's.</summary>
		public static void BeforeContents(Rect rect)
		{
			// The view as DoWindowContents makes it: below the title line, above the add button and the warning, 4 in from each side.
			float viewHeight = rect.height - (Text.LineHeight + 4f) - Text.LineHeight - 28f - (float)warningHeight.GetValue(null) - 8f;
			scrolling = (float)listingHeight.GetValue(null) > viewHeight;
		}

		public static void AfterContents()
		{
			scrolling = false;
		}

		public static void BeforeRow(ref Rect rect)
		{
			if (scrolling)
			{
				rect.width -= ScrollbarWidth;
			}
		}
	}
}
