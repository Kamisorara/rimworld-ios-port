using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using QRCoder;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PortWorkshop
{
	/// <summary>
	/// Opened from the mod page (PortMobile.WorkshopLoader, through the import menu), for a Workshop link, or by the main
	/// menu's update notice; and that notice's check.
	/// </summary>
	public static class Entry
	{
		public static void Open()
		{
			if (Find.WindowStack.WindowOfType<WorkshopWindow>() == null)
			{
				Find.WindowStack.Add(new WorkshopWindow());
			}
		}

		/// <summary>
		/// A Workshop link the game or a mod opened (PortMobile.OpenUrl): a required mod that is missing on the mod page, a
		/// mod's Workshop button. The item is added as if typed in, once signed in; its page stays a button away.
		/// </summary>
		public static void OpenItem(string item, string page)
		{
			WorkshopWindow window = Find.WindowStack.WindowOfType<WorkshopWindow>();
			if (window == null)
			{
				window = new WorkshopWindow();
				Find.WindowStack.Add(window);
			}
			window.Follow(item, page);
		}

		/// <summary>
		/// On a worker thread, when the main menu comes up with a sign-in remembered (PortMobile.UpdateNotice, through
		/// WorkshopLoader): today's update check if it is due; the notice is told what has a newer version.
		/// </summary>
		public static void CheckForUpdates(string mods)
		{
			_ = Updates.Check(mods);
		}

		/// <summary>
		/// On a worker thread, after the mod page deleted or imported mods (PortMobile.UpdateNotice, through WorkshopLoader):
		/// the notice is told again which mods are still installed as the last check found them.
		/// </summary>
		public static void RefreshUpdates(string mods)
		{
			Updates.Refresh(mods);
		}

		/// <summary>The update notice's button: the mods it names go into the list, the ones with a newer version ticked.</summary>
		public static void OpenUpdates()
		{
			WorkshopWindow window = Find.WindowStack.WindowOfType<WorkshopWindow>();
			if (window == null)
			{
				window = new WorkshopWindow();
				Find.WindowStack.Add(window);
			}
			window.FollowUpdates(Updates.Shown);
		}
	}

	/// <summary>
	/// The Steam Workshop on the phone. Signed out: a QR code for the Steam app on another device, or the account name and
	/// password (typed by the player, used once, not kept), then Steam Guard. Signed in: items are added by link or id
	/// (a collection adds its items; an item adds the items it requires), from the account's subscriptions, from the
	/// Workshop mods already installed (which shows the ones with a newer version), by a Workshop link the game or a mod
	/// opened, or by the main menu's update notice (its mods, those with a newer version ticked); the chosen ones are
	/// downloaded and installed as the import installs a mod.
	/// </summary>
	internal sealed class WorkshopWindow : Window
	{
		private string account = "", password = "", code = "", links = "";
		private Vector2 scroll;
		private string qrFor;
		private List<BitArray> qr;
		// What a link asked for (Entry.OpenItem): the items to add once signed in, the item to scroll the list to once it is
		// in it, and the page to offer for the browser.
		private string pending = "", page;
		private ulong scrollTo;
		// What the main menu's update notice asked for (Entry.OpenUpdates): the mods to look up once signed in.
		private List<ulong> pendingUpdates;

		public override Vector2 InitialSize => new Vector2(Mathf.Min(1100f, UI.screenWidth - 20f), Mathf.Min(680f, UI.screenHeight - 8f));

		public WorkshopWindow()
		{
			doCloseX = true;
			closeOnClickedOutside = false;
			absorbInputAroundWindow = true;
			forcePause = true;
			Downloads.ModsFolder = GenFilePaths.ModsFolderPath;
			Downloads.GameVersion = VersionControl.CurrentVersion;
			Session.Start();
		}

		public override void PostClose()
		{
			base.PostClose();
			if (!Downloads.Busy)
			{
				Session.Stop();
			}
		}

		public void Follow(string item, string url)
		{
			if (!string.IsNullOrEmpty(item))
			{
				pending += " " + item;
				scrollTo = Downloads.ParseIds(item).FirstOrDefault();
			}
			page = url;
		}

		/// <summary>The update notice's mods: put into the list once signed in, the list scrolled to the first of them.</summary>
		public void FollowUpdates(List<ulong> ids)
		{
			if (ids.Count > 0)
			{
				pendingUpdates = ids;
				scrollTo = ids[0];
			}
		}

		public override void DoWindowContents(Rect inRect)
		{
			if (Downloads.ReportReady)
			{
				Downloads.Report();
			}
			// No closing while files are being written into Mods.
			doCloseX = !Downloads.Busy;
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(inRect.x, inRect.y, 400f, 36f), Texts.Say("Steam 创意工坊", "Steam Workshop"));
			Text.Font = GameFont.Small;
			// Clear of the close button in the corner.
			if (page != null && Widgets.ButtonText(new Rect(inRect.xMax - 270f, inRect.y, 240f, 36f), Texts.Say("在浏览器里打开页面", "Open the page in the browser")))
			{
				OpenPage();
			}
			Rect body = new Rect(inRect.x, inRect.y + 44f, inRect.width, inRect.height - 44f);
			SessionState state = Session.State;
			if (state == SessionState.Online)
			{
				DoOnline(body);
			}
			else if (state == SessionState.Off || state == SessionState.Connecting || state == SessionState.LoggingOn)
			{
				Text.Anchor = TextAnchor.MiddleCenter;
				string line = state == SessionState.LoggingOn ? Texts.Say("正在登录 " + Session.Account + "……", "Signing in as " + Session.Account + "...") : Texts.Say("正在连接 Steam……\n（第一次连接要先向 Steam 要服务器列表，国内网络下可能要一分钟左右）", "Connecting to Steam...\n(the first time, the list of Steam servers is fetched first, which can take a minute)");
				Widgets.Label(body, line + (Session.Message.Length > 0 ? "\n\n" + Session.Message : ""));
				Text.Anchor = TextAnchor.UpperLeft;
			}
			else
			{
				DoSignIn(body, state);
			}
		}

		// ---- signing in ----

		private void DoSignIn(Rect rect, SessionState state)
		{
			float half = Mathf.Floor(rect.width / 2f) - 10f;
			Rect left = new Rect(rect.x, rect.y, half, rect.height);
			Rect right = new Rect(rect.xMax - half, rect.y, half, rect.height);
			// The QR code.
			Widgets.Label(new Rect(left.x, left.y, left.width, 50f), Texts.Say("用另一台设备上的 Steam App 扫码登录（Steam 令牌 → 扫描二维码）：", "Scan with the Steam app on another device (Steam Guard → scan a QR code):"));
			string url = Session.QrUrl;
			float side = Mathf.Min(left.width, left.height - 120f);
			Rect qrRect = new Rect(left.x + (left.width - side) / 2f, left.y + 56f, side, side);
			if (!string.IsNullOrEmpty(url) && state == SessionState.SignedOut)
			{
				DrawQr(qrRect, url);
			}
			else
			{
				Widgets.DrawBoxSolid(qrRect, new Color(0.15f, 0.15f, 0.15f));
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(qrRect, state == SessionState.SignedOut ? Texts.Say("二维码准备中……", "Preparing the QR code...") : "");
				Text.Anchor = TextAnchor.UpperLeft;
			}
			// The password form, or what Steam Guard wants.
			float y = right.y;
			if (state == SessionState.NeedCode)
			{
				Widgets.Label(new Rect(right.x, y, right.width, 60f), Session.CodePrompt);
				y += 64f;
				code = Widgets.TextField(new Rect(right.x, y, right.width, 40f), code);
				y += 50f;
				if (Widgets.ButtonText(new Rect(right.x, y, 160f, 44f), Texts.Say("确定", "OK")) && code.Trim().Length > 0)
				{
					Session.SubmitCode(code);
					code = "";
				}
				if (Widgets.ButtonText(new Rect(right.x + 180f, y, 160f, 44f), Texts.Say("取消", "Cancel")))
				{
					Session.Restart();
				}
				y += 60f;
			}
			else if (state == SessionState.WaitingApproval || state == SessionState.Authenticating)
			{
				Widgets.Label(new Rect(right.x, y, right.width, 120f), state == SessionState.WaitingApproval
					? Texts.Say("请在手机上的 Steam App 里同意这次登录（App 会弹出提示；也可以在 Steam 令牌页里确认）。同意后这里会自己继续。", "Approve this sign-in in the Steam app (it shows a prompt; or confirm it on the Steam Guard page). This continues by itself once approved.")
					: Texts.Say("正在验证……", "Checking..."));
				y += 124f;
				if (Widgets.ButtonText(new Rect(right.x, y, 160f, 44f), Texts.Say("取消", "Cancel")))
				{
					Session.Restart();
				}
				y += 60f;
			}
			else
			{
				Widgets.Label(new Rect(right.x, y, right.width, 50f), Texts.Say("或者用账号密码登录（只用于这一次登录，不会保存；之后的登录用 Steam 发给这台手机的凭据）：", "Or sign in with the account name and password (used for this sign-in only and not kept; later sessions use the token Steam gives this phone):"));
				y += 54f;
				Widgets.Label(new Rect(right.x, y, 100f, 40f), Texts.Say("账号", "Account"));
				account = Widgets.TextField(new Rect(right.x + 100f, y, right.width - 100f, 40f), account);
				y += 48f;
				Widgets.Label(new Rect(right.x, y, 100f, 40f), Texts.Say("密码", "Password"));
				password = GUI.PasswordField(new Rect(right.x + 100f, y, right.width - 100f, 40f), password, '*');
				y += 52f;
				if (Widgets.ButtonText(new Rect(right.x + 100f, y, 200f, 44f), Texts.Say("登录", "Sign in")) && account.Trim().Length > 0 && password.Length > 0)
				{
					Session.SignInWithPassword(account, password);
					password = "";
				}
				y += 60f;
			}
			if (Session.Message.Length > 0)
			{
				GUI.color = new Color(1f, 0.6f, 0.5f);
				Widgets.Label(new Rect(right.x, y, right.width, right.yMax - y), Session.Message);
				GUI.color = Color.white;
			}
		}

		/// <summary>The page of the link the window was opened for: the item's description, discussions and changes, as the PC's Steam shows them.</summary>
		private void OpenPage()
		{
			PortMobile.OpenUrl.InPhoneApp(page);
		}

		/// <summary>The QR code, one dark run of modules after another, each a hair larger than its share so that no light line shows between them.</summary>
		private void DrawQr(Rect rect, string url)
		{
			if (qrFor != url)
			{
				qrFor = url;
				using (var generator = new QRCodeGenerator())
				using (QRCodeData data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M))
				{
					qr = data.ModuleMatrix.Select(row => new BitArray(row)).ToList();
				}
			}
			Widgets.DrawBoxSolid(rect, Color.white);
			int count = qr.Count;
			float module = rect.width / count, hair = 0.6f / Mathf.Max(0.5f, GUI.matrix.m00);
			for (int row = 0; row < count; row++)
			{
				BitArray bits = qr[row];
				int column = 0;
				while (column < count)
				{
					if (!bits[column])
					{
						column++;
						continue;
					}
					int start = column;
					while (column < count && bits[column])
					{
						column++;
					}
					Widgets.DrawBoxSolid(new Rect(rect.x + start * module, rect.y + row * module, (column - start) * module + hair, module + hair), Color.black);
				}
			}
		}

		// ---- signed in ----

		private void DoOnline(Rect rect)
		{
			if (Time.frameCount % 120 == 0)
			{
				Downloads.TestManifests();
			}
			if (pending.Length > 0 && !Downloads.Busy)
			{
				Downloads.AddFromText(pending);
				pending = "";
			}
			if (pendingUpdates != null && !Downloads.Busy)
			{
				Downloads.AddUpdates(pendingUpdates);
				pendingUpdates = null;
			}
			bool busy = Downloads.Busy;
			float y = rect.y;
			Widgets.Label(new Rect(rect.x, y, rect.width - 170f, 36f), Texts.Say("已登录：", "Signed in: ") + Session.Account);
			if (!busy && Widgets.ButtonText(new Rect(rect.xMax - 160f, y, 160f, 36f), Texts.Say("退出登录", "Sign out")))
			{
				Session.SignOut();
				return;
			}
			y += 44f;
			links = Widgets.TextField(new Rect(rect.x, y, rect.width - 170f, 40f), links);
			if (links.Length == 0)
			{
				GUI.color = new Color(1f, 1f, 1f, 0.4f);
				Widgets.Label(new Rect(rect.x + 8f, y + 9f, rect.width - 180f, 30f), Texts.Say("粘贴工坊链接或输入 mod ID（可以多个，合集也行）", "Paste Workshop links or type item ids (several, or a collection)"));
				GUI.color = Color.white;
			}
			if (!busy && Widgets.ButtonText(new Rect(rect.xMax - 160f, y, 160f, 40f), Texts.Say("添加", "Add")))
			{
				Downloads.AddFromText(links);
				links = "";
			}
			y += 50f;
			float buttonWidth = 230f;
			if (!busy && Widgets.ButtonText(new Rect(rect.x, y, buttonWidth, 40f), Texts.Say("读取我的订阅", "My subscriptions")))
			{
				Downloads.AddSubscriptions();
			}
			if (!busy && Widgets.ButtonText(new Rect(rect.x + buttonWidth + 10f, y, buttonWidth, 40f), Texts.Say("检查已装 mod 的更新", "Check installed mods")))
			{
				Downloads.AddInstalled();
			}
			List<Item> items = Downloads.Snapshot();
			if (!busy && items.Count > 0 && Widgets.ButtonText(new Rect(rect.x + 2f * (buttonWidth + 10f), y, 160f, 40f), Texts.Say("全选 / 全不选", "All / none")))
			{
				bool any = items.Any(i => !i.Selected && !i.Collection);
				lock (Downloads.Items)
				{
					foreach (Item item in items)
					{
						item.Selected = any && !item.Collection;
					}
				}
			}
			y += 50f;
			// The list.
			float bottom = 60f;
			Rect outRect = new Rect(rect.x, y, rect.width, rect.yMax - y - bottom);
			float rowHeight = 46f;
			Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(outRect.height, items.Count * rowHeight));
			// The list keeps what was added earlier in the run (a collection, the subscriptions) and puts a new item at its end,
			// while a new window starts at its top: the item of a link is scrolled to, once, when it is in the list.
			int linked = scrollTo == 0 ? -1 : items.FindIndex(i => i.Id == scrollTo);
			if (linked >= 0)
			{
				scroll.y = Mathf.Min(linked * rowHeight, viewRect.height - outRect.height);
				scrollTo = 0;
			}
			Widgets.DrawMenuSection(outRect);
			Widgets.BeginScrollView(outRect, ref scroll, viewRect);
			for (int i = 0; i < items.Count; i++)
			{
				DoRow(new Rect(0f, i * rowHeight, viewRect.width, rowHeight), items[i], busy, i);
			}
			if (items.Count == 0)
			{
				Text.Anchor = TextAnchor.MiddleCenter;
				GUI.color = Color.gray;
				Widgets.Label(new Rect(0f, 0f, viewRect.width, outRect.height), Texts.Say("还没有 mod：在上面添加，或者读取订阅、检查已装的。", "Nothing yet: add items above, or read the subscriptions, or check the installed mods."));
				GUI.color = Color.white;
				Text.Anchor = TextAnchor.UpperLeft;
			}
			Widgets.EndScrollView();
			// The bottom line.
			Rect line = new Rect(rect.x, rect.yMax - bottom + 10f, rect.width, bottom - 10f);
			int chosen = items.Count(i => i.Selected && !i.Collection);
			if (!busy && Widgets.ButtonText(new Rect(line.x, line.y, 300f, 44f), Texts.Say($"下载 / 更新选中的（{chosen}）", $"Download / update chosen ({chosen})")) && chosen > 0)
			{
				Downloads.DownloadSelected();
			}
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(new Rect(line.x + 316f, line.y, line.width - 316f, 44f), Downloads.Status.Length > 0 ? Downloads.Status : Texts.Say("下载的 mod 装好后在 Mod 页左边“禁用”一栏，启用后点“保存并应用”。", "Installed mods appear among the disabled mods; enable them and press Save and apply."));
			Text.Anchor = TextAnchor.UpperLeft;
		}

		private void DoRow(Rect row, Item item, bool busy, int index)
		{
			if (index % 2 == 1)
			{
				Widgets.DrawLightHighlight(row);
			}
			Rect inner = row.ContractedBy(4f);
			bool selected = item.Selected;
			if (!item.Collection)
			{
				Widgets.Checkbox(new Vector2(inner.x, inner.y + (inner.height - 30f) / 2f), ref selected, 30f, busy);
				if (selected != item.Selected)
				{
					item.Selected = selected;
				}
			}
			float x = inner.x + 44f;
			float statusWidth = 300f, sizeWidth = 110f;
			Text.Anchor = TextAnchor.MiddleLeft;
			string title = item.Title.Length > 0 ? item.Title : item.Id.ToString();
			if (item.RequiredBy != 0)
			{
				title += Texts.Say("（前置）", " (required)");
			}
			Widgets.Label(new Rect(x, inner.y, inner.width - 44f - statusWidth - sizeWidth, inner.height), title.Truncate(inner.width - 44f - statusWidth - sizeWidth));
			Widgets.Label(new Rect(inner.xMax - statusWidth - sizeWidth, inner.y, sizeWidth, inner.height), item.Size > 0 ? (item.Size / 1048576.0).ToString("F1") + " MB" : "");
			Rect statusRect = new Rect(inner.xMax - statusWidth, inner.y, statusWidth, inner.height);
			if (item.State == ItemState.Downloading)
			{
				Widgets.FillableBar(statusRect.ContractedBy(0f, 8f), Mathf.Clamp01(item.Progress));
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(statusRect, (item.Progress * 100f).ToString("F0") + "%");
			}
			else
			{
				GUI.color = item.State == ItemState.Failed ? new Color(1f, 0.5f, 0.45f) : item.State == ItemState.Outdated ? new Color(1f, 0.85f, 0.4f) : item.State == ItemState.Done || item.State == ItemState.Installed ? new Color(0.6f, 1f, 0.6f) : Color.white;
				string status = StateText(item);
				Widgets.Label(statusRect, status.Truncate(statusWidth));
				TooltipHandler.TipRegion(statusRect, status);
				GUI.color = Color.white;
			}
			Text.Anchor = TextAnchor.UpperLeft;
			TooltipHandler.TipRegion(new Rect(x, inner.y, inner.width - 44f - statusWidth, inner.height), item.Id.ToString());
		}

		private static string StateText(Item item)
		{
			switch (item.State)
			{
				case ItemState.Unknown: return Texts.Say("未安装", "Not installed");
				case ItemState.Installed: return Texts.Say("已是最新", "Up to date");
				case ItemState.Outdated: return Texts.Say("有更新", "Update available");
				case ItemState.Untracked: return Texts.Say("已安装（版本未知）", "Installed (version unknown)");
				case ItemState.Queued: return Texts.Say("等待下载", "Queued");
				case ItemState.Installing: return Texts.Say("安装中……", "Installing...");
				case ItemState.Done: return Texts.Say("已装好", "Installed") + (item.Error.Length > 0 ? "：" + item.Error : "");
				case ItemState.Failed: return Texts.Say("失败：", "Failed: ") + item.Error;
				default: return "";
			}
		}
	}
}
