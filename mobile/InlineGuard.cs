using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace PortMobile
{
	/// <summary>
	/// Ludeon's runtime never copies a method of the game into the code Harmony makes for a patched method
	/// (WaylithClr.JitNoInlinePatchableIntoDynamicMethods, for Assembly-CSharp); the stock runtime does (inlining), and a
	/// patch put on that small method later is then skipped whenever the patched method runs. Two cases met:
	///   - the port's own patches go in at the main menu, after the mods' (Universal Turret Tweaker's patch of
	///     MapInterface.MapInterfaceUpdate held its own copy of DesignatorManager.DesignatorManagerUpdate: the cells a
	///     drag marked for the confirm button were not drawn);
	///   - a mod that patches only when it needs to (Map Preview patches Verse.Rand for its preview thread when a
	///     preview is made; Geological Landforms' patch of MapGenerator.GenerateContentsIntoMap, made at startup, had
	///     Rand.PopState in it: "Stack empty", no preview).
	/// So: every method the first time it is patched (Harmony's PatchFunctions.UpdateWrapper, which every patch goes
	/// through) is looked up among the methods patched before it. Those whose replacement may hold a copy of it get their
	/// replacement made again (by Harmony itself, with their patches as they are): a patched method is not copied into
	/// code compiled after it was patched. While the port puts its own patches in, this waits and is done once for all of
	/// them at the end. Each time, a replacement is made again only where PortHooks tells that it was made before the
	/// first patch of the method it may hold, or cannot tell (ReplacementCalls.MadeBefore): one made after it cannot hold
	/// a copy. That leaves some out for the port's batch and for the mods' patches made before the port; a method first
	/// patched while the game runs has every replacement made before its patch, and none is left out.
	///
	/// What a replacement may hold is what the stock JIT (.NET 10.0.12) copies into one, measured 2026-10-30 with its
	/// inlining events: what the replacement's code calls (Harmony's final instruction list, kept by PortHooks: the
	/// original as transpiled, and the prefixes, postfixes and finalizers, which are copied in too), what those call, and
	/// so on, 20 levels deep (DEFAULT_MAX_INLINE_DEPTH; ten were measured); a method of up to 128 IL bytes
	/// (JitExtDefaultPolicyMaxIL), of up to 1024 (JitExtDefaultPolicyMaxILProf) where the replacement is one straight run
	/// of code with few calls (a postfix on a getter), of any size where it asks for it (AggressiveInlining); never one
	/// with a catch, a NoInlining or synchronized one, nor one called virtually that the JIT cannot resolve from the code
	/// alone (where it knows the object's exact type it can: see below). A try/finally is copied in (a lock, a using, a
	/// foreach; GUI.ProcessPendingWindows into ImguiDispatch.Dispatch, below), and so are fault and filter blocks; a
	/// catch is not, even where the method asks for it ("has exception handling"). Until 2026-10-30 only the original's
	/// own code was looked at, two levels and 100 bytes deep: a postfix's call of Rand.Value (Vanilla Expanded), a
	/// transpiler's call, or Bombardment.TryDoExplosion -> FloatRange.RandomInRange -> Rand.Range -> Rand.Value were
	/// missed. The framework's code is not followed (it does not call the game), and on the PC its methods are copied
	/// into replacements as here. Measured on the PC's stock .NET 10.0.12 with the game, the 1049 methods the owner's 130
	/// mods patch (each given an empty postfix, in their order) and then the port's own targets: the old way left 22
	/// copies of patched methods in 22 replacements, this one none (PortHooks' InlineWatch, overload by overload),
	/// reading the code in 250-290 ms instead of 100. Started as on the phone (PortHooks' patch of Log.Warning first),
	/// making every replacement again made 835 (the old way 464) in 2.6-2.9 s; going by the order it makes 381, in
	/// 1.3-1.5 s (256 of 467 for the mods' patches before the port, 125 of 169 for the port's own), still leaving none.
	/// The Recorder's own count of Log.Warning, which takes a lock and so may be in 271 replacements, had put about 200
	/// more in the port's batch: all made after PortHooks' patch of Log.Warning, its first, so that going by the order
	/// leaves them as they are anyway; the Recorder now counts through that patch (with inlineGuardOrder=0, below, 636
	/// are made again).
	///
	/// A copy the JIT makes through a virtual call it resolves itself, knowing the object's exact type (an object of a
	/// sealed class, one made right there), is not looked for; none was found with the 130 mods' targets above, but their
	/// empty postfixes have none of the mods' own code. Making every replacement again took such a copy away by chance,
	/// wherever the replacement was made again for some method that is looked for; going by the order, a replacement made
	/// after the first patch of each method it may hold is left with it (measured on the PC: a mod's postfix calls a
	/// method another mod had patched before, and new Sealed().V(), which the JIT copies in; a third mod patches Sealed.V
	/// later). mobile.txt inlineGuardOrder=0 makes every one again as before, to tell whether that is why a mod's patch
	/// does nothing. Where such a method is first patched while the game runs, neither way takes the copy away.
	///
	/// The engine's own code is the other half. Its per-frame methods are compiled at full optimisation during the
	/// loading screens, before the port is there, and they may hold a copy of a small engine method the port patches
	/// (1.7.4979 made GUI.ProcessPendingWindows two calls in a try/finally, and ImguiDispatch.Dispatch took it in: the
	/// tap assist and the agent no longer saw the end of a frame). So, once, after the port's patches: every method of
	/// the engine's assemblies whose code calls one of the small engine methods the port patched is made again too
	/// (Harmony, with an empty postfix).
	/// </summary>
	internal static class InlineGuard
	{
		private static readonly object sync = new object();
		private static readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();
		// A method -> the patched methods whose replacement may hold a copy of it.
		private static readonly Dictionary<MethodBase, HashSet<MethodBase>> callers = new Dictionary<MethodBase, HashSet<MethodBase>>();
		// A method -> what its IL calls, and of that what the JIT can bind where it is called (see Callees).
		private static readonly Dictionary<MethodBase, (List<MethodBase> All, List<MethodBase> Direct)> calleesOf = new Dictionary<MethodBase, (List<MethodBase>, List<MethodBase>)>();
		// A method -> its IL size for the copy limits; 0 for one that asks to be copied, -1 for one the JIT never copies.
		private static readonly Dictionary<MethodBase, int> sizes = new Dictionary<MethodBase, int>();
		private static readonly Dictionary<Assembly, bool> framework = new Dictionary<Assembly, bool>();
		private static readonly Dictionary<(Module, int), MethodBase> resolved = new Dictionary<(Module, int), MethodBase>();
		private static readonly Dictionary<short, OpCode> oneByte = new Dictionary<short, OpCode>();
		private static readonly Dictionary<short, OpCode> twoByte = new Dictionary<short, OpCode>();
		private static HashSet<MethodBase> deferred = new HashSet<MethodBase>();
		private static MethodInfo updateWrapper, getPatchInfo;
		[ThreadStatic] private static int rebuilding;
		private static int rebuiltInAll;
		// Whether the order in which the replacements were made is gone by (ReplacementCalls.MadeBefore); where PortHooks
		// does not tell it (an older one) or mobile.txt inlineGuardOrder=0, every replacement that may hold a newly patched
		// method is made again.
		private static bool ordered;

		// The copy limits of the stock JIT (see the class notes): IL bytes into a replacement, and into one without jumps
		// and with at most this many calls (measured: with nine calls a 308-byte method was copied in, with ten it was not;
		// twelve leaves a margin); levels.
		private const int MaxInlineSize = 128, MaxInlineSizeStraight = 1024, MaxStraightCalls = 12, MaxInlineDepth = 20;

		/// <summary>First thing at the port's start: what is patched so far, and the watch on every patch from now on.</summary>
		public static void Start(Harmony harmony)
		{
			var clock = Stopwatch.StartNew();
			try
			{
				Type functions = AccessTools.TypeByName("HarmonyLib.PatchFunctions");
				Type shared = AccessTools.TypeByName("HarmonyLib.HarmonySharedState");
				updateWrapper = AccessTools.Method(functions, "UpdateWrapper");
				getPatchInfo = AccessTools.Method(shared, "GetPatchInfo");
				if (updateWrapper == null || getPatchInfo == null)
				{
					Console.WriteLine("[mobile] inline guard: this Harmony has no PatchFunctions.UpdateWrapper / HarmonySharedState.GetPatchInfo; left out");
					updateWrapper = null;
					return;
				}
				if (Settings.Get("inlineGuardTest", 0) == 1)
				{
					// As a mod would have done: GUI.ProcessPendingWindows patched before the port, by someone else.
					new Harmony("port17.test.mod").Patch(AccessTools.Method(typeof(UnityEngine.GUI), nameof(UnityEngine.GUI.ProcessPendingWindows)), postfix: new HarmonyMethod(typeof(InlineGuard), nameof(Nothing)));
					Console.WriteLine("[mobile] inline guard: test: GUI.ProcessPendingWindows patched first, as a mod would");
				}
				// mobile.txt inlineGuardOrder=0: every replacement that may hold a newly patched method is made again,
				// whichever came first, as before 2026-10-30 (for a mod's patch that seems to do nothing; class notes).
				if (Settings.Get("inlineGuardOrder", 1) == 1)
				{
					ordered = KnowsOrder();
				}
				else
				{
					Console.WriteLine("[mobile] inline guard: inlineGuardOrder=0: every replacement that may hold a newly patched method is made again, whichever came first");
				}
				int count = 0;
				lock (sync)
				{
					foreach (MethodBase method in Harmony.GetAllPatchedMethods())
					{
						if (patched.Add(method))
						{
							Index(method);
							count++;
						}
					}
				}
				long indexed = clock.ElapsedMilliseconds;
				harmony.Patch(updateWrapper, postfix: new HarmonyMethod(typeof(InlineGuard), nameof(AfterUpdateWrapper)));
				Console.WriteLine("[mobile] inline guard: " + count + " patched methods looked through, " + callers.Count + " methods their replacements may hold, " + sizes.Count + " read (" + indexed + " ms); watching new patches");
				// The mods patched before the port was there: one that patched a method and then a small method it calls
				// left the first one's replacement with its own copy of the second (Multiplayer: Map.ExposeData, then
				// MapFileCompressor.ExposeData, whose prefix makes the save's compressibility decider; hosting a game threw
				// for every rock and plant). Every patched method whose replacement may hold a patched method is made
				// again, once, unless PortHooks tells that the replacement came after that method's first patch.
				HashSet<MethodBase> early;
				lock (sync)
				{
					early = new HashSet<MethodBase>(patched.Where(m => callers.ContainsKey(m)));
				}
				Rebuild(early, "the patches made before the port", includeBatch: true);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] inline guard failed to start: " + e);
				updateWrapper = null;
			}
		}

		/// <summary>The port's patches are all in: the methods patched meanwhile have their callers made again, once.</summary>
		public static void EndOfStart()
		{
			if (updateWrapper == null)
			{
				return;
			}
			HashSet<MethodBase> batch;
			lock (sync)
			{
				batch = deferred;
				deferred = null;
			}
			// The port's small methods that a mod had patched first were not new to the watch and so not in the batch: on a
			// tester's vivo (35 mods, 2026-10-23) none of them was, the engine's interface dispatch kept its own copy of
			// GUI.ProcessPendingWindows without the port's postfix, and no list ever scrolled under a finger. Every method
			// the port has patched that may be copied somewhere is taken, whoever patched it first.
			int noted = batch.Count;
			// mobile.txt inlineGuardOwners=0: the batch alone, as before (to compare, with inlineGuardTest=1).
			foreach (MethodBase method in Settings.Get("inlineGuardOwners", 1) == 1 ? Harmony.GetAllPatchedMethods() : Enumerable.Empty<MethodBase>())
			{
				Patches info = Harmony.GetPatchInfo(method);
				if (info != null && info.Owners.Any(owner => owner.StartsWith("port17.mobile", StringComparison.Ordinal)))
				{
					lock (sync)
					{
						if (callers.ContainsKey(method) || Copyable(method, MaxInlineSize))
						{
							batch.Add(method);
						}
					}
				}
			}
			if (batch.Count > noted)
			{
				Console.WriteLine("[mobile] inline guard: " + (batch.Count - noted) + " small methods the port patched had been patched before it, by mods");
			}
			// The port's own replacements too: one made before the port patched a method it holds keeps its copy (the
			// Recorder patches WindowStack.Add and then WindowStack.TryRemove, which the replacement of Add was measured to hold).
			Rebuild(batch, "the port's patches", includeBatch: true);
			RebuildEngineCallers(batch);
		}

		// The engine's assemblies (not the game's: Assembly-CSharp is what mods patch, and its callers are left to the above).
		private static readonly HashSet<string> EngineAssemblies = new HashSet<string> { "RimWorld", "UnityStubs", "Waylith", "Waylith.UnityCompat" };

		private static void RebuildEngineCallers(HashSet<MethodBase> portPatched)
		{
			var clock = Stopwatch.StartNew();
			var targets = new HashSet<MethodBase>();
			lock (sync)
			{
				foreach (MethodBase method in portPatched)
				{
					// Normally compiled code takes methods of up to 128 IL bytes as well. Where the runtime's profile finds a
					// call hot it takes up to 1024 (measured); that is not followed here, it would make GUI.Button and the
					// other controls, called many times a frame, Harmony replacements too.
					if (EngineAssemblies.Contains(method.Module.Assembly.GetName().Name) && Copyable(method, MaxInlineSize))
					{
						targets.Add(method);
					}
				}
			}
			if (targets.Count == 0)
			{
				return;
			}
			var todo = new List<(MethodBase caller, MethodBase callee)>();
			int scanned = 0;
			lock (sync)
			{
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					if (!EngineAssemblies.Contains(assembly.GetName().Name))
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
						types = Array.FindAll(e.Types, t => t != null);
					}
					foreach (Type type in types)
					{
						if (type.IsGenericTypeDefinition || type.IsInterface)
						{
							continue;
						}
						const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
						foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
						{
							if (method.IsAbstract || method.IsGenericMethodDefinition || patched.Contains(method) || targets.Contains(method))
							{
								continue;
							}
							scanned++;
							foreach (MethodBase callee in Callees(method))
							{
								if (targets.Contains(callee))
								{
									todo.Add((method, callee));
									break;
								}
							}
						}
					}
				}
			}
			long looked = clock.ElapsedMilliseconds;
			var harmony = new Harmony("port17.inline");
			var nothing = new HarmonyMethod(typeof(InlineGuard), nameof(Nothing));
			int made = 0;
			rebuilding++;
			try
			{
				foreach (var (caller, callee) in todo)
				{
					try
					{
						harmony.Patch(caller, postfix: nothing);
						made++;
						Console.WriteLine("[mobile] inline guard: engine method " + Name(caller) + " made again (it calls " + Name(callee) + ")");
					}
					catch (Exception e)
					{
						Console.WriteLine("[mobile] inline guard: could not make engine method " + Name(caller) + " again: " + e.GetType().Name + ": " + e.Message);
					}
				}
			}
			finally
			{
				rebuilding--;
			}
			lock (sync)
			{
				foreach (var (caller, _) in todo)
				{
					// A replacement now: it may hold copies too.
					patched.Add(caller);
					Index(caller);
				}
			}
			Console.WriteLine("[mobile] inline guard: " + targets.Count + " small engine methods patched by the port; " + scanned + " engine methods looked through in " + looked + " ms, " + made + " of " + todo.Count + " that call them made again (" + clock.ElapsedMilliseconds + " ms in all)");
		}

		public static void Nothing()
		{
		}

		/// <summary>
		/// Postfix of Harmony's PatchFunctions.UpdateWrapper (every patch, every unpatch). Where someone's prefix skipped
		/// it, no replacement came back and the method is as it was: its first real patch is still new to the watch.
		/// </summary>
		public static void AfterUpdateWrapper(MethodBase original, MethodInfo __result)
		{
			if (original == null || __result == null || rebuilding > 0)
			{
				return;
			}
			HashSet<MethodBase> now;
			lock (sync)
			{
				// Its replacement was just made: what it may hold is looked up again (a new prefix or postfix calls more).
				Index(original);
				if (!patched.Add(original))
				{
					return;
				}
				if (deferred != null)
				{
					// While the port puts its patches in: noted for the end, where the engine's code is looked at too.
					if (callers.ContainsKey(original) || Copyable(original, MaxInlineSize))
					{
						deferred.Add(original);
					}
					return;
				}
				if (!callers.ContainsKey(original))
				{
					return;
				}
				now = new HashSet<MethodBase> { original };
			}
			Rebuild(now, null);
		}

		private static void Rebuild(HashSet<MethodBase> newlyPatched, string what, bool includeBatch = false)
		{
			var clock = Stopwatch.StartNew();
			var todo = new List<MethodBase>();
			// Replacements that may hold a newly patched method but were made after its first patch, and so cannot.
			var later = new HashSet<MethodBase>();
			lock (sync)
			{
				var seen = new HashSet<MethodBase>();
				foreach (MethodBase callee in newlyPatched)
				{
					if (callers.TryGetValue(callee, out HashSet<MethodBase> list))
					{
						foreach (MethodBase caller in list)
						{
							if ((includeBatch || !newlyPatched.Contains(caller)) && caller != callee && !seen.Contains(caller))
							{
								if (ordered && !MadeBefore(caller, callee))
								{
									later.Add(caller);
								}
								else
								{
									seen.Add(caller);
									todo.Add(caller);
								}
							}
						}
					}
				}
				later.ExceptWith(seen);
			}
			int made = 0;
			rebuilding++;
			try
			{
				foreach (MethodBase caller in todo)
				{
					try
					{
						object info = getPatchInfo.Invoke(null, new object[] { caller });
						if (info == null)
						{
							continue;
						}
						updateWrapper.Invoke(null, new object[] { caller, info });
						made++;
					}
					catch (Exception e)
					{
						Exception inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
						Console.WriteLine("[mobile] inline guard: could not make " + Name(caller) + " again: " + inner.GetType().Name + ": " + inner.Message);
					}
				}
			}
			finally
			{
				rebuilding--;
			}
			rebuiltInAll += made;
			if (todo.Count > 0 || what != null)
			{
				var names = new List<string>();
				foreach (MethodBase m in newlyPatched)
				{
					if (names.Count == 4)
					{
						names.Add("...");
						break;
					}
					names.Add(Name(m));
				}
				Console.WriteLine("[mobile] inline guard: " + (what ?? string.Join(", ", names)) + " newly patched (" + newlyPatched.Count + "); " + made + " of " + todo.Count + " patched methods that had it in them made again in " + clock.ElapsedMilliseconds + " ms (" + rebuiltInAll + " so far)"
					+ (later.Count > 0 ? "; " + later.Count + " made after its first patch left as they are" : ""));
			}
		}

		/// <summary>Whether PortHooks tells the order of the replacements: an older one has no ReplacementCalls.MadeBefore.</summary>
		private static bool KnowsOrder()
		{
			try
			{
				MadeBefore(null, null);
				return true;
			}
			catch (Exception)
			{
				Console.WriteLine("[mobile] inline guard: this PortHooks keeps no order of the replacements; every one that may hold a newly patched method is made again");
				return false;
			}
		}

		// Apart, and not inlined: with an older PortHooks the call fails where this is compiled, and only here.
		[MethodImpl(MethodImplOptions.NoInlining)]
		private static bool MadeBefore(MethodBase original, MethodBase method)
		{
			return PortHooks.ReplacementCalls.MadeBefore(original, method);
		}

		private static string Name(MethodBase method)
		{
			return method.DeclaringType?.FullName + "." + method.Name;
		}

		/// <summary>
		/// Notes a patched method under every method its replacement may hold a copy of (see the class notes): what the
		/// replacement calls, then level by level what the methods that may be copied in call.
		/// </summary>
		private static void Index(MethodBase method)
		{
			MethodBase[] calls = PortHooks.ReplacementCalls.Of(method, out bool jumps, out int count);
			// A replacement whose instruction list HarmonyCompat never saw (made by another copy of Harmony than the port's,
			// or from an empty list): the original's own code. (With an older PortHooks, which keeps no lists, this method
			// does not compile, and the guard's start fails.)
			IEnumerable<MethodBase> level = calls ?? (IEnumerable<MethodBase>)Callees(method, direct: true);
			int limit = !jumps && count <= MaxStraightCalls ? MaxInlineSizeStraight : MaxInlineSize;
			var seen = new HashSet<MethodBase>();
			for (int depth = 1; depth <= MaxInlineDepth; depth++)
			{
				var next = new List<MethodBase>();
				foreach (MethodBase callee in level)
				{
					if (!seen.Add(callee) || !Copyable(callee, limit))
					{
						continue;
					}
					Add(callee, method);
					if (depth < MaxInlineDepth && !Framework(callee))
					{
						next.AddRange(Callees(callee, direct: true));
					}
				}
				if (next.Count == 0)
				{
					break;
				}
				level = next;
			}
		}

		private static void Add(MethodBase callee, MethodBase caller)
		{
			if (!callers.TryGetValue(callee, out HashSet<MethodBase> set))
			{
				callers[callee] = set = new HashSet<MethodBase>();
			}
			set.Add(caller);
		}

		/// <summary>Whether the JIT may copy the method into code it compiles, where it takes methods of up to the given IL size.</summary>
		private static bool Copyable(MethodBase method, int limit)
		{
			if (!sizes.TryGetValue(method, out int size))
			{
				try
				{
					MethodImplAttributes flags = method.GetMethodImplementationFlags();
					const MethodImplAttributes never = MethodImplAttributes.NoInlining | MethodImplAttributes.Synchronized | MethodImplAttributes.NoOptimization;
					MethodBody body = method is DynamicMethod || method.IsAbstract || (flags & never) != 0 ? null : method.GetMethodBody();
					byte[] il = body?.GetILAsByteArray();
					// A catch keeps the JIT from copying a method in, even one that asks for it; finally, fault and filter blocks do not.
					size = il == null || body.ExceptionHandlingClauses.Any(clause => clause.Flags == ExceptionHandlingClauseOptions.Clause) ? -1 : (flags & MethodImplAttributes.AggressiveInlining) != 0 ? 0 : il.Length;
				}
				catch (Exception)
				{
					size = 0;
				}
				sizes[method] = size;
			}
			return size >= 0 && size <= limit;
		}

		/// <summary>The framework's assemblies, whose code is not followed (it does not call the game or the engine).</summary>
		private static bool Framework(MethodBase method)
		{
			Assembly assembly = method.Module.Assembly;
			if (!framework.TryGetValue(assembly, out bool known))
			{
				string name = assembly.GetName().Name ?? "";
				framework[assembly] = known = name == "mscorlib" || name == "netstandard" || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal);
			}
			return known;
		}

		/// <summary>
		/// The methods the IL of a method calls (call, callvirt, newobj, ldftn), cached; direct: only those the JIT can bind
		/// where they are called and so may copy in (call, newobj, or a callvirt of a method nothing can override).
		/// </summary>
		private static List<MethodBase> Callees(MethodBase method, bool direct = false)
		{
			if (calleesOf.TryGetValue(method, out var known))
			{
				return direct ? known.Direct : known.All;
			}
			var list = new List<MethodBase>();
			var bound = new List<MethodBase>();
			calleesOf[method] = (list, bound);
			byte[] il;
			try
			{
				il = method.GetMethodBody()?.GetILAsByteArray();
			}
			catch (Exception)
			{
				return direct ? bound : list;
			}
			if (il == null)
			{
				return direct ? bound : list;
			}
			if (oneByte.Count == 0)
			{
				foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
				{
					if (field.GetValue(null) is OpCode op)
					{
						(op.Size == 1 ? oneByte : twoByte)[op.Value] = op;
					}
				}
			}
			Module module = method.Module;
			Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
			Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
			int i = 0;
			while (i < il.Length)
			{
				OpCode op;
				if (il[i] == 0xFE && i + 1 < il.Length)
				{
					if (!twoByte.TryGetValue(unchecked((short)(0xFE00 | il[i + 1])), out op))
					{
						break;
					}
					i += 2;
				}
				else
				{
					if (!oneByte.TryGetValue(il[i], out op))
					{
						break;
					}
					i += 1;
				}
				switch (op.OperandType)
				{
					case OperandType.InlineNone:
						break;
					case OperandType.ShortInlineBrTarget:
					case OperandType.ShortInlineI:
					case OperandType.ShortInlineVar:
						i += 1;
						break;
					case OperandType.InlineVar:
						i += 2;
						break;
					case OperandType.InlineI8:
					case OperandType.InlineR:
						i += 8;
						break;
					case OperandType.InlineSwitch:
						if (i + 4 > il.Length)
						{
							i = il.Length;
							break;
						}
						i += 4 + 4 * BitConverter.ToInt32(il, i);
						break;
					case OperandType.InlineMethod:
						if (i + 4 > il.Length)
						{
							i = il.Length;
							break;
						}
						int token = BitConverter.ToInt32(il, i);
						i += 4;
						bool shared = typeArgs == null && methodArgs == null;
						if (!shared || !resolved.TryGetValue((module, token), out MethodBase callee))
						{
							try
							{
								callee = module.ResolveMethod(token, typeArgs, methodArgs);
							}
							catch (Exception)
							{
								callee = null;
							}
							// Generic arguments of the caller make the entry differ between callers; such a callee is looked up each time.
							if (shared)
							{
								resolved[(module, token)] = callee;
							}
						}
						if (callee != null)
						{
							list.Add(callee);
							if (op == OpCodes.Call || op == OpCodes.Newobj || (op == OpCodes.Callvirt && !Overridable(callee)))
							{
								bound.Add(callee);
							}
						}
						break;
					default:
						i += 4;
						break;
				}
			}
			return direct ? bound : list;
		}

		private static bool Overridable(MethodBase method)
		{
			return method.IsVirtual && !method.IsFinal && method.DeclaringType != null && !method.DeclaringType.IsSealed && !method.DeclaringType.IsValueType;
		}
	}
}
