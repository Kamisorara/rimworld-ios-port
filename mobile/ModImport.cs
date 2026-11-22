using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using HarmonyLib;
using PortHooks;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PortMobile
{
	/// <summary>
	/// The first button below the mod lists is "Get mods..." on the PC: it opens the Steam Workshop or the forum.
	/// Neither helps on a phone, where a mod has to be brought over as files. Here the button is "Import mods...":
	/// the player picks archives (.zip) or a folder in the system's file chooser, and every mod found in them
	/// (a folder with About/About.xml, as it lies in the PC's Workshop folder) is put into the game's Mods folder.
	/// A mod that is there already, recognised by its package id, is replaced: that is how a mod is updated (another
	/// Workshop item of that package id goes beside it instead, as on the PC).
	/// The second button, "Unsubscribe..." on the PC, deletes disabled mods from the phone.
	///
	/// Ported from the 1.6 project (tools/portmobile/ModImport.cs). What is different here:
	///   - choosing and copying happen in the activity (host/java MainActivity.pickFiles, started through the GLFW
	///     shim); it copies into files/ModImport and reports in files/ModImport.state, which this side reads;
	///   - a PC mod's assemblies have to be prepared for this runtime (patch/ModPrep.cs, run on the phone by
	///     PortHooks): the import does that for each mod it installs, on a worker thread, with the progress shown;
	///   - nothing is done about Unity asset bundles (the 1.6 import converted them for Android).
	/// Nothing in Mods is touched before a complete mod has arrived.
	/// </summary>
	internal static class ModImport
	{
		[DllImport("glfw")] private static extern int glfwAndroidPickFiles(int folder);
		[DllImport("glfw")] private static extern int glfwAndroidRestart();
		[DllImport("glfw")] private static extern void glfwAndroidPrepareExit();

		internal static string Say(string chinese, string english)
		{
			return Texts.Chinese ? chinese : english;
		}

		/// <summary>
		/// The copy's error as Android words it, with what it means where that is known. The user, 2026-10-26: "Only owner is
		/// able to interact with pending/trashed item content://media/...": the chooser handed over a file still being written
		/// by another app (a download not finished) or one in the recycle bin, which only that app may read.
		/// </summary>
		internal static string Explain(string message)
		{
			if (message == null)
			{
				return "";
			}
			if (message.IndexOf("pending/trashed", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return message + Say("\n\n这个文件还没写完（比如还在下载，或者别的应用正在复制它），或者已经被删进了回收站，系统只让创建它的那个应用读。请等下载完成后，到文件所在的文件夹（例如“下载”）里重新选它；如果是从“最近”里选的，换到文件夹里去选。",
					"\n\nThat file is not finished yet (still downloading, or another app is copying it), or it is in the recycle bin: only the app that made it may read it. Wait until the download is done, then pick it from its folder (such as Downloads), not from Recent.");
			}
			if (message.IndexOf("ENOSPC", StringComparison.Ordinal) >= 0 || message.IndexOf("No space left", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return message + Say("\n\n手机存储空间不够了，清出一些空间再导入。", "\n\nThe phone's storage is full: free some space and import again.");
			}
			return message;
		}

		// files/ModImport and files/ModImport.state, next to the game folder (the activity writes both).
		private static string FilesDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
		internal static string Staging => Path.Combine(FilesDir, "ModImport");
		private static string StateFile => Path.Combine(FilesDir, "ModImport.state");

		public static void Start(Harmony harmony)
		{
			try
			{
				// By parameter types: the base class Page has a DoBottomButtons of its own.
				harmony.Patch(AccessTools.Method(typeof(Page_ModsConfig), "DoBottomButtons", new[] { typeof(Rect) }), transpiler: new HarmonyMethod(typeof(ModImport), nameof(BottomButtons)));
				harmony.Patch(AccessTools.Method(typeof(ModsConfig), nameof(ModsConfig.Save)), prefix: new HarmonyMethod(typeof(ModImport), nameof(KeepPreviousModList)));
				harmony.Patch(AccessTools.Method(typeof(ModsConfig), "SaveFromList"), prefix: new HarmonyMethod(typeof(ModImport), nameof(KeepPreviousModListFrom)));
				harmony.Patch(AccessTools.Method(typeof(GenCommandLine), nameof(GenCommandLine.Restart)), prefix: new HarmonyMethod(typeof(ModImport), nameof(BeforeRestart)));
				harmony.Patch(AccessTools.Method(typeof(Application), nameof(Application.Quit), Type.EmptyTypes), prefix: new HarmonyMethod(typeof(ModImport), nameof(BeforeQuit)));
				Console.WriteLine("[mobile] mod import: the mod page's buttons are \"Import mods...\" and \"Delete mods...\"");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mod import not installed: " + e);
			}
		}

		/// <summary>
		/// Prefix of GenCommandLine.Restart (the mod page's "save and apply" ends in it, a change of language too): the PC
		/// game launches its executable again and shuts down, which leaves a phone at its home screen with the game gone.
		/// The activity starts itself again in a new process instead (MainActivity.restartGame); what had to be saved
		/// (the mod list, the preferences) is written before the game calls this.
		/// </summary>
		internal static bool BeforeRestart()
		{
			try
			{
				Console.WriteLine("[mobile] restart: the activity is started again in a new process");
				if (glfwAndroidRestart() != 0)
				{
					return false;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] restart through the activity failed: " + e.Message);
			}
			return true;
		}

		/// <summary>
		/// Prefix of Application.Quit ("quit to desktop"): the activity leaves the screen and the process is made to end
		/// without the C library's exit handlers (the shim's glfwAndroidPrepareExit says why). The engine's own quit goes on.
		/// When something asked the engine for a restart first (RestartManager.RequestRestart, as PurePatcher does before it
		/// quits), the PC launches the game again once it has quit: the activity is started again instead of finished
		/// (PortHooks.SelfContained; a finished activity may no longer start one).
		/// </summary>
		internal static void BeforeQuit()
		{
			try
			{
				if (PortHooks.SelfContained.RestartRequested() && PortHooks.SelfContained.AskRestart("asked of the engine, then quit"))
				{
					return;
				}
				Console.WriteLine("[mobile] quit: the activity finishes, the process ends when the runtime has shut down");
				glfwAndroidPrepareExit();
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] quit: not prepared: " + e.Message);
			}
		}

		/// <summary>Stands in for "GetMods".Translate() (see BottomButtons).</summary>
		public static TaggedString Label(string key)
		{
			return Say("导入Mod…", "Import mods...");
		}

		/// <summary>Stands in for "UnsubscribeMultiple".Translate().</summary>
		public static TaggedString DeleteLabel(string key)
		{
			return Say("删除Mod…", "Delete mods...");
		}

		/// <summary>
		/// Transpiler of Page_ModsConfig.DoBottomButtons: the first button gets another label, and a tap on it goes
		/// to OpenMenu instead of into the menu with the Workshop and the forum; the second, "Unsubscribe...", which
		/// only means something with Steam, becomes "Delete mods..." the same way.
		/// </summary>
		internal static IEnumerable<CodeInstruction> BottomButtons(IEnumerable<CodeInstruction> instructions)
		{
			var code = new List<CodeInstruction>(instructions);
			foreach (var button in new[] { ("GetMods", nameof(Label), nameof(OpenMenu)), ("UnsubscribeMultiple", nameof(DeleteLabel), nameof(DeleteMenu)) })
			{
				bool done = false;
				int at = code.FindIndex(i => i.opcode == OpCodes.Ldstr && (i.operand as string) == button.Item1);
				if (at >= 0 && at + 1 < code.Count && code[at + 1].operand is MethodInfo translate && translate.Name == "Translate" && translate.GetParameters().Length == 1)
				{
					// "if (row.ButtonText(...))" ends in a jump over the block when the button was not pressed.
					int skip = code.FindIndex(at, i => i.opcode == OpCodes.Brfalse || i.opcode == OpCodes.Brfalse_S);
					if (skip > at)
					{
						code[at + 1].opcode = OpCodes.Call;
						code[at + 1].operand = AccessTools.Method(typeof(ModImport), button.Item2);
						code.Insert(skip + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ModImport), button.Item3)));
						code.Insert(skip + 2, new CodeInstruction(OpCodes.Br, code[skip].operand));
						done = true;
					}
				}
				if (!done)
				{
					Console.WriteLine("[mobile] mod import: the mod screen's buttons look different from what was expected; \"" + button.Item1 + "\" stays as it is");
				}
			}
			return code;
		}

		// ---- the previous mod list, kept ----

		/// <summary>
		/// Prefix of ModsConfig.Save: the mod list as it is in the file is kept as a mod list of the game's own kind
		/// ("Previous mod list" in ModLists/), which the mod screen's "Load mod list" brings back. On a PC the file is
		/// easy to put back by hand; on the phone it is not, and the game throws the whole list away when it fails to
		/// start with it. Written only when the list changes.
		/// </summary>
		internal static void KeepPreviousModList()
		{
			// ModsConfig.data is of a private nested class (ModsConfigData); its activeMods is the list that is written.
			object data = typeof(ModsConfig).GetField("data", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
			KeepPreviousModListFrom(data?.GetType().GetField("activeMods")?.GetValue(data) as List<string>);
		}

		/// <summary>Prefix of ModsConfig.SaveFromList, which writes the given list instead of the current one.</summary>
		internal static void KeepPreviousModListFrom(List<string> mods)
		{
			try
			{
				string file = GenFilePaths.ModsConfigFilePath;
				if (mods == null || !File.Exists(file))
				{
					return;
				}
				var document = new XmlDocument();
				document.Load(file);
				var previous = new List<string>();
				XmlElement active = document.DocumentElement?["activeMods"];
				if (active != null)
				{
					foreach (XmlNode node in active.ChildNodes)
					{
						if (node.NodeType == XmlNodeType.Element)
						{
							previous.Add(node.InnerText);
						}
					}
				}
				if (previous.Count == 0 || previous.SequenceEqual(mods))
				{
					return;
				}
				var names = new List<string>();
				foreach (string id in previous)
				{
					names.Add(ModLister.GetModWithIdentifier(id)?.Name ?? id);
				}
				// The same shape as GameDataSaveLoader.SaveModList writes (ScribeMetaHeaderUtility reads gameVersion,
				// modIds and modNames from the header), written directly: this runs inside Save, which the game also
				// calls while it is starting up, before its own saver may be used.
				string target = GenFilePaths.AbsFilePathForModList(Say("上一次的模组列表", "Previous mod list"));
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				using (XmlWriter writer = XmlWriter.Create(target, new XmlWriterSettings { Indent = true, IndentChars = "\t", Encoding = new UTF8Encoding(false) }))
				{
					writer.WriteStartDocument();
					writer.WriteStartElement("savedModList");
					writer.WriteStartElement("meta");
					writer.WriteElementString("gameVersion", VersionControl.CurrentVersionStringWithRev);
					WriteList(writer, "modIds", previous);
					WriteList(writer, "modNames", names);
					writer.WriteEndElement();
					writer.WriteStartElement("modList");
					WriteList(writer, "ids", previous);
					WriteList(writer, "names", names);
					writer.WriteEndElement();
					writer.WriteEndElement();
					writer.WriteEndDocument();
				}
				Console.WriteLine("[mobile] the mod list is changing from " + previous.Count + " to " + mods.Count + " mods; the old one is kept as " + Path.GetFileName(target));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] could not keep the previous mod list: " + e.Message);
			}
		}

		private static void WriteList(XmlWriter writer, string name, List<string> items)
		{
			writer.WriteStartElement(name);
			foreach (string item in items)
			{
				writer.WriteElementString("li", item);
			}
			writer.WriteEndElement();
		}

		// ---- deleting ----

		// The mods that may be deleted: not the game's own, not in use, and in the game's Mods folder.
		private static List<ModMetaData> Deletable(IEnumerable<ModMetaData> mods)
		{
			string root = Path.GetFullPath(GenFilePaths.ModsFolderPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			var list = new List<ModMetaData>();
			foreach (ModMetaData mod in mods)
			{
				if (mod != null && !mod.Official && !mod.Active && mod.RootDir != null && Path.GetFullPath(mod.RootDir.FullName).StartsWith(root, StringComparison.Ordinal))
				{
					list.Add(mod);
				}
			}
			return list;
		}

		public static void DeleteMenu()
		{
			SoundDefOf.Click.PlayOneShotOnCamera();
			Page_ModsConfig page = Find.WindowStack.WindowOfType<Page_ModsConfig>();
			var selected = page == null ? null : AccessTools.Field(typeof(Page_ModsConfig), "selectedMods").GetValue(page) as List<ModMetaData>;
			var options = new List<FloatMenuOption>
			{
				new FloatMenuOption(Say("删除选中的 mod（只删已禁用的）", "Delete the selected mods (disabled ones only)"), delegate
				{
					var chosen = Deletable(selected ?? new List<ModMetaData>());
					if (chosen.Count == 0)
					{
						Find.WindowStack.Add(new Dialog_MessageBox(Say("没有选中可以删除的 mod。正在使用的 mod 要先禁用（它会到左边“禁用”一栏），再选中它删除。", "No deletable mod is selected. A mod in use has to be disabled first (it then moves to the list of disabled mods); select it there to delete it.")));
						return;
					}
					ConfirmDelete(chosen);
				}),
				new FloatMenuOption(Say("删除所有已禁用的 mod", "Delete all disabled mods"), delegate
				{
					var chosen = Deletable(ModLister.AllInstalledMods);
					if (chosen.Count == 0)
					{
						Find.WindowStack.Add(new Dialog_MessageBox(Say("没有已禁用的 mod。", "There is no disabled mod.")));
						return;
					}
					ConfirmDelete(chosen);
				})
			};
			List<(string name, string folder)> unlisted = UnlistedDuplicates();
			if (unlisted.Count > 0)
			{
				options.Add(new FloatMenuOption(Say($"删除没列出的重复 mod（{unlisted.Count} 个）", $"Delete the duplicates that are not listed ({unlisted.Count})"), delegate { ConfirmDeleteUnlisted(unlisted); }));
			}
			Find.WindowStack.Add(new FloatMenu(options));
		}

		/// <summary>
		/// The folders in Mods that the game neither lists nor runs because a listed mod has their package id (the game
		/// keeps the first it finds and logs the others as duplicates): other Workshop items of one package id, which the
		/// import puts beside each other as the PC keeps them. No row on the mod screen selects them, so they are offered here.
		/// </summary>
		private static List<(string name, string folder)> UnlistedDuplicates()
		{
			var unlisted = new List<(string, string)>();
			try
			{
				var kept = new HashSet<string>(StringComparer.Ordinal);
				foreach (ModMetaData mod in ModLister.AllInstalledMods)
				{
					if (mod?.RootDir != null)
					{
						kept.Add(Path.GetFullPath(mod.RootDir.FullName).TrimEnd(Path.DirectorySeparatorChar));
					}
				}
				// A mod loaded at start runs from its folder until the game starts again, listed or not (1.7 reads a texture's
				// file when it is first shown). After an import the mod page may list the other copy, since the phone reads
				// the folders in the order the file system keeps them: the copy loaded at start is offered after the next
				// start, once it is neither listed nor loaded.
				foreach (ModContentPack running in LoadedModManager.RunningModsListForReading)
				{
					if (running?.RootDir != null)
					{
						kept.Add(Path.GetFullPath(running.RootDir).TrimEnd(Path.DirectorySeparatorChar));
					}
				}
				foreach (string folder in Directory.GetDirectories(GenFilePaths.ModsFolderPath))
				{
					try
					{
						string about = kept.Contains(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)) ? null : AboutFile(folder, false);
						if (about == null)
						{
							continue;
						}
						Describe(about, out string id, out string name);
						if (!string.IsNullOrEmpty(id) && ModLister.GetModWithIdentifier(id, ignorePostfix: true) != null)
						{
							unlisted.Add((string.IsNullOrEmpty(name) ? Path.GetFileName(folder) : name, folder));
						}
					}
					catch (Exception)
					{
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mod delete: could not look for duplicates: " + e.Message);
			}
			return unlisted;
		}

		private static void ConfirmDeleteUnlisted(List<(string name, string folder)> unlisted)
		{
			string list = "  " + string.Join("\n  ", unlisted.Select(u => u.name + "  (" + Path.GetFileName(u.folder) + ")").ToArray());
			Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
				Say($"这 {unlisted.Count} 个 mod 和列表里的 mod 用同一个 packageId，游戏没有列出、也不加载它们。从手机上删除它们的文件？\n\n{list}", $"These {unlisted.Count} mod(s) have the package id of a mod in the list; the game neither lists nor loads them. Delete their files from the phone?\n\n{list}"),
				delegate
				{
					var problems = new List<string>();
					foreach (var (name, folder) in unlisted)
					{
						try
						{
							Directory.Delete(folder, true);
							Console.WriteLine("[mobile] mod delete: removed " + name + ", an unlisted duplicate, at " + folder);
						}
						catch (Exception e)
						{
							problems.Add(name + ": " + e.Message);
						}
					}
					// The list does not change (it never had these), but the update notice may name one of them.
					UpdateNotice.ModsChanged();
					if (problems.Count > 0)
					{
						Find.WindowStack.Add(new Dialog_MessageBox(Say("没能删除：\n  ", "Not deleted:\n  ") + string.Join("\n  ", problems.ToArray())));
					}
				}, destructive: true));
		}

		private static void ConfirmDelete(List<ModMetaData> mods)
		{
			var names = new List<string>();
			foreach (ModMetaData mod in mods)
			{
				names.Add(mod.Name);
			}
			string list = "  " + string.Join("\n  ", names.ToArray());
			Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
				Say($"从手机上删除这 {mods.Count} 个 mod 的文件？（存档里用到它们的话，读档会报缺少 mod。）\n\n{list}", $"Delete the files of these {mods.Count} mod(s) from the phone? (A save that uses them will report them missing.)\n\n{list}"),
				delegate
				{
					var problems = new List<string>();
					foreach (ModMetaData mod in mods)
					{
						try
						{
							Directory.Delete(mod.RootDir.FullName, true);
							Console.WriteLine("[mobile] mod delete: removed " + mod.Name + " (" + mod.PackageId + ") at " + mod.RootDir.FullName);
						}
						catch (Exception e)
						{
							problems.Add(mod.Name + ": " + e.Message);
						}
					}
					Refresh(null);
					if (problems.Count > 0)
					{
						Find.WindowStack.Add(new Dialog_MessageBox(Say("没能删除：\n  ", "Not deleted:\n  ") + string.Join("\n  ", problems.ToArray())));
					}
				}, destructive: true));
		}

		// ---- importing ----

		public static void OpenMenu()
		{
			SoundDefOf.Click.PlayOneShotOnCamera();
			var options = new List<FloatMenuOption>
			{
				new FloatMenuOption(Say("从压缩包导入（.zip，可以多选）", "From archives (.zip, several at once)"), delegate { Pick(false); }),
				new FloatMenuOption(Say("从文件夹导入", "From a folder"), delegate { Pick(true); }),
				new FloatMenuOption(Say("从 Steam 创意工坊下载", "From the Steam Workshop"), WorkshopLoader.Open),
				new FloatMenuOption(Say("怎么把mod放到手机上？", "How do mods get onto the phone?"), delegate
				{
					Find.WindowStack.Add(new Dialog_MessageBox(Say(
						"电脑上订阅的mod在 Steam 的 steamapps/workshop/content/294100 里，每个mod一个文件夹（里面有 About 文件夹）。\n\n"
						+ "把要用的mod文件夹原样拷到手机上（比如“下载”里），或者先打成 zip 再拷，然后回到这里导入：\n"
						+ "• 选 zip：一个 zip 里可以是一个mod，也可以是好几个mod的文件夹。\n"
						+ "• 选文件夹：可以选一个mod的文件夹，也可以选装着好几个mod的文件夹。\n\n"
						+ "导入时会自动把mod的程序集处理成手机能用的（电脑上的文件不用改）。导入的mod出现在左边“禁用”一栏，启用后点“保存并应用”。已经有的mod再导入一次就是更新。",
						"Mods subscribed on the PC are in Steam's steamapps/workshop/content/294100, one folder per mod (with an About folder inside).\n\n"
						+ "Copy the folders of the mods you want to the phone as they are (to Downloads, for one), or zipped, then import them here:\n"
						+ "• Archives: a zip may hold one mod or the folders of several.\n"
						+ "• A folder: one mod's folder, or a folder that holds several mods.\n\n"
						+ "The import prepares a mod's assemblies for the phone by itself (the PC's files need no change). Imported mods show up in the list of disabled mods. Importing a mod that is there already updates it.")));
				})
			};
			Find.WindowStack.Add(new FloatMenu(options));
		}

		/// <summary>The system's chooser, its copies going to Staging (also for SaveImport): throws when it cannot be opened.</summary>
		internal static void OpenChooser(bool folder)
		{
			CleanUp();
			Directory.CreateDirectory(Staging);
			File.Delete(StateFile);
			if (glfwAndroidPickFiles(folder ? 1 : 0) == 0)
			{
				throw new InvalidOperationException(Say("活动没有响应", "the activity did not answer"));
			}
		}

		private static void Pick(bool folder)
		{
			try
			{
				OpenChooser(folder);
				Find.WindowStack.Add(new Progress());
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mod import: could not open the file chooser: " + e);
				Find.WindowStack.Add(new Dialog_MessageBox(Say("打不开系统的文件选择界面：", "The system's file chooser could not be opened: ") + e.Message));
			}
		}

		/// <summary>What the activity last wrote to the state file: "state count bytes" and, from the second line on, a message.</summary>
		internal static bool ReadState(out int state, out int files, out long bytes, out string message)
		{
			state = 0;
			files = 0;
			bytes = 0;
			message = "";
			try
			{
				if (!File.Exists(StateFile))
				{
					return false;
				}
				string text = File.ReadAllText(StateFile, Encoding.UTF8);
				int end = text.IndexOf('\n');
				string[] parts = (end < 0 ? text : text.Substring(0, end)).Split(' ');
				if (parts.Length < 3)
				{
					return false;
				}
				state = int.Parse(parts[0]);
				files = int.Parse(parts[1]);
				bytes = long.Parse(parts[2]);
				message = end < 0 ? "" : text.Substring(end + 1);
				return true;
			}
			catch (Exception)
			{
				return false; // being replaced right now; the next look will do
			}
		}

		/// <summary>What the worker thread found and did; read by the window once the thread is done.</summary>
		internal sealed class Outcome
		{
			public readonly List<string> Imported = new List<string>();
			public readonly List<string> Updated = new List<string>();
			public readonly List<string> UpdatedIds = new List<string>();
			public readonly List<string> Beside = new List<string>();
			public readonly List<string> BesideIds = new List<string>();
			public readonly List<string> Problems = new List<string>();
			public readonly List<string> PrepProblems = new List<string>();
			public string FirstPackage;
			public int Prepared;
			public volatile string Line = "";
			public volatile bool Done;
		}

		/// <summary>
		/// Stays up while the player chooses, the files are copied and the mods are put in place and prepared, then
		/// refreshes the mod page and says what happened.
		/// </summary>
		private sealed class Progress : Window
		{
			private const int Choosing = 1, Copying = 2, Done = 3, Failed = 4, Cancelled = 5;
			private int state = Choosing, files;
			private long bytes;
			private float nextLook;
			private string leftOut = "";
			private Outcome outcome;
			// Set when the window has done its last step: the contents may be drawn once more in the frame that closes it,
			// and rebuilding the mod list takes longer than the pause between two looks (the result box came up twice).
			private bool finished;

			public override Vector2 InitialSize => new Vector2(560f, 180f);

			public Progress()
			{
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
				string line;
				if (outcome != null)
				{
					line = Say("正在安装并处理程序集……\n", "Installing and preparing assemblies...\n") + outcome.Line;
				}
				else if (state == Copying)
				{
					line = Say($"正在复制：{files} 个文件，{bytes / 1048576f:F1} MB", $"Copying: {files} files, {bytes / 1048576f:F1} MB");
				}
				else
				{
					line = Say("请在系统的文件选择界面里选择……", "Choose in the system's file chooser...");
				}
				Widgets.Label(inRect, line);
				Text.Anchor = TextAnchor.UpperLeft;
			}

			private void Poll()
			{
				if (finished)
				{
					return;
				}
				if (outcome != null)
				{
					if (outcome.Done)
					{
						finished = true;
						Close(false);
						Report(outcome, leftOut);
					}
					return;
				}
				if (!ReadState(out int now, out files, out bytes, out string message))
				{
					return;
				}
				state = now;
				if (state == Done)
				{
					leftOut = message ?? "";
					outcome = new Outcome();
					Outcome mine = outcome;
					string mods = GenFilePaths.ModsFolderPath;
					Version game = VersionControl.CurrentVersion;
					var worker = new Thread(() => Install(mine, mods, game)) { IsBackground = true, Name = "ModImport" };
					worker.Start();
				}
				else if (state == Failed)
				{
					finished = true;
					Close(false);
					CleanUp();
					Find.WindowStack.Add(new Dialog_MessageBox(Say("复制文件时出错，没有导入任何mod：\n\n", "Copying the files failed, nothing was imported:\n\n") + Explain(message)));
				}
				else if (state != Choosing && state != Copying)
				{
					finished = true;
					Close(false);
					CleanUp();
				}
			}
		}

		internal static void CleanUp()
		{
			try
			{
				if (Directory.Exists(Staging))
				{
					Directory.Delete(Staging, true);
				}
				File.Delete(StateFile);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mod import: could not remove " + Staging + ": " + e.Message);
			}
		}

		private static string Lookup(string folder, string name, bool directory)
		{
			foreach (string path in directory ? Directory.GetDirectories(folder) : Directory.GetFiles(folder))
			{
				if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
				{
					return path;
				}
			}
			return null;
		}

		// The About.xml of a mod folder, or null. A PC does not tell "about" from "About"; the game asks for
		// "About/About.xml". The phone's shared storage does not tell them apart either, so there the file is found
		// as it is and nothing needs renaming (renaming to a name that "exists already" fails there). On storage
		// that does tell them apart, other spellings are renamed.
		private static string AboutFile(string folder, bool fixSpelling)
		{
			string expected = Path.Combine(Path.Combine(folder, "About"), "About.xml");
			if (File.Exists(expected))
			{
				return expected;
			}
			string about = Lookup(folder, "About", true);
			string file = about == null ? null : Lookup(about, "About.xml", false);
			if (file == null || !fixSpelling)
			{
				return file;
			}
			if (Path.GetFileName(file) != "About.xml")
			{
				File.Move(file, Path.Combine(about, "About.xml"));
			}
			if (Path.GetFileName(about) != "About")
			{
				Directory.Move(about, Path.Combine(folder, "About"));
			}
			return expected;
		}

		private static void FindMods(string folder, int depth, List<string> into)
		{
			if (AboutFile(folder, false) != null)
			{
				into.Add(folder);
				return;
			}
			if (depth < 3)
			{
				foreach (string inside in Directory.GetDirectories(folder))
				{
					FindMods(inside, depth + 1, into);
				}
			}
		}

		private static string Element(string xml, string name)
		{
			Match match = Regex.Match(xml, "<" + name + ">\\s*([^<]*?)\\s*</" + name + ">", RegexOptions.IgnoreCase);
			return match.Success ? match.Groups[1].Value : null;
		}

		private static void Describe(string aboutFile, out string packageId, out string name)
		{
			string xml = File.ReadAllText(aboutFile);
			// The mods this one depends on are listed with names and package ids of their own.
			xml = Regex.Replace(xml, "<(modDependencies\\w*|loadAfter\\w*|loadBefore\\w*|incompatibleWith\\w*|forceLoad\\w*)>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
			packageId = Element(xml, "packageId");
			name = Element(xml, "name");
		}

		private static bool HasAssemblies(string folder)
		{
			return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// The Workshop item a mod folder holds, 0 when that is not known: the Workshop download's .workshop, else
		/// About/PublishedFileId.txt (the game's upload writes it into every item; read as the game reads it), else the
		/// name of a folder copied from the PC's Workshop folder, where Steam names each item's folder by its id.
		/// </summary>
		private static ulong WorkshopItem(string folder)
		{
			try
			{
				string stamp = Path.Combine(folder, ".workshop");
				if (File.Exists(stamp))
				{
					string[] lines = File.ReadAllLines(stamp);
					if (lines.Length > 0 && ulong.TryParse(lines[0], out ulong downloaded) && downloaded != 0)
					{
						return downloaded;
					}
				}
				string about = Lookup(folder, "About", true);
				string published = about == null ? null : Lookup(about, "PublishedFileId.txt", false);
				if (published != null && ulong.TryParse(File.ReadAllText(published), out ulong uploaded) && uploaded != 0)
				{
					return uploaded;
				}
			}
			catch (Exception)
			{
			}
			string name = Path.GetFileName(folder);
			return Regex.IsMatch(name, "^[0-9]{6,20}$") && ulong.TryParse(name, out ulong named) ? named : 0;
		}

		/// <summary>The name in a mod folder's About.xml, else the folder's.</summary>
		private static string NameOf(string folder)
		{
			try
			{
				Describe(AboutFile(folder, false), out string _, out string name);
				if (!string.IsNullOrEmpty(name))
				{
					return name;
				}
			}
			catch (Exception)
			{
			}
			return Path.GetFileName(folder);
		}

		/// <summary>
		/// On the worker thread: every mod found in what was copied goes into Mods (replacing the one of the same package
		/// id, unless that is another Workshop item) and has its assemblies prepared. Only files are touched here; the
		/// game's own lists are the window's business.
		/// </summary>
		internal static void Install(Outcome outcome, string mods, Version game, string staging = null)
		{
			try
			{
				var found = new List<string>();
				FindMods(staging ?? Staging, 0, found);
				// The mods that are here already, by package id (more than one where other Workshop items share it).
				var present = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
				foreach (string folder in Directory.GetDirectories(mods))
				{
					try
					{
						string about = AboutFile(folder, false);
						if (about != null)
						{
							Describe(about, out string id, out string _);
							if (!string.IsNullOrEmpty(id))
							{
								if (!present.TryGetValue(id, out List<string> folders))
								{
									present[id] = folders = new List<string>();
								}
								folders.Add(folder);
							}
						}
					}
					catch (Exception)
					{
					}
				}
				int index = 0;
				foreach (string folder in found)
				{
					index++;
					string shown = Path.GetFileName(folder);
					try
					{
						Describe(AboutFile(folder, true), out string id, out string name);
						shown = string.IsNullOrEmpty(name) ? shown : name;
						outcome.Line = shown + "  (" + index + "/" + found.Count + ")";
						// The mod of the same package id is replaced: an update. Not when both are Workshop items and other ones
						// (an original and its continuation): the PC keeps every subscribed item in a folder of its own, and the
						// game lists the first of them and logs the others as duplicates. Here the one imported last took the
						// other's place and folder (Hospitality (Continued) in Hospitality's, 2026-10-14). Where either side's
						// item is not known (a mod from elsewhere) it stays an update: the PC lists such a pair side by side,
						// the Workshop one as "<id>_steam", which the phone, with every mod in Mods, cannot.
						string target = null, beside = null;
						if (!string.IsNullOrEmpty(id) && present.TryGetValue(id, out List<string> same))
						{
							ulong item = WorkshopItem(folder);
							target = same.FirstOrDefault(f => WorkshopItem(f) == item) ?? same.FirstOrDefault(f => item == 0 || WorkshopItem(f) == 0);
							beside = target == null ? same[0] : null;
						}
						bool update = target != null;
						if (update)
						{
							Directory.Delete(target, true);
							outcome.UpdatedIds.Add(id);
						}
						else
						{
							// Another mod may sit in a folder of this name.
							target = Path.Combine(mods, Path.GetFileName(folder));
							for (int n = 2; Directory.Exists(target); n++)
							{
								target = Path.Combine(mods, Path.GetFileName(folder) + "_" + n);
							}
						}
						Directory.Move(folder, target);
						if (beside != null)
						{
							string other = NameOf(beside);
							outcome.Beside.Add(shown + Say("（和已有的“" + other + "”）", " (beside \"" + other + "\")"));
							outcome.BesideIds.Add(id);
						}
						else
						{
							(update ? outcome.Updated : outcome.Imported).Add(shown);
						}
						if (!string.IsNullOrEmpty(id))
						{
							if (!present.TryGetValue(id, out List<string> folders))
							{
								present[id] = folders = new List<string>();
							}
							if (!update)
							{
								folders.Add(target);
							}
							outcome.FirstPackage = outcome.FirstPackage ?? id;
						}
						if (HasAssemblies(target))
						{
							try
							{
								var watch = System.Diagnostics.Stopwatch.StartNew();
								string report = PortPlatform.PrepareModFolder(target, game.Major, game.Minor, out string[] unprepared);
								outcome.Prepared++;
								Console.WriteLine("[mobile] mod import: " + shown + ": " + report + " (" + watch.ElapsedMilliseconds + " ms)");
								if (unprepared.Length > 0)
								{
									outcome.PrepProblems.Add(shown + ": " + string.Join("; ", unprepared.Take(3)) + (unprepared.Length > 3 ? " ..." : ""));
								}
							}
							catch (Exception e)
							{
								outcome.PrepProblems.Add(shown + ": " + e.Message);
								Console.WriteLine("[mobile] mod import: preparing " + shown + " failed: " + e);
							}
						}
						Console.WriteLine("[mobile] mod import: " + (update ? "updated " : "imported ") + shown + " (" + id + ") in " + target + (beside != null ? ", beside the other Workshop item of its package id in " + beside : ""));
					}
					catch (Exception e)
					{
						outcome.Problems.Add(shown + ": " + e.Message);
						Console.WriteLine("[mobile] mod import: " + shown + " failed: " + e);
					}
				}
			}
			catch (Exception e)
			{
				outcome.Problems.Add(e.Message);
				Console.WriteLine("[mobile] mod import failed: " + e);
			}
			try
			{
				PortPlatform.ReleaseModPrep();
			}
			catch (Exception)
			{
			}
			if (staging == null)
			{
				CleanUp();
			}
			outcome.Done = true;
		}

		/// <summary>On the game's thread, once the worker is done: the mod page shows what is there now, and a box says what happened.</summary>
		internal static void Report(Outcome outcome, string leftOut)
		{
			List<string> imported = outcome.Imported, updated = outcome.Updated, beside = outcome.Beside, problems = outcome.Problems;
			// Asked before the list is rebuilt: a mod that is in use keeps running its old code until the game starts again.
			bool updatedActive = outcome.UpdatedIds.Any(id => ModLister.GetModWithIdentifier(id)?.Active ?? false);
			// Of two copies of a mod in use, the one the rebuilt list has is the one that runs from the next start, and
			// that need not be the one running now (the folders are read in the order the file system keeps them).
			bool besideActive = outcome.BesideIds.Any(id => ModLister.GetModWithIdentifier(id)?.Active ?? false);
			if (imported.Count + updated.Count + beside.Count > 0)
			{
				Refresh(outcome.FirstPackage);
			}
			var text = new StringBuilder();
			if (imported.Count > 0)
			{
				text.Append(Say($"导入了 {imported.Count} 个mod，在左边“禁用”一栏里，启用后点“保存并应用”：\n", $"{imported.Count} mod(s) imported; they are in the list of disabled mods:\n"));
				text.Append("  " + string.Join("\n  ", imported.ToArray()) + "\n\n");
			}
			if (updated.Count > 0)
			{
				text.Append(Say($"更新了 {updated.Count} 个已有的mod", $"{updated.Count} mod(s) that were here already have been updated"));
				text.Append(updatedActive ? Say("（其中有正在使用的，重新启动游戏后生效）：\n", " (some are in use: they change when the game is started again):\n") : Say("：\n", ":\n"));
				text.Append("  " + string.Join("\n  ", updated.ToArray()) + "\n\n");
			}
			if (beside.Count > 0)
			{
				text.Append(Say($"这 {beside.Count} 个mod和已有的mod用同一个 packageId，但在创意工坊上是另一个物品（比如原版和续作），所以各放在自己的文件夹里，不再互相替换，和电脑上两个都订阅时一样。游戏只列出、只加载其中一个，另一个启动时在日志里报重复；不要的那个可以用“删除Mod…”删掉（没列出的在“删除没列出的重复 mod”里）",
					$"These {beside.Count} mod(s) have the package id of a mod that is here already but are other Workshop items (an original and its continuation, say), so each keeps a folder of its own instead of replacing the other, as on a PC subscribed to both. The game lists and loads only one of them and logs the other as a duplicate when it starts; delete the one you do not want with \"Delete mods...\" (the one not listed is under \"Delete the duplicates that are not listed\")"));
				text.Append(besideActive ? Say("。其中有正在使用的，重新启动游戏后运行的是 mod 页上列出的那一个；正在运行的那个如果不是列出的那个，要重新启动后才能删：\n", ". Some are in use: from the next start, the one the mod page lists is the one that runs; if the one running now is not the one listed, it can be deleted only after that start:\n") : Say("：\n", ":\n"));
				text.Append("  " + string.Join("\n  ", beside.ToArray()) + "\n\n");
			}
			if (imported.Count + updated.Count + beside.Count == 0 && problems.Count == 0)
			{
				text.Append(Say("选的内容里没有找到mod。mod是一个里面有 About 文件夹（其中有 About.xml）的文件夹。\n\n", "No mod was found in what was chosen. A mod is a folder with an About folder (holding About.xml) inside.\n\n"));
			}
			if (problems.Count > 0)
			{
				text.Append(Say("没能导入：\n", "Not imported:\n") + "  " + string.Join("\n  ", problems.ToArray()) + "\n\n");
			}
			if (outcome.PrepProblems.Count > 0)
			{
				text.Append(Say("这些mod已导入，但有程序集没能处理成手机能用的，启用后可能报错：\n", "These mods are imported, but some of their assemblies could not be prepared for the phone; they may fail when enabled:\n"));
				text.Append("  " + string.Join("\n  ", outcome.PrepProblems.ToArray()) + "\n\n");
			}
			if (leftOut.Trim().Length > 0)
			{
				text.Append(Say("不是 zip，没有处理（rar、7z 请先在别处解压，再用“从文件夹导入”）：\n", "Not zip archives, left alone (unpack rar or 7z elsewhere first, then import the folder):\n") + "  " + leftOut.Trim().Replace("\n", "\n  "));
			}
			Find.WindowStack.Add(new Dialog_MessageBox(text.ToString().TrimEnd()));
		}

		// What the mod screen does when it opens, minus what belongs to "changes since it was opened".
		private static void Refresh(string select)
		{
			// In a game (a mod's Workshop link opens the Workshop window there too) the list is left as it is, as on the PC,
			// where Steam's installs reach the list when the mod screen opens and reads the folders again.
			if (Current.ProgramState != ProgramState.Entry)
			{
				return;
			}
			// The main menu's update notice no longer names a mod deleted here, or installed anew (its .workshop is another
			// one, or there is none).
			UpdateNotice.ModsChanged();
			try
			{
				ModLister.RebuildModList();
				Page_ModsConfig page = Find.WindowStack.WindowOfType<Page_ModsConfig>();
				if (page == null)
				{
					return;
				}
				ModMetaData chosen = select == null ? null : ModLister.GetModWithIdentifier(select);
				var selected = AccessTools.Field(typeof(Page_ModsConfig), "selectedMods").GetValue(page) as List<ModMetaData>;
				selected?.Clear();
				if (chosen != null)
				{
					selected?.Add(chosen);
				}
				AccessTools.Field(typeof(Page_ModsConfig), "primarySelectedMod").SetValue(page, chosen);
				AccessTools.Field(typeof(Page_ModsConfig), "modListsDirty").SetValue(page, true);
				AccessTools.Method(typeof(Page_ModsConfig), "RecacheSelectedModInfo").Invoke(page, null);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mod import: the mod screen could not be refreshed: " + e);
			}
		}
	}

	/// <summary>
	/// What the Workshop download (workshop/PortWorkshop.dll, loaded by WorkshopLoader into a load context of its own)
	/// may use of the phone layer: installing the mods found in a folder exactly as the import does, and the report
	/// afterwards.
	/// </summary>
	public static class ModInstall
	{
		public static bool Chinese => Texts.Chinese;

		/// <summary>
		/// On a worker thread: every mod found in the folder goes into the game's Mods folder (replacing the one with the
		/// same package id, unless that is another Workshop item) and has its assemblies prepared. Returns the outcome for
		/// Report; a line for the user.
		/// </summary>
		public static object Install(string staging, string mods, Version game, out string summary)
		{
			var outcome = new ModImport.Outcome();
			ModImport.Install(outcome, mods, game, staging);
			summary = string.Join("; ", outcome.Problems.Concat(outcome.PrepProblems));
			return outcome;
		}

		/// <summary>The package id of the first mod the outcome put in place, if any.</summary>
		public static string FirstPackage(object outcome)
		{
			return (outcome as ModImport.Outcome)?.FirstPackage;
		}

		/// <summary>On the game's thread: the mod page shows the new mods, and a box says what happened in all the outcomes.</summary>
		public static void Report(IEnumerable<object> outcomes)
		{
			var all = new ModImport.Outcome();
			foreach (ModImport.Outcome one in outcomes.OfType<ModImport.Outcome>())
			{
				all.Imported.AddRange(one.Imported);
				all.Updated.AddRange(one.Updated);
				all.UpdatedIds.AddRange(one.UpdatedIds);
				all.Beside.AddRange(one.Beside);
				all.BesideIds.AddRange(one.BesideIds);
				all.Problems.AddRange(one.Problems);
				all.PrepProblems.AddRange(one.PrepProblems);
				all.FirstPackage = all.FirstPackage ?? one.FirstPackage;
			}
			ModImport.Report(all, "");
		}
	}
}
