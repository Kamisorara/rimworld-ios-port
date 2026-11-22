using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace PortMobile
{
	/// <summary>
	/// MP4 videos (H.264, and whatever else the phone's decoders take) with the phone's MediaCodec. The engine opens an MP4
	/// with Vulkan video decoding (Mp4VulkanSource) and, where that is missing, with Windows' Media Foundation
	/// (MediaFoundationSource over MediaFoundationReader); phone GPUs have no Vulkan video (an Adreno 825 lists no
	/// VK_KHR_video_*) and Android no Media Foundation, so an MP4 had no decoder at all: ISEKAI RPG LEVELING's aura and
	/// explosion clips stayed empty (2026-10-27, found comparing with the PC). Here MediaFoundationReader gets the phone's
	/// body (host/video/portvideo.c, libportvideo.so): the same NV12 frames and 100 ns times Media Foundation gives, so the
	/// engine's own MediaFoundationSource (its NV12 to RGBA converter, its timing and seeking) is used as on Windows.
	/// Ogg Theora and WebM VP8 keep the engine's managed decoders, with this as their fallback as Windows has it.
	/// </summary>
	public static class AndroidVideo
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct VideoInfo
		{
			public int width, height, stride, rows, cropX, cropY;
			public double frameRate, length;
			public int colorStandard, colorRange;
		}

		[DllImport("portvideo")] private static extern IntPtr pv_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out VideoInfo info, byte[] error, int errorSize);
		[DllImport("portvideo")] private static extern int pv_read(IntPtr video, byte[] output, int size, out long timeUs, int[] crop);
		[DllImport("portvideo")] private static extern void pv_seek(IntPtr video, double seconds);
		[DllImport("portvideo")] private static extern void pv_close(IntPtr video);

		private sealed class State
		{
			public IntPtr video;
			public VideoInfo info;
			public readonly int[] crop = new int[4];
		}

		private static readonly ConditionalWeakTable<object, State> states = new ConditionalWeakTable<object, State>();
		private static Type reader;
		private static bool opened;

		public static void Start(Harmony harmony)
		{
			try
			{
				reader = AccessTools.TypeByName("Waylith.VideoDecode.MediaFoundationReader");
				if (reader == null)
				{
					Console.WriteLine("[mobile] video: the engine has no MediaFoundationReader, MP4 left as it is");
					return;
				}
				harmony.Patch(AccessTools.PropertyGetter(reader, "IsAvailable"), prefix: new HarmonyMethod(typeof(AndroidVideo), nameof(IsAvailable)));
				harmony.Patch(AccessTools.Constructor(reader, new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(AndroidVideo), nameof(Open)));
				harmony.Patch(AccessTools.Method(reader, "ReadFrame"), prefix: new HarmonyMethod(typeof(AndroidVideo), nameof(ReadFrame)));
				harmony.Patch(AccessTools.Method(reader, "Seek"), prefix: new HarmonyMethod(typeof(AndroidVideo), nameof(Seek)));
				harmony.Patch(AccessTools.Method(reader, "Dispose"), prefix: new HarmonyMethod(typeof(AndroidVideo), nameof(Dispose)));
				Console.WriteLine("[mobile] video: MP4 and the system decoder fallback through the phone's MediaCodec");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] video: MP4 left as it is: " + e.Message);
			}
		}

		/// <summary>
		/// mobile.txt videoTest=1, at the main menu: the first MP4 below Mods through the engine's MediaFoundationReader (so
		/// through the patches above): its size, and ten frames' times and how much of each is not black.
		/// </summary>
		public static void Test()
		{
			if (Settings.Get("videoTest", 0) != 1 || reader == null)
			{
				return;
			}
			try
			{
				string mods = System.IO.Path.Combine(AppContext.BaseDirectory, "Mods");
				string clip = null;
				foreach (string file in System.IO.Directory.EnumerateFiles(mods, "*.mp4", System.IO.SearchOption.AllDirectories))
				{
					clip = file;
					break;
				}
				if (clip == null)
				{
					Console.WriteLine("[mobile] video test: no MP4 below Mods");
					return;
				}
				var stopwatch = System.Diagnostics.Stopwatch.StartNew();
				object instance = Activator.CreateInstance(reader, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { clip }, null);
				Console.WriteLine("[mobile] video test: " + clip.Substring(mods.Length) + " " + AccessTools.Property(reader, "Width").GetValue(instance) + "x" + AccessTools.Property(reader, "Height").GetValue(instance)
					+ ", " + AccessTools.Property(reader, "Length").GetValue(instance) + " s, " + AccessTools.Property(reader, "FrameRate").GetValue(instance) + " fps, stride " + AccessTools.Property(reader, "Stride").GetValue(instance));
				MethodInfo read = AccessTools.Method(reader, "ReadFrame");
				var args = new object[] { null, 0L };
				var times = new System.Text.StringBuilder();
				for (int i = 0; i < 10; i++)
				{
					if (!(bool)read.Invoke(instance, args))
					{
						times.Append(" end");
						break;
					}
					var nv12 = (byte[])args[0];
					int lit = 0;
					for (int b = 0; b < nv12.Length; b += 97)
					{
						if (nv12[b] > 20)
						{
							lit++;
						}
					}
					times.Append(' ').Append(((long)args[1] / 10000).ToString()).Append(" ms (").Append(lit * 100 / Math.Max(1, nv12.Length / 97)).Append("% lit)");
					if (i == 5)
					{
						SaveBmp(nv12, (int)AccessTools.Property(reader, "Width").GetValue(instance), (int)AccessTools.Property(reader, "Height").GetValue(instance),
							(int)AccessTools.Property(reader, "Stride").GetValue(instance), (int)AccessTools.Property(reader, "Rows").GetValue(instance));
					}
				}
				AccessTools.Method(reader, "Seek").Invoke(instance, new object[] { 0.0 });
				bool again = (bool)read.Invoke(instance, args);
				((IDisposable)instance).Dispose();
				Console.WriteLine("[mobile] video test: frames" + times + "; after a seek to 0: " + (again ? ((long)args[1] / 10000) + " ms" : "nothing") + "; " + stopwatch.ElapsedMilliseconds + " ms in all");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] video test: " + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message);
			}
		}

		/// <summary>One NV12 frame as logs/video-test.bmp (BT.601, limited range), to look at what the decoder gave.</summary>
		private static void SaveBmp(byte[] nv12, int width, int height, int stride, int rows)
		{
			int rowBytes = (width * 3 + 3) & ~3;
			var bmp = new byte[54 + rowBytes * height];
			void Put(int at, int value) { bmp[at] = (byte)value; bmp[at + 1] = (byte)(value >> 8); bmp[at + 2] = (byte)(value >> 16); bmp[at + 3] = (byte)(value >> 24); }
			bmp[0] = (byte)'B';
			bmp[1] = (byte)'M';
			Put(2, bmp.Length);
			Put(10, 54);
			Put(14, 40);
			Put(18, width);
			Put(22, height);
			bmp[26] = 1;
			bmp[28] = 24;
			Put(34, rowBytes * height);
			for (int y = 0; y < height; y++)
			{
				int to = 54 + (height - 1 - y) * rowBytes;
				for (int x = 0; x < width; x++)
				{
					int c = nv12[y * stride + x] - 16, uv = stride * rows + (y / 2) * stride + (x / 2) * 2;
					int d = nv12[uv] - 128, e = nv12[uv + 1] - 128;
					int r = (298 * c + 409 * e + 128) >> 8, g = (298 * c - 100 * d - 208 * e + 128) >> 8, b = (298 * c + 516 * d + 128) >> 8;
					bmp[to + x * 3] = (byte)Math.Max(0, Math.Min(255, b));
					bmp[to + x * 3 + 1] = (byte)Math.Max(0, Math.Min(255, g));
					bmp[to + x * 3 + 2] = (byte)Math.Max(0, Math.Min(255, r));
				}
			}
			System.IO.File.WriteAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "logs", "video-test.bmp"), bmp);
		}

		public static bool IsAvailable(ref bool __result)
		{
			__result = true;
			return false;
		}

		private static void Set(object instance, string property, object value)
		{
			AccessTools.PropertySetter(reader, property)?.Invoke(instance, new[] { value });
		}

		public static bool Open(object __instance, string path)
		{
			var error = new byte[256];
			IntPtr video = pv_open(path, out VideoInfo info, error, error.Length);
			if (video == IntPtr.Zero)
			{
				string why = System.Text.Encoding.UTF8.GetString(error).TrimEnd('\0');
				// As Media Foundation's failure: the engine tries its next decoder, or reports this one.
				throw new InvalidOperationException("the phone's decoder: " + why);
			}
			states.AddOrUpdate(__instance, new State { video = video, info = info });
			Set(__instance, "Width", info.width);
			Set(__instance, "Height", info.height);
			Set(__instance, "Stride", info.stride);
			Set(__instance, "Rows", info.rows);
			Set(__instance, "CropX", info.cropX);
			Set(__instance, "CropY", info.cropY);
			Set(__instance, "FrameRate", info.frameRate);
			Set(__instance, "Length", info.length);
			// MediaFormat's color standard: 1 BT.709, 2 and 4 BT.601; none: by size, as Media Foundation's reader does.
			bool reported = info.colorStandard != 0;
			PropertyInfo matrix = AccessTools.Property(reader, "Matrix");
			if (matrix != null)
			{
				bool bt709 = reported ? info.colorStandard == 1 : info.height >= 720;
				Set(__instance, "Matrix", Enum.Parse(matrix.PropertyType, bt709 ? "Bt709" : "Bt601"));
			}
			Set(__instance, "ReportsColour", reported);
			Set(__instance, "FullRange", info.colorRange == 1);
			if (!opened)
			{
				opened = true;
				Console.WriteLine("[mobile] video: the phone decodes " + System.IO.Path.GetFileName(path) + " (" + info.width + "x" + info.height + ", " + info.frameRate.ToString("0.#") + " fps)");
			}
			return false;
		}

		public static bool ReadFrame(object __instance, ref byte[] nv12, ref long timestamp, ref bool __result)
		{
			timestamp = 0;
			if (!states.TryGetValue(__instance, out State state) || state.video == IntPtr.Zero)
			{
				__result = false;
				return false;
			}
			int size = state.info.stride * state.info.rows * 3 / 2;
			if (nv12 == null || nv12.Length < size)
			{
				nv12 = new byte[size];
			}
			int got = pv_read(state.video, nv12, nv12.Length, out long timeUs, state.crop);
			if (got < 0)
			{
				throw new InvalidOperationException("the phone's decoder failed on a frame");
			}
			__result = got > 0;
			if (__result)
			{
				timestamp = timeUs * 10;
				if (state.crop[2] > 0 && (state.crop[0] != state.info.cropX || state.crop[1] != state.info.cropY))
				{
					state.info.cropX = state.crop[0];
					state.info.cropY = state.crop[1];
					Set(__instance, "CropX", state.crop[0]);
					Set(__instance, "CropY", state.crop[1]);
				}
			}
			return false;
		}

		public static bool Seek(object __instance, double seconds)
		{
			if (states.TryGetValue(__instance, out State state) && state.video != IntPtr.Zero)
			{
				pv_seek(state.video, seconds);
			}
			return false;
		}

		public static bool Dispose(object __instance)
		{
			if (states.TryGetValue(__instance, out State state) && state.video != IntPtr.Zero)
			{
				pv_close(state.video);
				state.video = IntPtr.Zero;
			}
			return false;
		}
	}
}
