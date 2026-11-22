using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Brutal.VulkanApi;
using HarmonyLib;
using Waylith;
using Waylith.Graphics;
using Waylith.Rendering;

namespace PortHooks
{
	/// <summary>
	/// Block-compressed textures on a GPU that cannot sample them.
	///
	/// The engine's textures are BC formats, the ones every PC graphics card reads: the game's own 3 308 textures in
	/// Assets.zip are BC7 (and 99 are BC4), a mod's .dds files are BC1 / BC3 / BC7, and every other texture is encoded
	/// to BC7 when it loads (Bootstrap.ConfigureTextureCompression: "-texFormat", BC7 when not given). Qualcomm's Adreno
	/// reads them; ARM's Mali (Dimensity, Exynos before the Xclipse, Tensor, Kirin) does not: there the very first
	/// image fails ("VMA failed to allocate image ... Bc7UNormBlock: ErrorInitializationFailed", a Redmi K80 Ultra) and
	/// with it every window of the game.
	///
	/// When the device has no textureCompressionBC: the encoding at load time is switched off (textures then load as
	/// RGBA, as with "-texFormat=none"), and an image the engine asks for in a BC format is made in the plain format
	/// with the same channels instead (BC1/BC2/BC3/BC7 -> RGBA8, BC4 -> R8, BC5 -> RG8, sRGB kept), its data decoded
	/// on the processor with the engine's own block decoders before the upload. A decoded texture takes 4 times the
	/// memory of a BC7 one (8 times a BC1 one); the engine loads textures when they are first drawn. Where the GPU
	/// has ASTC, Astc.cs takes it from there: those textures and the atlases become ASTC 4x4, the size of BC7.
	///
	/// mobile.txt: bcDecode=1 decodes on any GPU (to try this on a phone that reads BC), bcDecode=0 never.
	/// </summary>
	internal static class Textures
	{
		private static Harmony harmony;
		private static long decodedTextures, decodedBytes, decodeTicks;
		private static int unhandledLogged;

