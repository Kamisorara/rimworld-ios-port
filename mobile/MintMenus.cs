using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Dubs Mint Menus' wheel (薄荷轮盘) on a touch screen. The wheel opens around the mouse, which on the phone is where the
	/// finger last was (a key wheel button does not move it: tap the spot on the map, then the button). There, near the
	/// bottom of the screen, the mod keeps the ring inside the screen but lets it cover the main button bar, where a
	/// slot cannot be tapped (the bar takes it). When the wheel opens it is moved up above the bar, as far as needed.
	/// The rest works by touch as it is: a tap on a slot picks the tool and closes the wheel, a tap outside closes it,
	/// "设置" (edit mode) adds a tool tapped in the architect menu. Nothing is done when the mod is not loaded.
	/// </summary>
	internal static class MintMenus
	{
		private static FieldInfo mousePos;
		private static MethodInfo area;

		public static void Start(Harmony harmony)
		{
			Type window = AccessTools.TypeByName("DubsMintMenus.MainTabWindow_MayaMenu");
			Type wheel = AccessTools.TypeByName("DubsMintMenus.MintWheel");
			if (window == null || wheel == null)
			{
				return;
			}
			try
			{
				mousePos = AccessTools.Field(window, "mousePos");
				area = AccessTools.Method(wheel, "Area", new[] { typeof(Vector2) });
				MethodInfo postOpen = AccessTools.DeclaredMethod(window, "PostOpen");
				if (mousePos == null || area == null || postOpen == null)
				{
					Console.WriteLine("[mobile] mint menus: the wheel is not as expected; left as it is");
					return;
				}
				harmony.Patch(postOpen, postfix: new HarmonyMethod(typeof(MintMenus), nameof(AfterPostOpen)));
				Console.WriteLine("[mobile] mint menus: the wheel opens above the main button bar");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mint menus: not installed: " + e.Message);
			}
		}

		public static void AfterPostOpen()
		{
			try
			{
				var center = (Vector2)mousePos.GetValue(null);
				var rect = (Rect)area.Invoke(null, new object[] { center });
				// The main button bar (35) and a little room. The wheel's own check closes it when the mouse is more
				// than 50 from it: the mouse, at most at the screen's bottom, stays within that of the new bottom edge.
				float limit = UI.screenHeight - 35f - 8f;
				if (center.y + rect.height / 2f > limit && rect.height < limit)
				{
					center.y = limit - rect.height / 2f;
					mousePos.SetValue(null, center);
				}
			}
			catch (Exception e)
			{
				Log.ErrorOnce("[Port17] Mint wheel position: " + e.Message, 0x51a7e11);
			}
		}
	}
}
