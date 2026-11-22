using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Brutal.VulkanApi;
using HarmonyLib;
using Verse;
using Waylith.Graphics;
using Waylith.Rendering;

namespace PortMobile
{
	/// <summary>
	/// Every static atlas page checked once, level by level, and a level that lost what the level above it shows made
	/// again from that level.
	///
	/// A tester's Adreno 720 (vivo, Snapdragon 7 Gen 3) had an Item page of 8192x4096 whose level 1 was empty while
	/// levels 0, 2 and 3 were whole (记录问题 with the thing selected: MarkThings): everything on that page vanished at
	/// the zoom that samples level 1, the closest one, and came back further out. The page came from the engine's atlas
	/// cache, so it stayed that way at every start; the OnePlus with the same mods makes the same page correctly. Which
	/// step failed there (the mip chain the engine computes for a drawn page, its BC7 encoding on the GPU, or the cache)
	/// is not known yet: the log says whether a broken page was made in this run or came from the cache.
	///
	/// When the game has loaded (the atlases are baked by then): each page is read back from the GPU (BC7, ASTC 4x4 or
	/// RGBA8), and at 128x64 points spread over it every level is compared with the one above: a level is broken when,
	/// of the points that the level above covers (alpha over half), more than 5% are empty in it (alpha under 1/16).
	/// A broken level is made again on the processor from the level above (decoded, halved, encoded as the page is:
	/// the engine's CPU BC7 encoder or astcenc), the page's image replaced and read back once more for the log, and the
	/// cache entry written again, so the next start finds it whole. A page found whole is noted by its cache key
	/// (TextureCache/atlas-checked.txt) and not read again. On the OnePlus, 23 pages (8192x4096 and smaller): 0.6 s
	/// for BC7, 0.9 s for ASTC, once; a level of 4096x2048 made again in 0.3 s (BC7) / 0.5 s (ASTC).
	/// mobile.txt: atlasCheck=0 leaves it out; atlasCheckTest=Item empties level 1 of the first Item page first, to
	/// see the repair work (atlasCheckTestCut=1 also cuts level 0 off below its middle). On some GPUs (Verify) pages are
	/// also compared with their cache files, which mends a broken level 0 as well.
	/// </summary>
	internal static class AtlasCheck
	{
		[DllImport("portastc")] private static extern int port_astc_decode(byte[] blocks, UIntPtr blocksLength, int width, int height, int srgb, byte[] rgba);

		private const int SamplesX = 128, SamplesY = 64;
		private static byte[][] readback;

		/// <summary>
		/// GPUs on which atlas pages have gone wrong in the GPU's memory after a check found them whole (a tester's Adreno 725,
		/// 2026-10-23: the Plant page from the cache, checked whole in an earlier run, had level 0 cut off below a line,
		/// level 1 empty and level 2 in part: trees drawn in part or not at all; earlier an Adreno 720's Item page). On these
		/// every page is compared with its cache file at every start, after every bake and after a game has loaded, and
		/// put up again from the file where they differ. mobile.txt atlasVerify=1 / 0 switches it on / off for any GPU.
		/// </summary>
		private static bool Verify
		{
			get
			{
				if (verify == null)
				{
					string setting = Settings.Get("atlasVerify", "auto");
					bool on = setting == "1";
					if (setting == "auto")
					{
						try
						{
							System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(Waylith.Graphics.GpuInfo.DeviceName ?? "", @"Adreno\D*(\d{3})");
							on = m.Success && int.Parse(m.Groups[1].Value) < 800;
						}
						catch (Exception)
						{
						}
					}
					verify = on;
				}
				return verify.Value;
			}
		}

		private static bool? verify;

