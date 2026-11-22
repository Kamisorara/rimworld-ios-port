using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PortMobile
{
	/// <summary>
	/// HugsLib's "publish logs" button (日志发布) looks for the log where Unity players wrote it: on Linux, which the engine
	/// says the phone is, /tmp/rimworld_log, which does not exist on Android (a tester, 2026-10-25: "FileNotFoundException:
	/// Log file not found:/tmp/rimworld_log"). The engine writes Logs/Player.log in the game's data folder
	/// (Application.consoleLogPath). HugsLib (1.6 at the latest) misses it on the PC's 1.7 as well, where it looks for
	/// Player.log beside the Logs folder. HugsLibUtility.TryGetLogFilePath now gives the engine's log when the path it
	/// works out is not there; a -logfile on the command line, which it reads first, still wins. Nothing is done when
	/// HugsLib is not loaded.
	/// </summary>
	internal static class HugsLogFile
	{
		public static void Start(Harmony harmony)
		{
			Type utility = AccessTools.TypeByName("HugsLib.Utils.HugsLibUtility");
			if (utility == null)
			{
				return;
			}
			try
			{
				MethodInfo path = AccessTools.Method(utility, "TryGetLogFilePath");
				if (path == null || path.ReturnType != typeof(string))
				{
					Console.WriteLine("[mobile] hugslib: no TryGetLogFilePath; the log publisher is left as it is");
					return;
				}
				harmony.Patch(path, postfix: new HarmonyMethod(typeof(HugsLogFile), nameof(AfterTryGetLogFilePath)));
				Console.WriteLine("[mobile] hugslib: the log publisher reads the engine's log");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] hugslib: log path not patched: " + e.Message);
			}
		}

		public static void AfterTryGetLogFilePath(ref string __result)
		{
			try
			{
				if (!string.IsNullOrEmpty(__result) && File.Exists(__result))
				{
					return;
				}
				string engine = Application.consoleLogPath;
				if (!string.IsNullOrEmpty(engine) && File.Exists(engine))
				{
					__result = engine;
				}
			}
			catch (Exception)
			{
			}
		}
	}
}
