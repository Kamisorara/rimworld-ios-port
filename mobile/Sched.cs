using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Waylith.Jobs;

namespace PortMobile
{
	/// <summary>
	/// Where the game's threads run. A phone's cores differ (this one: two small ones, five large, one fastest), the
	/// simulation is one thread that is busy all the time at high game speed, and the engine, written for a PC, hands its
	/// parallel jobs to seven workers that the phone may put anywhere, the small cores included.
	///
	/// Settings (Set, by name; perfcmd.txt while measuring):
	///     pin=1        the main thread runs on the fastest core only (checked every frame: the phone's game service
	///                  puts it back on "the large cores" every few seconds)
	///     clear=1      with pin: every other thread of the game is kept off the fastest core
	///     workers=...  the engine's job workers: sys (as the phone has them), big (large cores, not the fastest),
	///                  nosmall (large and fastest), all
	///     render=...   the render thread: sys, big, nosmall, or one core's number
	///     hint=N       Android's performance hint session for the main and the render thread, a frame to take N ms
	///     hintflags=F  with hint: the session made as Android 16 makes it (1 the graphics pipeline, 2 tied to the game's
	///                  window, 4 timed by the system from that window); 0 the Android 13 way
	///     rate=N       the frame rate told to the compositor (and through it to whoever listens)
	///     batches=N    a parallel job's share per thread is cut into N pieces (0: the game's own rule), so that the work
	///                  goes to whichever thread is free instead of waiting for the slowest core's whole share. With 57
	///                  colonists at triple speed: 41 -> 44 frames a second, the main thread busy 87% -> 94% of the time
	/// </summary>
	internal static class Sched
	{
		[DllImport("libc", SetLastError = true)] private static extern int sched_setaffinity(int pid, IntPtr size, ref ulong mask);
		[DllImport("libc", SetLastError = true)] private static extern int sched_getaffinity(int pid, IntPtr size, ref ulong mask);
		[DllImport("libc")] private static extern int gettid();
		[DllImport("glfw")] private static extern int glfwAndroidPerfHint(int mainTid, int renderTid, long targetNanos);
		[DllImport("glfw")] private static extern int glfwAndroidPerfHintConfig(int mainTid, int renderTid, long targetNanos, int flags);
		[DllImport("glfw")] private static extern void glfwAndroidPerfReport(long actualNanos);
		[DllImport("glfw")] private static extern int glfwAndroidSetFrameRate(float framesPerSecond);

		public static int MainThreadId;

		// What is on without being asked for: the batches (mobile.txt: jobBatches=0 for the game's own rule). The rest
		// made no difference when measured on the phone this was written on, whose game service already keeps the main
		// and the render thread on the large cores and the fastest one; they stay for measuring on other phones.
		private static int pin, clear, batches = Settings.Get("jobBatches", 8);
		private static string workers = "sys", render = "sys";
		private static bool dirty;
		private static int hintMs, hintOpenFor, hintFlags, hintFlagsOpen;
		private static float rate;
		private static double lastFrameAt;
		private static ulong fastest, large, small, all;
		private static int[] workerTids = new int[0];
		private static int renderTid;
		private static double nextSweep;
		private static int changes, refusals;
		private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

		public static void Start(Harmony harmony)
		{
			try
			{
				ReadCores();
				harmony.Patch(AccessTools.Method(typeof(UnityData), nameof(UnityData.GetIdealBatchCount)), postfix: new HarmonyMethod(typeof(Sched), nameof(AfterIdealBatchCount)));
				Console.WriteLine("[mobile] sched: cores fastest " + Mask(fastest) + ", large " + Mask(large) + ", small " + Mask(small) + "; " + WaylithJobRuntime.WorkerCount + " job workers");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] sched not installed: " + e);
			}
		}

