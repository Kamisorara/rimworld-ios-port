using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The game moves the camera while the mouse rests at an edge of the screen (the option "屏幕边缘滚动", on by
	/// default): within 20 units of the top, the left or the right outside the corners, or 6 of the bottom. On a touch
	/// screen the engine's mouse stays where the last finger was, so a tap there kept the camera going until the next
	/// touch (testers, 2026-10-22: the save dialog's close button is at the top of the screen, and after it the map ran
	/// off upwards). While the mouse is where a finger put it (Touch.PointerFromFinger), the map's and the world's camera
	/// take it as over a window, which is what turns the edges off; a real mouse still scrolls at them.
	/// </summary>
	internal static class EdgeScroll
	{
		public static void Start(Harmony harmony)
		{
			var before = new HarmonyMethod(typeof(EdgeScroll), nameof(BeforeDolly));
			var after = new HarmonyMethod(typeof(EdgeScroll), nameof(AfterDolly));
			harmony.Patch(AccessTools.Method(typeof(CameraDriver), "CalculateCurInputDollyVect"), prefix: before, postfix: after);
			harmony.Patch(AccessTools.Method(typeof(WorldCameraDriver), "CalculateCurInputDollyVect"), prefix: before, postfix: after);
		}

		/// <summary>Prefix of CalculateCurInputDollyVect (the map's and the world's camera): a finger's mouse is not at an edge.</summary>
		public static void BeforeDolly(ref bool ___mouseCoveredByUI, out bool __state)
		{
			__state = ___mouseCoveredByUI;
			if (Touch.PointerFromFinger)
			{
				___mouseCoveredByUI = true;
			}
		}

		/// <summary>Postfix: the camera's own idea of it comes back.</summary>
		public static void AfterDolly(ref bool ___mouseCoveredByUI, bool __state)
		{
			___mouseCoveredByUI = __state;
		}
	}
}
