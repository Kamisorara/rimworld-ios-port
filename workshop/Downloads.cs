using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SteamKit2;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace PortWorkshop
{
	internal static class Texts
	{
		public static string Say(string chinese, string english) => PortMobile.ModInstall.Chinese ? chinese : english;
	}

	internal enum ItemState
	{
		Unknown,      // looked up, nothing installed under its id
		Installed,    // installed, the same version as on the Workshop
		Outdated,     // installed, the Workshop has a newer version
		Untracked,    // installed (its folder has the id as its name), but where it came from and when is not known
		Queued,
		Downloading,
		Installing,
		Done,
		Failed,
	}

	internal sealed class Item
	{
		public ulong Id;
		public string Title = "";
		public ulong Size;
		public ulong Manifest;
		public uint Updated;
		public string FileUrl = "";
		public bool Collection;
		public ulong RequiredBy; // added because another item needs it
		public ItemState State;
		public float Progress;
		public string Error = "";
		public bool Selected;
		public string Note = "";
	}

	/// <summary>
	/// The items the window shows, and what is done with them. A download takes the item's content from Steam's content
	/// servers as the Steam client does (the depot of RimWorld, the item's manifest, its chunks, decrypted and unpacked),
	/// into a staging folder, writes .workshop into the mod's folder (id, manifest, time of the version) and then hands
	/// the folder to the phone layer's import (PortMobile.ModInstall), which puts it into Mods, replacing the mod of the
	/// same package id unless that is another Workshop item, and prepares its assemblies.
	/// </summary>
	internal static class Downloads
	{
		public static readonly List<Item> Items = new List<Item>();
		public static volatile bool Busy;
		public static volatile string Status = "";
		private static readonly List<object> outcomes = new List<object>();
		public static volatile bool ReportReady;

		public static string ModsFolder;
		public static Version GameVersion;

		private static string StageRoot => Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "WorkshopStage");

		private static void Log(string text)
		{
			Console.WriteLine("[workshop] " + text);
		}

		public static List<Item> Snapshot()
		{
			lock (Items)
			{
				return Items.ToList();
			}
		}

		private static Item Get(ulong id, bool create)
		{
			lock (Items)
			{
				Item item = Items.FirstOrDefault(i => i.Id == id);
				if (item == null && create)
				{
					item = new Item { Id = id };
					Items.Add(item);
				}
				return item;
			}
		}

		/// <summary>Every number of six digits or more in the text (ids, or links that end in "?id=...").</summary>
		public static List<ulong> ParseIds(string text)
		{
			return Regex.Matches(text ?? "", "\\d{6,20}").Select(m => ulong.TryParse(m.Value, out ulong id) ? id : 0).Where(id => id != 0).Distinct().ToList();
		}

		// ---- what is installed ----

		internal sealed class Stamp
		{
			public ulong Manifest;
			public uint Updated;
		}

		/// <summary>
		/// The mods in a Mods folder that came from the Workshop: by .workshop, or by a folder named as an id (with no
		/// .workshop, null: where it came from and when is not known).
		/// </summary>
		internal static Dictionary<ulong, Stamp> Installed(string mods)
		{
			var found = new Dictionary<ulong, Stamp>();
			if (mods == null || !Directory.Exists(mods))
			{
				return found;
			}
			foreach (string folder in Directory.GetDirectories(mods))
			{
				try
				{
					string stampFile = Path.Combine(folder, ".workshop");
					if (File.Exists(stampFile))
					{
						string[] lines = File.ReadAllLines(stampFile);
						if (lines.Length >= 3 && ulong.TryParse(lines[0], out ulong id))
						{
							found[id] = new Stamp { Manifest = ulong.Parse(lines[1]), Updated = uint.Parse(lines[2]) };
							continue;
						}
					}
					if (ulong.TryParse(Path.GetFileName(folder), out ulong named) && !found.ContainsKey(named))
					{
						found[named] = null;
					}
				}
				catch (Exception)
				{
				}
			}
			return found;
		}

		private static void Fill(Item item, PublishedFileDetails details, Dictionary<ulong, Stamp> installed)
		{
			item.Title = string.IsNullOrEmpty(details.title) ? item.Id.ToString() : details.title;
			item.Size = details.file_size;
			item.Manifest = details.hcontent_file;
			item.Updated = details.time_updated;
			item.FileUrl = details.file_url ?? "";
			item.Collection = details.file_type == 2;
			if (details.result != 1)
			{
				item.State = ItemState.Failed;
				item.Error = Texts.Say("工坊里没有这个 mod（" + (EResult)details.result + "）", "Not on the Workshop (" + (EResult)details.result + ")");
				return;
			}
			if (details.consumer_appid != 0 && details.consumer_appid != Session.App)
			{
				item.State = ItemState.Failed;
				item.Error = Texts.Say("这不是 RimWorld 的 mod", "Not a RimWorld item");
				return;
			}
			if (item.State == ItemState.Queued || item.State == ItemState.Downloading || item.State == ItemState.Installing)
			{
				return;
			}
			item.Error = "";
			if (!installed.TryGetValue(item.Id, out Stamp stamp))
			{
				item.State = ItemState.Unknown;
			}
			else if (stamp == null)
			{
				item.State = ItemState.Untracked;
			}
			else
			{
				item.State = UpToDate(stamp, item.Manifest, item.Updated) ? ItemState.Installed : ItemState.Outdated;
			}
		}

		/// <summary>
		/// Whether a mod's .workshop records the version the Workshop has: by the manifest of its content, or, for an old
		/// item that is one file on Steam's servers, by the time of the version. Such an item has no manifest, and its
		/// .workshop records none either: compared by manifest first, it was always up to date.
		/// </summary>
		internal static bool UpToDate(Stamp stamp, ulong manifest, uint updated)
		{
			return manifest != 0 ? stamp.Manifest == manifest : stamp.Updated >= updated;
		}

		/// <summary>Looks the ids up and puts them into the list, a collection's items and an item's required items with them.</summary>
		private static async Task Add(List<ulong> ids, bool select)
		{
			Dictionary<ulong, Stamp> installed = Installed(ModsFolder);
			var pending = new Queue<(ulong id, ulong requiredBy)>(ids.Select(i => (i, 0UL)));
			var seen = new HashSet<ulong>();
			while (pending.Count > 0)
			{
				var batch = new List<(ulong id, ulong requiredBy)>();
				while (pending.Count > 0 && batch.Count < 50)
				{
					var next = pending.Dequeue();
					if (seen.Add(next.id))
					{
						batch.Add(next);
					}
				}
				if (batch.Count == 0)
				{
					continue;
				}
				List<PublishedFileDetails> details = await Session.Details(batch.Select(b => b.id).ToList());
				foreach (PublishedFileDetails one in details)
				{
					var (_, requiredBy) = batch.FirstOrDefault(b => b.id == one.publishedfileid);
					if (one.file_type == 2)
					{
						// A collection is a list of items, not an item to download.
						foreach (var child in one.children)
						{
							pending.Enqueue((child.publishedfileid, 0));
						}
						continue;
					}
					Item item = Get(one.publishedfileid, true);
					lock (Items)
					{
						Fill(item, one, installed);
						if (requiredBy != 0 && item.RequiredBy == 0)
						{
							item.RequiredBy = requiredBy;
						}
						if (select && (item.State == ItemState.Unknown || item.State == ItemState.Outdated))
						{
							item.Selected = true;
						}
					}
					foreach (var child in one.children)
					{
						pending.Enqueue((child.publishedfileid, one.publishedfileid));
					}
				}
			}
		}

		private static void Run(string what, Func<Task> work)
		{
			if (Busy)
			{
				return;
			}
			Busy = true;
			Status = what;
			Task.Run(async () =>
			{
				try
				{
					await work();
					if (Status == what)
					{
						Status = "";
					}
				}
				catch (Exception e)
				{
					Log(what + " failed: " + e);
					Status = Texts.Say("出错了：", "Failed: ") + e.Message;
				}
				finally
				{
					Busy = false;
				}
			});
		}

		public static void AddFromText(string text)
		{
			List<ulong> ids = ParseIds(text);
			if (ids.Count == 0)
			{
				Status = Texts.Say("没找到 mod 的 ID：粘贴工坊链接，或者直接输入数字 ID。", "No item id found: paste Workshop links or type the numeric ids.");
				return;
			}
			Run(Texts.Say("正在读取 " + ids.Count + " 个 mod 的信息……", "Reading " + ids.Count + " item(s)..."), () => Add(ids, true));
		}

		public static void AddSubscriptions()
		{
			Run(Texts.Say("正在读取订阅……", "Reading the subscriptions..."), async () =>
			{
				List<PublishedFileDetails> subscribed = await Session.Subscriptions();
				await Add(subscribed.Select(d => d.publishedfileid).ToList(), true);
				Status = Texts.Say("订阅了 " + subscribed.Count + " 个。", subscribed.Count + " subscribed.");
			});
		}

		public static void AddInstalled()
		{
			Run(Texts.Say("正在检查已装的工坊 mod……", "Checking the installed Workshop mods..."), async () =>
			{
				List<ulong> ids = Installed(ModsFolder).Keys.ToList();
				await Add(ids, false);
				int outdated;
				lock (Items)
				{
					outdated = Items.Count(i => i.State == ItemState.Outdated);
					foreach (Item item in Items.Where(i => i.State == ItemState.Outdated))
					{
						item.Selected = true;
					}
				}
				Status = Texts.Say("已装 " + ids.Count + " 个工坊 mod，" + outdated + " 个有更新。", ids.Count + " installed Workshop mods, " + outdated + " with an update.");
			});
		}

		/// <summary>
		/// The mods the main menu's update notice names (Updates): looked up again, and the ones the Workshop has a newer
		/// version of ticked, as "检查已装 mod 的更新" ticks them (the items they require are listed, not ticked).
		/// </summary>
		public static void AddUpdates(List<ulong> ids)
		{
			Run(Texts.Say("正在读取 " + ids.Count + " 个 mod 的信息……", "Reading " + ids.Count + " item(s)..."), async () =>
			{
				await Add(ids, false);
				int outdated = 0;
				lock (Items)
				{
					foreach (Item item in Items.Where(i => i.State == ItemState.Outdated && ids.Contains(i.Id)))
					{
						item.Selected = true;
						outdated++;
					}
				}
				Status = Texts.Say(outdated + " 个 mod 有更新。", outdated + " with an update.");
			});
		}

		// ---- downloading ----

		public static void DownloadSelected()
		{
			List<Item> chosen;
			lock (Items)
			{
				chosen = Items.Where(i => i.Selected && !i.Collection && (i.Manifest != 0 || i.FileUrl.Length > 0)).ToList();
				foreach (Item item in chosen)
				{
					item.State = ItemState.Queued;
					item.Error = "";
					item.Progress = 0f;
				}
			}
			if (chosen.Count == 0)
			{
				return;
			}
			Run(Texts.Say("下载中……", "Downloading..."), async () =>
			{
				lock (outcomes)
				{
					outcomes.Clear();
				}
				int done = 0;
				foreach (Item item in chosen)
				{
					Status = Texts.Say($"下载中（{done + 1}/{chosen.Count}）：{item.Title}", $"Downloading ({done + 1}/{chosen.Count}): {item.Title}");
					try
					{
						await Download(item);
					}
					catch (Exception e)
					{
						Log("download of " + item.Id + " failed: " + e);
						item.State = ItemState.Failed;
						item.Error = e.Message;
					}
					done++;
				}
				int ok = chosen.Count(i => i.State == ItemState.Done);
				Status = Texts.Say($"完成：{ok} 个装好了" + (ok < chosen.Count ? $"，{chosen.Count - ok} 个失败" : "") + "。", $"Finished: {ok} installed" + (ok < chosen.Count ? $", {chosen.Count - ok} failed" : "") + ".");
				// The main menu's update notice no longer names what is installed anew now.
				Updates.Refresh(ModsFolder);
				ReportReady = true;
			});
		}

		/// <summary>On the game's thread: the mod page is refreshed and a box says what was installed.</summary>
		public static void Report()
		{
			ReportReady = false;
			List<object> all;
			lock (outcomes)
			{
				all = outcomes.ToList();
				outcomes.Clear();
			}
			if (all.Count > 0)
			{
				PortMobile.ModInstall.Report(all);
			}
		}

		private static async Task Download(Item item)
		{
			item.State = ItemState.Downloading;
			string stage = Path.Combine(StageRoot, item.Id.ToString());
			if (Directory.Exists(stage))
			{
				Directory.Delete(stage, true);
			}
			string modFolder = Path.Combine(stage, item.Id.ToString());
			Directory.CreateDirectory(modFolder);
			try
			{
				if (item.Manifest != 0)
				{
					await FromDepot(item, modFolder);
				}
				else if (item.FileUrl.Length > 0)
				{
					await FromUrl(item, modFolder, stage);
				}
				else
				{
					throw new Exception(Texts.Say("工坊没有给出这个 mod 的内容", "The Workshop gives no content for this item"));
				}
				File.WriteAllLines(Path.Combine(modFolder, ".workshop"), new[] { item.Id.ToString(), item.Manifest.ToString(), item.Updated.ToString(), item.Title });
				item.State = ItemState.Installing;
				item.Progress = 1f;
				object outcome = PortMobile.ModInstall.Install(stage, ModsFolder, GameVersion, out string problems);
				lock (outcomes)
				{
					outcomes.Add(outcome);
				}
				if (PortMobile.ModInstall.FirstPackage(outcome) == null && problems.Length > 0)
				{
					throw new Exception(problems);
				}
				item.State = ItemState.Done;
				item.Error = problems;
				item.Selected = false;
				Log("installed " + item.Id + " " + item.Title + (problems.Length > 0 ? " (" + problems + ")" : ""));
			}
			finally
			{
				try
				{
					if (Directory.Exists(stage))
					{
						Directory.Delete(stage, true);
					}
				}
				catch (Exception)
				{
				}
			}
		}

		/// <summary>
		/// For testing from the PC: the ids in game/workshop-test.txt are looked up and their manifests fetched (nothing is
		/// downloaded beyond the manifests, nothing is installed); the outcome goes to the log. The file is deleted.
		/// </summary>
		private static bool downloadAfterAdding;

		public static void TestManifests()
		{
			if (downloadAfterAdding && !Busy)
			{
				downloadAfterAdding = false;
				DownloadSelected();
				return;
			}
			string file = Path.Combine(AppContext.BaseDirectory, "workshop-test.txt");
			if (!File.Exists(file) || Busy)
			{
				return;
			}
			string text = File.ReadAllText(file);
			List<ulong> ids = ParseIds(text);
			File.Delete(file);
			if (text.Contains("download"))
			{
				// "download <links or ids>": what the player does with the field and the button, from the PC.
				Log("test: adding " + ids.Count + " id(s) and downloading what gets chosen");
				downloadAfterAdding = true;
				Run("test add", () => Add(ids, true));
				return;
			}
			Run("test", async () =>
			{
				List<PublishedFileDetails> details = await Session.Details(ids);
				byte[] key = await Session.DepotKey();
				List<Server> servers = await Session.Servers();
				foreach (PublishedFileDetails one in details.Where(d => d.hcontent_file != 0))
				{
					ulong code = await Session.RequestCode(one.hcontent_file);
					foreach (Server server in servers.Take(10))
					{
						try
						{
							string token = await Session.CdnToken(server);
							DepotManifest manifest = await Session.Cdn.DownloadManifestAsync(Session.App, one.hcontent_file, code, server, key, null, token);
							Log("test: manifest of " + one.publishedfileid + " from " + server.Host + " (" + server.Type + (token != null ? ", token" : "") + "): " + (manifest.Files?.Count ?? 0) + " files");
							break;
						}
						catch (Exception e)
						{
							Log("test: manifest of " + one.publishedfileid + " from " + server.Host + " (" + server.Type + "): " + e.Message);
						}
					}
				}
			});
		}

		/// <summary>As the Steam client does it: RimWorld's depot holds the item; its manifest lists the files and their chunks.</summary>
		private static async Task FromDepot(Item item, string folder)
		{
			byte[] key = await Session.DepotKey();
			List<Server> servers = await Session.Servers();
			ulong code = await Session.RequestCode(item.Manifest);
			DepotManifest manifest = null;
			Server chunkServer = null;
			Exception last = null;
			foreach (Server server in servers.Take(10))
			{
				try
				{
					string token = await Session.CdnToken(server);
					manifest = await Session.Cdn.DownloadManifestAsync(Session.App, item.Manifest, code, server, key, null, token);
					chunkServer = server;
					Log("manifest of " + item.Id + " from " + server.Host + (token != null ? " (with a token)" : ""));
					break;
				}
				catch (Exception e)
				{
					last = e;
					Log("manifest from " + server.Host + " failed: " + e.Message);
				}
			}
			if (manifest == null)
			{
				throw new Exception(Texts.Say("清单下载失败：", "The manifest could not be downloaded: ") + last?.Message);
			}
			if (manifest.FilenamesEncrypted)
			{
				manifest.DecryptFilenames(key);
			}
			List<DepotManifest.FileData> files = manifest.Files ?? new List<DepotManifest.FileData>();
			ulong total = (ulong)files.Sum(f => (long)f.TotalSize), done = 0;
			var chunkJobs = new List<(string path, DepotManifest.ChunkData chunk)>();
			string root = Path.GetFullPath(folder);
			foreach (DepotManifest.FileData file in files)
			{
				string relative = file.FileName.Replace('\\', '/').TrimStart('/');
				string path = Path.GetFullPath(Path.Combine(root, relative));
				if (!path.StartsWith(root + "/", StringComparison.Ordinal) && path != root)
				{
					throw new Exception("bad file name in the manifest: " + file.FileName);
				}
				if ((file.Flags & EDepotFileFlag.Directory) != 0)
				{
					Directory.CreateDirectory(path);
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
				{
					stream.SetLength((long)file.TotalSize);
				}
				foreach (DepotManifest.ChunkData chunk in file.Chunks)
				{
					chunkJobs.Add((path, chunk));
				}
			}
			using var gate = new SemaphoreSlim(8);
			int next = 0;
			var tasks = chunkJobs.Select(async job =>
			{
				await gate.WaitAsync();
				try
				{
					byte[] buffer = new byte[job.chunk.UncompressedLength];
					int written = -1;
					Exception failure = null;
					int start = Interlocked.Increment(ref next);
					for (int attempt = 0; attempt < 5 && written < 0; attempt++)
					{
						Server server = attempt == 0 ? chunkServer : servers[(start + attempt) % Math.Min(servers.Count, 10)];
						try
						{
							string token = await Session.CdnToken(server);
							written = await Session.Cdn.DownloadDepotChunkAsync(Session.App, job.chunk, server, buffer, key, null, token);
						}
						catch (Exception e)
						{
							failure = e;
						}
					}
					if (written < 0)
					{
						throw new Exception(Texts.Say("下载文件块失败：", "A chunk could not be downloaded: ") + failure?.Message);
					}
					using (SafeFileHandle handle = File.OpenHandle(job.path, FileMode.Open, FileAccess.Write, FileShare.Write))
					{
						RandomAccess.Write(handle, new ReadOnlySpan<byte>(buffer, 0, written), (long)job.chunk.Offset);
					}
					ulong now = Interlocked.Add(ref done, (ulong)written);
					item.Progress = total > 0 ? (float)now / total : 1f;
				}
				finally
				{
					gate.Release();
				}
			}).ToList();
			await Task.WhenAll(tasks);
		}

		/// <summary>An old item that is one file on Steam's servers (a zip of the mod, as far as RimWorld goes).</summary>
		private static async Task FromUrl(Item item, string folder, string stage)
		{
			string zip = Path.Combine(stage, "item.zip");
			using (var http = new HttpClient())
			using (var response = await http.GetAsync(item.FileUrl, HttpCompletionOption.ResponseHeadersRead))
			{
				response.EnsureSuccessStatusCode();
				using var source = await response.Content.ReadAsStreamAsync();
				using var target = File.Create(zip);
				await source.CopyToAsync(target);
			}
			ZipFile.ExtractToDirectory(zip, folder);
			File.Delete(zip);
		}
	}
}
