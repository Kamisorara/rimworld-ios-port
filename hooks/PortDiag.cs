using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Probes of the engine's resource lookup, run on a timer when <game>/diag.txt exists (one resource folder per line,
/// such as "Textures/Things/Special/Fire"). Everything goes through reflection so that the hook does not depend on
/// the engine's assemblies.
/// </summary>
internal static class PortDiag
{
	/// <summary>
	/// With memwatch.txt next to the game (a number of seconds, 5 when empty): every so often one line with the managed
	/// heap (size, committed, large objects, collections so far), what the kernel says of the process (resident, swapped)
	/// and the engine's own summary of graphics memory. For finding out what grows while mods load.
	/// </summary>
	private static void StartMemoryWatch()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "memwatch.txt");
		if (!File.Exists(path))
		{
			return;
		}
		int seconds;
		if (!int.TryParse(File.ReadAllText(path).Trim(), out seconds) || seconds <= 0)
		{
			seconds = 5;
		}
		new Thread(() =>
		{
			MethodInfo summary = null;
			while (true)
			{
				Thread.Sleep(seconds * 1000);
				try
				{
					GCMemoryInfo info = GC.GetGCMemoryInfo();
					long large = 0, pinned = 0, gen2 = 0;
					ReadOnlySpan<GCGenerationInfo> generations = info.GenerationInfo;
					if (generations.Length > 2) gen2 = generations[2].SizeAfterBytes;
					if (generations.Length > 3) large = generations[3].SizeAfterBytes;
					if (generations.Length > 4) pinned = generations[4].SizeAfterBytes;
					string resident = "?", swapped = "?";
					foreach (string line in File.ReadLines("/proc/self/status"))
					{
						if (line.StartsWith("VmRSS:")) resident = line.Substring(6).Trim();
						else if (line.StartsWith("VmSwap:")) swapped = line.Substring(7).Trim();
					}
					Console.WriteLine("[memwatch] managed " + (GC.GetTotalMemory(false) >> 20) + " MiB (heap " + (info.HeapSizeBytes >> 20) + ", committed " + (info.TotalCommittedBytes >> 20)
						+ ", gen2 " + (gen2 >> 20) + ", large " + (large >> 20) + ", pinned " + (pinned >> 20) + "; collections " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2)
						+ "; load " + (info.MemoryLoadBytes >> 20) + " of " + (info.TotalAvailableMemoryBytes >> 20) + ", high at " + (info.HighMemoryLoadThresholdBytes >> 20) + ") resident " + resident + " swapped " + swapped);
					if (summary == null)
					{
						Assembly waylith = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Waylith");
						summary = waylith?.GetType("Waylith.Graphics.GpuMemoryReport")?.GetMethod("BuildCompactSummary", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
					}
					if (summary != null)
					{
						Console.WriteLine("[memwatch] " + ((string)summary.Invoke(null, null) ?? "").Trim().Replace("\n", "\n[memwatch] "));
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("[memwatch] " + e.GetType().Name + ": " + (e.InnerException ?? e).Message);
				}
			}
		}) { IsBackground = true, Name = "PortMemWatch" }.Start();
	}

	public static void StartIfRequested()
	{
		StartMemoryWatch();
		string path = Path.Combine(AppContext.BaseDirectory, "diag.txt");
		if (!File.Exists(path))
		{
			return;
		}
		string[] folders = File.ReadAllLines(path).Where(l => l.Trim().Length > 0 && !l.StartsWith("#")).Select(l => l.Trim()).ToArray();
		int delaySeconds = 25;
		new Thread(() =>
		{
			Thread.Sleep(delaySeconds * 1000);
			try
			{
				Probe(folders);
			}
			catch (Exception e)
			{
				Console.WriteLine("[diag] failed: " + e);
			}
		}) { IsBackground = true, Name = "PortDiag" }.Start();
	}

	private static void Probe(string[] folders)
	{
		Assembly waylith = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Waylith");
		if (waylith == null)
		{
			Console.WriteLine("[diag] Waylith not loaded");
			return;
		}
		Type files = waylith.GetType("Waylith.Utils.ResourceFiles");
		Type resources = waylith.GetType("Waylith.Resources");
		const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		MethodInfo dirExists = files.GetMethod("DirectoryExists", any);
		MethodInfo resolve = files.GetMethod("TryResolveCaseInsensitive", any);
		MethodInfo enumerate = files.GetMethod("EnumerateFiles", any);
		MethodInfo getListing = files.GetMethod("GetListing", any);
		MethodInfo virtualEntries = files.GetMethod("VirtualEntriesOf", any);
		MethodInfo enumeratePaths = resources?.GetMethod("EnumerateResourcePaths", any);
		FieldInfo rootsField = files.GetField("VirtualRoots", any);
		string dataPath = Path.Combine(AppContext.BaseDirectory, "RimWorld_Data");
		string resourcesRoot = Path.Combine(dataPath, "Resources");
		Console.WriteLine($"[diag] dataPath {dataPath}; Resources exists={dirExists.Invoke(null, new object[] { resourcesRoot })}");
		Array roots = (Array)rootsField.GetValue(null);
		Console.WriteLine($"[diag] virtual roots: {roots.Length}");
		foreach (object root in roots)
		{
			string r = (string)root.GetType().GetProperty("Root").GetValue(root);
			IDictionary entries = (IDictionary)root.GetType().GetProperty("Entries").GetValue(root);
			Console.WriteLine($"[diag]   root {r}: {entries.Count} directories");
		}
		foreach (string folder in folders)
		{
			string full = Path.Combine(resourcesRoot, folder.Replace('/', Path.DirectorySeparatorChar));
			Console.WriteLine($"[diag] --- {folder}");
			string parent = Path.GetDirectoryName(full);
			object parentListing = getListing.Invoke(null, new object[] { parent });
			if (parentListing == null)
			{
				Console.WriteLine($"[diag] parent listing of {parent} is null");
			}
			else
			{
				var exactDirs = ((IEnumerable)parentListing.GetType().GetField("ExactDirectories").GetValue(parentListing)).Cast<string>().ToList();
				var dirsByName = (IDictionary)parentListing.GetType().GetField("DirectoriesByName").GetValue(parentListing);
				string name = Path.GetFileName(full);
				Console.WriteLine($"[diag] parent has {exactDirs.Count} dirs; exact contains '{name}': {exactDirs.Contains(name)}; by name: {dirsByName.Contains(name)}; sample: {string.Join(", ", exactDirs.Take(6))}");
				object vEntries = virtualEntries.Invoke(null, new object[] { parent });
				Console.WriteLine($"[diag] virtual entries of parent: {(vEntries == null ? "null" : "present")}");
			}
			object resolved = resolve.Invoke(null, new object[] { full, false });
			Console.WriteLine($"[diag] resolve '{full}' -> {resolved ?? "null"}");
			if (resolved != null)
			{
				var list = ((IEnumerable)enumerate.Invoke(null, new object[] { resolved, true })).Cast<string>().ToList();
				Console.WriteLine($"[diag] files under it: {list.Count}: {string.Join(", ", list.Take(3).Select(Path.GetFileName))}");
			}
			object vThis = virtualEntries.Invoke(null, new object[] { full });
			Console.WriteLine($"[diag] virtual entries of the folder itself: {(vThis == null ? "null" : "present")}");
			if (enumeratePaths != null)
			{
				var paths = ((IEnumerable)enumeratePaths.Invoke(null, new object[] { folder, new[] { ".psd", ".png", ".dds", ".jpg" } })).Cast<string>().ToList();
				Console.WriteLine($"[diag] EnumerateResourcePaths: {paths.Count}: {string.Join(", ", paths.Take(3))}");
			}
		}
	}
}
