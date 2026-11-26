using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using SteamKit2.Internal;

namespace PortWorkshop
{
	/// <summary>
	/// The check behind the main menu's update notice (PortMobile.UpdateNotice): which installed Workshop mods have a
	/// newer version on the Workshop. With a remembered sign-in, at most once a day, when the main menu comes up:
	/// connected in the background, signed in with the remembered token, and the versions of the mods with a .workshop
	/// compared with the Workshop's as "检查已装 mod 的更新" compares them, from their details only (no required items,
	/// the descriptions cut short). No sign-in is asked for and nothing is downloaded. No remembered sign-in Steam takes,
	/// no network, a slow server: a line in the log, nothing on the screen, and no other try that day. What a check finds
	/// is kept in the app's private files next to the sign-in, for the later visits of the main menu (in this run and the
	/// next ones) and the days a check cannot be made, and is told until those mods are installed anew or deleted.
	/// </summary>
	internal static class Updates
	{
		// The first connection to Steam from China takes about 45 s (the list of servers comes from Steam's web API
		// first), later ones about 12 s; a sign-in that has not come after this is given up for the day.
		private static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

		/// <summary>A mod the check found a newer version of, and the version of it that was installed then (its .workshop).</summary>
		private sealed class Found
		{
			public ulong Id;
			public ulong Manifest;
			public uint Updated;
			public string Title;
		}

		private static readonly object gate = new object();
		private static bool running;
		// The last check's day and findings (read from the file the first time they are wanted), and of those the mods
		// still installed as they were then, which the notice names.
		private static string checkedOn;
		private static List<Found> found;
		private static List<Found> shown = new List<Found>();

		private static string StateFile => Path.Combine(Session.PrivateDir, "updates");

		private static void Log(string text)
		{
			Console.WriteLine("[workshop] " + text);
		}

		/// <summary>The mods the notice names now (Entry.OpenUpdates).</summary>
		public static List<ulong> Shown
		{
			get
			{
				lock (gate)
				{
					return shown.Select(f => f.Id).ToList();
				}
			}
		}

		/// <summary>On a worker thread, whenever the main menu comes up (Entry.CheckForUpdates): today's check if it is due, then the notice is told.</summary>
		public static async Task Check(string mods)
		{
			lock (gate)
			{
				if (running)
				{
					return;
				}
				running = true;
			}
			try
			{
				string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
				bool due;
				lock (gate)
				{
					Load();
					due = checkedOn != today;
					if (due)
					{
						// The day's one try, whatever comes of it: one that fails is not made again before tomorrow.
						checkedOn = today;
						Save();
					}
				}
				if (due)
				{
					List<Found> now = await Compare(mods);
					if (now != null)
					{
						lock (gate)
						{
							found = now;
							Save();
						}
					}
				}
				Refresh(mods);
			}
			catch (Exception e)
			{
				Log("update check went wrong: " + e.Message);
			}
			finally
			{
				lock (gate)
				{
					running = false;
				}
			}
		}

		/// <summary>
		/// The installed mods with a .workshop that the Workshop has a newer version of; null when no check could be made.
		/// A mod without one (imported, or in a folder named as its id) has no version to compare, as in the window.
		/// </summary>
		private static async Task<List<Found>> Compare(string mods)
		{
			Dictionary<ulong, Downloads.Stamp> installed = Downloads.Installed(mods);
			List<ulong> ids = installed.Where(i => i.Value != null).Select(i => i.Key).ToList();
			if (ids.Count == 0)
			{
				Log("update check: no mod from the Workshop window is installed, nothing to compare");
				return new List<Found>();
			}
			try
			{
				string failure = await Session.SignInForCheck(Patience);
				if (failure != null)
				{
					Log("update check not made: " + failure);
					return null;
				}
				var newer = new List<Found>();
				foreach (PublishedFileDetails one in await Session.Versions(ids))
				{
					// As in the window: an item gone from the Workshop, or another game's, has nothing newer to offer.
					if (one.result != 1 || (one.consumer_appid != 0 && one.consumer_appid != Session.App) || !installed.TryGetValue(one.publishedfileid, out Downloads.Stamp stamp) || stamp == null || Downloads.UpToDate(stamp, one.hcontent_file, one.time_updated))
					{
						continue;
					}
					newer.Add(new Found { Id = one.publishedfileid, Manifest = stamp.Manifest, Updated = stamp.Updated, Title = string.IsNullOrEmpty(one.title) ? one.publishedfileid.ToString() : one.title });
				}
				Log("update check: " + ids.Count + " Workshop mods compared, " + newer.Count + " with an update" + (newer.Count > 0 ? " (" + string.Join(", ", newer.Select(f => f.Id)) + ")" : ""));
				return newer;
			}
			catch (Exception e)
			{
				Log("update check failed: " + e.Message);
				return null;
			}
			finally
			{
				Session.EndCheck();
			}
		}

		/// <summary>
		/// What the notice says now: of the last check's findings, the mods still installed as they were then. A download
		/// in the window or an import writes another .workshop (or none), and a mod deleted is gone.
		/// </summary>
		public static void Refresh(string mods)
		{
			try
			{
				Dictionary<ulong, Downloads.Stamp> installed = Downloads.Installed(mods);
				string[] names;
				lock (gate)
				{
					Load();
					shown = found.Where(f => installed.TryGetValue(f.Id, out Downloads.Stamp stamp) && stamp != null && stamp.Manifest == f.Manifest && stamp.Updated == f.Updated).ToList();
					names = shown.Select(f => f.Title).ToArray();
				}
				Tell(names);
			}
			catch (Exception e)
			{
				Log("update notice not refreshed: " + e.Message);
			}
		}

		// Apart, so that with a PortMobile.dll from before the notice (put on the phone by itself) only this call fails.
		[MethodImpl(MethodImplOptions.NoInlining)]
		private static void Tell(string[] names)
		{
			PortMobile.UpdateNotice.Show(names);
		}

		/// <summary>The last check's day and findings, from the file, once: its day (yyyy-MM-dd), then a line a mod (id, manifest, time, name).</summary>
		private static void Load()
		{
			if (found != null)
			{
				return;
			}
			found = new List<Found>();
			try
			{
				if (!File.Exists(StateFile))
				{
					return;
				}
				string[] lines = File.ReadAllLines(StateFile);
				checkedOn = lines.Length > 0 ? lines[0].Trim() : null;
				foreach (string line in lines.Skip(1))
				{
					string[] parts = line.Split(new[] { '\t' }, 4);
					if (parts.Length == 4 && ulong.TryParse(parts[0], out ulong id) && ulong.TryParse(parts[1], out ulong manifest) && uint.TryParse(parts[2], out uint updated))
					{
						found.Add(new Found { Id = id, Manifest = manifest, Updated = updated, Title = parts[3] });
					}
				}
			}
			catch (Exception e)
			{
				Log("the last update check could not be read: " + e.Message);
			}
		}

		private static void Save()
		{
			try
			{
				IEnumerable<string> mods = found.Select(f => f.Id + "\t" + f.Manifest + "\t" + f.Updated + "\t" + f.Title.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
				File.WriteAllLines(StateFile, new[] { checkedOn ?? "" }.Concat(mods));
			}
			catch (Exception e)
			{
				Log("the update check could not be kept: " + e.Message);
			}
		}
	}
}
