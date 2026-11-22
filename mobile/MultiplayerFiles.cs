using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace PortMobile
{
	/// <summary>
	/// Multiplayer (rwmt) compares the files of every mod between the players before they join, by a CRC-32 of each
	/// assembly (Multiplayer.Client.Extensions.CRC32(FileInfo)). The phone prepares mod assemblies in place
	/// (patch/ModPrep.cs), so the same mod's files differ from a PC's copy and a phone and a PC never match ("Files"
	/// in Multiplayer's mismatch window). The preparation records each assembly's checksum before and after
	/// (.port17-original-crc in the mod's folder); here, a file that is still as prepared is answered with its
	/// original's checksum. A mod prepared before that record existed keeps the prepared checksum (import it again).
	/// Nothing is done when Multiplayer is not loaded.
	/// </summary>
	internal static class MultiplayerFiles
	{
		private const string OriginalsName = ".port17-original-crc";

		// A mod folder -> its record (path below the folder -> original, prepared); null for a folder without one.
		private static readonly Dictionary<string, Dictionary<string, (int Original, int Prepared)>> records =
			new Dictionary<string, Dictionary<string, (int Original, int Prepared)>>(StringComparer.OrdinalIgnoreCase);
		private static readonly object sync = new object();
		private static int answered;

		public static void Start(Harmony harmony)
		{
			Type extensions = AccessTools.TypeByName("Multiplayer.Client.Extensions");
			MethodInfo crc = extensions == null ? null : AccessTools.Method(extensions, "CRC32", new[] { typeof(FileInfo) });
			if (crc == null)
			{
				return;
			}
			try
			{
				harmony.Patch(crc, postfix: new HarmonyMethod(typeof(MultiplayerFiles), nameof(AfterCrc32)));
				// The method that calls it, compiled while the game loaded, may hold its own copy of so small a method:
				// made again (an empty patch), it calls the patched one.
				Type joinData = AccessTools.TypeByName("Multiplayer.Client.JoinData");
				MethodInfo files = AccessTools.Method(joinData, "GetModFiles");
				if (files != null)
				{
					harmony.Patch(files, postfix: new HarmonyMethod(typeof(MultiplayerFiles), nameof(Nothing)));
				}
				// Multiplayer takes its snapshot of the mods' files while the game loads, before the port is there: again.
				MethodInfo snapshot = AccessTools.Method(joinData, "TakeModDataSnapshot");
				snapshot?.Invoke(null, null);
				Console.WriteLine("[mobile] multiplayer: prepared mod assemblies are compared by their original checksums" + (snapshot == null ? " (no snapshot to take again)" : ", snapshot taken again"));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] multiplayer: checksums left as they are: " + e.Message);
			}
		}

		public static void Nothing()
		{
		}

		public static void AfterCrc32(FileInfo file, ref int __result)
		{
			try
			{
				if (file == null || !file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
				lock (sync)
				{
					for (DirectoryInfo dir = file.Directory; dir != null; dir = dir.Parent)
					{
						Dictionary<string, (int Original, int Prepared)> record = RecordOf(dir.FullName);
						if (record == null)
						{
							if (dir.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase))
							{
								return;
							}
							continue;
						}
						string relative = Path.GetRelativePath(dir.FullName, file.FullName).Replace('\\', '/');
						if (record.TryGetValue(relative, out var entry) && entry.Prepared == __result)
						{
							__result = entry.Original;
							if (++answered == 1)
							{
								Console.WriteLine("[mobile] multiplayer: " + relative + " answered with its original checksum");
							}
						}
						return;
					}
				}
			}
			catch (Exception)
			{
			}
		}

		private static Dictionary<string, (int Original, int Prepared)> RecordOf(string folder)
		{
			if (records.TryGetValue(folder, out var cached))
			{
				return cached;
			}
			Dictionary<string, (int Original, int Prepared)> record = null;
			string path = Path.Combine(folder, OriginalsName);
			if (File.Exists(path))
			{
				record = new Dictionary<string, (int Original, int Prepared)>(StringComparer.OrdinalIgnoreCase);
				foreach (string line in File.ReadAllLines(path))
				{
					string[] parts = line.Split('\t');
					if (parts.Length == 3 && int.TryParse(parts[1], out int original) && int.TryParse(parts[2], out int prepared))
					{
						record[parts[0]] = (original, prepared);
					}
				}
			}
			records[folder] = record;
			return record;
		}
	}
}
