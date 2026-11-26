using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PortMobile
{
	/// <summary>
	/// iOS: what jetsam counts for this process, and what is left of its allowance. The Android layer measures
	/// through /proc/self/status and /proc/meminfo and asks bionic's mallopt(M_PURGE_ALL) for the allocator's
	/// pages back; none of that exists here (0.4.43: "memory not trimmed" after every load, and no process
	/// memory in the record's snap lines). Everything iOS-specific about memory lives in this file, which
	/// sync-mobile.py does not overwrite; the shared files (MemTrim, Recorder) carry one-line "iOS:" hooks.
	///   - proc_pid_rusage(RUSAGE_INFO_V2).ri_phys_footprint: the number the system kills by. Anonymous dirty
	///     pages, compressed pages, IOKit/GPU mappings and page tables count; clean file pages do not. The host
	///     (host/app/memory.m) reads the same pair every 10 s into port17-status.txt and counts memory warnings.
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

		private const int RUSAGE_INFO_V2 = 2;
		// struct rusage_info_v2 is 96 bytes: the uuid, then ri_phys_footprint as the eighth uint64_t.
		private const int FootprintOffset = 72;
		private const int RusageInfoSize = 96;

		/// <summary>The footprint in bytes; throws when the system will not tell (the callers decide what that means).</summary>
		public static long FootprintBytes()
		{
			byte[] info = new byte[RusageInfoSize];
			if (proc_pid_rusage(getpid(), RUSAGE_INFO_V2, info) != 0)
			{
				throw new InvalidOperationException("proc_pid_rusage failed");
			}
			return (long)BitConverter.ToUInt64(info, FootprintOffset);
		}

		public static long HeadroomBytes()
		{
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
			try
			{
				malloc_zone_pressure_relief(IntPtr.Zero, UIntPtr.Zero);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] iOS: the allocator was not asked for its pages back: " + e.Message);
			}
		}

		/// <summary>For the record's snap line, next to heap and gpu: "footprint 1234 MB, headroom 5123 MB".</summary>
		public static string Snap()
		{
			try
			{
				return string.Format(CultureInfo.InvariantCulture, "footprint {0} MB, headroom {1} MB", FootprintBytes() >> 20, HeadroomBytes() >> 20);
			}
			catch (Exception e)
			{
				return "footprint ? (" + e.Message + ")";
			}
		}
	}
}
