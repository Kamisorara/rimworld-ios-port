using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace PortMobile
{
	/// <summary>
	/// Where a frame's time goes, for the 30 fps question. The engine's iOS loop is not driven by the display link
	/// (FrameCallbacksEnabled is never set): the run loop timer runs continuously and EngineApplication.ThrottleToTargetFrameRate
	/// waits for the render thread's backlog, so a frame that needs more than one 60 Hz vsync costs two. This measures the
	/// main thread's work before the throttle, the throttle itself (the backlog wait and any sleep), and the backlog wait,
	/// and prints a line every five seconds:
	///   [mobile] frame: 30.2 fps; interval median 33.2 ms, 99% 34.0, longest 50; work median 12.3, 99% 20.1;
	///   throttle median 20.9, 99% 21.0; backlog median 16.5, max 16.8; cap 60 fps from game ...; pacing true
	/// A work median near 33 ms means the main thread is the limit; a throttle/backlog median near 16 ms means the render
	/// thread (or the GPU behind it) is the limit.
	/// </summary>
	internal static class FrameTime
	{
		private static readonly Stopwatch clock = Stopwatch.StartNew();
		private static readonly List<double> intervals = new List<double>(1024);
		private static readonly List<double> works = new List<double>(1024);
		private static readonly List<double> throttles = new List<double>(1024);
		private static long backlogTicks, backlogMaxTicks;
		private static int backlogCount;
		private static long renderTicks, acquireTicks, recordTicks, submitTicks;
		private static int renderCount, acquireCount, recordCount, submitCount;

		private static int mainThread;
		private static long frameStart, throttleStart, backlogStart;
		[ThreadStatic] private static long renderStart, acquireStart, recordStart, submitStart;
		private static bool inFrame, throttleSeen;
		private static double lastFrameAt, windowStart, longest;
		private static int frames;
		private static FieldInfo pacingField;

		public static void Start(Harmony harmony)
		{
			try
			{
				mainThread = Environment.CurrentManagedThreadId;
				Type app = typeof(Waylith.EngineApplication);
				harmony.Patch(AccessTools.Method(app, "RunFrame"),
					prefix: new HarmonyMethod(typeof(FrameTime), nameof(BeforeFrame)),
					postfix: new HarmonyMethod(typeof(FrameTime), nameof(AfterFrame)));
				harmony.Patch(AccessTools.Method(app, "ThrottleToTargetFrameRate"),
					prefix: new HarmonyMethod(typeof(FrameTime), nameof(BeforeThrottle)),
					postfix: new HarmonyMethod(typeof(FrameTime), nameof(AfterThrottle)));
				Type renderThread = AccessTools.TypeByName("Waylith.Rendering.RenderThread");
				harmony.Patch(AccessTools.Method(renderThread, "WaitForBacklogAtMost"),
					prefix: new HarmonyMethod(typeof(FrameTime), nameof(BeforeBacklog)),
					postfix: new HarmonyMethod(typeof(FrameTime), nameof(AfterBacklog)));
				harmony.Patch(AccessTools.Method(renderThread, "RenderFrame"),
					prefix: new HarmonyMethod(typeof(FrameTime), nameof(BeforeRenderFrame)),
					postfix: new HarmonyMethod(typeof(FrameTime), nameof(AfterRenderFrame)));
				Type renderContext = AccessTools.TypeByName("Waylith.Graphics.WindowRenderContext");
				harmony.Patch(AccessTools.Method(renderContext, "TryAcquireNextFrame", Type.EmptyTypes),
					prefix: new HarmonyMethod(typeof(FrameTime), nameof(BeforeAcquire)),
					postfix: new HarmonyMethod(typeof(FrameTime), nameof(AfterAcquire)));
				harmony.Patch(AccessTools.Method(renderContext, "TrySubmitFrame", Type.EmptyTypes),
					prefix: new HarmonyMethod(typeof(FrameTime), nameof(BeforeSubmit)),
					postfix: new HarmonyMethod(typeof(FrameTime), nameof(AfterSubmit)));
				Type frameRenderer = AccessTools.TypeByName("Waylith.Rendering.FrameRenderer");
				harmony.Patch(AccessTools.Method(frameRenderer, "RecordFrame"),
					prefix: new HarmonyMethod(typeof(FrameTime), nameof(BeforeRecord)),
					postfix: new HarmonyMethod(typeof(FrameTime), nameof(AfterRecord)));
				pacingField = AccessTools.Field(app, "_pacingPresents");
				windowStart = clock.Elapsed.TotalSeconds;
				Console.WriteLine("[mobile] frame time: measuring every 5 s");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] frame time not measured: " + e);
			}
		}

		public static void BeforeFrame()
		{
			if (!inFrame)
			{
				frameStart = Stopwatch.GetTimestamp();
				inFrame = true;
				throttleSeen = false;
			}
		}

		public static void BeforeThrottle()
		{
			if (inFrame && !throttleSeen)
			{
				throttleStart = Stopwatch.GetTimestamp();
				throttleSeen = true;
				works.Add(Millis(throttleStart - frameStart));
			}
		}

		public static void AfterThrottle()
		{
			if (inFrame && throttleSeen)
			{
				throttles.Add(Millis(Stopwatch.GetTimestamp() - throttleStart));
			}
		}

		public static void BeforeBacklog()
		{
			if (Environment.CurrentManagedThreadId == mainThread)
			{
				backlogStart = Stopwatch.GetTimestamp();
			}
		}

		public static void AfterBacklog()
		{
			if (Environment.CurrentManagedThreadId == mainThread && backlogStart != 0)
			{
				long ticks = Stopwatch.GetTimestamp() - backlogStart;
				backlogStart = 0;
				Interlocked.Add(ref backlogTicks, ticks);
				Interlocked.Increment(ref backlogCount);
				long oldMax = Interlocked.Read(ref backlogMaxTicks);
				while (ticks > oldMax)
				{
					long seen = Interlocked.CompareExchange(ref backlogMaxTicks, ticks, oldMax);
					if (seen == oldMax) break;
					oldMax = seen;
				}
			}
		}

		public static void BeforeRenderFrame()
		{
			renderStart = Stopwatch.GetTimestamp();
		}

		public static void AfterRenderFrame()
		{
			if (renderStart != 0)
			{
				Interlocked.Add(ref renderTicks, Stopwatch.GetTimestamp() - renderStart);
				Interlocked.Increment(ref renderCount);
				renderStart = 0;
			}
		}

		public static void BeforeAcquire()
		{
			acquireStart = Stopwatch.GetTimestamp();
		}

		public static void AfterAcquire()
		{
			if (acquireStart != 0)
			{
				Interlocked.Add(ref acquireTicks, Stopwatch.GetTimestamp() - acquireStart);
				Interlocked.Increment(ref acquireCount);
				acquireStart = 0;
			}
		}

		public static void BeforeRecord()
		{
			recordStart = Stopwatch.GetTimestamp();
		}

		public static void AfterRecord()
		{
			if (recordStart != 0)
			{
				Interlocked.Add(ref recordTicks, Stopwatch.GetTimestamp() - recordStart);
				Interlocked.Increment(ref recordCount);
				recordStart = 0;
			}
		}

		public static void BeforeSubmit()
		{
			submitStart = Stopwatch.GetTimestamp();
		}

		public static void AfterSubmit()
		{
			if (submitStart != 0)
			{
				Interlocked.Add(ref submitTicks, Stopwatch.GetTimestamp() - submitStart);
				Interlocked.Increment(ref submitCount);
				submitStart = 0;
			}
		}

		public static void AfterFrame()
		{
			if (!inFrame)
			{
				return;
			}
			inFrame = false;
			long now = Stopwatch.GetTimestamp();
			if (!throttleSeen)
			{
				works.Add(Millis(now - frameStart));
			}
			double seconds = clock.Elapsed.TotalSeconds;
			if (lastFrameAt > 0)
			{
				intervals.Add((seconds - lastFrameAt) * 1000.0);
			}
			lastFrameAt = seconds;
			frames++;
			longest = Math.Max(longest, Millis(now - frameStart));
			if (seconds - windowStart >= 5.0)
			{
				Report(seconds);
				windowStart = seconds;
			}
		}

		private static void Report(double now)
	{
			double seconds = Math.Max(0.001, now - windowStart);
			string cap = "?";
			bool pacing = false;
			try
			{
				cap = Waylith.EngineApplication.FrameRateCapDescription;
				object app = Waylith.EngineApplication.Instance;
				pacing = pacingField != null && app != null && pacingField.GetValue(app) is bool b && b;
			}
			catch
			{
			}
			int backlogFrames = Interlocked.Exchange(ref backlogCount, 0);
			long backlogSum = Interlocked.Exchange(ref backlogTicks, 0);
			long backlogMax = Interlocked.Exchange(ref backlogMaxTicks, 0);
			int renders = Interlocked.Exchange(ref renderCount, 0);
			int acquires = Interlocked.Exchange(ref acquireCount, 0);
			int records = Interlocked.Exchange(ref recordCount, 0);
			int submits = Interlocked.Exchange(ref submitCount, 0);
			long renderSum = Interlocked.Exchange(ref renderTicks, 0);
			long acquireSum = Interlocked.Exchange(ref acquireTicks, 0);
			long recordSum = Interlocked.Exchange(ref recordTicks, 0);
			long submitSum = Interlocked.Exchange(ref submitTicks, 0);
			Console.WriteLine(string.Format(
				"[mobile] frame: {0:F1} fps; interval median {1:F1} ms, 99% {2:F1}, longest {3:F0}; work median {4:F1}, 99% {5:F1}; throttle median {6:F1}, 99% {7:F1}; backlog median {8:F1}, max {9:F1}; render {14:F1} ms (acquire {15:F1}, record {16:F1}, submit {17:F1}); screen {10}x{11}; cap {12}; pacing {13}",
				frames / seconds, Median(intervals), High(intervals), longest, Median(works), High(works), Median(throttles), High(throttles),
				backlogFrames > 0 ? Millis(backlogSum) / backlogFrames : 0.0, Millis(backlogMax),
				UnityEngine.Screen.width, UnityEngine.Screen.height, cap, pacing,
				renders > 0 ? Millis(renderSum) / renders : 0.0,
				acquires > 0 ? Millis(acquireSum) / acquires : 0.0,
				records > 0 ? Millis(recordSum) / records : 0.0,
				submits > 0 ? Millis(submitSum) / submits : 0.0));
			frames = 0;
			longest = 0;
			intervals.Clear();
			works.Clear();
			throttles.Clear();
		}

		private static double Median(List<double> values)
		{
			if (values.Count == 0)
			{
				return 0.0;
			}
			values.Sort();
			return values[values.Count / 2];
		}

		private static double High(List<double> values)
		{
			if (values.Count == 0)
			{
				return 0.0;
			}
			values.Sort();
			return values[Math.Min(values.Count - 1, (int)(values.Count * 0.99))];
		}

		private static double Millis(long ticks)
		{
			return ticks * 1000.0 / Stopwatch.Frequency;
		}
	}
}
