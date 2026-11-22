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
	/// "Show TPS and FPS" on the interface page of the options, under the game's real-time clock.
	///
	/// The game has the two counter lines itself (DebugViewSettings.showTpsCounter / showFpsCounter, otherwise only
	/// reached through the developer tools); it draws them in the stack at the bottom right with the clock, on the map
	/// and on the world. Switched on here, they show what is measured over the last half second: the ticks that ran
	/// against the target of the speed (60 a second times the speed factor), and the frames drawn. The game's own TPS line
	/// is 1000 / its mean time per tick, capped at the target: what the processor could do if it only ticked, not what
	/// runs between the frame rate limit and the tick caps of the performance setting. In brackets: the mean time per tick
	/// and per frame, as the game gives them. A TPS clearly below the target is yellow.
	///
	/// Kept in mobile.txt as "showCounters=". Off, the counters are the game's again (for the developer tools).
	/// </summary>
	internal static class Counters
	{
		private static bool on = Settings.Get("showCounters", false);
		private static bool rowInserted;
		private static int lastFrame = -1, framesAtStart, ticksAtStart;
		private static float sampleStart = -1f, tps, fps;
		private static bool measured;

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(GlobalControlsUtility), nameof(GlobalControlsUtility.DrawTpsCounter)), prefix: new HarmonyMethod(typeof(Counters), nameof(DrawTps)));
				harmony.Patch(AccessTools.Method(typeof(GlobalControlsUtility), nameof(GlobalControlsUtility.DrawFpsCounter)), prefix: new HarmonyMethod(typeof(Counters), nameof(DrawFps)));
				MethodInfo uiOptions = AccessTools.Method(typeof(Dialog_Options), "DoUIOptions");
				harmony.Patch(uiOptions, prefix: new HarmonyMethod(typeof(Counters), nameof(BeforeUIOptions)), transpiler: new HarmonyMethod(typeof(Counters), nameof(RowAfterClock)), postfix: new HarmonyMethod(typeof(Counters), nameof(RowAtEnd)), finalizer: new HarmonyMethod(typeof(Counters), nameof(AfterUIOptions)));
				if (on)
				{
					Show(true);
				}
				Console.WriteLine("[mobile] counters: option ready" + (on ? ", shown" : ""));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] counters: not patched: " + e);
			}
		}

		private static void Show(bool show)
		{
			DebugViewSettings.showTpsCounter = show;
			DebugViewSettings.showFpsCounter = show;
			sampleStart = -1f;
			measured = false;
		}

		/// <summary>Transpiler of Dialog_Options.DoUIOptions: the row goes right after the real-time clock's (after Prefs.ShowRealtimeClock is set).</summary>
		public static IEnumerable<CodeInstruction> RowAfterClock(IEnumerable<CodeInstruction> instructions)
		{
			// Harmony runs this again whenever the method is patched once more (by a mod, or InlineGuard's rebuilding): each
			// run inserts the row into its own copy, and the last run says whether the row is there.
			MethodInfo setClock = AccessTools.PropertySetter(typeof(Prefs), nameof(Prefs.ShowRealtimeClock));
			bool inserted = false;
			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
				if (!inserted && instruction.operand is MethodInfo m && m == setClock)
				{
					inserted = true;
					yield return new CodeInstruction(OpCodes.Ldarg_1);
					yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Counters), nameof(OptionRow)));
				}
			}
			rowInserted = inserted;
			if (!inserted)
			{
				Console.WriteLine("[mobile] counters: the clock's row was not found, the option goes at the end of the page");
			}
		}

		// Whether the port's rows were drawn in this call of DoUIOptions, and whether a failure of the page was logged.
		private static bool rowsDrawn, failureLogged;

		public static void BeforeUIOptions()
		{
			rowsDrawn = false;
		}

		/// <summary>
		/// Finalizer of DoUIOptions: a page that a mod's patch makes throw part way still ends, with the port's rows. Each
		/// frame the exception left the window and its scroll view open, and everything after the failing row was gone:
		/// the camera hole, the input mode and the key wheel could not be switched (a tester, 2026-10-27, with an old copy
		/// of Shella Backgrounds made for 1.6: "设置没有完全显示"). The game's rows after the failure stay missing, as on a
		/// PC; the failure is logged once.
		/// </summary>
		public static Exception AfterUIOptions(Exception __exception, Listing_Standard listing)
		{
			if (__exception == null)
			{
				return null;
			}
			if (!failureLogged)
			{
				failureLogged = true;
				Log.Error("The interface page of the options threw part way (a mod's patch of Dialog_Options.DoUIOptions); the rest of the page is left out, the port's options are shown after what was drawn: " + __exception);
			}
			if (!rowsDrawn && listing != null)
			{
				GUI.color = Color.white;
				Text.Anchor = TextAnchor.UpperLeft;
				OptionRow(listing);
			}
			return null;
		}

		/// <summary>Postfix of DoUIOptions: the row at the end of the page, where it could not go under the clock.</summary>
		public static void RowAtEnd(Listing_Standard listing)
		{
			if (!rowInserted)
			{
				OptionRow(listing);
			}
		}

		public static void OptionRow(Listing_Standard listing)
		{
			rowsDrawn = true;
			try
			{
				bool check = on;
				listing.CheckboxLabeled(Texts.Chinese ? "显示 TPS 和 FPS" : "Show TPS and FPS", ref check, Texts.Chinese
					? "在右下角的时间信息里（实时时钟下方）显示：TPS（实际每秒 tick 数 / 当前速度的目标，括号里是平均每 tick 耗时）和 FPS（实际每秒帧数，括号里是平均每帧耗时）。TPS 明显低于目标时显示为黄色。"
					: "Shows in the time readout at the bottom right (under the real-time clock): TPS (the ticks a second that actually run / the target of the speed; in brackets the mean time per tick) and FPS (the frames a second; in brackets the mean time per frame). A TPS clearly below its target is yellow.", 30f, 0.6f);
				if (check != on)
				{
					on = check;
					Settings.Set("showCounters", on ? "1" : "0");
					Show(on);
				}
				// Touch, or the game's mouse and keyboard handling.
				InputMode.OptionRow(listing);
				// The key wheel's switch, in the same place, and the mouse buttons' after it.
				KeyWheel.OptionRow(listing);
				MouseButtons.OptionRow(listing);
				// The interface as on a PC; while it is on, the rows of what it leaves out are not shown.
				Compat.OptionRow(listing);
				if (!Compat.On)
				{
					// Whether the interface keeps clear of the camera's hole.
					Cutout.OptionRow(listing);
					// The size of the selection's commands.
					GizmoFit.OptionRows(listing);
				}
				// Whether the main menu tells of Workshop mods with a newer version.
				UpdateNotice.OptionRow(listing);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] counters: option not drawn: " + e.Message);
			}
		}

		/// <summary>Once a frame: ticks and frames counted over half a second.</summary>
		private static void Measure()
		{
			int frame = Time.frameCount;
			if (frame == lastFrame)
			{
				return;
			}
			lastFrame = frame;
			float now = Time.realtimeSinceStartup;
			int ticks = Find.TickManager.TicksGame;
			// Not drawn for a while (another screen, a load) or another game: start over.
			if (sampleStart < 0f || now - sampleStart > 3f || ticks < ticksAtStart)
			{
				sampleStart = now;
				ticksAtStart = ticks;
				framesAtStart = frame;
				return;
			}
			float span = now - sampleStart;
			if (span >= 0.5f)
			{
				tps = (ticks - ticksAtStart) / span;
				fps = (frame - framesAtStart) / span;
				measured = true;
				sampleStart = now;
				ticksAtStart = ticks;
				framesAtStart = frame;
			}
		}

		/// <summary>Prefix of GlobalControlsUtility.DrawTpsCounter (the same place and look as the game's line).</summary>
		public static bool DrawTps(float leftX, float width, ref float curBaseY)
		{
			if (!on || Find.TickManager == null)
			{
				return true;
			}
			Measure();
			TickManager tickManager = Find.TickManager;
			float target = 60f * tickManager.TickRateMultiplier;
			string text;
			Color color = Color.white;
			if (target <= 0f)
			{
				text = "TPS: " + (Texts.Chinese ? "暂停" : "paused");
			}
			else
			{
				text = "TPS: " + (measured ? Mathf.Min(tps, target).ToString("F0") : "-") + " / " + target.ToString("F0") + " (" + tickManager.MeanTickTime.ToString("F1") + "ms)";
				if (measured && tps < target * 0.9f)
				{
					color = new Color(1f, 0.85f, 0.4f);
				}
			}
			Line(leftX, width, ref curBaseY, text, color);
			return false;
		}

		/// <summary>Prefix of GlobalControlsUtility.DrawFpsCounter.</summary>
		public static bool DrawFps(float leftX, float width, ref float curBaseY)
		{
			if (!on || Find.TickManager == null)
			{
				return true;
			}
			Measure();
			float frameTime = Root.AverageFrameTime;
			float shown = measured ? fps : (frameTime > 0f ? 1000f / frameTime : 0f);
			Line(leftX, width, ref curBaseY, "FPS: " + shown.ToString("F0") + " (" + frameTime.ToString("F1") + "ms)", Color.white);
			return false;
		}

		private static void Line(float leftX, float width, ref float curBaseY, string text, Color color)
		{
			Rect rect = new Rect(leftX, curBaseY - 26f, width - 7f, 26f);
			Text.Anchor = TextAnchor.MiddleRight;
			GUI.color = color;
			Widgets.Label(rect, text);
			GUI.color = Color.white;
			Text.Anchor = TextAnchor.UpperLeft;
			curBaseY -= 26f;
		}
	}
}
