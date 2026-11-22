using System;
using System.Diagnostics;
using System.Globalization;
using System.Buffers;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using HarmonyLib;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// After a load (the start, up to the main menu; a saved game or a new one, up to the map) the process holds a lot
	/// that the load needed and the game does not:
	///   - the collector's heap for large objects is mostly holes. Loading allocates large arrays by the hundred (the
	///     parsed XML, decoding buffers, the map's meshes being built); the game's own full collections at the end of
	///     a load free them, but large objects are never moved, so the heap stays as large as it was at its peak
	///     (measured without mods, in a 250 map with 100 colonists: 440 MiB committed for 80 MiB of live arrays).
	///     One collection that moves them gives the holes back.
	///   - every page of the asset packs that was read is still mapped: the engine maps each Assets.zip whole and
	///     reads textures and sounds straight from the mapping (720 MiB for the official content). They are clean
	///     file pages and the kernel may drop them whenever it likes, but as long as they are mapped they count as
	///     this process's memory, for the system's own bookkeeping and for the phone's watchdogs. Letting go of them
	///     costs a page fault for what is read again (the page cache usually still has it).
	///   - the allocator keeps freed pages for a while; it is asked to give them back now.
	///   - textures that were loaded and never drawn are let go (ColdTextures).
	///   - the XML of every def is still there. While the defs are parsed the game notes XML nodes in two lists of its
	///     translation key system (TKeySystem.keys and treatAsList), for working out a table that this version of the game
	///     no longer works out (it brings the table ready-made). Nothing reads the lists after that, but through them
	///     the whole document stays alive: with 106 mods 2.6 million XML objects, well over 100 MiB.
	/// The collections are left out when much of the process is in swap ("trimSwapLimit" in mobile.txt, 512 MiB): they
	/// visit every live object, and the system has to read the swapped-out ones back first. Measured on a vivo short of
	/// memory (352 mods, 2 GB of the game swapped out): the collections after a load took 11 s, twice, with the map on
	/// the screen and nothing answering; on a phone that had it all in memory, 1.8 s for the same heap.
	/// mobile.txt: trimAfterLoad=0 switches all of it off.
	/// </summary>
	internal static class MemTrim
	{
		private const int MADV_DONTNEED = 4;
		private const int M_PURGE_ALL = -104;

		[DllImport("libc")] private static extern int madvise(IntPtr address, UIntPtr length, int advice);
		[DllImport("libc")] private static extern int mallopt(int parameter, int value);

		private static bool enabled;
		private static long swapLimitKb = 512 * 1024;
		// A game was loaded and the trim has not been done yet; it is done a few frames after the load's last piece of work,
		// when what the load itself and the first frames still held has been let go ("trimFrames" in mobile.txt, 3).
		private static string loaded;
		private static int framesAfterLoad;
		private static int trimFrames = 3;
		// For finding out when the load's last large objects have gone: "trimProbe=2,5,10" in mobile.txt collects again that many
		// seconds after a load and logs the sizes.
		private static double[] probeAt = new double[0];
		private static int probeNext;
		private static readonly Stopwatch sinceLoad = new Stopwatch();

		public static void Start(Harmony harmony)
		{
			enabled = Settings.Get("trimAfterLoad", 1) != 0;
			if (!enabled)
			{
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)), postfix: new HarmonyMethod(typeof(MemTrim), nameof(AfterGameInit)));
			// The defs loaded again without a restart (another language chosen): the same leftovers as after the start.
			harmony.Patch(AccessTools.Method(typeof(PlayDataLoader), nameof(PlayDataLoader.LoadAllPlayData)), postfix: new HarmonyMethod(typeof(MemTrim), nameof(AfterPlayDataLoaded)));
			trimFrames = Settings.Get("trimFrames", 3);
			swapLimitKb = Settings.Get("trimSwapLimit", 512) * 1024L;
			string probes = Settings.Get("trimProbe", "");
			if (probes.Length > 0)
			{
				probeAt = Array.ConvertAll(probes.Split(','), p => double.Parse(p, CultureInfo.InvariantCulture));
			}
		}

		/// <summary>Postfix of Game.FinalizeInit: the map is there; the rest of the load's work is queued behind it.</summary>
		public static void AfterGameInit()
		{
			loaded = "the game was loaded";
			framesAfterLoad = 0;
		}

		/// <summary>The main menu is being set up again (a game was left): the map and the world it held are garbage now.</summary>
		public static void AfterReturnToMenu()
		{
			if (enabled)
			{
				loaded = "the return to the main menu";
				framesAfterLoad = 0;
			}
		}

		public static void AfterPlayDataLoaded()
		{
			loaded = "the game's data was loaded again";
			framesAfterLoad = 0;
		}

		/// <summary>The main menu has come up for the first time.</summary>
		public static void AtMainMenu()
		{
			if (enabled)
			{
				Now("the start");
			}
		}

		/// <summary>Once a frame (the interface's postfix).</summary>
		public static void OnFrame()
		{
			if (loaded != null && !LongEventHandler.AnyEventNowOrWaiting && ++framesAfterLoad >= trimFrames)
			{
				string after = loaded;
				loaded = null;
				Now(after);
				probeNext = 0;
				sinceLoad.Restart();
			}
			if (probeNext < probeAt.Length && sinceLoad.IsRunning && sinceLoad.Elapsed.TotalSeconds >= probeAt[probeNext])
			{
				if (Settings.Get("trimProbeDump", 0) != 0)
				{
					// What is still alive at that moment, by what holds it (logs/memdump-after-load-N.txt).
					GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
					MemDiag.Dump("after-load-" + probeAt[probeNext].ToString(CultureInfo.InvariantCulture), true);
				}
				else
				{
					Now(probeAt[probeNext].ToString(CultureInfo.InvariantCulture) + " s more");
				}
				probeNext++;
			}
		}

		/// <summary>A size line of /proc/self/status ("VmRSS:", "VmSwap:") in kB.</summary>
		private static long StatusKb(string key)
		{
			try
			{
				foreach (string line in File.ReadLines("/proc/self/status"))
				{
					if (line.StartsWith(key, StringComparison.Ordinal))
					{
						return long.Parse(line.Substring(key.Length).Replace("kB", "").Trim(), CultureInfo.InvariantCulture);
					}
				}
			}
			catch
			{
			}
			return 0;
		}

		public static void Now(string after)
		{
			try
			{
				Stopwatch watch = Stopwatch.StartNew();
				long residentBefore = StatusKb("VmRSS:");
				long swapped = StatusKb("VmSwap:");
				GCMemoryInfo before = GC.GetGCMemoryInfo();
				int xmlNodes = DropXmlLeftovers();
				int pooled = EmptyArrayPools();
				string collection;
				if (swapped < swapLimitKb)
				{
					// Twice with the finalizers in between: what a finalizer still held goes with the second one, which is the one that moves the large objects.
					GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, false);
					GC.WaitForPendingFinalizers();
					// "Aggressive": everything is compacted, the large objects too, and the collector gives back all the memory it does
					// not need now. (A forced compacting collection alone leaves it holding several hundred MiB of emptied regions
					// against the next large allocations, sized by what the load needed.)
					GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
					GCMemoryInfo collected = GC.GetGCMemoryInfo();
					collection = "the collector's heap " + (before.HeapSizeBytes >> 20) + " -> " + (collected.HeapSizeBytes >> 20) + " MiB (committed " + (before.TotalCommittedBytes >> 20) + " -> "
						+ (collected.TotalCommittedBytes >> 20) + ", large objects " + (before.GenerationInfo[3].SizeAfterBytes >> 20) + " -> " + (collected.GenerationInfo[3].SizeAfterBytes >> 20) + ") in " + watch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
				}
				else
				{
					collection = "no collection: " + (swapped >> 10) + " MiB of the process is in swap (the collector's heap " + (before.HeapSizeBytes >> 20) + " MiB, committed " + (before.TotalCommittedBytes >> 20) + ")";
				}
				mallopt(M_PURGE_ALL, 0);
				long textures = ColdTextures.AfterLoad();
				long packBytes = LetGoOfPackPages();
				long residentAfter = StatusKb("VmRSS:");
				Console.WriteLine("[mobile] after " + after + ": " + collection + ", " + pooled + " pooled arrays let go" + (xmlNodes > 0 ? ", the defs' XML released (" + xmlNodes + " nodes were held)" : "") + "; " + (packBytes >> 20) + " MiB of asset pack mappings let go"
					+ (textures > 0 ? ", " + textures + " MiB of textures that were never drawn" : "") + "; resident " + (residentBefore >> 10) + " -> " + (residentAfter >> 10) + " MiB; " + watch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms in all");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] memory not trimmed after " + after + ": " + e.Message);
			}
		}

		/// <summary>
		/// TKeySystem's two lists of XML nodes, emptied when the defs are not being loaded at this moment. How many entries they held.
		/// </summary>
		private static int DropXmlLeftovers()
		{
			try
			{
				if (LongEventHandler.AnyEventNowOrWaiting && Current.ProgramState != ProgramState.Playing && Current.ProgramState != ProgramState.Entry)
				{
					return 0;
				}
				int held = 0;
				if (AccessTools.Field(typeof(TKeySystem), "keys")?.GetValue(null) is IList keys)
				{
					held += keys.Count;
					keys.Clear();
				}
				if (AccessTools.Field(typeof(TKeySystem), "treatAsList")?.GetValue(null) is System.Collections.Generic.HashSet<System.Xml.XmlNode> lists)
				{
					held += lists.Count;
					lists.Clear();
					lists.TrimExcess();
				}
				return held;
			}
			catch
			{
				return 0;
			}
		}

		/// <summary>
		/// The class library's shared array pools keep the last array each thread gave back, one per size class, and give
		/// those up only when they have lain unused through two full collections half a minute apart. A load leaves the
		/// worker threads holding the largest buffers they ever needed (decoding textures, unpacking pack entries): what
		/// the pool itself does when memory is short is done here, for the element types the game and the engine rent.
		/// </summary>
		private static int EmptyArrayPools()
		{
			int emptied = 0;
			foreach (Type element in new[] { typeof(byte), typeof(char), typeof(int), typeof(uint), typeof(float), typeof(double), typeof(long), typeof(ulong), typeof(ushort), typeof(short), typeof(bool), typeof(object), typeof(string),
				typeof(UnityEngine.Vector2), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector4), typeof(UnityEngine.Color), typeof(UnityEngine.Color32), typeof(UnityEngine.Matrix4x4) })
			{
				try
				{
					object pool = typeof(ArrayPool<>).MakeGenericType(element).GetProperty("Shared").GetValue(null);
					FieldInfo perThread = pool.GetType().GetField("_allTlsBuckets", BindingFlags.Instance | BindingFlags.NonPublic);
					if (perThread == null || !(perThread.GetValue(pool) is IEnumerable all))
					{
						continue;
					}
					foreach (object pair in all)
					{
						if (pair.GetType().GetProperty("Key").GetValue(pair) is Array slots)
						{
							FieldInfo held = slots.GetType().GetElementType().GetField("Array");
							for (int i = 0; i < slots.Length && held != null; i++)
							{
								if (held.GetValue(slots.GetValue(i)) != null)
								{
									emptied++;
								}
							}
							Array.Clear(slots);
						}
					}
				}
				catch
				{
				}
			}
			return emptied;
		}

		/// <summary>
		/// Every read-only shared mapping of an asset pack (…/Assets.zip): its pages are dropped from this process. The
		/// mapping stays, what is touched again comes back from the page cache or the file.
		/// </summary>
		private static long LetGoOfPackPages()
		{
			long bytes = 0;
			foreach (string line in File.ReadLines("/proc/self/maps"))
			{
				// "start-end perms offset dev inode   path"
				if (line.IndexOf(" r--s ", StringComparison.Ordinal) < 0 || !line.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				int dash = line.IndexOf('-'), space = line.IndexOf(' ');
				if (dash <= 0 || space <= dash)
				{
					continue;
				}
				long start = long.Parse(line.Substring(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
				long end = long.Parse(line.Substring(dash + 1, space - dash - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
				if (madvise(new IntPtr(start), new UIntPtr((ulong)(end - start)), MADV_DONTNEED) == 0)
				{
					bytes += end - start;
				}
			}
			return bytes;
		}
	}
}
