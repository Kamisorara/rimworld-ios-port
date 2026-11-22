using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Saves brought from a PC or another phone (the user, 2026-10-24: "不如直接给手机加一个存档导入功能"). A tester's vivo
	/// could not read a save copied into the saves folder with a file manager (UnauthorizedAccessException: a file put
	/// there that way belongs to the file manager), and the load dialog then failed altogether, as its rows read every
	/// save's header.
	///
	/// The load dialog gets "导入存档…" at its bottom left: the system's chooser of the mod import (MainActivity.pickFiles)
	/// copies the chosen .rws files, and .zip archives holding saves, into files/ModImport, and every save found there is
	/// put with the saves under its own name (a name already taken gets " (2)", " (3)"...). Saves the game may not read are
	/// left out of the list (GenFilePaths.AllSavedGameFiles), and the player is told once which, and what to do.
	/// "导出存档…" at the bottom right is the way back (ChooseExport).
	/// </summary>
	internal static class SaveImport
	{
		private const int Choosing = 1, Copying = 2, Done = 3, Failed = 4;
		private static readonly HashSet<string> told = new HashSet<string>();
		private static readonly List<string> toTell = new List<string>();

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(Dialog_FileList), nameof(Dialog_FileList.DoWindowContents)), postfix: new HarmonyMethod(typeof(SaveImport), nameof(AfterFileList)));
				harmony.Patch(AccessTools.PropertyGetter(typeof(GenFilePaths), nameof(GenFilePaths.AllSavedGameFiles)), postfix: new HarmonyMethod(typeof(SaveImport), nameof(ReadableOnly)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] save import: not patched: " + e.Message);
			}
		}

		/// <summary>Postfix of Dialog_FileList.DoWindowContents: the import button in the load dialog, and what to tell.</summary>
		public static void AfterFileList(Dialog_FileList __instance, Rect inRect)
		{
			if (toTell.Count > 0)
			{
				TellUnreadable();
			}
			if (!(__instance is Dialog_SaveFileList_Load))
			{
				return;
			}
			// Left of the window's close button, where the list leaves room for it.
			var button = new Rect(inRect.x, inRect.yMax - Window.CloseButSize.y, 170f, Window.CloseButSize.y);
			if (Widgets.ButtonText(button, ModImport.Say("导入存档…", "Import saves...")))
			{
				Pick(__instance);
			}
			TooltipHandler.TipRegion(button, ModImport.Say("从手机里选存档（.rws 文件，或者装着存档的 .zip）放进游戏的存档文件夹。电脑上的存档在 C:\\Users\\你的用户名\\AppData\\LocalLow\\Ludeon Studios\\RimWorld by Ludeon Studios\\Saves。",
				"Choose saves on the phone (.rws files, or .zip archives holding them) to put with the game's saves. A PC keeps them in C:\\Users\\<you>\\AppData\\LocalLow\\Ludeon Studios\\RimWorld by Ludeon Studios\\Saves."));
			// Right of the close button: the way out, for a PC or another phone.
			var export = new Rect(inRect.xMax - 170f, button.y, 170f, button.height);
			if (Widgets.ButtonText(export, ModImport.Say("导出存档…", "Export saves...")))
			{
				ChooseExport();
			}
			TooltipHandler.TipRegion(export, ModImport.Say("把存档复制到手机的 Download/RimWorld 文件夹（文件管理器的“下载”里），可以拷到电脑上，或者在聊天软件里发给别人。",
				"Copy saves into the phone's Download/RimWorld folder (Downloads in a file manager), to take them to a PC or send them to someone."));
		}

		/// <summary>
		/// The saves folder is the app's own (Android/data, which no file manager shows since Android 11), where a PC's is
		/// open in its file manager: a save, or all of them in one .zip (what "导入存档…" takes), is copied into
		/// Download/RimWorld, which an app may create files in by path; the media store is told (Downloads, a PC by cable).
		/// </summary>
		private const string ExportFolder = "/storage/emulated/0/Download/RimWorld";

		private static void ChooseExport()
		{
			List<FileInfo> saves;
			try
			{
				saves = GenFilePaths.AllSavedGameFiles.ToList();
			}
			catch (Exception e)
			{
				Find.WindowStack.Add(new Dialog_MessageBox(ModImport.Say("读不到存档列表：", "The saves could not be listed: ") + e.Message));
				return;
			}
			if (saves.Count == 0)
			{
				Messages.Message(ModImport.Say("还没有存档。", "There are no saves yet."), MessageTypeDefOf.RejectInput, false);
				return;
			}
			var options = new List<FloatMenuOption>();
			if (saves.Count > 1)
			{
				options.Add(new FloatMenuOption(ModImport.Say("全部 " + saves.Count + " 个存档（打包成一个 .zip）", "All " + saves.Count + " saves (in one .zip)"), () => Find.WindowStack.Add(new Exporting(saves, true))));
			}
			foreach (FileInfo save in saves)
			{
				options.Add(new FloatMenuOption(Path.GetFileNameWithoutExtension(save.Name) + "   " + save.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), () => Find.WindowStack.Add(new Exporting(new List<FileInfo> { save }, false))));
			}
			Find.WindowStack.Add(new FloatMenu(options, ModImport.Say("导出到手机的 Download/RimWorld", "Export to the phone's Download/RimWorld")));
		}

		/// <summary>Off the main thread: the copies (or the .zip) in Download/RimWorld; returns the files made.</summary>
		private static List<string> Export(List<FileInfo> saves, bool zip)
		{
			Directory.CreateDirectory(ExportFolder);
			var made = new List<string>();
			if (zip)
			{
				string name = ModImport.Say("RimWorld 存档 ", "RimWorld saves ") + DateTime.Now.ToString("yyyy-MM-dd HHmm");
				made.Add(WriteFree(name, ".zip", target =>
				{
					using (var archive = new ZipArchive(new FileStream(target, FileMode.CreateNew), ZipArchiveMode.Create))
					{
						foreach (FileInfo save in saves)
						{
							ZipArchiveEntry entry = archive.CreateEntry(save.Name, CompressionLevel.Fastest);
							entry.LastWriteTime = save.LastWriteTime;
							using (Stream from = save.OpenRead())
							using (Stream to = entry.Open())
							{
								from.CopyTo(to);
							}
						}
					}
				}));
			}
			else
			{
				foreach (FileInfo save in saves)
				{
					made.Add(WriteFree(Path.GetFileNameWithoutExtension(save.Name), save.Extension, target => File.Copy(save.FullName, target)));
				}
			}
			foreach (string file in made)
			{
				Screenshots.ScanMedia(file);
			}
			return made;
		}

		/// <summary>
		/// Writes under the name, or "name (2)"...; a name the media store still has for a file that is gone (deleted
		/// around it) is refused although File.Exists says false: then with the time added.
		/// </summary>
		private static string WriteFree(string name, string extension, Action<string> write)
		{
			string target = Path.Combine(ExportFolder, name + extension);
			for (int n = 2; File.Exists(target); n++)
			{
				target = Path.Combine(ExportFolder, name + " (" + n + ")" + extension);
			}
			try
			{
				write(target);
			}
			catch (IOException) when (!File.Exists(target))
			{
				target = Path.Combine(ExportFolder, name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + extension);
				write(target);
			}
			return target;
		}

		/// <summary>Stays up while the saves are copied, then says where they are.</summary>
		private sealed class Exporting : Window
		{
			private volatile bool done;
			private List<string> made;
			private string problem;

			public override Vector2 InitialSize => new Vector2(560f, 180f);

			public Exporting(List<FileInfo> saves, bool zip)
			{
				forcePause = true;
				absorbInputAroundWindow = true;
				closeOnAccept = false;
				closeOnCancel = false;
				closeOnClickedOutside = false;
				doCloseX = false;
				draggable = false;
				ThreadPool.QueueUserWorkItem(_ =>
				{
					try
					{
						made = Export(saves, zip);
					}
					catch (Exception e)
					{
						problem = e.GetType().Name + ": " + e.Message;
					}
					done = true;
				});
			}

			public override void DoWindowContents(Rect inRect)
			{
				if (done)
				{
					Close(false);
					Report();
					return;
				}
				Text.Font = GameFont.Small;
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(inRect, ModImport.Say("正在导出存档……", "Exporting saves..."));
				Text.Anchor = TextAnchor.UpperLeft;
			}

			private void Report()
			{
				Console.WriteLine("[mobile] save export: " + (problem ?? string.Join(", ", made)));
				if (problem != null)
				{
					Find.WindowStack.Add(new Dialog_MessageBox(ModImport.Say("导出存档时出错：\n\n", "Exporting the saves failed:\n\n") + problem));
					return;
				}
				string names = string.Join("\n", made.Select(f => "  " + Path.GetFileName(f)));
				Find.WindowStack.Add(new Dialog_MessageBox(ModImport.Say(
					"已导出到手机的 Download/RimWorld 文件夹：\n\n" + names + "\n\n在文件管理器的“下载”（Download）里能找到；用数据线连电脑时在“内部存储\\Download\\RimWorld”。发给别人：在聊天软件里发送文件时从“下载”里选。\n\n电脑上把 .rws 放进 C:\\Users\\你的用户名\\AppData\\LocalLow\\Ludeon Studios\\RimWorld by Ludeon Studios\\Saves 就能读（.zip 要先解压）；另一台手机用“导入存档…”选它（.zip 也可以）。",
					"Exported into the phone's Download/RimWorld folder:\n\n" + names + "\n\nA file manager shows it under Downloads; over a cable a PC finds it in Internal storage\\Download\\RimWorld. To send it to someone, pick it from Downloads in the chat app.\n\nOn a PC, put the .rws files into C:\\Users\\<you>\\AppData\\LocalLow\\Ludeon Studios\\RimWorld by Ludeon Studios\\Saves (unpack a .zip first); another phone takes them (or the .zip) with \"Import saves...\".")));
			}
		}

		private static void Pick(Dialog_FileList dialog)
		{
			try
			{
				ModImport.OpenChooser(false);
				Find.WindowStack.Add(new Progress(dialog));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] save import: could not open the file chooser: " + e);
				Find.WindowStack.Add(new Dialog_MessageBox(ModImport.Say("打不开系统的文件选择界面：", "The system's file chooser could not be opened: ") + e.Message));
			}
		}

		/// <summary>Puts every save the chooser copied with the saves; says what came in.</summary>
		private static void Import(Dialog_FileList dialog, string leftOut)
		{
			var imported = new List<string>();
			var problems = new List<string>();
			string saves = GenFilePaths.SavedGamesFolderPath;
			try
			{
				Directory.CreateDirectory(saves);
				foreach (string file in Directory.EnumerateFiles(ModImport.Staging, "*", SearchOption.AllDirectories))
				{
					if (!file.EndsWith(".rws", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					string name = Path.GetFileNameWithoutExtension(file);
					string target = Path.Combine(saves, name + ".rws");
					for (int n = 2; File.Exists(target); n++)
					{
						target = Path.Combine(saves, name + " (" + n + ").rws");
					}
					try
					{
						File.Copy(file, target);
						imported.Add(Path.GetFileNameWithoutExtension(target));
					}
					catch (Exception e)
					{
						problems.Add(name + ": " + e.Message);
					}
				}
			}
			catch (Exception e)
			{
				problems.Add(e.Message);
			}
			ModImport.CleanUp();
			Console.WriteLine("[mobile] save import: " + imported.Count + " imported (" + string.Join(", ", imported) + ")" + (problems.Count > 0 ? ", problems: " + string.Join("; ", problems) : ""));
			try
			{
				AccessTools.Method(typeof(Dialog_FileList), "ReloadFiles").Invoke(dialog, null);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] save import: the list was not reloaded: " + e.Message);
			}
			string text;
			if (imported.Count > 0)
			{
				text = ModImport.Say("导入了 " + imported.Count + " 个存档：\n\n", "Imported " + imported.Count + " save(s):\n\n") + string.Join("\n", imported.Select(n => "  " + n))
					+ ModImport.Say("\n\n读档时如果提示 mod 不一致，要先在 Mod 配置里启用和电脑上一样的 mod。", "\n\nIf loading says the mods differ, enable the same mods as on the PC on the mod page first.");
			}
			else
			{
				text = ModImport.Say("没有找到存档。请选择 .rws 文件，或者装着 .rws 存档的 .zip 压缩包。", "No save was found. Choose .rws files, or .zip archives holding .rws saves.");
			}
			if (!string.IsNullOrWhiteSpace(leftOut))
			{
				text += ModImport.Say("\n\n不是存档，没有导入：\n", "\n\nNot saves, left out:\n") + leftOut.Trim();
			}
			if (problems.Count > 0)
			{
				text += ModImport.Say("\n\n出错：\n", "\n\nProblems:\n") + string.Join("\n", problems);
			}
			Find.WindowStack.Add(new Dialog_MessageBox(text));
		}

		/// <summary>Postfix of GenFilePaths.AllSavedGameFiles: the saves the game may read (the others are told about).</summary>
		public static void ReadableOnly(ref IEnumerable<FileInfo> __result)
		{
			if (__result == null)
			{
				return;
			}
			var readable = new List<FileInfo>();
			foreach (FileInfo file in __result)
			{
				if (Readable(file))
				{
					readable.Add(file);
				}
				else if (told.Add(file.Name))
				{
					lock (toTell)
					{
						toTell.Add(file.Name);
					}
					Console.WriteLine("[mobile] save import: " + file.FullName + " may not be read by the game: left out of the list");
				}
			}
			__result = readable;
		}

		private static bool Readable(FileInfo file)
		{
			try
			{
				using (file.OpenRead())
				{
				}
				return true;
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}
			catch (Exception)
			{
				return true; // anything else is for the game's own handling
			}
		}

		/// <summary>In a file dialog's drawing (on top of it): which saves were left out, why, and what to do.</summary>
		private static void TellUnreadable()
		{
			string list;
			lock (toTell)
			{
				list = string.Join("\n", toTell.Select(n => "  " + n));
				toTell.Clear();
			}
			string folder = GenFilePaths.SavedGamesFolderPath;
			const string Shared = "/storage/emulated/0/";
			if (folder.StartsWith(Shared, StringComparison.Ordinal))
			{
				folder = folder.Substring(Shared.Length);
			}
			Find.WindowStack.Add(new Dialog_MessageBox(ModImport.Say(
				"游戏没有权限读取下面这些存档，已经从列表里拿掉：\n\n" + list + "\n\n位置：" + folder + "\n\n这通常是用文件管理器把存档直接复制进了这个文件夹：这样放进去的文件属于文件管理器，游戏读不了。\n\n请点“载入”窗口左下角的“导入存档…”，选择这个存档（.rws 文件，或者装着它的 .zip）重新导入。导入以后，可以用同一个文件管理器删掉原来那个。",
				"The game may not read these saves, so they were left out of the list:\n\n" + list + "\n\nIn: " + folder + "\n\nThis usually means they were copied into that folder with a file manager: files put there that way belong to the file manager, and the game cannot read them.\n\nUse \"Import saves...\" at the bottom left of the load dialog and choose the save (the .rws file, or a .zip holding it). Afterwards the old copy can be deleted with the same file manager.")));
		}

		/// <summary>Stays up while the player chooses and the files are copied, then imports what came.</summary>
		private sealed class Progress : Window
		{
			private readonly Dialog_FileList dialog;
			private int state = Choosing, files;
			private long bytes;
			private float nextLook;
			private bool finished;

			public override Vector2 InitialSize => new Vector2(560f, 180f);

			public Progress(Dialog_FileList dialog)
			{
				this.dialog = dialog;
				forcePause = true;
				absorbInputAroundWindow = true;
				closeOnAccept = false;
				closeOnCancel = false;
				closeOnClickedOutside = false;
				doCloseX = false;
				draggable = false;
			}

			public override void DoWindowContents(Rect inRect)
			{
				if (Time.realtimeSinceStartup >= nextLook)
				{
					nextLook = Time.realtimeSinceStartup + 0.2f;
					Poll();
				}
				Text.Font = GameFont.Small;
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(inRect, state == Copying
					? ModImport.Say($"正在复制：{files} 个文件，{bytes / 1048576f:F1} MB", $"Copying: {files} files, {bytes / 1048576f:F1} MB")
					: ModImport.Say("请在系统的文件选择界面里选择存档……", "Choose the saves in the system's file chooser..."));
				Text.Anchor = TextAnchor.UpperLeft;
			}

			private void Poll()
			{
				if (finished || !ModImport.ReadState(out int now, out files, out bytes, out string message))
				{
					return;
				}
				state = now;
				if (state == Choosing || state == Copying)
				{
					return;
				}
				finished = true;
				Close(false);
				if (state == Done)
				{
					Import(dialog, message);
				}
				else
				{
					ModImport.CleanUp();
					if (state == Failed)
					{
						Find.WindowStack.Add(new Dialog_MessageBox(ModImport.Say("复制文件时出错，没有导入存档：\n\n", "Copying the files failed, no save was imported:\n\n") + ModImport.Explain(message)));
					}
				}
			}
		}
	}
}
