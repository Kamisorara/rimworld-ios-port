using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// The engine's host information on the phone, as the Linux PC has it (the game's platform checks say Linux here,
	/// patch/Patch.cs). HostHardwareInfo.ReadCpuName (SystemInfo.processorType, crash reports) reads /proc/cpuinfo's
	/// "model name", "Hardware" or "Model", none of which an arm64 Android kernel writes: the phone said "Unknown" where
	/// a PC names its processor. Android names its chip in system properties (ro.soc.manufacturer and ro.soc.model since
	/// Android 12, ro.board.platform and ro.hardware before). ProcessMemoryReport's native heap figures come from
	/// glibc's mallinfo2, which bionic has only as a header alias of mallinfo (the same struct): the call found no such
	/// function and the report went without them; mallinfo is asked for directly instead.
	/// </summary>
	public static class HardwareInfo
	{
		private struct Mallinfo
		{
			public nuint arena, ordblks, smblks, hblks, hblkhd, usmblks, fsmblks, uordblks, fordblks, keepcost;
		}

		[DllImport("libc")] private static extern int __system_property_get(byte[] name, byte[] value);
		[DllImport("libc")] private static extern Mallinfo mallinfo();

		public static void Start(Harmony harmony)
		{
			try
			{
				MethodInfo readCpuName = Type.GetType("Waylith.Diagnostics.HostHardwareInfo, Waylith")?.GetMethod("ReadCpuName", BindingFlags.NonPublic | BindingFlags.Static);
				MethodInfo tryRead = Type.GetType("Waylith.Diagnostics.ProcessMemoryReport+LinuxMallinfo, Waylith")?.GetMethod("TryRead", BindingFlags.Public | BindingFlags.Static);
				if (readCpuName != null)
				{
					harmony.Patch(readCpuName, postfix: new HarmonyMethod(typeof(HardwareInfo), nameof(AfterReadCpuName)));
				}
				if (tryRead != null)
				{
					harmony.Patch(tryRead, prefix: new HarmonyMethod(typeof(HardwareInfo), nameof(BeforeTryRead)));
				}
				Console.WriteLine("[port17] host info: processor " + ChipName() + (readCpuName == null ? " (ReadCpuName not found)" : "") + "; native heap from bionic's mallinfo" + (tryRead == null ? " (LinuxMallinfo.TryRead not found)" : ""));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] host info: " + e.GetType().Name + ": " + e.Message);
			}
		}

		public static void AfterReadCpuName(ref string __result)
		{
			if (string.IsNullOrWhiteSpace(__result))
			{
				__result = ChipName();
			}
		}

		public static bool BeforeTryRead(ref bool __result, ref long inUse, ref long arena, ref long mmapped, ref long free)
		{
			Mallinfo m = mallinfo();
			arena = (long)m.arena;
			mmapped = (long)m.hblkhd;
			free = (long)m.fordblks;
			inUse = (long)m.uordblks + mmapped;
			__result = true;
			return false;
		}

		/// <summary>"QTI SM8750" and the like; null when the phone names none.</summary>
		private static string ChipName()
		{
			string model = Property("ro.soc.model");
			if (model != null)
			{
				string maker = Property("ro.soc.manufacturer");
				return maker != null ? maker + " " + model : model;
			}
			return Property("ro.board.platform") ?? Property("ro.hardware");
		}

		private static string Property(string name)
		{
			try
			{
				byte[] value = new byte[92];
				int length = __system_property_get(Encoding.ASCII.GetBytes(name + "\0"), value);
				string text = length > 0 ? Encoding.UTF8.GetString(value, 0, Math.Min(length, value.Length)).Trim() : "";
				return text.Length > 0 ? text : null;
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
