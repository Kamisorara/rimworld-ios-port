using System;
using System.Collections.Generic;
using System.IO;

namespace PortHooks
{
	/// <summary>
	/// The game builds its list of mods from every folder in Mods (ModLister.RebuildModList, first run by ModLister's
	/// static constructor) and reads About/About.xml, LoadFolders.xml and About/PublishedFileId.txt of each outside its
	/// own error handling: one of them it may not read ends the constructor with an exception, and from then on no mod
	/// loads at all, not even Core. On Android that is a folder copied into the game's Mods (under Android/data) with a
	/// file manager: what another app writes there belongs to that app (a tester, 2026-10-22: a Harmony folder, and the
	/// game stayed on a black screen). The game's listing of the folders comes here instead (patch/Patch.cs rewrites the
	/// call in RebuildModList): a mod folder with one of those files unreadable is left out, logged, and kept in
	/// <see cref="LeftOut"/> for PortMobile, which tells the player at the main menu. The game's own Data is listed as it is.
	/// </summary>
	public static class ModFolders
	{
		private static readonly object gate = new object();
		private static readonly List<string> leftOut = new List<string>();

		/// <summary>The mod folders left out in this run: "folder name|what could not be read" (empty: the folder itself).</summary>
		public static string[] LeftOut
		{
			get
			{
				lock (gate)
				{
					return leftOut.ToArray();
				}
			}
		}

		/// <summary>Stands in for DirectoryInfo.GetDirectories() in ModLister.RebuildModList (PortPlatform.ReadableModFolders).</summary>
		public static DirectoryInfo[] Readable(DirectoryInfo parent)
		{
			DirectoryInfo[] all = parent.GetDirectories();
			if (string.Equals(parent.Name, "Data", StringComparison.OrdinalIgnoreCase))
			{
				return all;
			}
			var readable = new List<DirectoryInfo>(all.Length);
			foreach (DirectoryInfo folder in all)
			{
				string unreadable = UnreadableFile(folder.FullName);
				if (unreadable == null)
				{
					readable.Add(folder);
					continue;
				}
				lock (gate)
				{
					string note = folder.Name + "|" + unreadable;
					if (!leftOut.Contains(note))
					{
						leftOut.Add(note);
					}
				}
				Console.WriteLine("[port17] mod folder left out, the game may not read " + (unreadable.Length > 0 ? Path.Combine(folder.FullName, unreadable) : folder.FullName) + " (written there by another app?)");
			}
			return readable.ToArray();
		}

		/// <summary>The first of the files the game's scan reads in a mod folder that cannot be read (its path below the folder, "" for the folder itself), or null.</summary>
		private static string UnreadableFile(string folder)
		{
			string current = "";
			try
			{
				string about = null;
				foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
				{
					// The game finds these without regard to case (GenFile.ResolveCaseInsensitiveFilePath).
					string name = Path.GetFileName(entry);
					if (name.Equals("About", StringComparison.OrdinalIgnoreCase) && Directory.Exists(entry))
					{
						about = entry;
					}
					else if (name.Equals("LoadFolders.xml", StringComparison.OrdinalIgnoreCase))
					{
						current = name;
						Open(entry);
						current = "";
					}
				}
				if (about != null)
				{
					string aboutName = Path.GetFileName(about);
					current = aboutName;
					foreach (string entry in Directory.EnumerateFiles(about))
					{
						string name = Path.GetFileName(entry);
						if (name.Equals("About.xml", StringComparison.OrdinalIgnoreCase) || name.Equals("PublishedFileId.txt", StringComparison.OrdinalIgnoreCase))
						{
							current = aboutName + "/" + name;
							Open(entry);
							current = aboutName;
						}
					}
				}
				return null;
			}
			catch (UnauthorizedAccessException)
			{
				return current;
			}
			catch (IOException)
			{
				return current;
			}
		}

		private static void Open(string path)
		{
			using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
			{
			}
		}
	}
}
