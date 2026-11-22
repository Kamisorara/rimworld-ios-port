using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.VulkanApi;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Verse;
using Waylith.Rendering;

namespace PortMobile
{
	/// <summary>
	/// For 记录问题 with things selected: what each one is drawn with (graphic, material, texture, the static atlas tile
	/// that replaces it), and pictures of it as the GPU holds it, mip level by level (marks/mark-…-N-atlas-L.png and
	/// -texture-L.png): a thing that a tester sees vanish at one zoom shows which level of which image is empty.
	/// The images are read back from the GPU (BC1/BC3/BC7, ASTC 4x4 and RGBA8; levels of 4 texels and more), cut to
	/// the tile with a margin, decoded on the processor. Up to 3 things, 4 levels each.
	/// </summary>
	internal static class MarkThings
	{
		[DllImport("portastc")] private static extern int port_astc_decode(byte[] blocks, UIntPtr blocksLength, int width, int height, int srgb, byte[] rgba);

		private const int Margin = 8, Levels = 4;

		public static string Describe(string marks, string name)
		{
			var text = new StringBuilder();
			int count = 0;
			foreach (object selected in Find.Selector.SelectedObjectsListForReading)
			{
				if (!(selected is Thing thing) || count >= 3)
				{
					continue;
				}
				count++;
				try
				{
					DescribeThing(thing, marks, name + "-" + count, text);
				}
				catch (Exception e)
				{
					text.AppendLine("  (" + e.GetType().Name + ": " + e.Message + ")");
				}
			}
			return text.ToString();
		}

		private static void DescribeThing(Thing thing, string marks, string name, StringBuilder text)
		{
			// A pawn is drawn by its renderer (body, head, apparel); its Graphic is the def's placeholder, UI/Misc/BadTexture,
			// which read as a texture missing in a mark (2026-10-25, a tester's colonist that was drawn whole). The body is described.
			Graphic graphic = thing is Pawn pawn && pawn.Drawer?.renderer?.BodyGraphic != null ? pawn.Drawer.renderer.BodyGraphic : thing.Graphic;
			Graphic inner = graphic?.ExtractInnerGraphicFor(thing);
			UnityEngine.Material material = inner?.MatAt(thing.Rotation, thing);
			var texture = material?.mainTexture as UnityEngine.Texture2D;
			text.AppendLine(thing.LabelCap + " (" + thing.def.defName + ", " + thing.def.category + ", stack " + thing.stackCount + "): " + graphic?.GetType().Name
				+ (inner != graphic ? " -> " + inner?.GetType().Name : "") + " '" + inner?.path + "'");
			text.AppendLine("  material '" + material?.name + "', shader " + material?.shader?.name + ", color " + material?.color);
			text.AppendLine("  texture " + Describe(texture));
			if (texture != null && GlobalTextureAtlasManager.TryGetStaticTile(thing.def.category.ToAtlasGroup(), texture, out StaticTextureAtlasTile tile))
			{
				UnityEngine.Texture2D page = tile.atlas.ColorTexture;
				text.AppendLine("  drawn from the static atlas: uv " + tile.uvRect + ", page " + Describe(page));
				Dump(page, tile.uvRect, Path.Combine(marks, name + "-atlas"), text);
			}
			else
			{
				text.AppendLine("  not in a static atlas (drawn with its own texture)");
			}
			Dump(texture, new Rect(0f, 0f, 1f, 1f), Path.Combine(marks, name + "-texture"), text);
		}

		private static string Describe(UnityEngine.Texture2D texture)
		{
			if (texture == null)
			{
				return "none";
			}
			Waylith.Rendering.Texture2D core = texture.CoreTexture;
			GpuTextureAllocation gpu = core?.GpuResource;
			return "'" + texture.name + "' " + texture.width + "x" + texture.height + ", " + (core?.SourcePath ?? "no file") + ", CPU format " + PortHooks.EngineTypes.CpuFormatOf(core)
				+ (gpu == null ? ", not on the GPU now" : ", GPU " + gpu.Format + " with " + gpu.MipCount + " levels") + ", filter " + texture.filterMode + ", mip bias " + texture.mipMapBias;
		}

