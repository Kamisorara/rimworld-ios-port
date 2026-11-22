using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>Tracing of what the game sees, on while touch-verbose.txt exists next to the game (Touch.Verbose).</summary>
	internal static class Diag
	{
		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(RimWorld.Selector), "HandleMapClicks"), prefix: new HarmonyMethod(typeof(Diag), nameof(BeforeMapClicks)));
			harmony.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), postfix: new HarmonyMethod(typeof(Diag), nameof(AfterWindowAdd)));
			harmony.Patch(AccessTools.Method(typeof(RimWorld.Selector), "SelectAllMatchingObjectUnderMouseOnScreen"), prefix: new HarmonyMethod(typeof(Diag), nameof(BeforeSelectAll)), postfix: new HarmonyMethod(typeof(Diag), nameof(AfterSelectAll)));
			harmony.Patch(AccessTools.Method(typeof(RimWorld.Selector), nameof(RimWorld.Selector.ClearSelection)), prefix: new HarmonyMethod(typeof(Diag), nameof(BeforeClearSelection)));
			harmony.Patch(AccessTools.Method(typeof(RimWorld.Selector), nameof(RimWorld.Selector.Deselect)), prefix: new HarmonyMethod(typeof(Diag), nameof(BeforeDeselect)));
		}

		public static void BeforeDeselect(object obj)
		{
			if (Touch.Verbose)
			{
				Console.WriteLine("[mobile] deselect " + obj + " by " + new System.Diagnostics.StackTrace(1, false).ToString().Replace("\n", " | "));
			}
		}

		public static void BeforeSelectAll()
		{
			if (Touch.Verbose)
			{
				Console.WriteLine("[mobile] select all of the kind: " + Find.Selector.NumSelected + " selected before, mouse cell " + UI.MouseCell());
			}
		}

		public static void AfterSelectAll()
		{
			if (Touch.Verbose)
			{
				Console.WriteLine("[mobile] select all of the kind: " + Find.Selector.NumSelected + " selected after");
			}
		}

		public static void BeforeClearSelection()
		{
			if (Touch.Verbose && Find.Selector.NumSelected > 1)
			{
				Console.WriteLine("[mobile] selection of " + Find.Selector.NumSelected + " cleared by " + new System.Diagnostics.StackTrace(1, false).ToString().Replace("\n", " | "));
			}
		}

		public static void BeforeMapClicks()
		{
			Event current = Event.current;
			if (Touch.Verbose && current != null && (current.type == EventType.MouseDown || current.rawType == EventType.MouseUp))
			{
				Console.WriteLine("[mobile] map clicks see " + current.type + " (raw " + current.rawType + ") button " + current.button + " clicks " + current.clickCount + " at " + current.mousePosition + ", input mouse " + (Vector2)Input.mousePosition + ", buttons " + Input.GetMouseButton(0) + "/" + Input.GetMouseButton(1) + "/" + Input.GetMouseButton(2) + ", selected " + Find.Selector.NumSelected + " (pawns " + Find.Selector.SelectedPawns.Count + "), goto " + ((object)AccessTools.Field(typeof(RimWorld.Selector), "gotoController").GetValue(Find.Selector) != null));
			}
		}

		public static void AfterWindowAdd(Window window)
		{
			if (Touch.Verbose)
			{
				Console.WriteLine("[mobile] window added: " + window.GetType().Name + " at " + window.windowRect);
			}
		}
	}
}

namespace PortMobile
{
	internal static class DiagFloatMenu
	{
		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(RimWorld.FloatMenuMakerMap), "GetOptions"), prefix: new HarmonyMethod(typeof(DiagFloatMenu), nameof(BeforeGetOptions)), postfix: new HarmonyMethod(typeof(DiagFloatMenu), nameof(AfterGetOptions)));
			harmony.Patch(AccessTools.Method(typeof(RimWorld.Selector), "HandleMapClicks"), postfix: new HarmonyMethod(typeof(DiagFloatMenu), nameof(AfterMapClicks)));
		}

		public static void BeforeGetOptions(Vector3 clickPos)
		{
			if (Touch.Verbose) Console.WriteLine("[mobile] float menu options asked at " + clickPos);
		}

		public static void AfterMapClicks()
		{
			Event current = Event.current;
			if (Touch.Verbose && current != null && current.button == 1 && current.rawType == EventType.MouseDown) Console.WriteLine("[mobile] map clicks done with the right press: type now " + current.type);
		}

		public static void AfterGetOptions(System.Collections.Generic.List<Verse.FloatMenuOption> __result, RimWorld.FloatMenuContext context)
		{
			if (!Touch.Verbose)
			{
				return;
			}
			var providers = (System.Collections.IList)AccessTools.Field(typeof(RimWorld.FloatMenuMakerMap), "providers").GetValue(null);
			string things = context != null ? string.Join(",", System.Linq.Enumerable.Select(context.ClickedThings, t => t.LabelShort)) : "no context";
			Console.WriteLine("[mobile] float menu options: " + (__result != null ? __result.Count : -1) + ", providers " + (providers != null ? providers.Count : -1) + ", clicked things " + things + (context != null ? ", cell " + context.ClickedCell + ", pawn " + context.FirstSelectedPawn + " accepted " + ((Verse.AcceptanceReport)AccessTools.Method(typeof(RimWorld.FloatMenuMakerMap), "ShouldGenerateFloatMenuForPawn").Invoke(null, new object[] { context.FirstSelectedPawn })).Accepted : ""));
		}
	}
}
