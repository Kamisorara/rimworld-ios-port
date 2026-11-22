using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Compatibility mode: the interface as on a PC, with only the touch layer kept (the user, 2026-10-27, on the UI mods
	/// that the phone's own changes to the interface may collide with). What fits the interface to the phone is left out
	/// at the start (it takes a restart): windows and grids made smaller to fit (WindowFit, GizmoFit, GearTabFit), the
	/// camera hole (Cutout), the larger menu rows and time buttons (TouchSizes), the main button bar's inset from the
	/// round corners (BarInset), the research tree's vertical scrolling, the faction list's scrollbar room, the Mint
	/// wheel moved above the bar. What makes a finger work stays: taps, drags, holds, pinches, list swiping, tap assist,
	/// the confirm buttons of map tools, the key wheel and the mouse buttons, the soft keyboard and its typing bar, and
	/// the port's own options (where this switch is). For a player whose mods collide with the port's changes: the game
	/// is playable at once, and the record says which mods were running. mobile.txt compatMode=1.
	/// </summary>
	internal static class Compat
	{
		/// <summary>Read once, at the start: what it leaves out is decided before anything is patched.</summary>
		public static readonly bool On = Settings.Get("compatMode", false);

		private static bool chosen = On;

		public static void OptionRow(Listing_Standard listing)
		{
			bool check = chosen;
			listing.CheckboxLabeled(Texts.Chinese ? "兼容模式（重启后生效）" : "Compatibility mode (after a restart)", ref check, Texts.Chinese
				? "关掉移植层对界面布局做的所有手机适配，界面和电脑版完全一样，只保留触控操作（点击、拖动、长按、双指缩放、列表滑动、确定/取消按钮、自定义按键盘、鼠标左右键、屏幕键盘）。\n\n装了界面类 mod 后出现错位、点不动、界面消失时，先打开它试试：如果打开后正常，说明是移植层和那个 mod 冲突，请把日志发给我们。\n\n打开后：避开摄像头挖孔、太大的窗口自动缩小、命令栏重排、放大的菜单行距和时间按钮、主按钮栏避开圆角、研究树上下滚动都会关闭。界面缩放仍按你的设置，窗口放不下时请把界面缩放调小。"
				: "Leaves out every change the port makes to the interface's layout for the phone: the interface is the PC's, with only the touch handling kept (taps, drags, holds, pinches, list swiping, confirm buttons, the key wheel, the mouse buttons, the soft keyboard).\n\nWhen a UI mod is misplaced, cannot be tapped or makes the interface vanish, try this first: if it is fine with this on, the port and that mod collide; please send us the log.\n\nWith it on: no camera hole avoidance, no shrinking of windows too large for the screen, no command grid rearranging, no larger menu rows and time buttons, no main bar inset from the round corners, no vertical research tree scrolling. The interface scale stays yours; lower it when a window does not fit.", 30f, 0.6f);
			if (check == chosen)
			{
				return;
			}
			chosen = check;
			Settings.Set("compatMode", chosen ? "1" : "0");
			Console.WriteLine("[mobile] compatibility mode " + (chosen ? "chosen" : "left") + "; after a restart");
			if (chosen == On)
			{
				return;
			}
			Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(Texts.Chinese
				? (chosen ? "兼容模式要重启游戏后才生效。现在重启吗？（没保存的进度会丢失）" : "关掉兼容模式要重启游戏后才生效。现在重启吗？（没保存的进度会丢失）")
				: (chosen ? "Compatibility mode takes effect after a restart. Restart now? (Unsaved progress is lost.)" : "Leaving compatibility mode takes effect after a restart. Restart now? (Unsaved progress is lost.)"),
				GenCommandLine.Restart, Current.ProgramState == ProgramState.Playing));
		}

		/// <summary>The line in the record and the log at the start.</summary>
		public static string Describe()
		{
			return On ? "compatibility mode: the interface's layout as on a PC (window and grid fitting, camera hole, touch sizes, bar inset, research and faction list changes left out)" : null;
		}
	}
}
