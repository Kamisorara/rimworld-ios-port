using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// The PC game is self-contained: one folder holds the launcher, the game's assemblies and the runtime's (System.*.dll,
	/// mscorlib, netstandard and the rest), and the game starts itself again by launching its executable. Tools that
	/// rewrite the game count on both. PurePatcher (Prepatcher's successor for 1.7: mods use it to add fields to the
	/// game's types and to replace method bodies) reads every *.dll of AppContext.BaseDirectory to resolve the game's
	/// references with Mono.Cecil, writes the rewritten Assembly-CSharp.dll into that folder, points
	/// ModAssemblyHandler.ReloadAll's Assembly.LoadFrom at its own loader (which takes the mod assemblies it rewrote from
	/// its cache) and asks the engine for a restart (RestartManager.RequestRestart, then Application.Quit). On the phone
	/// the first step failed at every start ("Failed to resolve System.Reflection.BindingFlags", a tester's Xiaomi
	/// 2026-10-29: the runtime has a folder of its own here), and the other steps would not have held either. So:
	/// - the runtime's assemblies are copied into the game folder (CopyRuntime: once per runtime; the runtime keeps
	///   loading its own copies, host.c leaves the game folder's copies of runtime names out of the trusted list);
	/// - a restart asked of the engine starts the activity again in a new process, as GenCommandLine.Restart does here
	///   (mobile/ModImport.cs), where the PC launches its executable again;
	/// - ReloadAll calls Assembly.LoadFrom itself, as on the PC (patch/Patch.cs no longer rewrites that call): the port's
	///   handling of a mod assembly comes in through a prefix on Assembly.LoadFrom (PortPlatform.BeforeLoadFrom).
	/// </summary>
	public static class SelfContained
	{
		[DllImport("glfw")] private static extern int glfwAndroidRestart();

		private static FieldInfo restartRequested;
		private static int restartAsked;

		/// <summary>
		/// Copies the runtime's assemblies into the game folder where a copy is missing or differs (size, time). A stamp in
		/// the game folder (the number of assemblies, their total size and the runtime's own CoreLib, which every runtime
		/// the port ships brings anew) lets later starts skip the comparison; the total size notices a single file the port
		/// replaces in the runtime (BinaryFormatter's implementation).
		/// </summary>
		public static void CopyRuntime()
		{
			try
			{
				string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
				string game = Path.GetFullPath(AppContext.BaseDirectory);
				if (string.IsNullOrEmpty(runtime) || string.Equals(Path.GetFullPath(runtime).TrimEnd('/'), game.TrimEnd('/'), StringComparison.Ordinal))
				{
					return;
				}
				string[] files = Directory.GetFiles(runtime, "*.dll");
				var coreLib = new FileInfo(typeof(object).Assembly.Location);
				long total = 0;
				foreach (string file in files)
				{
					total += new FileInfo(file).Length;
				}
				string stamp = files.Length + " " + total + " " + coreLib.Length + " " + coreLib.LastWriteTimeUtc.Ticks;
				string stampFile = Path.Combine(game, ".port17-runtime-copy");
				if (File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == stamp)
				{
					return;
				}
				var watch = Stopwatch.StartNew();
				int copied = 0;
				long bytes = 0;
				foreach (string source in files)
				{
					var from = new FileInfo(source);
					var to = new FileInfo(Path.Combine(game, from.Name));
					if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc)
					{
						continue;
					}
					string temp = to.FullName + ".port17-tmp";
					try
					{
						File.Copy(source, temp, true);
						try
						{
							File.SetLastWriteTimeUtc(temp, from.LastWriteTimeUtc);
						}
						catch (Exception)
						{
							// The next runtime copies it again: only the stamp says it is done.
						}
						File.Move(temp, to.FullName, true);
					}
					catch (Exception)
					{
						try
						{
							File.Delete(temp);
						}
						catch (Exception)
						{
						}
						throw;
					}
					copied++;
					bytes += from.Length;
				}
				File.WriteAllText(stampFile, stamp + "\n");
				Console.WriteLine("[port17] the runtime's " + files.Length + " assemblies are in the game folder too, as on the PC (" + copied + " copied, " + (bytes >> 20) + " MB, " + watch.ElapsedMilliseconds + " ms)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] the runtime's assemblies were not copied into the game folder: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// A restart asked of the engine: the PC launches its executable again once the run loop has ended
		/// (RestartManager.LaunchReplacementIfRequested, which here would try to start "RimWorld.exe" in the game folder:
		/// PretendProcessPath). Every engine since 1.7.4976 has it; should one not, nothing is done.
		/// </summary>
		public static void Start(Harmony harmony)
		{
			try
			{
				Type manager = Type.GetType("Waylith.RestartManager, Waylith");
				MethodInfo launch = manager?.GetMethod("LaunchReplacementIfRequested", BindingFlags.Public | BindingFlags.Static);
				restartRequested = manager?.GetField("_restartRequested", BindingFlags.NonPublic | BindingFlags.Static);
				if (launch == null || restartRequested == null || restartRequested.FieldType != typeof(bool))
				{
					Console.WriteLine("[port17] restarts asked of the engine: its RestartManager is not as expected, left as it is");
					return;
				}
				harmony.Patch(launch, prefix: new HarmonyMethod(typeof(SelfContained), nameof(BeforeLaunchReplacement)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] restarts asked of the engine left as they are: " + e.Message);
			}
		}

		/// <summary>Whether something asked the engine for a restart (RestartManager.RequestRestart) in this run.</summary>
		public static bool RestartRequested()
		{
			try
			{
				return restartRequested != null && (bool)restartRequested.GetValue(null);
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>Asks the activity to start the game again in a new process (once); false when it could not be asked.</summary>
		public static bool AskRestart(string why)
		{
			if (Interlocked.Exchange(ref restartAsked, 1) == 1)
			{
				return true;
			}
			try
			{
				Console.WriteLine("[port17] restart (" + why + "): the activity is started again in a new process");
				if (glfwAndroidRestart() != 0)
				{
					return true;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] restart through the activity failed: " + e.Message);
			}
			restartAsked = 0;
			return false;
		}

		/// <summary>
		/// Prefix of RestartManager.LaunchReplacementIfRequested, after the run loop has ended: the activity is started
		/// again and this thread waits for the activity to end the process (the PC's old process ends too). Never the
		/// engine's own launch here: an app cannot start the game's executable.
		/// </summary>
		public static bool BeforeLaunchReplacement()
		{
			if (RestartRequested() && AskRestart("asked of the engine, the game has quit"))
			{
				Thread.Sleep(15000);
			}
			return false;
		}
	}
}