		/// <summary>The part uv of each of the first levels, as PNG files path-L.png.</summary>
		private static void Dump(UnityEngine.Texture2D texture, Rect uv, string path, StringBuilder text)
		{
			GpuTextureAllocation gpu = texture?.CoreTexture?.GpuResource;
			if (gpu == null)
			{
				return;
			}
			int blockBytes = BlockBytes(gpu.Format), pixelBytes = gpu.Format == VkFormat.R8G8B8A8UNorm || gpu.Format == VkFormat.R8G8B8A8SRGB ? 4 : 0;
			if (blockBytes == 0 && pixelBytes == 0)
			{
				text.AppendLine("  (" + gpu.Format + ": not read back here)");
				return;
			}
			int width = texture.width, height = texture.height, levels = Math.Min(gpu.MipCount, Levels);
			while (levels > 1 && (Math.Max(1, width >> (levels - 1)) < 4 || Math.Max(1, height >> (levels - 1)) < 4))
			{
				levels--;
			}
			// The readback sizes its buffer as texels times bytes per texel and copies the blocks tightly packed: one byte
			// per texel is the size of 16-byte blocks, and twice what 8-byte ones need.
			int readBytes = pixelBytes > 0 ? 4 : 1;
			byte[][] data = GpuTextureService.ReadbackTexture2DLevels(gpu, width, height, levels, readBytes);
			if (data == null)
			{
				text.AppendLine("  (the readback failed)");
				return;
			}
			var line = new StringBuilder("  " + Path.GetFileName(path) + ":");
			for (int level = 0; level < data.Length; level++)
			{
				int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
				// uv has its origin at the bottom (Unity); the image's first row is its top, as StaticTextureAtlas draws it.
				int x0 = Math.Max(0, (int)Math.Floor(uv.x * w) - Margin), x1 = Math.Min(w, (int)Math.Ceiling((uv.x + uv.width) * w) + Margin);
				int y0 = Math.Max(0, (int)Math.Floor((1f - uv.y - uv.height) * h) - Margin), y1 = Math.Min(h, (int)Math.Ceiling((1f - uv.y) * h) + Margin);
				if (pixelBytes == 0)
				{
					x0 &= ~3;
					y0 &= ~3;
					x1 = Math.Min(w, (x1 + 3) & ~3);
					y1 = Math.Min(h, (y1 + 3) & ~3);
				}
				int cw = x1 - x0, ch = y1 - y0;
				if (cw <= 0 || ch <= 0)
				{
					continue;
				}
				byte[] rgba = pixelBytes > 0 ? Crop(data[level], w, x0, y0, cw, ch) : DecodeBlocks(data[level], gpu.Format, w, x0, y0, cw, ch);
				long alpha = 0;
				for (int i = 3; i < rgba.Length; i += 4)
				{
					alpha += rgba[i];
				}
				File.WriteAllBytes(path + "-" + level + ".png", ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)cw, (uint)ch));
				line.Append(" L" + level + " " + cw + "x" + ch + " alpha " + (alpha * 100 / (255L * cw * ch)) + "%");
			}
			text.AppendLine(line.ToString());
		}

		private static int BlockBytes(VkFormat format)
		{
			switch (format)
			{
			case VkFormat.Bc1RGBAUNormBlock:
			case VkFormat.Bc1RGBASRGBBlock:
			case VkFormat.Bc1RGBUNormBlock:
			case VkFormat.Bc1RGBSRGBBlock:
				return 8;
			case VkFormat.Bc3UNormBlock:
			case VkFormat.Bc3SRGBBlock:
			case VkFormat.Bc7UNormBlock:
			case VkFormat.Bc7SRGBBlock:
			case VkFormat.Astc4X4UNormBlock:
			case VkFormat.Astc4X4SRGBBlock:
				return 16;
			default:
				return 0;
			}
		}

		private static byte[] Crop(byte[] texels, int width, int x0, int y0, int cw, int ch)
		{
			var rgba = new byte[cw * ch * 4];
			for (int y = 0; y < ch; y++)
			{
				Buffer.BlockCopy(texels, ((y0 + y) * width + x0) * 4, rgba, y * cw * 4, cw * 4);
			}
			return rgba;
		}

		private static byte[] DecodeBlocks(byte[] blocks, VkFormat format, int width, int x0, int y0, int cw, int ch)
		{
			int blocksX = (width + 3) / 4, bytes = BlockBytes(format);
			var rgba = new byte[cw * ch * 4];
			var texels = new byte[64];
			var alpha = new byte[64];
			bool astc = format == VkFormat.Astc4X4UNormBlock || format == VkFormat.Astc4X4SRGBBlock;
			var one = astc ? new byte[16] : null;
			for (int by = y0 / 4; by < (y0 + ch) / 4; by++)
			{
				for (int bx = x0 / 4; bx < (x0 + cw) / 4; bx++)
				{
					var block = new ReadOnlySpan<byte>(blocks, (by * blocksX + bx) * bytes, bytes);
					if (astc)
					{
						block.CopyTo(one);
						port_astc_decode(one, (UIntPtr)16, 4, 4, format == VkFormat.Astc4X4SRGBBlock ? 1 : 0, texels);
					}
					else if (bytes == 8)
					{
						PortHooks.EngineTypes.DecodeBc1Block(block, texels);
					}
					else if (format == VkFormat.Bc7UNormBlock || format == VkFormat.Bc7SRGBBlock)
					{
						PortHooks.EngineTypes.DecodeBc7Block(block, texels);
					}
					else
					{
						PortHooks.EngineTypes.DecodeBc1Block(block.Slice(8), texels);
						PortHooks.EngineTypes.DecodeBc4Block(block.Slice(0, 8), alpha);
						for (int i = 0; i < 16; i++)
						{
							texels[i * 4 + 3] = alpha[i * 4];
						}
					}
					for (int y = 0; y < 4; y++)
					{
						Buffer.BlockCopy(texels, y * 16, rgba, ((by * 4 + y - y0) * cw + bx * 4 - x0) * 4, 16);
					}
				}
			}
			return rgba;
		}
	}
}
