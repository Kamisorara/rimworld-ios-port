using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// The paths inside a mod's asset bundles. The game looks a texture, sound or shader up in a mod's bundles under the
	/// mod's folder name and under its package id ("Assets/Data/&lt;folder&gt;/Materials/Map/X.shader",
	/// ContentFinder.TryFindAssetInModBundlesUncached; a folder of textures or sounds through the bundle's list of names,
	/// GetAllInFolder), and a modder builds the bundles where the Workshop puts the mod on the PC: in a folder named after
	/// its Workshop item (Ariandel Library: "assets/data/3665997350/materials/map/..."). On the phone a mod stays in the
	/// folder it was imported with, which need not be its item: a tester had 84 mods in "updated-&lt;number&gt;" folders
	/// (2026-11-03), nothing was found in Ariandel Library's bundle, and Milira Imperium's shield fell back to the
	/// engine's default shader (ShaderRegistry "not registered"), an opaque white disc with stripes where the PC draws a
	/// glowing orb. For a mod folder whose Workshop item is another number (the Workshop window's .workshop, else
	/// About/PublishedFileId.txt, as the import reads it), a name under the folder is looked up under the item, the folder
	/// the PC would have, in a bundle that has names under the item; the bundle's list of names
	/// (ModContentPack.AllAssetNamesInBundleTrie) also lists the item's names under the folder. Mod settings and
	/// everything else keep the folder's own name.
	/// </summary>
	public static class BundlePaths
	{
		private const string DataPrefix = "Assets/Data/";

		private static bool patchedStubs, patchedGame;
		private static Harmony harmony;
		// AssetBundle._reader and its Contains(name), made once (the reader is an internal type of UnityStubs).
		private static Func<object, object> readerOf;
		private static Func<object, string, bool> readerHas;
		private static MethodInfo allNames;
		private static PropertyInfo bundleName, folderName;
		private static readonly object gate = new object();
		// The folder names looked up so far (any case), each with its Workshop item where that is another number, else
		// null. Replaced as a whole, never changed once published: lookups come from any thread.
		private static volatile Dictionary<string, string> folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		[ThreadStatic] private static string lastFolder, previousFolder;
		[ThreadStatic] private static string lastItem, previousItem;
		// Per bundle: the folders its names are under (assets/data/<folder>/...), any case.
		private static readonly ConditionalWeakTable<object, HashSet<string>> heads = new ConditionalWeakTable<object, HashSet<string>>();
		// The bundles whose first lookup under the item is logged, and the lists of names given the item's names already.
		private static readonly ConditionalWeakTable<object, object> logged = new ConditionalWeakTable<object, object>();
		private static readonly ConditionalWeakTable<object, object> listed = new ConditionalWeakTable<object, object>();

		public static void Start(Harmony harmony)
		{
			BundlePaths.harmony = harmony;
			try
			{
				AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => TryPatch(args.LoadedAssembly);
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					TryPatch(assembly);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] bundle paths: left as they are: " + e.Message);
			}
		}

		private static void TryPatch(Assembly assembly)
		{
			string name = assembly.GetName().Name;
			if (name == "UnityStubs" && !patchedStubs)
			{
				patchedStubs = true;
				PatchBundle(assembly);
			}
			else if (name == "Assembly-CSharp" && !patchedGame)
			{
				patchedGame = true;
				PatchNameList(assembly);
			}
		}

		private static void PatchBundle(Assembly stubs)
		{
			try
			{
				Type bundle = stubs.GetType("UnityEngine.AssetBundle");
				FieldInfo reader = bundle?.GetField("_reader", BindingFlags.NonPublic | BindingFlags.Instance);
				MethodInfo contains = reader?.FieldType.GetMethod("Contains", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
				MethodInfo load = bundle?.GetMethod("LoadAsset", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Type) }, null);
				MethodInfo loadWithSub = bundle?.GetMethod("LoadAssetWithSubAssets", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Type) }, null);
				MethodInfo bundleContains = bundle?.GetMethod("Contains", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
				allNames = bundle?.GetMethod("GetAllAssetNames", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
				bundleName = bundle?.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
				if (contains == null || load == null || loadWithSub == null || bundleContains == null || allNames == null || allNames.ReturnType != typeof(string[]))
				{
					Console.WriteLine("[port17] bundle paths: the engine's AssetBundle is not as expected; names are looked up as they are");
					return;
				}
				readerOf = (Func<object, object>)Accessor("BundleReader", typeof(object), new[] { typeof(object) }, il =>
				{
					il.Emit(OpCodes.Ldarg_0);
					il.Emit(OpCodes.Castclass, bundle);
					il.Emit(OpCodes.Ldfld, reader);
					il.Emit(OpCodes.Ret);
				}).CreateDelegate(typeof(Func<object, object>));
				readerHas = (Func<object, string, bool>)Accessor("BundleReaderContains", typeof(bool), new[] { typeof(object), typeof(string) }, il =>
				{
					il.Emit(OpCodes.Ldarg_0);
					il.Emit(OpCodes.Castclass, reader.FieldType);
					il.Emit(OpCodes.Ldarg_1);
					il.Emit(OpCodes.Callvirt, contains);
					il.Emit(OpCodes.Ret);
				}).CreateDelegate(typeof(Func<object, string, bool>));
				var prefix = new HarmonyMethod(typeof(BundlePaths), nameof(BeforeLookup));
				harmony.Patch(load, prefix: prefix);
				harmony.Patch(loadWithSub, prefix: prefix);
				harmony.Patch(bundleContains, prefix: prefix);
			}
			catch (Exception e)
			{
				readerHas = null;
				Console.WriteLine("[port17] bundle paths: left as they are: " + e.Message);
			}
		}

		private static DynamicMethod Accessor(string name, Type returns, Type[] parameters, Action<ILGenerator> body)
		{
			var method = new DynamicMethod(name, returns, parameters, typeof(BundlePaths).Module, true);
			body(method.GetILGenerator());
			return method;
		}

		private static void PatchNameList(Assembly game)
		{
			try
			{
				Type pack = game.GetType("Verse.ModContentPack");
				folderName = pack?.GetProperty("FolderName", BindingFlags.Public | BindingFlags.Instance);
				MethodInfo trie = pack?.GetMethod("AllAssetNamesInBundleTrie", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
				if (folderName == null || trie == null || !typeof(ICollection<string>).IsAssignableFrom(trie.ReturnType))
				{
					Console.WriteLine("[port17] bundle paths: ModContentPack is not as expected; folders of textures and sounds are listed as they are");
					return;
				}
				harmony.Patch(trie, postfix: new HarmonyMethod(typeof(BundlePaths), nameof(AfterNameList)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] bundle paths: folders of textures and sounds are listed as they are: " + e.Message);
			}
		}

		/// <summary>Prefix of AssetBundle.LoadAsset(name, type), LoadAssetWithSubAssets(name, type) and Contains(name).</summary>
		public static void BeforeLookup(object __instance, ref string name)
		{
			try
			{
				if (name == null || name.Length <= DataPrefix.Length)
				{
					return;
				}
				string path = name.IndexOf('\\') >= 0 ? name.Replace('\\', '/') : name;
				int end = path.IndexOf('/', DataPrefix.Length);
				if (end < 0 || !path.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
				string item = ItemOf(path.AsSpan(DataPrefix.Length, end - DataPrefix.Length), out string folder);
				HashSet<string> under = item == null ? null : heads.GetValue(__instance, NamesUnder);
				if (under == null || !under.Contains(item))
				{
					// Nothing under the item in this bundle (one built under the package id, say): the name as it is.
					return;
				}
				string other = path.Substring(0, DataPrefix.Length) + item + path.Substring(end);
				// A bundle with names under the folder too keeps a name it has; one without has none of them.
				if (under.Contains(folder))
				{
					object reader = readerOf(__instance);
					if (reader == null || readerHas(reader, name) || !readerHas(reader, other))
					{
						return;
					}
				}
				name = other;
				lock (logged)
				{
					if (logged.TryGetValue(__instance, out object _))
					{
						return;
					}
					logged.Add(__instance, null);
				}
				Console.WriteLine("[port17] bundle paths: bundle " + bundleName?.GetValue(__instance) + " of Mods/" + folder + " is looked up under " + item + ", as the PC's folder is named (first: " + other + ")");
			}
			catch (Exception)
			{
			}
		}

		private static HashSet<string> NamesUnder(object bundle)
		{
			var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string asset in (string[])allNames.Invoke(bundle, null) ?? Array.Empty<string>())
			{
				int end = asset.Length > DataPrefix.Length ? asset.IndexOf('/', DataPrefix.Length) : -1;
				if (end > DataPrefix.Length && asset.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase))
				{
					found.Add(asset.Substring(DataPrefix.Length, end - DataPrefix.Length));
				}
			}
			return found;
		}

		/// <summary>
		/// Postfix of ModContentPack.AllAssetNamesInBundleTrie(index), which the game builds once per mod from each bundle's
		/// names and searches by the prefix "assets/data/&lt;folder&gt;/textures/&lt;path&gt;/": the names under the folder's
		/// item are added once more under the folder, and loading one of them comes to <see cref="BeforeLookup"/>.
		/// </summary>
		public static void AfterNameList(object __instance, object __result)
		{
			// Only with the bundle's lookups patched, which take these names back to the item's.
			if (!(__result is ICollection<string> names) || readerHas == null)
			{
				return;
			}
			try
			{
				lock (listed)
				{
					if (listed.TryGetValue(__result, out object _))
					{
						return;
					}
					listed.Add(__result, null);
				}
				string folder = folderName.GetValue(__instance) as string;
				string item = folder == null ? null : ItemOf(folder.AsSpan(), out string _);
				if (item == null)
				{
					return;
				}
				// Lower case as the game makes its prefix (ToLower), and as Unity keeps the names.
				string from = DataPrefix.ToLower() + item + "/", to = DataPrefix.ToLower() + folder.ToLower() + "/";
				List<string> under = names.Where(n => n.StartsWith(from, StringComparison.OrdinalIgnoreCase)).ToList();
				foreach (string n in under)
				{
					names.Add(to + n.Substring(from.Length));
				}
				if (under.Count > 0)
				{
					Console.WriteLine("[port17] bundle paths: " + under.Count + " names of a bundle of Mods/" + folder + " also listed under the folder");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] bundle paths: a bundle's names are listed as they are: " + e.Message);
			}
		}

		/// <summary>The Workshop item of the mod folder of this name where that is another number, else null.</summary>
		private static string ItemOf(ReadOnlySpan<char> folder, out string name)
		{
			// The game asks for one mod several times in a row, the folder and the package id by turns (each bundle and
			// extension): the last two names answer those without a new string.
			string last = lastFolder;
			if (last != null && folder.Equals(last.AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				name = last;
				return lastItem;
			}
			string before = previousFolder;
			string item;
			if (before != null && folder.Equals(before.AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				name = before;
				item = previousItem;
			}
			else
			{
				name = folder.ToString();
				item = Resolve(name);
			}
			previousFolder = last;
			previousItem = lastItem;
			lastFolder = name;
			lastItem = item;
			return item;
		}

		private static string Resolve(string name)
		{
			if (folders.TryGetValue(name, out string item))
			{
				return item;
			}
			// The first lookup of this name: a folder in Mods (each running mod with a bundle asks for its own once), or
			// none there (the game's Data, a package id, which every lookup tries too). Kept either way.
			lock (gate)
			{
				if (!folders.TryGetValue(name, out item))
				{
					string path = Path.Combine(AppContext.BaseDirectory, "Mods", name);
					item = name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Directory.Exists(path) ? OtherItem(path) : null;
					folders = new Dictionary<string, string>(folders, StringComparer.OrdinalIgnoreCase) { [name] = item };
					if (item != null)
					{
						Console.WriteLine("[port17] bundle paths: Mods/" + name + " is Workshop item " + item + "; its bundles are also searched under that number, as the PC's folder is named");
					}
				}
				return item;
			}
		}

		/// <summary>The folder's Workshop item when that is not the folder's name, else null.</summary>
		private static string OtherItem(string folder)
		{
			string item = WorkshopItem(folder);
			return item != null && !string.Equals(item, Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase) ? item : null;
		}

		/// <summary>
		/// The folder's Workshop item as the mod import takes it (ModImport.WorkshopItem): the first line of .workshop, which
		/// the Workshop window writes, else the number in About/PublishedFileId.txt (found without regard to case, as the
		/// game finds it); null for neither.
		/// </summary>
		private static string WorkshopItem(string folder)
		{
			try
			{
				string stamp = Path.Combine(folder, ".workshop");
				if (File.Exists(stamp) && ulong.TryParse(File.ReadLines(stamp).FirstOrDefault(), out ulong downloaded) && downloaded != 0)
				{
					return downloaded.ToString();
				}
				string file = Path.Combine(folder, "About", "PublishedFileId.txt");
				if (!File.Exists(file))
				{
					string about = Directory.EnumerateDirectories(folder).FirstOrDefault(d => string.Equals(Path.GetFileName(d), "About", StringComparison.OrdinalIgnoreCase));
					file = about == null ? null : Directory.EnumerateFiles(about).FirstOrDefault(f => string.Equals(Path.GetFileName(f), "PublishedFileId.txt", StringComparison.OrdinalIgnoreCase));
				}
				return file != null && ulong.TryParse(File.ReadAllText(file).Trim(), out ulong item) && item != 0 ? item.ToString() : null;
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
