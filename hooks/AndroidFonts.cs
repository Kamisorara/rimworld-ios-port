using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// The fallback fonts for glyphs the game's own fonts lack (symbols, CJK, Thai). Up to 1.7.4985 the launcher looked
	/// for Linux font files by name, and patch/Patch.cs pointed the names at the phone's (/system/fonts/DroidSans.ttf for
	/// DejaVuSans.ttf, Roboto for Liberation Sans...). From 4990 it asks for font families instead
	/// (UnityStubs.Internal.LinuxFontconfig.TryFind: fontconfig, which a phone does not have, then the names inside the OS's
	/// font files): "Noto Sans CJK SC", "Noto Sans Symbols", "Noto Sans Thai" are found under their own names, but the
	/// Linux ones with no such family on a phone were left out (DejaVu Sans, Liberation Sans, Noto Sans Symbols2, Droid
	/// Sans Fallback), and with them the symbols and scripts they covered. Each is the phone's file it was before, when the
	/// lookup by family finds nothing. Nothing is patched on an engine without TryFind.
	///
	/// Emoji and pictographs (RimTalk's "📷 Vision") the PC draws as outlines from Windows' Segoe UI Symbol and Segoe UI
	/// Emoji. A phone's only emoji font holds color pictures (NotoColorEmoji, PNG images: the port's FreeType has no PNG
	/// decoder, and the engine asks for no color glyphs anyway), so they were boxes. Google's Noto Emoji, an outline font
	/// like those, which the port brings into the game folder (host/stage.sh), goes after the whole list the engine makes
	/// (FontFallbacks.Resolve), so that it only fills what were boxes. In Symbola's place, among the symbol fonts where
	/// Linux has it, it would come before Pretendard and Noto Sans CJK and turn symbols that they draw in text style, as
	/// the PC's Segoe UI Symbol does (▶ ⚠ ➡), into emoji.
	/// </summary>
	public static class AndroidFonts
	{
		private static readonly (string Family, string[] Files)[] Stand =
		{
			("DejaVu Sans", new[] { "DroidSans.ttf", "Roboto-Regular.ttf" }),
			("Liberation Sans", new[] { "Roboto-Regular.ttf", "DroidSans.ttf" }),
			("Noto Sans Symbols2", new[] { "NotoSansSymbols-Regular-Subsetted2.ttf" }),
			("Droid Sans Fallback", new[] { "DroidSansFallbackFull.ttf", "DroidSansFallback.ttf" }),
		};

		private static bool patched;
		private static bool emojiPatched;
		// The engine's FontFallbacks.Source for game/NotoEmoji.ttf: its first face, no range left out.
		private static object emoji;

		public static void Start()
		{
			try
			{
				AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => TryPatch(args.LoadedAssembly);
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					TryPatch(assembly);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] fonts: fallback families left as they are: " + e.Message);
			}
		}

		private static void TryPatch(Assembly assembly)
		{
			string name = assembly.GetName().Name;
			if (name == "RimWorld")
			{
				TryAddEmoji(assembly);
				return;
			}
			if (patched || name != "UnityStubs")
			{
				return;
			}
			patched = true;
			try
			{
				Type fontconfig = assembly.GetType("UnityStubs.Internal.LinuxFontconfig");
				MethodInfo find = fontconfig?.GetMethod("TryFind", BindingFlags.Public | BindingFlags.Static);
				if (find == null)
				{
					return;
				}
				new Harmony("port17.fonts").Patch(find, postfix: new HarmonyMethod(typeof(AndroidFonts), nameof(AfterTryFind)));
				Console.WriteLine("[port17] fonts: Linux fallback families without a phone font get the phone's files");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] fonts: fallback families left as they are: " + e.Message);
			}
		}

		public static void AfterTryFind(string family, ref string path, ref uint fontNo, ref bool __result)
		{
			if (__result || family == null)
			{
				return;
			}
			foreach ((string name, string[] files) in Stand)
			{
				if (!string.Equals(name, family, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				foreach (string file in files)
				{
					string full = Path.Combine("/system/fonts", file);
					if (File.Exists(full))
					{
						path = full;
						fontNo = 0;
						__result = true;
						return;
					}
				}
			}
		}

		// The launcher's FontFallbacks.Resolve(locale) makes a new list of Source(path, fontNo, excludeRanges) for each
		// language (1.7.4979 on; 4976 had a list of paths), which ImGuiHost keeps and merges into every font in its order.
		private static void TryAddEmoji(Assembly assembly)
		{
			if (emojiPatched)
			{
				return;
			}
			emojiPatched = true;
			try
			{
				string path = Path.Combine(AppContext.BaseDirectory, "NotoEmoji.ttf");
				MethodInfo resolve = assembly.GetType("RimWorldWaylith.Imgui.FontFallbacks")?.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static);
				Type source = resolve != null && resolve.ReturnType.IsGenericType ? resolve.ReturnType.GetGenericArguments()[0] : null;
				ConstructorInfo make = source?.GetConstructor(new[] { typeof(string), typeof(uint), typeof(uint[]) });
				if (make == null || !File.Exists(path))
				{
					Console.WriteLine("[port17] fonts: no Noto Emoji after the fallback fonts (" + (make == null ? "the engine's list is not as expected" : path + " is missing") + ")");
					return;
				}
				emoji = make.Invoke(new object[] { path, 0u, null });
				new Harmony("port17.fonts").Patch(resolve, postfix: new HarmonyMethod(typeof(AndroidFonts), nameof(AfterResolve)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] fonts: no Noto Emoji after the fallback fonts: " + e.Message);
			}
		}

		public static void AfterResolve(object __result)
		{
			if (__result is IList list && !list.IsFixedSize && !list.IsReadOnly && !list.Contains(emoji))
			{
				list.Add(emoji);
				Console.WriteLine("[port17] fonts: Noto Emoji after the engine's " + (list.Count - 1) + " fallback fonts");
			}
		}
	}
}
