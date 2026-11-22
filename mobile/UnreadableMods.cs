using System;
using System.IO;
using System.Linq;
using System.Text;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Mod folders the game may not read are left out of the list of mods (PortHooks.ModFolders; a tester, 2026-10-22,
	/// stayed on a black screen before that). At the main menu the player is told which, why, and what to do instead.
	/// Each folder is told once per run.
	/// </summary>
	internal static class UnreadableMods
	{
		private static int told;

		/// <summary>From Entry.Start, whenever the main menu comes up.</summary>
		public static void Tell()
		{
			string[] leftOut = PortHooks.ModFolders.LeftOut;
			if (leftOut.Length <= told)
			{
				return;
			}
			var list = new StringBuilder();
			foreach (string note in leftOut.Skip(told))
			{
				string[] parts = note.Split('|');
				string what = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
				list.Append("  ").Append(parts[0]).Append(what == null ? "" : Texts.Chinese ? "（读不了 " + what + "）" : " (cannot read " + what + ")").Append('\n');
			}
			told = leftOut.Length;
			string folder = ModsFolder();
			string text = Texts.Chinese
				? "游戏没有权限读取下面这些 mod 文件夹，已经跳过，不会加载：\n\n" + list + "\n位置：" + folder + "\n\n这通常是用文件管理器把 mod 直接复制进了这个文件夹：这样放进去的文件属于文件管理器，游戏读不了。\n\n请用同一个文件管理器删掉这些文件夹，再到 Mod 页面点“导入Mod…”重新导入（从压缩包、从文件夹，或者从 Steam 创意工坊下载）。"
				: "The game may not read these mod folders, so they were left out and do not load:\n\n" + list + "\nIn: " + folder + "\n\nThis usually means the mods were copied into that folder with a file manager: files put there that way belong to the file manager, and the game cannot read them.\n\nDelete these folders with the same file manager, then add the mods again with \"Import mods...\" on the mod page (from a zip, from a folder, or from the Steam Workshop).";
			Console.WriteLine("[mobile] unreadable mod folders told to the player: " + string.Join(", ", leftOut));
			LongEventHandler.ExecuteWhenFinished(delegate
			{
				Find.WindowStack.Add(new Dialog_MessageBox(text));
			});
		}

		/// <summary>The game's Mods folder as a file manager shows it (below the phone's shared storage).</summary>
		private static string ModsFolder()
		{
			string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Mods"));
			const string Shared = "/storage/emulated/0/";
			return path.StartsWith(Shared, StringComparison.Ordinal) ? path.Substring(Shared.Length) : path;
		}
	}
}
