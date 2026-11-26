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

		// M5 ② (0.4.46, extended 0.4.47): the self-driven trim. iOS has no swap, jetsam kills at the footprint, and
		// the system's own memory warnings grow out of the same allowance (os_proc_available_memory), so the managed
		// side just watches that number: every WatchEvery'th frame both readings are taken, and a trim (the same one
		// a load ends with, MemTrim.Now) runs when either lane fires.
		//   - the floor: the allowance runs under the mobile.txt trimIosHeadroom (2048 MB by default, 0 = lane off).
		//     This is the jetsam line itself; on a 12 GB device the allowance never gets near it.
		//   - the damper: the allowance runs under trimIosDamperCeiling (4096 MB by default, 0 = lane off) AND the
		//     footprint has grown trimIosGrowthMb (1024 MB by default, 0 = lane off) since the last trim AND the last
		//     trim is at least trimIosIdleSeconds (300 s by default) old. A slower lane for the 8 GB end, whose
		//     allowance (~5-6 GB) a heavy mod set can walk down over a long session; it trims while there is still
		//     room instead of waiting for the floor. The ceiling keeps it silent on a 12 GB device (~9 GB left), so
		//     nothing new fires mid-game there.
		// The cooldown keeps a stubborn heap (everything live) from being walked every five seconds; frames in the
		// middle of a load are skipped.
		private const int WatchEvery = 300; // about 5 s at 60 fps
		private const long CooldownMs = 90 * 1000;
		private static int watchFrames;
		private static int headroomMb = -1; // -1 = settings not read yet; a lane's 0 = that lane off
		private static int growthMb = -1;
		private static int damperCeilingMb = -1;
		private static int idleSeconds = -1;
		private static long lastTrimTicks = -CooldownMs;
		private static long trimBaselineMb = -1; // the footprint the last trim left behind; -1 = not seen yet

		/// <summary>Once a frame, from MemTrim.OnFrame (iOS only): trim on our own when the allowance runs low, or when the footprint keeps growing on a small device.</summary>
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
					growthMb = Settings.Get("trimIosGrowthMb", 1024);
					damperCeilingMb = Settings.Get("trimIosDamperCeiling", 4096);
					idleSeconds = Settings.Get("trimIosIdleSeconds", 300);
				}
				if (Verse.LongEventHandler.AnyEventNowOrWaiting)
				{
					return;
				}
				long nowTicks = Environment.TickCount64;
				long leftMb = HeadroomBytes() >> 20;
				long footprintMb = FootprintBytes() >> 20;
				if (trimBaselineMb < 0 || footprintMb < trimBaselineMb)
				{
					// a start, load-end or menu trim elsewhere in MemTrim shrank it behind our back: re-baseline
					trimBaselineMb = footprintMb;
				}
				if (headroomMb > 0 && leftMb < headroomMb && nowTicks - lastTrimTicks >= CooldownMs)
				{
					Trim(nowTicks, "low headroom (" + leftMb + " MB of the allowance left)");
					return;
				}
				if (damperCeilingMb > 0 && growthMb > 0 && leftMb < damperCeilingMb
					&& footprintMb - trimBaselineMb >= growthMb
					&& nowTicks - lastTrimTicks >= idleSeconds * 1000L)
				{
					Trim(nowTicks, "footprint grew " + (footprintMb - trimBaselineMb) + " MB since the last trim (" + leftMb + " MB of the allowance left)");
				}
			}
			catch (Exception e)
			{
				headroomMb = 0; // the reading broke: the watch stands down, one log line instead of one every five seconds
				Console.WriteLine("[mobile] iOS: the headroom watch stood down: " + e.Message);
			}
		}

		private static void Trim(long nowTicks, string reason)
		{
			lastTrimTicks = nowTicks;
			trimBaselineMb = -1; // re-baselined at the next watch beat, after the trim has done its work
			MemTrim.Now(reason);
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
