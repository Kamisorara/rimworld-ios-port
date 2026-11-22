using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace PortHooks
{
	/// <summary>
	/// What the game's platform checks are rewritten to call (patch/Patch.cs): the runtime says "Android", which the
	/// engine does not know; it is treated as Linux, the Unix platform the engine has code for.
	/// </summary>
	public static class PortPlatform
	{
		/// <summary>Environment.TickCount64 when the startup hook ran: the earliest moment of the game's process that managed code sees (for timing the start).</summary>
		public static long StartedAtTick;

		public static bool IsLinux()
		{
			return true;
		}

		public static bool IsOSPlatform(System.Runtime.InteropServices.OSPlatform platform)
		{
			return platform == System.Runtime.InteropServices.OSPlatform.Linux || platform.ToString().Equals("ANDROID", StringComparison.OrdinalIgnoreCase);
		}

		private static MethodInfo getListing, addDirectory;
		private static FieldInfo unlistableField, diskDirectoriesField;
		private static Type listingType;

		/// <summary>
		/// Stands in for ResourceFiles.GetListing inside ResolveCaseInsensitive (patch/Patch.cs rewrites that one call).
		/// The engine resolves a path case-insensitively by listing every ancestor directory; on Android an app cannot
		/// list "Android/data" (nor some directories above it), the listing comes back "unlistable" and the resolve
		/// fails for everything below, which is how whole texture folders went missing. For an unlistable directory
		/// a listing is made up from the paths this process knows it lives in. Since 1.7.4998 the engine itself tries the
		/// exact path below an unlistable directory; one with none of those paths in it is left to that.
		/// </summary>
		public static object GetListingForResolve(string directory)
		{
			if (getListing == null)
			{
				Type files = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Waylith").GetType("Waylith.Utils.ResourceFiles");
				const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
				getListing = files.GetMethod("GetListing", any);
				listingType = files.GetNestedType("DirectoryListing", BindingFlags.Public | BindingFlags.NonPublic);
				unlistableField = listingType.GetField("Unlistable", any);
				diskDirectoriesField = listingType.GetField("DiskDirectories", any);
				addDirectory = listingType.GetMethod("AddDirectory", any);
			}
			object listing = getListing.Invoke(null, new object[] { directory });
			if (listing == null || !ReferenceEquals(listing, unlistableField.GetValue(null)))
			{
				return listing;
			}
			object made = Activator.CreateInstance(listingType, true);
			var diskDirectories = (System.Collections.Generic.HashSet<string>)diskDirectoriesField.GetValue(made);
			string prefix = directory.TrimEnd('/') + "/";
			foreach (string known in new[] { AppContext.BaseDirectory, Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("TMPDIR"), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") })
			{
				if (string.IsNullOrEmpty(known) || !known.StartsWith(prefix, StringComparison.Ordinal))
				{
					continue;
				}
				string rest = known.Substring(prefix.Length);
				int slash = rest.IndexOf('/');
				string child = slash < 0 ? rest : rest.Substring(0, slash);
				if (child.Length == 0)
				{
					continue;
				}
				addDirectory.Invoke(made, new object[] { child, Path.Combine(directory, child) });
				diskDirectories.Add(child);
			}
			return diskDirectories.Count > 0 ? made : listing;
		}

		/// <summary>Console.CancelKeyPress registers a POSIX signal handler underneath, which Android's runtime does not support; there is no console to press keys in anyway.</summary>
		public static void AddCancelKeyPress(ConsoleCancelEventHandler handler)
		{
		}

		public static void RemoveCancelKeyPress(ConsoleCancelEventHandler handler)
		{
		}

		/// <summary>POSIX signal registration is not supported by the runtime on Android; the engine keeps the registrations as nullable and never needs them here.</summary>
		/// <summary>Stands in for SteamManager.Initialized inside UIRoot_Entry.Init only: without it the main menu opens a "Could not initialize Steam API" box on every start (there is no Steam client on the phone). Everything else still sees Steam as absent.</summary>
		/// <summary>Called at the start of UIRoot_Entry.Init (patch/Patch.cs inserts the call): the game is fully loaded, the phone side (PortMobile.dll next to the game) starts here.</summary>
		public static void OnEntryUIInit()
		{
			Console.WriteLine("[port17] " + modLoads + " mod assemblies asked for: checking that the mods are prepared " + Ms(ticksPrepared) + ", looking for one of the same name " + Ms(ticksNames) + ", checking the architecture mark " + Ms(ticksArchitecture) + ", loading " + Ms(ticksLoad) + "; the stack check of " + stackChecks + " patched methods " + Ms(ticksStackChecks));
			try
			{
				ModPrepHost.Release();
			}
			catch (Exception)
			{
				// Mono.Cecil.dll is not there; said already when the first mod loaded
			}
			try
			{
				string path = Path.Combine(AppContext.BaseDirectory, "PortMobile.dll");
				if (!File.Exists(path))
				{
					Console.WriteLine("[port17] no PortMobile.dll next to the game; running without the phone layer");
					return;
				}
				Assembly mobile = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
				mobile.GetType("PortMobile.Entry").GetMethod("Start").Invoke(null, null);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] the phone layer failed to start: " + e);
			}
		}

		/// <summary>Stands in for DirectoryInfo.GetDirectories inside ModLister.RebuildModList: the mod folders the game may read (ModFolders).</summary>
		public static DirectoryInfo[] ReadableModFolders(DirectoryInfo parent)
		{
			return ModFolders.Readable(parent);
		}

		/// <summary>
		/// Prefix of Assembly.LoadFrom(string), installed by the startup hook: what ModAssemblyHandler.ReloadAll loads (and
		/// whatever else is loaded by file: a mod's own plugins, PurePatcher's loader in a game it rewrote, SelfContained)
		/// goes through LoadModAssembly; that method's own loads go to the runtime. Until 2026-10-29 patch/Patch.cs
		/// rewrote ReloadAll's call instead, and PurePatcher found no Assembly.LoadFrom there to point at its loader.
		/// </summary>
		public static bool BeforeLoadFrom(string assemblyFile, ref Assembly __result)
		{
			if (portLoad > 0 || string.IsNullOrEmpty(assemblyFile))
			{
				return true;
			}
			__result = LoadModAssembly(assemblyFile);
			return false;
		}

		[ThreadStatic] private static int portLoad;

		internal static void PatchLoadFrom(HarmonyLib.Harmony harmony)
		{
			try
			{
				MethodInfo loadFrom = typeof(Assembly).GetMethod(nameof(Assembly.LoadFrom), new[] { typeof(string) });
				harmony.Patch(loadFrom, prefix: new HarmonyLib.HarmonyMethod(typeof(PortPlatform), nameof(BeforeLoadFrom)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] Assembly.LoadFrom left as it is (mods load without the port's handling): " + e.Message);
			}
			try
			{
				// Both overloads of each: the short one only calls the long one, which its precompiled code may hold inlined.
				foreach (Type[] parameters in new[] { new[] { typeof(byte[]) }, new[] { typeof(byte[]), typeof(byte[]) } })
				{
					harmony.Patch(typeof(Assembly).GetMethod(nameof(Assembly.Load), parameters), prefix: new HarmonyLib.HarmonyMethod(typeof(PortPlatform), nameof(BeforeLoadBytes)));
				}
				foreach (Type[] parameters in new[] { new[] { typeof(Stream) }, new[] { typeof(Stream), typeof(Stream) } })
				{
					harmony.Patch(typeof(AssemblyLoadContext).GetMethod(nameof(AssemblyLoadContext.LoadFromStream), parameters), prefix: new HarmonyLib.HarmonyMethod(typeof(PortPlatform), nameof(BeforeLoadStream)));
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] assemblies loaded from bytes keep their processor mark: " + e.Message);
			}
		}

		/// <summary>
		/// What Assembly.LoadFrom does here (BeforeLoadFrom). A mod's own copy of 0Harmony.dll (the Harmony mod ships one,
		/// others bundle it) is not loaded: the copy in the game folder, patched for this runtime (MonoMod's Android and
		/// glibc assumptions, see patch/Patch.cs), is the one every mod gets. Loading a second copy of the same assembly
		/// name into the default context would fail anyway.
		/// </summary>
		public static Assembly LoadModAssembly(string path)
		{
			portLoad++;
			try
			{
				return LoadModAssemblyNow(path);
			}
			finally
			{
				portLoad--;
			}
		}

		private static Assembly LoadModAssemblyNow(string path)
		{
			long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
			PrepareModBeforeLoad(path);
			long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
			modLoads++;
			ticksPrepared += t1 - t0;
			string name = Path.GetFileNameWithoutExtension(path);
			NoteLoadedNames();
			if (loadedByName.TryGetValue(name, out Assembly loaded))
			{
				// Already loaded (the game folder's patched 0Harmony.dll, or a library another mod bundles too): the
				// runtime would refuse a second assembly of the same name in its default context anyway.
				Console.WriteLine("[port17] " + name + " is already loaded; " + path + " skipped");
				ticksNames += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
				return loaded;
			}
			long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
			ticksNames += t2 - t1;
			if (name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase))
			{
				string ours = Path.Combine(AppContext.BaseDirectory, "0Harmony.dll");
				if (File.Exists(ours))
				{
					Console.WriteLine("[port17] mod asked for " + path + "; loading the game folder's 0Harmony.dll instead");
					return Assembly.LoadFrom(ours);
				}
			}
			string fixedCopy = FixedArchitectureCopy(path);
			long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
			ticksArchitecture += t3 - t2;
			Assembly assembly;
			try
			{
				assembly = Assembly.LoadFrom(fixedCopy ?? path);
			}
			catch (FileLoadException) when (SameNameLoaded(fixedCopy ?? path, out Assembly same))
			{
				// A file of another name holding an assembly that is loaded already (another version of it too): on the
				// PC Assembly.LoadFrom hands back the loaded one (Ludeon's runtime, measured 2026-10-29 with the PC's own
				// runtime files), the stock runtime throws, and the game logged "Exception loading ..." and stopped
				// loading that mod's assemblies. A real case: CeleTech Arsenal MKIII ships two old copies of
				// CeleTech.Base in 1.7/Assemblies/backup/, which the game loads too, and by name before the real one.
				Console.WriteLine("[port17] " + Path.GetFileName(path) + " is " + same.GetName().Name + ", which is loaded already; that one is used, as on the PC");
				assembly = same;
			}
			ticksLoad += System.Diagnostics.Stopwatch.GetTimestamp() - t3;
			return assembly;
		}

		/// <summary>Whether an assembly of the file's own name (from its metadata, not the file name) is loaded; which one.</summary>
		private static bool SameNameLoaded(string file, out Assembly loaded)
		{
			loaded = null;
			try
			{
				string name = AssemblyName.GetAssemblyName(file).Name;
				NoteLoadedNames();
				return name != null && loadedByName.TryGetValue(name, out loaded);
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>The assemblies loaded so far, by name (each asked once: that makes an AssemblyName object every time).</summary>
		private static void NoteLoadedNames()
		{
			foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
			{
				// A stand-in (MissingAssemblies) is not the mod's assembly: a mod loaded earlier asked for this one before it
				// was there (Rimtalk - Secrets and side quest expansion before RimTalk), and the real one loads now, as on the
				// PC. Until 2026-10-28 the stand-in was taken for it and RimTalk with its 15 add-ons had no types.
				if (namesSeen.Add(candidate) && !candidate.IsDynamic && !MissingAssemblies.IsStandIn(candidate))
				{
					loadedByName[candidate.GetName().Name] = candidate;
				}
			}
		}

		private static readonly HashSet<Assembly> namesSeen = new HashSet<Assembly>();
		private static readonly Dictionary<string, Assembly> loadedByName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

		// How long the parts of LoadModAssembly took over all mod assemblies of this start (said once, in OnEntryUIInit).
		private static int modLoads, stackChecks;
		private static long ticksStackChecks;
		private static long ticksPrepared, ticksNames, ticksArchitecture, ticksLoad;

		private static string Ms(long ticks)
		{
			return (ticks * 1000 / System.Diagnostics.Stopwatch.Frequency) + " ms";
		}

		/// <summary>
		/// A mod that was not prepared for this runtime yet (imported raw, or copied into Mods by hand) has its assemblies
		/// prepared in place now (ModPrepHost, patch/ModPrep.cs). A failure, Mono.Cecil.dll missing from the game folder
		/// included, is reported and the mod loads as it is.
		/// </summary>
		private static void PrepareModBeforeLoad(string path)
		{
			try
			{
				ModPrepHost.BeforeLoad(path);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] mod prep failed for " + path + ": " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>
		/// For the in-game mod import (mobile/ModImport.cs): prepares the assemblies of a mod folder that was just installed
		/// and returns a one-line report ("12 assemblies seen, 12 rewritten"); problems lists the assemblies that could not
		/// be rewritten and that the game of the given version may load (not those in the folders of other game versions).
		/// Throws when it cannot prepare at all.
		/// </summary>
		public static string PrepareModFolder(string root, int gameMajor, int gameMinor, out string[] problems)
		{
			return ModPrepHost.PrepareForImport(root, new Version(gameMajor, gameMinor), out problems);
		}

		/// <summary>The import calls this when it is done: the preparation's copies of the game assemblies are let go.</summary>
		public static void ReleaseModPrep()
		{
			ModPrepHost.Release();
		}

		/// <summary>
		/// A mod assembly built for x64 only (PE machine type AMD64, pure IL all the same), or for x86 only (32BITREQUIRED),
		/// is refused by the runtime on arm64; a copy made loadable (ModPrep.FixMachineType) is kept next to the game
		/// (modcache/<mod folder>/) and loaded instead. Mods prepared before the x86 case was known get it here. Returns
		/// null when the file needs no change.
		/// </summary>
		/// <summary>Whether an I386 image's CLR header asks for a 32-bit process only (the header's flags read from the file; the section table is in the head).</summary>
		private static bool Only32Bit(string path, byte[] head, int got, int pe)
		{
			int offset = ModPrep.ClrFlagsOffset(head.Length == got ? head : head.AsSpan(0, got).ToArray(), pe);
			if (offset < 0)
			{
				return false;
			}
			var flags = new byte[4];
			using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, false))
			{
				stream.Position = offset;
				if (stream.Read(flags, 0, 4) != 4)
				{
					return false;
				}
			}
			uint value = BitConverter.ToUInt32(flags, 0);
			return (value & 0x1) != 0 && (value & 0x2) != 0 && (value & 0x20000) == 0;
		}

		private static string FixedArchitectureCopy(string path)
		{
			try
			{
				// The mark is in the file's first kilobyte; the whole file is read only when a copy has to be made
				// (never for a prepared mod: preparation changes the mark in place).
				var head = new byte[1024];
				int got;
				using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, false))
				{
					got = stream.Read(head, 0, head.Length);
				}
				if (got < 0x40 || head[0] != (byte)'M' || head[1] != (byte)'Z')
				{
					return null;
				}
				int pe = BitConverter.ToInt32(head, 0x3c);
				if (pe < 0 || pe + 24 > got || head[pe] != (byte)'P' || head[pe + 1] != (byte)'E')
				{
					return null;
				}
				ushort machine = BitConverter.ToUInt16(head, pe + 4);
				if (machine != 0x8664 && !(machine == 0x14c && Only32Bit(path, head, got, pe)))
				{
					return null;
				}
				byte[] bytes = File.ReadAllBytes(path);
				if (!ModPrep.FixMachineType(bytes))
				{
					return null;
				}
				string modFolder = "x";
				try
				{
					// <Mods>/<id>/<version>/Assemblies/<file>.dll (or <Mods>/<id>/Assemblies/...): the mod's folder name keeps copies apart.
					string assemblies = Path.GetDirectoryName(path);
					string above = Path.GetDirectoryName(assemblies);
					string aboveThat = Path.GetDirectoryName(above);
					modFolder = Path.GetFileName(Path.GetFileName(above).Contains(".") ? aboveThat : above);
				}
				catch (Exception)
				{
				}
				string folder = Path.Combine(AppContext.BaseDirectory, "..", "modcache", modFolder);
				Directory.CreateDirectory(folder);
				string copy = Path.Combine(folder, Path.GetFileName(path));
				if (!File.Exists(copy) || File.GetLastWriteTimeUtc(copy) < File.GetLastWriteTimeUtc(path) || new FileInfo(copy).Length != bytes.Length)
				{
					File.WriteAllBytes(copy, bytes);
				}
				Console.WriteLine("[port17] " + Path.GetFileName(path) + " is marked " + (machine == 0x8664 ? "AMD64" : "x86 only") + "; loading a copy marked for this runtime from " + copy);
				return copy;
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] could not check the architecture of " + path + ": " + e.Message);
				return null;
			}
		}

		/// <summary>
		/// Prefix of Assembly.Load(byte[]) and Load(byte[], byte[]), installed with the one of LoadFrom (PatchLoadFrom). An
		/// image marked for one processor is refused from bytes as from a file, and Ludeon's runtime takes any mark
		/// (WaylithClr.LaxAssemblyArchitecture). Files get a loadable mark from the preparation or FixedArchitectureCopy;
		/// an image a mod unpacks or makes itself (Costura's compressed resources, a zip, a download) passes neither, so a
		/// copy of its bytes with the mark fixed loads instead. The caller's array stays as it was; should the check itself
		/// fail, the runtime gets the bytes as they are, and a mod sees the runtime's own answer (BadImageFormatException
		/// for header garbage), as on the PC.
		/// </summary>
		public static void BeforeLoadBytes(ref byte[] rawAssembly)
		{
			try
			{
				byte[] loadable = LoadableCopy(rawAssembly);
				if (loadable != null)
				{
					rawAssembly = loadable;
				}
			}
			catch (Exception)
			{
				// the runtime gets the bytes as they are
			}
		}

		/// <summary>
		/// The same for AssemblyLoadContext.LoadFromStream(Stream) and (Stream, Stream). The image is read here once, from
		/// where the stream stands to its end, as the runtime reads it, and goes on in a MemoryStream (the fixed copy when it
		/// has such a mark), whose buffer the runtime takes without reading again: the caller's stream is read once, as on
		/// the PC, and ends where the runtime would leave it, and a read that fails throws here what the runtime's own read
		/// would. A stream that cannot seek, or whose length the runtime refuses, is left to the runtime, which refuses it
		/// as on the PC.
		/// </summary>
		public static void BeforeLoadStream(ref Stream assembly)
		{
			Stream given = assembly;
			byte[] image;
			try
			{
				image = given == null || !given.CanSeek ? null : new byte[given.Length - given.Position];
			}
			catch (Exception)
			{
				// no length, or none an array can have: the runtime asks again and refuses the stream
				image = null;
			}
			if (image == null)
			{
				return;
			}
			given.ReadExactly(image);
			byte[] loadable = image;
			try
			{
				loadable = LoadableCopy(image) ?? image;
			}
			catch (Exception)
			{
				// the runtime gets the image as it was read
			}
			assembly = new MemoryStream(loadable, 0, loadable.Length, false, true);
		}

		/// <summary>A copy of an image marked for one processor, marked for any (ModPrep.FixMachineType); null for any other image.</summary>
		private static byte[] LoadableCopy(byte[] image)
		{
			if (image == null || !ModPrep.MarkedForOneProcessor(image))
			{
				return null;
			}
			var copy = (byte[])image.Clone();
			ModPrep.FixMachineType(copy);
			Console.WriteLine("[port17] an assembly loaded from bytes (" + copy.Length + " bytes) is marked for one processor (AMD64 or x86 only); a copy marked for this runtime loads");
			return copy;
		}

		/// <summary>
		/// Stands in for Type.GetField(name, flags) inside Harmony (patch/Patch.cs rewrites the calls in 0Harmony.dll's
		/// HarmonyLib types). Mods written for Mono reach into the framework's private fields by Mono's names; this
		/// runtime's framework names many of them differently (Mono's XmlElement.name is _name here, m_value is _value,
		/// and so on). Ludeon's runtime fork tolerates that (WaylithClr.LaxPrivateFieldNames in the game's
		/// runtimeconfig); the stock runtime finds nothing, and Humanoid Alien Races for one fails to initialize.
		/// When the exact name is not there and the type is one of the framework's, a non-public field whose name is the
		/// same apart from a leading "_" or "m_" is taken, if there is exactly one.
		/// </summary>
		public static FieldInfo GetFieldLax(Type type, string name, BindingFlags flags)
		{
			FieldInfo field = type.GetField(name, flags);
			if (field != null || string.IsNullOrEmpty(name) || (flags & BindingFlags.NonPublic) == 0 || !IsFrameworkType(type))
			{
				return field;
			}
			string wanted = WithoutFieldPrefix(name);
			FieldInfo found = null;
			foreach (FieldInfo candidate in type.GetFields(flags))
			{
				if (!candidate.IsPublic && string.Equals(WithoutFieldPrefix(candidate.Name), wanted, StringComparison.Ordinal))
				{
					if (found != null)
					{
						return null; // two fields answer to the name: not guessed
					}
					found = candidate;
				}
			}
			if (found != null)
			{
				lock (laxFieldsReported)
				{
					if (laxFieldsReported.Add(type.FullName + "." + name))
					{
						Console.WriteLine("[port17] " + type.FullName + " has no field '" + name + "'; '" + found.Name + "' serves (private field names differ between Mono's framework and this one)");
					}
				}
			}
			return found;
		}

		private static readonly System.Collections.Generic.HashSet<string> laxFieldsReported = new System.Collections.Generic.HashSet<string>();

		private static bool IsFrameworkType(Type type)
		{
			string assembly = type.Assembly.GetName().Name ?? "";
			return assembly == "mscorlib" || assembly == "netstandard" || assembly.StartsWith("System", StringComparison.Ordinal);
		}

		private static string WithoutFieldPrefix(string name)
		{
			if (name.StartsWith("m_", StringComparison.Ordinal))
			{
				return name.Substring(2);
			}
			return name.StartsWith("_", StringComparison.Ordinal) ? name.Substring(1) : name;
		}

		/// <summary>
		/// The opcode Harmony emits for a call instruction of a patched method (patch/Patch.cs passes the arguments of
		/// HarmonyLib.Emitter.Emit(OpCode, MethodInfo) and EmitCall through here). Transpilers written for Mono use
		/// callvirt for static methods here and there (Toddlers' call of its static settings getter): Mono runs that,
		/// Ludeon's runtime fork too (WaylithClr.LaxCallvirt), the stock runtime throws "Method not found: '?'" or
		/// "invalid program" when it compiles the patched method. A callvirt to a static method becomes a call.
		/// </summary>
		public static System.Reflection.Emit.OpCode LaxCallOpcode(System.Reflection.Emit.OpCode opcode, MethodInfo method)
		{
			if (method != null && method.IsStatic && opcode == System.Reflection.Emit.OpCodes.Callvirt)
			{
				lock (laxFieldsReported)
				{
					if (laxFieldsReported.Add("callvirt " + method.DeclaringType?.FullName + "." + method.Name))
					{
						Console.WriteLine("[port17] a patch calls the static method " + method.DeclaringType?.FullName + "." + method.Name + " with callvirt; emitted as call");
					}
				}
				return System.Reflection.Emit.OpCodes.Call;
			}
			return opcode;
		}

		/// <summary>LaxCallOpcode for Harmony's Emitter.DynEmit(OpCode, object), which emits the instructions transpilers hand back: the operand is a method only for some.</summary>
		public static System.Reflection.Emit.OpCode LaxCallOpcodeForOperand(System.Reflection.Emit.OpCode opcode, object operand)
		{
			return operand is MethodInfo method ? LaxCallOpcode(opcode, method) : opcode;
		}

		/// <summary>
		/// Called at the start of HarmonyLib.MethodCreatorTools.EmitCodes (patch/Patch.cs adds the call) with Harmony's
		/// instruction list for a replacement method: values a transpiler left on the stack at a jump are popped, as
		/// Mono in effect does (hooks/HarmonyCompat.cs). Never lets anything escape into Harmony.
		/// </summary>
		public static void NormalizeHarmonyStack(object creator, object codes)
		{
			long began = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				NormalizeStack(creator, codes);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] the stack check of a patched method failed (the method is emitted as it is): " + e.GetType().Name + ": " + e.Message);
			}
			stackChecks++;
			ticksStackChecks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
		}

		// Apart, and not inlined: HarmonyCompat names Harmony's types, which are only there once Harmony is loaded.
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		private static void NormalizeStack(object creator, object codes)
		{
			HarmonyCompat.Normalize(creator, codes as System.Collections.Generic.List<HarmonyLib.CodeInstruction>);
		}

		public static bool SteamInitializedForMenu()
		{
			return true;
		}

		public static System.Runtime.InteropServices.PosixSignalRegistration RegisterPosixSignal(System.Runtime.InteropServices.PosixSignal signal, Action<System.Runtime.InteropServices.PosixSignalContext> handler)
		{
			return null;
		}
	}
}

