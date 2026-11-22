using System;
using RimWorld;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The player's game is not the version this build is made for (BuiltFor.Version, the staged game's, written by
	/// mobile/build.sh): at the main menu they are told which one is and how to change to it (the user, 2026-10-22, after
	/// testers on 1.7.4976 and 1.7.4981 met mods made for newer 1.7 builds failing). A game older than the oldest build
	/// this one still runs on is told so more firmly. Once per run.
	/// </summary>
	internal static class VersionNotice
	{
		// The oldest game build the current port passes host/compat-check.sh on.
		private const int OldestWorking = 4982;

		private static bool told;

		/// <summary>From Entry.Start, the first time the main menu comes up.</summary>
		public static void Tell()
		{
			if (told)
			{
				return;
			}
			told = true;
			string recommended = BuiltFor.Version;
			string[] parts = recommended.Split('.');
			if (parts.Length < 3 || !int.TryParse(parts[2], out int recommendedBuild))
			{
				return;
			}
			int build = VersionControl.CurrentBuild;
			if (build == recommendedBuild)
			{
				return;
			}
			string current = VersionControl.CurrentVersionString;
			string title, text;
			if (build < OldestWorking)
			{
				title = Texts.Chinese ? "游戏版本太旧" : "Old game version";
				text = Texts.Chinese
					? "你的游戏是 " + current + "，这个移植版是按 RimWorld " + recommended + " 做的。\n\n在这么旧的版本上，很多为新版 1.7 做的 mod 会出错，移植版的一些功能也可能不正常。\n\n请在电脑上把 RimWorld 更新到最新，再长按桌面上的 Port17 图标，选“重新导入游戏”，选择更新后的游戏文件。"
					: "Your game is " + current + "; this port is made for RimWorld " + recommended + ".\n\nOn a version this old many mods made for newer 1.7 builds fail, and parts of the port may not work.\n\nUpdate RimWorld on the PC, then long-press the Port17 icon, choose \"重新导入游戏\" (import the game again) and pick the updated game files.";
			}
			else if (build < recommendedBuild)
			{
				title = Texts.Chinese ? "游戏版本提醒" : "Game version";
				text = Texts.Chinese
					? "你的游戏是 " + current + "，这个移植版是按 RimWorld " + recommended + " 做的。\n\n可以正常用，但建议换成 " + recommended + "：在电脑上把 RimWorld 更新到最新，再长按桌面上的 Port17 图标，选“重新导入游戏”。"
					: "Your game is " + current + "; this port is made for RimWorld " + recommended + ".\n\nIt works, but " + recommended + " is recommended: update RimWorld on the PC, then long-press the Port17 icon and choose \"重新导入游戏\" (import the game again).";
			}
			else
			{
				title = Texts.Chinese ? "游戏版本提醒" : "Game version";
				text = Texts.Chinese
					? "你的游戏是 " + current + "，比这个移植版所用的 RimWorld " + recommended + " 新。\n\n一般可以用，但可能有地方不正常。反馈问题时请写上游戏版本，并留意新的移植版。"
					: "Your game is " + current + ", newer than the RimWorld " + recommended + " this port is made for.\n\nIt mostly works, but some things may not. Mention the game version when you report a problem, and look out for a new port.";
			}
			Console.WriteLine("[mobile] version notice: the game is " + current + ", the port is made for " + recommended);
			LongEventHandler.ExecuteWhenFinished(delegate
			{
				Find.WindowStack.Add(new Dialog_MessageBox(text, title: title));
			});
		}
	}
}
