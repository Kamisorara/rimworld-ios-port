using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

/// <summary>Reads resolved assemblies into memory, so that the files can be rewritten while others still refer to them.</summary>
internal sealed class InMemoryResolver : DefaultAssemblyResolver
{
	// The base class caches only in the overload this one replaces, so both outcomes are remembered here: without that, every
	// reference re-read the assembly from disk (5 ms each; a small mod took 10 s to check) and every reference into an
	// assembly that is not there probed all the search directories again.
	private readonly Dictionary<string, AssemblyDefinition> resolved = new Dictionary<string, AssemblyDefinition>();
	private readonly HashSet<string> failed = new HashSet<string>();

	public override AssemblyDefinition Resolve(AssemblyNameReference name)
	{
		if (resolved.TryGetValue(name.FullName, out AssemblyDefinition assembly))
		{
			return assembly;
		}
		if (failed.Contains(name.FullName))
		{
			throw new AssemblyResolutionException(name);
		}
		try
		{
			assembly = Resolve(name, new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred, InMemory = true });
			resolved[name.FullName] = assembly;
			return assembly;
		}
		catch (AssemblyResolutionException)
		{
			failed.Add(name.FullName);
			throw;
		}
	}

	/// <summary>How many assemblies are held (patch.dll --mods-import reports it).</summary>
	public int Cached => resolved.Count;

	/// <summary>
	/// Lets go of every assembly read, once a mod is prepared (PrepareFolder); the next mod reads what it refers to again.
	/// Kept, each was a whole file in memory and as much of its model as the checks had touched, and the checks of each
	/// further mod touched more: a tester's import of 107 mods held 4.5 GB until it ended (2026-10-28, a vivo with 16 GB:
	/// the collector stopped the game for three seconds every ten, then the system killed it). patch.dll --mods-import
	/// over 85 Workshop mods: 4.5 GB held and 56 s kept; next to nothing and 39 s let go after each mod.
	/// </summary>
	public void Forget()
	{
		foreach (AssemblyDefinition assembly in resolved.Values)
		{
			assembly.Dispose();
		}
		resolved.Clear();
	}

	protected override void Dispose(bool disposing)
	{
		Forget();
		base.Dispose(disposing);
	}
}

/// <summary>
/// Prepares the assemblies of a mod for the phone's runtime. PC mods are built for Mono and for Ludeon's runtime fork,
/// which is lenient where the stock runtime is not; every assembly below the mod's folder (a .dll or RimUnity's
/// .rimunity: IsAssemblyFile) gets, in place,
///   - the PE machine type ARM64 when it says AMD64 (pure IL, but the runtime refuses the mark on arm64);
///   - [assembly: IgnoresAccessChecksTo("...")] for every assembly it references plus the game's own: mods are built
///     against "publicized" game assemblies and touch private members straight from IL, which Mono let pass and
///     this runtime checks;
///   - static fields that are no longer initonly: the game fills DefOf fields by reflection, which this runtime
///     refuses for a readonly field once the type is initialized (it aborts the whole DefOf binding);
///   - calls to Mono's Array.UnsafeLoad<T>/UnsafeStore<T> (Fishery, Adaptive Storage) turned into ldelem/stelem: this
///     runtime's CoreLib has no such methods;
///   - the same for assemblies embedded as resources (a mod that loads its real code from memory never shows it as a file);
///   - attributes whose type does not exist removed (Mono passes over them, this runtime throws from every attribute query);
///   - calls to a method that the game's current version has only with more optional parameters at the end, or with
///     another return type that converts implicitly to the old one, adapted the way a recompilation would;
///   - an override of a game method by its old return type, where the old type converts implicitly to the new one,
///     joined by the override the game does call, which returns the mod's result;
///   - Lunar Framework's checksum of the file (name.lfc beside name.dll) recomputed when the file changed;
///   - its debug information kept: a portable PDB embedded in it, or beside it under the name its CodeView entry
///     gives, is written again for the rewritten code (ReadForPreparing, WriteAssembly);
///   - the three things Ludeon's runtime finds for a mod where the stock one does not (its LaxTypeRefResolution,
///     LaxMemberSignatures and LaxPrivateFieldNames): a framework type referenced in an assembly that does not have
///     it (Adaptive Storage's Unsafe "in mscorlib", DefaultInterpolatedStringHandler "in 0Harmony") pointed at the one
///     that does; a call of a framework method that
///     returned nothing on Mono and returns a value here (List<T>.EnsureCapacity) followed by a pop; a framework
///     field by its Mono name (Label.label) pointed at the field by this runtime's name (m_label). v12: a type that
///     the phone's CoreLib now has as Ludeon's has it (JitHelpers) is pointed there as well.
/// A stamp file in the mod's folder records that this was done (and with which version of these rules).
///
/// One source for two places: patch.dll on the PC (host/mods-set.js stages a copy of a Workshop mod and runs
/// patch.dll --mods on it) and PortHooks.dll on the phone (a mod imported in the game, or copied into Mods by any
/// other way, is prepared before its first assembly loads). The PC's Workshop folders are never written to.
/// </summary>
internal static class ModPrep
{
	/// <summary>Bumped whenever what is done to an assembly changes; part of the stamp, so prepared mods are done again.</summary>
	public const int RulesVersion = 14;

	public const string StampName = ".port17-prepared";

	/// <summary>
	/// The CRC-32 of each assembly as it was before it was prepared, and as it is after ("path below the folder", a tab,
	/// the original's, a tab, the prepared one's). A mod that compares files between players goes by their checksums:
	/// Multiplayer (rwmt) hashes every assembly of every mod with CRC-32, and a prepared assembly differs from the PC's
	/// copy of the same mod, so a phone could not join a PC (or the other way round) with the same mods. PortMobile's
	/// MultiplayerFiles answers the original's checksum for a file that is still as prepared. Written by PrepareFolder.
	/// </summary>
	public const string OriginalsName = ".port17-original-crc";

	public sealed class Result
	{
		public int Files, Changed, Embedded, Checksums;
		/// <summary>The assemblies that could not be rewritten: path below the mod's folder, and why.</summary>
		public readonly List<KeyValuePair<string, string>> Problems = new List<KeyValuePair<string, string>>();
		/// <summary>The mod's version folders ("1.6", "v1.6"), as they are named.</summary>
		public readonly List<string> VersionFolders = new List<string>();

