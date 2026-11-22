using System;
using System.Reflection;

namespace PortHooks
{
	/// <summary>
	/// Engine types that 1.7.4990 moved to other namespaces (Waylith.Rendering to Waylith.Graphics.Textures and its
	/// BlockCompression; the block decoders from Bc7BlockEncoder to BcBlockDecoder), used through delegates bound once,
	/// by name, from where they are: one build of the port runs on
	/// a game copy of either version (a tester keeps the copy imported last until importing again). Everything else the
	/// port uses of the engine stayed where it was.
	/// </summary>
	public static class EngineTypes
	{
		public delegate void BlockDecode(ReadOnlySpan<byte> block, Span<byte> rgba);
		public delegate void Bc1Decode(ReadOnlySpan<byte> block, Span<byte> rgba, int pitch);
		public delegate byte[][] Bc7LevelsEncode(byte[][] rgbaLevels, int width, int height, int mipCount, bool parallel);
		public delegate void DownsampleRgba(byte[] source, int sourceWidth, int sourceHeight, byte[] destination, int destinationWidth, int destinationHeight, bool parallel, bool premultiplied);

		private static readonly Assembly EngineAssembly = typeof(Waylith.Rendering.Texture2D).Assembly;

		private static Type Find(string name, params string[] namespaces)
		{
			foreach (string ns in namespaces)
			{
				Type type = EngineAssembly.GetType(ns + "." + name);
				if (type != null)
				{
					return type;
				}
			}
			throw new TypeLoadException("the engine has no " + name + " (in " + string.Join(", ", namespaces) + ")");
		}

		// The block decoders: Bc7BlockEncoder's up to 4985, BcBlockDecoder's from 4990.
		private static readonly Lazy<Type> Bc7 = new Lazy<Type>(() => EngineAssembly.GetType("Waylith.Graphics.Textures.BlockCompression.BcBlockDecoder") ?? Find("Bc7BlockEncoder", "Waylith.Graphics.Textures.BlockCompression", "Waylith.Rendering"));
		private static readonly Lazy<Type> Cpu = new Lazy<Type>(() => Find("CpuBlockEncoder", "Waylith.Graphics.Textures.BlockCompression", "Waylith.Rendering"));
		private static readonly Lazy<Type> Settings = new Lazy<Type>(() => Find("TextureCompressionSettings", "Waylith.Graphics.Textures", "Waylith.Rendering"));
		private static readonly Lazy<Type> PixelData = new Lazy<Type>(() => Find("TexturePixelData", "Waylith.Graphics.Textures", "Waylith.Rendering"));
		private static readonly Lazy<Type> Target = new Lazy<Type>(() => Find("BcEncodeTarget", "Waylith.Graphics.Textures", "Waylith.Rendering"));

		private static T Bind<T>(Lazy<Type> type, string name, params Type[] parameters) where T : Delegate
		{
			MethodInfo method = type.Value.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, parameters, null)
				?? throw new MissingMethodException(type.Value.FullName, name);
			return (T)Delegate.CreateDelegate(typeof(T), method);
		}

		private static readonly Lazy<BlockDecode> bc7 = new Lazy<BlockDecode>(() => Bind<BlockDecode>(Bc7, "DecodeBc7Block", typeof(ReadOnlySpan<byte>), typeof(Span<byte>)));
		private static readonly Lazy<BlockDecode> bc4 = new Lazy<BlockDecode>(() => Bind<BlockDecode>(Bc7, "DecodeBc4Block", typeof(ReadOnlySpan<byte>), typeof(Span<byte>)));
		private static readonly Lazy<BlockDecode> bc5 = new Lazy<BlockDecode>(() => Bind<BlockDecode>(Bc7, "DecodeBc5Block", typeof(ReadOnlySpan<byte>), typeof(Span<byte>)));
		private static readonly Lazy<Bc1Decode> bc1 = new Lazy<Bc1Decode>(() => Bind<Bc1Decode>(Bc7, "DecodeBc1Block", typeof(ReadOnlySpan<byte>), typeof(Span<byte>), typeof(int)));
		private static readonly Lazy<Bc7LevelsEncode> bc7Levels = new Lazy<Bc7LevelsEncode>(() => Bind<Bc7LevelsEncode>(Cpu, "EncodeBc7Levels", typeof(byte[][]), typeof(int), typeof(int), typeof(int), typeof(bool)));
		private static readonly Lazy<DownsampleRgba> downsample = new Lazy<DownsampleRgba>(() => Bind<DownsampleRgba>(Cpu, "Downsample", typeof(byte[]), typeof(int), typeof(int), typeof(byte[]), typeof(int), typeof(int), typeof(bool), typeof(bool)));
		private static readonly Lazy<Func<string, string[], bool>> matchesAny = new Lazy<Func<string, string[], bool>>(() => Bind<Func<string, string[], bool>>(Settings, "MatchesAny", typeof(string), typeof(string[])));
		private static readonly Lazy<Func<Waylith.Rendering.LoadedTextureFormat, bool>> isBlockCompressed = new Lazy<Func<Waylith.Rendering.LoadedTextureFormat, bool>>(() => Bind<Func<Waylith.Rendering.LoadedTextureFormat, bool>>(PixelData, "IsBlockCompressed", typeof(Waylith.Rendering.LoadedTextureFormat)));

