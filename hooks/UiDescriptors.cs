using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// The descriptor sets of interface draws with a material. The engine's ImGuiMaterialRenderer takes one or two
	/// descriptor sets for every such draw from a pool per frame in flight, made for 512 sets (512 uniform buffers,
	/// 4096 image samplers, 2048 storage buffers) and emptied when the frame starts again; and a set it cannot have is
	/// an exception that ends the game: "vkAllocateDescriptorSets failed with error: VK_ERROR_OUT_OF_POOL_MEMORY" from
	/// the render thread (a tester's Xiaomi, Dimensity 9200+ / Mali-G715, three times in ten minutes, a few seconds
	/// after a tap in the inspect pane, 2026-10-21). Mali keeps to a pool's sizes exactly; other drivers may give more
	/// than was asked for, which would be why the PC does not end there.
	///   - the pool is made eight times as large (4096 sets: the interface would need that many material draws in one
	///     frame), the counts in CreateDescriptorPool multiplied;
	///   - should it still run out, the draws that find no set are left out of that frame, as the renderer does for a
	///     draw whose uniforms do not fit (TryBuildMaterialSet / TryBuildFrameSet answer false), instead of the game
	///     ending; the first time is logged.
	/// mobile.txt uiPoolTest=N makes the pool N sets and descriptors of each kind (to try running out).
	/// </summary>
	internal static class UiDescriptors
	{
		private const int Factor = 8;

		private static int testSize;
		private static long skipped;

		public static void Start()
		{
			try
			{
				Type renderer = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImGuiMaterialRenderer");
				MethodInfo create = renderer == null ? null : AccessTools.Method(renderer, "CreateDescriptorPool");
				MethodInfo material = renderer == null ? null : AccessTools.Method(renderer, "TryBuildMaterialSet");
				MethodInfo frame = renderer == null ? null : AccessTools.Method(renderer, "TryBuildFrameSet");
				if (create == null || material == null || frame == null)
				{
					Console.WriteLine("[port17] ui descriptors: the material renderer is not as expected; left as it is");
					return;
				}
				int.TryParse(Textures.Setting("uiPoolTest"), out testSize);
				var harmony = new Harmony("port17.uidescriptors");
				harmony.Patch(create, transpiler: new HarmonyMethod(typeof(UiDescriptors), nameof(LargerPool)));
				harmony.Patch(material, finalizer: new HarmonyMethod(typeof(UiDescriptors), nameof(NoSetLeft)));
				harmony.Patch(frame, finalizer: new HarmonyMethod(typeof(UiDescriptors), nameof(NoSetLeft)));
				Console.WriteLine("[port17] ui descriptors: the material draws' pool " + (testSize > 0 ? "made " + testSize + " (uiPoolTest)" : Factor + " times as large") + "; a draw that finds no set is left out of its frame");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] ui descriptors: not installed: " + e.Message);
			}
		}

		/// <summary>The pool's sizes (the constants 512, 4096 and 2048 of CreateDescriptorPool) multiplied.</summary>
		public static IEnumerable<CodeInstruction> LargerPool(IEnumerable<CodeInstruction> instructions)
		{
			int changed = 0;
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int value && (value == 512 || value == 4096 || value == 2048))
				{
					instruction.operand = testSize > 0 ? testSize : value * Factor;
					changed++;
				}
				yield return instruction;
			}
			if (changed != 4)
			{
				Console.WriteLine("[port17] ui descriptors: " + changed + " pool sizes changed (4 expected)");
			}
		}

		/// <summary>A pool run out (or too fragmented): the draw is left out of this frame.</summary>
		public static Exception NoSetLeft(Exception __exception, ref bool __result)
		{
			if (__exception == null)
			{
				return null;
			}
			string message = __exception.Message ?? "";
			if (!message.Contains("OUT_OF_POOL_MEMORY") && !message.Contains("FRAGMENTED_POOL"))
			{
				return __exception;
			}
			__result = false;
			if (Interlocked.Increment(ref skipped) == 1)
			{
				Console.WriteLine("[port17] ui descriptors: the pool ran out in a frame; the draws past it are left out (" + message + ")");
			}
			return null;
		}
	}
}
