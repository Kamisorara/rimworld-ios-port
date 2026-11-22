using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The mod list's "auto-sort" button sorts the way RimSort does, instead of only by the rules mods declare in
	/// their About.xml (which is all the game's own sort knows). Rules come from four places: the mods' About.xml
	/// (loadAfter, loadBefore and the forced ones), their dependencies (a mod loads after what it depends on), the
	/// community rules database RimSort maintains (shipped as ModRules.txt next to the game, from host/modrules;
	/// refreshed from GitHub into Config/ModRules.txt when the mod list page opens and the copy is older than three
	/// days), and a few built-in ones (Harmony and Prepatcher before Core, Core before the expansions, the expansions
	/// in their order).
	///
	/// RimSort's tiers come first: the official content with Harmony and Prepatcher, then known frameworks, then
	/// everything else, then the mods the database tags "load at the bottom"; a mod that one of these must load after
	/// (or, for the bottom, before) joins its tier. Within that the list is sorted topologically, taking the mods in
	/// their current order whenever the rules allow: a list that already satisfies the rules does not change, and a
	/// mod added at the end stays there unless a rule moves it. Rules that cannot all be satisfied are reported and
	/// the mods concerned keep their order.
	///
	/// Ported from the 1.6 project (tools/portmobile/ModSort.cs); the sorting is the same.
	/// </summary>
	internal static class ModSort
	{
		private const string RulesUrl = "https://raw.githubusercontent.com/RimSort/Community-Rules-Database/main/communityRules.json";
		private const double RefreshDays = 3.0;

		private static readonly string[] Official = { "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology", "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey" };
		// Loaded before the official content (RimSort's first tier, with the official content).
		private static readonly string[] First = { "brrainz.harmony", "zetrith.prepatcher" };
		// Frameworks that go right after the official content (RimSort's second tier), besides what the rules tag "top".
		private static readonly string[] Frameworks = { "unlimitedhugs.hugslib", "oskarpotocki.vanillafactionsexpanded.core", "imranfish.xmlextensions", "sindre0830.universum" };

		private sealed class Rule
		{
			public readonly List<string> After = new List<string>(), Before = new List<string>(), Incompatible = new List<string>();
			public bool Top, Bottom;
		}

		private static Dictionary<string, Rule> rules;
		private static long rulesTimestamp;
		private static string rulesPath;

		private sealed class Edge
		{
			public Entry Other;
			public string Source; // null: the mod's own About.xml
		}

		private sealed class Entry
		{
			public ModMetaData Mod;
			public int Index; // in the list before sorting
			public string Id;
			public int Tier = 2;
			public readonly List<Edge> After = new List<Edge>(), Before = new List<Edge>();
		}

		private static bool Chinese => Texts.Chinese;

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(ModsConfig), nameof(ModsConfig.TrySortMods)), prefix: new HarmonyMethod(typeof(ModSort), nameof(BeforeSort)));
				harmony.Patch(AccessTools.Method(typeof(Page_ModsConfig), nameof(Page_ModsConfig.PreOpen)), postfix: new HarmonyMethod(typeof(ModSort), nameof(AfterModsPageOpened)));
				Console.WriteLine("[mobile] mod sort: the mod page's auto-sort follows RimSort's rules");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mod sort not installed: " + e);
			}
		}

		private static string SourceDependency => Chinese ? "依赖" : "dependency";
		private static string SourceCommunity => Chinese ? "社区规则库" : "community rules";
		private static string SourceBuiltIn => Chinese ? "内置" : "built-in";

		/// <summary>Prefix of ModsConfig.TrySortMods: the game's sort is replaced unless this one fails.</summary>
		internal static bool BeforeSort()
		{
			try
			{
				List<ModMetaData> mods = ModsConfig.ActiveModsInLoadOrder.ToList();
				if (mods.Count == 0 || mods.Any(m => m == null))
				{
					return true;
				}
				LoadRules();
				Result result = Sort(mods);
				ModsConfig.Reorder(result.Order);
				Console.WriteLine("[mobile] mod sort: " + result.Summary);
				if (Current.Root != null && Find.WindowStack != null && Find.WindowStack.IsOpen<Page_ModsConfig>())
				{
					Find.WindowStack.Add(new Dialog_MessageBox(result.Report, null, null, null, null, Chinese ? "自动排序" : "Auto-sort"));
				}
				return false;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mod sort failed, the game's own sort runs instead: " + e);
				return true;
			}
		}

		private sealed class Result
		{
			public List<int> Order;
			public string Summary, Report;
		}

		private static string PlainId(string id)
		{
			if (id == null)
			{
				return "";
			}
			id = id.Trim().ToLowerInvariant();
			if (id.EndsWith(ModMetaData.SteamModPostfix))
			{
				id = id.Substring(0, id.Length - ModMetaData.SteamModPostfix.Length);
			}
			return id;
		}

		private static Result Sort(List<ModMetaData> mods)
		{
			var entries = new List<Entry>();
			var byId = new Dictionary<string, Entry>();
			for (int i = 0; i < mods.Count; i++)
			{
				var e = new Entry { Mod = mods[i], Index = i, Id = PlainId(mods[i].PackageIdNonUnique) };
				entries.Add(e);
				if (!byId.ContainsKey(e.Id))
				{
					byId[e.Id] = e;
				}
			}
			Entry Find(string id)
			{
				return byId.TryGetValue(PlainId(id), out Entry found) ? found : null;
			}
			int own = 0, dependencies = 0, community = 0, builtIn = 0;
			void Link(Entry first, Entry second, string source)
			{
				if (first == null || second == null || first == second || second.After.Any(x => x.Other == first))
				{
					return;
				}
				second.After.Add(new Edge { Other = first, Source = source });
				first.Before.Add(new Edge { Other = second, Source = source });
				if (source == null) own++;
				else if (source == SourceDependency) dependencies++;
				else if (source == SourceCommunity) community++;
				else builtIn++;
			}
			var incompatible = new List<string>();
			foreach (Entry e in entries)
			{
				ModMetaData m = e.Mod;
				foreach (string id in m.LoadAfter.Concat(m.ForceLoadAfter))
				{
					Link(Find(id), e, null);
				}
				foreach (string id in m.LoadBefore.Concat(m.ForceLoadBefore))
				{
					Link(e, Find(id), null);
				}
				foreach (ModDependency d in m.Dependencies)
				{
					Entry dep = Find(d.packageId);
					if (dep != null && !e.Before.Any(x => x.Other == dep))
					{
						Link(dep, e, SourceDependency);
					}
				}
				if (rules.TryGetValue(e.Id, out Rule r))
				{
					foreach (string id in r.After)
					{
						Link(Find(id), e, SourceCommunity);
					}
					foreach (string id in r.Before)
					{
						Link(e, Find(id), SourceCommunity);
					}
					foreach (string id in r.Incompatible)
					{
						Entry other = Find(id);
						if (other != null)
						{
							incompatible.Add(e.Mod.Name + (Chinese ? " 与 " : " and ") + other.Mod.Name);
						}
					}
				}
			}
			Entry core = Find(Official[0]);
			foreach (string id in First)
			{
				Link(Find(id), core, SourceBuiltIn);
			}
			Entry previous = core;
			for (int i = 1; i < Official.Length; i++)
			{
				Entry expansion = Find(Official[i]);
				if (expansion != null)
				{
					Link(previous, expansion, SourceBuiltIn);
					previous = expansion;
				}
			}

			// Tiers.
			var top = entries.Where(e => Official.Contains(e.Id) || First.Contains(e.Id)).ToList();
			foreach (Entry e in Closure(top, x => x.After.Select(a => a.Other)))
			{
				e.Tier = 0;
			}
			var frameworks = entries.Where(e => e.Tier == 2 && (Frameworks.Contains(e.Id) || (rules.TryGetValue(e.Id, out Rule r) && r.Top))).ToList();
			foreach (Entry e in Closure(frameworks, x => x.After.Select(a => a.Other)))
			{
				if (e.Tier == 2)
				{
					e.Tier = 1;
				}
			}
			var bottom = entries.Where(e => rules.TryGetValue(e.Id, out Rule r) && r.Bottom).ToList();
			foreach (Entry e in Closure(bottom, x => x.Before.Select(b => b.Other)))
			{
				if (e.Tier == 2)
				{
					e.Tier = 3;
				}
			}

			// Topological order, taking the mods in their current order whenever the rules allow it.
			var remaining = entries.OrderBy(e => e.Tier).ThenBy(e => e.Index).ToList();
			var placed = new List<Entry>();
			var done = new HashSet<Entry>();
			var conflicts = new List<string>();
			while (remaining.Count > 0)
			{
				Entry pick = remaining.FirstOrDefault(e => e.After.All(a => done.Contains(a.Other)));
				if (pick == null)
				{
					// A cycle: the first of them keeps its place and the rules it breaks are reported.
					pick = remaining[0];
					foreach (Edge a in pick.After)
					{
						if (!done.Contains(a.Other))
						{
							conflicts.Add(Describe(pick, a.Other, a.Source));
						}
					}
				}
				remaining.Remove(pick);
				done.Add(pick);
				placed.Add(pick);
			}

			// The mods that moved: those outside the longest subsequence that kept its order.
			var newPosition = new Dictionary<Entry, int>();
			for (int i = 0; i < placed.Count; i++)
			{
				newPosition[placed[i]] = i;
			}
			HashSet<Entry> kept = LongestKeptOrder(entries.Select(e => newPosition[e]).ToList(), entries);
			var moved = new List<string>();
			foreach (Entry e in placed)
			{
				if (!kept.Contains(e))
				{
					moved.Add(MoveLine(e, newPosition[e]));
				}
			}

			var result = new Result { Order = placed.Select(e => e.Index).ToList() };
			string database = rulesTimestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(rulesTimestamp).ToString("yyyy-MM-dd") : (Chinese ? "无" : "none");
			var sb = new StringBuilder();
			if (Chinese)
			{
				sb.Append($"共 {entries.Count} 个，调整了 {moved.Count} 个的位置。规则：mod 自带 {own} 条、依赖 {dependencies} 条、社区规则库 {community} 条（库日期 {database}）、内置 {builtIn} 条。");
			}
			else
			{
				sb.Append($"{entries.Count} mods, {moved.Count} moved. Rules: {own} from the mods' About.xml, {dependencies} dependencies, {community} from the community database (dated {database}), {builtIn} built-in.");
			}
			result.Summary = sb.ToString();
			if (moved.Count == 0)
			{
				sb.Append("\n\n" + (Chinese ? "现在的顺序已经符合全部规则。" : "The order already satisfies every rule."));
			}
			else
			{
				sb.Append("\n\n" + (Chinese ? "调整：" : "Moved:") + "\n" + string.Join("\n", moved));
			}
			if (conflicts.Count > 0)
			{
				sb.Append("\n\n" + (Chinese ? "互相矛盾、没有满足的规则（这些 mod 保持原来的先后）：" : "Rules that contradict each other (these mods keep their order):") + "\n" + string.Join("\n", conflicts.Distinct()));
			}
			if (incompatible.Count > 0)
			{
				sb.Append("\n\n" + (Chinese ? "社区规则库标注为不兼容：" : "Marked incompatible by the community database:") + "\n" + string.Join("\n", incompatible.Distinct().Select(x => "- " + x)));
			}
			result.Report = sb.ToString();
			return result;
		}

		private static string Describe(Entry mod, Entry other, string source)
		{
			string tag = source == null ? "" : "（" + source + "）";
			if (!Chinese)
			{
				tag = source == null ? "" : " (" + source + ")";
				return $"- {mod.Mod.Name} after {other.Mod.Name}{tag}";
			}
			return $"- {mod.Mod.Name} 要在 {other.Mod.Name} 之后{tag}";
		}

		private static string MoveLine(Entry e, int newIndex)
		{
			string after = Names(e.After), before = Names(e.Before);
			var reasons = new List<string>();
			if (after.Length > 0)
			{
				reasons.Add((Chinese ? "在 " : "after ") + after + (Chinese ? " 之后" : ""));
			}
			if (before.Length > 0)
			{
				reasons.Add((Chinese ? "在 " : "before ") + before + (Chinese ? " 之前" : ""));
			}
			string why = reasons.Count > 0 ? "：" + string.Join(Chinese ? "；" : "; ", reasons) : "";
			if (!Chinese)
			{
				why = reasons.Count > 0 ? ": " + string.Join("; ", reasons) : "";
			}
			return $"- {e.Mod.Name}  {e.Index + 1} → {newIndex + 1}{why}";
		}

		private static string Names(List<Edge> edges)
		{
			var names = edges.Select(x => x.Other.Mod.Name + (x.Source == null ? "" : (Chinese ? "（" + x.Source + "）" : " (" + x.Source + ")"))).ToList();
			if (names.Count > 4)
			{
				int more = names.Count - 3;
				names = names.Take(3).ToList();
				names.Add(Chinese ? $"等 {more} 个" : $"{more} more");
			}
			return string.Join(Chinese ? "、" : ", ", names);
		}

		private static HashSet<Entry> Closure(List<Entry> start, Func<Entry, IEnumerable<Entry>> next)
		{
			var seen = new HashSet<Entry>(start);
			var queue = new Queue<Entry>(start);
			while (queue.Count > 0)
			{
				foreach (Entry n in next(queue.Dequeue()))
				{
					if (seen.Add(n))
					{
						queue.Enqueue(n);
					}
				}
			}
			return seen;
		}

		/// <summary>The mods of a longest increasing subsequence of their new positions, taken in the old order.</summary>
		private static HashSet<Entry> LongestKeptOrder(List<int> positions, List<Entry> entries)
		{
			int n = positions.Count;
			var length = new int[n];
			var previous = new int[n];
			int bestEnd = -1;
			for (int i = 0; i < n; i++)
			{
				length[i] = 1;
				previous[i] = -1;
				for (int j = 0; j < i; j++)
				{
					if (positions[j] < positions[i] && length[j] + 1 > length[i])
					{
						length[i] = length[j] + 1;
						previous[i] = j;
					}
				}
				if (bestEnd < 0 || length[i] > length[bestEnd])
				{
					bestEnd = i;
				}
			}
			var kept = new HashSet<Entry>();
			for (int i = bestEnd; i >= 0; i = previous[i])
			{
				kept.Add(entries[i]);
			}
			return kept;
		}

		// ──────────────────── The rules file ────────────────────

		private static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "ModRules.txt");
		private static string ConfigFolder => Path.GetDirectoryName(GenFilePaths.ModsConfigFilePath);
		private static string DownloadedPath => Path.Combine(ConfigFolder, "ModRules.txt");
		private static string CheckedPath => Path.Combine(ConfigFolder, "ModRules.checked");

		private static long HeaderTimestamp(string path)
		{
			try
			{
				if (!File.Exists(path))
				{
					return -1;
				}
				using (var reader = new StreamReader(path))
				{
					string first = reader.ReadLine() ?? "";
					string[] words = first.Split(' ');
					return first.StartsWith("# rules ") && words.Length >= 3 && long.TryParse(words[2], out long ts) ? ts : 0;
				}
			}
			catch (Exception)
			{
				return -1;
			}
		}

		/// <summary>Reads the newer of the shipped and the downloaded rules file (again when another became the newer one).</summary>
		private static void LoadRules()
		{
			string chosen = null;
			long best = -1;
			foreach (string path in new[] { ShippedPath, DownloadedPath })
			{
				long ts = HeaderTimestamp(path);
				if (ts > best)
				{
					best = ts;
					chosen = path;
				}
			}
			if (rules != null && chosen == rulesPath && best == rulesTimestamp)
			{
				return;
			}
			rules = new Dictionary<string, Rule>();
			rulesPath = chosen;
			rulesTimestamp = Math.Max(0, best);
			if (chosen == null)
			{
				Console.WriteLine("[mobile] mod sort: no rules file (ModRules.txt next to the game), sorting by the mods' own rules only");
				return;
			}
			foreach (string line in File.ReadAllLines(chosen))
			{
				if (line.Length == 0 || line[0] == '#')
				{
					continue;
				}
				string[] parts = line.Split('\t');
				var rule = new Rule();
				for (int i = 1; i < parts.Length; i++)
				{
					string part = parts[i];
					if (part.StartsWith("after=")) rule.After.AddRange(part.Substring(6).Split(',').Where(x => x.Length > 0));
					else if (part.StartsWith("before=")) rule.Before.AddRange(part.Substring(7).Split(',').Where(x => x.Length > 0));
					else if (part.StartsWith("incompatible=")) rule.Incompatible.AddRange(part.Substring(13).Split(',').Where(x => x.Length > 0));
					else if (part == "top") rule.Top = true;
					else if (part == "bottom") rule.Bottom = true;
				}
				rules[PlainId(parts[0])] = rule;
			}
			Console.WriteLine($"[mobile] mod sort: {rules.Count} mods with community rules from {chosen} (database of {DateTimeOffset.FromUnixTimeSeconds(rulesTimestamp):yyyy-MM-dd})");
		}

		// ──────────────────── Refreshing it from GitHub ────────────────────

		private static bool triedRefresh;

		/// <summary>Postfix of Page_ModsConfig.PreOpen: once per run, a copy older than a few days is refreshed in the background.</summary>
		internal static void AfterModsPageOpened()
		{
			if (triedRefresh)
			{
				return;
			}
			triedRefresh = true;
			try
			{
				if (File.Exists(CheckedPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(CheckedPath)).TotalDays < RefreshDays)
				{
					return;
				}
				// The game's paths are read here, on its own thread; the download and the conversion run on another.
				string downloaded = DownloadedPath, checkedPath = CheckedPath;
				Task.Run(() => Refresh(downloaded, checkedPath));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mod sort: could not start the rules download: " + e.Message);
			}
		}

		private static void Refresh(string downloaded, string checkedPath)
		{
			try
			{
				using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) })
				{
					string json = client.GetStringAsync(RulesUrl).GetAwaiter().GetResult();
					string converted = Convert(json, out long timestamp, out int count);
					Directory.CreateDirectory(Path.GetDirectoryName(downloaded));
					File.WriteAllText(downloaded, converted);
					File.WriteAllText(checkedPath, DateTime.UtcNow.ToString("o"));
					Console.WriteLine($"[mobile] mod sort: community rules refreshed, {count} mods, database of {DateTimeOffset.FromUnixTimeSeconds(timestamp):yyyy-MM-dd}");
				}
			}
			catch (Exception e)
			{
				Exception inner = e.InnerException ?? e;
				Console.WriteLine("[mobile] mod sort: rules download failed (" + inner.GetType().Name + ": " + inner.Message + "), the copy on hand stays in use");
			}
		}

		/// <summary>communityRules.json in the text form of ModRules.txt (the same conversion as tools/modrules/build.js).</summary>
		private static string Convert(string json, out long timestamp, out int count)
		{
			var root = MiniJson.Parse(json) as Dictionary<string, object>;
			if (root == null || !(root.TryGetValue("rules", out object rulesObject) && rulesObject is Dictionary<string, object> all))
			{
				throw new FormatException("no \"rules\" object");
			}
			timestamp = root.TryGetValue("timestamp", out object ts) && ts is double d ? (long)d : 0;
			var sb = new StringBuilder();
			sb.Append("# rules ").Append(timestamp).Append(' ').Append(DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("yyyy-MM-dd")).Append('\n');
			count = 0;
			foreach (string pkg in all.Keys.OrderBy(k => k, StringComparer.Ordinal))
			{
				var r = all[pkg] as Dictionary<string, object>;
				if (r == null)
				{
					continue;
				}
				string id = pkg.ToLowerInvariant();
				var parts = new List<string> { id };
				List<string> after = Ids(r, "loadAfter").Where(x => x != id).ToList(), before = Ids(r, "loadBefore").Where(x => x != id).ToList(), incompatible = Ids(r, "incompatibleWith");
				if (after.Count > 0) parts.Add("after=" + string.Join(",", after));
				if (before.Count > 0) parts.Add("before=" + string.Join(",", before));
				if (Flag(r, "loadTop")) parts.Add("top");
				if (Flag(r, "loadBottom")) parts.Add("bottom");
				if (incompatible.Count > 0) parts.Add("incompatible=" + string.Join(",", incompatible));
				if (parts.Count == 1)
				{
					continue;
				}
				sb.Append(string.Join("\t", parts)).Append('\n');
				count++;
			}
			return sb.ToString();
		}

		private static List<string> Ids(Dictionary<string, object> rule, string key)
		{
			var ids = new List<string>();
			if (!rule.TryGetValue(key, out object value) || value == null)
			{
				return ids;
			}
			if (value is Dictionary<string, object> map)
			{
				ids.AddRange(map.Keys.Select(k => k.ToLowerInvariant()));
			}
			else if (value is List<object> list)
			{
				ids.AddRange(list.Where(x => x != null).Select(x => x.ToString().ToLowerInvariant()));
			}
			else
			{
				ids.Add(value.ToString().ToLowerInvariant());
			}
			return ids;
		}

		private static bool Flag(Dictionary<string, object> rule, string key)
		{
			if (!rule.TryGetValue(key, out object value) || value == null)
			{
				return false;
			}
			if (value is bool b)
			{
				return b;
			}
			if (value is Dictionary<string, object> map)
			{
				if (map.TryGetValue("value", out object inner))
				{
					return inner is bool ib ? ib : inner != null && inner.ToString() == "true";
				}
				return map.Count > 0;
			}
			return value.ToString() == "true";
		}
	}

	/// <summary>A small JSON reader: objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double.</summary>
	internal static class MiniJson
	{
		public static object Parse(string text)
		{
			int i = 0;
			object value = ReadValue(text, ref i);
			SkipSpace(text, ref i);
			if (i != text.Length)
			{
				throw new FormatException("trailing text at " + i);
			}
			return value;
		}

		private static void SkipSpace(string s, ref int i)
		{
			while (i < s.Length && char.IsWhiteSpace(s[i]))
			{
				i++;
			}
		}

		private static object ReadValue(string s, ref int i)
		{
			SkipSpace(s, ref i);
			if (i >= s.Length)
			{
				throw new FormatException("unexpected end");
			}
			char c = s[i];
			if (c == '{')
			{
				i++;
				var map = new Dictionary<string, object>();
				SkipSpace(s, ref i);
				if (i < s.Length && s[i] == '}')
				{
					i++;
					return map;
				}
				while (true)
				{
					SkipSpace(s, ref i);
					string key = ReadString(s, ref i);
					SkipSpace(s, ref i);
					Expect(s, ref i, ':');
					map[key] = ReadValue(s, ref i);
					SkipSpace(s, ref i);
					if (i < s.Length && s[i] == ',')
					{
						i++;
						continue;
					}
					Expect(s, ref i, '}');
					return map;
				}
			}
			if (c == '[')
			{
				i++;
				var list = new List<object>();
				SkipSpace(s, ref i);
				if (i < s.Length && s[i] == ']')
				{
					i++;
					return list;
				}
				while (true)
				{
					list.Add(ReadValue(s, ref i));
					SkipSpace(s, ref i);
					if (i < s.Length && s[i] == ',')
					{
						i++;
						continue;
					}
					Expect(s, ref i, ']');
					return list;
				}
			}
			if (c == '"')
			{
				return ReadString(s, ref i);
			}
			if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0)
			{
				i += 4;
				return true;
			}
			if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0)
			{
				i += 5;
				return false;
			}
			if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0)
			{
				i += 4;
				return null;
			}
			int start = i;
			while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
			{
				i++;
			}
			if (start == i)
			{
				throw new FormatException("unexpected character at " + i);
			}
			return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
		}

		private static void Expect(string s, ref int i, char c)
		{
			if (i >= s.Length || s[i] != c)
			{
				throw new FormatException("expected " + c + " at " + i);
			}
			i++;
		}

		private static string ReadString(string s, ref int i)
		{
			Expect(s, ref i, '"');
			var sb = new StringBuilder();
			while (i < s.Length)
			{
				char c = s[i++];
				if (c == '"')
				{
					return sb.ToString();
				}
				if (c != '\\')
				{
					sb.Append(c);
					continue;
				}
				if (i >= s.Length)
				{
					break;
				}
				char e = s[i++];
				switch (e)
				{
					case 'n': sb.Append('\n'); break;
					case 't': sb.Append('\t'); break;
					case 'r': sb.Append('\r'); break;
					case 'b': sb.Append('\b'); break;
					case 'f': sb.Append('\f'); break;
					case 'u':
						sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
						i += 4;
						break;
					default: sb.Append(e); break;
				}
			}
			throw new FormatException("unterminated string");
		}
	}
}
