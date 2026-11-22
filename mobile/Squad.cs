using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Orders by tapping while a drafted squad is selected (the user's choice, 2026-10-16): everything selected is a pawn
	/// of the colony, and at least one of them is drafted. On the map then (Touch decides what a touch is):
	///   tap            a right click where the finger was: move there, attack what is there, or the menu of orders
	///                  (what a hold does otherwise)
	///   tap on one of the colony's own (a colonist or a mech under the player's control, standing): it joins the
	///                  selection, or leaves it if it was in it
	///   hold           the game's own left button: lifting selects what is there (nothing there: the selection is
	///                  cleared), dragging from the hold draws a selection box
	///   drag           moves the camera, as always
	/// The selection is also cleared with the back key or changed by tapping a portrait in the colonist bar.
	///
	/// Two orders tapped quickly one after the other would make the game see a double click on the second press, and a
	/// double click selects every object like the one under the mouse on the screen (the squad would be gone): while a
	/// squad is selected, a press on the map counts as a single click.
	///
	/// Off since 2026-10-20 (testers: a tap on the ground to clear the selection sent the squad there; the hold is the
	/// right click as without a squad, and the key wheel's right mouse button gives taps that order). mobile.txt:
	/// squadTaps=1 brings it back.
	/// </summary>
	internal static class Squad
	{
		// How far from the finger (in cells) a pawn still counts as tapped.
		private const float PawnRadius = 0.6f;

		private static bool enabled;

		public static void Start(Harmony harmony)
		{
			enabled = Settings.Get("squadTaps", 0) != 0;
			if (!enabled)
			{
				return;
			}
			try
			{
				harmony.Patch(AccessTools.Method(typeof(Selector), "HandleMapClicks"), prefix: new HarmonyMethod(typeof(Squad), nameof(SingleClicks)));
				Console.WriteLine("[mobile] squad: a drafted squad takes orders by tapping");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] squad: not patched: " + e);
			}
		}

		/// <summary>Whether the selection is a drafted squad on the map (and no tool or targeting is active).</summary>
		public static bool Active
		{
			get
			{
				if (!enabled || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || WorldRendererUtility.WorldSelected)
				{
					return false;
				}
				if (Find.DesignatorManager.SelectedDesignator != null || Find.Targeter.IsTargeting)
				{
					return false;
				}
				List<object> selected = Find.Selector.SelectedObjectsListForReading;
				if (selected.Count == 0)
				{
					return false;
				}
				bool drafted = false;
				foreach (object o in selected)
				{
					if (!(o is Pawn pawn) || pawn.Faction != Faction.OfPlayer)
					{
						return false;
					}
					drafted |= pawn.Drafted;
				}
				return drafted;
			}
		}

		/// <summary>The pawn nearest to the mouse on the map, if one is close enough to have been meant.</summary>
		private static Pawn PawnAtMouse()
		{
			Vector3 at = UI.MouseMapPosition();
			if (!at.ToIntVec3().InBounds(Find.CurrentMap))
			{
				return null;
			}
			var parms = new TargetingParameters { canTargetPawns = true, canTargetBuildings = false, canTargetItems = false, mapObjectTargetsMustBeAutoAttackable = false };
			Pawn nearest = null;
			float best = PawnRadius;
			foreach (Thing thing in GenUI.ThingsUnderMouse(at, 1f, parms))
			{
				if (thing is Pawn pawn && !pawn.Dead)
				{
					float distance = (pawn.DrawPos - at).MagnitudeHorizontal();
					if (distance < best)
					{
						best = distance;
						nearest = pawn;
					}
				}
			}
			return nearest;
		}

		private static bool CanJoin(Pawn pawn)
		{
			return pawn.Spawned && !pawn.Downed && pawn.Faction == Faction.OfPlayer && (pawn.IsColonistPlayerControlled || pawn.IsColonyMechPlayerControlled);
		}

		/// <summary>
		/// A tap on the map with a squad selected (the mouse is where the finger was): true when it was one of the
		/// colony's own and joined or left the selection; false when the tap is an order (the caller right-clicks).
		/// </summary>
		public static bool TapJoins()
		{
			try
			{
				Pawn pawn = PawnAtMouse();
				if (pawn == null || !CanJoin(pawn))
				{
					return false;
				}
				Selector selector = Find.Selector;
				if (selector.IsSelected(pawn))
				{
					selector.Deselect(pawn);
				}
				else
				{
					selector.Select(pawn, playSound: true, forceDesignatorDeselect: false);
				}
				return true;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] squad: tap not handled: " + e.Message);
				return false;
			}
		}

		/// <summary>Prefix of Selector.HandleMapClicks: with a squad selected, a press of the left button is a single click.</summary>
		public static void SingleClicks()
		{
			Event e = Event.current;
			if (e != null && e.type == EventType.MouseDown && e.button == 0 && e.clickCount > 1 && Touch.TouchInProgress && Active)
			{
				e.clickCount = 1;
			}
		}
	}
}
