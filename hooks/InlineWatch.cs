using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace PortHooks
{
	/// <summary>
	/// For measuring only (mobile.txt inlineWatch=1): every method the compiler copies into another (the runtime's
	/// MethodJitInliningSucceeded events), so that at the main menu the methods Harmony patched can be looked up among
	/// them. A patched method is not copied into code compiled after it was patched, so each one found was copied in
	/// before: where that copy runs, the patch is skipped. Tier 0 copies nothing; the tiers that do come from call
	/// counts, which the runtime starts 100 ms after the compiler last met a new method (TC_CallCountingDelayMs). This
	/// tells how many patches a change of those settings would cost. It slows the start (every inlining decision is an
	/// event). Measured 2026-10-23 (OnePlus, 137 mods, 2389 patched methods, at the main menu): as the runtime comes,
	/// 8 patched methods in 36-37 places (Rand.get_Int/PushState/PopState/set_Seed, ThingFilter.Allows, the loading
	/// screen's Widgets.DrawShadowAround/DrawAtlas into LongEventsOnGUI, Texture2D.EnsureResident); with
	/// TC_CallCountingDelayMs=0 9 in 89 places (Rand.get_Int alone 63); with TieredPGO=0 3 in 7 (left on all the same: see host.c).
	///
	/// The report also names the patched methods still copied into a replacement Harmony runs, which PortMobile's
	/// InlineGuard is there to make again: a copy of one there was made before it was patched. Harmony names the
	/// replacements of all overloads of a method alike ("type.name_PatchN", N each one's own patch count), so a copy is
	/// matched to its replacement by the signature the runtime gives with it. Listening all the time, instead of the
	/// guard's reading of the code, was measured on the PC at about 7 microseconds an inlining decision, some 60 000
	/// decisions for 5 000 methods compiled at full optimisation.
	/// </summary>
	public static class InlineWatch
	{

		private sealed class Listener : EventListener
		{
			protected override void OnEventSourceCreated(EventSource source)
			{
				if (source.Name == "Microsoft-Windows-DotNETRuntime")
				{
					// JitTracing: the inlining and tail call decisions.
					EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1000);
				}
			}

			protected override void OnEventWritten(EventWrittenEventArgs e)
			{
				if (e.EventName != "MethodJitInliningSucceeded" || e.Payload == null || e.PayloadNames == null)
				{
					return;
				}
				Interlocked.Increment(ref events);
				string inlineeType = Field(e, "InlineeNamespace"), inlinee = Field(e, "InlineeName");
				if (inlineeType.StartsWith("System.", StringComparison.Ordinal) || inlineeType.StartsWith("Microsoft.", StringComparison.Ordinal) || inlineeType.StartsWith("Internal.", StringComparison.Ordinal))
				{
					return;
				}
				string intoType = Field(e, "MethodBeingCompiledNamespace"), into = Field(e, "MethodBeingCompiledName");
				// Where it was copied into, and whether that is a method made at run time (Harmony's replacements).
				copies.GetOrAdd(inlineeType + "::" + inlinee, _ => new ConcurrentDictionary<string, bool>())[intoType + "::" + into] = intoType.Length == 0 || intoType == "dynamicClass";
				if (intoType == "dynamicClass")
				{
					intoReplacements.Enqueue((into, Field(e, "MethodBeingCompiledNameSignature"), inlineeType + "::" + inlinee, Arity(Field(e, "InlineeNameSignature"))));
				}
			}

			private static string Field(EventWrittenEventArgs e, string name)
			{
				int i = e.PayloadNames.IndexOf(name);
				return i >= 0 && e.Payload[i] is string s ? s : "";
			}
		}

		private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> copies = new ConcurrentDictionary<string, ConcurrentDictionary<string, bool>>();
		// The copies into methods made at run time: (name of the method, its signature, the method copied in, how many
		// parameters that has with "this").
		private static readonly ConcurrentQueue<(string Into, string Signature, string Inlinee, int Arity)> intoReplacements = new ConcurrentQueue<(string, string, string, int)>();
		private static Listener listener;
		private static long events;

		/// <summary>From the startup hook, before the game's assemblies are loaded.</summary>
		public static void Start()
		{
			if (Textures.Setting("inlineWatch") != "1")
			{
				return;
			}
			try
			{
				listener = new Listener();
				Console.WriteLine("[port17] inline watch: listening to the compiler's inlining (for measuring; slows the start)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] inline watch: not started: " + e.Message);
			}
		}

		/// <summary>From PortMobile at the main menu: the patched methods that had been copied into other code before.</summary>
		public static void Report(string when, IEnumerable<MethodBase> patched)
		{
			if (listener == null)
			{
				return;
			}
			try
			{
				var patchedNames = new HashSet<string>();
				List<MethodBase> list = patched.ToList();
				foreach (MethodBase method in list)
				{
					if (method.DeclaringType != null)
					{
						patchedNames.Add(method.DeclaringType.FullName + "::" + method.Name);
					}
				}
				int intoStatic = 0, intoDynamic = 0, places = 0;
				var samples = new List<string>();
				foreach (KeyValuePair<string, ConcurrentDictionary<string, bool>> pair in copies)
				{
					if (!patchedNames.Contains(pair.Key))
					{
						continue;
					}
					// Code that is itself patched now has been made again from its IL since, without the copy.
					List<string> into = pair.Value.Where(v => !v.Value && !patchedNames.Contains(v.Key)).Select(v => v.Key).ToList();
					if (into.Count == 0)
					{
						if (!pair.Value.IsEmpty)
						{
							intoDynamic++;
						}
						continue;
					}
					intoStatic++;
					places += into.Count;
					if (samples.Count < 25)
					{
						samples.Add(pair.Key + " (into " + into[0] + (into.Count > 1 ? " and " + (into.Count - 1) + " more" : "") + ")");
					}
				}
				Console.WriteLine("[port17] inline watch (" + when + "): " + events + " inlinings seen; of " + patchedNames.Count + " patched methods, " + intoStatic + " had been copied into compiled game or mod code (" + places
					+ " copies, where their patches are skipped), " + intoDynamic + " only into Harmony's replacement methods");
				foreach (string sample in samples)
				{
					Console.WriteLine("[port17] inline watch:   " + sample);
				}
				ReportStale(when, list);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] inline watch: no report: " + e.Message);
			}
		}

		/// <summary>
		/// The copies of patched methods in the replacements Harmony runs now (named "type.name_PatchN" by Harmony's
		/// MethodPatcherTools.CreateDynamicMethod, N the method's patch count): each was made before the method was patched
		/// and the replacement was not made again since. Apart, and not inlined: it names Harmony's types.
		/// </summary>
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		private static void ReportStale(string when, List<MethodBase> patched)
		{
			// The replacements by name, each with how many parameters it has and the method tables of their types: the
			// runtime writes a replacement's signature with those ("int32  (pMT: 00007FF9...,int32)"), so overloads differ.
			var live = new Dictionary<string, List<(int Arity, HashSet<string> Tables)>>();
			var patchedArities = new HashSet<string>();
			MethodInfo getPatchInfo = HarmonyLib.AccessTools.Method("HarmonyLib.HarmonySharedState:GetPatchInfo");
			foreach (MethodBase method in patched)
			{
				if (!(getPatchInfo?.Invoke(null, new object[] { method }) is HarmonyLib.PatchInfo info))
				{
					continue;
				}
				var types = new List<Type>();
				if (!method.IsStatic && method.DeclaringType != null)
				{
					types.Add(method.DeclaringType);
				}
				types.AddRange(method.GetParameters().Select(p => p.ParameterType));
				var tables = new HashSet<string>(types.Select(t => t.IsByRef ? t.GetElementType() : t).Where(t => !t.IsPrimitive).Select(t => Hex(t.TypeHandle.Value.ToString("X"))));
				string name = ((method.DeclaringType?.FullName ?? "GLOBALTYPE") + "." + method.Name + "_Patch" + info.VersionCount).Replace("<>", "");
				if (!live.TryGetValue(name, out var overloads))
				{
					live[name] = overloads = new List<(int, HashSet<string>)>();
				}
				overloads.Add((types.Count, tables));
				patchedArities.Add(method.DeclaringType?.FullName + "::" + method.Name + "/" + types.Count);
			}
			var stale = new Dictionary<string, HashSet<string>>();
			foreach (var (into, signature, inlinee, arity) in intoReplacements)
			{
				if (!patchedArities.Contains(inlinee + "/" + arity) || !live.TryGetValue(into, out var overloads))
				{
					continue;
				}
				int intoArity = Arity(signature);
				List<string> seen = Tables(signature);
				if (!overloads.Exists(o => o.Arity == intoArity && seen.TrueForAll(o.Tables.Contains)))
				{
					continue;
				}
				if (!stale.TryGetValue(inlinee, out HashSet<string> places))
				{
					stale[inlinee] = places = new HashSet<string>();
				}
				// One place per replacement: the name with the signature.
				places.Add(into + "|" + signature);
			}
			Console.WriteLine("[port17] inline watch (" + when + "): " + stale.Count + " patched methods still copied into " + stale.Values.Sum(p => p.Count) + " of the " + live.Values.Sum(o => o.Count)
				+ " replacements Harmony runs (made before they were patched and not made again: their patches are skipped there)");
			foreach (KeyValuePair<string, HashSet<string>> pair in stale.Take(25))
			{
				string first = pair.Value.First();
				Console.WriteLine("[port17] inline watch:   " + pair.Key + " (in " + first.Substring(0, first.IndexOf('|')) + (pair.Value.Count > 1 ? " and " + (pair.Value.Count - 1) + " more" : "") + ")");
			}
		}

		/// <summary>How many parameters a signature as the runtime writes it has ("instance bool  (class Verse.Thing,int32)"), "this" counted.</summary>
		private static int Arity(string signature)
		{
			int open = signature.IndexOf('('), close = signature.LastIndexOf(')');
			int count = signature.StartsWith("instance", StringComparison.Ordinal) ? 1 : 0;
			if (open < 0 || close <= open + 1)
			{
				return count;
			}
			count++;
			int depth = 0;
			for (int i = open + 1; i < close; i++)
			{
				char c = signature[i];
				if (c == '<' || c == '[' || c == '(')
				{
					depth++;
				}
				else if (c == '>' || c == ']' || c == ')')
				{
					depth--;
				}
				else if (c == ',' && depth == 0)
				{
					count++;
				}
			}
			return count;
		}

		/// <summary>The method tables a signature names ("pMT: 00007FF9DE28EC38", or with 0x where the runtime's printf writes that).</summary>
		private static List<string> Tables(string signature)
		{
			var found = new List<string>();
			int i = 0;
			while ((i = signature.IndexOf("pMT: ", i, StringComparison.Ordinal)) >= 0)
			{
				i += 5;
				if (i + 1 < signature.Length && signature[i] == '0' && (signature[i + 1] == 'x' || signature[i + 1] == 'X'))
				{
					i += 2;
				}
				int end = i;
				while (end < signature.Length && char.IsAsciiHexDigit(signature[end]))
				{
					end++;
				}
				found.Add(Hex(signature.Substring(i, end - i)));
				i = end;
			}
			return found;
		}

		private static string Hex(string digits)
		{
			return digits.TrimStart('0').ToUpperInvariant();
		}
	}
}
