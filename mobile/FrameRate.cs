using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Two choices on the graphics page of the options, as the 1.6 port had them (tools/portmobile/FrameRate.cs and
	/// Schedule.cs there), kept in mobile.txt as "frameRate=" and "schedule=".
	///
	/// Frame rate limit: 30, 60, 90 or 120 frames a second (the phone's display runs at 30 to 165 Hz; the system's highest
	/// rate is a display setting of the phone, 120 here). Fewer frames mean less heat, and a cooler phone keeps its
	/// processor faster for the simulation. The engine holds the limit with its own timer (Application.targetFrameRate);
	/// the compositor is told the rate, so that the display can run at it or at a multiple.
	///
	/// Performance: what gives way when the phone cannot keep up with both the frames and the game speed. The game's
	/// tick loop (TickManager.TickManagerUpdate) ticks through the real time that passed since the last frame, but no
	/// further than two caps allow: a number of ticks (the speed factor times 2: 12 at the third speed) and a stretch of
	/// time (45 ms of ticking in one frame). What it could not tick through is dropped.
	///   default   the game's own caps. The count cap is scaled with the frame rate limit: at 30 frames a second a frame
	///             needs exactly the game's maximum, so every frame that is a little late would lose game time
	///   frames    the frame rate limit is kept: ticks get the time a frame has left after everything else (drawing,
	///             interface), at least one tick a frame; game time runs slower when that is little
	///   ticks     the game speed is kept: the caps go up (8 times the speed factor, 150 ms), so that the ticks the real
	///             time calls for are run, and the frames take as long as that takes
	///
	/// The wait for the limit. The engine waits out the rest of a frame with Thread.Sleep in whole milliseconds, one
	/// short of what is left, and then reads the clock in a loop until the frame's time is up: one to two milliseconds
	/// of every frame at full load (Windows cannot sleep more finely). Measured here: 11 % of the main thread's
	/// processor time with the game paused at 60 frames a second, 3 % in a scene too heavy to hold 60. This kernel
	/// sleeps to a fraction of a millisecond, so the wait here sleeps (nanosleep) until a short margin before the
	/// frame's end and only that margin is spent on the clock: "frameWaitSpin" in mobile.txt, microseconds, default
	/// 300; 0 leaves the engine's own wait in place. Since 1.7.4998 the engine waits this way itself (clock_nanosleep,
	/// with a margin it learns from how late its sleeps end), and its wait is left in place.
	/// </summary>
	internal static class FrameRate
	{
		[DllImport("glfw")] private static extern int glfwAndroidSetFrameRate(float framesPerSecond);
		[DllImport("glfw")] private static extern float glfwAndroidDisplayRate();

		[StructLayout(LayoutKind.Sequential)]
		private struct TimeSpec
		{
			public long Seconds, Nanoseconds;
		}

		[DllImport("libc")] private static extern int nanosleep(ref TimeSpec wanted, IntPtr remaining);

		private static int spinMicros = Mathf.Clamp(Settings.Get("frameWaitSpin", 300), 0, 2000);
		// Whether the wait below is in place: the engines before 1.7.4998, whose ThrottleToTargetFrameRate takes the frame's start.
		private static bool ownWait;
		// For the measuring report: how late the sleeps ended, and how long the loop on the clock ran.
		private static int waits, sleeps;
		private static double lateSum, lateMax, spinSum;

		private static int displayRate;
		private static float displayAskedAt = -10f;

		/// <summary>The display's refresh rate, asked at most once a second (0: not known).</summary>
		private static int DisplayRate()
		{
			if (Time.realtimeSinceStartup - displayAskedAt > 1f)
			{
				displayAskedAt = Time.realtimeSinceStartup;
				try
				{
					displayRate = Mathf.RoundToInt(glfwAndroidDisplayRate());
				}
				catch (Exception)
				{
					displayRate = 0;
				}
			}
			return displayRate;
		}

		public static readonly int[] Choices = { 30, 60, 90, 120 };
		public const int Default = 0, Frames = 1, Ticks = 2;
		private const float GameTimeCapMs = 45.454544f, TicksTimeCapMs = 150f;

		private static int limit = Clamp(Settings.Get("frameRate", 60));
		private static int schedule = Mathf.Clamp(Settings.Get("schedule", Default), Default, Ticks);
		private static int told = -1, toldCountdown;

		// The part of the last frame that was neither ticking nor idle (drawing, interface), smoothed. The frame's work
		// ends with the interface's repaint; what follows until the next frame's ticks is the wait for the frame rate
		// limit, which must not count: ticks may use it to catch up.
		private static float otherMs, lastEnd = -1f, start, drawEnd = -1f;

		private static int Clamp(int rate)
		{
			// The nearest choice.
			int best = Choices[0];
			foreach (int choice in Choices)
			{
				if (Math.Abs(choice - rate) < Math.Abs(best - rate))
				{
					best = choice;
				}
			}
			return best;
		}

		/// <summary>What the game multiplies its speed factor with to get the most ticks it runs in one frame (2 in the game's code).</summary>
		public static float TicksPerFrame => 2f * Mathf.Max(1f, 60f / limit) * (schedule == Ticks ? 4f : 1f);

		/// <summary>Stands in for the game's 45 ms: how long one frame may spend ticking.</summary>
		public static float TimeCapMs
		{
			get
			{
				switch (schedule)
				{
					case Frames: return Mathf.Clamp(1000f / limit - otherMs, 0f, GameTimeCapMs);
					case Ticks: return TicksTimeCapMs;
					default: return GameTimeCapMs;
				}
			}
		}

		public static void Start(Harmony harmony)
		{
			try
			{
				MethodInfo update = AccessTools.Method(typeof(TickManager), nameof(TickManager.TickManagerUpdate));
				harmony.Patch(update, prefix: new HarmonyMethod(typeof(FrameRate), nameof(BeforeTicks)), postfix: new HarmonyMethod(typeof(FrameRate), nameof(AfterTicks)), transpiler: new HarmonyMethod(typeof(FrameRate), nameof(ScaleTickCaps)));
				harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.Update)), postfix: new HarmonyMethod(typeof(FrameRate), nameof(EveryFrame)));
				harmony.Patch(AccessTools.Method(typeof(Root), "OnGUI"), postfix: new HarmonyMethod(typeof(FrameRate), nameof(AfterGui)));
				harmony.Patch(AccessTools.Method(typeof(Dialog_Options), "DoVideoOptions"), postfix: new HarmonyMethod(typeof(FrameRate), nameof(Options)));
				MethodInfo throttle = AccessTools.Method(typeof(Waylith.EngineApplication), "ThrottleToTargetFrameRate");
				ParameterInfo[] throttleParameters = throttle.GetParameters();
				ownWait = throttleParameters.Length == 1 && throttleParameters[0].ParameterType == typeof(float);
				if (ownWait)
				{
					harmony.Patch(throttle, prefix: new HarmonyMethod(typeof(FrameRate), nameof(WaitForFrameEnd)));
				}
				Console.WriteLine("[mobile] frame rate limit " + limit + ", schedule " + schedule + " (options, graphics page)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] frame rate options not installed: " + e);
			}
		}

		/// <summary>
		/// Prefix of EngineApplication.ThrottleToTargetFrameRate, in its place unless the margin is set to 0: first, as the
		/// engine does since 1.7.4985, waits until the render thread has at most one frame queued (the game does not run
		/// ahead of what is shown); then sleeps until the margin before the frame's end (again if a signal ended the sleep
		/// early: the collector suspends threads with one), then reads the clock until the end.
		/// </summary>
		private static bool WaitForFrameEnd(float frameStartElapsed, System.Diagnostics.Stopwatch ____stopwatch, Waylith.EngineApplication __instance)
		{
			if (spinMicros <= 0)
			{
				return true;
			}
			WaitForRenderBacklog(__instance);
			int target = Waylith.EngineWindow.TargetFrameRate;
			if (target <= 0)
			{
				return false;
			}
			double end = (double)frameStartElapsed + 1.0 / target;
			double margin = spinMicros * 1e-6;
			double now = ____stopwatch.Elapsed.TotalSeconds;
			for (int round = 0; round < 4 && end - now - margin > 0.00005; round++)
			{
				double sleep = end - now - margin;
				var wanted = new TimeSpec { Seconds = (long)sleep, Nanoseconds = (long)((sleep - Math.Floor(sleep)) * 1e9) };
				nanosleep(ref wanted, IntPtr.Zero);
				double woke = ____stopwatch.Elapsed.TotalSeconds;
				double late = woke - (now + sleep);
				if (late > 0)
				{
					lateSum += late;
					if (late > lateMax)
					{
						lateMax = late;
					}
				}
				sleeps++;
				now = woke;
			}
			double spinFrom = now;
			while (now < end)
			{
				now = ____stopwatch.Elapsed.TotalSeconds;
			}
			spinSum += now - spinFrom;
			waits++;
			return false;
		}

		// The engine's render thread and its WaitForBacklogAtMost (1.7.4985 and later; none before: nothing to wait for)
		private static readonly FieldInfo RenderThreadField = AccessTools.Field(typeof(Waylith.EngineApplication), "_renderThread");
		private static object renderThread;
		private static Action<int> waitForBacklog;

		private static void WaitForRenderBacklog(Waylith.EngineApplication app)
		{
			object current = RenderThreadField?.GetValue(app);
			if (current == null)
			{
				return;
			}
			if (current != renderThread)
			{
				renderThread = current;
				MethodInfo wait = AccessTools.Method(current.GetType(), "WaitForBacklogAtMost", new[] { typeof(int) });
				waitForBacklog = wait == null ? null : (Action<int>)wait.CreateDelegate(typeof(Action<int>), current);
			}
			waitForBacklog?.Invoke(1);
		}

		/// <summary>For the measuring report, since the last call: "wait=300 (sleeps late 85/410 us, clock loop 0.21 ms a frame)".</summary>
		public static string DescribeWait()
		{
			string text = ownWait ? "wait=" + spinMicros : "wait=engine";
			if (waits > 0)
			{
				text += " (sleeps late " + (sleeps > 0 ? lateSum / sleeps * 1e6 : 0).ToString("F0") + "/" + (lateMax * 1e6).ToString("F0") + " us, clock loop " + (spinSum / waits * 1e3).ToString("F2") + " ms a frame)";
			}
			waits = sleeps = 0;
			lateSum = lateMax = spinSum = 0;
			return text;
		}

		/// <summary>For measuring (perfcmd.txt): "frames=30", "schedule=1", "wait=300" (the margin in microseconds, 0: the engine's own wait).</summary>
		public static bool Set(string name, string value)
		{
			if (name == "wait") spinMicros = Mathf.Clamp(int.Parse(value), 0, 2000);
			else if (name == "frames") limit = Clamp(int.Parse(value));
			else if (name == "schedule") schedule = Mathf.Clamp(int.Parse(value), Default, Ticks);
			else return false;
			return true;
		}

		public static string Describe()
		{
			return "frames=" + limit + " schedule=" + schedule;
		}

		/// <summary>Postfix of Root.Update. The game sets the target once at its start (to the display's rate); it is kept up here.</summary>
		public static void EveryFrame()
		{
			if (Application.targetFrameRate != limit)
			{
				Application.targetFrameRate = limit;
			}
			// The window is made anew when the game comes back from the background, so the compositor is told again
			// now and then (the shim only logs a change).
			if (told != limit || --toldCountdown < 0)
			{
				told = limit;
				toldCountdown = 600;
				try
				{
					glfwAndroidSetFrameRate(limit);
				}
				catch (Exception)
				{
				}
			}
		}

		public static void BeforeTicks()
		{
			start = Time.realtimeSinceStartup;
			if (lastEnd >= 0f)
			{
				float workEnd = drawEnd > lastEnd && drawEnd <= start ? drawEnd : start;
				float other = Mathf.Max(0f, (workEnd - lastEnd) * 1000f);
				otherMs = otherMs <= 0f ? other : Mathf.Lerp(otherMs, other, 0.25f);
			}
		}

		public static void AfterTicks()
		{
			lastEnd = Time.realtimeSinceStartup;
		}

		/// <summary>Postfix of Root.OnGUI: the repaint is the last work of a frame.</summary>
		public static void AfterGui()
		{
			if (Event.current != null && Event.current.type == EventType.Repaint)
			{
				drawEnd = Time.realtimeSinceStartup;
			}
		}

		/// <summary>
		/// Transpiler of TickManager.TickManagerUpdate: "speed factor * 2" becomes "speed factor * TicksPerFrame", and the
		/// 45 ms a frame may spend ticking becomes TimeCapMs.
		/// </summary>
		public static IEnumerable<CodeInstruction> ScaleTickCaps(IEnumerable<CodeInstruction> instructions)
		{
			bool count = false, time = false;
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float value && (!count && value == 2f || !time && value > 45.4f && value < 45.5f))
				{
					MethodInfo getter;
					if (value == 2f)
					{
						count = true;
						getter = AccessTools.PropertyGetter(typeof(FrameRate), nameof(TicksPerFrame));
					}
					else
					{
						time = true;
						getter = AccessTools.PropertyGetter(typeof(FrameRate), nameof(TimeCapMs));
					}
					var call = new CodeInstruction(OpCodes.Call, getter);
					call.labels.AddRange(instruction.labels);
					yield return call;
				}
				else
				{
					yield return instruction;
				}
			}
			if (!count || !time)
			{
				Console.WriteLine("[mobile] frame rate: the tick loop looks different from what was expected (count cap " + (count ? "found" : "not found") + ", time cap " + (time ? "found" : "not found") + "); the performance setting may not work");
			}
		}

		private static string Name(int mode)
		{
			switch (mode)
			{
				case Frames: return Texts.Chinese ? "帧率优先" : "Frame rate first";
				case Ticks: return Texts.Chinese ? "游戏速度优先" : "Game speed first";
				default: return Texts.Chinese ? "默认" : "Default";
			}
		}

		private static string Explanation(int mode)
		{
			switch (mode)
			{
				case Frames:
					return Texts.Chinese
						? "跟不上时保住帧率上限：每帧只跑画面之外剩下的时间够跑的 tick（至少一个），游戏时间走得慢一些。"
						: "When the phone cannot keep up, the frame rate limit is kept: a frame runs only the ticks the time left after drawing allows (at least one), and game time runs slower.";
				case Ticks:
					return Texts.Chinese
						? "跟不上时保住游戏速度：一帧跑完该跑的 tick 再画，帧率跟着降（最低六帧左右）。"
						: "When the phone cannot keep up, the game speed is kept: a frame runs all the ticks that are due before it is drawn, and the frame rate drops (to about six at the least).";
				default:
					return Texts.Chinese
						? "游戏自己的规矩：一帧最多跑速度倍数的两倍个 tick、最多 45 毫秒；跟不上时帧率和游戏速度一起降。"
						: "The game's own rule: at most twice the speed factor in ticks and 45 ms of them per frame; when the phone cannot keep up, both the frame rate and the game speed drop.";
			}
		}

		/// <summary>Postfix of Dialog_Options.DoVideoOptions: the two rows, at the end of the graphics page.</summary>
		public static void Options(Listing_Standard listing)
		{
			try
			{
				string unit = Texts.Chinese ? " 帧" : " fps";
				if (listing.ButtonTextLabeledPct(Texts.Chinese ? "帧率上限" : "Frame rate limit", limit + unit, 0.6f, TextAnchor.MiddleLeft))
				{
					var choices = new List<FloatMenuOption>();
					foreach (int choice in Choices)
					{
						int rate = choice;
						choices.Add(new FloatMenuOption(rate + unit, delegate
						{
							limit = rate;
							Settings.Set("frameRate", rate.ToString());
						}));
					}
					Find.WindowStack.Add(new FloatMenu(choices));
				}
				int display = DisplayRate();
				if (display > 0 && limit > display)
				{
					// The phone decides the display's rate (its display settings, its game mode); the game can only ask.
					GUI.color = new Color(1f, 0.85f, 0.4f);
					listing.Label(Texts.Chinese
						? "屏幕现在是 " + display + " Hz，所以实际最多显示 " + display + " 帧（刷新率由手机系统的显示设置决定）。"
						: "The screen is at " + display + " Hz now, so " + display + " is the most it shows (the phone's display settings decide the rate).");
					GUI.color = Color.white;
				}
				if (listing.ButtonTextLabeledPct(Texts.Chinese ? "性能" : "Performance", Name(schedule), 0.6f, TextAnchor.MiddleLeft))
				{
					var choices = new List<FloatMenuOption>();
					foreach (int mode in new[] { Default, Frames, Ticks })
					{
						int chosen = mode;
						choices.Add(new FloatMenuOption(Name(chosen), delegate
						{
							schedule = chosen;
							Settings.Set("schedule", chosen.ToString());
						}));
					}
					Find.WindowStack.Add(new FloatMenu(choices));
				}
				GUI.color = new Color(0.75f, 0.75f, 0.75f);
				Text.Font = GameFont.Tiny;
				listing.Label(Explanation(schedule));
				Text.Font = GameFont.Small;
				GUI.color = Color.white;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] frame rate options not drawn: " + e.Message);
			}
		}
	}
}
