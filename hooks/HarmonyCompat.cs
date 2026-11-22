using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// Makes the code of a patched method acceptable to this runtime where a transpiler written for Mono left it
	/// unbalanced: values still on the evaluation stack at a jump whose target expects fewer.
	///
	/// Two of the owner's mods do this. MinifyEverything turns "ldfld category; ldc.i4.3; bne.un L" into
	/// "ldfld category; ldc.i4.3; br L" (two values left); Humanoid Alien Races wraps ApplyBirthOutcome in a loop,
	/// "call ApplyBirthOutcome; br Loop" (the returned Thing left). Mono's JIT does not compare stack depths where
	/// control flow joins, so there the extra values are simply lost and the patches have worked for years; this
	/// runtime's JIT refuses the whole method ("Common Language Runtime detected an invalid program"), which takes the
	/// patch, or the mod that applies it in its constructor, down.
	///
	/// Harmony's instruction list for the replacement method passes through here just before it is emitted
	/// (patch/Patch.cs adds the call at the start of HarmonyLib.MethodCreatorTools.EmitCodes). The stack depth is
	/// followed along every path; where an unconditional jump or the fall into a jump target arrives with more values
	/// than the least any path brings there, "pop" instructions are put in front. A list that is balanced, which is
	/// nearly all of them, is left exactly as it is; so is one that cannot be followed with certainty (calli, a method
	/// with a variable argument list, a path that would take more than the stack holds) or cannot be repaired this way
	/// (a conditional jump with extra values).
	///
	/// Two more places where the runtime wants the stack empty and the PC's runtime does not insist (its JIT has the
	/// switches LaxILStack and LaxRetInTry): the start of a try block, and the return of a method that returns nothing.
	/// Vanilla Gravship Expanded (2026-10-21) turns the call "DrawFieldEdges(cells, 2900)" in
	/// GravshipLandingMarker.DrawAt into a single "pop", so the cells stay on the stack into the try block of the
	/// foreach that follows; here the whole mod failed in its constructor ("invalid program"). The values are popped
	/// in front of the try block (outside it) or of the return.
	///
	/// Harmony 3 follows the stack itself before the JIT sees the method (DynamicMethodBodyWriter.MaximumStack throws
	/// "Inconsistent stack depth" where two paths bring different depths), on the PC as here. Two more shapes get past
	/// it that the PC's JIT runs and this one refuses (measured 2026-10-30 with the same Harmony on both runtimes, the
	/// PC's own runtime files, and transpilers that leave each shape); here they run as they do there:
	///   - values under the return value of a method that returns something, as many on every path: the PC returns the
	///     top one. Here it is put aside in a local while the others are popped. Not where the return comes right after
	///     a call: the JIT takes "call; ret" for a tail call and wants nothing else on the stack, and the PC refuses the
	///     method then too ("Stack should be empty after tailcall").
	///   - a jump out of a try block or a catch handler: Harmony turns a transpiler's "ret" there into a "br" to the end
	///     of the original, and the PC's JIT runs such a jump as a leave (WaylithClr's JitLaxRetInTry; finally blocks
	///     run). With nothing on the stack it becomes a leave here, and so does one in code no path reaches (the JIT
	///     checks every jump, reached or not). A jump that carries a value out (the return value of a "ret" in a try) is
	///     left as it is: the PC runs some such methods, the value lost on that path (the type's default comes back), and
	///     refuses others ("stack underflow"), with or without a finally, by how the code around the jump is laid out (21
	///     shapes measured: 11 run, 10 refused), not by a rule that could be followed here.
	/// Where the original's returns meet, a method that returns something keeps the return value on top and what a
	/// transpiler left over under it, so paths that arrive there with different depths get no pops: the PC's Harmony
	/// refuses that method, and here Harmony is left to refuse it too. Until 2026-10-30 the pops took the return value
	/// itself, and the patched method returned whatever had been under it.
	/// </summary>
	internal static class HarmonyCompat
	{
		private const int Unvisited = int.MinValue;

		public static void Normalize(object creator, List<CodeInstruction> codes)
		{
			if (codes == null || codes.Count == 0)
			{
				return;
			}
			try
			{
				Balance(creator, codes);
				LeaveRegions(creator, codes);
			}
			finally
			{
				// Also where a repair could not be looked at: InlineGuard goes by what the replacement calls.
				ReplacementCalls.Note(OriginalOf(creator), codes);
			}
		}

		/// <summary>The pops in front of jumps, try blocks and returns, and under a return value, that leave the stack as this runtime wants it.</summary>
		private static void Balance(object creator, List<CodeInstruction> codes)
		{
			Dictionary<Label, int> labels = LabelsOf(codes);
			int[] depth = Follow(codes, labels, out bool certain);
			if (!certain)
			{
				return;
			}
			// (index to insert before, how many pops, what they take over from the instruction there: Fall nothing, Jump its
			// labels and block starts, TryEntry its labels only: the try block itself starts after them, UnderReturn as Jump)
			var fixes = new List<(int At, int Count, int Kind)>();
			bool repairable = true, returnValueAtStake = false;
			Type returnType = ReturnTypeOf(creator);
			bool? voidReturn = returnType == null ? (bool?)null : returnType == typeof(void);
			// Where Harmony's jumps for the original's returns meet: in a method that returns something, the value on top
			// there is the return value.
			int returnJoin = voidReturn == false ? ReturnJoin(codes) : -1;
			for (int i = 0; i < codes.Count && repairable; i++)
			{
				if (depth[i] == Unvisited)
				{
					continue;
				}
				CodeInstruction code = codes[i];
				Effect(code, out int pop, out int push);
				int after = depth[i] - pop + push;
				FlowControl flow = code.opcode.FlowControl;
				bool leave = code.opcode == OpCodes.Leave || code.opcode == OpCodes.Leave_S;
				if (flow == FlowControl.Branch && !leave && code.operand is Label target && labels.TryGetValue(target, out int t) && depth[t] != Unvisited)
				{
					if (after > depth[t] && t == returnJoin)
					{
						returnValueAtStake = true;
						repairable = false;
					}
					else if (after > depth[t])
					{
						fixes.Add((i, after - depth[t], Jump));
					}
				}
				else if (flow == FlowControl.Cond_Branch)
				{
					foreach (Label conditional in Targets(code))
					{
						if (labels.TryGetValue(conditional, out int c) && depth[c] != Unvisited && after > depth[c])
						{
							repairable = false; // would need a landing place of its own; not seen in any mod so far
						}
					}
				}
				if (FallsThrough(code) && i + 1 < codes.Count && depth[i + 1] != Unvisited && !BeginsHandler(codes[i + 1]) && !Ends(code))
				{
					if (after > depth[i + 1] && i + 1 == returnJoin)
					{
						returnValueAtStake = true;
						repairable = false;
					}
					else if (after > depth[i + 1])
					{
						fixes.Add((i + 1, after - depth[i + 1], Fall));
					}
				}
				if (depth[i] > 0 && BeginsTry(code))
				{
					// A try block entered with values on the stack (the least any path brings; the fixes above take the rest).
					fixes.Add((i, depth[i], TryEntry));
				}
				else if (depth[i] > 0 && code.opcode == OpCodes.Ret && voidReturn == true)
				{
					fixes.Add((i, depth[i], Jump));
				}
				else if (depth[i] > 1 && code.opcode == OpCodes.Ret && voidReturn == false && !CallJustBefore(codes, i))
				{
					// As many values under the return value on every path (Harmony has checked that much): the PC returns the top one.
					fixes.Add((i, depth[i] - 1, UnderReturn));
				}
			}
			if (returnValueAtStake)
			{
				Console.WriteLine("[port17] a patch of " + NameOf(creator) + " reaches the end of the original with more values on some paths than on others; left as it is, for Harmony to refuse as on the PC (pops there would take the return value)");
			}
			if (fixes.Count == 0 || !repairable)
			{
				return;
			}
			LocalBuilder aside = null;
			if (fixes.Exists(f => f.Kind == UnderReturn) && (aside = DeclareLocal(creator, returnType)) == null)
			{
				return;
			}
			var repaired = new List<CodeInstruction>(codes);
			// From the end backwards, so that the places stay right; where two fall on the same place, the pops that take
			// the jump's labels go in first and the ones for the fall into it end up in front of them.
			// (A try entry's pops come after a jump's or a fall's at the same place: those bring the depth down to the least
			// first, then these empty the stack before the block starts.)
			fixes.Sort((a, b) => a.At != b.At ? b.At.CompareTo(a.At) : InsertOrder(a.Kind).CompareTo(InsertOrder(b.Kind)));
			int added = 0;
			foreach ((int at, int count, int kind) in fixes)
			{
				var inserted = new List<CodeInstruction>();
				if (kind == UnderReturn)
				{
					inserted.Add(new CodeInstruction(OpCodes.Stloc, aside));
				}
				for (int k = 0; k < count; k++)
				{
					inserted.Add(new CodeInstruction(OpCodes.Pop));
				}
				if (kind == UnderReturn)
				{
					inserted.Add(new CodeInstruction(OpCodes.Ldloc, aside));
				}
				added += count;
				if (kind != Fall)
				{
					// Every path that reaches the jump has to pass the pops: its labels (and, for a jump or a return, its block
					// starts) move to the first of them. A try block starts after its pops. The instruction there is replaced by
					// a copy without them: the original list keeps its own as they were.
					CodeInstruction there = repaired[at], copy = new CodeInstruction(there);
					inserted[0].labels.AddRange(there.labels);
					copy.labels.Clear();
					if (kind != TryEntry)
					{
						inserted[0].blocks.AddRange(there.blocks.FindAll(b => b.blockType != ExceptionBlockType.EndExceptionBlock));
						copy.blocks.RemoveAll(b => b.blockType != ExceptionBlockType.EndExceptionBlock);
					}
					repaired[at] = copy;
				}
				repaired.InsertRange(at, inserted);
			}
			// Only a list that is balanced afterwards replaces the original.
			if (!Balanced(repaired, voidReturn))
			{
				return;
			}
			codes.Clear();
			codes.AddRange(repaired);
			Console.WriteLine("[port17] a patch of " + NameOf(creator) + " left values on the stack at " + fixes.Count + " place(s) (jumps, try blocks, returns); " + added + " pop(s) added (Mono and the PC's runtime drop them, this runtime refuses the method)");
		}

		private const int Fall = 0, Jump = 1, TryEntry = 2, UnderReturn = 3;

		/// <summary>Where several fixes fall on one place: those taking labels are inserted first (they end up last), a try entry's before all.</summary>
		private static int InsertOrder(int kind)
		{
			return kind == TryEntry ? 0 : kind == Fall ? 2 : 1;
		}

		/// <summary>
		/// Jumps out of a try block or a catch handler with nothing on the stack become leaves (see the class notes). A
		/// conditional one jumps to a leave of its own, put right after it, and the way on is jumped to around that.
		/// </summary>
		private static void LeaveRegions(object creator, List<CodeInstruction> codes)
		{
			if (!codes.Exists(BeginsTry))
			{
				return;
			}
			List<(int Parent, ExceptionBlockType Kind)> parts = PartsOf(codes, out int[] part);
			if (parts == null)
			{
				return;
			}
			Dictionary<Label, int> labels = LabelsOf(codes);
			var exits = new List<int>();
			for (int i = 0; i < codes.Count; i++)
			{
				OpCode op = codes[i].opcode;
				bool jump = (op.FlowControl == FlowControl.Branch && op != OpCodes.Leave && op != OpCodes.Leave_S) || (op.FlowControl == FlowControl.Cond_Branch && op != OpCodes.Switch);
				if (jump && codes[i].operand is Label target && labels.TryGetValue(target, out int t) && Leaves(parts, part[i], part[t]))
				{
					exits.Add(i);
				}
			}
			if (exits.Count == 0)
			{
				return;
			}
			int[] depth = Follow(codes, labels, out bool certain);
			if (!certain)
			{
				return;
			}
			ILGenerator il = null;
			var repaired = new List<CodeInstruction>(codes);
			int made = 0;
			// From the end backwards, so that the places stay right.
			for (int e = exits.Count - 1; e >= 0; e--)
			{
				int i = exits[e];
				CodeInstruction code = codes[i];
				int t = labels[(Label)code.operand];
				Effect(code, out int pop, out int push);
				// A jump in code no path reaches becomes a leave too, whatever it would carry: the JIT refuses the method for
				// it all the same (the PC's runs it), and it never runs.
				if (depth[i] != Unvisited && (depth[i] - pop + push != 0 || depth[t] != 0))
				{
					continue;
				}
				if (code.opcode.FlowControl == FlowControl.Branch)
				{
					repaired[i] = new CodeInstruction(OpCodes.Leave, code.operand) { labels = new List<Label>(code.labels), blocks = new List<ExceptionBlock>(code.blocks) };
					made++;
					continue;
				}
				// "brX target" becomes "brX pad; br on; pad: leave target; on: ...", all inside the part the jump is in.
				if (i + 1 >= codes.Count || part[i + 1] != part[i] || Ends(code) || BeginsTry(codes[i + 1]) || BeginsHandler(codes[i + 1]) || (il = il ?? IlOf(creator)) == null)
				{
					continue;
				}
				Label pad = il.DefineLabel(), on = il.DefineLabel();
				var leave = new CodeInstruction(OpCodes.Leave, code.operand);
				leave.labels.Add(pad);
				var next = new CodeInstruction(repaired[i + 1]);
				next.labels.Add(on);
				repaired[i] = new CodeInstruction(code.opcode, pad) { labels = new List<Label>(code.labels), blocks = new List<ExceptionBlock>(code.blocks) };
				repaired[i + 1] = next;
				repaired.InsertRange(i + 1, new[] { new CodeInstruction(OpCodes.Br, on), leave });
				made++;
			}
			if (made == 0 || !Balanced(repaired, ReturnTypeOf(creator) is Type type ? type == typeof(void) : (bool?)null))
			{
				return;
			}
			codes.Clear();
			codes.AddRange(repaired);
			Console.WriteLine("[port17] a patch of " + NameOf(creator) + " jumps out of a try block or catch handler at " + made + " place(s); made leaves (the PC's runtime runs such jumps as leaves, this runtime refuses the method)");
		}

		/// <summary>
		/// The parts of the try statements in the list (a try block or one of its handlers: the part around it, -1 for
		/// none, and the marker that began it) and, for each instruction, the innermost part it is in; null when the
		/// markers do not nest.
		/// </summary>
		private static List<(int Parent, ExceptionBlockType Kind)> PartsOf(List<CodeInstruction> codes, out int[] part)
		{
			var parts = new List<(int Parent, ExceptionBlockType Kind)>();
			part = new int[codes.Count];
			var open = new Stack<int>();
			int current = -1;
			for (int i = 0; i < codes.Count; i++)
			{
				foreach (ExceptionBlock block in codes[i].blocks)
				{
					if (block.blockType == ExceptionBlockType.BeginExceptionBlock)
					{
						parts.Add((current, block.blockType));
					}
					else if (block.blockType != ExceptionBlockType.EndExceptionBlock && open.Count > 0)
					{
						// A handler: the next part of the same statement, around which is what was around the block before it.
						parts.Add((parts[open.Pop()].Parent, block.blockType));
					}
					else if (block.blockType != ExceptionBlockType.EndExceptionBlock)
					{
						return null;
					}
					else
					{
						continue;
					}
					current = parts.Count - 1;
					open.Push(current);
				}
				part[i] = current;
				// The end of a statement is marked on the last instruction of its last handler.
				foreach (ExceptionBlock block in codes[i].blocks)
				{
					if (block.blockType == ExceptionBlockType.EndExceptionBlock)
					{
						if (open.Count == 0)
						{
							return null;
						}
						current = parts[open.Pop()].Parent;
					}
				}
			}
			return open.Count == 0 ? parts : null;
		}

		/// <summary>Whether a jump from one part to another leaves try blocks and catch handlers only (what a leave can leave), and at least one.</summary>
		private static bool Leaves(List<(int Parent, ExceptionBlockType Kind)> parts, int from, int to)
		{
			if (from == to)
			{
				return false;
			}
			for (int p = from; p != to; p = parts[p].Parent)
			{
				// -1 before reaching the target's part: the jump would go into a part it is not in (not a leave's).
				if (p < 0 || (parts[p].Kind != ExceptionBlockType.BeginExceptionBlock && parts[p].Kind != ExceptionBlockType.BeginCatchBlock))
				{
					return false;
				}
			}
			return true;
		}

		private static Dictionary<Label, int> LabelsOf(List<CodeInstruction> codes)
		{
			var labels = new Dictionary<Label, int>();
			for (int i = 0; i < codes.Count; i++)
			{
				if (codes[i].labels != null)
				{
					foreach (Label label in codes[i].labels)
					{
						labels[label] = i;
					}
				}
			}
			return labels;
		}

		/// <summary>Where Harmony's jumps for the original's returns land: the instruction after its note "end original" (CreateReplacement, CleanupCodes); -1 when there is none.</summary>
		private static int ReturnJoin(List<CodeInstruction> codes)
		{
			for (int i = 0; i + 1 < codes.Count; i++)
			{
				if (codes[i].opcode == OpCodes.Nop && codes[i].operand is string note && note == "end original")
				{
					return i + 1;
				}
			}
			return -1;
		}

		/// <summary>Whether the instruction emitted right before this one is a call (Harmony's notes, nops with a text, are not emitted).</summary>
		private static bool CallJustBefore(List<CodeInstruction> codes, int index)
		{
			if (BeginsTry(codes[index]) || BeginsHandler(codes[index]))
			{
				return false;
			}
			for (int i = index - 1; i >= 0; i--)
			{
				CodeInstruction code = codes[i];
				if (code.opcode == OpCodes.Nop && code.operand is string)
				{
					continue;
				}
				return !Ends(code) && (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Calli);
			}
			return false;
		}

		private static bool BeginsTry(CodeInstruction code)
		{
			return code.blocks != null && code.blocks.Exists(b => b.blockType == ExceptionBlockType.BeginExceptionBlock);
		}

		/// <summary>
		/// What the method being made returns (void for a constructor; null when that cannot be told): Harmony's
		/// MethodCreator.config.returnType, which is also right for the helper methods of inner patches, where the
		/// original is the patched method around them.
		/// </summary>
		private static Type ReturnTypeOf(object creator)
		{
			if (ConfigField(creator, "returnType") is Type type)
			{
				return type;
			}
			MethodBase original = OriginalOf(creator);
			if (original is MethodInfo info)
			{
				return info.ReturnType;
			}
			return original is ConstructorInfo ? typeof(void) : null;
		}

		/// <summary>The method being patched: Harmony's MethodCreator.config.original.</summary>
		private static MethodBase OriginalOf(object creator)
		{
			return ConfigField(creator, "original") as MethodBase;
		}

		/// <summary>The generator of the method being made (MethodCreator.config.il), for labels and locals of its own.</summary>
		private static ILGenerator IlOf(object creator)
		{
			return ConfigField(creator, "il") as ILGenerator;
		}

		private static object ConfigField(object creator, string name)
		{
			try
			{
				object config = creator?.GetType().GetField("config", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(creator);
				return config?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(config);
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>A new local of the given type in the method being made; null when there can be none.</summary>
		private static LocalBuilder DeclareLocal(object creator, Type type)
		{
			try
			{
				return type == null || type == typeof(void) ? null : IlOf(creator)?.DeclareLocal(type);
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static string NameOf(object creator)
		{
			try
			{
				foreach (FieldInfo field in creator.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
				{
					object value = field.GetValue(creator);
					if (value is MethodBase method)
					{
						return method.DeclaringType?.FullName + "." + method.Name;
					}
					if (value != null && field.Name == "config")
					{
						foreach (FieldInfo inner in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
						{
							if (inner.GetValue(value) is MethodBase original)
							{
								return original.DeclaringType?.FullName + "." + original.Name;
							}
						}
					}
				}
			}
			catch (Exception)
			{
			}
			return "a method";
		}

		private static bool BeginsHandler(CodeInstruction code, out int entryDepth)
		{
			entryDepth = 0;
			if (code.blocks == null)
			{
				return false;
			}
			foreach (ExceptionBlock block in code.blocks)
			{
				switch (block.blockType)
				{
					case ExceptionBlockType.BeginCatchBlock:
					case ExceptionBlockType.BeginExceptFilterBlock:
						entryDepth = 1; // the exception
						return true;
					case ExceptionBlockType.BeginFaultBlock:
					case ExceptionBlockType.BeginFinallyBlock:
						entryDepth = 0;
						return true;
				}
			}
			return false;
		}

		private static bool BeginsHandler(CodeInstruction code)
		{
			return BeginsHandler(code, out int _);
		}

		private static bool Ends(CodeInstruction code)
		{
			return code.blocks != null && code.blocks.Exists(b => b.blockType == ExceptionBlockType.EndExceptionBlock);
		}

		private static bool FallsThrough(CodeInstruction code)
		{
			switch (code.opcode.FlowControl)
			{
				case FlowControl.Branch:
				case FlowControl.Return:
				case FlowControl.Throw:
					return false;
				default:
					return true;
			}
		}

		private static IEnumerable<Label> Targets(CodeInstruction code)
		{
			if (code.operand is Label one)
			{
				yield return one;
			}
			else if (code.operand is Label[] many)
			{
				foreach (Label label in many)
				{
					yield return label;
				}
			}
		}

		/// <summary>What an instruction takes from and puts on the stack; false when that cannot be told.</summary>
		private static bool Effect(CodeInstruction code, out int pop, out int push)
		{
			pop = 0;
			push = 0;
			OpCode op = code.opcode;
			if (op == OpCodes.Calli)
			{
				return false;
			}
			if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
			{
				if (!(code.operand is MethodBase method) || (method.CallingConvention & CallingConventions.VarArgs) != 0)
				{
					return false;
				}
				pop = method.GetParameters().Length;
				if (op == OpCodes.Newobj)
				{
					push = 1;
				}
				else
				{
					if (!method.IsStatic)
					{
						pop++;
					}
					push = method is MethodInfo info && info.ReturnType != typeof(void) ? 1 : 0;
				}
				return true;
			}
			if (op == OpCodes.Ret)
			{
				return true; // whatever is there is the return value; nothing follows
			}
			switch (op.StackBehaviourPop)
			{
				case StackBehaviour.Pop0: pop = 0; break;
				case StackBehaviour.Pop1:
				case StackBehaviour.Popi:
				case StackBehaviour.Popref: pop = 1; break;
				case StackBehaviour.Pop1_pop1:
				case StackBehaviour.Popi_pop1:
				case StackBehaviour.Popi_popi:
				case StackBehaviour.Popi_popi8:
				case StackBehaviour.Popi_popr4:
				case StackBehaviour.Popi_popr8:
				case StackBehaviour.Popref_pop1:
				case StackBehaviour.Popref_popi: pop = 2; break;
				case StackBehaviour.Popi_popi_popi:
				case StackBehaviour.Popref_popi_popi:
				case StackBehaviour.Popref_popi_popi8:
				case StackBehaviour.Popref_popi_popr4:
				case StackBehaviour.Popref_popi_popr8:
				case StackBehaviour.Popref_popi_popref:
				case StackBehaviour.Popref_popi_pop1: pop = 3; break;
				default: return false; // Varpop of something that is not a call
			}
			switch (op.StackBehaviourPush)
			{
				case StackBehaviour.Push0: push = 0; break;
				case StackBehaviour.Push1:
				case StackBehaviour.Pushi:
				case StackBehaviour.Pushi8:
				case StackBehaviour.Pushr4:
				case StackBehaviour.Pushr8:
				case StackBehaviour.Pushref: push = 1; break;
				case StackBehaviour.Push1_push1: push = 2; break;
				default: return false;
			}
			return true;
		}

		/// <summary>
		/// The least stack depth with which each instruction is reached (Unvisited for code no path leads to), starting
		/// with an empty stack at the first instruction and with the exception, or nothing, at the start of each handler.
		/// certain is false when the list cannot be followed.
		/// </summary>
		private static int[] Follow(List<CodeInstruction> codes, Dictionary<Label, int> labels, out bool certain)
		{
			certain = true;
			var depth = new int[codes.Count];
			for (int i = 0; i < depth.Length; i++)
			{
				depth[i] = Unvisited;
			}
			var work = new Stack<(int Index, int Depth)>();
			for (int i = codes.Count - 1; i >= 0; i--)
			{
				if (BeginsHandler(codes[i], out int entry))
				{
					work.Push((i, entry));
				}
			}
			work.Push((0, 0));
			int steps = 0;
			while (work.Count > 0)
			{
				(int index, int d) = work.Pop();
				if (index < 0 || index >= codes.Count)
				{
					continue;
				}
				if (++steps > 4000000)
				{
					certain = false;
					return depth;
				}
				CodeInstruction code = codes[index];
				if (BeginsHandler(code, out int fixedEntry))
				{
					d = fixedEntry;
				}
				if (depth[index] != Unvisited && depth[index] <= d)
				{
					continue;
				}
				depth[index] = d;
				if (!Effect(code, out int pop, out int push) || d < pop)
				{
					certain = false;
					return depth;
				}
				int after = d - pop + push;
				OpCode op = code.opcode;
				bool leave = op == OpCodes.Leave || op == OpCodes.Leave_S;
				switch (op.FlowControl)
				{
					case FlowControl.Branch:
						foreach (Label target in Targets(code))
						{
							if (labels.TryGetValue(target, out int t))
							{
								work.Push((t, leave ? 0 : after));
							}
						}
						break;
					case FlowControl.Cond_Branch:
						foreach (Label target in Targets(code))
						{
							if (labels.TryGetValue(target, out int t))
							{
								work.Push((t, after));
							}
						}
						break;
				}
				if (Ends(code))
				{
					// The block is left through the jump the emitter adds here: what follows starts with an empty stack.
					work.Push((index + 1, 0));
				}
				else if (FallsThrough(code) && index + 1 < codes.Count && !BeginsHandler(codes[index + 1]))
				{
					work.Push((index + 1, after));
				}
			}
			return depth;
		}

		/// <summary>
		/// Whether the list can be followed and every jump and every fall into the next instruction arrives with the depth
		/// found for its target; try blocks start with an empty stack, returns have nothing or only the return value on it.
		/// </summary>
		private static bool Balanced(List<CodeInstruction> codes, bool? voidReturn)
		{
			Dictionary<Label, int> labels = LabelsOf(codes);
			int[] depth = Follow(codes, labels, out bool certain);
			if (!certain)
			{
				return false;
			}
			for (int i = 0; i < codes.Count; i++)
			{
				if (depth[i] == Unvisited)
				{
					continue;
				}
				CodeInstruction code = codes[i];
				if (depth[i] != 0 && (BeginsTry(code) || (voidReturn == true && code.opcode == OpCodes.Ret)))
				{
					return false;
				}
				if (depth[i] > 1 && voidReturn == false && code.opcode == OpCodes.Ret)
				{
					return false;
				}
				if (!Effect(code, out int pop, out int push))
				{
					return false;
				}
				int after = depth[i] - pop + push;
				bool leave = code.opcode == OpCodes.Leave || code.opcode == OpCodes.Leave_S;
				FlowControl flow = code.opcode.FlowControl;
				if ((flow == FlowControl.Branch && !leave) || flow == FlowControl.Cond_Branch)
				{
					foreach (Label target in Targets(code))
					{
						if (labels.TryGetValue(target, out int t) && depth[t] != Unvisited && depth[t] != after)
						{
							return false;
						}
					}
				}
				if (FallsThrough(code) && !Ends(code) && i + 1 < codes.Count && !BeginsHandler(codes[i + 1]) && depth[i + 1] != Unvisited && depth[i + 1] != after)
				{
					return false;
				}
			}
			return true;
		}
	}

	/// <summary>
	/// A mod that takes every owner's patches off (Harmony.UnpatchAll() without an owner, Unpatch with the owner "*";
	/// RimHUD does it when its own start fails) takes the port's off too, through PatchInfo.Remove with the owner "*":
	/// textures and fonts for this phone's GPU, the mod loading, the touch layer. On the PC those are the engine's own
	/// code, not patches, and stay. So where every owner's patches go, those of the port's owners ("port17.*") stay.
	/// </summary>
	internal static class PortPatches
	{
		private static bool told;

		/// <summary>From the startup hook, before any mod runs.</summary>
		public static void Keep(Harmony harmony)
		{
			try
			{
				MethodInfo remove = AccessTools.Method(typeof(PatchInfo), "Remove", new[] { typeof(string), typeof(Patch[]) });
				if (remove == null)
				{
					Console.WriteLine("[port17] this Harmony has no PatchInfo.Remove(owner, patches); a mod that takes every owner's patches off takes the port's too");
					return;
				}
				harmony.Patch(remove, prefix: new HarmonyMethod(typeof(PortPatches), nameof(BeforeRemove)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] the port's patches are not kept from a mod's unpatch-all: " + e.Message);
			}
		}

		/// <summary>Prefix of PatchInfo.Remove(owner, current), which takes one owner's patches out of a list, or all for "*".</summary>
		public static bool BeforeRemove(string owner, Patch[] current, ref Patch[] __result)
		{
			if (owner != "*" || current == null)
			{
				return true;
			}
			__result = Array.FindAll(current, patch => patch.owner != null && patch.owner.StartsWith("port17.", StringComparison.Ordinal));
			if (__result.Length > 0 && !told)
			{
				told = true;
				Console.WriteLine("[port17] a mod took every owner's Harmony patches off; the port's own stay (on the PC its changes are the engine's code)");
			}
			return false;
		}
	}

	/// <summary>
	/// What the code of each replacement Harmony made calls, from its final instruction list (HarmonyCompat.Normalize
	/// sees every one just before it is emitted): the original as transpiled, the prefixes, postfixes and finalizers, the
	/// helper methods of inner patches. PortMobile's InlineGuard looks here for what the JIT may have copied into a
	/// replacement. Kept from the startup hook on, so that the mods' patches made before PortMobile loads are here too.
	///
	/// Also the order in which the replacements were made. A method is not copied into code compiled after it was
	/// patched, so a replacement made after the first patch of a method cannot hold a copy of it, and InlineGuard need
	/// not make it again for that method: of the 360 replacements of the owner's 130 mods that may hold a method patched
	/// before the port (measured on the PC), only 256 were made before that method's first patch. A replacement is taken
	/// as made when its list is seen here, before the JIT compiles it, and a method as patched once Harmony has put its
	/// first replacement in (a postfix of PatchFunctions.UpdateWrapper, put on by the startup hook before any other
	/// patch). That postfix also tells which list became the replacement in use: a patch that fails after its list was
	/// seen (a transpiler's code the JIT refuses) leaves the replacement before it.
	/// </summary>
	public static class ReplacementCalls
	{
		private sealed class Calls
		{
			public readonly HashSet<MethodBase> Methods = new HashSet<MethodBase>();
			public bool Jumps;
			public int Count;
			// On the clock below: when the list of the last replacement made for the method was seen, when that of the
			// replacement in use was, and when its first replacement was put in; 0 where it is not known.
			public long Listed, Made, Patched;
		}

		private static readonly Dictionary<MethodBase, Calls> byOriginal = new Dictionary<MethodBase, Calls>();
		private static long clock;

		/// <summary>From the startup hook, before the first patch: the replacements that are put in, in order.</summary>
		public static void Watch(Harmony harmony)
		{
			try
			{
				MethodInfo update = AccessTools.Method(AccessTools.TypeByName("HarmonyLib.PatchFunctions"), "UpdateWrapper");
				if (update == null)
				{
					Console.WriteLine("[port17] this Harmony has no PatchFunctions.UpdateWrapper; the order of its replacements is not kept");
					return;
				}
				harmony.Patch(update, postfix: new HarmonyMethod(typeof(ReplacementCalls), nameof(AfterUpdateWrapper)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] the order of Harmony's replacements is not kept: " + e.Message);
			}
		}

		/// <summary>
		/// Postfix of Harmony's PatchFunctions.UpdateWrapper: the replacement it returns is in. A patch that failed does
		/// not get here; where someone's prefix skipped UpdateWrapper, none came back and the method is as it was (it may
		/// still be copied into code compiled now, and is not taken as patched).
		/// </summary>
		public static void AfterUpdateWrapper(MethodBase original, MethodInfo __result)
		{
			if (original == null || __result == null)
			{
				return;
			}
			lock (byOriginal)
			{
				if (!byOriginal.TryGetValue(original, out Calls calls))
				{
					byOriginal[original] = calls = new Calls();
				}
				calls.Made = calls.Listed;
				if (calls.Patched == 0)
				{
					calls.Patched = ++clock;
				}
			}
		}

		/// <summary>
		/// Whether the replacement in use for a patched method may have been made before the other method was first
		/// patched, and so may hold a copy of it: its list was seen before, or the order of the two is not known.
		/// </summary>
		public static bool MadeBefore(MethodBase original, MethodBase method)
		{
			if (original == null || method == null)
			{
				return true;
			}
			lock (byOriginal)
			{
				return !byOriginal.TryGetValue(original, out Calls made) || !byOriginal.TryGetValue(method, out Calls patched) || made.Made == 0 || patched.Patched == 0 || made.Made < patched.Patched;
			}
		}

		internal static void Note(MethodBase original, List<CodeInstruction> codes)
		{
			if (original == null)
			{
				return;
			}
			lock (byOriginal)
			{
				// Every list made for the method adds to what it may call (a helper's list comes before the replacement's own);
				// only the replacement's own list, the one with Harmony's note "end original", says what it looks like.
				if (!byOriginal.TryGetValue(original, out Calls calls))
				{
					byOriginal[original] = calls = new Calls();
				}
				bool own = false, jumps = false;
				int count = 0;
				foreach (CodeInstruction code in codes)
				{
					OpCode op = code.opcode;
					own |= op == OpCodes.Nop && code.operand is string note && note == "end original";
					jumps |= op.FlowControl == FlowControl.Branch || op.FlowControl == FlowControl.Cond_Branch || (code.blocks != null && code.blocks.Count > 0);
					if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj || op == OpCodes.Calli)
					{
						count++;
						// What the JIT can bind where it is called (call, newobj, or a callvirt of a method nothing overrides).
						if (code.operand is MethodBase method && !(method is DynamicMethod) && (op != OpCodes.Callvirt || !method.IsVirtual || method.IsFinal || method.DeclaringType == null || method.DeclaringType.IsSealed || method.DeclaringType.IsValueType))
						{
							calls.Methods.Add(method);
						}
					}
				}
				if (own)
				{
					calls.Jumps = jumps;
					calls.Count = count;
					calls.Listed = ++clock;
				}
			}
		}

		/// <summary>
		/// What the replacement of a patched method calls that the JIT can copy in, null when its list was not seen; jumps:
		/// whether it has jumps or try blocks, calls: how many calls it makes (the JIT takes bigger methods into a
		/// replacement without jumps and with few calls).
		/// </summary>
		public static MethodBase[] Of(MethodBase original, out bool jumps, out int calls)
		{
			lock (byOriginal)
			{
				if (original == null || !byOriginal.TryGetValue(original, out Calls known))
				{
					jumps = true;
					calls = int.MaxValue;
					return null;
				}
				jumps = known.Jumps;
				calls = known.Count;
				var methods = new MethodBase[known.Methods.Count];
				known.Methods.CopyTo(methods);
				return methods;
			}
		}
	}
}
