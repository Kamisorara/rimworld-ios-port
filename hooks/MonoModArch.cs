using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PortHooks
{
	/// <summary>
	/// Harmony's MonoMod takes the CPU's architecture from the kernel's AT_PLATFORM string (getauxval): "aarch64" or
	/// "arm64" is Arm64, anything else with "arm" in it 32-bit ARM, and for 32-bit ARM it has no detours at all
	/// (LinuxSystem: NotImplementedException). A tester's Huawei (Kirin 8000, Android 12, 2026-10-24) got no Harmony:
	/// every patch of the port failed ("textures: not installed", "inline guard failed to start"), BC textures went to
	/// its Mali GPU, which cannot sample them ("VMA failed to allocate image 'Perlin' ... Bc7UNormBlock"), and the game
	/// crashed. The process is 64-bit ARM whatever the kernel calls it: MonoMod is told so before anything patches.
	/// </summary>
	internal static class MonoModArch
	{
		[DllImport("libc")] private static extern nint getauxval(nint type);

		public static void Check()
		{
			if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
			{
				return;
			}
			try
			{
				Type detection = typeof(HarmonyLib.Harmony).Assembly.GetType("MonoMod.Utils.PlatformDetection", true);
				PropertyInfo property = detection.GetProperty("Architecture", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				FieldInfo arch = detection.GetField("arch", BindingFlags.Static | BindingFlags.NonPublic);
				object seen = property.GetValue(null);
				if (System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "monomod-arch-test.txt")))
				{
					// To try this on a phone whose kernel names its CPU plainly: MonoMod is first made to see 32-bit ARM.
					arch.SetValue(null, Enum.Parse(arch.FieldType, "Arm"));
					seen = property.GetValue(null);
				}
				if (seen.ToString() == "Arm64")
				{
					return;
				}
				arch.SetValue(null, Enum.Parse(arch.FieldType, "Arm64"));
				string platform = "?";
				try
				{
					nint text = getauxval(15); // AT_PLATFORM
					if (text != 0)
					{
						platform = Marshal.PtrToStringAnsi(text);
					}
				}
				catch (Exception)
				{
				}
				Console.WriteLine("[port17] MonoMod took the CPU for " + seen + " (AT_PLATFORM \"" + platform + "\"): told it is Arm64");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] MonoMod's architecture not checked: " + e.Message);
			}
		}
	}
}
