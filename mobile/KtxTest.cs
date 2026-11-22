using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PortMobile
{
	/// <summary>
	/// mobile.txt ktxTest=1, at the main menu: a 4x4 RGBA texture written as KTX 1 and as KTX 2 (uncompressed, with its
	/// data format descriptor) into logs/, each loaded through the engine's TextureLoader (Brutal.TextureApi, over
	/// libKtx.so: native/build.sh ktx), its size, format and first texel logged. Until 2026-10-27 the phone had no Ktx
	/// library at all, so any .ktx/.ktx2 texture of a mod failed.
	/// </summary>
	internal static class KtxTest
	{
		public static void Run()
		{
			if (Settings.Get("ktxTest", 0) != 1)
			{
				return;
			}
			Type loader = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Brutal.TextureApi.TextureLoader")).FirstOrDefault(t => t != null);
			if (loader == null)
			{
				Console.WriteLine("[mobile] ktx test: no TextureLoader");
				return;
			}
			string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"));
			var pixels = new byte[4 * 4 * 4];
			for (int i = 0; i < 16; i++)
			{
				pixels[i * 4] = (byte)(i * 16);
				pixels[i * 4 + 1] = 200;
				pixels[i * 4 + 2] = 40;
				pixels[i * 4 + 3] = 255;
			}
			Load(loader, Path.Combine(dir, "ktx-test.ktx"), Ktx1(pixels));
			Load(loader, Path.Combine(dir, "ktx-test.ktx2"), Ktx2(pixels));
		}

		private static void Load(Type loader, string path, byte[] bytes)
		{
			try
			{
				File.WriteAllBytes(path, bytes);
				MethodInfo load = loader.GetMethod("LoadFromFile", new[] { typeof(string), loader.Assembly.GetType("Brutal.TextureApi.TextureLoadSettings").MakeArrayType() });
				object texture = load.Invoke(null, new object[] { path, Array.CreateInstance(loader.Assembly.GetType("Brutal.TextureApi.TextureLoadSettings"), 0) });
				Type face = texture.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "ITexture") ?? texture.GetType();
				string extent = face.GetProperty("Extent")?.GetValue(texture)?.ToString();
				string format = face.GetProperty("Format")?.GetValue(texture)?.ToString();
				string levels = face.GetProperty("LevelCount")?.GetValue(texture)?.ToString();
				Console.WriteLine("[mobile] ktx test: " + Path.GetFileName(path) + " loaded: " + extent + ", " + format + ", " + levels + " level(s), " + texture.GetType().Name);
				loader.GetMethod("Unload")?.Invoke(null, new[] { texture });
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
				Console.WriteLine("[mobile] ktx test: " + Path.GetFileName(path) + ": " + inner.GetType().Name + ": " + inner.Message);
			}
		}

		private static void U32(MemoryStream s, uint v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24)); }
		private static void U64(MemoryStream s, ulong v) { U32(s, (uint)v); U32(s, (uint)(v >> 32)); }

		/// <summary>KTX 1.1: GL_UNSIGNED_BYTE, GL_RGBA, GL_RGBA8, one level.</summary>
		private static byte[] Ktx1(byte[] pixels)
		{
			var s = new MemoryStream();
			s.Write(new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 12);
			U32(s, 0x04030201);
			U32(s, 0x1401); // glType UNSIGNED_BYTE
			U32(s, 1); // glTypeSize
			U32(s, 0x1908); // glFormat RGBA
			U32(s, 0x8058); // glInternalFormat RGBA8
			U32(s, 0x1908); // glBaseInternalFormat RGBA
			U32(s, 4);
			U32(s, 4);
			U32(s, 0);
			U32(s, 0);
			U32(s, 1);
			U32(s, 1);
			U32(s, 0);
			U32(s, (uint)pixels.Length);
			s.Write(pixels, 0, pixels.Length);
			return s.ToArray();
		}

		/// <summary>KTX 2: VK_FORMAT_R8G8B8A8_UNORM, one level, a basic data format descriptor (RGBSDA, BT.709, linear).</summary>
		private static byte[] Ktx2(byte[] pixels)
		{
			var dfd = new MemoryStream();
			uint blockSize = 24 + 16 * 4;
			U32(dfd, 4 + blockSize);
			U32(dfd, 0); // vendor 0, type 0
			U32(dfd, 2 | (blockSize << 16)); // version 2, size
			dfd.WriteByte(1); // RGBSDA
			dfd.WriteByte(1); // BT.709
			dfd.WriteByte(1); // linear
			dfd.WriteByte(0); // flags (straight alpha)
			U32(dfd, 0); // texel block 1x1x1x1
			dfd.Write(new byte[] { 4, 0, 0, 0, 0, 0, 0, 0 }, 0, 8); // bytesPlane0..7
			byte[] channels = { 0, 1, 2, 15 };
			for (int c = 0; c < 4; c++)
			{
				U32(dfd, (uint)(c * 8) | (7u << 16) | ((uint)channels[c] << 24)); // bitOffset, bitLength-1, channel
				U32(dfd, 0); // sample position
				U32(dfd, 0); // lower
				U32(dfd, 255); // upper
			}
			byte[] dfdBytes = dfd.ToArray();
			const int headerSize = 80, levelIndexSize = 24;
			uint dfdOffset = headerSize + levelIndexSize;
			ulong dataOffset = (ulong)(dfdOffset + dfdBytes.Length + 15) & ~15UL;
			var s = new MemoryStream();
			s.Write(new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 12);
			U32(s, 37); // VK_FORMAT_R8G8B8A8_UNORM
			U32(s, 1); // typeSize
			U32(s, 4);
			U32(s, 4);
			U32(s, 0);
			U32(s, 0);
			U32(s, 1); // faces
			U32(s, 1); // levels
			U32(s, 0); // no supercompression
			U32(s, dfdOffset);
			U32(s, (uint)dfdBytes.Length);
			U32(s, 0); // no key/value data
			U32(s, 0);
			U64(s, 0); // no supercompression global data
			U64(s, 0);
			U64(s, dataOffset);
			U64(s, (ulong)pixels.Length);
			U64(s, (ulong)pixels.Length);
			s.Write(dfdBytes, 0, dfdBytes.Length);
			while ((ulong)s.Length < dataOffset)
			{
				s.WriteByte(0);
			}
			s.Write(pixels, 0, pixels.Length);
			return s.ToArray();
		}
	}
}
