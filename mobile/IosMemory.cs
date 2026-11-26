using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PortMobile
{
	/// <summary>
	/// iOS: what jetsam counts for this process, and what is left of its allowance. The Android layer measures
	/// through /proc/self/status and /proc/meminfo and asks bionic's mallopt(M_PURGE_ALL) for the allocator's
	/// pages back; none of that exists here (0.4.43: "memory not trimmed" after every load, and no process
	/// memory in the record's snap lines). Everything iOS-specific about memory lives in this file, which
	/// sync-mobile.py does not overwrite; the shared files (MemTrim, Recorder) carry one-line "iOS:" hooks.
	///   - proc_pid_rusage(RUSAGE_INFO_V0).ri_phys_footprint: the number the system kills by. Anonymous dirty
	///     pages, compressed pages, IOKit/GPU mappings and page tables count; clean file pages do not. The host
	///     (host/app/memory.m) reads the same number, through the v2 structure, every 10 s into
	///     port17-status.txt and counts memory warnings.
	///   - os_proc_available_memory(): what this process may still take.
	///   - malloc_zone_pressure_relief(NULL, 0): libmalloc's counterpart of M_PURGE_ALL, every zone, fully.
	/// </summary>
	internal static class IosMemory
	{
		// libSystem under the name MemTrim already uses (0.4.43: the library loaded, only mallopt's entry point was missing).
		[DllImport("libc")] private static extern int getpid();
		[DllImport("libc")] private static extern int proc_pid_rusage(int pid, int flavor, byte[] buffer);
		[DllImport("libc")] private static extern ulong os_proc_available_memory();
		[DllImport("libc")] private static extern IntPtr malloc_zone_pressure_relief(IntPtr zone, UIntPtr goal);

		// ri_phys_footprint has been in the structure since its first version, so the smallest flavor is asked for.
		// The kernel sizes what it copies from the flavor alone and never learns how long the caller's array is
		// (libc's own wrapper picks the length the same way). The array is therefore far longer than any flavor's
		// structure (v0 = 96, v2 = 160 bytes), so that nothing here depends on the size table being complete.
		private const int RUSAGE_INFO_V0 = 0;
		private const int FootprintOffset = 72; // after the 16-byte uuid and seven uint64_t
		private const int RusageInfoSize = 512;

		private static int probed;

		/// <summary>The footprint in bytes; throws when the system will not tell (the callers decide what that means).</summary>
		public static long FootprintBytes()
		{
			SayWhatResolved();
			byte[] info = new byte[RusageInfoSize];
			if (proc_pid_rusage(getpid(), RUSAGE_INFO_V0, info) != 0)
			{
				throw new InvalidOperationException("proc_pid_rusage failed");
			}
			return (long)BitConverter.ToUInt64(info, FootprintOffset);
		}

		public static long HeadroomBytes()
		{
			SayWhatResolved();
			return (long)os_proc_available_memory();
		}

		/// <summary>The footprint in kB, the unit MemTrim's StatusKb reports VmRSS in; 0 when it cannot be read.</summary>
		public static long FootprintKb()
		{
			try
			{
				return FootprintBytes() >> 10;
			}
			catch
			{
				return 0;
			}
		}

		/// <summary>What the Android layer asks mallopt(M_PURGE_ALL) for. A failure is logged, not thrown: the trim goes on.</summary>
		public static void PressureRelief()
		{
			SayWhatResolved();
			try
			{
				malloc_zone_pressure_relief(IntPtr.Zero, UIntPtr.Zero);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] iOS: the allocator was not asked for its pages back: " + e.Message);
			}
		}

		// M5 ② (0.4.46): the headroom-driven trim. iOS has no swap, jetsam kills at the footprint, and the system's
		// own memory warnings grow out of the same allowance (os_proc_available_memory), so the managed side just
		// watches that number: every WatchEvery'th frame it is checked, and when it runs under the mobile.txt
		// trimIosHeadroom (2048 MB by default, 0 = watch off) and the last pressure trim is CooldownMs old, the same
		// trim runs that a load ends with (MemTrim.Now). The cooldown keeps a stubborn heap (everything live) from
		// being walked every five seconds; frames in the middle of a load are skipped.
		private const int WatchEvery = 300; // about 5 s at 60 fps
		private const long CooldownMs = 90 * 1000;
		private static int watchFrames;
		private static int headroomMb = -1; // -1 = settings not read yet; 0 = stood down (a reading failed once)
		private static long lastTrimTicks = -CooldownMs;

		/// <summary>Once a frame, from MemTrim.OnFrame (iOS only): trim on our own when the allowance runs low.</summary>
		public static void OnFrame()
		{
			if (++watchFrames < WatchEvery)
			{
				return;
			}
			watchFrames = 0;
			try
			{
				if (headroomMb < 0)
				{
					headroomMb = Settings.Get("trimIosHeadroom", 2048);
				}
				if (headroomMb <= 0 || Verse.LongEventHandler.AnyEventNowOrWaiting)
				{
					return;
				}
				long leftMb = HeadroomBytes() >> 20;
				if (leftMb >= headroomMb || Environment.TickCount64 - lastTrimTicks < CooldownMs)
				{
					return;
				}
				lastTrimTicks = Environment.TickCount64;
				MemTrim.Now("low headroom (" + leftMb + " MB of the allowance left)");
			}
			catch (Exception e)
			{
				headroomMb = 0; // the reading broke: the watch stands down, one log line instead of one every five seconds
				Console.WriteLine("[mobile] iOS: the headroom watch stood down: " + e.Message);
			}
		}

		/// <summary>For the record's snap line, next to heap and gpu: "footprint 1234 MB, headroom 5123 MB".</summary>
		public static string Snap()
		{
			try
			{
				long footprintMb = FootprintBytes() >> 20;
				try
				{
					return string.Format(CultureInfo.InvariantCulture, "footprint {0} MB, headroom {1} MB", footprintMb, HeadroomBytes() >> 20);
				}
				catch (Exception e)
				{
					return string.Format(CultureInfo.InvariantCulture, "footprint {0} MB, headroom ? ({1})", footprintMb, e.Message);
				}
			}
			catch (Exception e)
			{
				return "footprint ? (" + e.Message + ")";
			}
		}

		// Once a process, before the first reading: which of the four entry points the name "libc" really gave us.
		// 0.4.43 could not tell a symbol the platform does not export from a handle that sees too little; this line can.
		// The pressure relief is really run here, once, early: harmless, and the trim asks for the same after every load.
		private static void SayWhatResolved()
		{
			if (Interlocked.Exchange(ref probed, 1) != 0)
			{
				return;
			}
			Console.WriteLine("[mobile] iOS: libSystem through \"libc\": getpid " + Outcome(() => getpid())
				+ ", proc_pid_rusage " + Outcome(() => FootprintBytes())
				+ ", os_proc_available_memory " + Outcome(() => HeadroomBytes())
				+ ", malloc_zone_pressure_relief " + Outcome(() => malloc_zone_pressure_relief(IntPtr.Zero, UIntPtr.Zero).ToInt64()));
		}

		private static string Outcome(Func<long> call)
		{
			try
			{
				call();
				return "resolved";
			}
			catch (Exception e)
			{
				return "MISSING (" + e.GetType().Name + ": " + e.Message + ")";
			}
		}
	}
}
