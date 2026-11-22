using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using Verse;
using Waylith.Graphics;

namespace PortMobile
{
	/// <summary>
	/// "memdump=LABEL" in perfcmd.txt: what the game's memory consists of, written to logs/memdump-LABEL.txt next to the
	/// game folder. Three accounts, each of a part the kernel's list of mappings cannot look into:
	///   - graphics memory: the engine's own list of what it allocated (every texture, target and buffer with its size);
	///   - the native heap: the allocator's totals (in use against held), and its own statistics to memdump-LABEL-malloc.xml;
	///   - the managed heap: every object that can be reached from a static field, counted by type and by the static
	///     field it was first reached from, and the large arrays (85 000 bytes and up: the collector keeps those apart
	///     and never moves them) with what holds them.
	/// The walk runs on the main thread between two frames and takes its time (reflection over every field).
	/// </summary>
	internal static unsafe class MemDiag
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct MallInfo { public nuint arena, ordblks, smblks, hblks, hblkhd, usmblks, fsmblks, uordblks, fordblks, keepcost; }

		[DllImport("libc")] private static extern MallInfo mallinfo();
		[DllImport("libc")] private static extern IntPtr fopen([MarshalAs(UnmanagedType.LPStr)] string path, [MarshalAs(UnmanagedType.LPStr)] string mode);
		[DllImport("libc")] private static extern int fclose(IntPtr file);
		[DllImport("libc")] private static extern int malloc_info(int options, IntPtr file);

		private static string Mib(long bytes) => (bytes / 1048576.0).ToString("0.0").PadLeft(8);

