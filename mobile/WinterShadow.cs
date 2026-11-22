using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The game's "winter shadow" (GenUI.DrawTextWinterShadow): on a map with snow in daylight, a dark gradient from
	/// the screen's corners behind the white text of the resource list (top left, 256 x 512 units), the mouseover
	/// readout (bottom left, 256 x 256) and the time controls (bottom right, 270 x 450), and a shadow around the alerts
	/// (AlertsReadout takes the same strength). Its size is in interface units, so at the phone's scale (1.85 on a
	/// 1272 px tall screen: 688 units) the top-left one reaches down two thirds of the screen (measured: a seventh of
	/// its width), where on a PC (1080 units) it stays beside the list.
	/// Looked at for a tester's screenshot with the left of the map dark (2026-10-21): not that, its dark part reached
	/// half the screen's width. Kept as the game draws it; mobile.txt winterShadow=0 leaves it out, winterShadowTest=1
	/// draws it as on a snowy map at noon whatever the map (to see it, or compare, without snow).
	/// </summary>
	internal static class WinterShadow
	{
		public static void Start(Harmony harmony)
		{
			bool keep = Settings.Get("winterShadow", true);
			bool test = Settings.Get("winterShadowTest", false);
			try
			{
				MethodInfo alpha = AccessTools.Method(typeof(GenUI), nameof(GenUI.BackgroundDarkAlphaForText));
				if (alpha == null)
				{
					Console.WriteLine("[mobile] winter shadow: not as expected; left as it is");
					return;
				}
				if (test)
				{
					harmony.Patch(alpha, postfix: new HarmonyMethod(typeof(WinterShadow), nameof(AsOnSnow)));
					Console.WriteLine("[mobile] winter shadow: drawn as on a snowy map (winterShadowTest=1)");
				}
				else if (!keep)
				{
					harmony.Patch(alpha, postfix: new HarmonyMethod(typeof(WinterShadow), nameof(None)));
					Console.WriteLine("[mobile] winter shadow: not drawn (winterShadow=0)");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] winter shadow not installed: " + e.Message);
			}
		}

		public static void None(ref float __result)
		{
			__result = 0f;
		}

		public static void AsOnSnow(ref float __result)
		{
			if (Find.CurrentMap != null)
			{
				__result = 0.41f;
			}
		}
	}
}