		private static void ReadCores()
		{
			int count = Environment.ProcessorCount;
			var capacity = new int[count];
			int max = 0, min = int.MaxValue;
			for (int i = 0; i < count; i++)
			{
				try
				{
					capacity[i] = int.Parse(File.ReadAllText("/sys/devices/system/cpu/cpu" + i + "/cpu_capacity").Trim());
				}
				catch (Exception)
				{
					capacity[i] = 1024;
				}
				max = Math.Max(max, capacity[i]);
				min = Math.Min(min, capacity[i]);
			}
			for (int i = 0; i < count; i++)
			{
				ulong bit = 1UL << i;
				all |= bit;
				if (capacity[i] == max) fastest |= bit;
				else if (capacity[i] == min) small |= bit;
				else large |= bit;
			}
			if (large == 0)
			{
				// Two kinds of cores only: the slower kind is "large" for the purposes here.
				large = small;
				small = 0;
			}
		}

		public static void Set(string name, string value)
		{
			switch (name)
			{
				case "pin": pin = int.Parse(value); break;
				case "clear": clear = int.Parse(value); break;
				case "batches": batches = int.Parse(value); break;
				case "workers": workers = value; break;
				case "render": render = value; break;
				case "hint": hintMs = int.Parse(value); break;
				case "hintflags": hintFlags = int.Parse(value); break;
				case "rate":
					rate = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
					try { glfwAndroidSetFrameRate(rate); } catch (Exception e) { Console.WriteLine("[mobile] sched: frame rate not told: " + e.Message); }
					break;
				default: Console.WriteLine("[mobile] sched: unknown setting " + name); return;
			}
			dirty = true;
		}

		public static string Describe()
		{
			ulong mainMask = 0;
			if (MainThreadId != 0)
			{
				sched_getaffinity(MainThreadId, (IntPtr)8, ref mainMask);
			}
			return "pin=" + pin + " clear=" + clear + " workers=" + workers + " render=" + render + " batches=" + batches + " hint=" + hintMs + " hintflags=" + hintFlags + " rate=" + rate + " (main on " + Mask(mainMask) + ", " + changes + " set, " + refusals + " refused)";
		}

		/// <summary>Postfix of UnityData.GetIdealBatchCount.</summary>
		public static void AfterIdealBatchCount(ref int __result)
		{
			if (batches > 0)
			{
				__result = Math.Max(1, __result / batches);
			}
		}

		/// <summary>Once a frame, on the main thread.</summary>
		public static void OnFrame()
		{
			if (MainThreadId == 0)
			{
				return;
			}
			if (pin != 0 && fastest != 0 && fastest != all)
			{
				Want(MainThreadId, fastest);
			}
			double now = clock.Elapsed.TotalSeconds;
			Hint(now);
			if (dirty || now >= nextSweep)
			{
				nextSweep = now + 2.0;
				bool settingsChanged = dirty;
				dirty = false;
				Sweep(settingsChanged);
			}
		}

		/// <summary>
		/// "hint=N": a performance hint session of Android for the main and the render thread with N milliseconds as the
		/// time a frame may take; every frame's actual time is reported to it.
		/// </summary>
		private static void Hint(double now)
		{
			try
			{
				if (hintMs != hintOpenFor || (hintMs > 0 && hintFlags != hintFlagsOpen))
				{
					if (renderTid == 0)
					{
						renderTid = Perf.FindThread("RenderThread");
					}
					bool open = hintFlags > 0 && hintMs > 0 ? glfwAndroidPerfHintConfig(MainThreadId, renderTid, hintMs * 1000000L, hintFlags) != 0 : glfwAndroidPerfHint(MainThreadId, renderTid, hintMs * 1000000L) != 0;
					hintOpenFor = open ? hintMs : 0;
					hintFlagsOpen = open ? hintFlags : 0;
					if (hintOpenFor == 0)
					{
						hintMs = 0;
					}
				}
				else if (hintOpenFor > 0 && lastFrameAt > 0 && (hintFlagsOpen & 4) == 0)
				{
					glfwAndroidPerfReport((long)((now - lastFrameAt) * 1e9));
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] sched: performance hint failed: " + e.Message);
				hintMs = hintOpenFor = 0;
			}
			lastFrameAt = now;
		}