		/// <summary>From the startup hook: the decision waits for the GPU, which exists when the engine configures the texture compression.</summary>
		public static void Start()
		{
			try
			{
				Type bootstrap = AccessTools.TypeByName("RimWorldWaylith.Bootstrap");
				var configure = bootstrap == null ? null : AccessTools.Method(bootstrap, "ConfigureTextureCompression");
				if (configure == null)
				{
					Console.WriteLine("[port17] textures: Bootstrap.ConfigureTextureCompression not found; BC textures are left to the GPU");
					return;
				}
				harmony = new Harmony("port17.textures");
				harmony.Patch(configure, postfix: new HarmonyMethod(typeof(Textures), nameof(AfterCompressionConfigured)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] textures: not installed: " + e.Message);
			}
		}

		/// <summary>A value of mobile.txt ("" when not there).</summary>
		internal static string Setting(string name)
		{
			try
			{
				string file = Path.Combine(AppContext.BaseDirectory, "mobile.txt");
				if (File.Exists(file))
				{
					foreach (string line in File.ReadLines(file))
					{
						string[] parts = line.Split('=');
						if (parts.Length == 2 && parts[0].Trim() == name)
						{
							return parts[1].Trim();
						}
					}
				}
			}
			catch (Exception)
			{
			}
			return "";
		}

		// ---- the sources of the static atlases, as they are drawn into a page

		private static System.Reflection.FieldInfo atlasTextures, pendingBuild, gpuResource;
		private static System.Reflection.PropertyInfo coreOfTexture;
		private static bool atlasSourceErrorSaid;

		/// <summary>
		/// A tester's Adreno 730 had textures missing from cached atlas pages (PortMobile.AtlasCheck finds such pages now).
		/// The engine bakes the pages on the loading thread (PlayDataLoader.DoPlayLoad); a source still being built there is
		/// not finished (TextureBuildBatch.Resolve does that on the main thread only), and a source let go after an earlier
		/// page (the engine evicts a page's sources once it is baked; one texture can be in several pages: the comms console
		/// is in Building, Item and Misc) comes back from the texture cache. Before a page is drawn, each source that is not
		/// ready is made ready the engine's own way (as its draw would), and the log says how many were not and which stayed
		/// so: what went wrong on such a phone shows in its next log.
		/// </summary>
		private static void WatchAtlasSources()
		{
			try
			{
				Type atlas = AccessTools.TypeByName("Verse.StaticTextureAtlas");
				System.Reflection.MethodInfo blit = atlas == null ? null : AccessTools.Method(atlas, "BlitTexturesToColorAtlas");
				atlasTextures = atlas == null ? null : AccessTools.Field(atlas, "textures");
				Type unityTexture = AccessTools.TypeByName("UnityEngine.Texture2D");
				coreOfTexture = unityTexture == null ? null : AccessTools.DeclaredProperty(unityTexture, "CoreTexture");
				pendingBuild = AccessTools.Field(typeof(Waylith.Rendering.Texture), "PendingBuild");
				gpuResource = AccessTools.Field(typeof(Waylith.Rendering.Texture), "_gpuResource");
				if (blit == null || atlasTextures == null || coreOfTexture == null || pendingBuild == null || gpuResource == null)
				{
					Console.WriteLine("[port17] atlas sources: the game's atlas code is not as expected; not watched");
					return;
				}
				harmony.Patch(blit, prefix: new HarmonyMethod(typeof(Textures), nameof(BeforeAtlasBlit)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] atlas sources: not watched: " + e.Message);
			}
		}

		public static void BeforeAtlasBlit(object __instance)
		{
			try
			{
				if (!(atlasTextures.GetValue(__instance) is System.Collections.IList list))
				{
					return;
				}
				int building = 0, away = 0, notReady = 0;
				var names = new System.Collections.Generic.List<string>();
				foreach (object texture in list)
				{
					if (!(coreOfTexture.GetValue(texture) is Waylith.Rendering.Texture core))
					{
						continue;
					}
					bool pending = pendingBuild.GetValue(core) != null;
					if (!pending && gpuResource.GetValue(core) != null)
					{
						continue;
					}
					if (pending)
					{
						building++;
					}
					else
					{
						away++;
					}
					if (!core.EnsureResident())
					{
						notReady++;
						if (names.Count < 8)
						{
							names.Add(core.Name);
						}
					}
				}
				if (building + away > 0)
				{
					Console.WriteLine("[port17] atlas sources: a page of " + list.Count + " textures, drawn on thread " + Environment.CurrentManagedThreadId + " (main " + Waylith.Runtime.WaylithRuntime.Current?.App.MainThreadId + "): "
						+ building + " still being built, " + away + " not in graphics memory; " + notReady + " not ready after EnsureResident" + (names.Count > 0 ? " (" + string.Join(", ", names) + ")" : ""));
				}
			}
			catch (Exception e)
			{
				if (!atlasSourceErrorSaid)
				{
					atlasSourceErrorSaid = true;
					Console.WriteLine("[port17] atlas sources: " + e.GetType().Name + ": " + e.Message);
				}
			}
		}

		/// <summary>
		/// On Adreno GPUs below the 800s the static atlas pages are encoded to BC7 on the processor, not with the engine's
		/// GPU encoder (GlobalTextureAtlasManager.TryEncodeOnGpu, whose output is read back and becomes the cache entry).
		/// Pages made on such GPUs have had regions empty at the large levels while a small level showed other things
		/// there: an Adreno 720's Item page (level 1 empty), an Adreno 725's Plant page (level 0 cut off below a line), a
		/// tester's Adreno 730 (2026-10-25: the comms console invisible, its tile empty at levels 0 to 2 and level 3 holding
		/// pieces of other sprites, the same in the engine's cache). The levels do not belong together, so the encoder read
		/// a page that was not all there yet; the OnePlus (Adreno 825) never showed it. Declined, the engine takes its own
		/// way for this case, CompressInPlace: it reads the page back (after the GPU's work on it) and encodes it with
		/// CpuBlockEncoder, then writes the cache from that. It costs time only when a page is made, not when it comes
		/// from the cache. mobile.txt atlasCpuEncode=1 / 0 for any GPU.
		/// </summary>
		private static void EncodeAtlasesOnCpu()
		{
			try
			{
				string setting = Setting("atlasCpuEncode");
				bool on = setting == "1";
				string gpuName = "";
				if (setting != "0" && setting != "1")
				{
					gpuName = Waylith.Graphics.GpuInfo.DeviceName ?? "";
					// "Adreno (TM) 730": the first three digits after the word.
					int at = gpuName.IndexOf("Adreno", StringComparison.OrdinalIgnoreCase), model = 0, digits = 0;
					for (int i = at < 0 ? gpuName.Length : at + 6; i < gpuName.Length && digits < 3; i++)
					{
						if (char.IsDigit(gpuName[i]))
						{
							model = model * 10 + (gpuName[i] - '0');
							digits++;
						}
						else if (digits > 0)
						{
							break;
						}
					}
					on = digits == 3 && model < 800;
				}
				if (!on)
				{
					return;
				}
				Type manager = AccessTools.TypeByName("Verse.GlobalTextureAtlasManager");
				System.Reflection.MethodInfo onGpu = manager == null ? null : AccessTools.Method(manager, "TryEncodeOnGpu");
				if (onGpu == null)
				{
					Console.WriteLine("[port17] atlas pages: the game's atlas compression is not as expected; left on the GPU");
					return;
				}
				harmony.Patch(onGpu, prefix: new HarmonyMethod(typeof(Textures), nameof(NotOnGpu)));
				atlasesOnCpu = true;
				Console.WriteLine("[port17] atlas pages: encoded on the processor (" + (setting == "1" ? "atlasCpuEncode=1" : gpuName) + ")");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] atlas pages: left on the GPU: " + e.Message);
			}
		}

		public static bool NotOnGpu(ref bool __result)
		{
			__result = false;
			return false;
		}

		// The GPU was lost (VK_ERROR_DEVICE_LOST) in an earlier run: noted by NoteDeviceLost, read at the next starts.
		private static bool atlasesOnCpu;
		private static string GpuLostNote => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs", "gpu-lost.txt"));

		/// <summary>
		/// The textures a game loads in batches (TextureBuildBatch: a mod's PNGs, mip levels and BC7 made by compute
		/// shaders) are made on the processor for 7 days after the GPU was lost in a run, as the atlas pages are on
		/// Adreno below 800. A tester's Adreno 730 (Android 13 driver, Vulkan 1.1) lost the device on 1.7.4990 in the
		/// middle of those batches at the start (2026-10-27; the same phone had loaded on 4985 for days); the engine then
		/// made the rest on the processor anyway, the process grew to 7.8 GB and ended in VMA's assertion while the
		/// engine shut down. Declined here, the engine takes its own processor path (CompressAtLoad); the cache stays the
		/// same. mobile.txt gpuTextureBuild=0 always on the processor, =1 never.
		/// </summary>
		private static void BuildTexturesOnCpuAfterLostGpu()
		{
			try
			{
				string setting = Setting("gpuTextureBuild");
				string why = setting == "0" ? "gpuTextureBuild=0 in mobile.txt" : null;
				if (why == null && setting != "1" && File.Exists(GpuLostNote) && DateTime.UtcNow - File.GetLastWriteTimeUtc(GpuLostNote) < TimeSpan.FromDays(7))
				{
					why = "the GPU was lost in a run of " + File.GetLastWriteTime(GpuLostNote).ToString("MM-dd HH:mm") + " (logs/gpu-lost.txt)";
				}
				if (why == null)
				{
					return;
				}
				System.Reflection.MethodInfo canBuild = AccessTools.Method(AccessTools.TypeByName("Waylith.Rendering.TextureBuildBatch"), "CanBuild");
				Type manager = AccessTools.TypeByName("Verse.GlobalTextureAtlasManager");
				System.Reflection.MethodInfo onGpu = manager == null ? null : AccessTools.Method(manager, "TryEncodeOnGpu");
				if (canBuild != null)
				{
					harmony.Patch(canBuild, prefix: new HarmonyMethod(typeof(Textures), nameof(NotOnGpu)));
				}
				if (onGpu != null && !atlasesOnCpu)
				{
					harmony.Patch(onGpu, prefix: new HarmonyMethod(typeof(Textures), nameof(NotOnGpu)));
					atlasesOnCpu = true;
				}
				Console.WriteLine("[port17] textures and atlas pages: made on the processor (" + why + ")" + (canBuild == null ? "; this engine has no texture batches" : ""));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] textures: left on the GPU: " + e.Message);
			}
		}

