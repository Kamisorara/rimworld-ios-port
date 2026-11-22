using System;
using System.IO;
using System.Runtime;

namespace PortHooks
{
	/// <summary>
	/// The runtime's multi-core JIT (System.Runtime.ProfileOptimization): a start records which methods were compiled
	/// until the main menu (files/jit/startup.mcj), and the next start compiles those on other cores while the main
	/// thread loads the game, instead of each one when it is first called. Of a 55 s start about 5 to 8 s are spent
	/// compiling on the main thread (Entry's "of that:" line).
	///
	/// Mods are not touched: the same JIT compiles the same IL, earlier. With tiered compilation (on: the game's
	/// runtimeconfig does not turn it off) what is compiled early is the first tier, which inlines nothing, so a
	/// Harmony patch put on later reaches every caller as it does now; the inlining tier comes from call counts, while
	/// the game runs, after the mods' patches, as without this. A profile entry for an assembly that changed (a mod
	/// updated) is skipped by the runtime.
	///
	/// Measured 2026-10-21 on the OnePlus with the user's 147 mods, starts to the main menu alternated: with it
	/// 50.0 / 49.5 / 52.3 / 51.3 s, main thread compiling 4.3 / 4.2 / 4.6 / 4.4 s; without it 50.8 / 51.1 / 50.8 /
	/// 50.2 s, 5.1 / 4.2 / 4.5 / 4.2 s. No difference beyond the noise (the profile is written, 202 KB, and read);
	/// probably because much of what the main thread compiles is Harmony's replacement methods (3 025 patched
	/// methods), dynamic methods that no profile can hold, and the rest is needed before the playback reaches it. Errors,
	/// warnings and patch counts were the same in all 11 starts. So it is off; mobile.txt multicoreJit=1 turns it on
	/// (for measuring on a slower phone).
	/// </summary>
	public static class JitProfile
	{
		private const string Name = "startup.mcj";
		private static string file;
		private static bool started;

		/// <summary>From the startup hook, before the game's assemblies are loaded.</summary>
		public static void Start()
		{
			try
			{
				if (Textures.Setting("multicoreJit") != "1")
				{
					return;
				}
				string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "jit"));
				Directory.CreateDirectory(dir);
				file = Path.Combine(dir, Name);
				long had = File.Exists(file) ? new FileInfo(file).Length : -1;
				ProfileOptimization.SetProfileRoot(dir);
				ProfileOptimization.StartProfile(Name);
				started = true;
				Console.WriteLine("[port17] multi-core JIT: " + (had >= 0 ? "compiling ahead from " + Name + " (" + had / 1024 + " KB) on other cores, and recording it again" : "recording " + Name + " for the next start"));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] multi-core JIT: not started: " + e.Message);
			}
		}

		/// <summary>From PortMobile at the main menu: the recording ends and is written (the process is never ended in a way that would write it).</summary>
		public static void AtMainMenu()
		{
			if (!started)
			{
				return;
			}
			started = false;
			try
			{
				ProfileOptimization.StartProfile(null);
				Console.WriteLine("[port17] multi-core JIT: " + Name + " written" + (File.Exists(file) ? " (" + new FileInfo(file).Length / 1024 + " KB)" : ", but there is no file (this runtime may not have multi-core JIT)"));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] multi-core JIT: the profile not written: " + e.Message);
			}
		}
	}
}