/// <summary>
/// Runs before the launcher's Main (CoreCLR's STARTUP_HOOKS). It stands in for the assembly-binding behaviour of
/// Ludeon's runtime fork that a stock runtime does not have: references to the Unity assemblies (mods were
/// compiled against UnityEngine.CoreModule and friends) resolve to UnityStubs.dll, the engine's compatibility
/// layer, the way "WaylithClr.AssemblyNameRedirects" in the game's runtimeconfig says.
/// </summary>
internal static class StartupHook
{
	private static Assembly stubs;

	public static void Initialize()
	{
		PortHooks.PortPlatform.StartedAtTick = Environment.TickCount64;
		PortHooks.PortLog.Start();
		// Memory and the game's last log line while it loads (the record starts only with the main menu), stalls later.
		PortHooks.Watch.Start();
		// Before anything of Harmony's runs: a kernel that names its CPU oddly left MonoMod without detours (a Kirin 8000).
		PortHooks.MonoModArch.Check();
		PortHooks.JitProfile.Start();
		PortHooks.InlineWatch.Start();
		AssemblyLoadContext.Default.Resolving += Resolve;
		PretendProcessPath();
		LimitGraphicsMemory();
		DropAtlasCacheIfAsked();
		MoveBackslashNames();
		MakeHomeFolders();
		// Before the first patch: the order in which Harmony's replacements are put in, for PortMobile's InlineGuard.
		PortHooks.ReplacementCalls.Watch(new HarmonyLib.Harmony("port17.replacements"));
		// A mod that takes every owner's patches off leaves the port's, which are engine code on the PC.
		PortHooks.PortPatches.Keep(new HarmonyLib.Harmony("port17.keeppatches"));
		PortHooks.Textures.Start();
		PortHooks.UiDescriptors.Start();
		// A mod's mesh renderer let go while the game runs must not take the white fallback textures every renderer shares.
		PortHooks.SharedFallbacks.Start(new HarmonyLib.Harmony("port17.sharedfallbacks"));
		PortHooks.AndroidFonts.Start();
		// Mods' asset bundles name their assets after the Workshop folder the PC has, which a mod's folder here may not be.
		PortHooks.BundlePaths.Start(new HarmonyLib.Harmony("port17.bundlepaths"));
		PortHooks.WindowsLibraries.Start();
		// The phone's proxy for web requests, as the PC's runtime follows Windows' proxy settings.
		PortHooks.SystemProxy.Start();
		PortHooks.MissingAssemblies.Start(new HarmonyLib.Harmony("port17.missingassemblies"));
		// The PC's self-contained game folder, restart and Assembly.LoadFrom, which tools that rewrite the game (PurePatcher) count on.
		PortHooks.SelfContained.CopyRuntime();
		PortHooks.SelfContained.Start(new HarmonyLib.Harmony("port17.selfcontained"));
		PortHooks.PortPlatform.PatchLoadFrom(new HarmonyLib.Harmony("port17.loadfrom"));
		// Slang's sessions hold its core module (about 250 MB each): one at a time, let go when idle.
		PortHooks.ShaderSessions.Start(new HarmonyLib.Harmony("port17.shadersessions"));
		// The chip's name and the native heap figures, which the engine's Linux code reads from places Android has not.
		PortHooks.HardwareInfo.Start(new HarmonyLib.Harmony("port17.hardwareinfo"));
		PortHooks.LoadRecovery.Start();
		// ICU as on the PC unless the host found the system's unusable (host.c system_icu) or runtime.properties says otherwise.
		bool invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out bool on) && on;
		Console.WriteLine("[port17] globalization: " + (invariant ? "invariant (no ICU)" : "ICU, as on the PC (zh-CN October: " + System.Globalization.CultureInfo.GetCultureInfo("zh-CN").DateTimeFormat.GetMonthName(10) + ")"));
		Console.WriteLine("[port17] startup hook installed (Unity assembly names resolve to UnityStubs)");
		PortDiag.StartIfRequested();
	}

	/// <summary>
	/// The user's folders, which a Windows PC always has (and a Linux desktop as well). Harmony's FileLog writes
	/// harmony.log.txt on the desktop, and its Reset deletes that file, which throws DirectoryNotFoundException without
	/// the folder: Change DevSpeed calls Reset in its transpiler, so its static constructor failed and its patch was never
	/// made (a tester, 2026-10-30). Mods write into the others the same way (Documents, ApplicationData, Pictures). The
	/// runtime for Android puts each in $HOME (Desktop, Documents, .config, Pictures, Music, Videos, Templates, .fonts),
	/// here inside the app's own files, and makes none of them, not even for SpecialFolderOption.Create. Those are made;
	/// the folders it puts elsewhere (/usr/share) or nowhere ("") stay as on a Linux PC.
	/// </summary>
	private static void MakeHomeFolders()
	{
		try
		{
			string home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));
			if (string.IsNullOrEmpty(home) || home == "/")
			{
				return;
			}
			var made = new List<string>();
			foreach (Environment.SpecialFolder folder in Enum.GetValues<Environment.SpecialFolder>())
			{
				string path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
				if (!path.StartsWith(home + "/", StringComparison.Ordinal) || Directory.Exists(path))
				{
					continue;
				}
				try
				{
					Directory.CreateDirectory(path);
					made.Add(Path.GetRelativePath(home, path));
				}
				catch (Exception e)
				{
					Console.WriteLine("[port17] " + path + " not made: " + e.Message);
				}
			}
			if (made.Count > 0)
			{
				Console.WriteLine("[port17] folders made in " + home + ", as a PC has them (Harmony's log goes to the desktop): " + string.Join(", ", made));
			}
		}
		catch (Exception e)
		{
			Console.WriteLine("[port17] home folders not made: " + e.Message);
		}
	}

	/// <summary>
	/// PortMobile's atlas check found textures missing from a static atlas page that came from the engine's cache
	/// (TextureCache/atlas-rebuild.txt says which): the cached pages (TextureCache/v*/*/pages.*.zip) are deleted before
	/// the engine opens them, so that this start bakes every atlas again from its textures. The pages are baked before
	/// PortMobile loads, so it cannot do this itself.
	/// </summary>
	private static void DropAtlasCacheIfAsked()
	{
		try
		{
			string home = Environment.GetEnvironmentVariable("HOME");
			if (string.IsNullOrEmpty(home))
			{
				return;
			}
			string cache = Path.Combine(home, "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios", "TextureCache");
			string flag = Path.Combine(cache, "atlas-rebuild.txt");
			if (!File.Exists(flag))
			{
				return;
			}
			int deleted = 0;
			foreach (string file in Directory.EnumerateFiles(cache, "pages.*", SearchOption.AllDirectories))
			{
				File.Delete(file);
				deleted++;
			}
			foreach (string file in Directory.EnumerateFiles(cache, "atlas-checked*.txt"))
			{
				File.Delete(file);
			}
			string why = File.ReadAllText(flag).Trim();
			File.Delete(flag);
			Console.WriteLine("[port17] atlas cache dropped (" + deleted + " files), the atlases are baked again: " + why);
		}
		catch (Exception e)
		{
			Console.WriteLine("[port17] atlas cache not dropped: " + e.Message);
		}
	}

	/// <summary>
	/// What mods made under names with backslashes before ModPrep read those as separators, as Windows does (rules 14):
	/// RimTalk's memory patch exported into one folder named "RimTalkMemory\MemoryExports", a mod writing
	/// SaveDataFolderPath + "\\x.xml" made "RimWorld by Ludeon Studios\x.xml" beside the data folder. Moved, before any mod
	/// runs, to where the mods look now: the nested folders, without replacing a file that is there already. Looked for
	/// in the data folder, its folders and the one above it, and in the game's Mods.
	/// </summary>
	private static void MoveBackslashNames()
	{
		try
		{
			string home = Environment.GetEnvironmentVariable("HOME");
			if (string.IsNullOrEmpty(home))
			{
				return;
			}
			string data = Path.Combine(home, "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios");
			var folders = new List<string> { Path.GetDirectoryName(data), data, Path.Combine(AppContext.BaseDirectory, "Mods") };
			if (Directory.Exists(data))
			{
				folders.AddRange(Directory.GetDirectories(data).Where(d => Path.GetFileName(d).IndexOf('\\') < 0));
			}
			foreach (string folder in folders)
			{
				if (!Directory.Exists(folder))
				{
					continue;
				}
				foreach (string entry in Directory.GetFileSystemEntries(folder))
				{
					string name = Path.GetFileName(entry);
					if (name.IndexOf('\\') < 0 || !ModPrep.IsBackslashPath(name))
					{
						continue;
					}
					string target = Path.Combine(folder, name.Trim('\\').Replace('\\', '/'));
					int moved = MoveInto(entry, target);
					bool all = !File.Exists(entry) && !Directory.Exists(entry);
					Console.WriteLine("[port17] \"" + name + "\" (a mod's path with backslashes): " + moved + " file(s) moved to " + target + (all ? "" : "; the others stay, the same names are there already"));
				}
			}
		}
		catch (Exception e)
		{
			Console.WriteLine("[port17] names with backslashes not moved: " + e.Message);
		}
	}

	/// <summary>A file, or what a folder holds, moved to the target, keeping what is there already; returns the files moved.</summary>
	private static int MoveInto(string source, string target)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(target));
		if (File.Exists(source))
		{
			if (File.Exists(target) || Directory.Exists(target))
			{
				return 0;
			}
			File.Move(source, target);
			return 1;
		}
		if (!Directory.Exists(target) && !File.Exists(target))
		{
			int files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length;
			Directory.Move(source, target);
			return files;
		}
		int moved = 0;
		foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
		{
			string to = Path.Combine(target, Path.GetRelativePath(source, file));
			if (!File.Exists(to) && !Directory.Exists(to))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(to));
				File.Move(file, to);
				moved++;
			}
		}
		if (Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length == 0)
		{
			Directory.Delete(source, true);
		}
		return moved;
	}

	/// <summary>
	/// The engine sizes its texture budget by the Vulkan memory heap, which on a phone is most of the RAM (11 GiB on
	/// a 12 GB phone): its watermark for putting unused textures back to the disk cache, about 7 GiB there, is never
	/// reached, because the phone's own watchdog kills an app whose graphics memory passes 6 GiB ("process memory is
	/// leaking"; seen while loading 106 mods, 14 of them race mods with 12 000 textures). WAYLITH_VRAM_BUDGET_MB is the
	/// engine's own switch for a smaller heap: past its watermark the textures nothing has drawn stay in the disk
	/// cache until they are first used. It is set here from the phone's RAM (three eighths, 4 GiB at most) unless it
	/// is set already; game/vram.txt holds another number of MB for it (0: no limit).
	/// </summary>
	private static void LimitGraphicsMemory()
	{
		const string name = "WAYLITH_VRAM_BUDGET_MB";
		try
		{
			if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
			{
				return;
			}
			long megabytes = 0;
			string file = Path.Combine(AppContext.BaseDirectory, "vram.txt");
			if (File.Exists(file))
			{
				if (!long.TryParse(File.ReadAllText(file).Trim(), out megabytes) || megabytes <= 0)
				{
					Console.WriteLine("[port17] graphics memory: no limit (vram.txt)");
					return;
				}
			}
			else
			{
				foreach (string line in File.ReadLines("/proc/meminfo"))
				{
					if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
					{
						string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
						megabytes = long.Parse(parts[1]) / 1024 * 3 / 8;
						break;
					}
				}
				if (megabytes <= 0)
				{
					return;
				}
				megabytes = Math.Max(1536, Math.Min(4096, megabytes));
			}
			Environment.SetEnvironmentVariable(name, megabytes.ToString());
			Console.WriteLine("[port17] graphics memory: the engine's heap is limited to " + megabytes + " MB (" + name + ")");
		}
		catch (Exception e)
		{
			Console.WriteLine("[port17] graphics memory: no limit set: " + e.Message);
		}
	}

	/// <summary>
	/// The launcher takes the game folder from Environment.ProcessPath, which in an Android app is the system's
	/// app_process. The runtime caches the path in a private field; it is set here to a name in the game folder
	/// before anyone reads it.
	/// </summary>
	private static void PretendProcessPath()
	{
		try
		{
			FieldInfo field = typeof(Environment).GetField("s_processPath", BindingFlags.NonPublic | BindingFlags.Static);
			if (field == null)
			{
				Console.WriteLine("[port17] Environment.s_processPath not found; the launcher will look for RimWorld_Data next to app_process");
				return;
			}
			string pretend = Path.Combine(AppContext.BaseDirectory, "RimWorld.exe");
			field.SetValue(null, pretend);
			Console.WriteLine("[port17] process path pretends to be " + Environment.ProcessPath);
		}
		catch (Exception e)
		{
			Console.WriteLine("[port17] could not set the process path: " + e.Message);
		}
	}

	/// <summary>
	/// Unity assembly names (UnityEngine.CoreModule and the other modules mods were compiled against) are served by
	/// type-forwarding facades in the game folder (patch/Patch.cs --forwarders); the runtime refuses a resolver that
	/// answers a request with an assembly of another name, so nothing can be done here but say what was asked for.
	/// </summary>
	private static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
	{
		string n = name.Name ?? "";
		foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (loaded.GetName().Name == n)
			{
				// Loaded under another version (mods bundle different versions of the same library): the loaded one serves.
				return loaded;
			}
		}
		if (n.StartsWith("UnityEngine", StringComparison.Ordinal) || n.StartsWith("Unity.", StringComparison.Ordinal))
		{
			Console.WriteLine("[port17] no facade for " + n + " (add it to the forwarders list in host/stage.sh)");
		}
		// Not loaded yet, but there under an older version than asked for (a mod built against a newer library): that one
		// serves, as on the PC (WaylithClr.LaxAssemblyBinding, measured 2026-10-27: a reference to 3.0 bound to the 1.0
		// there, FileNotFoundException here).
		if (name.Version != null && !byNameOnly && n.Length > 0)
		{
			byNameOnly = true;
			try
			{
				return context.LoadFromAssemblyName(new AssemblyName(n));
			}
			catch (Exception)
			{
			}
			finally
			{
				byNameOnly = false;
			}
		}
		return null;
	}

	[ThreadStatic] private static bool byNameOnly;
}
