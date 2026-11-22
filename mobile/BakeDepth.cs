using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using HarmonyLib;
using Waylith.Graphics;

namespace PortMobile
{
	/// <summary>
	/// One depth buffer for all the textures of one size that cameras render into.
	///
	/// Whatever a camera renders into a texture (the atlases the pawns are drawn into, every portrait, what mods bake)
	/// goes through the engine's CameraTextureBaker, and that gives every such texture a depth buffer of its own, kept
	/// for as long as the texture lives. But its render pass clears the depth buffer when it starts and does not keep
	/// it when it ends, and every such rendering is finished before the next begins: the buffers are scratch space.
	/// Measured: 80 MiB for the four pawn atlases and 11 MiB for a hundred portraits without mods; with 106 mods 388 MiB
	/// (a 6144 pawn atlas: 181 MiB of depth; 120 baked textures of 512: 147 MiB; portraits: 41 MiB).
	/// The two calls in CreateTarget that allocate and register the depth image, and the two in DestroyTarget that
	/// unregister and free it, are redirected here: targets of the same size and format get the same image, which is
	/// freed with the last of them. Each target still has its own view of it and its own framebuffer.
	/// mobile.txt: sharedBakeDepth=0 leaves the engine as it is.
	/// </summary>
	internal static class BakeDepth
	{
		private struct Shared
		{
			public ImageAllocationInfo Info;
			public int Users;
		}

		private static readonly object gate = new object();
		private static readonly Dictionary<(int, int, int), Shared> bySize = new Dictionary<(int, int, int), Shared>();
		private static readonly Dictionary<long, (int, int, int)> byImage = new Dictionary<long, (int, int, int)>();
		private static int redirected;

		public static void Start(Harmony harmony)
		{
			if (Settings.Get("sharedBakeDepth", 1) == 0)
			{
				return;
			}
			try
			{
				Type baker = AccessTools.TypeByName("RimWorldWaylith.Rendering.CameraTextureBaker");
				MethodInfo create = AccessTools.Method(baker, "CreateTarget"), destroy = AccessTools.Method(baker, "DestroyTarget");
				if (create == null || destroy == null)
				{
					Console.WriteLine("[mobile] bake depth: the engine's baker is not as expected, left alone");
					return;
				}
				HarmonyMethod transpiler = new HarmonyMethod(typeof(BakeDepth), nameof(Redirect));
				harmony.Patch(create, transpiler: transpiler);
				harmony.Patch(destroy, transpiler: transpiler);
				Console.WriteLine("[mobile] bake depth: one depth buffer per size for rendering into textures (" + redirected + " calls redirected)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] bake depth not installed: " + e.Message);
			}
		}

