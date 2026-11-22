using System;
using System.Linq;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// "N 个 mod 有更新" at the main menu, with a button that opens the Steam Workshop window with those mods ticked. On the
	/// PC Steam updates the subscribed mods by itself; here only the window's "检查已装 mod 的更新" told, when the player
	/// thought of it. The user chose this notice, 2026-10-30, over updating by itself as Steam does, which would replace
	/// mod files in the middle of a game and use mobile data.
	///
	/// The check is PortWorkshop's (Updates, loaded by WorkshopLoader in a load context of its own): with a remembered
	/// Steam sign-in, at most once a day, on a worker thread once the main menu is up. The menu does not wait for it, nor
	/// does a game loaded meanwhile; none is started in a game, and what one finds then waits for the main menu. It signs
	/// in with the remembered token or not at all, and reads only the mods' details. The notice is under the language
	/// button, in the pane's column of links, where nothing else is (the pane is kept clear of a camera hole before this,
	/// Cutout); the game draws no main menu while a window covers it, nor the notice then. Nothing is downloaded until the
	/// player chooses to in the window.
	///
	/// Options, interface page: "每天检查 mod 更新", on by default (mobile.txt workshopUpdates=0 switches it off).
	/// </summary>
	public static class UpdateNotice
	{
		// Under the game's language button (10 below the links, 50 high); the button as high as the menu's own.
		private const float BelowLinks = 10f + 50f + 12f, ButtonHeight = 45f;

		private static bool on = Settings.Get("workshopUpdates", true);
		// Patched, and the main menu's column of links found: only then is there anywhere to tell.
		private static bool ready;
		// Whether this visit of the main menu has yet to start its check (Entry.Start says when a visit begins), and the
		// frame since which the menu has been up without long events (-1: it is not).
		private static bool due;
		private static int upSince = -1;
		// What PortWorkshop last told, from any thread: the names of the installed Workshop mods with a newer version.
		private static volatile string[] names = new string[0];
		private static string[] tipFor;
		private static bool tipChinese;
		private static string tip;
		private static bool failureLogged;
		// MainMenuDrawer's column of links (its place in the pane, how far down its links go) and the shade under them.
		private static AccessTools.FieldRef<Rect> linksRect;
		private static AccessTools.FieldRef<float> linksHeight;
		private static AccessTools.FieldRef<Texture2D> shade;
		private static AccessTools.FieldRef<Color> shadeColor;

		public static void Start(Harmony harmony)
		{
			try
			{
				linksRect = AccessTools.StaticFieldRefAccess<Rect>(AccessTools.Field(typeof(MainMenuDrawer), "webRect"));
				linksHeight = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(MainMenuDrawer), "webBackgroundYMax"));
				shade = AccessTools.StaticFieldRefAccess<Texture2D>(AccessTools.Field(typeof(MainMenuDrawer), "WebBGAtlas"));
				shadeColor = AccessTools.StaticFieldRefAccess<Color>(AccessTools.Field(typeof(MainMenuDrawer), "WebBGColor"));
				harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls)), postfix: new HarmonyMethod(typeof(UpdateNotice), nameof(AfterMainMenuControls)));
				ready = true;
				Console.WriteLine("[mobile] update notice: ready" + (on ? "" : " (the check is switched off in the options)"));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] update notice: not patched, so no update check: " + e.Message);
			}
		}

		/// <summary>From Entry.Start, whenever the main menu comes up (at the start, and back from a game).</summary>
		internal static void AtMainMenu()
		{
			due = true;
		}

		/// <summary>
		/// From the end of every OnGUI: once this visit's main menu is up (the start's or a game's long events over) and
		/// still up a frame later, its check goes to a worker thread, where WorkshopLoader loads the Workshop only if a
		/// sign-in is remembered. The menu's first frame can be its last: with -quicktest the entry UI's Init asks for the
		/// play scene, which comes at the start of the next frame, and a check started in that frame ran through the quick
		/// test's map generation.
		/// </summary>
		internal static void OnFrame()
		{
			if (!due || Current.ProgramState != ProgramState.Entry || LongEventHandler.AnyEventNowOrWaiting)
			{
				upSince = -1;
				return;
			}
			int frame = Time.frameCount;
			if (upSince < 0)
			{
				upSince = frame;
			}
			if (frame == upSince)
			{
				return;
			}
			due = false;
			upSince = -1;
			if (!on || !ready)
			{
				return;
			}
			string mods = GenFilePaths.ModsFolderPath;
			ThreadPool.QueueUserWorkItem(_ => WorkshopLoader.CheckForUpdates(mods));
		}

		/// <summary>From PortWorkshop's check (Updates), on any thread: the installed Workshop mods with a newer version, by name.</summary>
		public static void Show(string[] mods)
		{
			names = mods ?? new string[0];
		}

		/// <summary>
		/// From the mod page (ModImport), after it deleted or imported mods: the notice no longer names the ones gone or
		/// installed anew, at once rather than at the next visit of the main menu. The folders are read on a worker thread,
		/// and only while the notice names something (it names nothing before the Workshop is loaded).
		/// </summary>
		internal static void ModsChanged()
		{
			if (names.Length == 0)
			{
				return;
			}
			string mods = GenFilePaths.ModsFolderPath;
			ThreadPool.QueueUserWorkItem(_ => WorkshopLoader.RefreshUpdates(mods));
		}

		/// <summary>
		/// Where the notice goes, in the pane the main menu is drawn in: in the column of links (its place in the pane, and
		/// how far down its links go), under the language button; the line of text as high as it needs, the button under it.
		/// </summary>
		internal static void Layout(Rect pane, Rect links, float linksBottom, float textHeight, out Rect text, out Rect button)
		{
			float x = pane.x + links.x, y = pane.y + links.y + linksBottom + BelowLinks;
			text = new Rect(x, y, links.width, textHeight);
			button = new Rect(x, text.yMax + 4f, links.width, ButtonHeight);
		}

		/// <summary>Postfix of MainMenuDrawer.DoMainMenuControls, which draws the main menu's pane at __0 (and the game's menu tab in a game).</summary>
		public static void AfterMainMenuControls(Rect __0)
		{
			string[] mods = names;
			if (!on || mods.Length == 0 || Current.ProgramState != ProgramState.Entry)
			{
				return;
			}
			try
			{
				Rect links = linksRect();
				if (links.width <= 0f)
				{
					// A mod's prefix drew the pane its own way and the game's never laid its column of links out.
					return;
				}
				Text.Font = GameFont.Small;
				string label = Texts.Chinese ? mods.Length + " 个 mod 有更新" : mods.Length == 1 ? "1 mod has an update" : mods.Length + " mods have updates";
				Layout(__0, links, linksHeight(), Text.CalcHeight(label, links.width), out Rect text, out Rect button);
				// The links' shade: the line reads over the picture as they do.
				GUI.color = shadeColor();
				Widgets.DrawAtlas(text.ExpandedBy(4f), shade());
				// The Workshop window's colour for "有更新".
				GUI.color = new Color(1f, 0.85f, 0.4f);
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(text, label);
				Text.Anchor = TextAnchor.UpperLeft;
				GUI.color = Color.white;
				if (Widgets.ButtonText(button, Texts.Chinese ? "更新" : "Update"))
				{
					WorkshopLoader.OpenUpdates();
				}
				TooltipHandler.TipRegion(new Rect(text.x, text.y, text.width, button.yMax - text.y), Tip(mods));
			}
			catch (Exception e)
			{
				GUI.color = Color.white;
				Text.Anchor = TextAnchor.UpperLeft;
				if (!failureLogged)
				{
					failureLogged = true;
					Console.WriteLine("[mobile] update notice: not drawn: " + e);
				}
			}
		}

		private static string Tip(string[] mods)
		{
			if (tipFor != mods || tipChinese != Texts.Chinese)
			{
				tipFor = mods;
				tipChinese = Texts.Chinese;
				string list = string.Join("\n", mods.Take(30).Select(m => "  " + m)) + (mods.Length > 30 ? "\n  ……" : "");
				tip = tipChinese
					? "创意工坊里有新版本：\n" + list + "\n\n点“更新”打开创意工坊窗口，这些 mod 已经勾好；在窗口里点“下载 / 更新选中的”才会下载。"
					: "A newer version is on the Workshop:\n" + list + "\n\n\"Update\" opens the Workshop window with these ticked; nothing is downloaded until you press \"Download / update chosen\" there.";
			}
			return tip;
		}

		internal static void OptionRow(Listing_Standard listing)
		{
			bool check = on;
			listing.CheckboxLabeled(Texts.Chinese ? "每天检查 mod 更新" : "Check for mod updates daily", ref check, Texts.Chinese
				? "在创意工坊窗口里登录过 Steam、记住了登录时，每天第一次来到主菜单时在后台查一次：用创意工坊窗口下载的 mod 在工坊里有没有新版本。有的话，主菜单右边语言按钮下面显示“N 个 mod 有更新”；点“更新”打开创意工坊窗口并勾好这些 mod，在窗口里点“下载 / 更新选中的”才会下载。\n\n只读取 mod 的信息，流量很小；只在主菜单上开始检查，不会拖慢主菜单和读档；没联网或 Steam 登录过期时什么都不提示。用 zip 或文件夹导入的 mod 不在检查之内。"
				: "With a Steam sign-in remembered from the Workshop window, once a day, the first time the main menu comes up, the mods downloaded through the Workshop window are checked in the background for a newer version on the Workshop. When there is one, the main menu says \"N mods have updates\" under the language button; \"Update\" opens the Workshop window with them ticked, and nothing is downloaded until you press \"Download / update chosen\" there.\n\nOnly the mods' details are read (little data); the check starts only at the main menu and holds up neither the menu nor a load; without a connection or with an expired sign-in nothing is shown. Mods imported from zips or folders are not checked.", 30f, 0.6f);
			if (check != on)
			{
				on = check;
				Settings.Set("workshopUpdates", on ? "1" : "0");
				// Switched on: the check is made at this main menu (if today's has not been), not at the next one.
				due = on;
			}
		}
	}
}