		/// <summary>Postfix of GpuCrashDiagnostics.CaptureDeviceLost (1.7.4990 and later): the note for the next starts.</summary>
		public static void NoteDeviceLost(string where)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(GpuLostNote));
				File.AppendAllText(GpuLostNote, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + (GpuInfo.DeviceName ?? "?") + ": " + where + "\n");
				Console.WriteLine("[port17] the GPU was lost (" + where + "); the next starts make textures on the processor for 7 days (logs/gpu-lost.txt)");
			}
			catch (Exception)
			{
			}
		}

		/// <summary>
		/// Engines before 1.7.4990 have no GpuCrashDiagnostics: the engine's own crash report says it instead
		/// ("Vulkan call to vkDeviceWaitIdle failed with error: VK_ERROR_DEVICE_LOST", Crashes/crash-*.txt). A tester's vivo
		/// on 1.7.4976 (Adreno 830) lost the device four times in four days, each time just as a new game's map came up,
		/// and never got the processor path (2026-10-28). Such a report of the last 7 days, newer than the note, is noted.
		/// </summary>
		private static void NoteLostGpuFromCrashReports()
		{
			try
			{
				string home = Environment.GetEnvironmentVariable("HOME");
				string crashes = string.IsNullOrEmpty(home) ? null : Path.Combine(home, "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios", "Crashes");
				if (crashes == null || !Directory.Exists(crashes))
				{
					return;
				}
				DateTime since = DateTime.UtcNow - TimeSpan.FromDays(7);
				if (File.Exists(GpuLostNote) && File.GetLastWriteTimeUtc(GpuLostNote) > since)
				{
					since = File.GetLastWriteTimeUtc(GpuLostNote);
				}
				foreach (string report in Directory.GetFiles(crashes, "crash-*.txt"))
				{
					if (File.GetLastWriteTimeUtc(report) > since && new FileInfo(report).Length < (1 << 20) && File.ReadAllText(report).Contains("VK_ERROR_DEVICE_LOST"))
					{
						NoteDeviceLost("the engine's crash report " + Path.GetFileName(report));
						return;
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] device lost: crash reports not read: " + e.Message);
			}
		}

		private static void WatchDeviceLost()
		{
			NoteLostGpuFromCrashReports();
			try
			{
				System.Reflection.MethodInfo capture = AccessTools.Method(AccessTools.TypeByName("Waylith.Graphics.GpuCrashDiagnostics"), "CaptureDeviceLost", new[] { typeof(string) });
				if (capture != null)
				{
					harmony.Patch(capture, postfix: new HarmonyMethod(typeof(Textures), nameof(NoteDeviceLost)));
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] device lost: not watched: " + e.Message);
			}
		}

		public static void AfterCompressionConfigured()
		{
			WatchAtlasSources();
			EncodeAtlasesOnCpu();
			WatchDeviceLost();
			BuildTexturesOnCpuAfterLostGpu();
			try
			{
				string setting = Setting("bcDecode");
				bool decode;
				string why;
				PhysicalDevice gpu = EngineApplication.Instance?.PhysicalDevice;
				if (setting == "1" || setting == "0")
				{
					decode = setting == "1";
					why = "bcDecode=" + setting + " in mobile.txt";
				}
				else
				{
					if (gpu == null)
					{
						Console.WriteLine("[port17] textures: no GPU to ask yet; BC textures are left to the GPU");
						return;
					}
					decode = !gpu.GetFeatures().TextureCompressionBC;
					why = decode ? "the GPU cannot sample BC textures" : "the GPU samples BC textures";
				}
				if (!decode)
				{
					Console.WriteLine("[port17] textures: " + why + "; left as they are");
					return;
				}
				EngineTypes.ConfigureNoBcEncoding();
				var service = typeof(GpuTextureService);
				harmony.Patch(AccessTools.Method(service, "CreateTexture2D"), prefix: new HarmonyMethod(typeof(Textures), nameof(BeforeCreateTexture2D)));
				harmony.Patch(AccessTools.Method(service, "AllocateTexture2D"), prefix: new HarmonyMethod(typeof(Textures), nameof(PlainFormat)));
				harmony.Patch(AccessTools.Method(service, "CreateEmptyTexture2D"), prefix: new HarmonyMethod(typeof(Textures), nameof(PlainFormat)));
				harmony.Patch(AccessTools.Method(service, "CreateTexture2DArrayCore"), prefix: new HarmonyMethod(typeof(Textures), nameof(PlainFormat)));
				harmony.Patch(AccessTools.Method(service, "CreateTexture3D"), prefix: new HarmonyMethod(typeof(Textures), nameof(BeforeCreateTexture3D)));
				Console.WriteLine("[port17] textures: " + why + ": BC encoding at load time is off, BC textures are decoded before they go to the GPU");
				Astc.Start(harmony, gpu);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] textures: the BC fallback failed to install: " + e);
			}
		}

		/// <summary>The plain format with the channels of a BC one, and the kind of block (0: not BC; -1: a BC format not decoded here).</summary>
		private static VkFormat Plain(VkFormat format, out int kind)
		{
			switch (format)
			{
			case VkFormat.Bc1RGBUNormBlock: kind = 11; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc1RGBSRGBBlock: kind = 11; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc1RGBAUNormBlock: kind = 1; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc1RGBASRGBBlock: kind = 1; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc2UNormBlock: kind = 2; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc2SRGBBlock: kind = 2; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc3UNormBlock: kind = 3; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc3SRGBBlock: kind = 3; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc4UNormBlock: kind = 4; return VkFormat.R8UNorm;
			case VkFormat.Bc5UNormBlock: kind = 5; return VkFormat.R8G8UNorm;
			case VkFormat.Bc7UNormBlock: kind = 7; return VkFormat.R8G8B8A8UNorm;
			case VkFormat.Bc7SRGBBlock: kind = 7; return VkFormat.R8G8B8A8SRGB;
			case VkFormat.Bc4SNormBlock:
			case VkFormat.Bc5SNormBlock:
			case VkFormat.Bc6HUfloatBlock:
			case VkFormat.Bc6HSFloatBlock:
				kind = -1;
				return format;
			default:
				kind = 0;
				return format;
			}
		}

		/// <summary>Prefix of the image allocations that take no data (the data, if any, is plain then too: the engine's own uploads of RGBA).</summary>
		public static void PlainFormat(ref VkFormat format, string label)
		{
			VkFormat plain = Plain(format, out int kind);
			if (kind > 0)
			{
				format = plain;
			}
			else if (kind < 0)
			{
				Unhandled(format, label);
			}
		}

		/// <summary>
		/// Prefix of GpuTextureService.CreateTexture2D: a BC texture becomes a plain one, every level decoded; or ASTC from
		/// its cache (Astc.cs), as does a file's RGBA texture.
		/// </summary>
		public static void BeforeCreateTexture2D(string label, int width, int height, int mipCount, ref VkFormat format, ref ReadOnlySpan<TextureLevelUpload> levels, GpuMemoryCategory category, ref TextureLevelFill fill)
		{
			Waylith.Rendering.Texture2D owner = Astc.On ? Astc.TakeUploading(width, height) : null;
			VkFormat plain = Plain(format, out int kind);
			if (kind == 0)
			{
				if (owner != null && (format == VkFormat.R8G8B8A8UNorm || format == VkFormat.R8G8B8A8SRGB) && Astc.Wanted(width, height, mipCount, category, levels) && Astc.WouldBeCompressed(owner))
				{
					RgbaToAstc(width, height, ref format, ref levels, ref fill);
				}
				return;
			}
			if (kind < 0)
			{
				Unhandled(format, label);
				return;
			}
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			var source = new byte[levels.Length][];
			for (int i = 0; i < levels.Length; i++)
			{
				TextureLevelUpload level = levels[i];
				if (level.Size == 0)
				{
					continue;
				}
				source[i] = level.Bytes;
				if (fill != null)
				{
					source[i] = new byte[level.Size];
					fill(level.Level, source[i]);
				}
			}
			// BC4 and BC5 (one and two channels) stay R8 and RG8: no smaller in ASTC.
			bool srgb = plain == VkFormat.R8G8B8A8SRGB;
			bool astc = kind != 4 && kind != 5 && Astc.Wanted(width, height, mipCount, category, levels);
			ulong key = 0;
			if (astc)
			{
				key = Astc.KeyOf(kind, width, height, srgb, source);
				if (Astc.TryCached(key, width, height, levels.Length, out TextureLevelUpload[] cached, out VkFormat astcFormat))
				{
					format = astcFormat;
					levels = cached;
					fill = null;
					return;
				}
			}
			var decoded = new TextureLevelUpload[levels.Length];
			var texels = new byte[levels.Length][];
			long bytes = 0;
			for (int i = 0; i < levels.Length; i++)
			{
				TextureLevelUpload level = levels[i];
				if (source[i] == null)
				{
					decoded[i] = level;
					continue;
				}
				texels[i] = Decode(source[i], level.Width, level.Height, kind);
				bytes += texels[i].Length;
				decoded[i] = new TextureLevelUpload(level.Level, level.Width, level.Height, level.Depth, 0, texels[i], texels[i].Length);
			}
			format = plain;
			levels = decoded;
			fill = null;
			if (astc)
			{
				// The decoded levels are only read from here on (CreateTexture2D copies them to its staging buffers).
				Astc.Enqueue(key, width, height, srgb, texels);
			}
			long count = Interlocked.Increment(ref decodedTextures);
			Interlocked.Add(ref decodedBytes, bytes);
			long ticks = Interlocked.Add(ref decodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
			if (count <= 3 || count % 500 == 0)
			{
				Console.WriteLine("[port17] textures: " + count + " BC textures decoded so far, " + (Interlocked.Read(ref decodedBytes) / 1048576.0).ToString("F0") + " MB, " + (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F0") + " ms (latest: " + label + ")");
			}
		}

		/// <summary>A file's RGBA texture (every level there): ASTC from the cache, or RGBA now and ASTC for the next time.</summary>
		private static void RgbaToAstc(int width, int height, ref VkFormat format, ref ReadOnlySpan<TextureLevelUpload> levels, ref TextureLevelFill fill)
		{
			var texels = new byte[levels.Length][];
			bool own = fill != null;
			for (int i = 0; i < levels.Length; i++)
			{
				TextureLevelUpload level = levels[i];
				if (level.Size != level.Width * level.Height * 4)
				{
					return;
				}
				if (own)
				{
					texels[i] = new byte[level.Size];
					fill(level.Level, texels[i]);
				}
				else
				{
					texels[i] = level.Bytes;
					if (texels[i] == null || texels[i].Length < level.Size)
					{
						return;
					}
					if (texels[i].Length != level.Size)
					{
						texels[i] = level.Bytes.AsSpan(0, level.Size).ToArray();
					}
				}
			}
			bool srgb = format == VkFormat.R8G8B8A8SRGB;
			ulong key = Astc.KeyOf(0, width, height, srgb, texels);
			if (Astc.TryCached(key, width, height, levels.Length, out TextureLevelUpload[] cached, out VkFormat astcFormat))
			{
				format = astcFormat;
				levels = cached;
				fill = null;
				return;
			}
			if (own)
			{
				// Read once: the upload takes these instead of reading the cache file again.
				var uploads = new TextureLevelUpload[levels.Length];
				for (int i = 0; i < uploads.Length; i++)
				{
					TextureLevelUpload level = levels[i];
					uploads[i] = new TextureLevelUpload(level.Level, level.Width, level.Height, level.Depth, 0, texels[i], texels[i].Length);
				}
				levels = uploads;
				fill = null;
			}
			// Without the fill these are the texture's own pixels, which it may change later: the encoder gets a copy.
			Astc.Enqueue(key, width, height, srgb, texels, copy: !own);
		}

		/// <summary>Prefix of GpuTextureService.CreateTexture3D: the engine makes no BC volume textures; one from elsewhere is only reported.</summary>
		public static void BeforeCreateTexture3D(string label, VkFormat format)
		{
			Plain(format, out int kind);
			if (kind != 0)
			{
				Unhandled(format, label + " (3D)");
			}
		}

		private static void Unhandled(VkFormat format, string label)
		{
			if (Interlocked.Increment(ref unhandledLogged) <= 5)
			{
				Console.WriteLine("[port17] textures: " + format + " is not decoded here; '" + label + "' will fail on this GPU");
			}
		}

		/// <summary>One level of BC blocks to texels of 4, 2 or 1 bytes (RGBA, RG, R), rows tightly packed.</summary>
		private static byte[] Decode(byte[] blocks, int width, int height, int kind)
		{
			width = Math.Max(1, width);
			height = Math.Max(1, height);
			int channels = kind == 4 ? 1 : kind == 5 ? 2 : 4;
			int blockBytes = kind == 1 || kind == 11 || kind == 4 ? 8 : 16;
			int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
			if (blocks.Length < blocksX * blocksY * blockBytes)
			{
				throw new InvalidDataException("BC level of " + width + "x" + height + " has " + blocks.Length + " bytes, " + blocksX * blocksY * blockBytes + " expected");
			}
			byte[] texels = new byte[width * height * channels];
			if (blocksX * blocksY >= 4096)
			{
				Parallel.For(0, blocksY, row => DecodeRow(blocks, texels, row, width, height, blocksX, blockBytes, channels, kind));
			}
			else
			{
				for (int row = 0; row < blocksY; row++)
				{
					DecodeRow(blocks, texels, row, width, height, blocksX, blockBytes, channels, kind);
				}
			}
			return texels;
		}

		private static void DecodeRow(byte[] blocks, byte[] texels, int row, int width, int height, int blocksX, int blockBytes, int channels, int kind)
		{
			Span<byte> rgba = stackalloc byte[64];
			Span<byte> alpha = stackalloc byte[64];
			for (int column = 0; column < blocksX; column++)
			{
				ReadOnlySpan<byte> block = new ReadOnlySpan<byte>(blocks, (row * blocksX + column) * blockBytes, blockBytes);
				switch (kind)
				{
				case 7:
					EngineTypes.DecodeBc7Block(block, rgba);
					break;
				case 4:
					EngineTypes.DecodeBc4Block(block, rgba);
					break;
				case 5:
					EngineTypes.DecodeBc5Block(block, rgba);
					break;
				case 1:
					EngineTypes.DecodeBc1Block(block, rgba);
					break;
				case 11:
					// BC1 in an RGB format: no alpha, the "transparent" colour is black.
					EngineTypes.DecodeBc1Block(block, rgba);
					for (int i = 3; i < 64; i += 4)
					{
						rgba[i] = 255;
					}
					break;
				case 2:
				{
					// BC2: 4 bits of alpha per texel, then a BC1 colour block.
					EngineTypes.DecodeBc1Block(block.Slice(8), rgba);
					ulong bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block);
					for (int i = 0; i < 16; i++)
					{
						rgba[i * 4 + 3] = (byte)(((bits >> (i * 4)) & 0xF) * 17);
					}
					break;
				}
				case 3:
					// BC3: a BC4 block of alpha, then a BC1 colour block.
					EngineTypes.DecodeBc1Block(block.Slice(8), rgba);
					EngineTypes.DecodeBc4Block(block.Slice(0, 8), alpha);
					for (int i = 0; i < 16; i++)
					{
						rgba[i * 4 + 3] = alpha[i * 4];
					}
					break;
				}
				for (int y = 0; y < 4; y++)
				{
					int ty = row * 4 + y;
					if (ty >= height)
					{
						break;
					}
					for (int x = 0; x < 4; x++)
					{
						int tx = column * 4 + x;
						if (tx >= width)
						{
							break;
						}
						int source = (y * 4 + x) * 4, target = (ty * width + tx) * channels;
						for (int c = 0; c < channels; c++)
						{
							texels[target + c] = rgba[source + c];
						}
					}
				}
			}
		}
	}
}