		/// <summary>From Entry, once the atlases are baked: the check now, and again after every bake and every loaded game where pages are compared with their files.</summary>
		public static void Start(Harmony harmony)
		{
			if (Settings.Get("atlasCheck", 1) == 0)
			{
				return;
			}
			Run("start");
			if (Verify)
			{
				harmony.Patch(AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases)), postfix: new HarmonyMethod(typeof(AtlasCheck), nameof(AfterBake)));
				harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)), postfix: new HarmonyMethod(typeof(AtlasCheck), nameof(AfterGameInit)));
			}
		}

		public static void AfterBake()
		{
			LongEventHandler.ExecuteWhenFinished(() => Run("after a bake"));
		}

		/// <summary>Postfix of Game.FinalizeInit (on the loading thread): the check on the main thread when the loading is over.</summary>
		public static void AfterGameInit()
		{
			LongEventHandler.ExecuteWhenFinished(() => Run("after the game loaded"));
		}

		private static void Run(string why)
		{
			try
			{
				var atlases = AccessTools.Field(typeof(GlobalTextureAtlasManager), "staticTextureAtlases")?.GetValue(null) as List<StaticTextureAtlas>;
				if (atlases == null)
				{
					Console.WriteLine("[mobile] atlas check: the game's list of static atlases is not there; nothing checked");
					return;
				}
				string root = Waylith.Runtime.WaylithRuntime.Current?.App.CacheDataPath;
				// "2": pages noted whole before the tiles were checked as well are checked once more.
				string list = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "TextureCache", "atlas-checked2.txt");
				cacheRoot = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "TextureCache");
				var checkedKeys = new HashSet<string>(list != null && File.Exists(list) ? File.ReadAllLines(list) : new string[0]);
				string test = Settings.Get("atlasCheckTest", "");
				if (test.Length > 0)
				{
					checkedKeys.Clear();
					BreakForTest(atlases, test);
				}
				var stopwatch = Stopwatch.StartNew();
				int pages = 0, skipped = 0, repaired = 0, compared = 0, reuploaded = 0;
				var added = new List<string>();
				foreach (StaticTextureAtlas atlas in atlases)
				{
					foreach (bool mask in new[] { false, true })
					{
						UnityEngine.Texture2D page = mask ? atlas.MaskTexture : atlas.ColorTexture;
						if (page?.CoreTexture?.GpuResource == null)
						{
							continue;
						}
						string key = atlas.CacheKey(mask);
						string id = key == null ? null : Hash(key + " " + page.CoreTexture.GpuResource.Format + " " + page.CoreTexture.GpuResource.MipCount);
						if (Verify && key != null)
						{
							int put = Compare(page, key);
							if (put >= 0)
							{
								compared++;
								reuploaded += put;
							}
						}
						if (id != null && checkedKeys.Contains(id))
						{
							skipped++;
							continue;
						}
						pages++;
						bool whole = Check(page, key, atlas, mask, ref repaired);
						if (whole && id != null)
						{
							added.Add(id);
						}
					}
				}
				readback = null;
				if (added.Count > 0 && list != null)
				{
					File.AppendAllLines(list, added);
				}
				if (pages > 0 || compared > 0)
				{
					Console.WriteLine("[mobile] atlas check (" + why + "): " + pages + " pages checked (" + skipped + " known whole), " + repaired + " levels made again"
						+ (Verify ? ", " + compared + " compared with their cache files, " + reuploaded + " of them put up again" : "") + ", " + stopwatch.ElapsedMilliseconds + " ms");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] atlas check failed: " + e);
			}
		}

		/// <summary>
		/// mobile.txt atlasCheckTest=Item (a part of a page's name): level 1 of the first such block-compressed page is
		/// emptied on the GPU, as the tester's was, for the check to find and make again.
		/// </summary>
		private static void BreakForTest(List<StaticTextureAtlas> atlases, string name)
		{
			bool cutLevel0 = Settings.Get("atlasCheckTestCut", 0) == 1;
			foreach (StaticTextureAtlas atlas in atlases)
			{
				UnityEngine.Texture2D page = atlas.ColorTexture;
				GpuTextureAllocation gpu = page?.CoreTexture?.GpuResource;
				if (gpu == null || page.name == null || !page.name.Contains(name) || gpu.MipCount < 2 || (gpu.Format != VkFormat.Bc7UNormBlock && gpu.Format != VkFormat.Astc4X4UNormBlock && gpu.Format != VkFormat.Astc4X4SRGBBlock))
				{
					continue;
				}
				int width = page.width, height = page.height, mipCount = gpu.MipCount;
				byte[][] levels = GpuTextureService.ReadbackTexture2DLevels(gpu, width, height, mipCount, 1);
				var uploads = new TextureLevelUpload[mipCount];
				for (int i = 0; i < mipCount; i++)
				{
					int w = Math.Max(1, width >> i), h = Math.Max(1, height >> i);
					byte[] level = levels[i].AsSpan(0, ((w + 3) / 4) * ((h + 3) / 4) * 16).ToArray();
					if (i == 1)
					{
						Blank(level, 0, level.Length, gpu.Format != VkFormat.Bc7UNormBlock);
					}
					else if (i == 0 && cutLevel0)
					{
						// The Adreno 725's Plant page: level 0 cut off below a line.
						int start = ((h + 3) / 4 / 2) * ((w + 3) / 4) * 16;
						Blank(level, start, level.Length - start, gpu.Format != VkFormat.Bc7UNormBlock);
					}
					uploads[i] = new TextureLevelUpload(i, w, h, 1, 0, level, level.Length);
				}
				GpuTextureAllocation image = GpuTextureService.CreateTexture2D(page.name, width, height, mipCount, gpu.Format, uploads, GpuMemoryCategory.Atlas);
				if (image != null)
				{
					page.CoreTexture.AdoptEncodedImage(image, LoadedTextureFormat.Bc7, mipCount);
					Console.WriteLine("[mobile] atlas check: test: level 1 of " + page.name + " emptied" + (cutLevel0 ? ", level 0 cut off below its middle" : ""));
				}
				return;
			}
		}

		/// <summary>Blocks of transparent black: BC7 all zero is a block of mode "none"; ASTC needs the void-extent block (0xFC 0xFD, then all ones, colour zero).</summary>
		private static void Blank(byte[] level, int start, int count, bool astc)
		{
			Array.Clear(level, start, count);
			if (astc)
			{
				for (int b = start; b < start + count; b += 16)
				{
					level[b] = 0xFC;
					level[b + 1] = 0xFD;
					for (int k = 2; k < 8; k++)
					{
						level[b + k] = 0xFF;
					}
				}
			}
		}

		/// <summary>
		/// A BC7 page against its file in the engine's cache: -1 when there is no file to compare with, 1 when the GPU's copy
		/// differed and the page was put up again from the file, 0 when they were the same.
		/// </summary>
		private static int Compare(UnityEngine.Texture2D page, string key)
		{
			Waylith.Rendering.Texture2D core = page.CoreTexture;
			GpuTextureAllocation gpu = core.GpuResource;
			if (gpu.Format != VkFormat.Bc7UNormBlock && gpu.Format != VkFormat.Bc7SRGBBlock)
			{
				return -1;
			}
			int width = page.width, height = page.height, mipCount = gpu.MipCount;
			if (!GpuTextureService.TryLoadKeyedTextureCache(key, out LoadedTextureData file) || file.Format != LoadedTextureFormat.Bc7 || file.Levels == null
				|| file.Width != width || file.Height != height || file.MipCount != mipCount || file.Levels.Length < mipCount)
			{
				return -1;
			}
			byte[][] levels = GpuTextureService.ReadbackTexture2DLevels(gpu, width, height, mipCount, 1, readback);
			if (levels == null)
			{
				return -1;
			}
			readback = levels;
			string differ = Differences(levels, file.Levels, width, height, mipCount);
			if (differ == null)
			{
				return 0;
			}
			var uploads = new TextureLevelUpload[mipCount];
			for (int i = 0; i < mipCount; i++)
			{
				uploads[i] = new TextureLevelUpload(i, Math.Max(1, width >> i), Math.Max(1, height >> i), 1, 0, file.Levels[i], BlockBytes(width, height, i));
			}
			GpuTextureAllocation image = GpuTextureService.CreateTexture2D(page.name ?? "TextureAtlas", width, height, mipCount, gpu.Format, uploads, GpuMemoryCategory.Atlas);
			if (image == null)
			{
				Console.WriteLine("[mobile] atlas check: " + page.name + " differed from its cache file (" + differ + "); could not be put up again");
				return 0;
			}
			core.AdoptEncodedImage(image, LoadedTextureFormat.Bc7, mipCount);
			byte[][] now = GpuTextureService.ReadbackTexture2DLevels(image, width, height, mipCount, 1, readback);
			if (now != null)
			{
				readback = now;
			}
			Console.WriteLine("[mobile] atlas check: " + page.name + " (" + width + "x" + height + ") differed from its cache file (" + differ + "); put up again from the file, the GPU has now: "
				+ (now == null ? "(no readback)" : Differences(now, file.Levels, width, height, mipCount) ?? "the same as the file"));
			return 1;
		}

		private static int BlockBytes(int width, int height, int level)
		{
			return ((Math.Max(1, width >> level) + 3) / 4) * ((Math.Max(1, height >> level) + 3) / 4) * 16;
		}

		/// <summary>Null when every level is the same, else the share of blocks that differ in each level that differs.</summary>
		private static string Differences(byte[][] gpu, byte[][] file, int width, int height, int mipCount)
		{
			System.Text.StringBuilder text = null;
			for (int level = 0; level < mipCount; level++)
			{
				int length = BlockBytes(width, height, level);
				if (gpu[level] == null || file[level] == null || gpu[level].Length < length || file[level].Length < length)
				{
					return "level " + level + " not all there";
				}
				ReadOnlySpan<byte> a = gpu[level].AsSpan(0, length), b = file[level].AsSpan(0, length);
				if (a.SequenceEqual(b))
				{
					continue;
				}
				int blocks = length / 16, different = 0;
				for (int i = 0; i < blocks; i++)
				{
					if (!a.Slice(i * 16, 16).SequenceEqual(b.Slice(i * 16, 16)))
					{
						different++;
					}
				}
				text = text ?? new System.Text.StringBuilder();
				text.Append((text.Length > 0 ? ", " : "") + "level " + level + ": " + (different * 100.0 / blocks).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "% of blocks");
			}
			return text?.ToString();
		}

		private static string Hash(string text)
		{
			var hash = new System.IO.Hashing.XxHash3();
			hash.Append(System.Text.Encoding.UTF8.GetBytes(text));
			return hash.GetCurrentHashAsUInt64().ToString("x16");
		}

		private enum Kind { Bc7, Astc, Rgba }

		/// <summary>Whether the page is whole (after any repair).</summary>
		private static bool Check(UnityEngine.Texture2D page, string key, StaticTextureAtlas atlas, bool mask, ref int repaired)
		{
			bool fromCache = atlas.AdoptedFromCache;
			Waylith.Rendering.Texture2D core = page.CoreTexture;
			GpuTextureAllocation gpu = core.GpuResource;
			Kind kind;
			switch (gpu.Format)
			{
			case VkFormat.Bc7UNormBlock:
			case VkFormat.Bc7SRGBBlock:
				kind = Kind.Bc7;
				break;
			case VkFormat.Astc4X4UNormBlock:
			case VkFormat.Astc4X4SRGBBlock:
				kind = Kind.Astc;
				break;
			case VkFormat.R8G8B8A8UNorm:
			case VkFormat.R8G8B8A8SRGB:
				kind = Kind.Rgba;
				break;
			default:
				return true;
			}
			bool srgb = gpu.Format == VkFormat.Bc7SRGBBlock || gpu.Format == VkFormat.Astc4X4SRGBBlock || gpu.Format == VkFormat.R8G8B8A8SRGB;
			int width = page.width, height = page.height, mipCount = gpu.MipCount;
			if (mipCount < 2 || (width >> (mipCount - 1)) < 4 || (height >> (mipCount - 1)) < 4)
			{
				return true;
			}
			byte[][] levels = GpuTextureService.ReadbackTexture2DLevels(gpu, width, height, mipCount, kind == Kind.Rgba ? 4 : 1, readback);
			if (levels == null)
			{
				return false;
			}
			readback = levels;
			if (!mask && MissingTiles(atlas, page, key, levels[0], width, height, kind, srgb))
			{
				return false;
			}
			bool changed = false;
			byte[][] fixedLevels = null;
			for (int level = 1; level < mipCount; level++)
			{
				int lost = Lost(levels, level, width, height, kind, srgb, out int covered);
				if (covered < 20 || lost * 20 <= covered)
				{
					continue;
				}
				var stopwatch = Stopwatch.StartNew();
				int aboveW = Math.Max(1, width >> (level - 1)), aboveH = Math.Max(1, height >> (level - 1));
				int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
				byte[] above = Decode(levels[level - 1], aboveW, aboveH, kind, srgb);
				var rgba = new byte[w * h * 4];
				PortHooks.EngineTypes.Downsample(above, aboveW, aboveH, rgba, w, h, parallel: true);
				byte[] remade = kind == Kind.Bc7 ? PortHooks.EngineTypes.EncodeBc7Levels(new[] { rgba }, w, h, 1, parallel: true)[0] : kind == Kind.Astc ? PortHooks.Astc.EncodeAtlasLevel(rgba, w, h, srgb) : rgba;
				if (remade == null)
				{
					Console.WriteLine("[mobile] atlas check: " + page.name + " level " + level + " could not be encoded again");
					return false;
				}
				// The readback arrays are reused for the next page: the page's own levels are copies.
				fixedLevels = fixedLevels ?? Array.ConvertAll(levels, l => l);
				fixedLevels[level] = remade;
				levels = fixedLevels;
				changed = true;
				repaired++;
				Console.WriteLine("[mobile] atlas check: " + page.name + " (" + width + "x" + height + ", " + gpu.Format + ", " + (fromCache ? "from the engine's cache" : "made in this run") + "): level " + level + " had lost " + (lost * 100 / covered)
					+ "% of what level " + (level - 1) + " covers; made again from it in " + stopwatch.ElapsedMilliseconds + " ms");
			}
			if (!changed)
			{
				return true;
			}
			// Copies: the readback arrays are used again for the next page, and the cache is written while that goes on.
			var exact = new byte[mipCount][];
			for (int i = 0; i < mipCount; i++)
			{
				int length = kind == Kind.Rgba ? Math.Max(1, width >> i) * Math.Max(1, height >> i) * 4 : ((Math.Max(1, width >> i) + 3) / 4) * ((Math.Max(1, height >> i) + 3) / 4) * 16;
				exact[i] = levels[i].AsSpan(0, length).ToArray();
			}
			if (kind == Kind.Rgba)
			{
				// UploadRgba32 takes the level's own size (the extent of its copy).
				for (int i = 1; i < mipCount; i++)
				{
					GpuTextureService.UploadRgba32(gpu, Math.Max(1, width >> i), Math.Max(1, height >> i), i, exact[i]);
				}
				return true;
			}
			var uploads = new TextureLevelUpload[mipCount];
			for (int i = 0; i < mipCount; i++)
			{
				uploads[i] = new TextureLevelUpload(i, Math.Max(1, width >> i), Math.Max(1, height >> i), 1, 0, exact[i], exact[i].Length);
			}
			GpuTextureAllocation image = GpuTextureService.CreateTexture2D(page.name ?? "TextureAtlas", width, height, mipCount, gpu.Format, uploads, GpuMemoryCategory.Atlas);
			if (image == null)
			{
				return false;
			}
			core.AdoptEncodedImage(image, LoadedTextureFormat.Bc7, mipCount);
			// Read back again: what the GPU has now (one more readback, only for a page that was made again).
			byte[][] now = GpuTextureService.ReadbackTexture2DLevels(image, width, height, mipCount, 1);
			var result = new System.Text.StringBuilder();
			for (int level = 1; now != null && level < mipCount; level++)
			{
				int lost = Lost(now, level, width, height, kind, srgb, out int covered);
				result.Append(" level " + level + " " + (covered == 0 ? "-" : (lost * 100 / covered) + "% lost"));
			}
			Console.WriteLine("[mobile] atlas check: " + page.name + " as the GPU has it now:" + (now == null ? " (no readback)" : result.ToString()));
			if (key != null)
			{
				if (kind == Kind.Bc7)
				{
					GpuTextureService.BeginWriteKeyedTextureCache(key, new LoadedTextureData(width, height, mipCount, LoadedTextureFormat.Bc7, exact));
				}
				else
				{
					PortHooks.Astc.RewriteAtlasPage(key, width, height, srgb, exact);
				}
			}
			return true;
		}

		private static readonly AccessTools.FieldRef<StaticTextureAtlas, Dictionary<UnityEngine.Texture, StaticTextureAtlasTile>> Tiles = AccessTools.FieldRefAccess<StaticTextureAtlas, Dictionary<UnityEngine.Texture, StaticTextureAtlasTile>>("tiles");
		private static string cacheRoot;
		private static bool rebuildSaid;

		/// <summary>
		/// Textures that are not in the page at all: a tile empty at 5 x 5 points inside it, at level 0, where its texture has
		/// something. A tester's Adreno 730 (Xiaomi, 2026-10-25) had the comms console invisible: its tile in the Building page
		/// was empty at every level, in the GPU's copy and in the engine's cache alike, so it stayed that way at every start
		/// (the checks above compare a level with the one above it, and the page with its cache file: both found it whole).
		/// Such a page cannot be mended here (the textures are drawn into a page when it is baked); its cache entry goes (the
		/// ASTC file now; the engine's pack at the next start, PortHooks.DropAtlasCacheIfAsked) and the player is told that
		/// the next start bakes it again. Which way up the page is read back is not assumed: the tiles are looked at both
		/// ways, and the one with fewer empty tiles counts.
		/// </summary>
		private static bool MissingTiles(StaticTextureAtlas atlas, UnityEngine.Texture2D page, string key, byte[] level0, int width, int height, Kind kind, bool srgb)
		{
			Dictionary<UnityEngine.Texture, StaticTextureAtlasTile> tiles = Tiles(atlas);
			if (tiles == null || tiles.Count == 0)
			{
				return false;
			}
			string test = Settings.Get("atlasTileTest", "");
			if (test.Length > 0)
			{
				BlankTileForTest(tiles, test, level0, width, height, kind);
			}
			var texels = new byte[64];
			var empty = new[] { new List<UnityEngine.Texture>(), new List<UnityEngine.Texture>() };
			foreach (KeyValuePair<UnityEngine.Texture, StaticTextureAtlasTile> pair in tiles)
			{
				UnityEngine.Rect uv = pair.Value.uvRect;
				for (int way = 0; way < 2; way++)
				{
					bool any = false;
					for (int i = 0; i < 25 && !any; i++)
					{
						float u = uv.x + uv.width * (0.15f + 0.7f * (i % 5) / 4f), v = uv.y + uv.height * (0.15f + 0.7f * (i / 5) / 4f);
						any = Alpha(level0, 0, width, height, u, way == 0 ? v : 1f - v, kind, srgb, texels) >= 16;
					}
					if (!any)
					{
						empty[way].Add(pair.Key);
					}
				}
			}
			List<UnityEngine.Texture> candidates = empty[0].Count <= empty[1].Count ? empty[0] : empty[1];
			var missing = new List<string>();
			foreach (UnityEngine.Texture texture in candidates)
			{
				if (HasContent(texture as UnityEngine.Texture2D))
				{
					missing.Add(texture.name);
				}
			}
			if (missing.Count == 0)
			{
				return false;
			}
			string names = string.Join(", ", missing.Take(8)) + (missing.Count > 8 ? " ..." : "");
			bool dropped = kind == Kind.Astc ? PortHooks.Astc.DropAtlasPage(key) : AskRebuild(page.name + ": " + missing.Count + " textures missing (" + names + ")");
			Console.WriteLine("[mobile] atlas check: " + page.name + " (" + width + "x" + height + ", " + (atlas.AdoptedFromCache ? "from the engine's cache" : "made in this run") + "): " + missing.Count + " of " + tiles.Count
				+ " textures are not in it (" + names + "; empty tiles " + empty[0].Count + " / " + empty[1].Count + " read either way up); "
				+ (dropped ? "its cache entry goes, the next start bakes it again" : "its cache entry could not be dropped"));
			if (dropped && !rebuildSaid)
			{
				rebuildSaid = true;
				string text = Texts.Chinese
					? "有 " + missing.Count + " 张贴图在图集缓存里缺失（比如 " + string.Join("、", missing.Take(3)) + "），这些东西会看不见。缓存已清除，重启游戏后会重新生成图集（第一次启动会慢一些）。"
					: missing.Count + " textures are missing from a cached texture atlas (such as " + string.Join(", ", missing.Take(3)) + "), so those things are invisible. The cache was cleared: restart the game and the atlas is made again (the first start takes a little longer).";
				Find.WindowStack.Add(new Dialog_MessageBox(text));
			}
			return true;
		}

		/// <summary>mobile.txt atlasTileTest=&lt;texture name&gt;: that texture's tile is emptied in the read-back level 0 (read either way up), to see the check find it.</summary>
		private static void BlankTileForTest(Dictionary<UnityEngine.Texture, StaticTextureAtlasTile> tiles, string name, byte[] level0, int width, int height, Kind kind)
		{
			foreach (KeyValuePair<UnityEngine.Texture, StaticTextureAtlasTile> pair in tiles)
			{
				if (pair.Key.name != name || kind == Kind.Rgba)
				{
					continue;
				}
				UnityEngine.Rect uv = pair.Value.uvRect;
				int blocksX = (width + 3) / 4;
				foreach (float top in new[] { uv.y, 1f - uv.y - uv.height })
				{
					int x0 = (int)(uv.x * width) / 4, x1 = (int)((uv.x + uv.width) * width) / 4, y0 = (int)(top * height) / 4, y1 = (int)((top + uv.height) * height) / 4;
					for (int by = y0; by <= y1 && by < (height + 3) / 4; by++)
					{
						Blank(level0, (by * blocksX + x0) * 16, (Math.Min(x1, blocksX - 1) - x0 + 1) * 16, kind == Kind.Astc);
					}
				}
				Console.WriteLine("[mobile] atlas check: test: " + name + "'s tile emptied in the read-back copy");
			}
		}

		/// <summary>Whether a texture is solid at 3 or more of the 5 x 5 points over its middle (level 0, read back from the GPU).</summary>
		private static bool HasContent(UnityEngine.Texture2D texture)
		{
			try
			{
				Waylith.Rendering.Texture2D core = texture?.CoreTexture;
				if (core == null || texture.width < 4 || texture.height < 4 || !core.EnsureResident() || core.GpuResource == null)
				{
					return false;
				}
				GpuTextureAllocation gpu = core.GpuResource;
				Kind kind;
				switch (gpu.Format)
				{
				case VkFormat.Bc7UNormBlock:
				case VkFormat.Bc7SRGBBlock:
					kind = Kind.Bc7;
					break;
				case VkFormat.Astc4X4UNormBlock:
				case VkFormat.Astc4X4SRGBBlock:
					kind = Kind.Astc;
					break;
				case VkFormat.R8G8B8A8UNorm:
				case VkFormat.R8G8B8A8SRGB:
					kind = Kind.Rgba;
					break;
				default:
					return false;
				}
				bool srgb = gpu.Format == VkFormat.Bc7SRGBBlock || gpu.Format == VkFormat.Astc4X4SRGBBlock || gpu.Format == VkFormat.R8G8B8A8SRGB;
				int width = texture.width, height = texture.height, mipCount = Math.Max(1, gpu.MipCount);
				byte[][] levels = GpuTextureService.ReadbackTexture2DLevels(gpu, width, height, mipCount, kind == Kind.Rgba ? 4 : 1);
				if (levels == null)
				{
					return false;
				}
				// Level 0, at the points the tile was looked at (the grid is the same either way up): a smaller level blurs an
				// outline into the middle (the crafting spot's frame, hollow inside, looked filled at 4 x 4).
				var texels = new byte[64];
				int filled = 0;
				for (int i = 0; i < 25; i++)
				{
					if (Alpha(levels[0], 0, width, height, 0.15f + 0.7f * (i % 5) / 4f, 0.15f + 0.7f * (i / 5) / 4f, kind, srgb, texels) > 128)
					{
						filled++;
					}
				}
				return filled >= 3;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] atlas check: " + texture?.name + " could not be read back: " + e.Message);
				return false;
			}
		}

		/// <summary>For the engine's BC7 pages: the next start drops its cached pages before it bakes (PortHooks reads the file).</summary>
		private static bool AskRebuild(string why)
		{
			if (cacheRoot == null)
			{
				return false;
			}
			try
			{
				File.AppendAllText(Path.Combine(cacheRoot, "atlas-rebuild.txt"), why + "\n");
				return true;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] atlas check: the rebuild could not be asked for: " + e.Message);
				return false;
			}
		}

		/// <summary>Of the sample points the level above covers, how many are empty in this level.</summary>
		private static int Lost(byte[][] levels, int level, int width, int height, Kind kind, bool srgb, out int covered)
		{
			covered = 0;
			int lost = 0;
			var texels = new byte[64];
			for (int sy = 0; sy < SamplesY; sy++)
			{
				for (int sx = 0; sx < SamplesX; sx++)
				{
					float u = (sx + 0.5f) / SamplesX, v = (sy + 0.5f) / SamplesY;
					if (Alpha(levels[level - 1], level - 1, width, height, u, v, kind, srgb, texels) > 128)
					{
						covered++;
						if (Alpha(levels[level], level, width, height, u, v, kind, srgb, texels) < 16)
						{
							lost++;
						}
					}
				}
			}
			return lost;
		}

		private static int Alpha(byte[] data, int level, int width, int height, float u, float v, Kind kind, bool srgb, byte[] texels)
		{
			int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
			int x = Math.Min(w - 1, (int)(u * w)), y = Math.Min(h - 1, (int)(v * h));
			if (kind == Kind.Rgba)
			{
				return data[(y * w + x) * 4 + 3];
			}
			int offset = ((y / 4) * ((w + 3) / 4) + x / 4) * 16;
			DecodeBlock(data, offset, kind, srgb, texels);
			return texels[((y % 4) * 4 + x % 4) * 4 + 3];
		}

		private static void DecodeBlock(byte[] data, int offset, Kind kind, bool srgb, byte[] texels)
		{
			if (kind == Kind.Bc7)
			{
				PortHooks.EngineTypes.DecodeBc7Block(new ReadOnlySpan<byte>(data, offset, 16), texels);
			}
			else
			{
				byte[] block = new ReadOnlySpan<byte>(data, offset, 16).ToArray();
				port_astc_decode(block, (UIntPtr)16, 4, 4, srgb ? 1 : 0, texels);
			}
		}

		/// <summary>A whole level to RGBA8.</summary>
		private static byte[] Decode(byte[] data, int w, int h, Kind kind, bool srgb)
		{
			if (kind == Kind.Rgba)
			{
				return data.Length == w * h * 4 ? data : data.AsSpan(0, w * h * 4).ToArray();
			}
			var rgba = new byte[w * h * 4];
			int blocksX = (w + 3) / 4, blocksY = (h + 3) / 4;
			if (kind == Kind.Astc)
			{
				port_astc_decode(data, (UIntPtr)(blocksX * blocksY * 16), w, h, srgb ? 1 : 0, rgba);
				return rgba;
			}
			Parallel.For(0, blocksY, by =>
			{
				var texels = new byte[64];
				for (int bx = 0; bx < blocksX; bx++)
				{
					PortHooks.EngineTypes.DecodeBc7Block(new ReadOnlySpan<byte>(data, (by * blocksX + bx) * 16, 16), texels);
					for (int y = 0; y < 4 && by * 4 + y < h; y++)
					{
						int count = Math.Min(4, w - bx * 4) * 4;
						Buffer.BlockCopy(texels, y * 16, rgba, ((by * 4 + y) * w + bx * 4) * 4, count);
					}
				}
			});
			return rgba;
		}
	}
}
