using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Where the letters on the right of the screen are, for a long press on one (Touch): a letter that a right click
	/// dismisses on the PC goes away under a finger that rests on it (testers, 2026-10-22). A tap still opens it.
	/// </summary>
	internal static class Letters
	{
		private static readonly List<Rect> dismissible = new List<Rect>();
		private static int drawnFrame = -1;

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Letter), nameof(Letter.DrawButtonAt)), postfix: new HarmonyMethod(typeof(Letters), nameof(AfterDrawButton)));
		}

		/// <summary>Postfix of Letter.DrawButtonAt: the letter's button, as the game places it (UI units).</summary>
		public static void AfterDrawButton(Letter __instance, float topY)
		{
			Event current = Event.current;
			if (current == null || current.type != EventType.Repaint)
			{
				return;
			}
			if (drawnFrame != Time.frameCount)
			{
				dismissible.Clear();
				drawnFrame = Time.frameCount;
			}
			if (__instance.CanDismissWithRightClick)
			{
				dismissible.Add(new Rect(UI.screenWidth - 38f - 12f, topY, 38f, 30f));
			}
		}

		/// <summary>Whether a letter that a right click dismisses is at this point of the screen (window pixels, y down).</summary>
		public static bool DismissibleAt(Vector2 pixels)
		{
			if (Time.frameCount - drawnFrame > 2)
			{
				return false;
			}
			Vector2 at = pixels / Prefs.UIScale;
			foreach (Rect rect in dismissible)
			{
				if (rect.ExpandedBy(4f).Contains(at))
				{
					return true;
				}
			}
			return false;
		}
	}
}
