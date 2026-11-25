using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PortHooks
{
	/// <summary>
	/// iOS 的回退字体（安卓 AndroidFonts 的对应物）。引擎的 FontFallbacks.Resolve 按平台选候选：Windows/macOS 给的是文件
	/// 路径，Linux 给的是族名（"Noto Sans CJK SC" … "DejaVu Sans"），交给 UnityStubs.Internal.LinuxFontconfig.TryFind 解析——
	/// 先 fontconfig（只在 IsLinux 下），再 FontFaceReader 按 Font.GetPathsToOSFonts() 的目录读字体文件的 name table 建索引。
	/// iOS 上 OperatingSystem.IsMacOS() 为 false，走 Linux 分支，而目录是 /usr/share/fonts 等，一个都没有 → 索引为空 →
	/// "[ImGuiHost] No OS fallback fonts found, non-latin text will render as missing glyph boxes" → 中文全是方框（用户自始如此，
	/// 一直用英文玩）。
	///
	/// 0.4.34 只把 Linux 族名映射到系统字体文件名；0.4.34 真机（iPadOS 26）：四个目录里 PingFang/STHeiti/Songti/Arial Unicode
	/// 一个都没有——iOS 18 起 Apple 删了 /System/Library/Fonts/Core/PingFang.ttc，换成私有框架里的 PingFangUI.ttc，字形是 hvgl
	/// 表，FreeType 画不出来（Unity 文本引擎同样中招）。所以中文不能靠系统字体：随包带 Noto Sans SC（OFL，host/app/fonts/，
	/// build-ipa.sh 放进 bundle 根），简中回退第一候选就是它；系统字体只作补充，按 name table 建索引而不是按文件名猜——
	/// 哪个目录、哪个文件名都随系统版本变。TTC 的 face 序号不猜：按文件里的 name table 定位族名与 Regular。没有 glyf/CFF
	/// 轮廓表的文件（hvgl）直接跳过。彩色 emoji 与安卓同样不给：移植层的 FreeType 没有 PNG 解码。
	/// </summary>
	internal static class IosFonts
	{
		private const string Sys = "/System/Library/Fonts";
		private static readonly string[] Dirs =
		{
			Sys, Sys + "/Core", Sys + "/LanguageSupport", Sys + "/Supplemental",
			"/System/Cryptexes/OS/System/Library/Fonts",
		};

		private const string BundledFile = "NotoSansSC-Regular.otf";
		private static string bundled;

		private static readonly string[] CjkScFamilies =
		{
			"PingFang SC", "Heiti SC", "STHeiti", "Songti SC", "STSong", "Hiragino Sans GB", "Arial Unicode MS",
		};
		private static readonly string[] CjkTcFamilies =
		{
			"PingFang TC", "PingFang HK", "Heiti TC", "Songti TC", "LiSong Pro", "Arial Unicode MS",
		};
		private static readonly string[] CjkJpFamilies =
		{
			"Hiragino Sans", "Hiragino Kaku Gothic ProN", "Hiragino Sans W3", "PingFang JP", "Arial Unicode MS",
		};
		private static readonly string[] HangulFamilies = { "Apple SD Gothic Neo", "AppleGothic", "NanumGothic", "Arial Unicode MS" };
		private static readonly string[] ThaiFamilies = { "Thonburi", "Ayuthaya", "Thonburi MT" };
		private static readonly string[] SymbolFamilies = { "Apple Symbols", "Arial Unicode MS" };
		private static readonly string[] LatinFamilies = { "Arial", "Helvetica", "Helvetica Neue" };

		private sealed class Group
		{
			public readonly string[] Families;
			public readonly bool Cjk;
			public readonly bool BundledFirst;

			public Group(string[] families, bool cjk, bool bundledFirst)
			{
				Families = families;
				Cjk = cjk;
				BundledFirst = bundledFirst;
			}
		}

		private static readonly Group Sc = new Group(CjkScFamilies, true, true);
		private static readonly Group Tc = new Group(CjkTcFamilies, true, false);
		private static readonly Group Jp = new Group(CjkJpFamilies, true, false);
		private static readonly Group Kr = new Group(HangulFamilies, true, false);
		private static readonly Group Thai = new Group(ThaiFamilies, false, false);
		private static readonly Group Symbols = new Group(SymbolFamilies, false, false);
		private static readonly Group Latin = new Group(LatinFamilies, false, false);

		private static readonly Dictionary<string, Group> Map = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase)
		{
			["Noto Sans CJK SC"] = Sc, ["Source Han Sans SC"] = Sc, ["Noto Sans SC"] = Sc, ["WenQuanYi Micro Hei"] = Sc, ["Droid Sans Fallback"] = Sc,
			["Noto Sans CJK TC"] = Tc, ["Source Han Sans TC"] = Tc, ["Noto Sans TC"] = Tc,
			["Noto Sans CJK JP"] = Jp, ["Source Han Sans JP"] = Jp, ["Noto Sans JP"] = Jp, ["IPAGothic"] = Jp, ["VL Gothic"] = Jp,
			["Noto Sans CJK KR"] = Kr, ["Source Han Sans KR"] = Kr, ["Noto Sans KR"] = Kr, ["NanumGothic"] = Kr, ["Baekmuk Gulim"] = Kr,
			["Noto Sans Thai"] = Thai, ["Garuda"] = Thai, ["Loma"] = Thai, ["Tlwg Typo"] = Thai,
			["Noto Sans Symbols"] = Symbols, ["Noto Sans Symbols2"] = Symbols, ["Symbola"] = Symbols, ["Unifont"] = Symbols,
			["DejaVu Sans"] = Latin, ["Liberation Sans"] = Latin,
		};

		private static readonly string[] CjkMarks =
		{
			"PingFang", "Heiti", "Songti", "STSong", "Hiragino Sans GB", "Kaiti", "Yuanti", "Xingkai", "Libian", "Hannotate",
			"HanziPen", "Baoli", "Weibei", "Wawati", "Lantinghei", "Yuppy", "Arial Unicode", "Gothic", "Myungjo", "AppleGothic",
			"Noto Sans CJK", "Source Han",
		};

		private struct Entry
		{
			public string File;
			public int Face;
			public bool Regular;
		}

		// Family (from the name table, case-insensitive) -> the Regular face of the first file that has it.
		private static readonly Dictionary<string, Entry> index = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
		private static bool indexed;

		public static void Find(string family, ref string path, ref uint fontNo, ref bool result)
		{
			if (family == null || !Map.TryGetValue(family, out Group group))
			{
				return;
			}
			EnsureIndexed();
			string file = null;
			int face = 0;
			string how = null;
			if (group.BundledFirst && bundled != null)
			{
				file = bundled;
				how = "bundled";
			}
			if (file == null)
			{
				foreach (string name in group.Families)
				{
					if (TryFind(name, out Entry entry))
					{
						file = entry.File;
						face = entry.Face;
						how = "system '" + name + "'";
						break;
					}
				}
			}
			if (file == null && group.Cjk && bundled != null)
			{
				file = bundled;
				how = "bundled";
			}
			if (file == null)
			{
				Console.WriteLine("[port17] fonts: iOS has no font for '" + family + "' (index: " + index.Count + " families)");
				return;
			}
			path = file;
			fontNo = (uint)face;
			result = true;
			Console.WriteLine("[port17] fonts: iOS '" + family + "' -> " + file + (face != 0 ? "#" + face : "") + " (" + how + ")");
		}

		private static bool TryFind(string family, out Entry entry)
		{
			if (index.TryGetValue(family, out entry))
			{
				return true;
			}
			string best = null;
			foreach (KeyValuePair<string, Entry> pair in index)
			{
				if (pair.Key.StartsWith(family, StringComparison.OrdinalIgnoreCase) && (best == null || pair.Key.Length < best.Length))
				{
					best = pair.Key;
					entry = pair.Value;
				}
			}
			return best != null;
		}

		private static void EnsureIndexed()
		{
			lock (index)
			{
				if (indexed)
				{
					return;
				}
				indexed = true;
				bundled = Path.Combine(AppContext.BaseDirectory, BundledFile);
				if (!File.Exists(bundled))
				{
					Console.WriteLine("[port17] fonts: iOS bundled Noto Sans SC missing at " + bundled);
					bundled = null;
				}
				else
				{
					Console.WriteLine("[port17] fonts: iOS bundled Noto Sans SC: " + bundled + " (" + new FileInfo(bundled).Length + " bytes)");
				}
				var options = new EnumerationOptions
				{
					RecurseSubdirectories = true,
					IgnoreInaccessible = true,
					AttributesToSkip = 0,
					MatchType = MatchType.Win32,
				};
				var cjk = new List<string>();
				int faces = 0;
				foreach (string dir in Dirs)
				{
					if (!Directory.Exists(dir))
					{
						Console.WriteLine("[port17] fonts: iOS " + dir + ": not present");
						continue;
					}
					var files = new List<string>();
					try
					{
						foreach (string file in Directory.EnumerateFiles(dir, "*", options))
						{
							if (IsFont(file))
							{
								files.Add(file);
							}
						}
					}
					catch (Exception e)
					{
						Console.WriteLine("[port17] fonts: iOS " + dir + ": not listed: " + e.Message);
						continue;
					}
					int before = faces;
					foreach (string file in files)
					{
						faces += AddFile(file, dir, cjk);
					}
					Console.WriteLine("[port17] fonts: iOS " + dir + ": " + files.Count + " font files, " + (faces - before) + " faces");
					var names = new List<string>();
					foreach (string file in files)
					{
						names.Add(file.Substring(dir.Length + 1));
					}
					Console.WriteLine("[port17] fonts: iOS " + dir + " files: " + Join(names, 6000));
				}
				Console.WriteLine("[port17] fonts: iOS index: " + index.Count + " families from " + faces + " faces"
					+ (cjk.Count > 0 ? "; CJK: " + Join(cjk, 2000) : ""));
			}
		}

		private static string Join(List<string> items, int limit)
		{
			var sb = new StringBuilder();
			foreach (string item in items)
			{
				if (sb.Length > 0)
				{
					sb.Append(", ");
				}
				sb.Append(item);
				if (sb.Length > limit)
				{
					sb.Append(" …");
					break;
				}
			}
			return sb.ToString();
		}

		private static bool IsFont(string file)
		{
			string ext = Path.GetExtension(file).ToLowerInvariant();
			return ext == ".ttf" || ext == ".ttc" || ext == ".otf" || ext == ".ttcf";
		}

		private static int AddFile(string file, string dir, List<string> cjk)
		{
			string[][] faces;
			try
			{
				faces = ReadFaces(file);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] fonts: iOS " + file + " not read: " + e.Message);
				return 0;
			}
			if (faces == null)
			{
				return 0;
			}
			int count = 0;
			string shown = file.Substring(dir.Length + 1);
			for (int i = 0; i < faces.Length; i++)
			{
				string[] names = faces[i];
				if (names == null)
				{
					continue;
				}
				count++;
				string family = names[0];
				if (family.Length == 0)
				{
					continue;
				}
				bool regular = names[1].Length == 0 || names[1].Equals("Regular", StringComparison.OrdinalIgnoreCase);
				if (!index.TryGetValue(family, out Entry entry) || (!entry.Regular && regular))
				{
					index[family] = new Entry { File = file, Face = i, Regular = regular };
				}
				if (IsCjk(family))
				{
					string line = family + " -> " + shown + (i != 0 ? "#" + i : "") + (regular ? "" : " (" + names[1] + ")");
					if (!cjk.Contains(line))
					{
						cjk.Add(line);
					}
				}
			}
			return count;
		}

		private static bool IsCjk(string family)
		{
			foreach (string mark in CjkMarks)
			{
				if (family.IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			return false;
		}

		// Faces of a font file in file order ("Family|Style"), null for a face without usable names or outlines.
		private static string[][] ReadFaces(string file)
		{
			using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess))
			{
				var faces = new List<string[]>();
				uint magic = ReadU32(stream, 0);
				if (magic == 0x74746366) // 'ttcf'
				{
					uint count = ReadU32(stream, 8);
					if (count == 0 || count > 512)
					{
						return null;
					}
					for (uint i = 0; i < count; i++)
					{
						faces.Add(ReadFace(stream, ReadU32(stream, 12 + 4 * i)));
					}
				}
				else
				{
					faces.Add(ReadFace(stream, 0));
				}
				return faces.ToArray();
			}
		}

		private static string[] ReadFace(FileStream stream, long sfnt)
		{
			uint version = ReadU32(stream, sfnt);
			if (version != 0x00010000 && version != 0x00020000 && version != 0x4F54544F && version != 0x74727565)
			{
				return null;
			}
			byte[] header = Read(stream, sfnt + 4, 2);
			if (header == null)
			{
				return null;
			}
			int tables = (header[0] << 8) | header[1];
			bool outlines = false;
			long nameOffset = -1;
			long nameLength = 0;
			for (int i = 0; i < tables; i++)
			{
				byte[] record = Read(stream, sfnt + 12 + 16 * i, 16);
				if (record == null)
				{
					return null;
				}
				uint tag = Be32(record, 0);
				if (tag == 0x6E616D65) // 'name'
				{
					nameOffset = Be32(record, 8);
					nameLength = Be32(record, 12);
				}
				else if (tag == 0x676C7966 || tag == 0x43464620 || tag == 0x43464632) // 'glyf', 'CFF ', 'CFF2'
				{
					outlines = true;
				}
			}
			if (nameOffset < 0 || !outlines)
			{
				return null;
			}
			byte[] table = Read(stream, nameOffset, (int)Math.Min(nameLength, 1 << 20));
			return table == null ? null : ReadNames(table);
		}

		// Same choice as the engine's FontFaceReader.TryReadNameTable: name 16 over 1 for the family, 17 over 2 for
		// the style, English (Windows) over Mac Roman; but UCS-4 records (encoding 10) decode as UCS-4, which the
		// engine does not do, and NULs never survive.
		private static string[] ReadNames(byte[] table)
		{
			if (table.Length < 6)
			{
				return null;
			}
			int count = (table[2] << 8) | table[3];
			int strings = (table[4] << 8) | table[5];
			string family = null, subfamily = null, typoFamily = null, typoSubfamily = null;
			int familyRank = 0, subfamilyRank = 0, typoFamilyRank = 0, typoSubfamilyRank = 0;
			for (int i = 0; i < count; i++)
			{
				int record = 6 + 12 * i;
				if (record + 12 > table.Length)
				{
					break;
				}
				int platform = (table[record] << 8) | table[record + 1];
				int encoding = (table[record + 2] << 8) | table[record + 3];
				int language = (table[record + 4] << 8) | table[record + 5];
				int name = (table[record + 6] << 8) | table[record + 7];
				int length = (table[record + 8] << 8) | table[record + 9];
				int offset = (table[record + 10] << 8) | table[record + 11];
				if (name != 1 && name != 2 && name != 16 && name != 17)
				{
					continue;
				}
				int rank;
				int width;
				if (platform == 3 && (encoding == 1 || encoding == 10))
				{
					rank = language == 0x409 ? 3 : 2;
					width = encoding == 10 ? 4 : 2;
				}
				else if (platform == 1 && encoding == 0)
				{
					rank = 1;
					width = 1;
				}
				else if (platform == 0)
				{
					rank = 2;
					width = 2;
				}
				else
				{
					continue;
				}
				string value = Clean(Decode(table, strings + offset, length, width));
				if (value.Length == 0)
				{
					continue;
				}
				switch (name)
				{
					case 1:
						if (rank > familyRank)
						{
							family = value;
							familyRank = rank;
						}
						break;
					case 2:
						if (rank > subfamilyRank)
						{
							subfamily = value;
							subfamilyRank = rank;
						}
						break;
					case 16:
						if (rank > typoFamilyRank)
						{
							typoFamily = value;
							typoFamilyRank = rank;
						}
						break;
					case 17:
						if (rank > typoSubfamilyRank)
						{
							typoSubfamily = value;
							typoSubfamilyRank = rank;
						}
						break;
				}
			}
			string chosen = typoFamily ?? family;
			if (string.IsNullOrEmpty(chosen))
			{
				return null;
			}
			return new[] { chosen, typoSubfamily ?? subfamily ?? "" };
		}

		private static string Decode(byte[] table, int start, int length, int width)
		{
			if (start < 0 || length <= 0 || start >= table.Length)
			{
				return "";
			}
			length = Math.Min(length, table.Length - start);
			var sb = new StringBuilder(length / width);
			for (int i = 0; i + width <= length; i += width)
			{
				uint code;
				if (width == 1)
				{
					code = table[start + i];
				}
				else if (width == 2)
				{
					code = (uint)((table[start + i] << 8) | table[start + i + 1]);
					if (code >= 0xD800 && code <= 0xDBFF && i + 4 <= length)
					{
						uint low = (uint)((table[start + i + 2] << 8) | table[start + i + 3]);
						if (low >= 0xDC00 && low <= 0xDFFF)
						{
							sb.Append((char)code);
							sb.Append((char)low);
							i += 2;
							continue;
						}
					}
				}
				else
				{
					code = ((uint)table[start + i] << 24) | ((uint)table[start + i + 1] << 16)
						| ((uint)table[start + i + 2] << 8) | table[start + i + 3];
				}
				if (code == 0 || code > 0x10FFFF)
				{
					continue;
				}
				if (code <= 0xFFFF)
				{
					sb.Append((char)code);
				}
				else
				{
					sb.Append(char.ConvertFromUtf32((int)code));
				}
			}
			return sb.ToString();
		}

		private static string Clean(string value)
		{
			var sb = new StringBuilder(value.Length);
			bool space = false;
			foreach (char c in value)
			{
				if (c == '\0')
				{
					continue;
				}
				if (char.IsWhiteSpace(c))
				{
					space = sb.Length > 0;
					continue;
				}
				if (space)
				{
					sb.Append(' ');
				}
				space = false;
				sb.Append(c);
			}
			return sb.ToString();
		}

		private static uint ReadU32(FileStream stream, long offset)
		{
			byte[] bytes = Read(stream, offset, 4);
			return bytes == null ? 0 : Be32(bytes, 0);
		}

		private static uint Be32(byte[] bytes, int offset)
		{
			return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
		}

		private static byte[] Read(FileStream stream, long offset, int count)
		{
			if (offset < 0 || count <= 0 || offset + count > stream.Length)
			{
				return null;
			}
			var bytes = new byte[count];
			stream.Position = offset;
			int done = 0;
			while (done < count)
			{
				int read = stream.Read(bytes, done, count - done);
				if (read <= 0)
				{
					return null;
				}
				done += read;
			}
			return bytes;
		}
	}
}