		private static bool Want(int tid, ulong mask)
		{
			ulong current = 0;
			if (sched_getaffinity(tid, (IntPtr)8, ref current) != 0 || current == mask)
			{
				return false;
			}
			if (sched_setaffinity(tid, (IntPtr)8, ref mask) == 0)
			{
				changes++;
				return true;
			}
			refusals++;
			return false;
		}

		private static ulong Named(string name, ulong whenSys)
		{
			switch (name)
			{
				case "sys": return whenSys;
				case "all": return all;
				case "big": return large;
				case "nosmall": return large | fastest;
				case "fastest": return fastest;
				default:
					return int.TryParse(name, out int core) && core >= 0 && core < 64 ? 1UL << core : whenSys;
			}
		}

		private static void Sweep(bool settingsChanged)
		{
			try
			{
				if (workerTids.Length == 0 && (workers != "sys" || settingsChanged))
				{
					FindWorkers();
				}
				if (renderTid == 0)
				{
					renderTid = Perf.FindThread("RenderThread");
				}
				var isWorker = new HashSet<int>(workerTids);
				foreach (string dir in Directory.GetDirectories("/proc/self/task"))
				{
					if (!int.TryParse(Path.GetFileName(dir), out int tid) || tid == MainThreadId)
					{
						continue;
					}
					ulong wanted = 0;
					if (isWorker.Contains(tid))
					{
						wanted = Named(workers, 0);
						// Back to "sys": what was narrowed here is widened again; the phone's own choice comes back by itself.
						if (wanted == 0 && settingsChanged) wanted = all;
					}
					else if (tid == renderTid)
					{
						wanted = Named(render, 0);
						if (wanted == 0 && settingsChanged) wanted = large | fastest;
					}
					bool keepOff = pin != 0 && clear != 0 && fastest != all;
					if (wanted != 0)
					{
						if (keepOff && (wanted & ~fastest) != 0) wanted &= ~fastest;
						Want(tid, wanted);
					}
					else if (keepOff)
					{
						ulong current = 0;
						if (sched_getaffinity(tid, (IntPtr)8, ref current) == 0 && (current & fastest) != 0 && (current & ~fastest) != 0)
						{
							Want(tid, current & ~fastest);
						}
					}
				}
				if (pin == 0 && settingsChanged)
				{
					Want(MainThreadId, large | fastest);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] sched: sweep failed: " + e.Message);
			}
		}

		/// <summary>The engine's job workers by thread id: a parallel job of many short pieces, each noting who ran it.</summary>
		private static void FindWorkers()
		{
			var seen = new HashSet<int>();
			try
			{
				for (int round = 0; round < 3; round++)
				{
					WaylithJobRuntime.For(0, 256, delegate(int i)
					{
						int tid = gettid();
						lock (seen)
						{
							seen.Add(tid);
						}
						Thread.SpinWait(4000);
					}, 1);
				}
				seen.Remove(MainThreadId);
				workerTids = new int[seen.Count];
				seen.CopyTo(workerTids);
				Console.WriteLine("[mobile] sched: " + workerTids.Length + " job worker threads found");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] sched: job workers not found: " + e.Message);
			}
		}

		private static string Mask(ulong mask)
		{
			if (mask == 0)
			{
				return "-";
			}
			var parts = new List<string>();
			for (int i = 0; i < 64; i++)
			{
				if ((mask & (1UL << i)) == 0)
				{
					continue;
				}
				int end = i;
				while (end + 1 < 64 && (mask & (1UL << (end + 1))) != 0)
				{
					end++;
				}
				parts.Add(end > i ? i + "-" + end : i.ToString());
				i = end;
			}
			return string.Join(",", parts);
		}
	}
}