		public static void DecodeBc7Block(ReadOnlySpan<byte> block, Span<byte> rgba) => bc7.Value(block, rgba);
		public static void DecodeBc4Block(ReadOnlySpan<byte> block, Span<byte> rgba) => bc4.Value(block, rgba);
		public static void DecodeBc5Block(ReadOnlySpan<byte> block, Span<byte> rgba) => bc5.Value(block, rgba);
		public static void DecodeBc1Block(ReadOnlySpan<byte> block, Span<byte> rgba, int pitch = 16) => bc1.Value(block, rgba, pitch);
		public static byte[][] EncodeBc7Levels(byte[][] rgbaLevels, int width, int height, int mipCount, bool parallel = true) => bc7Levels.Value(rgbaLevels, width, height, mipCount, parallel);
		public static void Downsample(byte[] source, int sourceWidth, int sourceHeight, byte[] destination, int destinationWidth, int destinationHeight, bool parallel = false, bool premultiplied = false) => downsample.Value(source, sourceWidth, sourceHeight, destination, destinationWidth, destinationHeight, parallel, premultiplied);
		public static bool IsBlockCompressed(Waylith.Rendering.LoadedTextureFormat format) => isBlockCompressed.Value(format);

		private static readonly Lazy<PropertyInfo> cpuData = new Lazy<PropertyInfo>(() => typeof(Waylith.Rendering.Texture2D).GetProperty("CpuData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

		/// <summary>Texture2D.CpuData (a TexturePixelData, which moved), or null.</summary>
		public static object CpuDataOf(Waylith.Rendering.Texture2D texture) => texture == null ? null : cpuData.Value.GetValue(texture);

		/// <summary>The format of a texture's CPU data, or null without any.</summary>
		public static Waylith.Rendering.LoadedTextureFormat? CpuFormatOf(Waylith.Rendering.Texture2D texture)
		{
			object data = CpuDataOf(texture);
			return data == null ? (Waylith.Rendering.LoadedTextureFormat?)null : (Waylith.Rendering.LoadedTextureFormat)data.GetType().GetProperty("Format").GetValue(data);
		}

		/// <summary>TexturePixelData.ReleaseTracked() and Release() on a texture's CPU data.</summary>
		public static void ReleaseCpuData(Waylith.Rendering.Texture2D texture)
		{
			object data = CpuDataOf(texture);
			if (data != null)
			{
				data.GetType().GetMethod("ReleaseTracked", Type.EmptyTypes)?.Invoke(data, null);
				data.GetType().GetMethod("Release", Type.EmptyTypes)?.Invoke(data, null);
			}
		}

		/// <summary>
		/// Every member bound here, looked up now: a later engine that moves one again is named in the log at the start,
		/// not found out when a texture needs it. Null when all are there.
		/// </summary>
		public static string Verify()
		{
			var missing = new System.Collections.Generic.List<string>();
			void Try(string what, Func<object> get)
			{
				try
				{
					if (get() == null)
					{
						missing.Add(what);
					}
				}
				catch (Exception e)
				{
					missing.Add(what + " (" + e.GetType().Name + ")");
				}
			}
			Try("DecodeBc7Block", () => bc7.Value);
			Try("DecodeBc4Block", () => bc4.Value);
			Try("DecodeBc5Block", () => bc5.Value);
			Try("DecodeBc1Block", () => bc1.Value);
			Try("EncodeBc7Levels", () => bc7Levels.Value);
			Try("Downsample", () => downsample.Value);
			Try("MatchesAny", () => matchesAny.Value);
			Try("IsBlockCompressed", () => isBlockCompressed.Value);
			Try("Texture2D.CpuData", () => cpuData.Value);
			Try("RawMaxSize", () => RawMaxSize);
			Try("RawPaths", () => RawPaths ?? new string[0]);
			Try("BcEncodeTarget", () => Target.Value);
			return missing.Count == 0 ? null : string.Join(", ", missing);
		}

		/// <summary>TextureCompressionSettings.RawMaxSize: textures up to this size stay RGBA.</summary>
		public static int RawMaxSize => (int)Settings.Value.GetProperty("RawMaxSize", BindingFlags.Public | BindingFlags.Static).GetValue(null);

		/// <summary>TextureCompressionSettings.RawPaths: path patterns that stay RGBA.</summary>
		public static string[] RawPaths => (string[])Settings.Value.GetProperty("RawPaths", BindingFlags.Public | BindingFlags.Static).GetValue(null);

		public static bool MatchesAny(string path, string[] patterns) => matchesAny.Value(path, patterns);

		private static readonly Lazy<PropertyInfo> keepRaw = new Lazy<PropertyInfo>(() => Settings.Value.GetProperty("KeepRaw", BindingFlags.Public | BindingFlags.Static));

		/// <summary>
		/// TextureCompressionSettings.KeepRaw (from 1.7.4990: textures a def marks "uncompressed", ImportDefaults): whether
		/// the engine keeps this file's texture RGBA. False on an engine without it.
		/// </summary>
		public static bool KeepRaw(string path) => keepRaw.Value?.GetValue(null) is Func<string, bool> rule && rule(path);

		/// <summary>TextureCompressionSettings.Configure(BcEncodeTarget.None, Quality): no BC encoding at load time.</summary>
		public static void ConfigureNoBcEncoding()
		{
			Type settings = Settings.Value;
			object quality = settings.GetProperty("Quality", BindingFlags.Public | BindingFlags.Static).GetValue(null);
			object none = Enum.Parse(Target.Value, "None");
			settings.GetMethod("Configure", BindingFlags.Public | BindingFlags.Static, null, new[] { Target.Value, typeof(float) }, null).Invoke(null, new[] { none, quality });
		}
	}
}
