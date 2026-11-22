using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The few places where the game's own layout is given more room for a finger. Only layouts that size themselves
	/// from the value changed here are touched (the menu, the list and the column grow, nothing else moves), and each
	/// is the game's own class, which a mod's interface uses as it is:
	///   float menus        the rows get the vertical margin the game itself uses on a Steam Deck (10 instead of 4: a
	///                      row is 42 units, 4.4 mm, instead of 30), and the rows of a menu with more than 60 entries,
	///                      which the game draws with a margin of 1, get 5. mobile.txt: floatMenuMargin=10,
	///                      floatMenuTinyMargin=5 (0 leaves the game's values);
	///   thing filters      (storage, bills, outfits...) the lines of the tree are 20 units with 2 between them; here
	///                      10 between them, so that a finger between two checkboxes is nearer to one of them
	///                      (TapAssist then presses that one). The lines themselves are drawn as they were.
	///                      filterRowGap=10 (0 leaves it);
	///   time controls      the pause and speed buttons are 32 x 24 units (3.3 x 2.5 mm) in a row; here 1.4 times that,
	///                      which still fits the column they are in. timeButtons=1.4 (0 or 1 leaves them). Left alone
	///                      when a mod has patched the game's time controls, except Smart Speed, whose five buttons are
	///                      made larger the same way (SmartSpeedButtons).
	/// </summary>
	internal static class TouchSizes
	{
		private static float menuMargin, menuTinyMargin, filterGap, timeScale;
		private static readonly FieldInfo SizeMode = AccessTools.Field(typeof(FloatMenuOption), "sizeMode");

		private static Harmony patcher;

		public static void Start(Harmony harmony)
		{
			patcher = harmony;
			menuMargin = Number("floatMenuMargin", 10f);
			menuTinyMargin = Number("floatMenuTinyMargin", 5f);
			filterGap = Number("filterRowGap", 10f);
			timeScale = Number("timeButtons", 1.4f);
			string done = "";
			try
			{
				if (menuMargin > 0f || menuTinyMargin > 0f)
				{
					harmony.Patch(AccessTools.PropertyGetter(typeof(FloatMenuOption), "VerticalMargin"), postfix: new HarmonyMethod(typeof(TouchSizes), nameof(AfterMenuMargin)));
					done += "float menu rows (margin " + menuMargin + ", " + menuTinyMargin + " in long menus)";
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] touch sizes: float menus left as they are: " + e.Message);
			}
			try
			{
				if (filterGap > 0f)
				{
					foreach (ConstructorInfo constructor in typeof(Listing_TreeThingFilter).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
					{
						harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(TouchSizes), nameof(AfterFilterTree)));
					}
					done += (done.Length > 0 ? ", " : "") + "thing filter lines (" + filterGap + " between them)";
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] touch sizes: thing filters left as they are: " + e.Message);
			}
			try
			{
				if (timeScale > 0f && timeScale != 1f)
				{
					// Written now, before any method that reads it is compiled (the compiler may take the value of a field that is never written again for a constant).
					ref Vector2 size = ref AccessTools.StaticFieldRefAccess<Vector2>(AccessTools.Field(typeof(TimeControls), nameof(TimeControls.TimeButSize)))();
					timeWas = size;
					// Four buttons have to fit the column of 200 units less the 16 the game leaves at its left.
					float scale = Mathf.Min(timeScale, 184f / (timeWas.x * 4f));
					size = new Vector2(Mathf.Floor(timeWas.x * scale), Mathf.Floor(timeWas.y * scale));
					playSettingsShift = size.y - timeWas.y;
					done += (done.Length > 0 ? ", " : "") + "time buttons " + timeWas.x + " x " + timeWas.y + " -> " + size.x + " x " + size.y;
				}
			}
			catch (Exception e)
			{
				timeScale = 0f;
				Console.WriteLine("[mobile] touch sizes: time controls left as they are: " + e.Message);
			}
			Console.WriteLine("[mobile] touch sizes: " + (done.Length > 0 ? done : "nothing changed"));
		}

		private static float Number(string key, float fallback)
		{
			string text = Settings.Get(key, null);
			return text != null && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) && value >= 0f ? value : fallback;
		}

		/// <summary>Postfix of FloatMenuOption.VerticalMargin: the space above and below a row's text (the game's with a mouse: InputMode).</summary>
		public static void AfterMenuMargin(FloatMenuOption __instance, ref float __result)
		{
			if (InputMode.Mouse)
			{
				return;
			}
			float least = (FloatMenuSizeMode)SizeMode.GetValue(__instance) == FloatMenuSizeMode.Normal ? menuMargin : menuTinyMargin;
			if (__result < least)
			{
				__result = least;
			}
		}

		/// <summary>Postfix of the thing filter tree's constructor.</summary>
		public static void AfterFilterTree(Listing_TreeThingFilter __instance)
		{
			if (__instance.verticalSpacing < filterGap && !InputMode.Mouse)
			{
				__instance.verticalSpacing = filterGap;
			}
		}

		// ---- the time controls ----

		private static bool timeDone;
		private static Vector2 timeWas;

		/// <summary>
		/// Every frame until the interface is first drawn with the mods in place (the main menu, or a game loaded straight
		/// away): the time controls get their strip, unless a mod has its own way with them - then the buttons get their
		/// size back.
		/// </summary>
		public static void BeforeInterface()
		{
			if (timeDone)
			{
				return;
			}
			if (Current.ProgramState != ProgramState.Playing && (Current.ProgramState != ProgramState.Entry || LongEventHandler.AnyEventNowOrWaiting))
			{
				return;
			}
			timeDone = true;
			Harmony harmony = patcher;
			if (harmony == null || timeScale <= 0f || timeScale == 1f)
			{
				return;
			}
			try
			{
				MethodInfo column = AccessTools.Method(typeof(GlobalControlsUtility), nameof(GlobalControlsUtility.DoTimespeedControls));
				MethodInfo buttons = AccessTools.Method(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI));
				MethodInfo settings = AccessTools.Method(typeof(GlobalControlsUtility), nameof(GlobalControlsUtility.DoPlaySettings));
				bool smartSpeed = false;
				foreach (MethodInfo method in new[] { column, buttons })
				{
					Patches patches = Harmony.GetPatchInfo(method);
					// The port's own patches (GizmoFit measures where the time buttons are) are not a mod's: counted, they had
					// left the buttons small for everyone since 2026-10-26 (found by the UI smoke test, host/ui-smoke.js).
					List<string> mods = patches == null ? new List<string>() : patches.Prefixes.Concat(patches.Transpilers).Select(p => p.owner).Where(o => !o.StartsWith("port17.", StringComparison.Ordinal)).Distinct().ToList();
					if (method == buttons && SmartSpeedAlone(patches, mods))
					{
						smartSpeed = true;
						continue;
					}
					if (mods.Count > 0)
					{
						AccessTools.StaticFieldRefAccess<Vector2>(AccessTools.Field(typeof(TimeControls), nameof(TimeControls.TimeButSize)))() = timeWas;
						Console.WriteLine("[mobile] touch sizes: the time controls are patched by a mod (" + string.Join(", ", mods) + "), left as they are");
						return;
					}
				}
				harmony.Patch(column, prefix: new HarmonyMethod(typeof(TouchSizes), nameof(BeforeTimeColumn)));
				// Only once the column gives them their strip: in the game's (150 units, which Smart Speed moves 14 left) five of
				// the larger buttons would reach past the screen's edge. Before the view toggles' patch, so that a failure of that
				// one leaves only the toggles out of place.
				if (smartSpeed)
				{
					SmartSpeedButtons(harmony, buttons);
				}
				harmony.Patch(settings, prefix: new HarmonyMethod(typeof(TouchSizes), nameof(BeforePlaySettings)));
				Console.WriteLine("[mobile] touch sizes: the time buttons have their strip" + (smartSpeed ? " (Smart Speed's five, " + TimeControls.TimeButSize.x + " x " + TimeControls.TimeButSize.y + ")" : ""));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] touch sizes: time controls left as they are: " + e.Message);
			}
		}

		private static float playSettingsShift;

		// How many buttons the strip holds (the game's four, Smart Speed's five), the buttons' size with Smart Speed, and the
		// strip the column hands the buttons while it draws them.
		private static int timeButtons = 4;
		private static Vector2 smartSpeedSize;
		private static Rect strip;
		private static bool inColumn;

		/// <summary>
		/// Stands in for GlobalControlsUtility.DoTimespeedControls, which gives the buttons a strip of 150 units: the
		/// strip is as wide as the buttons, and ends where the game's ends (6 units before the column's edge).
		/// </summary>
		public static bool BeforeTimeColumn(float leftX, float width, ref float curBaseY)
		{
			Vector2 size = TimeControls.TimeButSize;
			float all = size.x * timeButtons;
			Rect timerRect = new Rect(leftX + width - 6f - all, curBaseY - size.y, all, size.y);
			strip = timerRect;
			inColumn = true;
			try
			{
				TimeControls.DoTimeControlsGUI(timerRect);
			}
			finally
			{
				inColumn = false;
			}
			curBaseY -= timerRect.height;
			return false;
		}

		// ---- Smart Speed (1504723424) ----

		private const string SmartSpeedId = "com.smartspeed";

		/// <summary>
		/// Whether the only mod with the time buttons is Smart Speed, as it is known: its prefix of DoTimeControlsGUI
		/// (CallGUIConfig) sets the buttons to 28 x 24 before every drawing and moves the strip 14 units left, and its
		/// transpiler draws the fifth button (ultrafast) too. The buttons are drawn at TimeButSize as the game's are.
		/// </summary>
		private static bool SmartSpeedAlone(Patches patches, List<string> mods)
		{
			if (patches == null || mods.Count != 1 || mods[0] != SmartSpeedId)
			{
				return false;
			}
			List<Patch> theirs = patches.Prefixes.Concat(patches.Transpilers).Where(p => p.owner == SmartSpeedId).ToList();
			return theirs.Count == 2 && patches.Postfixes.All(p => p.owner != SmartSpeedId) && patches.Finalizers.All(p => p.owner != SmartSpeedId)
				&& theirs.Any(p => p.PatchMethod.Name == "CallGUIConfig" && p.PatchMethod.DeclaringType?.Name == "SmartSpeed_TimeControls_DoTimeControlsGUI_Prefix")
				&& theirs.Any(p => p.PatchMethod.DeclaringType?.Name == "SmartSpeed_TimeControls_DoTimeControlsGUI_Transpiler");
		}

		/// <summary>
		/// With Smart Speed the strip holds its five buttons, larger by as much as five of its 28 x 24 fit the column (the
		/// same 184 units as the game's four). The size is set again after its prefix (SmartSpeedPlaced), and stays between
		/// frames, so that the column and the row of view toggles place themselves by it as with the game's buttons.
		/// </summary>
		private static void SmartSpeedButtons(Harmony harmony, MethodInfo buttons)
		{
			Vector2 theirs = new Vector2(28f, 24f);
			float scale = Mathf.Min(timeScale, 184f / (theirs.x * 5f));
			smartSpeedSize = new Vector2(Mathf.Floor(theirs.x * scale), Mathf.Floor(theirs.y * scale));
			timeButtons = 5;
			TimeControls.TimeButSize = smartSpeedSize;
			playSettingsShift = smartSpeedSize.y - timeWas.y;
			// After Smart Speed's prefix (the usual priority), before GizmoFit's (last), which measures where the strip begins.
			harmony.Patch(buttons, prefix: new HarmonyMethod(typeof(TouchSizes), nameof(SmartSpeedPlaced)) { priority = Priority.VeryLow });
		}

		/// <summary>Prefix of TimeControls.DoTimeControlsGUI after Smart Speed's: the buttons' size is the larger one again, and in the column the strip is the one made for five of them.</summary>
		public static void SmartSpeedPlaced(ref Rect timerRect)
		{
			TimeControls.TimeButSize = smartSpeedSize;
			if (inColumn)
			{
				timerRect = strip;
			}
		}

		/// <summary>The row of view toggles above places itself by the height of a time button, where the game means the height of its own icons.</summary>
		public static void BeforePlaySettings(ref float curBaseY)
		{
			curBaseY += playSettingsShift;
		}
	}
}