		public static void Dump(string label, bool walk)
		{
			string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"));
			string file = Path.Combine(folder, "memdump-" + label + ".txt");
			try
			{
				Directory.CreateDirectory(folder);
				using (StreamWriter w = new StreamWriter(file, false, new UTF8Encoding(false)))
				{
					w.WriteLine("memory dump '" + label + "' at " + DateTime.Now.ToString("HH:mm:ss") + ", " + (Current.ProgramState == ProgramState.Playing ? "in a game" : "at the menu"));
					Section(w, "the collector", Collector);
					Section(w, "the native heap", x => Native(x, Path.Combine(folder, "memdump-" + label + "-malloc.xml")));
					Section(w, "graphics memory", Graphics);
					if (walk)
					{
						Section(w, "the managed heap", Managed);
					}
				}
				Console.WriteLine("[mobile] memdump: written to " + file);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] memdump failed: " + e);
			}
		}

		/// <summary>One full blocking collection, with the heap's size before and after in the log.</summary>
		public static void Collect(bool compactLarge)
		{
			GCMemoryInfo before = GC.GetGCMemoryInfo();
			long usedBefore = GC.GetTotalMemory(false);
			Stopwatch watch = Stopwatch.StartNew();
			if (compactLarge)
			{
				GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
			}
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
			GC.WaitForPendingFinalizers();
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
			double took = watch.Elapsed.TotalMilliseconds;
			GCMemoryInfo after = GC.GetGCMemoryInfo();
			ReadOnlySpan<GCGenerationInfo> g0 = before.GenerationInfo, g1 = after.GenerationInfo;
			Console.WriteLine("[mobile] gc" + (compactLarge ? " (large objects compacted)" : "") + ": in use " + Mib(usedBefore).Trim() + " -> " + Mib(GC.GetTotalMemory(false)).Trim() + " MiB, heap " + Mib(before.HeapSizeBytes).Trim() + " -> " + Mib(after.HeapSizeBytes).Trim()
				+ ", committed " + Mib(before.TotalCommittedBytes).Trim() + " -> " + Mib(after.TotalCommittedBytes).Trim() + "; gen2 " + Mib(g0[2].SizeAfterBytes).Trim() + " -> " + Mib(g1[2].SizeAfterBytes).Trim()
				+ ", large " + Mib(g0[3].SizeAfterBytes).Trim() + " -> " + Mib(g1[3].SizeAfterBytes).Trim() + " (free in it " + Mib(g1[3].FragmentationAfterBytes).Trim() + "); took " + took.ToString("0") + " ms");
		}

		private static void Section(StreamWriter w, string name, Action<StreamWriter> body)
		{
			w.WriteLine();
			w.WriteLine("=== " + name + " ===");
			try
			{
				body(w);
			}
			catch (Exception e)
			{
				w.WriteLine("failed: " + e);
			}
			w.Flush();
		}

		private static void Collector(StreamWriter w)
		{
			GCMemoryInfo info = GC.GetGCMemoryInfo();
			w.WriteLine("heap " + Mib(info.HeapSizeBytes) + " MiB, committed " + Mib(info.TotalCommittedBytes) + ", fragmented " + Mib(info.FragmentedBytes) + ", in use now " + Mib(GC.GetTotalMemory(false))
				+ "; collections " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2) + "; server " + GCSettings.IsServerGC + ", latency " + GCSettings.LatencyMode
				+ ", machine load " + Mib(info.MemoryLoadBytes) + " of " + Mib(info.TotalAvailableMemoryBytes) + ", high from " + Mib(info.HighMemoryLoadThresholdBytes));
			string[] names = { "gen0", "gen1", "gen2", "large objects", "pinned objects" };
			ReadOnlySpan<GCGenerationInfo> generations = info.GenerationInfo;
			for (int i = 0; i < generations.Length && i < names.Length; i++)
			{
				w.WriteLine("  " + names[i].PadRight(15) + Mib(generations[i].SizeAfterBytes) + " MiB after the last collection (" + Mib(generations[i].FragmentationAfterBytes) + " of it free space)");
			}
		}

		private static void Native(StreamWriter w, string xml)
		{
			MallInfo m = mallinfo();
			w.WriteLine("malloc: in use " + Mib((long)m.uordblks) + " MiB, free inside the heap " + Mib((long)m.fordblks) + ", held from the system " + Mib((long)m.arena + (long)m.hblkhd)
				+ " (of it large blocks " + Mib((long)m.hblkhd) + ")");
			IntPtr f = fopen(xml, "w");
			if (f != IntPtr.Zero)
			{
				malloc_info(0, f);
				fclose(f);
				w.WriteLine("the allocator's own statistics: " + xml);
			}
		}

		private static void Graphics(StreamWriter w)
		{
			w.WriteLine(GpuMemoryReport.BuildReport(false).TrimEnd());
			List<GpuMemoryTracker.TrackedAllocation> all = new List<GpuMemoryTracker.TrackedAllocation>();
			GpuMemoryTracker.CollectAll(all);
			foreach (IGrouping<GpuMemoryCategory, GpuMemoryTracker.TrackedAllocation> group in all.GroupBy(a => a.Category).OrderByDescending(g => g.Sum(a => a.Bytes)))
			{
				List<GpuMemoryTracker.TrackedAllocation> list = group.OrderByDescending(a => a.Bytes).ToList();
				w.WriteLine();
				w.WriteLine(group.Key + ": " + Mib(list.Sum(a => a.Bytes)) + " MiB in " + list.Count);
				// By size class: how much sits in the few large ones.
				foreach (IGrouping<int, GpuMemoryTracker.TrackedAllocation> bucket in list.GroupBy(a => a.Bytes >= 16 << 20 ? 4 : a.Bytes >= 4 << 20 ? 3 : a.Bytes >= 1 << 20 ? 2 : a.Bytes >= 256 << 10 ? 1 : 0).OrderByDescending(b => b.Key))
				{
					string[] bucketNames = { "under 256 KiB", "256 KiB to 1 MiB", "1 to 4 MiB", "4 to 16 MiB", "16 MiB and more" };
					w.WriteLine("    " + bucketNames[bucket.Key].PadRight(18) + Mib(bucket.Sum(a => a.Bytes)) + " MiB in " + bucket.Count());
				}
				// The same name many times over (a pool of targets, one buffer per frame in flight) reads better as one line.
				foreach (var named in list.GroupBy(a => (a.Name ?? "(no name)") + (a.Detail != null ? "  [" + a.Detail + "]" : "")).Select(g => new { g.Key, Bytes = g.Sum(a => a.Bytes), Count = g.Count() }).OrderByDescending(g => g.Bytes).Take(group.Key == GpuMemoryCategory.GameTexture ? 60 : 40))
				{
					w.WriteLine("  " + Mib(named.Bytes) + (named.Count > 1 ? " x" + named.Count : "") + "  " + named.Key);
				}
			}
		}

		// ---- the managed heap ----

		private sealed class Kind
		{
			public Type type;
			public bool skip, holdsReferences, elementsAreReferences, elementsAreStructs;
			public uint baseSize, componentSize;
			public FieldInfo[] fields;
			public bool[] fieldIsStruct;
			public int[] fieldContext;
			public long count, bytes;
		}

		private struct Large
		{
			public long bytes;
			public Type type;
			public int context, holderContext, root;
		}

		private static Dictionary<Type, Kind> kinds;
		private static Dictionary<Type, bool> structHoldsReferences;
		private static HashSet<object> seen;
		private static Stack<(object, int)> pending;
		private static List<string> contexts;
		private static List<Large> large;
		private static long rootBytes, rootCount, allBytes, allCount;
		private static int currentRoot;

		private static bool IsPlain(Type t)
		{
			return t.IsPrimitive || t.IsEnum || t.IsPointer || t.IsByRef || t.IsFunctionPointer || t == typeof(IntPtr) || t == typeof(UIntPtr) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid) || t == typeof(TimeSpan);
		}

		private static bool HoldsReferences(Type t)
		{
			if (!t.IsValueType)
			{
				return true;
			}
			if (IsPlain(t) || t.IsByRefLike)
			{
				return false;
			}
			if (structHoldsReferences.TryGetValue(t, out bool known))
			{
				return known;
			}
			structHoldsReferences[t] = false; // while it is being worked out (a struct cannot hold itself)
			bool holds = false;
			foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				if (HoldsReferences(f.FieldType))
				{
					holds = true;
					break;
				}
			}
			structHoldsReferences[t] = holds;
			return holds;
		}

		// A collection's own innards say nothing about who holds an object: what is found through them keeps the
		// collection's own context.
		private static bool IsCollectionInnards(Type declaring)
		{
			if (declaring == null || !declaring.IsGenericType)
			{
				return false;
			}
			string name = declaring.GetGenericTypeDefinition().FullName ?? "";
			return name.StartsWith("System.Collections.", StringComparison.Ordinal);
		}

		private static Kind KindOf(Type type)
		{
			if (kinds.TryGetValue(type, out Kind kind))
			{
				return kind;
			}
			kind = new Kind { type = type };
			kinds[type] = kind;
			IntPtr table = type.TypeHandle.Value;
			uint flags = *(uint*)table;
			kind.baseSize = *(uint*)(table + 4);
			kind.componentSize = (flags & 0x80000000u) != 0 ? flags & 0xFFFFu : 0u;
			if (typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type) || typeof(Module).IsAssignableFrom(type) || type == typeof(Pointer)
				|| (type.Namespace != null && type.Namespace.StartsWith("System.Reflection", StringComparison.Ordinal)) || type.DeclaringType == typeof(MemDiag) || type == typeof(MemDiag))
			{
				kind.skip = true;
				return kind;
			}
			if (type.IsArray)
			{
				Type element = type.GetElementType();
				kind.elementsAreReferences = !element.IsValueType && !element.IsPointer;
				kind.elementsAreStructs = element.IsValueType && HoldsReferences(element);
				kind.holdsReferences = kind.elementsAreReferences || kind.elementsAreStructs;
				return kind;
			}
			if (type == typeof(string))
			{
				return kind;
			}
			List<FieldInfo> fields = new List<FieldInfo>();
			for (Type t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
			{
				foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					if (!IsPlain(f.FieldType) && !f.FieldType.IsByRefLike && HoldsReferences(f.FieldType))
					{
						fields.Add(f);
					}
				}
			}
			kind.fields = fields.ToArray();
			kind.fieldIsStruct = fields.Select(f => f.FieldType.IsValueType).ToArray();
			kind.fieldContext = fields.Select(f => IsCollectionInnards(f.DeclaringType) ? -1 : Context(Name(f.DeclaringType) + "." + f.Name)).ToArray();
			kind.holdsReferences = kind.fields.Length > 0;
			return kind;
		}

		private static string Name(Type t)
		{
			if (t == null)
			{
				return "?";
			}
			if (t.IsArray)
			{
				return Name(t.GetElementType()) + "[]";
			}
			string name = t.Namespace != null && !t.IsNested ? t.Namespace + "." + t.Name : t.IsNested ? Name(t.DeclaringType) + "+" + t.Name : t.Name;
			if (t.IsGenericType && !t.IsGenericTypeDefinition)
			{
				name += "<" + string.Join(",", t.GetGenericArguments().Select(a => a.Name)) + ">";
			}
			return name;
		}

		private static readonly Dictionary<string, int> contextIds = new Dictionary<string, int>();

		private static int Context(string name)
		{
			if (!contextIds.TryGetValue(name, out int id))
			{
				id = contexts.Count;
				contexts.Add(name);
				contextIds[name] = id;
			}
			return id;
		}

		private static void Reach(object o, int context, int holderContext)
		{
			if (o == null || !seen.Add(o))
			{
				return;
			}
			Type type = o.GetType();
			Kind kind = KindOf(type);
			if (kind.skip)
			{
				return;
			}
			long size = kind.baseSize;
			if (kind.componentSize != 0)
			{
				size += (long)kind.componentSize * (o is string s ? s.Length : ((Array)o).LongLength);
			}
			size = (size + 7) & ~7L;
			kind.count++;
			kind.bytes += size;
			rootBytes += size;
			rootCount++;
			if (size >= 85000)
			{
				large.Add(new Large { bytes = size, type = type, context = context, holderContext = holderContext, root = currentRoot });
			}
			if (kind.holdsReferences)
			{
				pending.Push((o, context));
			}
		}

		private static void Fields(object o, Kind kind, int context)
		{
			for (int i = 0; i < kind.fields.Length; i++)
			{
				object value;
				try
				{
					value = kind.fields[i].GetValue(o);
				}
				catch
				{
					continue;
				}
				if (value == null)
				{
					continue;
				}
				int fieldContext = kind.fieldContext[i] >= 0 ? kind.fieldContext[i] : context;
				if (kind.fieldIsStruct[i])
				{
					Kind inner = KindOf(value.GetType());
					if (!inner.skip && inner.fields != null)
					{
						Fields(value, inner, fieldContext);
					}
				}
				else
				{
					Reach(value, fieldContext, context);
				}
			}
		}

		private static void Drain()
		{
			while (pending.Count > 0)
			{
				(object o, int context) = pending.Pop();
				Kind kind = KindOf(o.GetType());
				if (o is Array array)
				{
					if (kind.elementsAreReferences)
					{
						if (array is object[] objects)
						{
							for (int i = 0; i < objects.Length; i++)
							{
								Reach(objects[i], context, context);
							}
						}
						else
						{
							foreach (object element in array)
							{
								Reach(element, context, context);
							}
						}
					}
					else if (kind.elementsAreStructs)
					{
						Kind inner = KindOf(o.GetType().GetElementType());
						if (inner.fields != null && inner.fields.Length > 0)
						{
							foreach (object element in array)
							{
								Fields(element, inner, context);
							}
						}
					}
				}
				else if (kind.fields != null)
				{
					Fields(o, kind, context);
				}
			}
		}

		private static void Managed(StreamWriter w)
		{
			Stopwatch watch = Stopwatch.StartNew();
			kinds = new Dictionary<Type, Kind>();
			structHoldsReferences = new Dictionary<Type, bool>();
			seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
			pending = new Stack<(object, int)>();
			contexts = new List<string>();
			contextIds.Clear();
			large = new List<Large>();
			allBytes = allCount = 0;
			List<(string name, long bytes, long count)> roots = new List<(string, long, long)>();
			List<string> rootNames = new List<string>();
			int failed = 0;

			void Root(string name, Func<object> read, Type fieldType)
			{
				object value;
				try
				{
					value = read();
				}
				catch
				{
					failed++;
					return;
				}
				if (value == null)
				{
					return;
				}
				rootBytes = rootCount = 0;
				currentRoot = rootNames.Count;
				rootNames.Add(name);
				int context = Context(name);
				if (fieldType.IsValueType)
				{
					Kind inner = KindOf(value.GetType());
					if (!inner.skip && inner.fields != null)
					{
						Fields(value, inner, context);
					}
				}
				else
				{
					Reach(value, context, context);
				}
				Drain();
				if (rootBytes > 0)
				{
					roots.Add((name, rootBytes, rootCount));
					allBytes += rootBytes;
					allCount += rootCount;
				}
			}

			void Statics(Type type, string group)
			{
				FieldInfo[] fields;
				try
				{
					fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
				}
				catch
				{
					failed++;
					return;
				}
				foreach (FieldInfo f in fields)
				{
					if (f.IsLiteral || IsPlain(f.FieldType) || f.FieldType.IsByRefLike || !HoldsReferences(f.FieldType))
					{
						continue;
					}
					FieldInfo field = f;
					Root(group ?? Name(type) + "." + f.Name, () => field.GetValue(null), f.FieldType);
				}
			}

			// The owners first, so that what they hold is counted under their names and not under a cache that happens to
			// point at it: the mods' content, the defs, the game in progress. Then every other static field.
			Statics(typeof(LoadedModManager), null);
			foreach (Type def in typeof(Def).AllSubclassesNonAbstract().Concat(new[] { typeof(Def) }))
			{
				try
				{
					Statics(typeof(DefDatabase<>).MakeGenericType(def), "Verse.DefDatabase<" + def.Name + ">");
				}
				catch
				{
					failed++;
				}
			}
			Statics(typeof(Current), null);
			foreach (Type element in new[] { typeof(byte), typeof(char), typeof(int), typeof(uint), typeof(float), typeof(ushort), typeof(short), typeof(long), typeof(double), typeof(bool), typeof(object), typeof(string),
				typeof(UnityEngine.Vector2), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector4), typeof(UnityEngine.Color), typeof(UnityEngine.Color32), typeof(UnityEngine.Matrix4x4) })
			{
				Type pool = typeof(ArrayPool<>).MakeGenericType(element);
				Root("System.Buffers.ArrayPool<" + element.Name + ">.Shared", () => pool.GetProperty("Shared").GetValue(null), typeof(object));
			}
			string[] first = { "Assembly-CSharp", "UnityStubs", "Waylith", "RimWorld", "Waylith.UnityCompat" };
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => { int at = Array.IndexOf(first, a.GetName().Name); return at < 0 ? first.Length : at; }))
			{
				string assemblyName = assembly.GetName().Name;
				// The class library's own statics, except the core's: touching one runs its type's initializer, and some of
				// those look for native libraries this platform does not have.
				if ((assemblyName.StartsWith("System.", StringComparison.Ordinal) || assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal) || assemblyName == "netstandard" || assemblyName == "mscorlib") && assemblyName != "System.Private.CoreLib")
				{
					continue;
				}
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException e)
				{
					types = e.Types.Where(t => t != null).ToArray();
				}
				catch
				{
					failed++;
					continue;
				}
				foreach (Type type in types)
				{
					if (type.ContainsGenericParameters || type == typeof(MemDiag) || type.DeclaringType == typeof(MemDiag))
					{
						continue;
					}
					Statics(type, null);
				}
			}

			GCMemoryInfo info = GC.GetGCMemoryInfo();
			w.WriteLine("reached from static fields: " + allCount + " objects, " + Mib(allBytes) + " MiB (the collector's heap: " + Mib(info.HeapSizeBytes) + " MiB; the walk's own tables are in that, not in this); "
				+ watch.Elapsed.TotalSeconds.ToString("0.0") + " s, " + failed + " static fields or types could not be read");
			w.WriteLine("not reached this way: what only a thread's stack, a handle from native code or a generic static class's field points at");

			w.WriteLine();
			w.WriteLine("by the static field first reached from (MiB, objects):");
			foreach ((string name, long bytes, long count) in roots.GroupBy(r => r.name).Select(g => (g.Key, g.Sum(r => r.bytes), g.Sum(r => r.count))).OrderByDescending(r => r.Item2).Take(70))
			{
				w.WriteLine("  " + Mib(bytes) + " " + count.ToString().PadLeft(9) + "  " + name);
			}

			w.WriteLine();
			w.WriteLine("by type (MiB, objects):");
			foreach (Kind kind in kinds.Values.Where(k => k.count > 0).OrderByDescending(k => k.bytes).Take(90))
			{
				w.WriteLine("  " + Mib(kind.bytes) + " " + kind.count.ToString().PadLeft(9) + "  " + Name(kind.type));
			}

			w.WriteLine();
			w.WriteLine("by the assembly of the type (arrays: of their element type), MiB:");
			foreach (var byAssembly in kinds.Values.Where(k => k.count > 0).GroupBy(k => { Type t = k.type; while (t.IsArray) { t = t.GetElementType(); } return t.Assembly.GetName().Name; })
				.Select(g => new { g.Key, Bytes = g.Sum(k => k.bytes), Count = g.Sum(k => k.count) }).OrderByDescending(g => g.Bytes).Take(40))
			{
				w.WriteLine("  " + Mib(byAssembly.Bytes) + " " + byAssembly.Count.ToString().PadLeft(9) + "  " + byAssembly.Key);
			}

			long largeBytes = large.Sum(l => l.bytes);
			w.WriteLine();
			w.WriteLine("large arrays (85 000 bytes and up): " + large.Count + ", " + Mib(largeBytes) + " MiB; by what holds them (holder's holder -> holder), MiB, arrays:");
			foreach (var held in large.GroupBy(l => Name(l.type) + "  <-  " + contexts[l.context] + (l.holderContext != l.context ? "  <-  " + contexts[l.holderContext] : ""))
				.Select(g => new { g.Key, Bytes = g.Sum(l => l.bytes), Count = g.Count() }).OrderByDescending(g => g.Bytes).Take(80))
			{
				w.WriteLine("  " + Mib(held.Bytes) + " " + held.Count.ToString().PadLeft(6) + "  " + held.Key);
			}
			w.WriteLine();
			w.WriteLine("the same by the static field they were reached from:");
			foreach (var held in large.GroupBy(l => rootNames[l.root]).Select(g => new { g.Key, Bytes = g.Sum(l => l.bytes), Count = g.Count() }).OrderByDescending(g => g.Bytes).Take(40))
			{
				w.WriteLine("  " + Mib(held.Bytes) + " " + held.Count.ToString().PadLeft(6) + "  " + held.Key);
			}
			w.WriteLine();
			w.WriteLine("the 60 largest single objects:");
			foreach (Large l in large.OrderByDescending(l => l.bytes).Take(60))
			{
				w.WriteLine("  " + Mib(l.bytes) + "  " + Name(l.type) + "  <-  " + contexts[l.context] + "  <-  " + contexts[l.holderContext] + "   (from " + rootNames[l.root] + ")");
			}
			kinds = null;
			seen = null;
			pending = null;
			large = null;
		}
	}
}
