using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Application.OpenURL on the phone. The game's Linux path starts xdg-open, which Android has not, so nothing opened:
	/// the main menu's links (wiki, Reddit, Ludeon's blog...), the mod page's Workshop and author links, mods' "open the
	/// page" buttons (2026-10-27, from comparing with the PC, whose Windows path opens the browser). A web address (or any
	/// other link with a scheme: mailto:) goes to the phone's app for it (MainActivity.openUrlUtf8). A folder or a file in
	/// the game's data folder or its Mods opens in the system's Files app, as Explorer opens it on the PC (InFilesApp);
	/// any other folder of the game's, which no other app may open on Android, is named in a message instead. Steam's
	/// links and the pages of Workshop items go their own way (SteamLink).
	/// </summary>
	public static class OpenUrl
	{
		[DllImport("glfw")] private static extern int glfwAndroidOpenUrl(byte[] url);
		[DllImport("glfw")] private static extern int glfwAndroidOpenFolder(byte[] path);

		// steam://openurl/<address> has Steam's browser open the address; openurl_external, the system's browser.
		private static readonly Regex SteamOpenUrl = new Regex("^steam://openurl(?:_external)?/(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
		// A Workshop item: SteamUtility.SteamWorkshopPageUrl's link (what most mods' About.xml give as steamWorkshopUrl),
		// and the item's web page, under either of its two paths ("?id=" may come after other parameters).
		private static readonly Regex SteamItem = new Regex("^steam://url/CommunityFilePage/(\\d{6,20})/?$", RegexOptions.IgnoreCase);
		private static readonly Regex ItemPage = new Regex("^https?://(?:www\\.)?steamcommunity\\.com/(?:sharedfiles|workshop)/filedetails/?\\?(?:[^#]*&)?id=(\\d{6,20})(?:[&#].*)?$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(UnityEngine.Application), nameof(UnityEngine.Application.OpenURL), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(OpenUrl), nameof(BeforeOpenURL)));
				Console.WriteLine("[mobile] links open in the phone's apps (Application.OpenURL)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] links: Application.OpenURL left as it is: " + e.Message);
			}
			try
			{
				harmony.Patch(AccessTools.Method(typeof(System.Diagnostics.Process), nameof(System.Diagnostics.Process.Start), Type.EmptyTypes), prefix: new HarmonyMethod(typeof(OpenUrl), nameof(BeforeProcessStart)));
				Console.WriteLine("[mobile] links: what a mod hands to the shell opens as with Application.OpenURL (Process.Start)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] links: Process.Start left as it is: " + e.Message);
			}
		}

		/// <summary>
		/// Prefix of Process.Start(), which every Process.Start overload ends in: a link, folder or file a mod hands to the
		/// shell (UseShellExecute; RimTalk's "open folder" of its prompt presets, 2026-10-29) is opened on Windows, in the
		/// browser, Explorer or the file's app. Android has no shell for it, nor .NET's Linux way (xdg-open): it goes the way
		/// of Application.OpenURL here. Programs (UseShellExecute false: MonoMod asks uname) start as they do; a mod's
		/// Process.Start(folder) without the shell fails on the PC's .NET as well, and is left to fail.
		/// </summary>
		public static bool BeforeProcessStart(System.Diagnostics.Process __instance, ref bool __result)
		{
			System.Diagnostics.ProcessStartInfo info;
			try
			{
				info = __instance.StartInfo;
			}
			catch (Exception)
			{
				return true;
			}
			if (info == null || !info.UseShellExecute || string.IsNullOrWhiteSpace(info.FileName))
			{
				return true;
			}
			string target = info.FileName.Trim().Trim('"');
			int colon = target.IndexOf(':');
			bool link = colon > 1 && target.Substring(0, colon).IndexOfAny(new[] { '/', '\\', ' ' }) < 0;
			if (!link && !System.IO.Directory.Exists(target) && !System.IO.File.Exists(target))
			{
				return true; // a program by its name: the runtime says why it does not start
			}
			Console.WriteLine("[mobile] links: " + target + " handed to the shell (Process.Start)" + (UnityData.IsInMainThread ? "" : ", off the main thread: not shown"));
			if (UnityData.IsInMainThread)
			{
				BeforeOpenURL(target);
			}
			__result = false;
			return false;
		}

		public static bool BeforeOpenURL(string url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return false;
			}
			string link = url.Trim();
			if (SteamLink(link))
			{
				return false;
			}
			int colon = link.IndexOf(':');
			bool web = colon > 1 && link.Substring(0, colon).IndexOfAny(new[] { '/', '\\', ' ' }) < 0 && !link.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
			if (web)
			{
				InPhoneApp(link);
				return false;
			}
			string path = link.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(link.Substring(5).TrimStart('/')) : link;
			if (!path.StartsWith("/", StringComparison.Ordinal) && link.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
			{
				path = "/" + path;
			}
			if (InFilesApp(path))
			{
				Console.WriteLine("[mobile] link to a folder or file, opened in the Files app: " + path);
				return false;
			}
			Console.WriteLine("[mobile] link to a folder or file, named instead: " + path);
			Messages.Message(Texts.Chinese ? "手机上不能直接打开游戏的文件夹，它在：" + path : "The game's folders cannot be opened on the phone; this one is " + path, MessageTypeDefOf.NeutralEvent, false);
			return false;
		}

		/// <summary>
		/// A folder in the game's data folder or its Mods (for a file, the folder it is in) shown in the system's Files app:
		/// the APK's files provider has those two as places there (host/java FilesProvider, MainActivity.openFolderUtf8,
		/// 2026-10-30). False for any other folder, when the phone has no Files app for it, and with an APK from before the
		/// provider, which has no glfwAndroidOpenFolder.
		/// </summary>
		private static bool InFilesApp(string path)
		{
			try
			{
				return glfwAndroidOpenFolder(System.Text.Encoding.UTF8.GetBytes(path + "\0")) != 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] links: the Files app not asked: " + e.Message);
				return false;
			}
		}

		/// <summary>
		/// A link with a scheme, straight to the phone's app for it (the browser, for a web page); also the Workshop window's
		/// way to an item's page, which BeforeOpenURL would send back to that window.
		/// </summary>
		public static void InPhoneApp(string link)
		{
			bool asked = false;
			try
			{
				asked = glfwAndroidOpenUrl(System.Text.Encoding.UTF8.GetBytes(link + "\0")) != 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] links: " + e.Message);
			}
			Console.WriteLine("[mobile] link " + (asked ? "opened: " : "not opened (no app for it): ") + link);
			if (!asked)
			{
				Messages.Message(Texts.Chinese ? "手机上没有能打开这个链接的应用：" + link : "No app on the phone opens this link: " + link, MessageTypeDefOf.RejectInput, false);
			}
		}

		/// <summary>
		/// Steam's links and the pages of Workshop items; false for any other link. On the PC the Steam client opens a
		/// steam:// link, and a required mod that is missing on the mod page (ModDependency.OnClicked, through
		/// SteamUtility.OpenUrl, mostly with a steam:// link) or a mod's own Workshop button shows the item in Steam, where
		/// Subscribe gets it. The phone has no Steam client, and subscribing in the browser brings nothing onto the phone
		/// (2026-10-30, from comparing with the PC): an item opens the port's Workshop window with the item in its list,
		/// ready to download once signed in, and its page a button away in the browser. steam://openurl/ opens its address
		/// as any link. Any other steam:// link is said to be the Steam client's: handed to Android, it only got "no app
		/// opens this link".
		/// </summary>
		private static bool SteamLink(string link)
		{
			Match match = SteamOpenUrl.Match(link);
			if (match.Success)
			{
				BeforeOpenURL(match.Groups[1].Value);
				return true;
			}
			string item, page;
			if ((match = SteamItem.Match(link)).Success)
			{
				item = match.Groups[1].Value;
				page = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + item;
			}
			else if ((match = ItemPage.Match(link)).Success)
			{
				item = match.Groups[1].Value;
				page = link;
			}
			else if (link.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine("[mobile] link for the Steam client, not opened: " + link);
				Messages.Message(Texts.Chinese ? "这个链接要用电脑上的 Steam 客户端打开，手机上没有 Steam 客户端：" + link : "This link opens in the Steam client on a PC; the phone has no Steam client: " + link, MessageTypeDefOf.RejectInput, false);
				return true;
			}
			else
			{
				return false;
			}
			// Windows belong to the game's thread: a link opened on another, or with no Workshop to open, goes to the browser.
			if (UnityData.IsInMainThread && WorkshopLoader.OpenItem(item, page))
			{
				Console.WriteLine("[mobile] link to Workshop item " + item + ", the Workshop window opened: " + link);
			}
			else
			{
				InPhoneApp(page);
			}
			return true;
		}
	}
}