		/// <summary>
		/// The problems in assemblies the game may load: not those in the folder of another game version (a mod keeps
		/// its builds for 1.0 ... 1.6 side by side; the game takes the highest one not above its own version, and an old
		/// build that refers to something long gone from the game cannot be rewritten, which does not matter).
		/// LoadFolders.xml is not read: a folder that is not named like a version always counts.
		/// </summary>
		public List<string> ProblemsThatMatter(Version game)
		{
			Version best = null;
			foreach (string folder in VersionFolders)
			{
				Version v = VersionOf(folder);
				if (v != null && v <= game && (best == null || v > best))
				{
					best = v;
				}
			}
			var list = new List<string>();
			foreach (KeyValuePair<string, string> problem in Problems)
			{
				string[] parts = problem.Key.Split('/', '\\');
				// The game loads assemblies only from an Assemblies folder: a mod's build output (Phinix ships Source/bin/Debug
				// with copies of the engine's DLLs, two of which cannot be read here) is never loaded, and was reported as a
				// mod that may fail (the user, 2026-10-26: "Phinix这个mod没法编译").
				if (!parts.Take(parts.Length - 1).Any(p => p.Equals("Assemblies", StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				Version v = VersionOf(parts[0]);
				if (v == null || v == best)
				{
					list.Add(problem.Key + ": " + problem.Value);
				}
			}
			return list;
		}

		public override string ToString()
		{
			return Files + " assemblies seen, " + Changed + " rewritten"
				+ (Embedded > 0 ? ", " + Embedded + " embedded" : "")
				+ (Checksums > 0 ? ", " + Checksums + " Lunar checksums refreshed" : "")
				+ (Problems.Count > 0 ? ", " + Problems.Count + " not patched" : "");
		}
	}

	/// <summary>The game version a folder name stands for ("1.6", "v1.6"), or null.</summary>
	private static Version VersionOf(string folder)
	{
		string name = folder.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? folder.Substring(1) : folder;
		return name.Length <= 5 && name.Contains(".") && System.Version.TryParse(name, out Version v) ? v : null;
	}

	/// <summary>A resolver over the given folders (the game's assemblies and the runtime's); dispose it when done.</summary>
	public static InMemoryResolver NewResolver(IEnumerable<string> searchDirs)
	{
		var resolver = new InMemoryResolver();
		foreach (string dir in searchDirs)
		{
			if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
			{
				resolver.AddSearchDirectory(dir);
			}
		}
		return resolver;
	}

	private static IEnumerable<string> AssembliesBelow(string root)
	{
		return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(IsAssemblyFile);
	}

	/// <summary>
	/// Whether a file is an assembly that preparation takes and a stamp lists: a .dll, or a .rimunity file. RimUnity (the
	/// framework Nivarian Race brings) keeps a mod's code in Assemblies/*.rimunity, which the game leaves alone and
	/// RimUnity loads itself with Assembly.LoadFrom; Ludeon's runtime is as lenient with it as with any assembly. Until
	/// 2026-10-30 those were not prepared (Nivarian's memory gauge read 0 on the phone: its platform check found neither
	/// Windows nor Linux), and since no stamp listed them, ModPrepHost.BeforeLoad prepared the whole Nivarian folder again
	/// at every start. No other extension holds an assembly in the Workshop's 85 mods (2026-10-30).
	/// </summary>
	public static bool IsAssemblyFile(string path)
	{
		// The extension is compared here: file name patterns are case-sensitive on the phone and not on the PC.
		return path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rimunity", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// What the stamp of a prepared folder says: the rules' version on the first line, then every assembly below the
	/// folder with its size ("path below the folder", a tab, the bytes). Finding the assemblies means walking every
	/// file of the mod (textures, sounds, definitions: 50 000 files for a hundred mods), so that is done once, when the
	/// stamp is written; a start reads the list and looks at the files it names (StampHolds).
	/// </summary>
	public static string StampOf(string root)
	{
		var lines = new List<string> { "v" + RulesVersion };
		foreach (string file in AssembliesBelow(root).OrderBy(f => f, StringComparer.Ordinal))
		{
			lines.Add(Path.GetRelativePath(root, file).Replace('\\', '/') + "\t" + new FileInfo(file).Length);
		}
		return string.Join("\n", lines);
	}

	/// <summary>
	/// The assemblies a folder's stamp lists, with their sizes; null when there is no stamp, or one of other rules.
	/// (A stamp of the first kind, "v8:count:bytes" on one line, is checked the old way once and written anew.)
	/// </summary>
	public static Dictionary<string, long> ReadStamp(string root)
	{
		try
		{
			string path = Path.Combine(root, StampName);
			if (!File.Exists(path))
			{
				return null;
			}
			string[] lines = File.ReadAllText(path).Split('\n');
			string first = lines[0].Trim();
			if (lines.Length == 1 && first.StartsWith("v" + RulesVersion + ":", StringComparison.Ordinal))
			{
				int count = 0;
				long bytes = 0;
				foreach (string file in AssembliesBelow(root))
				{
					count++;
					bytes += new FileInfo(file).Length;
				}
				if (first != "v" + RulesVersion + ":" + count + ":" + bytes)
				{
					return null;
				}
				WriteStamp(root);
				lines = File.ReadAllText(path).Split('\n');
				first = lines[0].Trim();
			}
			if (first != "v" + RulesVersion)
			{
				return null;
			}
			var listed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
			for (int i = 1; i < lines.Length; i++)
			{
				int tab = lines[i].LastIndexOf('\t');
				if (tab > 0)
				{
					listed[lines[i].Substring(0, tab)] = long.Parse(lines[i].Substring(tab + 1).Trim());
				}
			}
			return listed;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// Whether the folder is as the stamp describes it: every assembly listed is there at its size, and the folders
	/// that hold them hold no other assembly. An assembly in a folder that had none when the stamp was written is not
	/// seen here; the loader's hook asks for each assembly the game loads whether the stamp lists it (hooks/ModPrepHost.cs).
	/// </summary>
	public static bool StampHolds(string root, Dictionary<string, long> listed)
	{
		try
		{
			var perFolder = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (KeyValuePair<string, long> entry in listed)
			{
				var info = new FileInfo(Path.Combine(root, entry.Key));
				if (!info.Exists || info.Length != entry.Value)
				{
					return false;
				}
				perFolder.TryGetValue(info.DirectoryName, out int count);
				perFolder[info.DirectoryName] = count + 1;
			}
			foreach (KeyValuePair<string, int> folder in perFolder)
			{
				int found = 0;
				foreach (string file in Directory.EnumerateFiles(folder.Key))
				{
					if (IsAssemblyFile(file))
					{
						found++;
					}
				}
				if (found != folder.Value)
				{
					return false;
				}
			}
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static bool IsPrepared(string root)
	{
		Dictionary<string, long> listed = ReadStamp(root);
		return listed != null && StampHolds(root, listed);
	}

	public static void WriteStamp(string root)
	{
		File.WriteAllText(Path.Combine(root, StampName), StampOf(root));
	}

	/// <summary>Prepares every assembly below the folder in place and stamps the folder.</summary>
	public static Result PrepareFolder(string root, InMemoryResolver resolver, Action<string> log)
	{
		try
		{
			return PrepareFolderCore(root, resolver, log);
		}
		finally
		{
			resolver.Forget();
		}
	}

	private static Result PrepareFolderCore(string root, InMemoryResolver resolver, Action<string> log)
	{
		var result = new Result();
		Dictionary<string, (int Original, int Prepared)> before = ReadOriginals(root);
		var originals = new Dictionary<string, (int Original, int Prepared)>(StringComparer.OrdinalIgnoreCase);
		foreach (string folder in Directory.GetDirectories(root))
		{
			if (VersionOf(Path.GetFileName(folder)) != null)
			{
				result.VersionFolders.Add(Path.GetFileName(folder));
			}
		}
		foreach (string file in AssembliesBelow(root).ToList())
		{
			result.Files++;
			string name = Path.GetFileName(file);
			try
			{
				byte[] original = File.ReadAllBytes(file);
				byte[] data = (byte[])original.Clone();
				string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
				int crcNow = Crc32(original);
				// A file prepared before (by older rules) is still the earlier output: its original stays the recorded one.
				int crcOriginal = before.TryGetValue(relativePath, out var known) && known.Prepared == crcNow ? known.Original : crcNow;
				byte[] output = null;
				if (FixMachineType(data))
				{
					output = data;
					log(name + ": marked for one processor (AMD64 or x86 only); now loadable here");
				}
				resolver.AddSearchDirectory(Path.GetDirectoryName(file));
				AssemblyDefinition assembly;
				string pdbFile = null;
				byte[] pdb = null;
				try { assembly = ReadForPreparing(data, file, resolver, out pdbFile); }
				catch { assembly = null; } // a native library, or not an image at all
				if (assembly != null)
				{
					using (assembly)
					{
						ModuleDefinition module = assembly.MainModule;
						if (module.Kind == ModuleKind.Dll && module.AssemblyReferences.Count > 0 && PrepareAssembly(assembly, resolver, name, result, log) > 0)
						{
							output = WriteAssembly(assembly, out pdb);
						}
					}
				}
				if (output != null)
				{
					// To a temporary file first: a failed write must not leave half an assembly.
					string temp = file + ".patched";
					File.WriteAllBytes(temp, output);
					File.Copy(temp, file, true);
					File.Delete(temp);
					result.Changed++;
					if (pdb != null && pdbFile != null)
					{
						WritePdb(pdbFile, pdb, log);
					}
					if (RefreshLunarChecksum(file, original, output))
					{
						result.Checksums++;
					}
					originals[relativePath] = (crcOriginal, Crc32(File.ReadAllBytes(file)));
				}
				else if (crcOriginal != crcNow)
				{
					originals[relativePath] = (crcOriginal, crcNow);
				}
			}
			catch (Exception e)
			{
				string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
				result.Problems.Add(new KeyValuePair<string, string>(relative, e.GetType().Name + ": " + e.Message));
				// (PORT17_PREP_TRACE=1: with the stack, for finding out which rule could not cope.)
				log(relative + ": not patched (" + e.GetType().Name + ": " + e.Message + ")" + (Environment.GetEnvironmentVariable("PORT17_PREP_TRACE") == "1" ? " " + e : ""));
			}
		}
		WriteOriginals(root, originals);
		WriteStamp(root);
		return result;
	}

	/// <summary>
	/// Reads an assembly for preparing (in memory, so that its file can be rewritten), with its debug information when
	/// the runtime could use it (DebugInformation; file null: only an embedded PDB). Cecil writes debug information only
	/// when it read it, and until 2026-10-30 every prepared assembly lost it: a mod's stack trace had file and line on
	/// the PC and IL offsets on the phone (HAR embeds its PDB, Adaptive Storage ships ten beside its assemblies). pdbFile
	/// is the PDB beside the file the symbols came from, or null. A Windows PDB (which the runtime reads on neither
	/// side), a missing, mismatched or broken one is left out, as before; it never keeps an assembly from being prepared.
	/// </summary>
	private static AssemblyDefinition ReadForPreparing(byte[] data, string file, IAssemblyResolver resolver, out string pdbFile)
	{
		var symbols = new DebugInformation(file);
		AssemblyDefinition assembly;
		try
		{
			assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(data), new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true, AssemblyResolver = resolver, SymbolReaderProvider = symbols, ThrowIfSymbolsAreNotMatching = false });
			if (assembly.MainModule.HasSymbols)
			{
				// Cecil puts the debug information on the instructions when it reads a body, and a PDB that fits the assembly
				// but not its code (an async method's or state machine's offset that starts no instruction) fails there, so
				// every body is read here, where a failure means reading without the symbols; preparing reads them all anyway.
				foreach (TypeDefinition type in assembly.MainModule.GetTypes())
				{
					foreach (MethodDefinition method in type.Methods)
					{
						if (method.HasBody)
						{
							_ = method.Body;
						}
					}
				}
			}
		}
		catch (Exception) when (symbols.Asked)
		{
			pdbFile = null;
			return AssemblyDefinition.ReadAssembly(new MemoryStream(data), new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true, AssemblyResolver = resolver });
		}
		pdbFile = assembly.MainModule.HasSymbols ? symbols.PdbFile : null;
		return assembly;
	}

	/// <summary>
	/// Picks an assembly's debug information while Cecil reads it (only then: a module read without symbols cannot
	/// take them afterwards) as the runtime finds it: a portable PDB embedded in the assembly, or one beside the file
	/// under the name its CodeView entry gives. That name has to be the assembly's own, which is the name the rewritten
	/// assembly gives its PDB (Cecil's PortablePdbWriter); a PDB of another name is left out.
	/// </summary>
	private sealed class DebugInformation : ISymbolReaderProvider
	{
		// The CodeView entry's minor version for a portable PDB ("PM"); a Windows PDB's is 0.
		private const short PortableCodeView = 0x504d;

		private readonly string file;

		/// <summary>Cecil asked for the debug information, which it does once the rest of the module is read: a failure from there on is taken for the symbols' (one that is not theirs comes again without them).</summary>
		public bool Asked;

		/// <summary>The PDB beside the assembly that the reader reads, or null.</summary>
		public string PdbFile;

		public DebugInformation(string file)
		{
			this.file = file;
		}

		public ISymbolReader GetSymbolReader(ModuleDefinition module, string fileName)
		{
			Asked = true;
			ImageDebugHeaderEntry codeView = null;
			foreach (ImageDebugHeaderEntry entry in module.GetDebugHeader().Entries)
			{
				if (entry.Directory.Type == ImageDebugType.EmbeddedPortablePdb)
				{
					return new EmbeddedPortablePdbReaderProvider().GetSymbolReader(module, fileName);
				}
				if (entry.Directory.Type == ImageDebugType.CodeView && entry.Directory.MinorVersion == PortableCodeView)
				{
					codeView = entry;
				}
			}
			byte[] data = codeView?.Data;
			if (file == null || data == null || data.Length <= 24 || data[0] != (byte)'R' || data[1] != (byte)'S' || data[2] != (byte)'D' || data[3] != (byte)'S')
			{
				return null;
			}
			int end = Array.IndexOf(data, (byte)0, 24);
			string path = System.Text.Encoding.UTF8.GetString(data, 24, (end < 0 ? data.Length : end) - 24);
			string name = path.Substring(path.LastIndexOfAny(new[] { '/', '\\' }) + 1);
			string pdb = Path.Combine(Path.GetDirectoryName(file), name);
			if (name != module.Assembly?.Name.Name + ".pdb" || !File.Exists(pdb))
			{
				return null;
			}
			PdbFile = pdb;
			return new PortablePdbReaderProvider().GetSymbolReader(module, new MemoryStream(File.ReadAllBytes(pdb)));
		}

		public ISymbolReader GetSymbolReader(ModuleDefinition module, Stream symbolStream)
		{
			return GetSymbolReader(module, "");
		}
	}

	/// <summary>
	/// The image of a prepared assembly, with the debug information it was read with (ReadForPreparing): embedded again,
	/// or as a PDB for beside it in pdb (the one there no longer fits the code). Without it when Cecil cannot write it.
	/// </summary>
	private static byte[] WriteAssembly(AssemblyDefinition assembly, out byte[] pdb)
	{
		pdb = null;
		ModuleDefinition module = assembly.MainModule;
		if (module.HasSymbols)
		{
			try
			{
				var image = new MemoryStream();
				var symbols = new MemoryStream();
				assembly.Write(image, module.SymbolReader is EmbeddedPortablePdbReader
					? new WriterParameters { SymbolWriterProvider = new EmbeddedPdbWriterProvider() }
					: new WriterParameters { SymbolWriterProvider = new PortablePdbWriterProvider(), SymbolStream = symbols });
				pdb = symbols.Length > 0 ? symbols.ToArray() : null;
				return image.ToArray();
			}
			catch (Exception)
			{
				// written as before, without
			}
		}
		var stream = new MemoryStream();
		assembly.Write(stream);
		return stream.ToArray();
	}

	/// <summary>Cecil's writer of an embedded PDB for an assembly written to memory: Cecil's own asks for a file name, which it does not use.</summary>
	private sealed class EmbeddedPdbWriterProvider : ISymbolWriterProvider
	{
		public ISymbolWriter GetSymbolWriter(ModuleDefinition module, string fileName)
		{
			return new EmbeddedPortablePdbWriterProvider().GetSymbolWriter(module, "memory");
		}

		public ISymbolWriter GetSymbolWriter(ModuleDefinition module, Stream symbolStream)
		{
			return GetSymbolWriter(module, "memory");
		}
	}

	/// <summary>The PDB of a rewritten assembly in place of the one it was read with; a failure costs only the line numbers.</summary>
	private static void WritePdb(string pdbFile, byte[] pdb, Action<string> log)
	{
		try
		{
			string temp = pdbFile + ".patched";
			File.WriteAllBytes(temp, pdb);
			File.Copy(temp, pdbFile, true);
			File.Delete(temp);
		}
		catch (Exception e)
		{
			log(Path.GetFileName(pdbFile) + ": not written (" + e.GetType().Name + ": " + e.Message + "); its assembly's stack traces go without line numbers");
		}
	}

	private static Dictionary<string, (int Original, int Prepared)> ReadOriginals(string root)
	{
		var map = new Dictionary<string, (int Original, int Prepared)>(StringComparer.OrdinalIgnoreCase);
		try
		{
			string path = Path.Combine(root, OriginalsName);
			if (File.Exists(path))
			{
				foreach (string line in File.ReadAllLines(path))
				{
					string[] parts = line.Split('\t');
					if (parts.Length == 3 && int.TryParse(parts[1], out int original) && int.TryParse(parts[2], out int prepared))
					{
						map[parts[0]] = (original, prepared);
					}
				}
			}
		}
		catch (Exception)
		{
		}
		return map;
	}

	private static void WriteOriginals(string root, Dictionary<string, (int Original, int Prepared)> originals)
	{
		string path = Path.Combine(root, OriginalsName);
		if (originals.Count == 0)
		{
			File.Delete(path);
			return;
		}
		File.WriteAllLines(path, originals.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "\t" + e.Value.Original + "\t" + e.Value.Prepared));
	}

	private static readonly uint[] CrcTable = MakeCrcTable();

	private static uint[] MakeCrcTable()
	{
		var table = new uint[256];
		for (uint i = 0; i < 256; i++)
		{
			uint c = i;
			for (int k = 0; k < 8; k++)
			{
				c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
			}
			table[i] = c;
		}
		return table;
	}

	/// <summary>CRC-32 (IEEE) as an int: what Multiplayer's checksum of a file (DotNetZip's Ionic.Crc.CRC32, in the game) gives.</summary>
	public static int Crc32(byte[] data)
	{
		uint crc = 0xFFFFFFFFu;
		foreach (byte b in data)
		{
			crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
		}
		return (int)~crc;
	}

	/// <summary>The edits on one assembly, the assemblies in its resources included; returns how many were made.</summary>
	private static int PrepareAssembly(AssemblyDefinition assembly, InMemoryResolver resolver, string label, Result result, Action<string> log)
	{
		ModuleDefinition module = assembly.MainModule;
		int edits = AddAccessCheckExemptions(assembly);
		// First: the rules below look types up, and a type pointed at its assembly resolves for them.
		edits += RetargetMissingFrameworkTypes(module, resolver, label, log);
		edits += AdaptFrameworkReturnTypes(module, label, log);
		edits += MatchFrameworkFieldNames(module, label, log);
		edits += MatchInheritedFields(module, label, log);
		foreach (TypeDefinition type in module.GetTypes())
		{
			foreach (FieldDefinition field in type.Fields)
			{
				if (field.IsStatic && field.IsInitOnly && !field.HasConstant)
				{
					field.IsInitOnly = false;
					edits++;
				}
			}
			foreach (MethodDefinition method in type.Methods)
			{
				if (method.HasBody)
				{
					edits += ReplaceMonoArrayHelpers(method.Body);
				}
			}
		}
		edits += DropAttributesOfMissingTypes(module, label, log);
		edits += TreatAndroidAsLinux(module, label, log);
		edits += ReadBackslashesAsSeparators(module, label, log);
		edits += AdaptChangedSignatures(module, label, log);
		edits += BridgeChangedOverrides(module, label, log);
		return edits + PrepareEmbeddedAssemblies(module, resolver, label, result, log);
	}

	/// <summary>
	/// The runtime says "Android", not "Linux": RuntimeInformation.IsOSPlatform(OSPlatform.Linux) and
	/// OperatingSystem.IsLinux() are false on the phone. The game's own checks are rewritten (patch/Patch.cs:
	/// PortPlatform); a mod's were not, and a mod that picks its files by them found none (Nice Health Tab, a tester's
	/// phone 2026-10-20: "Unable to open archive file: .../Materials/Bundles//outline", the platform's folder left
	/// empty, and its shaders, the health tab and Vehicle Framework's materials after it all failed). Here they are
	/// rewritten in the mod as well, without a reference to the port: IsOSPlatform(p) becomes p == OSPlatform.Linux,
	/// IsLinux() true.
	/// </summary>
	private static int TreatAndroidAsLinux(ModuleDefinition module, string label, Action<string> log)
	{
		int edits = 0;
		foreach (TypeDefinition type in module.GetTypes())
		{
			foreach (MethodDefinition method in type.Methods)
			{
				if (!method.HasBody)
				{
					continue;
				}
				Mono.Cecil.Cil.MethodBody body = method.Body;
				for (int i = 0; i < body.Instructions.Count; i++)
				{
					Instruction instruction = body.Instructions[i];
					if (instruction.OpCode != OpCodes.Call || !(instruction.Operand is MethodReference called) || called.DeclaringType == null)
					{
						continue;
					}
					string declaring = called.DeclaringType.FullName;
					if (declaring == "System.Runtime.InteropServices.RuntimeInformation" && called.Name == "IsOSPlatform" && called.Parameters.Count == 1 && !called.HasThis)
					{
						// Stack: the platform asked about. The call (a branch may lead to it) becomes OSPlatform.Linux, then ==.
						TypeReference platform = called.Parameters[0].ParameterType;
						var linux = new MethodReference("get_Linux", platform, platform) { HasThis = false };
						var equals = new MethodReference("op_Equality", called.ReturnType, platform) { HasThis = false };
						equals.Parameters.Add(new ParameterDefinition(platform));
						equals.Parameters.Add(new ParameterDefinition(platform));
						instruction.Operand = linux;
						WidenBranches(body);
						body.Instructions.Insert(i + 1, Instruction.Create(OpCodes.Call, equals));
						i++;
						edits++;
					}
					else if (declaring == "System.OperatingSystem" && called.Name == "IsLinux" && called.Parameters.Count == 0)
					{
						instruction.OpCode = OpCodes.Ldc_I4_1;
						instruction.Operand = null;
						edits++;
					}
				}
			}
		}
		if (edits > 0)
		{
			log(label + ": " + edits + " platform checks answer Linux for Android");
		}
		return edits;
	}

	/// <summary>
	/// Windows reads a backslash in a path as a separator, as it reads a slash; Linux and Android read it as a character
	/// of the name. A mod that writes its paths with backslashes works on the PC and finds nothing on the phone: Gloomy
	/// Furniture's jukebox lists its songs in RootDirectory + "\\Sounds\\Songs" (none on the phone, "Scanning Error"),
	/// RimHUD looks for other mods' presets in "RimHUD\\Presets", RimTalk's memory patch exports into
	/// "RimTalkMemory\\MemoryExports" (on the phone one folder of that name; StartupHook.MoveBackslashNames moves what is
	/// in it). In a method that calls System.IO, a string that is nothing but such a path (IsBackslashPath) gets slashes.
	/// A lone "\\" is left alone: it is also how escaping is written (Replace("\\", "\\\\")).
	/// </summary>
	private static int ReadBackslashesAsSeparators(ModuleDefinition module, string label, Action<string> log)
	{
		int edits = 0;
		foreach (TypeDefinition type in module.GetTypes())
		{
			foreach (MethodDefinition method in type.Methods)
			{
				if (!method.HasBody || !CallsFileSystem(method.Body))
				{
					continue;
				}
				foreach (Instruction instruction in method.Body.Instructions)
				{
					if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string text && IsBackslashPath(text))
					{
						instruction.Operand = text.Replace('\\', '/');
						edits++;
					}
				}
			}
		}
		if (edits > 0)
		{
			log(label + ": " + edits + " paths written with backslashes use slashes");
		}
		return edits;
	}

	private static readonly HashSet<string> FileSystemTypes = new HashSet<string> { "File", "Directory", "Path", "FileInfo", "DirectoryInfo", "FileSystemInfo", "FileStream", "StreamReader", "StreamWriter" };

	private static bool CallsFileSystem(Mono.Cecil.Cil.MethodBody body)
	{
		foreach (Instruction instruction in body.Instructions)
		{
			if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Newobj) && instruction.Operand is MethodReference called && called.DeclaringType != null && called.DeclaringType.Namespace == "System.IO" && FileSystemTypes.Contains(called.DeclaringType.Name))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Names joined by single backslashes, with one before the first or after the last at most ("\\Sounds\\Songs",
	/// "RimHUD\\Presets\\"): every name two characters or more of letters, digits, '_', '-', '.' and inner spaces, not
	/// starting with '.'. Regular expressions ("\\d", "\\.png", "\\bword\\b") and escapes ("\\n", "\\u00e9", "\\x41")
	/// do not look like that.
	/// </summary>
	public static bool IsBackslashPath(string text)
	{
		if (text.IndexOf('\\') < 0)
		{
			return false;
		}
		string[] names = text.Split('\\');
		int named = 0;
		for (int i = 0; i < names.Length; i++)
		{
			string name = names[i];
			if (name.Length == 0)
			{
				if (i == 0 || i == names.Length - 1)
				{
					continue;
				}
				return false;
			}
			if (name.Length < 2 || !(char.IsLetterOrDigit(name[0]) || name[0] == '_') || name[name.Length - 1] == ' ')
			{
				return false;
			}
			foreach (char c in name)
			{
				if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.' && c != ' ')
				{
					return false;
				}
			}
			if ((name[0] == 'u' || name[0] == 'U' || name[0] == 'x') && name.Length <= 9 && name.Skip(1).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
			{
				return false;
			}
			named++;
		}
		return named > 0;
	}

	/// <summary>
	/// A mod built for the game's previous version calls methods by their old signature, and the game's developers
	/// change signatures in ways that cost a mod's author nothing but a recompilation: a new optional parameter at the
	/// end (DamageInfo's constructor, GenExplosion.DoExplosion, TraverseParms.For and GenClosest.ClosestThingReachable
	/// all got one or more in 1.7), or another return type that converts implicitly to the old one (Ability.CanCast:
	/// bool became AcceptanceReport). Unrecompiled, the old call is a MissingMethodException the first time the method
	/// around it runs. Here the call is rewritten as the compiler would have written it: the new parameters' default
	/// values are pushed, the new method is called, the result is converted.
	///
	/// Only for methods of the game's own assembly, where the method by its old signature is not found and exactly one method of that name fits:
	/// the old parameters in their order among the new ones, every other new one optional with a default that can be
	/// loaded, the same return type or one with an op_Implicit to the old. The old parameters are taken to be the
	/// leading ones when that fits (new parameters are added at the end); otherwise there has to be exactly one way to
	/// line them up (GenExplosion.DoExplosion got two in the middle: a float? and an int between a GasType? and a
	/// bool), and the arguments already on the stack are set aside in locals to push the defaults between them. A
	/// changed parameter type, two ways to line up, a method that is gone: those are left as they are.
	/// </summary>
	private static int AdaptChangedSignatures(ModuleDefinition module, string label, Action<string> log)
	{
		var verdicts = new Dictionary<string, MethodDefinition>();
		var lineUps = new Dictionary<string, int[]>();
		var names = new List<string>();
		int adapted = 0;
		foreach (TypeDefinition type in module.GetTypes())
		{
			foreach (MethodDefinition method in type.Methods)
			{
				if (!method.HasBody)
				{
					continue;
				}
				Mono.Cecil.Cil.MethodBody body = method.Body;
				bool widened = false;
				for (int i = 0; i < body.Instructions.Count; i++)
				{
					Instruction instruction = body.Instructions[i];
					if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Newobj)
					{
						continue;
					}
					if (!(instruction.Operand is MethodReference called) || called is GenericInstanceMethod || called.HasGenericParameters
						|| called.DeclaringType == null || called.DeclaringType is GenericInstanceType || called.DeclaringType is ArrayType || called.DeclaringType.Scope == module)
					{
						continue;
					}
					string key = called.FullName;
					if (!verdicts.TryGetValue(key, out MethodDefinition replacement))
					{
						try
						{
							replacement = FindChangedMethod(called, out int[] found);
							lineUps[key] = found;
						}
						catch (Exception)
						{
							replacement = null;
						}
						verdicts[key] = replacement;
						if (replacement != null)
						{
							names.Add(called.DeclaringType.Name + "." + called.Name + " (" + called.Parameters.Count + " -> " + replacement.Parameters.Count + " parameters" + (replacement.ReturnType.FullName != called.ReturnType.FullName ? ", returns " + replacement.ReturnType.Name : "") + ")");
						}
					}
					if (replacement == null)
					{
						continue;
					}
					// A prefix (constrained., tail.) belongs to the call it stands before: nothing may come between them.
					if (replacement.Parameters.Count > called.Parameters.Count && i > 0 && body.Instructions[i - 1].OpCode.OpCodeType == OpCodeType.Prefix)
					{
						continue;
					}
					if (!widened)
					{
						WidenBranches(body);
						widened = true;
					}
					// source[j]: which of the old arguments the new parameter j gets, or -1 for its default. The arguments from
					// the first default on are taken off the stack into locals (last one first) and pushed again in place.
					int[] source = lineUps[key];
					int first = 0;
					while (first < source.Length && source[first] == first)
					{
						first++;
					}
					var sequence = new List<Instruction>();
					var kept = new VariableDefinition[called.Parameters.Count];
					for (int n = called.Parameters.Count - 1; n >= first; n--)
					{
						kept[n] = new VariableDefinition(module.ImportReference(called.Parameters[n].ParameterType));
						body.Variables.Add(kept[n]);
						sequence.Add(Instruction.Create(OpCodes.Stloc, kept[n]));
					}
					for (int n = first; n < source.Length; n++)
					{
						if (source[n] < 0)
						{
							PushDefault(module, body, replacement.Parameters[n], sequence);
						}
						else
						{
							sequence.Add(Instruction.Create(OpCodes.Ldloc, kept[source[n]]));
						}
					}
					sequence.Add(Instruction.Create(instruction.OpCode, module.ImportReference(replacement)));
					if (instruction.OpCode != OpCodes.Newobj && replacement.ReturnType.FullName != called.ReturnType.FullName)
					{
						sequence.Add(Instruction.Create(OpCodes.Call, module.ImportReference(ImplicitConversion(replacement.ReturnType, called.ReturnType))));
					}
					// The call's own instruction becomes the first of the sequence, so that jumps to it still arrive before
					// the pushes.
					instruction.OpCode = sequence[0].OpCode;
					instruction.Operand = sequence[0].Operand;
					for (int n = 1; n < sequence.Count; n++)
					{
						body.Instructions.Insert(i + n, sequence[n]);
					}
					i += sequence.Count - 1;
					adapted++;
				}
			}
		}
		if (adapted > 0)
		{
			log(label + ": " + adapted + " call(s) adapted to a signature the game changed: " + string.Join(", ", names.Take(6)) + (names.Count > 6 ? ", ..." : ""));
		}
		return adapted;
	}

	/// <summary>The one method that a call by an old signature can be adapted to, or null (also when the call resolves as it is).</summary>
	private static MethodDefinition FindChangedMethod(MethodReference called, out int[] lineUp)
	{
		lineUp = null;
		TypeDefinition declaring = called.DeclaringType.Resolve();
		// The game's own methods only. Another mod's assembly may be found here in the build for another game version
		// (its 1.5 folder beside its 1.6 one), where a method "missing" means nothing.
		if (declaring == null || declaring.Module.Assembly.Name.Name != "Assembly-CSharp" || called.Resolve() != null)
		{
			return null;
		}
		MethodDefinition found = null;
		for (TypeDefinition type = declaring; type != null; type = type.BaseType?.Resolve())
		{
			foreach (MethodDefinition candidate in type.Methods)
			{
				if (candidate.Name != called.Name || candidate.HasThis != called.HasThis || candidate.HasGenericParameters || candidate.Parameters.Count < called.Parameters.Count)
				{
					continue;
				}
				bool sameReturn = candidate.ReturnType.FullName == called.ReturnType.FullName;
				if (candidate.Parameters.Count == called.Parameters.Count && sameReturn)
				{
					continue; // the same signature: the call fails to resolve for some other reason
				}
				int[] source = LineUp(called, candidate);
				if (source == null || (!sameReturn && (candidate.IsConstructor || ImplicitConversion(candidate.ReturnType, called.ReturnType) == null)))
				{
					continue;
				}
				if (found != null)
				{
					lineUp = null;
					return null; // two that fit: not for this place to choose
				}
				found = candidate;
				lineUp = source;
			}
			if (found != null || called.Name == ".ctor")
			{
				break;
			}
		}
		return found;
	}

	/// <summary>
	/// For each parameter of the new method the old argument it gets (-1: its default), or null when the old parameters
	/// do not line up with the new ones: as the leading ones if that fits, else in the one way there is.
	/// </summary>
	private static int[] LineUp(MethodReference called, MethodDefinition candidate)
	{
		int m = called.Parameters.Count, n = candidate.Parameters.Count;
		var same = new bool[m, n];
		var optional = new bool[n];
		for (int j = 0; j < n; j++)
		{
			optional[j] = DefaultCanBePushed(candidate.Parameters[j]);
			for (int i = 0; i < m; i++)
			{
				same[i, j] = candidate.Parameters[j].ParameterType.FullName == called.Parameters[i].ParameterType.FullName;
			}
		}
		var source = new int[n];
		bool leading = true;
		for (int j = 0; j < n && leading; j++)
		{
			leading = j < m ? same[j, j] : optional[j];
			source[j] = j < m ? j : -1;
		}
		if (leading)
		{
			return source;
		}
		// ways[i, j]: in how many ways the old parameters from i on line up with the new ones from j on (2 stands for "more than one").
		var ways = new int[m + 1, n + 1];
		ways[m, n] = 1;
		for (int j = n - 1; j >= 0; j--)
		{
			for (int i = m; i >= 0; i--)
			{
				int count = optional[j] ? ways[i, j + 1] : 0;
				if (i < m && same[i, j])
				{
					count += ways[i + 1, j + 1];
				}
				ways[i, j] = Math.Min(count, 2);
			}
		}
		if (ways[0, 0] != 1)
		{
			return null;
		}
		for (int i = 0, j = 0; j < n; j++)
		{
			if (i < m && same[i, j] && ways[i + 1, j + 1] == 1)
			{
				source[j] = i++;
			}
			else
			{
				source[j] = -1;
			}
		}
		return source;
	}

	private static MethodDefinition ImplicitConversion(TypeReference from, TypeReference to)
	{
		foreach (TypeReference holder in new[] { from, to })
		{
			TypeDefinition type;
			try { type = holder.Resolve(); }
			catch (Exception) { type = null; }
			if (type == null)
			{
				continue;
			}
			foreach (MethodDefinition method in type.Methods)
			{
				if (method.Name == "op_Implicit" && method.IsStatic && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == from.FullName && method.ReturnType.FullName == to.FullName)
				{
					return method;
				}
			}
		}
		return null;
	}

	private static bool IsStruct(TypeReference type)
	{
		// (Enums are value types too; their defaults come as constants.)
		return type.IsValueType && !type.IsPrimitive;
	}

	private static bool DefaultCanBePushed(ParameterDefinition parameter)
	{
		TypeReference type = parameter.ParameterType;
		if (!parameter.IsOptional || type.IsByReference || type.IsPointer || type.IsGenericParameter)
		{
			return false;
		}
		object constant = parameter.HasConstant ? parameter.Constant : null;
		if (constant == null)
		{
			return true; // null for a reference, default(T) for a value
		}
		if (type is GenericInstanceType)
		{
			return false; // "int? x = 5": a constant that needs wrapping
		}
		return constant is bool || constant is char || constant is sbyte || constant is byte || constant is short || constant is ushort || constant is int || constant is uint
			|| constant is long || constant is ulong || constant is float || constant is double || constant is string;
	}

	private static void PushDefault(ModuleDefinition module, Mono.Cecil.Cil.MethodBody body, ParameterDefinition parameter, List<Instruction> into)
	{
		TypeReference type = parameter.ParameterType;
		object constant = parameter.HasConstant ? parameter.Constant : null;
		if (constant == null)
		{
			if (type.IsValueType)
			{
				// default(T): through a local of the method.
				var local = new VariableDefinition(module.ImportReference(type));
				body.Variables.Add(local);
				body.InitLocals = true;
				into.Add(Instruction.Create(OpCodes.Ldloca, local));
				into.Add(Instruction.Create(OpCodes.Initobj, local.VariableType));
				into.Add(Instruction.Create(OpCodes.Ldloc, local));
			}
			else
			{
				into.Add(Instruction.Create(OpCodes.Ldnull));
			}
			return;
		}
		switch (constant)
		{
			case bool b: into.Add(Instruction.Create(OpCodes.Ldc_I4, b ? 1 : 0)); break;
			case char c: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)c)); break;
			case sbyte v: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)v)); break;
			case byte v: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)v)); break;
			case short v: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)v)); break;
			case ushort v: into.Add(Instruction.Create(OpCodes.Ldc_I4, (int)v)); break;
			case int v: into.Add(Instruction.Create(OpCodes.Ldc_I4, v)); break;
			case uint v: into.Add(Instruction.Create(OpCodes.Ldc_I4, unchecked((int)v))); break;
			case long v: into.Add(Instruction.Create(OpCodes.Ldc_I8, v)); break;
			case ulong v: into.Add(Instruction.Create(OpCodes.Ldc_I8, unchecked((long)v))); break;
			case float v: into.Add(Instruction.Create(OpCodes.Ldc_R4, v)); break;
			case double v: into.Add(Instruction.Create(OpCodes.Ldc_R8, v)); break;
			case string v: into.Add(Instruction.Create(OpCodes.Ldstr, v)); break;
			default: throw new InvalidOperationException("a default of type " + constant.GetType().Name);
		}
	}

	private static bool IsFrameworkName(string assembly)
	{
		return assembly == "mscorlib" || assembly == "netstandard" || assembly.StartsWith("System", StringComparison.Ordinal);
	}

	private static TypeDefinition TryResolve(TypeReference type)
	{
		try
		{
			return type.Resolve();
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static AssemblyDefinition TryResolve(IAssemblyResolver resolver, string name)
	{
		try
		{
			return resolver.Resolve(new AssemblyNameReference(name, new Version(0, 0, 0, 0)));
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// Ludeon's runtime looks a referenced type up by its name when the assembly the reference names does not have it
	/// (WaylithClr.LaxTypeRefResolution). Mono's mscorlib held types that .NET keeps elsewhere: Adaptive Storage's
	/// Fishery uses System.Runtime.CompilerServices.Unsafe "from mscorlib", and the stock runtime threw a
	/// TypeLoadException whenever a storage building took an item (the building broke, and with it the drawing of
	/// everything after it on the map). A framework type that does not resolve where it is referenced is pointed at
	/// System.Runtime when that forwards it, else at System.Private.CoreLib when that defines it. Mono's JitHelpers
	/// (Adaptive Storage compares enum keys with it) is in the phone's CoreLib too since rules v12, as it is in
	/// Ludeon's (patch/Patch.cs AddLudeonMembers); a type neither has stays as it is.
	/// The same for a System type referenced in another library that turns out not to have it: mods built for 1.6
	/// against Harmony 2.3 take C# string interpolation's System.Runtime.CompilerServices.DefaultInterpolatedStringHandler
	/// "from 0Harmony" (Harmony carried it for the old framework), and the game's Harmony 3.0 does not carry it any more:
	/// Prepatcher and PurePatcher failed to construct ("Could not load type ... from assembly '0Harmony'"), and
	/// Kingfisher, which needs PurePatcher, threw from every building that was built or removed. Such a reference is
	/// changed only when its library is found and does not have the type.
	/// </summary>
	private static int RetargetMissingFrameworkTypes(ModuleDefinition module, IAssemblyResolver resolver, string label, Action<string> log)
	{
		var names = new List<string>();
		AssemblyDefinition runtime = null, coreLib = null;
		bool looked = false;
		foreach (TypeReference type in module.GetTypeReferences().ToList())
		{
			if (type.DeclaringType != null || !(type.Scope is AssemblyNameReference scope) || TryResolve(type) != null)
			{
				continue;
			}
			if (!IsFrameworkName(scope.Name))
			{
				// A System type "from" a library (0Harmony and the like): only when that library is there and lacks it.
				if (!(type.Namespace == "System" || type.Namespace.StartsWith("System.", StringComparison.Ordinal)))
				{
					continue;
				}
				AssemblyDefinition library = TryResolve(resolver, scope.Name);
				if (library == null || library.MainModule.GetType(type.FullName) != null || library.MainModule.ExportedTypes.Any(e => e.FullName == type.FullName))
				{
					continue;
				}
			}
			if (!looked)
			{
				looked = true;
				runtime = TryResolve(resolver, "System.Runtime");
				coreLib = TryResolve(resolver, "System.Private.CoreLib");
			}
			AssemblyDefinition holder = null;
			if (runtime != null && (runtime.MainModule.GetType(type.FullName) != null || runtime.MainModule.ExportedTypes.Any(e => e.FullName == type.FullName)))
			{
				holder = runtime;
			}
			else if (coreLib != null && coreLib.MainModule.GetType(type.FullName) != null)
			{
				holder = coreLib;
			}
			if (holder == null || holder.Name.Name == scope.Name)
			{
				continue;
			}
			AssemblyNameReference target = module.AssemblyReferences.FirstOrDefault(r => r.Name == holder.Name.Name);
			if (target == null)
			{
				target = new AssemblyNameReference(holder.Name.Name, holder.Name.Version) { PublicKeyToken = holder.Name.PublicKeyToken };
				module.AssemblyReferences.Add(target);
			}
			type.Scope = target;
			names.Add(type.FullName + " (" + scope.Name + " -> " + target.Name + ")");
		}
		// Nested types go with the type around them.
		foreach (TypeReference type in module.GetTypeReferences())
		{
			TypeReference outer = type;
			while (outer.DeclaringType != null)
			{
				outer = outer.DeclaringType;
			}
			if (outer != type && type.Scope != outer.Scope && outer.Scope is AssemblyNameReference)
			{
				type.Scope = outer.Scope;
			}
		}
		if (names.Count > 0)
		{
			log(label + ": " + names.Count + " framework type(s) found by name elsewhere: " + string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", ..." : ""));
		}
		return names.Count;
	}

	/// <summary>
	/// A framework method that returned nothing on Mono and returns a value on this runtime, called by its Mono
	/// signature (List<T>.EnsureCapacity(int): void on Mono, int on .NET; Fishery calls it on every insertion). Ludeon's
	/// runtime binds such a call (WaylithClr.LaxMemberSignatures); here it calls the method by its signature and drops
	/// the value. Only when exactly one method of the type has that name and those parameters.
	/// </summary>
	private static int AdaptFrameworkReturnTypes(ModuleDefinition module, string label, Action<string> log)
	{
		var verdicts = new Dictionary<string, MethodDefinition>();
		var names = new List<string>();
		int adapted = 0;
		foreach (TypeDefinition type in module.GetTypes())
		{
			foreach (MethodDefinition method in type.Methods)
			{
				if (!method.HasBody)
				{
					continue;
				}
				Mono.Cecil.Cil.MethodBody body = method.Body;
				bool widened = false;
				for (int i = 0; i < body.Instructions.Count; i++)
				{
					Instruction instruction = body.Instructions[i];
					if ((instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) || !(instruction.Operand is MethodReference called)
						|| called is GenericInstanceMethod || called.HasGenericParameters || called.ReturnType.MetadataType != MetadataType.Void
						|| called.DeclaringType == null || called.DeclaringType is ArrayType
						|| !(called.DeclaringType.GetElementType().Scope is AssemblyNameReference scope) || !IsFrameworkName(scope.Name))
					{
						continue;
					}
					// tail. before the call: a value-returning call cannot be one any more.
					if (i > 0 && body.Instructions[i - 1].OpCode.OpCodeType == OpCodeType.Prefix)
					{
						continue;
					}
					string key = called.FullName;
					if (!verdicts.TryGetValue(key, out MethodDefinition found))
					{
						found = FindValueReturningTwin(called);
						verdicts[key] = found;
						if (found != null)
						{
							names.Add(called.DeclaringType.GetElementType().Name + "." + called.Name + " (now returns " + found.ReturnType.Name + ")");
						}
					}
					TypeReference returned = found == null ? null : PrimitiveOf(module, found.ReturnType);
					if (returned == null)
					{
						continue;
					}
					if (!widened)
					{
						WidenBranches(body);
						widened = true;
					}
					var replacement = new MethodReference(called.Name, returned, called.DeclaringType)
					{
						HasThis = called.HasThis,
						ExplicitThis = called.ExplicitThis,
						CallingConvention = called.CallingConvention,
					};
					foreach (ParameterDefinition parameter in called.Parameters)
					{
						replacement.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
					}
					instruction.Operand = replacement;
					body.GetILProcessor().InsertAfter(instruction, Instruction.Create(OpCodes.Pop));
					i++;
					adapted++;
				}
			}
		}
		if (adapted > 0)
		{
			log(label + ": " + adapted + " call(s) of framework methods that return a value now: " + string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", ..." : ""));
		}
		return adapted;
	}

	/// <summary>The one method of the called one's type with its name and parameters that returns a value, when the call itself does not bind.</summary>
	private static MethodDefinition FindValueReturningTwin(MethodReference called)
	{
		try
		{
			if (called.Resolve() != null)
			{
				return null;
			}
		}
		catch (Exception)
		{
		}
		TypeDefinition type = TryResolve(called.DeclaringType);
		if (type == null)
		{
			return null;
		}
		MethodDefinition found = null;
		foreach (MethodDefinition candidate in type.Methods)
		{
			if (candidate.Name != called.Name || candidate.HasThis != called.HasThis || candidate.HasGenericParameters
				|| candidate.ReturnType.MetadataType == MetadataType.Void || candidate.Parameters.Count != called.Parameters.Count)
			{
				continue;
			}
			bool same = true;
			for (int n = 0; same && n < called.Parameters.Count; n++)
			{
				same = SameParameterType(candidate.Parameters[n].ParameterType, called.Parameters[n].ParameterType);
			}
			if (!same)
			{
				continue;
			}
			if (found != null)
			{
				return null; // two of them: not guessed
			}
			found = candidate;
		}
		return found;
	}

	private static bool SameParameterType(TypeReference defined, TypeReference referenced)
	{
		if (defined is GenericParameter a && referenced is GenericParameter b)
		{
			return a.Position == b.Position && a.Type == b.Type;
		}
		return defined.FullName == referenced.FullName;
	}

	/// <summary>The module's own reference to a primitive type (through its own core library), or null for any other type.</summary>
	private static TypeReference PrimitiveOf(ModuleDefinition module, TypeReference type)
	{
		TypeSystem types = module.TypeSystem;
		switch (type.MetadataType)
		{
			case MetadataType.Boolean: return types.Boolean;
			case MetadataType.Char: return types.Char;
			case MetadataType.SByte: return types.SByte;
			case MetadataType.Byte: return types.Byte;
			case MetadataType.Int16: return types.Int16;
			case MetadataType.UInt16: return types.UInt16;
			case MetadataType.Int32: return types.Int32;
			case MetadataType.UInt32: return types.UInt32;
			case MetadataType.Int64: return types.Int64;
			case MetadataType.UInt64: return types.UInt64;
			case MetadataType.Single: return types.Single;
			case MetadataType.Double: return types.Double;
			case MetadataType.IntPtr: return types.IntPtr;
			case MetadataType.UIntPtr: return types.UIntPtr;
			default: return null;
		}
	}

	/// <summary>
	/// A private field of a framework type used straight from IL by its Mono name (Fishery compares Label.label; .NET
	/// calls it m_label). Ludeon's runtime finds the field by the name without its "m_"/"_" prefix
	/// (WaylithClr.LaxPrivateFieldNames; hooks/StartupHook.cs does the same for Harmony's lookups by name). Here the
	/// reference is pointed at that field when exactly one non-public field of the type answers to the name and has
	/// the referenced field's type.
	/// </summary>
	private static int MatchFrameworkFieldNames(ModuleDefinition module, string label, Action<string> log)
	{
		var verdicts = new Dictionary<string, FieldDefinition>();
		var names = new List<string>();
		int matched = 0;
		foreach (TypeDefinition type in module.GetTypes())
		{
			foreach (MethodDefinition method in type.Methods)
			{
				if (!method.HasBody)
				{
					continue;
				}
				foreach (Instruction instruction in method.Body.Instructions)
				{
					if (!(instruction.Operand is FieldReference field) || field is FieldDefinition || field.DeclaringType == null
						|| !(field.DeclaringType.GetElementType().Scope is AssemblyNameReference scope) || !IsFrameworkName(scope.Name))
					{
						continue;
					}
					string key = field.FullName;
					if (!verdicts.TryGetValue(key, out FieldDefinition found))
					{
						found = FindFieldByMonoName(field);
						verdicts[key] = found;
						if (found != null)
						{
							names.Add(field.DeclaringType.GetElementType().Name + "." + field.Name + " -> " + found.Name);
						}
					}
					if (found == null)
					{
						continue;
					}
					instruction.Operand = new FieldReference(found.Name, field.FieldType, field.DeclaringType);
					matched++;
				}
			}
		}
		if (matched > 0)
		{
			log(label + ": " + matched + " use(s) of framework fields by their Mono names: " + string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", ..." : ""));
		}
		return matched;
	}

	/// <summary>
	/// A field used through a type that no longer declares it, because the game (or a library) moved it to a base class:
	/// Ludeon's runtime finds it in the base (WaylithClr.LaxInheritedFieldRefs, measured on the PC 2026-10-27: a field
	/// moved from Derived to Base read fine there, MissingFieldException on this runtime). The reference is pointed at
	/// the nearest base type that declares a field of that name and type. Non-generic types only.
	/// v13.
	/// </summary>
	private static int MatchInheritedFields(ModuleDefinition module, string label, Action<string> log)
	{
		var verdicts = new Dictionary<string, TypeDefinition>();
		var names = new List<string>();
		int matched = 0;
		foreach (TypeDefinition type in module.GetTypes())
		{
			foreach (MethodDefinition method in type.Methods)
			{
				if (!method.HasBody)
				{
					continue;
				}
				foreach (Instruction instruction in method.Body.Instructions)
				{
					if (!(instruction.Operand is FieldReference field) || field is FieldDefinition || field.DeclaringType == null
						|| field.DeclaringType is GenericInstanceType || field.DeclaringType.Module == module && field.DeclaringType.Scope == module)
					{
						continue;
					}
					string key = field.FullName;
					if (!verdicts.TryGetValue(key, out TypeDefinition owner))
					{
						owner = FindInheritedField(field);
						verdicts[key] = owner;
						if (owner != null)
						{
							names.Add(field.DeclaringType.Name + "." + field.Name + " -> " + owner.Name);
						}
					}
					if (owner == null)
					{
						continue;
					}
					instruction.Operand = new FieldReference(field.Name, field.FieldType, module.ImportReference(owner));
					matched++;
				}
			}
		}
		if (matched > 0)
		{
			log(label + ": " + matched + " use(s) of fields now in a base class: " + string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", ..." : ""));
		}
		return matched;
	}

	/// <summary>The base type that declares the field a reference names, when its own declaring type does not.</summary>
	private static TypeDefinition FindInheritedField(FieldReference field)
	{
		try
		{
			if (field.Resolve() != null)
			{
				return null;
			}
		}
		catch (Exception)
		{
		}
		TypeDefinition type = TryResolve(field.DeclaringType);
		for (int depth = 0; type != null && depth < 16; depth++)
		{
			TypeReference baseRef = type.BaseType;
			if (baseRef == null || baseRef is GenericInstanceType)
			{
				return null;
			}
			type = TryResolve(baseRef);
			if (type == null)
			{
				return null;
			}
			foreach (FieldDefinition candidate in type.Fields)
			{
				if (candidate.Name == field.Name && SameParameterType(candidate.FieldType, field.FieldType))
				{
					return type;
				}
			}
		}
		return null;
	}

	private static FieldDefinition FindFieldByMonoName(FieldReference field)
	{
		try
		{
			if (field.Resolve() != null)
			{
				return null;
			}
		}
		catch (Exception)
		{
		}
		TypeDefinition type = TryResolve(field.DeclaringType);
		if (type == null)
		{
			return null;
		}
		string wanted = WithoutFieldPrefix(field.Name);
		FieldDefinition found = null;
		foreach (FieldDefinition candidate in type.Fields)
		{
			if (candidate.IsPublic || WithoutFieldPrefix(candidate.Name) != wanted || !SameParameterType(candidate.FieldType, field.FieldType))
			{
				continue;
			}
			if (found != null)
			{
				return null;
			}
			found = candidate;
		}
		return found;
	}

	private static string WithoutFieldPrefix(string name)
	{
		if (name.StartsWith("m_", StringComparison.Ordinal))
		{
			return name.Substring(2);
		}
		return name.StartsWith("_", StringComparison.Ordinal) ? name.Substring(1) : name;
	}

	/// <summary>Short jumps reach 127 bytes; a method that gets instructions inserted gets the long forms.</summary>
	private static void WidenBranches(Mono.Cecil.Cil.MethodBody body)
	{
		foreach (Instruction instruction in body.Instructions)
		{
			switch (instruction.OpCode.Code)
			{
				case Code.Br_S: instruction.OpCode = OpCodes.Br; break;
				case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
				case Code.Brtrue_S: instruction.OpCode = OpCodes.Brtrue; break;
				case Code.Beq_S: instruction.OpCode = OpCodes.Beq; break;
				case Code.Bge_S: instruction.OpCode = OpCodes.Bge; break;
				case Code.Bgt_S: instruction.OpCode = OpCodes.Bgt; break;
				case Code.Ble_S: instruction.OpCode = OpCodes.Ble; break;
				case Code.Blt_S: instruction.OpCode = OpCodes.Blt; break;
				case Code.Bne_Un_S: instruction.OpCode = OpCodes.Bne_Un; break;
				case Code.Bge_Un_S: instruction.OpCode = OpCodes.Bge_Un; break;
				case Code.Bgt_Un_S: instruction.OpCode = OpCodes.Bgt_Un; break;
				case Code.Ble_Un_S: instruction.OpCode = OpCodes.Ble_Un; break;
				case Code.Blt_Un_S: instruction.OpCode = OpCodes.Blt_Un; break;
				case Code.Leave_S: instruction.OpCode = OpCodes.Leave; break;
			}
		}
	}

	/// <summary>
	/// The other side of a changed return type: a class of the mod overrides a method of the game by the old signature
	/// (Miho's MihochanPsycast has "override bool CanCast", and Ability.CanCast returns AcceptanceReport now). The
	/// runtime finds nothing for it to override and gives it a slot of its own: the game calls its own method and the
	/// mod's never runs (Miho's psycasts could be cast without psyfocus and past the heat limit). Unlike a call, this
	/// would not recompile either; but where the old return type converts implicitly to the new one, what the author
	/// has to write is plain: the override by the new signature, returning what the old one returns. That method is
	/// added: it calls the mod's method (virtually: a class derived from this one overrides the old signature) and
	/// converts the result. The mod's method is marked as what it already is in effect, a new virtual method.
	///
	/// Only for a virtual method of the game's own assembly, taken from the nearest base type that has a virtual method
	/// of that name and those parameters, in a class that is not generic and has no method by the new signature. (Two
	/// methods of one name that differ in the return type only: a lookup by name alone, Type.GetMethod("get_CanCast")
	/// on the mod's class, finds two now. Nothing among the mods here does that.)
	/// </summary>
	private static int BridgeChangedOverrides(ModuleDefinition module, string label, Action<string> log)
	{
		var names = new List<string>();
		foreach (TypeDefinition type in module.GetTypes().ToArray())
		{
			if (type.BaseType == null || type.HasGenericParameters || type.BaseType is GenericInstanceType)
			{
				continue;
			}
			foreach (MethodDefinition method in type.Methods.ToArray())
			{
				if (!method.IsVirtual || method.IsNewSlot || method.IsStatic || method.HasGenericParameters || method.HasOverrides || method.ReturnType.MetadataType == MetadataType.Void)
				{
					continue;
				}
				MethodDefinition changed;
				try
				{
					changed = FindChangedBase(type, method);
				}
				catch (Exception)
				{
					changed = null;
				}
				if (changed == null || changed.IsFinal || changed.ReturnType.MetadataType == MetadataType.Void)
				{
					continue;
				}
				MethodDefinition conversion = ImplicitConversion(method.ReturnType, changed.ReturnType);
				if (conversion == null || type.Methods.Any(other => SameNameAndParameters(other, method) && other.ReturnType.FullName == changed.ReturnType.FullName))
				{
					continue;
				}
				var bridge = new MethodDefinition(method.Name, (method.Attributes & ~(MethodAttributes.NewSlot | MethodAttributes.Abstract)) | MethodAttributes.HideBySig, module.ImportReference(changed.ReturnType));
				foreach (ParameterDefinition parameter in method.Parameters)
				{
					bridge.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
				}
				ILProcessor il = bridge.Body.GetILProcessor();
				il.Emit(OpCodes.Ldarg_0);
				foreach (ParameterDefinition parameter in bridge.Parameters)
				{
					il.Emit(OpCodes.Ldarg, parameter);
				}
				il.Emit(OpCodes.Callvirt, method);
				il.Emit(OpCodes.Call, module.ImportReference(conversion));
				il.Emit(OpCodes.Ret);
				type.Methods.Add(bridge);
				method.IsNewSlot = true;
				names.Add(type.Name + "." + method.Name + " (" + method.ReturnType.Name + " -> " + changed.ReturnType.Name + ")");
			}
		}
		if (names.Count > 0)
		{
			log(label + ": " + names.Count + " override(s) of a method whose return type the game changed got the override by the new one: " + string.Join(", ", names.Take(6)) + (names.Count > 6 ? ", ..." : ""));
		}
		return names.Count;
	}

	/// <summary>
	/// The game's method that the mod's override was written for, where it has another return type now; null when the
	/// override still overrides something, or when that cannot be told (a base type that is not found, or generic).
	/// </summary>
	private static MethodDefinition FindChangedBase(TypeDefinition type, MethodDefinition method)
	{
		for (TypeReference reference = type.BaseType; reference != null; )
		{
			if (reference is GenericInstanceType)
			{
				return null;
			}
			TypeDefinition t = reference.Resolve();
			if (t == null || t.HasGenericParameters)
			{
				return null;
			}
			MethodDefinition changed = null;
			foreach (MethodDefinition candidate in t.Methods)
			{
				if (!candidate.IsVirtual || candidate.HasGenericParameters || !SameNameAndParameters(candidate, method))
				{
					continue;
				}
				if (candidate.ReturnType.FullName == method.ReturnType.FullName)
				{
					return null; // it overrides this one
				}
				changed = candidate;
			}
			if (changed != null)
			{
				return t.Module.Assembly.Name.Name == "Assembly-CSharp" ? changed : null;
			}
			reference = t.BaseType;
		}
		return null;
	}

	private static bool SameNameAndParameters(MethodDefinition a, MethodDefinition b)
	{
		if (a.Name != b.Name || a.HasThis != b.HasThis || a.Parameters.Count != b.Parameters.Count)
		{
			return false;
		}
		for (int i = 0; i < a.Parameters.Count; i++)
		{
			if (a.Parameters[i].ParameterType.FullName != b.Parameters[i].ParameterType.FullName)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// A mod built against the real Unity may carry attributes the engine's stand-in for Unity does not have (Ariandel
	/// Library's LumiParticle: [UnityEngine.ImageEffectAllowedInSceneView]). Mono passes over an attribute whose type it
	/// cannot find; this runtime throws TypeLoadException from every GetCustomAttributes and IsDefined on the member that
	/// carries it, whatever attribute was asked for: Harmony's PatchAll failed in the mod, and the game's own search for
	/// [DefOf] types (GenTypes.AllTypesWithAttribute) failed for everybody, which ended the loading of all mods. Such
	/// attributes are removed. Only where the assembly the attribute should be in is found and has no such type: an
	/// attribute from an assembly that is not found here (another mod's) stays.
	/// </summary>
	private static int DropAttributesOfMissingTypes(ModuleDefinition module, string label, Action<string> log)
	{
		var missing = new Dictionary<string, bool>();
		var names = new List<string>();
		int dropped = 0;
		Action<ICustomAttributeProvider> clean = delegate(ICustomAttributeProvider provider)
		{
			if (provider == null || !provider.HasCustomAttributes)
			{
				return;
			}
			for (int i = provider.CustomAttributes.Count - 1; i >= 0; i--)
			{
				TypeReference type = provider.CustomAttributes[i].AttributeType;
				if (type == null || !(type.Scope is AssemblyNameReference scope))
				{
					continue;
				}
				string key = scope.Name + "/" + type.FullName;
				if (!missing.TryGetValue(key, out bool gone))
				{
					gone = false;
					try
					{
						// The assembly first: when that is not found, nothing is known about the type.
						if (module.AssemblyResolver.Resolve(scope) != null)
						{
							gone = type.Resolve() == null;
						}
					}
					catch (Exception)
					{
					}
					missing[key] = gone;
					if (gone)
					{
						names.Add(type.FullName);
					}
				}
				if (gone)
				{
					provider.CustomAttributes.RemoveAt(i);
					dropped++;
				}
			}
		};
		clean(module.Assembly);
		clean(module);
		foreach (TypeDefinition type in module.GetTypes())
		{
			clean(type);
			foreach (GenericParameter parameter in type.GenericParameters) clean(parameter);
			foreach (FieldDefinition field in type.Fields) clean(field);
			foreach (PropertyDefinition property in type.Properties) clean(property);
			foreach (EventDefinition evt in type.Events) clean(evt);
			foreach (MethodDefinition method in type.Methods)
			{
				clean(method);
				clean(method.MethodReturnType);
				foreach (ParameterDefinition parameter in method.Parameters) clean(parameter);
				foreach (GenericParameter parameter in method.GenericParameters) clean(parameter);
			}
		}
		if (dropped > 0)
		{
			log(label + ": " + dropped + " attribute(s) of types that do not exist removed (" + string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", ..." : "") + ")");
		}
		return dropped;
	}

	/// <summary>
	/// Some mods keep their real code as an assembly in a resource and load it with Assembly.Load(byte[]) (Brrainz's
	/// CrossPromotion.dll holds two and picks one by game version): such code never passes through the files prepared
	/// here, so its private-member access failed on the phone (the prefix on Page_ModsConfig.DoModInfo threw every frame
	/// and the mod page's detail pane stayed empty). Each embedded resource that is a managed assembly is prepared in
	/// place. Compressed or encrypted ones (Costura's *.compressed) are not recognised.
	/// </summary>
	private static int PrepareEmbeddedAssemblies(ModuleDefinition module, InMemoryResolver resolver, string label, Result result, Action<string> log)
	{
		int replaced = 0;
		for (int i = 0; i < module.Resources.Count; i++)
		{
			if (!(module.Resources[i] is EmbeddedResource resource))
			{
				continue;
			}
			byte[] data = resource.GetResourceData();
			if (data.Length < 0x80 || data[0] != (byte)'M' || data[1] != (byte)'Z')
			{
				continue;
			}
			string name = label + " :: " + resource.Name;
			bool changed = FixMachineType(data);
			try
			{
				using (AssemblyDefinition embedded = ReadForPreparing(data, null, resolver, out _))
				{
					if (embedded.MainModule.Kind == ModuleKind.Dll && embedded.MainModule.AssemblyReferences.Count > 0 && PrepareAssembly(embedded, resolver, name, result, log) > 0)
					{
						data = WriteAssembly(embedded, out _);
						changed = true;
					}
				}
			}
			catch (BadImageFormatException)
			{
				// a native library or something else that starts like one
			}
			catch (Exception e)
			{
				log(name + ": embedded assembly not patched (" + e.GetType().Name + ": " + e.Message + ")");
				continue;
			}
			if (changed)
			{
				module.Resources[i] = new EmbeddedResource(resource.Name, resource.Attributes, data);
				log(name + ": embedded assembly prepared");
				result.Embedded++;
				replaced++;
			}
		}
		return replaced;
	}

	/// <summary>
	/// Lunar Framework mods (Map Preview and its kin) check each component assembly before loading it:
	/// name.lfc = MD5(name.dll) XOR MD5(mod version). A rewritten assembly gets MD5(new) XOR the version term, which is
	/// the old checksum XOR MD5(original) XOR MD5(new).
	/// </summary>
	private static bool RefreshLunarChecksum(string file, byte[] original, byte[] output)
	{
		string checksum = Path.ChangeExtension(file, ".lfc");
		if (!File.Exists(checksum))
		{
			return false;
		}
		byte[] old = File.ReadAllBytes(checksum);
		byte[] before, after;
		using (MD5 md5 = MD5.Create())
		{
			before = md5.ComputeHash(original);
			after = md5.ComputeHash(output);
		}
		if (old.Length != before.Length)
		{
			return false;
		}
		for (int i = 0; i < old.Length; i++)
		{
			old[i] = (byte)(old[i] ^ before[i] ^ after[i]);
		}
		File.WriteAllBytes(checksum, old);
		return true;
	}

	/// <summary>
	/// Makes an assembly marked for one processor loadable here, in place; false when the bytes are not such an image.
	/// The PE machine type AMD64 becomes ARM64. An IL-only image marked for 32-bit processes only (x86: the CLR header's
	/// 32BITREQUIRED without 32BITPREFERRED) becomes AnyCPU: Expanded Prosthetics and Organ Engineering's EPIA.dll is built
	/// that way, which Mono and the PC's runtime let pass and this one refuses ("The assembly architecture is not
	/// compatible with the current process"; a tester, 2026-10-25). An AMD64 image is changed only when it is IL only
	/// too: native code (a native library, a mixed-mode assembly) runs here under no mark, and until 2026-10-30 such a
	/// file was marked ARM64 all the same.
	/// </summary>
	public static bool FixMachineType(byte[] bytes)
	{
		return FixMachineType(bytes, true);
	}

	/// <summary>Whether FixMachineType would change the image; the bytes stay as they are.</summary>
	public static bool MarkedForOneProcessor(byte[] bytes)
	{
		return FixMachineType(bytes, false);
	}

	private static bool FixMachineType(byte[] bytes, bool change)
	{
		if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') return false;
		// The offsets come from the file and may be anything: compared so that a large one cannot overflow past the checks.
		int pe = BitConverter.ToInt32(bytes, 0x3c);
		if (pe < 0 || pe > bytes.Length - 24 || bytes[pe] != (byte)'P' || bytes[pe + 1] != (byte)'E') return false;
		ushort machine = BitConverter.ToUInt16(bytes, pe + 4);
		int flags = machine == 0x8664 || machine == 0x14c ? ClrFlagsOffset(bytes, pe) : -1;
		if (flags < 0 || flags > bytes.Length - 4) return false;
		uint value = BitConverter.ToUInt32(bytes, flags);
		const uint ilOnly = 0x1, required32 = 0x2, preferred32 = 0x20000;
		if ((value & ilOnly) == 0 || (machine == 0x14c && ((value & required32) == 0 || (value & preferred32) != 0))) return false;
		if (change && machine == 0x8664)
		{
			bytes[pe + 4] = 0x64;
			bytes[pe + 5] = 0xaa;
		}
		else if (change)
		{
			BitConverter.GetBytes(value & ~required32).CopyTo(bytes, flags);
		}
		return true;
	}

	/// <summary>
	/// Where the Flags of the CLR header are in the file (the COM descriptor's RVA through the section table), from the
	/// bytes given, which need to reach only as far as the section table; -1 when there is no CLR header.
	/// </summary>
	public static int ClrFlagsOffset(byte[] bytes, int pe)
	{
		if (pe < 0 || pe > bytes.Length - 24) return -1;
		int sections = BitConverter.ToUInt16(bytes, pe + 6);
		int optionalSize = BitConverter.ToUInt16(bytes, pe + 20);
		int optional = pe + 24;
		if (optional + 2 > bytes.Length) return -1;
		ushort magic = BitConverter.ToUInt16(bytes, optional);
		int directories = optional + (magic == 0x20b ? 112 : magic == 0x10b ? 96 : -100000);
		int com = directories + 14 * 8;
		if (directories < 0 || com + 8 > optional + optionalSize || com + 8 > bytes.Length) return -1;
		uint rva = BitConverter.ToUInt32(bytes, com);
		if (rva == 0) return -1;
		int table = optional + optionalSize;
		for (int i = 0; i < sections; i++)
		{
			int s = table + i * 40;
			if (s + 40 > bytes.Length) return -1;
			uint size = BitConverter.ToUInt32(bytes, s + 8), address = BitConverter.ToUInt32(bytes, s + 12), raw = BitConverter.ToUInt32(bytes, s + 20);
			if (size == 0) size = BitConverter.ToUInt32(bytes, s + 16);
			if (rva >= address && rva < address + size)
			{
				return (int)(rva - address + raw) + 16;
			}
		}
		return -1;
	}

	/// <summary>
	/// Mono's CoreLib has internal T Array.UnsafeLoad<T>(T[], int) and void Array.UnsafeStore<T>(T[], int, T), which some
	/// performance-minded mods call straight from IL; the stack layout is that of ldelem/stelem, so the calls become those.
	/// </summary>
	private static int ReplaceMonoArrayHelpers(MethodBody body)
	{
		int replaced = 0;
		foreach (Instruction i in body.Instructions)
		{
			if (i.OpCode != OpCodes.Call || !(i.Operand is GenericInstanceMethod call) || call.DeclaringType.FullName != "System.Array" || call.GenericArguments.Count != 1)
			{
				continue;
			}
			TypeReference element = call.GenericArguments[0];
			if (call.Name == "UnsafeLoad" && call.Parameters.Count == 2)
			{
				i.OpCode = OpCodes.Ldelem_Any;
				i.Operand = element;
				replaced++;
			}
			else if (call.Name == "UnsafeStore" && call.Parameters.Count == 3)
			{
				i.OpCode = OpCodes.Stelem_Any;
				i.Operand = element;
				replaced++;
			}
		}
		return replaced;
	}

	private static readonly string[] GameAssemblies = { "Assembly-CSharp", "UnityStubs", "Waylith", "Waylith.UnityCompat", "RimWorld", "0Harmony" };

	// The framework assemblies whose private members mods reach into (Fishery reads List<T>._items): the runtime checks the
	// attribute against the defining assembly, System.Private.CoreLib and friends, not the facade the mod references.
	private static readonly string[] FrameworkInternals = { "System.Private.CoreLib", "System.Private.Xml", "System.Private.Xml.Linq", "System.Private.Uri", "System.Collections", "System.Collections.Concurrent", "System.Linq", "System.Text.RegularExpressions" };

	/// <summary>Adds [assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("name")] for the game's assemblies and everything referenced.</summary>
	private static int AddAccessCheckExemptions(AssemblyDefinition assembly)
	{
		ModuleDefinition module = assembly.MainModule;
		var wanted = new HashSet<string>(GameAssemblies.Concat(FrameworkInternals));
		foreach (AssemblyNameReference r in module.AssemblyReferences)
		{
			if (!r.Name.StartsWith("System", StringComparison.Ordinal) && r.Name != "mscorlib" && r.Name != "netstandard" && !r.Name.StartsWith("Microsoft.", StringComparison.Ordinal))
			{
				wanted.Add(r.Name);
			}
		}
		return IgnoreAccessChecksTo(assembly, wanted);
	}

	/// <summary>
	/// Adds [assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("name")] for each of the names the assembly
	/// does not have it for yet (the attribute type is declared in the assembly itself, as the runtime expects); returns
	/// how many were added. The attribute derives from the running runtime's System.Attribute, or from the one given.
	/// patch.dll gives the game's own assemblies theirs with it as well (Patch.ExemptFromAccessChecks), with the Attribute
	/// of the assembly's own core library: the PC's patch.dll and the phone's PortSetup then write the same bytes.
	/// </summary>
	internal static int IgnoreAccessChecksTo(AssemblyDefinition assembly, IEnumerable<string> names, TypeReference attributeBase = null)
	{
		ModuleDefinition module = assembly.MainModule;
		var wanted = new HashSet<string>(names);
		foreach (CustomAttribute existing in assembly.CustomAttributes)
		{
			if (existing.AttributeType.Name == "IgnoresAccessChecksToAttribute" && existing.ConstructorArguments.Count == 1)
			{
				wanted.Remove(existing.ConstructorArguments[0].Value as string ?? "");
			}
		}
		if (wanted.Count == 0)
		{
			return 0;
		}
		TypeDefinition attribute = module.GetType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute");
		MethodDefinition ctor;
		if (attribute == null)
		{
			TypeReference baseType = attributeBase ?? module.ImportReference(typeof(Attribute));
			TypeReference stringType = module.TypeSystem.String;
			attribute = new TypeDefinition("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute", TypeAttributes.NotPublic | TypeAttributes.Class | TypeAttributes.BeforeFieldInit, baseType);
			var field = new FieldDefinition("assemblyName", FieldAttributes.Private | FieldAttributes.InitOnly, stringType);
			attribute.Fields.Add(field);
			ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
			ctor.Parameters.Add(new ParameterDefinition("assemblyName", ParameterAttributes.None, stringType));
			MethodReference baseCtor = attributeBase == null
				? module.ImportReference(typeof(Attribute).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null))
				: new MethodReference(".ctor", module.TypeSystem.Void, attributeBase) { HasThis = true };
			ILProcessor il = ctor.Body.GetILProcessor();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Call, baseCtor);
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(OpCodes.Stfld, field);
			il.Emit(OpCodes.Ret);
			attribute.Methods.Add(ctor);
			var getter = new MethodDefinition("get_AssemblyName", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, stringType);
			ILProcessor gil = getter.Body.GetILProcessor();
			gil.Emit(OpCodes.Ldarg_0);
			gil.Emit(OpCodes.Ldfld, field);
			gil.Emit(OpCodes.Ret);
			attribute.Methods.Add(getter);
			var property = new PropertyDefinition("AssemblyName", PropertyAttributes.None, stringType) { GetMethod = getter };
			attribute.Properties.Add(property);
			module.Types.Add(attribute);
		}
		else
		{
			ctor = attribute.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1);
		}
		int added = 0;
		foreach (string name in wanted.OrderBy(n => n))
		{
			var custom = new CustomAttribute(ctor);
			custom.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, name));
			assembly.CustomAttributes.Add(custom);
			added++;
		}
		return added;
	}
}
