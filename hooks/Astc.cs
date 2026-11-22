using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Hashing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Brutal.VulkanApi;
using HarmonyLib;
using Waylith;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.Runtime;

namespace PortHooks
{
	/// <summary>
	/// ASTC instead of RGBA on the GPUs without BC textures (Textures.cs: ARM's Mali, the Dimensity phones).
	///
	/// There every BC texture is decoded to RGBA and every other texture stays RGBA, four times the memory of the BC7
	/// a PC or an Adreno has, and four times what the GPU reads for every sample. Measured on a Dimensity 9200 with a
	/// 250x250 colony: atlases 736 MB, textures 363 MB. ASTC 4x4 is what those GPUs read instead: 16 bytes for 4x4
	/// texels, the size of BC7. ARM's encoder (native/astc, libportastc.so) is too slow to run while a texture is
	/// waited for, so:
	///   - the static atlases (Verse.StaticTextureAtlas, about two thirds of it) are read back after the game has drawn
	///     them, encoded on every core while the loading screen waits (a few seconds, once), and kept in the cache under
	///     the game's own key for that atlas (its textures' paths and content hashes, positions, the engine's version):
	///     the next start takes them from there and draws no atlas at all;
	///   - a texture that would be BC7 on a PC (BC data, or a file's RGBA over 16x16 that the engine's rule would have
	///     encoded: TextureCompressionSettings.SelectTarget) goes up as RGBA the first time and is encoded on a
	///     background thread of low priority; every later upload of the same content (the next start, and every time
	///     ColdTextures brings it back) takes the ASTC from the cache. The key is a hash of the texture's data, so a
	///     changed mod texture is a different entry.
	/// The cache: TextureCache/astc1 next to the engine's own (astcCacheMB; by default a quarter of the free storage,
	/// 3 to 8 GB; past that nothing new is kept, and at the next start, from nine tenths full, the least recently used
	/// entries go down to three quarters; unused for 30 days, an entry goes anyway).
	/// A texture of the atlases' sources while they are drawn is left as it is: it is let go right afterwards.
	///
	/// Only where the GPU says it samples both ASTC 4x4 formats with linear filtering (textureCompressionASTC_LDR and
	/// the format's own features: an old driver or a GPU without it keeps RGBA), and where the library loads and encodes
	/// a test image correctly on this processor (NEON only: every 64-bit ARM core). mobile.txt: astc=0 never,
	/// astcQuality (10: astcenc's "fast"), astcAtlasQuality (0, "fastest"), astcThreads (the background encoders, 1, or 2
	/// with 8 cores or more), astcCacheMB.
	/// </summary>
	public static class Astc
	{
		[DllImport("portastc")] private static extern int port_astc_encode(byte[] rgba, int width, int height, int srgb, float quality, byte[] output, UIntPtr outLength);
		[DllImport("portastc")] private static extern int port_astc_encode_parallel(byte[] rgba, int width, int height, int srgb, float quality, byte[] output, UIntPtr outLength, int threads);
		[DllImport("portastc")] private static extern int port_astc_decode(byte[] blocks, UIntPtr blocksLength, int width, int height, int srgb, byte[] rgba);
		[DllImport("portastc")] private static extern void port_astc_release();
		[DllImport("libc")] private static extern int gettid();
		[DllImport("libc", SetLastError = true)] private static extern int statfs(string path, byte[] buffer);
		[DllImport("libc")] private static extern int setpriority(int which, int who, int prio);

		private const int Version = 1;
		private const uint Magic = 0x31435341; // "ASC1"

		public static bool On { get; private set; }

		// astcenc's presets: 0 fastest, 10 fast, 60 medium. Measured on game textures (one core of the OnePlus): 0 at 43 Mtexels/s,
		// 52.6 dB on average (the worst of 40 sprites 40.1 dB); 10 at 32 Mtexels/s, 53.2 dB (41.0). The atlases, which the
		// loading screen waits for, take the fastest; the textures, encoded in the background, the next one.
		private static float quality = 10f, atlasQuality = 0f;
		private static int workerCount = 1;
		private static long cacheLimit = 3072L << 20, cacheBytes;
		private static bool cacheLimitSet;
		private static string cacheDir;
		// The entries of the cache, with when each was last used (UTC ticks).
		private static readonly ConcurrentDictionary<ulong, long> known = new ConcurrentDictionary<ulong, long>();
		private static readonly ConcurrentDictionary<ulong, bool> pending = new ConcurrentDictionary<ulong, bool>();
		private static readonly BlockingCollection<Job> jobs = new BlockingCollection<Job>();
		private static long queuedBytes;
		private const long QueueLimit = 128L << 20;
		private static int workersStarted, baking, idleReported;
		private static long hits, misses, encoded, dropped, encodeTicks, encodedTexels, savedBytes;
		private static long atlasFromCache, atlasEncoded, atlasTicks, atlasReadTicks, atlasEncodeTicks, atlasTexels;
		private static byte[][] readback;

