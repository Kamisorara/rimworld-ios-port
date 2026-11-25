using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PortHooks
{
	/// <summary>
	/// iOS 的回退字体（安卓 AndroidFonts 的对应物）。引擎的 FontFallbacks.Resolve 按平台选候选：Windows/macOS 给的是文件
	/// 路径，Linux 给的是族名（"Noto Sans CJK SC" … "DejaVu Sans"），交给 UnityStubs.Internal.LinuxFontconfig.TryFind 解析——
	/// 先 fontconfig（只在 IsLinux 下），再 FontFaceReader 按 Font.GetPathsToOSFonts() 的目录读字体文件的 name table 建索引。
	/// iOS 上 OperatingSystem.IsMacOS() 为 false，走 Linux 分支，而目录是 /usr/share/fonts 等，一个都没有 → 索引为空 →
	/// "[ImGuiHost] No OS fallback fonts found, non-latin text will render as missing glyph boxes" → 中文全是方框（用户自始如此，
	/// 一直用英文玩）。做法同安卓：TryFind 的后缀把 Linux 族名指到 iOS 系统字体。TTC 的 face 序号不猜——用引擎自己的
	/// FontFaceReader.TryReadFaces 读 name table 找族名（PingFang.ttc 里哪一个 face 是 PingFang SC）。彩色 emoji（Apple Color
	/// Emoji）与安卓同样不给：移植层的 FreeType 没有 PNG 解码。
	/// </summary>
	internal static class IosFonts
	{
		private const string Sys = "/System/Library/Fonts";
		private const string Sup = "/System/Library/Fonts/Supplemental";

		// iOS 近几版把一部分系统字体挪进了 Core/ 与 LanguageSupport/：同一个文件名四个目录都试。
		private static readonly string[] Dirs = { Sys, Sys + "/Core", Sys + "/LanguageSupport", Sup };

		// 候选写成 "文件:文件内要匹配的族名"（空名 = 第 0 个 face），按顺序取第一个存在且读得出族名的。
		private static string[] F(string file, string family)
		{
			var list = new List<string>();
			foreach (string dir in Dirs)
			{
				list.Add(dir + "/" + file + ":" + family);
			}
			return list.ToArray();
		}

		private static string[] Concat(params string[][] parts)
		{
			var list = new List<string>();
			foreach (string[] part in parts)
			{
				list.AddRange(part);
			}
			return list.ToArray();
		}

		private static readonly string[] CjkSc =
			Concat(F("PingFang.ttc", "PingFang SC"), F("STHeiti Medium.ttc", "Heiti SC"), F("STHeiti Light.ttc", "Heiti SC"),
				F("Songti.ttc", "Songti SC"), F("Arial Unicode.ttf", ""));
		private static readonly string[] CjkTc =
			Concat(F("PingFang.ttc", "PingFang TC"), F("PingFang.ttc", "PingFang HK"), F("STHeiti Medium.ttc", "Heiti TC"),
				F("Songti.ttc", "Songti TC"), F("Arial Unicode.ttf", ""));
		private static readonly string[] CjkJp =
			Concat(F("Hiragino Sans.ttc", "Hiragino Sans"), F("PingFang.ttc", "PingFang JP"), F("Arial Unicode.ttf", ""));
		private static readonly string[] Hangul =
			Concat(F("Apple SD Gothic Neo.ttc", "Apple SD Gothic Neo"), F("Arial Unicode.ttf", ""));
		private static readonly string[] Thai =
			Concat(F("Thonburi.ttc", "Thonburi"), F("Ayuthaya.ttf", ""));
		private static readonly string[] Symbols =
			Concat(F("Apple Symbols.ttf", ""), F("Arial Unicode.ttf", ""));
		private static readonly string[] Latin =
			Concat(F("Arial.ttf", ""), F("Helvetica.ttc", "Helvetica"));

		private static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
		{
			["Noto Sans CJK SC"] = CjkSc, ["Source Han Sans SC"] = CjkSc, ["Noto Sans SC"] = CjkSc, ["WenQuanYi Micro Hei"] = CjkSc, ["Droid Sans Fallback"] = CjkSc,
			["Noto Sans CJK TC"] = CjkTc, ["Source Han Sans TC"] = CjkTc, ["Noto Sans TC"] = CjkTc,
			["Noto Sans CJK JP"] = CjkJp, ["Source Han Sans JP"] = CjkJp, ["Noto Sans JP"] = CjkJp, ["IPAGothic"] = CjkJp, ["VL Gothic"] = CjkJp,
			["Noto Sans CJK KR"] = Hangul, ["Source Han Sans KR"] = Hangul, ["Noto Sans KR"] = Hangul, ["NanumGothic"] = Hangul, ["Baekmuk Gulim"] = Hangul,
			["Noto Sans Thai"] = Thai, ["Garuda"] = Thai, ["Loma"] = Thai, ["Tlwg Typo"] = Thai,
			["Noto Sans Symbols"] = Symbols, ["Noto Sans Symbols2"] = Symbols, ["Symbola"] = Symbols, ["Unifont"] = Symbols,
			["DejaVu Sans"] = Latin, ["Liberation Sans"] = Latin,
		};

		// A file's faces as "Family|Style" in TTC order (empty: read, no face; null: not readable).
		private static readonly Dictionary<string, string[]> faces = new Dictionary<string, string[]>(StringComparer.Ordinal);
		private static MethodInfo tryReadFaces;

		public static void Find(string family, ref string path, ref uint fontNo, ref bool result)
		{
			if (!Map.TryGetValue(family, out string[] candidates))
			{
				return;
			}
			foreach (string candidate in candidates)
			{
				int sep = candidate.LastIndexOf(':');
				string file = candidate.Substring(0, sep);
				string name = candidate.Substring(sep + 1);
				if (!File.Exists(file))
				{
					continue;
				}
				int index = name.Length == 0 ? 0 : FaceOf(file, name);
				if (index < 0)
				{
					continue;
				}
				path = file;
				fontNo = (uint)index;
				result = true;
				Console.WriteLine("[port17] fonts: iOS '" + family + "' -> " + file + (index != 0 ? "#" + index : ""));
				return;
			}
			Console.WriteLine("[port17] fonts: iOS has no font for '" + family + "'");
		}

		private static int FaceOf(string file, string family)
		{
			string[] names;
			lock (faces)
			{
				if (!faces.TryGetValue(file, out names))
				{
					names = ReadFaces(file);
					faces[file] = names;
				}
			}
			if (names == null)
			{
				return -1;
			}
			int any = -1;
			for (int i = 0; i < names.Length; i++)
			{
				string[] parts = names[i].Split('|');
				if (!string.Equals(parts[0], family, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (any < 0)
				{
					any = i;
				}
				if (parts.Length < 2 || parts[1].Length == 0 || parts[1].Equals("Regular", StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}
			return any;
		}

		private static string[] ReadFaces(string file)
		{
			try
			{
				if (tryReadFaces == null)
				{
					Type reader = Type.GetType("UnityStubs.Internal.FontFaceReader, UnityStubs");
					tryReadFaces = reader?.GetMethod("TryReadFaces", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
				}
				if (tryReadFaces == null)
				{
					Console.WriteLine("[port17] fonts: iOS: the engine's FontFaceReader.TryReadFaces is not where it was; faces taken as #0");
					return Array.Empty<string>();
				}
				object[] args = new object[] { File.ReadAllBytes(file), null };
				if (!(bool)tryReadFaces.Invoke(null, args) || args[1] == null)
				{
					return Array.Empty<string>();
				}
				var list = new List<string>();
				foreach (object face in (IEnumerable)args[1])
				{
					Type t = face.GetType();
					list.Add((t.GetProperty("Family")?.GetValue(face) as string ?? "") + "|" + (t.GetProperty("Style")?.GetValue(face) as string ?? ""));
				}
				return list.ToArray();
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] fonts: iOS face table of " + file + " not read: " + e.Message);
				return null;
			}
		}
	}
}
