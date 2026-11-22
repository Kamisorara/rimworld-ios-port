using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The camera's hole (the display cutout) covers no part of the interface; the picture still fills the screen.
	///
	/// The window is laid out over the hole (MainActivity: the map is drawn under it). Where the hole is, the activity
	/// reads from the system's insets for the screen's present turn (any phone: a hole in the middle of a side, in a
	/// corner, a notch, two holes; none on a phone with the camera under the screen) and the GLFW shim hands it over
	/// (glfwAndroidCutout, asked once a second). Only what would lie over the hole is moved, away from the edge the hole
	/// is at:
	///   - windows (the architect's menu, the main tabs, the inspect pane, float menus, dialogs), before the window stack
	///     draws them: moved aside, made narrower where there is no room;
	///   - the resource readout at the top left, and the column at the bottom right (time controls, date and weather,
	///     letters, alerts), as a whole, when they reach the hole;
	///   - what the architect's menu and the inspect pane draw outside their windows (their ExtraOnGUI), with them, when
	///     they were moved off a hole at the left: the grid of commands (it begins at a fixed x, 210 for the architect,
	///     the pane's width for a selection), the architect's info box above the menu, a tool's controls, the inspect
	///     pane's tabs (immediate windows at x 0). The user, 2026-10-23: the menu moved, the first commands were under
	///     it, and the info box stayed at the edge. The resource readout goes behind the moved info box with them;
	///   - an immediate window asked for while the windows are drawn (the inspect pane's tab), which the move of the
	///     windows comes too early for: when it is asked for;
	///   - the inspect pane's row of tabs, with the pane; and the fill the game draws in that row from the screen's edge
	///     to the first tab while a tab is open (it joins the tab's window to the row) begins where that window is. The
	///     user, 2026-10-24: with the tab moved off a hole at the left, the fill stayed at the edge as a black bar. Where
	///     that window is drawn counts: RimHUD's taller tab window, above the screen's top, is drawn smaller and centred
	///     on itself (WindowFit). RimHUD draws the row itself, in a prefix of DoTabs that skips the game's: its method is
	///     rewritten the same way when it is there;
	///   - the main menu's box of translators (left) and pane of buttons and links (right), drawn outside any window.
	/// Nothing on the map moves: the game and the mods place labels there by the camera's screen position.
	/// A black band beside the hole (the window kept clear of it, LAYOUT_IN_DISPLAY_CUTOUT_MODE_NEVER) was tried first and
	/// turned down by the user ("太难看了").
	/// Options, interface page: "避开摄像头挖孔", on by default, at once (mobile.txt avoidCutout=0 switches it off;
	/// cutoutMirror=1 puts the hole on the other side, for trying the right column without turning the phone over).
	/// </summary>
	internal static class Cutout
	{
		[DllImport("glfw")] private static extern int glfwAndroidCutout(byte[] buffer, int size);

		private const float Margin = 6f;

		private static bool on = Settings.Get("avoidCutout", true);
		// The holes in interface units (origin top left), with the margin around them.
		private static readonly List<Rect> holes = new List<Rect>();
		private static readonly byte[] buffer = new byte[512];
		private static string lastAsked;
		private static int nextPoll;
		private static float scaleAtPoll;
		private static AccessTools.FieldRef<ResourceReadout, float> readoutHeight;
		private static AccessTools.FieldRef<WindowStack, List<Window>> stackWindows;
		// Whether a hole is at the left or the right edge (the readout and the column are looked at only then), and the
		// column's top for this frame (the alerts' labels are measured for it: once a frame, not once a draw call).
		private static bool holeLeft, holeRight;
		private static int columnFrame = -1;
		private static float columnTop;
		// mobile.txt cutoutTiming=1: what this costs, logged every 1200 frames.
		private static readonly bool timing = Settings.Get("cutoutTiming", 0) == 1;
		private static long windowTicks, partTicks, askTicks, askMaxTicks;
		private static int asks, timedFrom = -1;
		// Whether each patched drawing call (in the order they run) opened a group; the finalizer closes it.
		private static readonly Stack<bool> groups = new Stack<bool>();
		private static bool movedSaid;
		// Where a window moved off a hole was before it was moved (its own place, to which the game puts it back when it opens).
		private static readonly ConditionalWeakTable<Window, StrongBox<float>> ownX = new ConditionalWeakTable<Window, StrongBox<float>>();
		// While the architect's menu or the inspect pane, moved right off a hole, draws what goes with it: how far it is
		// moved, and whether a grid of commands is being drawn (its immediate windows are the grid's: GizmoFit).
		private static float tabShift;
		private static int gridDepth;
		// Where the open inspect tab's window begins (after it was kept clear), and the shift of the row of tabs being drawn.
		private const int InspectTabWindowId = 235086;
		private static float openTabX, tabsShift;
		// The texture of the fill left of the tabs (the game's, which RimHUD reuses), and whether RimHUD's row of tabs was
		// looked for (it is loaded after the port starts).
		private static Texture tabFill;
		private static Harmony patcher;
		private static bool modRowsLooked;
		// Whether the mods that draw the architect's panel themselves were looked for, and how far such a panel is moved now.
		private static bool modPanelsLooked;
		private static float modPanelShift;
		// While the main menu draws itself (BeforeMainMenu).
		private static bool mainMenu;

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.WindowStackOnGUI)), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeWindows)));
				harmony.Patch(AccessTools.Method(typeof(Window), "InnerWindowOnGUI"), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeWindowInside)) { priority = Priority.First });
				HarmonyMethod end = new HarmonyMethod(typeof(Cutout), nameof(EndShift));
				harmony.Patch(AccessTools.Method(typeof(ResourceReadout), nameof(ResourceReadout.ResourceReadoutOnGUI)), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeReadout)), finalizer: end);
				HarmonyMethod right = new HarmonyMethod(typeof(Cutout), nameof(BeforeRightColumn));
				harmony.Patch(AccessTools.Method(typeof(GlobalControls), nameof(GlobalControls.GlobalControlsOnGUI)), prefix: right, finalizer: end);
				harmony.Patch(AccessTools.Method(typeof(RimWorld.Planet.WorldGlobalControls), nameof(RimWorld.Planet.WorldGlobalControls.WorldGlobalControlsOnGUI)), prefix: right, finalizer: end);
				harmony.Patch(AccessTools.Method(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutOnGUI)), prefix: right, finalizer: end);
				HarmonyMethod extras = new HarmonyMethod(typeof(Cutout), nameof(BeforeTabExtras)), extrasEnd = new HarmonyMethod(typeof(Cutout), nameof(AfterTabExtras));
				harmony.Patch(AccessTools.Method(typeof(MainTabWindow_Architect), nameof(MainTabWindow_Architect.ExtraOnGUI)), prefix: extras, finalizer: extrasEnd);
				harmony.Patch(AccessTools.Method(typeof(MainTabWindow_Inspect), nameof(MainTabWindow_Inspect.ExtraOnGUI)), prefix: extras, finalizer: extrasEnd);
				// Before GizmoFit's prefix, which lays the grid out from where it begins.
				harmony.Patch(AccessTools.Method(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGrid)), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeGrid)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Cutout), nameof(AfterGrid)));
				harmony.Patch(AccessTools.Method(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGridFor)), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeSelectionGrid)), finalizer: new HarmonyMethod(typeof(Cutout), nameof(AfterSelectionGrid)));
				harmony.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.ImmediateWindow)), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeImmediateWindow)) { priority = Priority.Last });
				// First: before a mod's prefix that draws the row in the game's place (RimHUD) and skips the rest.
				patcher = harmony;
				harmony.Patch(AccessTools.Method(typeof(InspectPaneUtility), "DoTabs"), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeTabs)) { priority = Priority.First }, finalizer: end, transpiler: new HarmonyMethod(typeof(Cutout), nameof(TabFillerTranspiler)));
				harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI)), prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeMainMenu)), finalizer: new HarmonyMethod(typeof(Cutout), nameof(AfterMainMenu)));
				HarmonyMethod menuPart = new HarmonyMethod(typeof(Cutout), nameof(BeforeMainMenuPart));
				harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls)), prefix: menuPart);
				harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), "DoTranslationInfoRect"), prefix: menuPart);
				readoutHeight = AccessTools.FieldRefAccess<ResourceReadout, float>("lastDrawnHeight");
				stackWindows = AccessTools.FieldRefAccess<WindowStack, List<Window>>("windows");
				Console.WriteLine("[mobile] cutout: the interface keeps clear of the camera's hole" + (on ? "" : " (switched off in the options)"));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] cutout: not patched: " + e.Message);
			}
		}

		/// <summary>About once a second, and when the interface scale changed: where the holes are now.</summary>
		private static void Poll()
		{
			float scale = Prefs.UIScale > 0f ? Prefs.UIScale : 1f;
			if (Time.frameCount < nextPoll && scale == scaleAtPoll)
			{
				return;
			}
			nextPoll = Time.frameCount + 60;
			scaleAtPoll = scale;
			string raw;
			try
			{
				long asked0 = Stopwatch.GetTimestamp();
				int length = glfwAndroidCutout(buffer, buffer.Length);
				long took = Stopwatch.GetTimestamp() - asked0;
				asks++;
				askTicks += took;
				askMaxTicks = Math.Max(askMaxTicks, took);
				raw = length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : "";
			}
			catch (Exception)
			{
				// An APK from before the shim had it: nothing to ask.
				raw = "";
				nextPoll = int.MaxValue;
			}
			string asked = raw + "@" + scale.ToString(CultureInfo.InvariantCulture);
			if (asked == lastAsked)
			{
				return;
			}
			lastAsked = asked;
			holes.Clear();
			foreach (string part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string[] edges = part.Split(',');
				if (edges.Length == 4 && int.TryParse(edges[0], out int left) && int.TryParse(edges[1], out int top) && int.TryParse(edges[2], out int rightEdge) && int.TryParse(edges[3], out int bottom))
				{
					if (Settings.Get("cutoutMirror", 0) == 1)
					{
						// Test: the hole on the other side, as with the phone turned over (mobile.txt cutoutMirror=1).
						int width = Screen.width;
						(left, rightEdge) = (width - rightEdge, width - left);
					}
					holes.Add(new Rect(left / scale - Margin, top / scale - Margin, (rightEdge - left) / scale + 2f * Margin, (bottom - top) / scale + 2f * Margin));
				}
			}
			holeLeft = holes.Any(h => h.center.x < UI.screenWidth * 0.3f);
			holeRight = holes.Any(h => h.center.x > UI.screenWidth * 0.7f);
			Console.WriteLine("[mobile] cutout: " + (holes.Count == 0 ? "no hole" : string.Join(", ", holes.Select(h => "(" + h.xMin.ToString("0") + "," + h.yMin.ToString("0") + ")-(" + h.xMax.ToString("0") + "," + h.yMax.ToString("0") + ")")) + " in interface units of " + UI.screenWidth + "x" + UI.screenHeight));
		}

		/// <summary>
		/// Moves rect off the holes it overlaps, away from the edge each hole is at; narrower (or lower) where there is no
		/// room. Whether it changed. To whole units: the game rounds a window's rect when it draws it (Window.WindowOnGUI),
		/// which put it a fraction of a unit back over the hole, to be moved again the next time.
		/// </summary>
		private static bool KeepClear(ref Rect rect)
		{
			bool changed = false;
			float width = UI.screenWidth, height = UI.screenHeight;
			foreach (Rect hole in holes)
			{
				if (!rect.Overlaps(hole))
				{
					continue;
				}
				changed = true;
				if (hole.center.x < width * 0.3f)
				{
					float edge = Mathf.Ceil(hole.xMax), need = edge - rect.xMin, room = Mathf.Max(0f, width - rect.xMax);
					rect.x += Mathf.Min(need, room);
					if (need > room)
					{
						rect.xMin = edge;
					}
				}
				else if (hole.center.x > width * 0.7f)
				{
					float edge = Mathf.Floor(hole.xMin), need = rect.xMax - edge, room = Mathf.Max(0f, rect.xMin);
					rect.x -= Mathf.Min(need, room);
					if (need > room)
					{
						rect.xMax = edge;
					}
				}
				else if (hole.center.y < height * 0.5f)
				{
					float edge = Mathf.Ceil(hole.yMax), need = edge - rect.yMin, room = Mathf.Max(0f, height - rect.yMax);
					rect.y += Mathf.Min(need, room);
					if (need > room)
					{
						rect.yMin = edge;
					}
				}
				else
				{
					float edge = Mathf.Floor(hole.yMin), need = rect.yMax - edge, room = Mathf.Max(0f, rect.yMin);
					rect.y -= Mathf.Min(need, room);
					if (need > room)
					{
						rect.yMax = edge;
					}
				}
			}
			return changed;
		}

		/// <summary>
		/// A window moved right off a hole at the left that has made itself as wide as the screen again: narrower again, as
		/// when it was moved. The research tab sets its width to the screen's in every DoWindowContents and keeps the place
		/// it was moved to; clear of the hole then, it stuck out at the right, and WindowFit drew all of it smaller, with a
		/// gap above the bottom bar (the user, 2026-10-25: "研究界面发生了偏移").
		/// </summary>
		private static bool NarrowAgain(Window window, ref Rect rect)
		{
			float width = UI.screenWidth;
			if (!holeLeft || rect.xMax <= width + 0.5f || rect.width > width + 0.5f || !ownX.TryGetValue(window, out StrongBox<float> own) || rect.x <= own.Value + 0.5f)
			{
				return false;
			}
			rect.xMax = width;
			return true;
		}

		/// <summary>Prefix of WindowStack.WindowStackOnGUI: every window that would lie over a hole is moved before it is drawn (and hit).</summary>
		public static void BeforeWindows(WindowStack __instance)
		{
			if (!on)
			{
				if (narrowedTables.Count > 0 && stackWindows != null)
				{
					FitTables(stackWindows(__instance));
				}
				return;
			}
			long start = timing ? Stopwatch.GetTimestamp() : 0;
			Poll();
			List<Window> windows = holes.Count > 0 && stackWindows != null ? stackWindows(__instance) : null;
			if (windows != null || narrowedTables.Count > 0)
			{
				FitTables(windows ?? stackWindows(__instance));
			}
			for (int i = 0; windows != null && i < windows.Count; i++)
			{
				MoveOff(windows[i]);
			}
			if (timing)
			{
				windowTicks += Stopwatch.GetTimestamp() - start;
				TimingReport();
			}
		}

		private static readonly AccessTools.FieldRef<MainTabWindow_PawnTable, PawnTable> TableOf = AccessTools.FieldRefAccess<MainTabWindow_PawnTable, PawnTable>("table");
		private static readonly AccessTools.FieldRef<PawnTable, int> TableMinWidth = AccessTools.FieldRefAccess<PawnTable, int>("minTableWidth");
		private static readonly AccessTools.FieldRef<PawnTable, int> TableMaxWidth = AccessTools.FieldRefAccess<PawnTable, int>("maxTableWidth");
		private static readonly AccessTools.FieldRef<PawnTable, int> TableMinHeight = AccessTools.FieldRefAccess<PawnTable, int>("minTableHeight");
		private static readonly AccessTools.FieldRef<PawnTable, int> TableMaxHeight = AccessTools.FieldRefAccess<PawnTable, int>("maxTableHeight");
		private static readonly Action<MainTabWindow_PawnTable> TableTabDirty = AccessTools.MethodDelegate<Action<MainTabWindow_PawnTable>>(AccessTools.Method(typeof(MainTabWindow_PawnTable), "SetDirty"));
		// The tables made narrower than the game made them, with the width the game gave them.
		private static readonly Dictionary<PawnTable, int> narrowedTables = new Dictionary<PawnTable, int>();

		/// <summary>
		/// The pawn tables (work, schedule, assign, animals, wildlife, mechs; and mods' tables in such a window) as wide as
		/// the room beside the holes. The game makes such a table at most as wide as the screen, whatever the window it is
		/// in, and the window as wide as the table: one as wide as the screen was made narrower here, and its right
		/// columns were cut off (2x on this phone: the work tab's research and the columns after it). A table that fits
		/// the room is left as it is; one made narrower gets its width back when the hole no longer reaches its window.
		/// </summary>
		private static void FitTables(List<Window> windows)
		{
			for (int i = 0; i < windows.Count; i++)
			{
				if (!(windows[i] is MainTabWindow_PawnTable tab) || !(TableOf(tab) is PawnTable table))
				{
					continue;
				}
				narrowedTables.TryGetValue(table, out int own);
				int gameMax = own > 0 ? own : TableMaxWidth(table);
				int want = Mathf.Min(gameMax, (int)(UI.screenWidth - Blocked(tab.windowRect)) - (UI.screenWidth - gameMax));
				if (want >= gameMax)
				{
					if (own > 0)
					{
						narrowedTables.Remove(table);
						Resize(tab, table, own);
					}
					continue;
				}
				if (TableMaxWidth(table) != want)
				{
					narrowedTables[table] = gameMax;
					Resize(tab, table, want);
				}
			}
		}

		private static void Resize(MainTabWindow_PawnTable tab, PawnTable table, int maxWidth)
		{
			table.SetMinMaxSize(Mathf.Min(TableMinWidth(table), maxWidth), maxWidth, TableMinHeight(table), TableMaxHeight(table));
			// The window takes the table's new size, at the game's place for it (moved off the holes next).
			TableTabDirty(tab);
		}

		/// <summary>How much of the screen's width the holes at its sides take beside a window's rows (0 while switched off).</summary>
		private static float Blocked(Rect rect)
		{
			if (!on)
			{
				return 0f;
			}
			float left = 0f, right = 0f, width = UI.screenWidth;
			foreach (Rect hole in holes)
			{
				if (hole.yMax <= rect.yMin || hole.yMin >= rect.yMax)
				{
					continue;
				}
				if (hole.center.x < width * 0.3f)
				{
					left = Mathf.Max(left, Mathf.Ceil(hole.xMax));
				}
				else if (hole.center.x > width * 0.7f)
				{
					right = Mathf.Max(right, width - Mathf.Floor(hole.xMin));
				}
			}
			return left + right;
		}

		private static void MoveOff(Window window)
		{
			Rect rect = window.windowRect;
			if (KeepClear(ref rect) || NarrowAgain(window, ref rect))
			{
				if (!ownX.TryGetValue(window, out StrongBox<float> own) || window.windowRect.x <= own.Value + 0.5f)
				{
					ownX.AddOrUpdate(window, new StrongBox<float>(window.windowRect.x));
				}
				window.windowRect = rect;
				if (!movedSaid)
				{
					movedSaid = true;
					Console.WriteLine("[mobile] cutout: " + window.GetType().Name + " moved off the hole");
				}
			}
		}

		/// <summary>
		/// Prefix of Window.InnerWindowOnGUI: the same before the window's own drawing. The engine hands a press to the window
		/// under the mouse before the game's window pass (GUI.DispatchInputToWindows), so BeforeWindows has not run for it,
		/// and a window that makes itself as wide as the screen in DoWindowContents (the research tab) took the press at that
		/// width: its tabs were laid out wider than drawn, the last row was gone, and a tap chose the tab beside the one under
		/// the finger or none (the user, 2026-10-26: "改变ui缩放后，研究的第三栏就无法点击了").
		/// </summary>
		public static void BeforeWindowInside(Window __instance)
		{
			if (on && holes.Count > 0)
			{
				MoveOff(__instance);
			}
		}

		/// <summary>For what places itself (the key wheel's button): rect moved off the holes, as a window would be.</summary>
		public static Rect ClearOf(Rect rect)
		{
			if (on && holes.Count > 0)
			{
				KeepClear(ref rect);
			}
			return rect;
		}

		/// <summary>Whether rect lies over a hole (with the margin around it).</summary>
		public static bool OverHole(Rect rect)
		{
			if (!on)
			{
				return false;
			}
			foreach (Rect hole in holes)
			{
				if (rect.Overlaps(hole))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>Prefix of MainTabWindow_Architect.ExtraOnGUI and MainTabWindow_Inspect.ExtraOnGUI: how far the window is moved off a hole at the left.</summary>
		public static void BeforeTabExtras(Window __instance)
		{
			if (!modPanelsLooked)
			{
				modPanelsLooked = true;
				PatchModPanels();
			}
			tabShift = ShiftOf(__instance);
			gridDepth = 0;
		}

		/// <summary>
		/// Better Architect Menu draws the category's panel itself (a prefix of ArchitectCategoryTab.DesignationTabOnGUI that
		/// skips the game's), straight onto the screen from the architect's width on, as wide as the screen leaves: with the
		/// menu moved off a hole at the left, the panel's first column lay under the menu (the user, 2026-10-26: "避开摄像头
		/// 挖孔会导致Better Architect Menu显示错位"). Its drawing goes as far right as the menu went, and the screen's
		/// width it reads (its ExtensionMethods.UIWidth, there for patches like this) is that much less, so that it still
		/// ends short of the right column. Its info box and a tool's controls are immediate windows, moved as the game's are.
		/// Vanilla Gravship Expanded draws the Odyssey category's panel with its own copy of Better Architect Menu's (only
		/// while that mod is not active), placed the same way, from the game's UI.screenWidth (read in DrawBetterArchitectMenu):
		/// it goes with the menu the same way.
		/// </summary>
		private static void PatchModPanels()
		{
			try
			{
				Type bam = AccessTools.TypeByName("BetterArchitect.ArchitectCategoryTab_DesignationTabOnGUI_Patch");
				MethodInfo prefix = bam != null ? AccessTools.Method(bam, "Prefix") : null;
				if (prefix != null)
				{
					patcher.Patch(prefix, prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeModPanel)), finalizer: new HarmonyMethod(typeof(Cutout), nameof(AfterModPanel)), transpiler: new HarmonyMethod(typeof(Cutout), nameof(ModWidthTranspiler)));
					Console.WriteLine("[mobile] cutout: Better Architect Menu's panel goes with the menu");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] cutout: a mod's architect panel left as it is: " + e.Message);
			}
			try
			{
				// Its prefix of the game's method, put in only while Better Architect Menu is not active (its Prepare), is looked
				// for among that method's patches (AccessTools.TypeByName, finding nothing, goes through every type of every
				// assembly loaded: for every player without the mod, at the first architect or inspect pane).
				Patches tab = Harmony.GetPatchInfo(AccessTools.Method(typeof(ArchitectCategoryTab), nameof(ArchitectCategoryTab.DesignationTabOnGUI)));
				MethodInfo prefix = tab?.Prefixes.Select(p => p.PatchMethod).FirstOrDefault(m => m.Name == "Prefix" && m.DeclaringType?.FullName == "VanillaGravshipExpanded.ArchitectCategoryTab_DesignationTabOnGUI_Patch");
				MethodInfo draw = prefix != null ? AccessTools.Method(prefix.DeclaringType, "DrawBetterArchitectMenu") : null;
				if (draw != null)
				{
					// The drawing goes with the menu, not the prefix that calls it: the prefix is small enough for the compiler to
					// copy it into the game's method when VGE patches that at start, where a patch of it would not be reached
					// (InlineGuard looks only at the calls of the game's own code). The drawing is far too large to be copied.
					patcher.Patch(draw, prefix: new HarmonyMethod(typeof(Cutout), nameof(BeforeModPanel)), finalizer: new HarmonyMethod(typeof(Cutout), nameof(AfterModPanel)), transpiler: new HarmonyMethod(typeof(Cutout), nameof(ScreenWidthTranspiler)));
					Console.WriteLine("[mobile] cutout: Vanilla Gravship Expanded's Odyssey panel goes with the menu");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] cutout: Vanilla Gravship Expanded's architect panel left as it is: " + e.Message);
			}
		}

		public static void BeforeModPanel()
		{
			modPanelShift = tabShift;
			Push(tabShift);
		}

		public static void AfterModPanel()
		{
			modPanelShift = 0f;
			EndShift();
		}

		/// <summary>Transpiler of Better Architect Menu's panel: the screen's width it reads (ExtensionMethods.UIWidth) is ModPanelWidth.</summary>
		public static IEnumerable<CodeInstruction> ModWidthTranspiler(IEnumerable<CodeInstruction> instructions)
		{
			MethodInfo ours = AccessTools.Method(typeof(Cutout), nameof(ModPanelWidth));
			foreach (CodeInstruction instruction in instructions)
			{
				if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo called
					&& called.Name == "get_UIWidth" && called.DeclaringType?.FullName == "BetterArchitect.ExtensionMethods")
				{
					instruction.opcode = OpCodes.Call;
					instruction.operand = ours;
				}
				yield return instruction;
			}
		}

		public static float ModPanelWidth()
		{
			return UI.screenWidth - modPanelShift;
		}

		/// <summary>Transpiler of a copy of the panel that reads the game's UI.screenWidth: it reads ModPanelScreenWidth.</summary>
		public static IEnumerable<CodeInstruction> ScreenWidthTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			FieldInfo screenWidth = AccessTools.Field(typeof(UI), nameof(UI.screenWidth));
			MethodInfo ours = AccessTools.Method(typeof(Cutout), nameof(ModPanelScreenWidth));
			int replaced = 0;
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.opcode == OpCodes.Ldsfld && Equals(instruction.operand, screenWidth))
				{
					instruction.opcode = OpCodes.Call;
					instruction.operand = ours;
					replaced++;
				}
				yield return instruction;
			}
			if (replaced == 0)
			{
				Console.WriteLine("[mobile] cutout: " + original?.DeclaringType?.Name + "." + original?.Name + " no longer reads UI.screenWidth");
			}
		}

		/// <summary>The screen's width (whole units, as UI.screenWidth is) less how far the panel is moved.</summary>
		public static int ModPanelScreenWidth()
		{
			return UI.screenWidth - Mathf.CeilToInt(modPanelShift);
		}

		/// <summary>How far an open window is moved right off a hole at the left (0: not moved, or not open).</summary>
		private static float ShiftOf(Window window)
		{
			if (!on || !holeLeft || window == null || !ownX.TryGetValue(window, out StrongBox<float> own) || !window.IsOpen)
			{
				return 0f;
			}
			float dx = window.windowRect.x - own.Value;
			return dx > 0.5f ? dx : 0f;
		}

		public static void AfterTabExtras()
		{
			tabShift = 0f;
			gridDepth = 0;
		}

		/// <summary>Prefix of GizmoGridDrawer.DrawGizmoGrid (first of its prefixes): the grid begins as much further right.</summary>
		public static void BeforeGrid(ref float startX)
		{
			if (tabShift > 0f)
			{
				startX += tabShift;
				gridDepth++;
			}
		}

		/// <summary>
		/// Prefix of GizmoGridDrawer.DrawGizmoGridFor: the selection's commands (drawn by the map's and the world's
		/// interface, not by the pane) begin at the inspect pane's width; with the pane moved right off a hole at the left
		/// (RimHUD's taller pane at a large scale reaches the hole) they go as much further right.
		/// </summary>
		public static void BeforeSelectionGrid(out float __state)
		{
			__state = tabShift;
			if (tabShift == 0f)
			{
				tabShift = ShiftOf(Find.WindowStack?.WindowOfType<IInspectPane>() as Window);
			}
		}

		public static void AfterSelectionGrid(float __state)
		{
			tabShift = __state;
		}

		public static void AfterGrid()
		{
			if (gridDepth > 0)
			{
				gridDepth--;
			}
		}

		/// <summary>
		/// Prefix of WindowStack.ImmediateWindow (the last of its prefixes, after GizmoFit's): the info box, a tool's controls
		/// and the inspect pane's tabs go with their window (not the grid's own: they are where the grid is). And an immediate
		/// window keeps clear of the holes itself: one asked for while the window stack draws (a window's ExtraOnGUI: the
		/// inspect pane's tab, a selection's commands) is drawn right after, before BeforeWindows could see it.
		/// </summary>
		public static void BeforeImmediateWindow(int ID, ref Rect rect)
		{
			if (tabShift > 0f && gridDepth == 0)
			{
				rect.x += tabShift;
			}
			if (on && holes.Count > 0)
			{
				KeepClear(ref rect);
			}
			if (ID == InspectTabWindowId)
			{
				// Where it is drawn: a window above the screen's top is drawn smaller (RimHUD's tab, under its taller pane).
				openTabX = WindowFit.Drawn(rect).x;
			}
		}

		/// <summary>Prefix of InspectPaneUtility.DoTabs (the finalizer is EndShift): the row of tabs goes with a moved pane.</summary>
		public static void BeforeTabs()
		{
			if (!modRowsLooked)
			{
				modRowsLooked = true;
				PatchModRows();
			}
			tabsShift = tabShift;
			openTabX = 0f;
			Push(tabShift);
		}

		/// <summary>The rows of tabs mods draw in the game's place: RimHUD's (InspectPaneTabs.Draw, from its prefix of DoTabs).</summary>
		private static void PatchModRows()
		{
			try
			{
				tabFill = AccessTools.Field(typeof(InspectPaneUtility), "InspectTabButtonFillTex")?.GetValue(null) as Texture;
				Type rimHud = AccessTools.TypeByName("RimHUD.Interface.Screen.InspectPaneTabs");
				MethodInfo draw = rimHud != null ? AccessTools.Method(rimHud, "Draw") : null;
				if (draw != null)
				{
					patcher.Patch(draw, transpiler: new HarmonyMethod(typeof(Cutout), nameof(TabFillerTranspiler)));
					Console.WriteLine("[mobile] cutout: RimHUD's row of tabs fills from the open tab's window too");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] cutout: a mod's row of tabs left as it is: " + e.Message);
			}
		}

		/// <summary>Transpiler of a row of tabs (the game's DoTabs, RimHUD's): its GUI.DrawTexture calls are DrawTabFiller.</summary>
		public static IEnumerable<CodeInstruction> TabFillerTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			MethodInfo draw = AccessTools.Method(typeof(GUI), nameof(GUI.DrawTexture), new[] { typeof(Rect), typeof(Texture) });
			MethodInfo filler = AccessTools.Method(typeof(Cutout), nameof(DrawTabFiller));
			int replaced = 0;
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.Calls(draw))
				{
					instruction.opcode = OpCodes.Call;
					instruction.operand = filler;
					replaced++;
				}
				yield return instruction;
			}
			if (replaced != 1)
			{
				Console.WriteLine("[mobile] cutout: " + original?.DeclaringType?.Name + "." + original?.Name + " has " + replaced + " texture draws where 1 was expected");
			}
		}

		/// <summary>The fill from the screen's edge to the first tab, from where the open tab's window is drawn.</summary>
		public static void DrawTabFiller(Rect rect, Texture image)
		{
			if (tabFill == null || (object)image == tabFill)
			{
				float from = openTabX - tabsShift;
				if (from > rect.xMin)
				{
					rect.xMin = Mathf.Min(from, rect.xMax);
				}
			}
			if (rect.width > 0f)
			{
				GUI.DrawTexture(rect, image);
			}
		}

		/// <summary>Prefix of ResourceReadout.ResourceReadoutOnGUI: to the right of a hole at the left edge the list reaches.</summary>
		/// <summary>
		/// The main menu is drawn outside any window (MainMenuDrawer.MainMenuOnGUI): the box of translators at the left (8,
		/// 100, 300 x 400; a hole in the middle of the left side covered its lower part and its buttons, a tester's OPPO
		/// 2026-10-29) and the pane of buttons and links at the right (where the hole is with the phone turned over) are kept
		/// clear as a window is. DoMainMenuControls also draws the game's menu tab, inside that window (which is moved as a
		/// window): only the main menu's own call is moved.
		/// </summary>
		public static void BeforeMainMenu()
		{
			mainMenu = true;
		}

		public static void AfterMainMenu()
		{
			mainMenu = false;
		}

		public static void BeforeMainMenuPart(ref Rect __0)
		{
			if (on && mainMenu && holes.Count > 0)
			{
				KeepClear(ref __0);
			}
		}

		public static void BeforeReadout(ResourceReadout __instance)
		{
			long start = timing ? Stopwatch.GetTimestamp() : 0;
			float dx = 0f;
			if (on && holeLeft)
			{
				float drawn = readoutHeight != null ? readoutHeight(__instance) : UI.screenHeight;
				Rect area = new Rect(0f, 0f, 130f, 7f + Mathf.Max(drawn, 30f));
				foreach (Rect hole in holes)
				{
					if (hole.center.x < UI.screenWidth * 0.3f && area.Overlaps(hole))
					{
						dx = Mathf.Max(dx, Mathf.Ceil(hole.xMax));
					}
				}
				// The architect's info box lies over the list (as in the game); moved with the menu, the list goes along
				// behind it rather than show its left edge beside it.
				dx = Mathf.Max(dx, ShiftOf(MainButtonDefOf.Architect?.TabWindow));
			}
			Push(dx);
			if (timing)
			{
				partTicks += Stopwatch.GetTimestamp() - start;
			}
		}

		/// <summary>
		/// Prefix of the bottom right column's parts (GlobalControls, with the letters, and AlertsReadout above them): to the
		/// left of a hole at the right edge, all of them together, when the column (as high as it was the frame before) reaches it.
		/// </summary>
		public static void BeforeRightColumn()
		{
			long start = timing ? Stopwatch.GetTimestamp() : 0;
			float dx = 0f;
			if (on && holeRight && Current.ProgramState == ProgramState.Playing)
			{
				if (columnFrame != Time.frameCount)
				{
					columnFrame = Time.frameCount;
					columnTop = 0f;
					try
					{
						columnTop = Find.LetterStack.LastTopY - Find.Alerts.AlertsHeight;
					}
					catch (Exception)
					{
					}
				}
				float top = columnTop;
				Rect area = new Rect(UI.screenWidth - 200f, top, 200f, UI.screenHeight - top);
				foreach (Rect hole in holes)
				{
					if (hole.center.x > UI.screenWidth * 0.7f && area.Overlaps(hole))
					{
						dx = Mathf.Min(dx, hole.xMin - UI.screenWidth);
					}
				}
			}
			Push(dx);
			if (timing)
			{
				partTicks += Stopwatch.GetTimestamp() - start;
			}
		}

		private static void TimingReport()
		{
			int frame = Time.frameCount;
			if (timedFrom < 0)
			{
				timedFrom = frame;
			}
			int frames = frame - timedFrom;
			if (frames < 1200)
			{
				return;
			}
			double us = 1e6 / Stopwatch.Frequency;
			Console.WriteLine("[mobile] cutout timing over " + frames + " frames: windows " + (windowTicks * us / frames).ToString("0.0", CultureInfo.InvariantCulture) + " us a frame, readout and column "
				+ (partTicks * us / frames).ToString("0.0", CultureInfo.InvariantCulture) + " us a frame; " + asks + " asks to the activity, " + (asks > 0 ? (askTicks * us / asks).ToString("0", CultureInfo.InvariantCulture) : "0") + " us each, longest "
				+ (askMaxTicks * us).ToString("0", CultureInfo.InvariantCulture) + " us");
			timedFrom = frame;
			windowTicks = partTicks = askTicks = askMaxTicks = 0;
			asks = 0;
		}

		private static void Push(float dx)
		{
			bool shift = dx != 0f;
			if (shift)
			{
				GUI.BeginGroup(new Rect(dx, 0f, UI.screenWidth, UI.screenHeight));
			}
			groups.Push(shift);
		}

		/// <summary>Finalizer of the shifted drawing calls: the group their prefix opened is closed, whatever happened inside.</summary>
		public static void EndShift()
		{
			if (groups.Count > 0 && groups.Pop())
			{
				GUI.EndGroup();
			}
		}

		public static void OptionRow(Listing_Standard listing)
		{
			bool check = on;
			listing.CheckboxLabeled(Texts.Chinese ? "避开摄像头挖孔" : "Keep clear of the camera cutout", ref check, Texts.Chinese
				? "画面仍然铺满屏幕，但会和摄像头挖孔重叠的界面（窗口、左上角资源、右下角时间和信件等）会自动挪开。挖孔的位置由手机系统提供，手机翻转时跟着换边。"
				: "The picture still fills the screen, but the parts of the interface that would lie over the camera's hole (windows, the resources at the top left, the time controls and letters at the bottom right) move aside. Where the hole is comes from the phone; it changes side when the phone is turned over.", 30f, 0.6f);
			if (check != on)
			{
				on = check;
				Settings.Set("avoidCutout", on ? "1" : "0");
			}
		}
	}
}