		[ThreadStatic] private static Waylith.Rendering.Texture2D uploading;

		private sealed class Job
		{
			public ulong Key;
			public int Width, Height;
			public bool Srgb;
			public byte[][] Levels;
			public long Bytes;
		}

		/// <summary>From Textures.AfterCompressionConfigured when the BC textures are decoded: ASTC from here on if this GPU and processor can.</summary>
		public static void Start(Harmony harmony, PhysicalDevice gpu)
		{
			try
			{
				string setting = Textures.Setting("astc");
				if (setting == "0")
				{
					Console.WriteLine("[port17] astc: astc=0 in mobile.txt; textures stay RGBA");
					return;
				}
				if (!Usable(gpu, out string why))
				{
					Console.WriteLine("[port17] astc: " + why + "; textures stay RGBA");
					return;
				}
				if (float.TryParse(Textures.Setting("astcQuality"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float q) && q >= 0 && q <= 100)
				{
					quality = q;
				}
				if (float.TryParse(Textures.Setting("astcAtlasQuality"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out q) && q >= 0 && q <= 100)
				{
					atlasQuality = q;
				}
				workerCount = int.TryParse(Textures.Setting("astcThreads"), out int t) && t >= 1 && t <= 8 ? t : Environment.ProcessorCount >= 8 ? 2 : 1;
				if (long.TryParse(Textures.Setting("astcCacheMB"), out long mb) && mb >= 0)
				{
					cacheLimit = mb << 20;
					cacheLimitSet = true;
				}
				if (!SelfTest(out why))
				{
					Console.WriteLine("[port17] astc: the encoder failed its test on this processor (" + why + "); textures stay RGBA");
					return;
				}
				OpenCache();
				harmony.Patch(AccessTools.Method(typeof(Waylith.Rendering.Texture2D), "UploadLevels"), prefix: new HarmonyMethod(typeof(Astc), nameof(BeforeUploadLevels)), postfix: new HarmonyMethod(typeof(Astc), nameof(AfterUploadLevels)));
				try
				{
					PatchAtlases(harmony);
				}
				catch (Exception e)
				{
					Console.WriteLine("[port17] astc: atlases stay RGBA: " + e);
				}
				On = true;
				Console.WriteLine("[port17] astc: on (quality " + quality.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", atlases " + atlasQuality.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " + workerCount + " background encoder(s)); cache " + cacheDir + ": " + known.Count + " entries, " + (cacheBytes >> 20) + " of " + (cacheLimit >> 20) + " MB");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] astc: not started: " + e);
			}
		}

		private static bool Usable(PhysicalDevice gpu, out string why)
		{
			if (gpu == null)
			{
				why = "no GPU to ask";
				return false;
			}
			if (!gpu.GetFeatures().TextureCompressionASTCLDR)
			{
				why = "the GPU has no ASTC (textureCompressionASTC_LDR)";
				return false;
			}
			const VkFormatFeatureFlags needed = VkFormatFeatureFlags.SampledImageBit | VkFormatFeatureFlags.SampledImageFilterLinearBit;
			foreach (VkFormat format in new[] { VkFormat.Astc4X4UNormBlock, VkFormat.Astc4X4SRGBBlock })
			{
				VkFormatFeatureFlags features = gpu.GetFormatProperties(format).OptimalTilingFeatures;
				if ((features & needed) != needed)
				{
					why = "the GPU cannot sample " + format + " with linear filtering (" + features + ")";
					return false;
				}
			}
			why = null;
			return true;
		}

		/// <summary>A 16x16 image of gradients and hard edges through the encoder and back: the library loads, and its NEON code gives what it should here.</summary>
		private static bool SelfTest(out string why)
		{
			try
			{
				const int size = 16;
				byte[] image = new byte[size * size * 4];
				for (int y = 0; y < size; y++)
				{
					for (int x = 0; x < size; x++)
					{
						int i = (y * size + x) * 4;
						image[i] = (byte)(x * 16);
						image[i + 1] = (byte)(y * 16);
						image[i + 2] = (byte)(x < 8 ? 40 : 220);
						image[i + 3] = (byte)(y < 4 ? 0 : 255);
					}
				}
				byte[] blocks = new byte[BlockBytes(size, size)];
				byte[] back = new byte[image.Length];
				int error = port_astc_encode(image, size, size, 0, quality, blocks, (UIntPtr)blocks.Length);
				if (error == 0)
				{
					error = port_astc_decode(blocks, (UIntPtr)blocks.Length, size, size, 0, back);
				}
				if (error != 0)
				{
					why = "error " + error;
					return false;
				}
				int worst = 0;
				for (int i = 0; i < image.Length; i++)
				{
					worst = Math.Max(worst, Math.Abs(image[i] - back[i]));
				}
				port_astc_release();
				why = "largest difference " + worst;
				return worst <= 24;
			}
			catch (Exception e)
			{
				why = e.GetType().Name + ": " + e.Message;
				return false;
			}
		}

		private static int BlockBytes(int width, int height)
		{
			return ((Math.Max(1, width) + 3) / 4) * ((Math.Max(1, height) + 3) / 4) * 16;
		}

		// ---- the cache: one file per texture or atlas page, <key>.astc: magic, width, height, levels, sRGB, then the levels' blocks.

		/// <summary>
		/// The entries there; those not used for 30 days are dropped (an atlas of a mod list given up, a texture of a mod
		/// taken out), and past the limit the oldest go until a quarter of it is free. An entry's time is when it was last
		/// used (TryRead puts it forward, once a day at most).
		/// </summary>
		private static void OpenCache()
		{
			string root = WaylithRuntime.Current?.App.CacheDataPath;
			if (string.IsNullOrWhiteSpace(root))
			{
				root = Path.GetTempPath();
			}
			cacheDir = Path.Combine(root, "TextureCache", "astc" + Version);
			Directory.CreateDirectory(cacheDir);
			var entries = new System.Collections.Generic.List<(ulong Key, long Used, long Size, string File)>();
			long stale = DateTime.UtcNow.AddDays(-30).Ticks;
			int dropped = 0;
			long droppedBytes = 0;
			foreach (string file in Directory.EnumerateFiles(cacheDir))
			{
				if (file.EndsWith(".tmp", StringComparison.Ordinal))
				{
					TryDelete(file);
					continue;
				}
				if (!ulong.TryParse(Path.GetFileNameWithoutExtension(file), System.Globalization.NumberStyles.HexNumber, null, out ulong key))
				{
					continue;
				}
				var info = new FileInfo(file);
				long used = info.LastWriteTimeUtc.Ticks;
				if (used < stale)
				{
					TryDelete(file);
					dropped++;
					droppedBytes += info.Length;
					continue;
				}
				entries.Add((key, used, info.Length, file));
				cacheBytes += info.Length;
			}
			if (!cacheLimitSet)
			{
				cacheLimit = SizedLimit(cacheBytes);
			}
			// Nearly full counts as full: nothing that would pass the limit is written, so a cache never goes over it, and
			// one stuck just below it took nothing new for good (a tester with 588 mods, 2026-10-26: 3071 of 3072 MB, and
			// the textures it had no room for went up as RGBA, four times the size, at every start, until the graphics
			// memory ran out).
			if (cacheBytes > cacheLimit / 10 * 9)
			{
				entries.Sort((a, b) => a.Used.CompareTo(b.Used));
				int i = 0;
				for (; i < entries.Count && cacheBytes > cacheLimit / 4 * 3; i++)
				{
					TryDelete(entries[i].File);
					cacheBytes -= entries[i].Size;
					dropped++;
					droppedBytes += entries[i].Size;
				}
				entries.RemoveRange(0, i);
			}
			foreach (var entry in entries)
			{
				known[entry.Key] = entry.Used;
			}
			if (dropped > 0)
			{
				Console.WriteLine("[port17] astc: " + dropped + " old cache entries dropped (" + (droppedBytes >> 20) + " MB)");
			}
		}

		/// <summary>
		/// The cache's size when mobile.txt does not say: 3 GB, or a quarter of what is free on the storage (with the cache
		/// counted as free) up to 8 GB. Hundreds of mods have more textures than 3 GB of ASTC, and what has no room stays
		/// RGBA in graphics memory.
		/// </summary>
		private static long SizedLimit(long used)
		{
			try
			{
				// struct statfs on arm64: f_bsize at 8, f_bavail at 32 (64-bit fields).
				byte[] stat = new byte[120];
				if (statfs(cacheDir, stat) != 0)
				{
					return cacheLimit;
				}
				long free = BitConverter.ToInt64(stat, 32) * BitConverter.ToInt64(stat, 8) + used;
				return Math.Max(3072L << 20, Math.Min(8192L << 20, free / 4));
			}
			catch (Exception)
			{
				return cacheLimit;
			}
		}

		private static string FileOf(ulong key)
		{
			return Path.Combine(cacheDir, key.ToString("x16") + ".astc");
		}

		private static void TryDelete(string file)
		{
			try
			{
				File.Delete(file);
			}
			catch (Exception)
			{
			}
		}

		private static bool TryRead(ulong key, int width, int height, int mipCount, out byte[][] levels, out bool srgb)
		{
			levels = null;
			srgb = false;
			if (!known.TryGetValue(key, out long used))
			{
				return false;
			}
			string file = FileOf(key);
			try
			{
				using (var reader = new BinaryReader(File.OpenRead(file)))
				{
					if (reader.ReadUInt32() != Magic || reader.ReadInt32() != width || reader.ReadInt32() != height || reader.ReadInt32() != mipCount)
					{
						throw new InvalidDataException("header");
					}
					srgb = reader.ReadInt32() != 0;
					levels = new byte[mipCount][];
					for (int i = 0; i < mipCount; i++)
					{
						int length = BlockBytes(width >> i, height >> i);
						levels[i] = reader.ReadBytes(length);
						if (levels[i].Length != length)
						{
							throw new InvalidDataException("level " + i);
						}
					}
				}
				long now = DateTime.UtcNow.Ticks;
				if (now - used > TimeSpan.TicksPerDay && known.TryUpdate(key, now, used))
				{
					File.SetLastWriteTimeUtc(file, new DateTime(now, DateTimeKind.Utc));
				}
				return true;
			}
			catch (Exception e)
			{
				known.TryRemove(key, out _);
				TryDelete(file);
				Console.WriteLine("[port17] astc: " + Path.GetFileName(file) + " dropped from the cache (" + e.Message + ")");
				levels = null;
				return false;
			}
		}

		private static void Write(ulong key, int width, int height, bool srgb, byte[][] levels)
		{
			long size = 20;
			foreach (byte[] level in levels)
			{
				size += level.Length;
			}
			if (Interlocked.Read(ref cacheBytes) + size > cacheLimit)
			{
				return;
			}
			string file = FileOf(key);
			string temporary = file + "." + Environment.CurrentManagedThreadId + ".tmp";
			try
			{
				using (var writer = new BinaryWriter(File.Create(temporary)))
				{
					writer.Write(Magic);
					writer.Write(width);
					writer.Write(height);
					writer.Write(levels.Length);
					writer.Write(srgb ? 1 : 0);
					foreach (byte[] level in levels)
					{
						writer.Write(level);
					}
				}
				File.Move(temporary, file, overwrite: true);
				if (known.TryAdd(key, DateTime.UtcNow.Ticks))
				{
					Interlocked.Add(ref cacheBytes, size);
				}
			}
			catch (Exception e)
			{
				TryDelete(temporary);
				Console.WriteLine("[port17] astc: could not write " + Path.GetFileName(file) + ": " + e.Message);
			}
		}

		// ---- textures

		public static void BeforeUploadLevels(Waylith.Rendering.Texture2D __instance)
		{
			uploading = __instance;
		}

		public static void AfterUploadLevels()
		{
			uploading = null;
		}

		/// <summary>From Textures.BeforeCreateTexture2D: the Texture2D whose UploadLevels this is, if it is (taken once).</summary>
		public static Waylith.Rendering.Texture2D TakeUploading(int width, int height)
		{
			Waylith.Rendering.Texture2D texture = uploading;
			uploading = null;
			return texture != null && texture.width == width && texture.height == height ? texture : null;
		}

		/// <summary>
		/// Whether a texture of this upload is worth ASTC: every level there (no mip chain to make on the GPU afterwards),
		/// over 16x16, a game texture, and not one of the atlases' sources being drawn into their atlas.
		/// </summary>
		public static bool Wanted(int width, int height, int mipCount, GpuMemoryCategory category, ReadOnlySpan<TextureLevelUpload> levels)
		{
			if (!On || Volatile.Read(ref baking) > 0 || category != GpuMemoryCategory.GameTexture || (width <= 16 && height <= 16) || levels.Length != mipCount)
			{
				return false;
			}
			for (int i = 0; i < levels.Length; i++)
			{
				TextureLevelUpload level = levels[i];
				if (level.Level != i || level.Size == 0 || level.Width != Math.Max(1, width >> i) || level.Height != Math.Max(1, height >> i) || level.Depth > 1 || (level.RowLengthTexels != 0 && level.RowLengthTexels != level.Width))
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>The engine's rule for a file's RGBA (SelectTarget, which says None here only because BC is off): a texture it would have made BC7.</summary>
		public static bool WouldBeCompressed(Waylith.Rendering.Texture2D texture)
		{
			string path = texture?.SourcePath;
			if (path == null)
			{
				return false;
			}
			if (texture.width <= EngineTypes.RawMaxSize && texture.height <= EngineTypes.RawMaxSize)
			{
				return false;
			}
			return !EngineTypes.MatchesAny(path, EngineTypes.RawPaths) && !Path.GetFileNameWithoutExtension(path).EndsWith("_Uncompressed", StringComparison.OrdinalIgnoreCase) && !EngineTypes.KeepRaw(path);
		}

		/// <summary>The cache key of a texture's data: its blocks (BC) or texels (RGBA), all levels, with what they are.</summary>
		public static ulong KeyOf(int kind, int width, int height, bool srgb, byte[][] levels)
		{
			var hash = new XxHash3();
			Span<byte> header = stackalloc byte[24];
			BitConverter.TryWriteBytes(header, Version);
			BitConverter.TryWriteBytes(header.Slice(4), kind);
			BitConverter.TryWriteBytes(header.Slice(8), width);
			BitConverter.TryWriteBytes(header.Slice(12), height);
			BitConverter.TryWriteBytes(header.Slice(16), levels.Length | (srgb ? 1 << 16 : 0));
			BitConverter.TryWriteBytes(header.Slice(20), quality);
			hash.Append(header);
			foreach (byte[] level in levels)
			{
				hash.Append(level);
			}
			return hash.GetCurrentHashAsUInt64();
		}

		/// <summary>The ASTC levels of this key from the cache, as uploads (format: the ASTC format to make the image in).</summary>
		public static bool TryCached(ulong key, int width, int height, int mipCount, out TextureLevelUpload[] uploads, out VkFormat format)
		{
			uploads = null;
			format = default;
			if (!TryRead(key, width, height, mipCount, out byte[][] levels, out bool srgb))
			{
				Interlocked.Increment(ref misses);
				return false;
			}
			uploads = Uploads(width, height, levels);
			format = srgb ? VkFormat.Astc4X4SRGBBlock : VkFormat.Astc4X4UNormBlock;
			long count = Interlocked.Increment(ref hits);
			long rgba = 0, astc = 0;
			for (int i = 0; i < levels.Length; i++)
			{
				rgba += (long)Math.Max(1, width >> i) * Math.Max(1, height >> i) * 4;
				astc += levels[i].Length;
			}
			Interlocked.Add(ref savedBytes, rgba - astc);
			if (count <= 3 || count % 1000 == 0)
			{
				Report("texture " + count + " from the cache");
			}
			return true;
		}

		private static TextureLevelUpload[] Uploads(int width, int height, byte[][] levels)
		{
			var uploads = new TextureLevelUpload[levels.Length];
			for (int i = 0; i < levels.Length; i++)
			{
				uploads[i] = new TextureLevelUpload(i, Math.Max(1, width >> i), Math.Max(1, height >> i), 1, 0, levels[i], levels[i].Length);
			}
			return uploads;
		}

		/// <summary>RGBA texels of every level (tightly packed; copied when they may change afterwards) to encode in the background and keep under key.</summary>
		public static void Enqueue(ulong key, int width, int height, bool srgb, byte[][] texels, bool copy = false)
		{
			if (known.ContainsKey(key) || !pending.TryAdd(key, true))
			{
				return;
			}
			long bytes = 0;
			foreach (byte[] level in texels)
			{
				bytes += level.Length;
			}
			// One texture over the limit is still taken when nothing else waits (the menu's background: 203 MB of RGBA).
			long queued = Interlocked.Add(ref queuedBytes, bytes);
			if (Interlocked.Read(ref cacheBytes) + bytes / 4 > cacheLimit || (queued > QueueLimit && queued != bytes))
			{
				Interlocked.Add(ref queuedBytes, -bytes);
				pending.TryRemove(key, out _);
				Interlocked.Increment(ref dropped);
				return;
			}
			if (copy)
			{
				texels = Array.ConvertAll(texels, level => (byte[])level.Clone());
			}
			if (Interlocked.CompareExchange(ref workersStarted, 1, 0) == 0)
			{
				for (int i = 0; i < workerCount; i++)
				{
					new Thread(Work) { IsBackground = true, Name = "port17 astc " + i }.Start();
				}
			}
			Volatile.Write(ref idleReported, 0);
			jobs.Add(new Job { Key = key, Width = width, Height = height, Srgb = srgb, Levels = texels, Bytes = bytes });
		}

		private static void Work()
		{
			try
			{
				// Below the game's threads (nice 10): only what they leave over.
				setpriority(0, gettid(), 10);
			}
			catch (Exception)
			{
			}
			foreach (Job job in jobs.GetConsumingEnumerable())
			{
				// While the atlases are made the loading screen waits for every core.
				while (Volatile.Read(ref baking) > 0)
				{
					Thread.Sleep(100);
				}
				try
				{
					long started = System.Diagnostics.Stopwatch.GetTimestamp();
					var blocks = new byte[job.Levels.Length][];
					long texels = 0;
					for (int i = 0; i < blocks.Length; i++)
					{
						int w = Math.Max(1, job.Width >> i), h = Math.Max(1, job.Height >> i);
						blocks[i] = new byte[BlockBytes(w, h)];
						int error = port_astc_encode(job.Levels[i], w, h, job.Srgb ? 1 : 0, quality, blocks[i], (UIntPtr)blocks[i].Length);
						if (error != 0)
						{
							throw new InvalidOperationException("astcenc error " + error + " at level " + i + " (" + w + "x" + h + ")");
						}
						texels += (long)w * h;
					}
					job.Levels = null;
					Write(job.Key, job.Width, job.Height, job.Srgb, blocks);
					Interlocked.Add(ref encodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
					Interlocked.Add(ref encodedTexels, texels);
					long count = Interlocked.Increment(ref encoded);
					// The first, every 500th, and once each time the queue runs dry.
					if (count == 1 || count % 500 == 0 || (jobs.Count == 0 && Interlocked.Exchange(ref idleReported, 1) == 0))
					{
						Report("encoded " + count + " in the background");
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("[port17] astc: encoding failed: " + e.Message);
				}
				finally
				{
					pending.TryRemove(job.Key, out _);
					Interlocked.Add(ref queuedBytes, -job.Bytes);
				}
			}
		}

		private static void Report(string what)
		{
			double seconds = Interlocked.Read(ref encodeTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
			Console.WriteLine("[port17] astc: " + what + "; textures " + Interlocked.Read(ref hits) + " from the cache, " + Interlocked.Read(ref misses) + " not yet (" + jobs.Count + " waiting, " + Interlocked.Read(ref dropped) + " left for later), "
				+ Interlocked.Read(ref encoded) + " encoded (" + (Interlocked.Read(ref encodedTexels) / 1e6).ToString("F0") + " Mtexels, " + seconds.ToString("F1") + " s); " + (Interlocked.Read(ref savedBytes) >> 20) + " MB less than RGBA; cache " + (Interlocked.Read(ref cacheBytes) >> 20) + " MB");
		}

		// ---- the static atlases (Verse, Assembly-CSharp: by name, loaded before the texture compression is configured)

		private static MethodInfo cacheKey, nameTextures;
		private static PropertyInfo colorPage, maskPage, coreOf;

		private static void PatchAtlases(Harmony harmony)
		{
			Type atlas = AccessTools.TypeByName("Verse.StaticTextureAtlas");
			Type manager = AccessTools.TypeByName("Verse.GlobalTextureAtlasManager");
			Type unityTexture = AccessTools.TypeByName("UnityEngine.Texture2D");
			cacheKey = atlas == null ? null : AccessTools.Method(atlas, "CacheKey", new[] { typeof(bool) });
			nameTextures = atlas == null ? null : AccessTools.Method(atlas, "NameAtlasTextures");
			colorPage = atlas == null ? null : AccessTools.Property(atlas, "ColorTexture");
			maskPage = atlas == null ? null : AccessTools.Property(atlas, "MaskTexture");
			// Texture2D's own (Waylith.Rendering.Texture2D), not the base class's of the same name (Waylith.Rendering.Texture).
			coreOf = unityTexture == null ? null : AccessTools.DeclaredProperty(unityTexture, "CoreTexture");
			MethodInfo adopt = atlas == null ? null : AccessTools.Method(atlas, "TryAdoptCachedPages");
			MethodInfo compress = manager == null ? null : AccessTools.Method(manager, "CompressAtlas");
			MethodInfo bake = manager == null ? null : AccessTools.Method(manager, "BakeStaticAtlases");
			if (cacheKey == null || nameTextures == null || colorPage == null || maskPage == null || coreOf == null || adopt == null || compress == null || bake == null)
			{
				Console.WriteLine("[port17] astc: the game's atlas code is not as expected; atlases stay RGBA");
				return;
			}
			harmony.Patch(adopt, postfix: new HarmonyMethod(typeof(Astc), nameof(AfterTryAdoptCachedPages)));
			harmony.Patch(compress, prefix: new HarmonyMethod(typeof(Astc), nameof(BeforeCompressAtlas)));
			harmony.Patch(bake, prefix: new HarmonyMethod(typeof(Astc), nameof(BeforeBakeStaticAtlases)), postfix: new HarmonyMethod(typeof(Astc), nameof(AfterBakeStaticAtlases)));
		}

		private static Waylith.Rendering.Texture2D Core(object page)
		{
			return page == null ? null : coreOf.GetValue(page) as Waylith.Rendering.Texture2D;
		}

		private static ulong AtlasKey(string cacheKey)
		{
			var hash = new XxHash3();
			hash.Append(Encoding.UTF8.GetBytes("atlas " + Version + " " + atlasQuality.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + cacheKey));
			return hash.GetCurrentHashAsUInt64();
		}

		/// <summary>For PortMobile.AtlasCheck: one level of an atlas page encoded as the pages are, on every core (null when astcenc fails).</summary>
		public static byte[] EncodeAtlasLevel(byte[] rgba, int width, int height, bool srgb)
		{
			var blocks = new byte[BlockBytes(width, height)];
			int error = port_astc_encode_parallel(rgba, width, height, srgb ? 1 : 0, atlasQuality, blocks, (UIntPtr)blocks.Length, width * height >= 65536 ? Environment.ProcessorCount : 1);
			port_astc_release();
			return error == 0 ? blocks : null;
		}

		/// <summary>For PortMobile.AtlasCheck: a repaired page in place of its cache entry.</summary>
		public static void RewriteAtlasPage(string cacheKey, int width, int height, bool srgb, byte[][] levels)
		{
			if (On && cacheKey != null)
			{
				Write(AtlasKey(cacheKey), width, height, srgb, levels);
			}
		}

		/// <summary>For PortMobile.AtlasCheck: a page with textures missing is dropped from the cache; the next start draws it again.</summary>
		public static bool DropAtlasPage(string cacheKey)
		{
			if (!On || cacheKey == null)
			{
				return false;
			}
			string file = FileOf(AtlasKey(cacheKey));
			bool was = File.Exists(file);
			TryDelete(file);
			return was;
		}

		public static void BeforeBakeStaticAtlases()
		{
			Interlocked.Increment(ref baking);
		}

		public static void AfterBakeStaticAtlases()
		{
			Interlocked.Decrement(ref baking);
			if (Interlocked.Read(ref atlasFromCache) + Interlocked.Read(ref atlasEncoded) > 0)
			{
				double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				Console.WriteLine("[port17] astc: atlas pages: " + atlasFromCache + " from the cache, " + atlasEncoded + " encoded (" + (atlasTexels / 1e6).ToString("F0") + " Mtexels in " + (atlasTicks * ms).ToString("F0") + " ms: read back " + (atlasReadTicks * ms).ToString("F0") + ", encoded " + (atlasEncodeTicks * ms).ToString("F0") + ", the rest uploads)");
			}
			// The shared encoder's working areas (one per core) and the pages' readback are not needed until the next bake.
			readback = null;
			port_astc_release();
		}

		/// <summary>Postfix of StaticTextureAtlas.TryAdoptCachedPages (false here: the engine's compression is off): the pages from the ASTC cache, then nothing is drawn.</summary>
		public static void AfterTryAdoptCachedPages(object __instance, ref bool __result)
		{
			if (__result || !On)
			{
				return;
			}
			GpuTextureAllocation color = null, mask = null;
			try
			{
				Waylith.Rendering.Texture2D colorCore = Core(colorPage.GetValue(__instance)), maskCore = Core(maskPage.GetValue(__instance));
				string colorKey = (string)cacheKey.Invoke(__instance, new object[] { false });
				string maskKey = maskCore == null ? null : (string)cacheKey.Invoke(__instance, new object[] { true });
				if (colorCore == null || colorKey == null || (maskCore != null && maskKey == null))
				{
					return;
				}
				// Both pages made before either is taken: half an atlas in ASTC would get the other half drawn into it.
				color = CachedPage(colorCore, AtlasKey(colorKey));
				if (color == null)
				{
					return;
				}
				if (maskCore != null)
				{
					mask = CachedPage(maskCore, AtlasKey(maskKey));
					if (mask == null)
					{
						color.Dispose();
						return;
					}
				}
				Adopt(colorCore, color);
				color = null;
				if (maskCore != null)
				{
					Adopt(maskCore, mask);
					mask = null;
				}
				Interlocked.Add(ref atlasFromCache, maskCore != null ? 2 : 1);
				nameTextures.Invoke(__instance, null);
				__result = true;
			}
			catch (Exception e)
			{
				color?.Dispose();
				mask?.Dispose();
				Console.WriteLine("[port17] astc: an atlas from the cache failed: " + e);
			}
		}

		private static GpuTextureAllocation CachedPage(Waylith.Rendering.Texture2D page, ulong key)
		{
			int mipCount = Math.Max(1, page.MipCount);
			if (!TryRead(key, page.width, page.height, mipCount, out byte[][] levels, out bool srgb))
			{
				return null;
			}
			return GpuTextureService.CreateTexture2D(page.Name ?? "TextureAtlas", page.width, page.height, mipCount, srgb ? VkFormat.Astc4X4SRGBBlock : VkFormat.Astc4X4UNormBlock, Uploads(page.width, page.height, levels), GpuMemoryCategory.Atlas);
		}

		/// <summary>As the engine takes a BC7 page (GlobalTextureAtlasManager.TryEncodeOnGpu, ReleaseCpuLevels): "Bc7" tells it the image is block compressed, of the same size.</summary>
		private static void Adopt(Waylith.Rendering.Texture2D page, GpuTextureAllocation image)
		{
			page.AdoptEncodedImage(image, LoadedTextureFormat.Bc7, image.MipCount);
			EngineTypes.ReleaseCpuData(page);
		}

		/// <summary>Prefix of GlobalTextureAtlasManager.CompressAtlas (which does nothing with the compression off): the drawn pages to ASTC, and into the cache.</summary>
		public static bool BeforeCompressAtlas(object atlas)
		{
			if (!On)
			{
				return true;
			}
			try
			{
				EncodePage(Core(colorPage.GetValue(atlas)), (string)cacheKey.Invoke(atlas, new object[] { false }));
				Waylith.Rendering.Texture2D maskCore = Core(maskPage.GetValue(atlas));
				if (maskCore != null)
				{
					EncodePage(maskCore, (string)cacheKey.Invoke(atlas, new object[] { true }));
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] astc: an atlas page could not be encoded: " + e);
			}
			return false;
		}

		private static void EncodePage(Waylith.Rendering.Texture2D page, string key)
		{
			GpuTextureAllocation gpu = page?.GpuResource;
			if (gpu == null || !(EngineTypes.CpuFormatOf(page) is Waylith.Rendering.LoadedTextureFormat cpuFormat) || EngineTypes.IsBlockCompressed(cpuFormat) || (gpu.Format != VkFormat.R8G8B8A8UNorm && gpu.Format != VkFormat.R8G8B8A8SRGB))
			{
				return;
			}
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			int width = page.width, height = page.height, mipCount = Math.Max(1, gpu.MipCount);
			bool srgb = gpu.Format == VkFormat.R8G8B8A8SRGB;
			// The pages are mostly of one size: the same arrays for each (released after the bake), not 100 MB of new ones per page.
			byte[][] texels = GpuTextureService.ReadbackTexture2DLevels(gpu, width, height, mipCount, 4, readback);
			if (texels == null || texels.Length < mipCount)
			{
				return;
			}
			readback = texels;
			long read = System.Diagnostics.Stopwatch.GetTimestamp();
			var blocks = new byte[mipCount][];
			long count = 0;
			for (int i = 0; i < mipCount; i++)
			{
				int w = Math.Max(1, width >> i), h = Math.Max(1, height >> i);
				blocks[i] = new byte[BlockBytes(w, h)];
				int threads = w * h >= 65536 ? Environment.ProcessorCount : 1;
				int error = port_astc_encode_parallel(texels[i], w, h, srgb ? 1 : 0, atlasQuality, blocks[i], (UIntPtr)blocks[i].Length, threads);
				if (error != 0)
				{
					Console.WriteLine("[port17] astc: astcenc error " + error + " for " + page.Name + " level " + i + "; the page stays RGBA");
					return;
				}
				count += (long)w * h;
			}
			long encodedAt = System.Diagnostics.Stopwatch.GetTimestamp();
			GpuTextureAllocation image = GpuTextureService.CreateTexture2D(page.Name ?? "TextureAtlas", width, height, mipCount, srgb ? VkFormat.Astc4X4SRGBBlock : VkFormat.Astc4X4UNormBlock, Uploads(width, height, blocks), GpuMemoryCategory.Atlas);
			if (image == null)
			{
				return;
			}
			Adopt(page, image);
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			Interlocked.Increment(ref atlasEncoded);
			Interlocked.Add(ref atlasTexels, count);
			Interlocked.Add(ref atlasTicks, now - started);
			Interlocked.Add(ref atlasReadTicks, read - started);
			Interlocked.Add(ref atlasEncodeTicks, encodedAt - read);
			if (key != null)
			{
				ulong atlasKey = AtlasKey(key);
				Task.Run(() => Write(atlasKey, width, height, srgb, blocks));
			}
		}
	}
}
