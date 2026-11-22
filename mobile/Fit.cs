using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Brutal.Numerics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The interface scale on the phone: fixed, from mobile.txt ("uiScale=1.85"; 1.85 is where the Architect menu
	/// just fits the screen height). The game's options dialog can still change it: the new value is kept and
	/// written back. Nothing rescales by itself while playing (the user wants the interface to hold still);
	/// a window that does not fit is drawn smaller on its own (WindowFit).
	/// No limits (the user's choice, 2026-10-20, after testers still could not change it): every scale of the list is
	/// taken whatever the screen, from 0.5 up; the game's own "keep / revert" confirmation is the safety net (not kept
	/// within its countdown, the old scale comes back).
	/// </summary>
	internal static class UiScale
	{
		private static readonly string FilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "mobile.txt");
		private static float preferred = 1.85f;
		private static float lastSet;

		public static void Start(Harmony harmony)
		{
			try
			{
				if (File.Exists(FilePath))
				{
					foreach (string line in File.ReadAllLines(FilePath))
					{
						string[] parts = line.Split('=');
						if (parts.Length == 2 && parts[0].Trim() == "uiScale" && float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
						{
							preferred = Mathf.Clamp(value, MinScale, MaxScale);
						}
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] uiScale not read: " + e.Message);
			}
			OfferInOptions();
			// 1.7.4980 caps the scale at what keeps a 1024 x 768 window on the screen (ResolutionUtility.ResolveUIScale:
			// at most the larger of the smallest choice and min(width / 1024, height / 768)), 1.66 on this screen: the
			// phone's 1.85 was cut down, and Update below would have taken that for the player's choice and saved it.
			// Here the windows that do not fit are made smaller one by one (WindowFit), so the scale is the one set.
			MethodInfo resolve = AccessTools.Method(typeof(ResolutionUtility), "ResolveUIScale");
			if (resolve != null)
			{
				harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(UiScale), nameof(Uncapped)));
			}
			MethodInfo safe = AccessTools.Method(typeof(ResolutionUtility), "UIScaleSafeWithResolution");
			if (safe != null)
			{
				harmony.Patch(safe, prefix: new HarmonyMethod(typeof(UiScale), nameof(FitsWithWindowFit)));
			}
			harmony.Patch(AccessTools.Method(typeof(ResolutionUtility), nameof(ResolutionUtility.Update)), prefix: new HarmonyMethod(typeof(UiScale), nameof(WithoutScaleReset)));
			if (Compat.On)
			{
				// Compatibility mode: windows and grids as on a PC (the scale stays the player's, never changed by itself).
				Console.WriteLine("[mobile] UI scale " + preferred.ToString("F2") + "; compatibility mode: windows and grids are not fitted to the screen");
				return;
			}
			harmony.Patch(AccessTools.Method(typeof(Window), nameof(Window.WindowOnGUI)), prefix: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.Before)), finalizer: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.After)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.DrawShadowAround)), prefix: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.BeforeShadow)));
			GizmoFit.Start(harmony);
			GearTabFit.Start(harmony);
			harmony.Patch(AccessTools.Method(typeof(UI), nameof(UI.RotateAroundPivot)), prefix: new HarmonyMethod(typeof(RotateInFit), nameof(RotateInFit.Before)));
			try
			{
				Type backend = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImGuiGuiBackend");
				harmony.Patch(AccessTools.Method(backend, "GuiToScreenPoint"), postfix: new HarmonyMethod(typeof(WindowFit), nameof(WindowFit.AfterGuiToScreen)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] window fit: positions inside a fitted window are left as the engine gives them: " + e.Message);
			}
			Console.WriteLine("[mobile] UI scale " + preferred.ToString("F2") + " (" + Screen.width + "x" + Screen.height + " -> " + Mathf.RoundToInt(Screen.width / preferred) + "x" + Mathf.RoundToInt(Screen.height / preferred) + ")");
		}

		/// <summary>The phone's default scale.</summary>
		private const float PhoneDefault = 1.85f;

		/// <summary>Only against nonsense (0, a typo in mobile.txt): the scale itself is the player's.</summary>
		private const float MinScale = 0.5f, MaxScale = 5f;

		/// <summary>Steps between the game's choices (0.75, 1, 1.25, 1.5, 1.75, 2, 2.5...), for a screen they all miss.</summary>
		private static readonly float[] FinerSteps = { 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.1f, 1.2f, 1.3f, 1.4f, 1.6f, 1.7f, 1.8f, 1.9f, 2.1f, 2.2f, 2.25f, 2.3f, 2.4f, 2.75f };

		/// <summary>
		/// The choices of the options dialog: the game's, the finer steps above, the phone's default (1.85) and the scale in
		/// use, whatever was chosen since (once a smaller scale was chosen, 1.85 was no longer in the list: it was put in
		/// only while it was the scale in use).
		/// </summary>
		private static void OfferInOptions()
		{
			try
			{
				float[] scales = Dialog_Options.UIScales;
				if (scales == null || scales.Length < 2)
				{
					return;
				}
				var list = new System.Collections.Generic.List<float>(scales);
				var extras = new System.Collections.Generic.List<float>(FinerSteps) { PhoneDefault, preferred };
				foreach (float extra in extras)
				{
					if (!list.Exists(s => Mathf.Abs(s - extra) < 0.001f))
					{
						list.Add(extra);
					}
				}
				list.Sort();
				Dialog_Options.UIScales = list.ToArray();
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] could not add the phone's UI scale to the options: " + e.Message);
			}
		}

		/// <summary>
		/// Prefix of ResolutionUtility.UIScaleSafeWithResolution, which the options dialog asks before it takes a scale: the
		/// game wants 1024 x 768 units left on the screen, at most 1.66 on a phone 1272 pixels high (1.4 on one 1080 high),
		/// so a player who chose a smaller scale could not go back ("screen resolution too small"). Every scale is taken:
		/// a window that does not fit is drawn smaller on its own (WindowFit), and a scale the player cannot work with is
		/// not kept in the game's confirmation.
		/// </summary>
		public static bool FitsWithWindowFit(float scale, int w, int h, ref bool __result)
		{
			__result = scale > 0f;
			return false;
		}

		/// <summary>
		/// Prefix of ResolutionUtility.Update, in place of it: the same, without "Resolution too small (WxH), resetting UI
		/// scale": every 30 frames, when the scale leaves less than 1024 units across and 768 down, the game puts back its
		/// recommended scale (1.3 on this phone at 3x; at 1.75 already on a phone 1600 x 720) — taken here for the player's
		/// choice and saved, so such a phone could never keep a larger scale.
		/// </summary>
		public static bool WithoutScaleReset()
		{
			if (RealTime.frameCount % 30 != 0 || LongEventHandler.AnyEventNowOrWaiting)
			{
				return false;
			}
			bool changed = false;
			if (Screen.fullScreenMode != Prefs.FullScreenMode)
			{
				Prefs.FullScreenMode = Screen.fullScreenMode;
				if (Screen.fullScreen)
				{
					Resolution native = ResolutionUtility.NativeResolution;
					Prefs.ScreenWidth = native.width;
					Prefs.ScreenHeight = native.height;
				}
				changed = true;
			}
			if (!Screen.fullScreen)
			{
				if (Screen.windowWidth != Prefs.ScreenWidth)
				{
					Prefs.ScreenWidth = Screen.windowWidth;
					changed = true;
				}
				if (Screen.windowHeight != Prefs.ScreenHeight)
				{
					Prefs.ScreenHeight = Screen.windowHeight;
					changed = true;
				}
			}
			if (changed)
			{
				Prefs.Save();
			}
			return false;
		}

		/// <summary>
		/// Prefix of ResolutionUtility.ResolveUIScale (1.7.4980): the scale set, times the system's display scaling as the
		/// game counts it (1 on the phone: the GLFW shim's content scale is 1), without the cap.
		/// </summary>
		public static bool Uncapped(float preference, ref float __result)
		{
			__result = preference * ResolutionUtility.DpiScale;
			return false;
		}

		/// <summary>Every frame.</summary>
		public static void Update()
		{
			float current = Prefs.UIScale;
			if (lastSet > 0f && Mathf.Abs(current - lastSet) > 0.001f)
			{
				// Someone else set it: the player, in the options dialog.
				preferred = Mathf.Clamp(current, MinScale, MaxScale);
				Save();
			}
			if (Mathf.Abs(preferred - current) > 0.004f)
			{
				Prefs.UIScale = preferred;
			}
			lastSet = preferred;
		}

		private static void Save()
		{
			try
			{
				string text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
				var lines = new System.Collections.Generic.List<string>(text.Split('\n'));
				lines.RemoveAll(l => l.TrimStart().StartsWith("uiScale=") || l.Trim().Length == 0);
				lines.Add("uiScale=" + preferred.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
				File.WriteAllText(FilePath, string.Join("\n", lines) + "\n");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] uiScale not saved: " + e.Message);
			}
		}
	}

	/// <summary>
	/// Many dialogs are a fixed 1024x768, and panels attached to something (the tabs above the inspect pane) can
	/// reach past the screen edge: more than a phone has at a readable scale. Such a window is drawn smaller,
	/// by itself; nothing else on the screen moves or changes size.
	///
	/// It works through the two things the game hands to the GUI for each window: its rectangle, and the GUI
	/// matrix in force at that moment, which the backend keeps for the window's contents and its mouse
	/// coordinates. Around Window.WindowOnGUI the matrix gets an extra factor and the rectangle is expressed in
	/// that smaller unit; afterwards both are put back, so that the game goes on seeing the window at the size
	/// it was made for.
	/// </summary>
	internal static class WindowFit
	{
		// Smaller than this and a window would be unreadable anyway; it is then left to stick out.
		private const float SmallestFactor = 0.5f;

		private static bool active;
		private static Rect design, passed;
		private static float factor;
		private static Matrix4x4 matrix;

		// One axis of a window: how much it has to shrink to fit, and where the shrunk window goes.
		// position: -1 keep the low edge, 1 keep the high edge, 0 centre on the screen, 2 nothing to do.
		private static float Axis(float min, float size, float limit, out int position)
		{
			bool low = min < -0.5f, high = min + size > limit + 0.5f;
			if (!low && !high)
			{
				position = 2;
				return 1f;
			}
			if ((low && high) || size > limit)
			{
				position = 0;
				return limit / size;
			}
			if (low)
			{
				// Attached with its high edge (a panel growing upwards from something): that edge stays.
				position = 1;
				return (min + size) / size;
			}
			position = -1;
			return (limit - min) / size;
		}

		private static float Place(float min, float size, float limit, int position, float f)
		{
			float fitted = size * f;
			switch (position)
			{
				case -1: return min;
				case 1: return min + size - fitted;
				case 0: return (limit - fitted) / 2f;
			}
			// This axis fitted; the other one made the window smaller. Stay at the screen edge it sat on, else centred on itself.
			if (min <= 1f)
			{
				return min;
			}
			if (min + size >= limit - 1f)
			{
				return min + size - fitted;
			}
			return min + (size - fitted) / 2f;
		}

		/// <summary>Where a window that does not fit the screen is drawn, smaller by f; false when it is drawn as it is.</summary>
		private static bool Fitted(Window window, out Rect fitted, out float f)
		{
			return Fitted(window.windowRect, window.draggable || window.resizeable, out fitted, out f);
		}

		/// <summary>Where a window at this rectangle that the player cannot move (an immediate window) is drawn.</summary>
		public static Rect Drawn(Rect rect)
		{
			return Fitted(rect, false, out Rect fitted, out _) ? fitted : rect;
		}

		private static bool Fitted(Rect rect, bool movable, out Rect fitted, out float f)
		{
			fitted = default(Rect);
			f = 1f;
			float width = UI.screenWidth, height = UI.screenHeight;
			if (rect.width < 1f || rect.height < 1f)
			{
				return false;
			}
			bool sizeFits = rect.width <= width + 0.5f && rect.height <= height + 0.5f;
			if (sizeFits && movable)
			{
				return false; // the player may have moved it there
			}
			float fx = Axis(rect.xMin, rect.width, width, out int px), fy = Axis(rect.yMin, rect.height, height, out int py);
			f = Mathf.Min(fx, fy);
			if (f > 0.999f || f < SmallestFactor)
			{
				return false;
			}
			fitted = new Rect(Place(rect.xMin, rect.width, width, px, f), Place(rect.yMin, rect.height, height, py, f), rect.width * f, rect.height * f);
			return true;
		}

		/// <summary>
		/// Prefix of Widgets.DrawShadowAround. The window stack draws a window's shadow before the window, around its
		/// rectangle as designed (WindowStack.WindowStackOnGUI); the middle of the shadow, which the window's background
		/// covers, was left showing beside a window drawn smaller: a dark box over the map (the user, 2026-10-22: the log
		/// tab's 630 x 510 window above RimHUD's taller inspect pane). The shadow goes around the window as it is drawn.
		/// </summary>
		public static void BeforeShadow(ref Rect rect)
		{
			WindowStack stack = Find.WindowStack;
			if (stack == null)
			{
				return;
			}
			foreach (Window window in stack.Windows)
			{
				if (window.windowRect == rect)
				{
					if (Fitted(window, out Rect fitted, out _))
					{
						rect = fitted;
					}
					return;
				}
			}
		}

		/// <summary>Prefix of Window.WindowOnGUI.</summary>
		public static void Before(Window __instance)
		{
			active = false;
			Rect rect = __instance.windowRect;
			if (!Fitted(__instance, out Rect fitted, out float f))
			{
				return;
			}
			design = rect;
			factor = f;
			// The same window in units that are f times smaller, rounded as the game is about to round it.
			passed = new Rect(fitted.x / f, fitted.y / f, rect.width, rect.height).Rounded();
			matrix = GUI.matrix;
			float scale = Prefs.UIScale * f;
			GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
			__instance.windowRect = passed;
			active = true;
		}

		/// <summary>
		/// Postfix of the backend's GuiToScreenPoint. The game asks where on the screen a point of the interface being
		/// drawn is by handing over the point divided by its scale, and wants the answer in units of that scale; the
		/// engine answers with the offset of the open groups plus the matrix times what it was handed. That is the
		/// right answer while the matrix is the game's scale. Inside a window drawn smaller the offset is in the
		/// window's smaller units and comes out too large (or, in a list scrolled far, too negative): a row being
		/// dragged to another place in such a window (the mod page) then lands some rows away from the finger, the
		/// further the list is scrolled. Here the point is put through the matrix as it is drawn.
		/// </summary>
		public static void AfterGuiToScreen(Vector2 guiPoint, ref Vector2 __result, float ___m00, float ___m11, bool ___rotated, float2 ___translate, float2 ___offset)
		{
			float scale = Prefs.UIScale;
			if (___rotated || scale <= 0f || (Mathf.Abs(___m00 - scale) < 0.0001f && Mathf.Abs(___m11 - scale) < 0.0001f))
			{
				return;
			}
			__result = new Vector2((___m00 * (___offset.X + guiPoint.x * scale) + ___translate.X) / scale, (___m11 * (___offset.Y + guiPoint.y * scale) + ___translate.Y) / scale);
		}

		/// <summary>Finalizer of Window.WindowOnGUI: runs whether or not the window's code threw.</summary>
		public static void After(Window __instance)
		{
			if (!active)
			{
				return;
			}
			active = false;
			GUI.matrix = matrix;
			// The GUI hands back the rectangle, moved if the window was dragged by its title.
			Vector2 moved = (__instance.windowRect.position - passed.position) * factor;
			design.position += moved;
			__instance.windowRect = design;
		}
	}

	/// <summary>
	/// The gear tab (ITab_Pawn_Gear) sizes itself: 450 units high, or as high as its contents up to the room above the
	/// inspect pane when they need more. On a phone the room is less than 450 (1920 x 864 at 1.6: about 310), so a pawn
	/// with little gear got a tab too high for the screen, which WindowFit drew smaller, and one carrying more got a tab
	/// as high as the room, drawn at full size: the tab grew and shrank as the pawn picked things up and put them away
	/// (a tester, 2026-10-25: "这个栏的大小会随时变"). Here it is never higher than the room: always at full size, its
	/// list scrolling when longer. Only the game's own gear tab; other tabs (and mods' gear tabs, laid out their own way)
	/// keep their fixed sizes, made smaller when they do not fit, which does not change as they are used.
	///
	/// The tab sets its size again at the end of every layout pass (FillTab), and a press reaches the window under the
	/// finger before the game's OnGUI places the tab (GUI.DispatchInputToWindows): that pass drew the tab 450 high again,
	/// its list as high as its contents or higher, and the engine's scroll view put the list back at the top (a tester,
	/// 2026-10-28, 960 x 600: every tap in the scrolled list "让滑条位置上升到最高"). The size is held after FillTab too.
	/// </summary>
	internal static class GearTabFit
	{
		private static readonly Func<InspectTabBase, float> PaneTopY = AccessTools.MethodDelegate<Func<InspectTabBase, float>>(AccessTools.PropertyGetter(typeof(InspectTabBase), "PaneTopY"));
		private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> Size = AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size");

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(InspectTabBase), "UpdateSize"), postfix: new HarmonyMethod(typeof(GearTabFit), nameof(AfterUpdateSize)));
				harmony.Patch(AccessTools.Method(typeof(ITab_Pawn_Gear), "FillTab"), postfix: new HarmonyMethod(typeof(GearTabFit), nameof(AfterUpdateSize)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] gear tab: left to size itself: " + e.Message);
			}
		}

		/// <summary>Postfix of InspectTabBase.UpdateSize (TabRect calls it before it places the tab) and of ITab_Pawn_Gear.FillTab.</summary>
		public static void AfterUpdateSize(InspectTabBase __instance)
		{
			if (__instance.GetType() != typeof(ITab_Pawn_Gear))
			{
				return;
			}
			// The tab sits on the row of tab buttons (30) above the pane, as InspectTabBase.TabRect places it.
			float room = PaneTopY(__instance) - 30f;
			ref Vector2 size = ref Size(__instance);
			if (room >= 200f && size.y > room)
			{
				size.y = room;
			}
		}
	}

	/// <summary>
	/// A turned icon inside something drawn smaller (GizmoFit's grid, WindowFit's window) turned about the wrong point
	/// (a tester, 2026-10-23: in the shrunk Architect menu the trims, steps and other icons the game draws turned went
	/// off their buttons, further the further right and down they were). UI.RotateAroundPivot hands GUIUtility the pivot
	/// times Prefs.UIScale, as an offset in screen pixels from where the group starts; that is the scale of GUI.matrix
	/// only while nothing else scales it. The pivot is now multiplied by the scale GUI.matrix really has: the same as the
	/// game's where nothing is drawn smaller.
	/// </summary>
	internal static class RotateInFit
	{
		public static bool Before(float angle, Vector2 center)
		{
			Matrix4x4 m = GUI.matrix;
			float scale = new Vector2(m.m00, m.m10).magnitude;
			if (Mathf.Abs(scale - Prefs.UIScale) < 0.0001f)
			{
				return true;
			}
			GUIUtility.RotateAroundPivot(angle, center * scale);
			return false;
		}
	}

	/// <summary>
	/// A grid of gizmos (the Architect's buildings, the commands of what is selected) is laid out from above the bottom bar
	/// upwards, in rows as wide as the screen less its right-hand column (GizmoGridDrawer.DrawGizmoGrid). The phone's screen
	/// is short: with many mods' buildings in one Architect category the rows went past its top (the user, 2026-10-22: the
	/// Storage category, 8 rows where 7 fit). A grid that does not fit is drawn smaller, from its bottom-left corner, and the
	/// room it gains on the right takes more of it in each row.
	///
	/// The commands of what is selected (GizmoGridDrawer.DrawGizmoGridFor, on the map and the world) covered the middle of
	/// the screen in a fight (the user, 2026-10-23: a mechanitor's mech, four rows of commands, its shield, weapon and
	/// bandwidth). Two choices on the interface page of the options, for that grid only (the Architect's is left alone):
	/// its size ("gizmoScale" in mobile.txt, percent, 100 / 85 / 70) and at most how many rows it may take before it is
	/// drawn smaller still ("gizmoRows", 2 / 3, 0 for no limit; never below RowsSmallest, so that it can still be tapped).
	///
	/// Some gizmos draw part of themselves in an immediate window (WindowStack.ImmediateWindow, at a rect from where they
	/// are drawn): a mod weapon's ammunition box (the user, 2026-10-23, at 70%: full size over its neighbours). The engine
	/// draws windows after everything else, with the matrix of the moment GUI.Window was called, so such a window kept
	/// its full size at the unscaled place. Asked for inside a smaller grid, it is moved and sized as the grid is (that
	/// rect is what the window stack sees: clicks, the shadow) and drawn with the grid's factor around its top-left corner.
	/// </summary>
	internal static class GizmoFit
	{
		private const float Smallest = 0.5f, RowsSmallest = 0.6f, RowHeight = 75f + 14f, ColumnSpacing = 5f, RightColumn = 147f, BottomBar = 35f, TopMargin = 4f;

		/// <summary>
		/// What the grid leaves free at the right: the game's 147 units, or as far in as the time buttons were last drawn.
		/// They reach further: TouchSizes makes them 1.4 times wider (four of them 185 units in), Smart Speed adds a fifth,
		/// the column moves left off a camera hole at the right; a full row of commands covered the pause button (the user,
		/// 2026-10-26).
		/// </summary>
		private static float RightReserve => timeLeft > 0f ? Mathf.Max(RightColumn, UI.screenWidth - timeLeft + ColumnSpacing) : RightColumn;

		// Where on the screen the time buttons begin, in interface units, as last drawn (0: not drawn yet).
		private static float timeLeft;

		/// <summary>Prefix of TimeControls.DoTimeControlsGUI, after the mods' (which move and widen the strip): where it begins.</summary>
		public static void SeeTimeControls(Rect timerRect)
		{
			timeLeft = UI.GUIToScreenPoint(timerRect.position).x;
		}

		private sealed class Fit
		{
			public int count = -1, screenWidth, screenHeight, rows;
			public float factor = 1f, size = 1f, reserve;
		}

		// The selection's commands: the size chosen and the most rows (0: any), and whether they are being drawn now.
		private static int sizePercent = Mathf.Clamp(Settings.Get("gizmoScale", 100), 50, 100);
		private static int maxRows = Mathf.Clamp(Settings.Get("gizmoRows", 2), 0, 9);
		private static bool selection;

		// While a smaller grid is drawn: its factor (0: none) and the point that stays where it is. The immediate windows
		// asked for inside one, by ID, with the factor they are drawn at.
		private static float gridFactor;
		private static Vector2 gridCorner;
		private static readonly Dictionary<int, float> scaledWindows = new Dictionary<int, float>();

		// By where the grid starts (the Architect's and a selection's apart).
		private static readonly Dictionary<int, Fit> fits = new Dictionary<int, Fit>();
		private static readonly List<Gizmo> all = new List<Gizmo>(), firsts = new List<Gizmo>(), groups = new List<Gizmo>();
		private static readonly List<Command> shrinkables = new List<Command>();

		public struct State
		{
			public bool active;
			public Matrix4x4 matrix;
			public int width;
		}

		public struct WindowState
		{
			public float factor;
			public Matrix4x4 matrix;
		}

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGrid)), prefix: new HarmonyMethod(typeof(GizmoFit), nameof(Before)), finalizer: new HarmonyMethod(typeof(GizmoFit), nameof(After)));
			harmony.Patch(AccessTools.Method(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGridFor)), prefix: new HarmonyMethod(typeof(GizmoFit), nameof(BeforeSelection)), finalizer: new HarmonyMethod(typeof(GizmoFit), nameof(AfterSelection)));
			harmony.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.ImmediateWindow)), prefix: new HarmonyMethod(typeof(GizmoFit), nameof(BeforeImmediateWindow)));
			harmony.Patch(AccessTools.Method(typeof(Window), nameof(Window.WindowOnGUI)), prefix: new HarmonyMethod(typeof(GizmoFit), nameof(BeforeWindow)), finalizer: new HarmonyMethod(typeof(GizmoFit), nameof(AfterWindow)));
			harmony.Patch(AccessTools.Method(typeof(Window), "InnerWindowOnGUI"), prefix: new HarmonyMethod(typeof(GizmoFit), nameof(BeforeWindowInside)), finalizer: new HarmonyMethod(typeof(GizmoFit), nameof(AfterWindowInside)));
			harmony.Patch(AccessTools.Method(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI)), prefix: new HarmonyMethod(typeof(GizmoFit), nameof(SeeTimeControls)) { priority = Priority.Last });
		}

		/// <summary>Prefix of WindowStack.ImmediateWindow: inside a smaller grid, the rect as it shows on the screen.</summary>
		public static void BeforeImmediateWindow(int ID, ref Rect rect)
		{
			int id = -Math.Abs(ID);
			if (gridFactor <= 0f)
			{
				if (scaledWindows.Count > 0)
				{
					scaledWindows.Remove(id);
				}
				return;
			}
			float f = gridFactor;
			rect = new Rect(gridCorner.x + (rect.x - gridCorner.x) * f, gridCorner.y + (rect.y - gridCorner.y) * f, rect.width * f, rect.height * f);
			scaledWindows[id] = f;
		}

		/// <summary>
		/// Prefix of Window.WindowOnGUI: such a window goes to GUI.Window at its full size, from its top-left corner on the
		/// screen, with the matrix that draws it f times smaller around that corner. The finalizer puts back its rect.
		/// </summary>
		public static void BeforeWindow(Window __instance, out WindowState __state)
		{
			__state = default(WindowState);
			if (scaledWindows.Count == 0 || !(__instance is ImmediateWindow) || !scaledWindows.TryGetValue(__instance.ID, out float f))
			{
				return;
			}
			Rect rect = __instance.windowRect;
			__state.factor = f;
			__state.matrix = GUI.matrix;
			GUI.matrix = __state.matrix * Matrix4x4.TRS(new Vector3(rect.x * (1f - f), rect.y * (1f - f), 0f), Quaternion.identity, new Vector3(f, f, 1f));
			__instance.windowRect = new Rect(rect.x, rect.y, rect.width / f, rect.height / f);
		}

		public static void AfterWindow(Window __instance, WindowState __state)
		{
			if (__state.factor <= 0f)
			{
				return;
			}
			GUI.matrix = __state.matrix;
			Rect rect = __instance.windowRect;
			__instance.windowRect = new Rect(rect.x, rect.y, rect.width * __state.factor, rect.height * __state.factor);
		}

		/// <summary>
		/// Prefix and finalizer of Window.InnerWindowOnGUI (run later, by the engine): its background and contents are laid
		/// out at the full size too, inside the smaller matrix.
		/// </summary>
		public static void BeforeWindowInside(Window __instance, out float __state)
		{
			__state = 0f;
			if (scaledWindows.Count == 0 || !(__instance is ImmediateWindow) || !scaledWindows.TryGetValue(__instance.ID, out float f))
			{
				return;
			}
			__state = f;
			Rect rect = __instance.windowRect;
			__instance.windowRect = new Rect(rect.x, rect.y, rect.width / f, rect.height / f);
		}

		public static void AfterWindowInside(Window __instance, float __state)
		{
			if (__state > 0f)
			{
				Rect rect = __instance.windowRect;
				__instance.windowRect = new Rect(rect.x, rect.y, rect.width * __state, rect.height * __state);
			}
		}

		/// <summary>Prefix and finalizer of GizmoGridDrawer.DrawGizmoGridFor: the grid drawn inside is the selection's.</summary>
		public static void BeforeSelection()
		{
			selection = true;
		}

		public static void AfterSelection()
		{
			selection = false;
		}

		/// <summary>Prefix of GizmoGridDrawer.DrawGizmoGrid: a grid that does not fit is laid out in units f times smaller, as wide as the screen in them.</summary>
		public static void Before(IEnumerable<Gizmo> gizmos, float startX, out State __state)
		{
			__state = default(State);
			if (Event.current == null || Event.current.type == EventType.Layout || gizmos == null)
			{
				return;
			}
			float f = selection ? FactorFor(gizmos, startX, sizePercent / 100f, maxRows) : FactorFor(gizmos, startX, 1f, 0);
			float reserve = RightReserve;
			if (f > 0.999f && reserve <= RightColumn + 0.5f)
			{
				return;
			}
			__state.active = true;
			__state.matrix = GUI.matrix;
			__state.width = UI.screenWidth;
			// The game wraps its rows 147 units before the screen's width; that width is made to put the wrap at the reserve.
			if (f > 0.999f)
			{
				UI.screenWidth = Mathf.FloorToInt(UI.screenWidth - (reserve - RightColumn));
				return;
			}
			// Smaller around the grid's bottom-left corner, which stays where it is.
			float cornerY = UI.screenHeight - BottomBar;
			GUI.matrix = __state.matrix * Matrix4x4.TRS(new Vector3(startX * (1f - f), cornerY * (1f - f), 0f), Quaternion.identity, new Vector3(f, f, 1f));
			UI.screenWidth = Mathf.FloorToInt(startX + RightColumn + (UI.screenWidth - reserve - startX) / f);
			gridFactor = f;
			gridCorner = new Vector2(startX, cornerY);
		}

		/// <summary>Finalizer: the screen as it was.</summary>
		public static void After(State __state)
		{
			if (__state.active)
			{
				GUI.matrix = __state.matrix;
				UI.screenWidth = __state.width;
				gridFactor = 0f;
			}
		}

		/// <summary>
		/// The largest factor, size at the most, at which the grid fits between the bottom bar and the top of the screen and
		/// (rows > 0) takes no more than rows rows, its gizmos grouped and laid out as the game does (LaidOutHeight). Worked
		/// out again when their number, the screen or the choices change.
		/// </summary>
		private static float FactorFor(IEnumerable<Gizmo> gizmos, float startX, float size, int rows)
		{
			all.Clear();
			all.AddRange(gizmos);
			int key = Mathf.RoundToInt(startX);
			if (!fits.TryGetValue(key, out Fit fit))
			{
				fits[key] = fit = new Fit();
			}
			float reserve = RightReserve;
			if (fit.count == all.Count && fit.screenWidth == UI.screenWidth && fit.screenHeight == UI.screenHeight && fit.size == size && fit.rows == rows && fit.reserve == reserve)
			{
				return fit.factor;
			}
			fit.reserve = reserve;
			fit.count = all.Count;
			fit.screenWidth = UI.screenWidth;
			fit.screenHeight = UI.screenHeight;
			fit.size = size;
			fit.rows = rows;
			// In the game's order (a stable sort by Gizmo.Order) and groups (Gizmo.GroupsWith with the first of a group; nothing
			// is merged here), each group shown by its first gizmo that is not disabled.
			for (int i = 1; i < all.Count; i++)
			{
				Gizmo gizmo = all[i];
				int j = i - 1;
				for (; j >= 0 && all[j].Order > gizmo.Order; j--)
				{
					all[j + 1] = all[j];
				}
				all[j + 1] = gizmo;
			}
			firsts.Clear();
			groups.Clear();
			foreach (Gizmo gizmo in all)
			{
				int group = firsts.FindIndex(first => first.GroupsWith(gizmo));
				if (group < 0)
				{
					firsts.Add(gizmo);
					groups.Add(gizmo);
				}
				else if (groups[group].Disabled && !gizmo.Disabled)
				{
					groups[group] = gizmo;
				}
			}
			float room = UI.screenHeight - BottomBar - TopMargin, factor = Smallest;
			for (float f = size; f > Smallest; f -= 0.01f)
			{
				float height = LaidOutHeight(startX, f);
				int taken = Mathf.CeilToInt(height / RowHeight - 0.01f);
				// Too many rows: smaller, but only down to RowsSmallest; below it the rows are as many as they are.
				if (height * f <= room && (rows <= 0 || taken <= rows || f <= RowsSmallest))
				{
					factor = f;
					break;
				}
			}
			if (Mathf.Abs(factor - fit.factor) > 0.001f)
			{
				Console.WriteLine("[mobile] gizmo grid at x " + key + ": " + groups.Count + " gizmos, drawn at " + factor.ToString("F2") + (rows > 0 || size < 1f ? " (size " + size.ToString("F2") + ", at most " + rows + " rows)" : ""));
			}
			fit.factor = factor;
			return factor;
		}

		/// <summary>
		/// How high above the bottom bar the groups are drawn at factor f, in the grid's own units, as
		/// GizmoGridDrawer.DrawGizmoGrid lays them out (1.7): rows wrapped at the right-hand column; when a first count
		/// over every group makes three rows or more, the shrinkable commands (two or more) leave the rows and come after
		/// them as small icons, along the top row (its lower half, then its upper half), then upwards from the left.
		/// </summary>
		private static float LaidOutHeight(float startX, float f)
		{
			float right = startX + (UI.screenWidth - RightReserve - startX) / f, maxWidth = right - startX, x = startX;
			int wraps = 0;
			shrinkables.Clear();
			foreach (Gizmo gizmo in groups)
			{
				if (gizmo is Command command && command.shrinkable && command.Visible)
				{
					shrinkables.Add(command);
				}
				float width = gizmo.GetWidth(maxWidth);
				if (x + width > right)
				{
					x = startX;
					wraps++;
				}
				x += width + ColumnSpacing;
			}
			bool shrunk = wraps > 1 && shrinkables.Count > 1;
			int rows = 1;
			x = startX;
			foreach (Gizmo gizmo in groups)
			{
				if (!gizmo.Visible || shrunk && gizmo is Command command && shrinkables.Contains(command))
				{
					continue;
				}
				float width = gizmo.GetWidth(maxWidth);
				if (x + width > right)
				{
					x = startX;
					rows++;
				}
				x += width + ColumnSpacing;
			}
			float height = rows * RowHeight;
			if (shrunk)
			{
				// y: where the small icons' line is, from the top row's top (upwards negative); each is drawn a line lower.
				float lineX = x, y = 0f;
				int lines = 0;
				foreach (Command command in shrinkables)
				{
					float iconSize = command.GetShrunkSize;
					if (x + iconSize > right)
					{
						if (++lines > 1)
						{
							lineX = startX;
						}
						x = lineX;
						y -= iconSize + 3f;
					}
					height = Mathf.Max(height, rows * RowHeight - (y + iconSize + 3f));
					x += iconSize + 3f;
				}
			}
			return height;
		}

		/// <summary>The two rows on the interface page of the options (after the counters', the key wheel's and the cutout's).</summary>
		public static void OptionRows(Listing_Standard listing)
		{
			if (listing.ButtonTextLabeledPct(Texts.Chinese ? "命令图标大小" : "Command icon size", sizePercent + "%", 0.6f, TextAnchor.MiddleLeft, null, Texts.Chinese
				? "选中小人、机械族或物品时，屏幕下方那一片命令图标的大小（建筑栏不变）。"
				: "The size of the commands at the bottom of the screen for what is selected (the Architect's menu stays as it is)."))
			{
				var choices = new List<FloatMenuOption>();
				foreach (int percent in new[] { 100, 85, 70 })
				{
					int chosen = percent;
					choices.Add(new FloatMenuOption(chosen + "%", delegate
					{
						sizePercent = chosen;
						Settings.Set("gizmoScale", chosen.ToString());
					}));
				}
				Find.WindowStack.Add(new FloatMenu(choices));
			}
			if (listing.ButtonTextLabeledPct(Texts.Chinese ? "命令图标多时自动缩小" : "Many commands: smaller", RowsName(maxRows), 0.6f, TextAnchor.MiddleLeft, null, Texts.Chinese
				? "命令图标超过这么多行时，自动缩小到放得下（最小缩到 60%，再多就照常多排几行）。"
				: "When the commands take more rows than this, they are drawn smaller until they fit (down to 60%; beyond that they take more rows as usual)."))
			{
				var choices = new List<FloatMenuOption>();
				foreach (int count in new[] { 0, 2, 3 })
				{
					int chosen = count;
					choices.Add(new FloatMenuOption(RowsName(chosen), delegate
					{
						maxRows = chosen;
						Settings.Set("gizmoRows", chosen.ToString());
					}));
				}
				Find.WindowStack.Add(new FloatMenu(choices));
			}
		}

		private static string RowsName(int rows)
		{
			if (rows <= 0)
			{
				return Texts.Chinese ? "关" : "Off";
			}
			return Texts.Chinese ? "最多" + (rows == 2 ? "两" : rows == 3 ? "三" : rows.ToString()) + "行" : "At most " + rows + " rows";
		}
	}
}