		public static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo method)
				{
					ParameterInfo[] parameters = method.GetParameters();
					string replacement = null;
					if (method.DeclaringType == typeof(VmaAllocator) && method.Name == "AllocateImage" && parameters.Length == 3)
					{
						replacement = nameof(Allocate);
					}
					else if (method.DeclaringType == typeof(VmaAllocationExtensions) && method.Name == "AllocateImageChecked" && parameters.Length == 7)
					{
						// 1.7.4982: the allocation that reclaims, relaxes and retries when memory runs short.
						replacement = nameof(AllocateChecked);
					}
					else if (method.DeclaringType == typeof(VmaAllocator) && method.Name == "FreeAllocation" && parameters.Length == 1 && parameters[0].ParameterType.GetElementType() == typeof(ImageAllocationInfo))
					{
						replacement = nameof(Free);
					}
					else if (method.DeclaringType == typeof(GpuMemoryTracker) && method.Name == "Add" && parameters.Length == 4 && parameters[1].ParameterType.GetElementType() == typeof(ImageAllocationInfo))
					{
						replacement = nameof(Track);
					}
					else if (method.DeclaringType == typeof(GpuMemoryTracker) && method.Name == "Remove" && parameters.Length == 2 && parameters[1].ParameterType.GetElementType() == typeof(ImageAllocationInfo))
					{
						replacement = nameof(Untrack);
					}
					if (replacement != null)
					{
						instruction.opcode = OpCodes.Call;
						instruction.operand = AccessTools.Method(typeof(BakeDepth), replacement);
						redirected++;
					}
				}
				yield return instruction;
			}
		}

		private static long Handle(ref ImageAllocationInfo info)
		{
			return (long)info.Image.VkHandle;
		}

		/// <summary>In place of VmaAllocator.AllocateImage in CreateTarget.</summary>
		public static ImageAllocationInfo Allocate(VmaAllocator allocator, ref string name, ref VkImageCreateInfo createInfo, ref AllocationInfo.CreateInfo allocateInfo)
		{
			(int, int, int) key = (createInfo.Extent.Width, createInfo.Extent.Height, (int)createInfo.Format);
			lock (gate)
			{
				if (bySize.TryGetValue(key, out Shared shared))
				{
					shared.Users++;
					bySize[key] = shared;
					return shared.Info;
				}
				string label = "CameraBake.SharedDepth";
				shared = new Shared { Info = allocator.AllocateImage(in label, in createInfo, in allocateInfo), Users = 1 };
				bySize[key] = shared;
				byImage[Handle(ref shared.Info)] = key;
				return shared.Info;
			}
		}

		/// <summary>In place of VmaAllocationExtensions.AllocateImageChecked in CreateTarget (from 1.7.4982); the shared image is allocated by it too.</summary>
		public static ImageAllocationInfo AllocateChecked(VmaAllocator allocator, string name, ref VkImageCreateInfo createInfo, ref AllocationInfo.CreateInfo allocateInfo, uint memoryTypeBits, bool anyMemoryOnRefusal, bool reclaimOnRefusal)
		{
			(int, int, int) key = (createInfo.Extent.Width, createInfo.Extent.Height, (int)createInfo.Format);
			lock (gate)
			{
				if (bySize.TryGetValue(key, out Shared shared))
				{
					shared.Users++;
					bySize[key] = shared;
					return shared.Info;
				}
				shared = new Shared { Info = allocator.AllocateImageChecked("CameraBake.SharedDepth", in createInfo, in allocateInfo, memoryTypeBits, anyMemoryOnRefusal, reclaimOnRefusal), Users = 1 };
				bySize[key] = shared;
				byImage[Handle(ref shared.Info)] = key;
				return shared.Info;
			}
		}

		/// <summary>In place of GpuMemoryTracker.Add in CreateTarget: the image is registered once, under a name that says what it is.</summary>
		public static void Track(GpuMemoryCategory category, ref ImageAllocationInfo info, string label, string name)
		{
			lock (gate)
			{
				if (SharedOf(ref info, out (int, int, int) key, out Shared shared))
				{
					if (shared.Users == 1)
					{
						GpuMemoryTracker.Add(category, in info, label, "CameraBake.SharedDepth " + key.Item1 + "x" + key.Item2);
					}
					return;
				}
			}
			GpuMemoryTracker.Add(category, in info, label, name);
		}

		/// <summary>In place of GpuMemoryTracker.Remove in DestroyTarget.</summary>
		public static void Untrack(GpuMemoryCategory category, ref ImageAllocationInfo info)
		{
			lock (gate)
			{
				if (SharedOf(ref info, out _, out Shared shared))
				{
					if (shared.Users == 1)
					{
						GpuMemoryTracker.Remove(category, in info);
					}
					return;
				}
			}
			GpuMemoryTracker.Remove(category, in info);
		}

		/// <summary>
		/// Whether the image is a shared depth image, and which. A handle can be left behind pointing at a size that is no
		/// longer shared (an image the engine let go another way, its handle then given to a new image): MP's reload of the
		/// game freed the pawn atlases in Root.Start and Untrack threw KeyNotFoundException for (2048, 2048, 130). Such a
		/// stale entry is dropped and the image taken as an ordinary one.
		/// </summary>
		private static bool SharedOf(ref ImageAllocationInfo info, out (int, int, int) key, out Shared shared)
		{
			long handle = Handle(ref info);
			shared = default;
			if (!byImage.TryGetValue(handle, out key))
			{
				return false;
			}
			if (bySize.TryGetValue(key, out shared) && Handle(ref shared.Info) == handle)
			{
				return true;
			}
			byImage.Remove(handle);
			return false;
		}

		/// <summary>In place of VmaAllocator.FreeAllocation in DestroyTarget: freed with its last user.</summary>
		public static void Free(VmaAllocator allocator, ref ImageAllocationInfo info)
		{
			lock (gate)
			{
				long handle = Handle(ref info);
				if (SharedOf(ref info, out (int, int, int) key, out Shared shared))
				{
					if (--shared.Users > 0)
					{
						bySize[key] = shared;
						return;
					}
					bySize.Remove(key);
					byImage.Remove(handle);
				}
			}
			allocator.FreeAllocation(in info);
		}
	}
}
