using System;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PortMobile
{
	/// <summary>
	/// The left and the right mouse button for a finger: two round buttons of their own, "左键" and "右键", shown while a
	/// game is played with fingers (the user, 2026-10-30: "将左右键从按键盘分离出来，把两个按键做成单独存在，可自由移动").
	/// Until then they were switches of the key wheel, and with the right one on every touch was a right click until the
	/// player switched back: three to five taps for one right click. Now a right click costs one tap more than a left one:
	///   a tap on 右键    the next touch is a right click, pressed from its landing to its lifting; touches are left
	///                    clicks again once that finger lifts. It shows yellow, "一次"; tapped again, it is taken back.
	///   a hold on 右键   every touch is a right click until 左键 or 右键 is tapped. It shows red, "锁定", as soon as
	///                    the finger has rested long enough (with the sound of a switch, as the finger covers it), and
	///                    holds once the finger lifts; a finger that drags the button instead locks nothing.
	///   a tap on 左键    left clicks again, whatever was on: the right button, or the wheel's middle one.
	/// The key wheel can hold a 左键 and a 右键 of its own as well (the user, 2026-11-03: "保持两个单独按键，并且按键盘里也
	/// 可以添加鼠标左右键"; not among its first keys, added in its setup): they do what these do, a tap, a hold (TapLeft,
	/// TapRight, LockRight), on the one state of both, and the wheel's ring closes as its 右键 switches a right click on,
	/// so that the whole map is free for it. With these buttons switched off, the wheel's keys alone keep the right button.
	/// The middle button stays a switch on the key wheel (KeyWheel); it is held here with the right one, one of the three
	/// at a time as before, and the wheel's closed button still says when the right or the middle one is on. Everything
	/// else about right clicks is Touch's as before (a hold on the map with a colonist selected, a hold on a letter, the
	/// Shift and Ctrl that the wheel holds down come with a right click too). A touch on these buttons or on the wheel is
	/// always the left button, taken by the button's own window: it never reaches the game, and it does not spend a right
	/// click waiting for the next touch.
	/// They are first put at the right of the screen (DefaultCenter). Each button is moved by dragging it, as the wheel's
	/// button is (mobile.txt mouseLeftAt=X,Y and mouseRightAt=X,Y, the centre in interface units), kept on the screen and
	/// off the camera's hole (Cutout); the wheel's keys go round them.
	/// They look like the wheel's button and have its size. Left out while the game's own mouse handling is on
	/// (InputMode: the mouse has buttons of its own; they come back once a finger touches the screen after the mouse has
	/// gone), and outside a game (the user, of the menus: "使用触控可以完美控制"). Options → Interface "显示鼠标左右键"
	/// (mobile.txt mouseButtons=0 leaves them out).
	/// </summary>
	internal static class MouseButtons
	{
		// GLFW's numbers of the mouse buttons, as Touch injects them; 0 and 1 are also the buttons' own (左键, 右键) here.
		internal const int Left = 0, Right = 1, Middle = 2;

		private const int NoTouch = -1;
		// Next to the key wheel's windows (73195801, and its keys from 73195810).
		private const int WindowIdBase = 73195806;
		private const float Size = KeyWheel.HubSize;
		// Until they are moved, 左键's centre is this far from the right edge and 右键 a button and the wheel's spacing right
		// of it, both at this part of the screen's height (DefaultCenter).
		private const float DefaultFromRight = 447f, DefaultHeight = 0.33f;
		private static readonly string[] PlaceKeys = { "mouseLeftAt", "mouseRightAt" };

		private enum State
		{
			Left,
			RightOnce,   // the next touch is a right click
			RightLocked, // every touch is, until 左键 or 右键 is tapped
			Middle       // the key wheel's switch: every touch is a middle click until it is tapped again
		}

		private static bool enabled;
		private static State state = State.Left;
		// Each button's place (its centre; x below 0: not moved yet, where DefaultCenter puts it), and where it is shown:
		// moved off the camera's hole if it lies over it. Touches are taken where it is shown.
		private static readonly Vector2[] places = { new Vector2(-1f, -1f), new Vector2(-1f, -1f) };
		private static readonly Vector2[] shown = { new Vector2(-1f, -1f), new Vector2(-1f, -1f) };
		private static readonly Action[] drawers = { () => DrawButton(Left), () => DrawButton(Right) };
		private static int drawnFrame = -100;
		// The finger on a button: which one, where and when it landed, whether it drags the button, and whether it has
		// rested on 右键 long enough for the lock.
		private static int touching = NoTouch;
		private static Vector2 touchStart, grabOffset;
		private static double touchTime;
		private static bool dragged, restedLong;

		/// <summary>Shown: switched on, and not the game's own mouse handling (InputMode).</summary>
		public static bool Enabled => enabled && !InputMode.Mouse;

		/// <summary>The buttons are on the screen: drawn in the last few frames (not in the screenshot mode, nor while the game loads).</summary>
		private static bool Drawn => Time.frameCount - drawnFrame <= 3;

		/// <summary>The button a finger on the screen is now: Left, Right (for the next touch, or locked) or Middle (the key wheel's).</summary>
		internal static int Active => state == State.Middle ? Middle : state == State.Left ? Left : Right;

		/// <summary>A 右键 is there to switch the right button on and off: these buttons, or the key wheel's own 左键 or 右键.</summary>
		private static bool RightKept => Enabled || KeyWheel.Enabled && KeyWheel.HasMouseKeys;

		public static void Start()
		{
			enabled = Settings.Get("mouseButtons", true);
			for (int i = 0; i < places.Length; i++)
			{
				if (KeyWheel.TryParsePlace(Settings.Get(PlaceKeys[i], null), out Vector2 at))
				{
					places[i] = at;
				}
			}
			Console.WriteLine("[mobile] mouse buttons " + (enabled ? "on" : "off"));
		}

		/// <summary>Both buttons back where they are first put (the key wheel's setup puts its own button and these back together).</summary>
		internal static void ResetPlaces()
		{
			for (int i = 0; i < places.Length; i++)
			{
				places[i] = new Vector2(-1f, -1f);
				Settings.Set(PlaceKeys[i], "");
			}
		}

		/// <summary>A mouse button's name, as the buttons and the key wheel show it.</summary>
		internal static string Name(int button)
		{
			if (Texts.Chinese)
			{
				return button == Right ? "右键" : button == Middle ? "中键" : "左键";
			}
			return button == Right ? "RMB" : button == Middle ? "MMB" : "LMB";
		}

		private static void SetState(State value, string why)
		{
			if (state == value)
			{
				return;
			}
			state = value;
			string now = value == State.RightOnce ? "the next touch is a right click"
				: value == State.RightLocked ? "every touch is a right click until 左键 or 右键 is tapped"
				: value == State.Middle ? "every touch is a middle click (the key wheel's switch)"
				: "touches are left clicks";
			Console.WriteLine("[mobile] mouse buttons: " + now + " (" + why + ")");
		}

		/// <summary>The key wheel's middle button tapped: every touch a middle click (the right button off), or left clicks again. Whether it is on now.</summary>
		internal static bool ToggleMiddle()
		{
			SetState(state == State.Middle ? State.Left : State.Middle, "the key wheel's 中键");
			return state == State.Middle;
		}

		/// <summary>A tap on a 左键 (this button or the key wheel's): left clicks again, whatever was on.</summary>
		internal static void TapLeft(string why)
		{
			bool wasOn = state != State.Left;
			SetState(State.Left, why);
			(wasOn ? SoundDefOf.Checkbox_TurnedOff : SoundDefOf.Tick_Low).PlayOneShotOnCamera();
		}

		/// <summary>A tap on a 右键: the next touch a right click, or, while the right button is on already, left clicks again. Whether it is on now.</summary>
		internal static bool TapRight(string why)
		{
			bool on = state == State.Left || state == State.Middle;
			SetState(on ? State.RightOnce : State.Left, why);
			(on ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
			return on;
		}

		/// <summary>A finger rested on a 右键 long enough and lifted: every touch a right click until a 左键 or a 右键 is tapped (the sound came as it rested, HoldSound).</summary>
		internal static void LockRight(string why)
		{
			SetState(State.RightLocked, why);
		}

		/// <summary>A finger has rested on a 右键 long enough for the lock: told now, as the finger covers the button that will show it.</summary>
		internal static void HoldSound()
		{
			SoundDefOf.Checkbox_TurnedOn.PlayOneShotOnCamera();
		}

		/// <summary>
		/// How a 右键 looks while the right button is on (this one and the key wheel's): yellow for the next touch, red
		/// locked, and red too while a finger that rests on it has rested long enough (the lock comes as it lifts). False
		/// while it is off: then it looks like any other key.
		/// </summary>
		internal static bool RightLook(bool lockComing, float alpha, out Color fill, out string word)
		{
			State look = lockComing ? State.RightLocked : state;
			if (look == State.RightOnce)
			{
				// The yellow of the wheel's button when something is on.
				fill = new Color(0.85f, 0.65f, 0.2f, alpha);
				word = Texts.Chinese ? "一次" : "once";
				return true;
			}
			if (look == State.RightLocked)
			{
				fill = new Color(0.8f, 0.3f, 0.22f, alpha);
				word = Texts.Chinese ? "锁定" : "locked";
				return true;
			}
			fill = default(Color);
			word = null;
			return false;
		}

		/// <summary>
		/// Which mouse button a finger that lands at this point of the screen (window pixels) is, for Touch: the right one
		/// while it is on (a right click for the next touch is spent by this one), the middle one while the wheel's is on.
		/// The left one on these buttons and on the wheel, and while what switched the other on is not on the screen (the
		/// screenshot mode, a load; for the right one these buttons or the wheel, which may hold a 右键 too).
		/// </summary>
		internal static int TapButton(Vector2 pixels)
		{
			if (state == State.Left || Current.ProgramState != ProgramState.Playing)
			{
				return Left;
			}
			Vector2 ui = pixels / Prefs.UIScale;
			bool middle = state == State.Middle;
			if (!(middle ? KeyWheel.Drawn : (Drawn || KeyWheel.Drawn)) || Covers(ui) || KeyWheel.Covers(ui))
			{
				return Left;
			}
			if (state == State.RightOnce)
			{
				// No other touch begins before this one's fingers have lifted: the one after it is a left click again.
				SetState(State.Left, "the touch beginning now is the right click");
			}
			return middle ? Middle : Right;
		}

		/// <summary>Whether this point (interface units) is on one of the buttons as they were last drawn (their windows take every press on them).</summary>
		internal static bool Covers(Vector2 ui)
		{
			if (!Drawn)
			{
				return false;
			}
			foreach (Vector2 center in shown)
			{
				if (RectAt(center).Contains(ui))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>Whether a window at this rect (interface units) would lie on one of the buttons as they are drawn now (the key wheel lays its keys round them).</summary>
		internal static bool Overlaps(Rect rect)
		{
			if (!Drawn)
			{
				return false;
			}
			foreach (Vector2 center in shown)
			{
				if (RectAt(center).Overlaps(rect))
				{
					return true;
				}
			}
			return false;
		}

		// ---- every frame ----

		/// <summary>
		/// Once a frame (from Touch, before this frame's fingers): the left button again when what switched another on is
		/// gone (the wheel for its middle button; for the right one these buttons and the wheel's 左键 and 右键: a mouse
		/// came, the game was left, they were switched off or taken out of the wheel); the finger on a button followed, and
		/// what it meant done when it lifts.
		/// </summary>
		public static void Update()
		{
			bool playing = Current.ProgramState == ProgramState.Playing;
			if (state == State.Middle && (!KeyWheel.Enabled || !playing))
			{
				SetState(State.Left, "the key wheel is put away");
			}
			else if ((state == State.RightOnce || state == State.RightLocked) && (!RightKept || !playing))
			{
				SetState(State.Left, "the mouse buttons are put away");
			}
			if (touching == NoTouch)
			{
				return;
			}
			if (!Enabled || !playing || Touch.FingerCount > 1)
			{
				// Put away under the finger, or a second finger came: the touch is a pinch (Touch), not meant for the button.
				// A button it was dragging stays where the finger left it, and is kept there (it came back to its old place
				// at the next start).
				if (dragged)
				{
					Settings.Set(PlaceKeys[touching], KeyWheel.PlaceText(places[touching]));
				}
				touching = NoTouch;
				return;
			}
			if (Input.GetMouseButton(0))
			{
				Vector2 at = UI.MousePositionOnUIInverted;
				if (!dragged && (at - touchStart).magnitude > 14f)
				{
					// As the wheel's button: a finger that goes further moves it.
					dragged = true;
				}
				if (dragged)
				{
					places[touching] = KeyWheel.Clamp(at + grabOffset, Size / 2f);
				}
				else if (touching == Right && !restedLong && Time.realtimeSinceStartupAsDouble - touchTime >= Touch.HoldSeconds)
				{
					// As long as a hold anywhere: the lock comes when the finger lifts.
					restedLong = true;
					HoldSound();
				}
				return;
			}
			// The finger lifted.
			int was = touching;
			touching = NoTouch;
			if (dragged)
			{
				Settings.Set(PlaceKeys[was], KeyWheel.PlaceText(places[was]));
				return;
			}
			if (was == Left)
			{
				TapLeft("a tap on 左键");
			}
			else if (restedLong)
			{
				LockRight("a hold on 右键");
			}
			else
			{
				TapRight("a tap on 右键");
			}
		}

		/// <summary>From the end of the play screen's interface (Entry), every frame, before the key wheel lays its keys round them: the buttons' windows.</summary>
		public static void OnGui()
		{
			if (!Enabled || Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.UIRoot == null || LongEventHandler.ShouldWaitForEvent)
			{
				return;
			}
			if (Find.UIRoot.screenshotMode != null && Find.UIRoot.screenshotMode.FiltersCurrentEvent)
			{
				return;
			}
			for (int i = 0; i < places.Length; i++)
			{
				Place(i);
				Find.WindowStack.ImmediateWindow(WindowIdBase + i, RectAt(shown[i]), WindowLayer.Super, drawers[i], doBackground: false, absorbInputAroundWindow: false, shadowAlpha: 0f);
			}
			drawnFrame = Time.frameCount;
		}

		/// <summary>Where a button is shown: its place (until it is moved, DefaultCenter), kept on the screen and off the camera's hole.</summary>
		private static void Place(int button)
		{
			Vector2 center = KeyWheel.Clamp(places[button].x < 0f ? DefaultCenter(button) : places[button], Size / 2f);
			shown[button] = Cutout.ClearOf(RectAt(center)).center;
		}

		/// <summary>
		/// Where a button is until it is moved: at the right, in the room the game's own windows leave there. The main tabs
		/// (1010 wide), the inspect tabs (630 at most: health), the architect's menu and a tool's controls open from the
		/// left edge; the colonist bar keeps to the top (130 at most); the time controls, letters and alerts keep to the
		/// right edge (300), and a minimap is first put 350 from it (Dubs Mint Minimap: 右键 ends 8 units before it); the
		/// game's menu tab (390 high) and the architect's grid of designators (rows of 89) come up from the bottom. So 左键
		/// is 447 units from the right edge, 右键 62 right of it, both at 0.33 of the screen's height. On the user's
		/// phone (2772 x 1272 pixels) at the interface scales 1.5 and 1.85 (1848 x 848 and 1498 x 688 units) that is
		/// (1401, 280) and (1463, 280), and (1051, 227) and (1113, 227): clear of all of those (of a main tab by 14 units at
		/// 1.85), of a grid of designators five rows high (four at 1.85: vanilla's furniture), of the camera's hole at either
		/// edge, and of the key wheel and its ring at its first place and at the user's (233,299). Beside the wheel's button
		/// they would lie on the inspect tabs (the gear tab's drop and info icons) and on every main tab, which a player
		/// opens all the time.
		/// A wheel's button the player has put on that row would hide them (it is drawn above them): the row goes above it
		/// then, or below it where there is no room above.
		/// </summary>
		private static Vector2 DefaultCenter(int button)
		{
			float x = UI.screenWidth - DefaultFromRight, y = Mathf.Round(UI.screenHeight * DefaultHeight);
			var row = new Rect(x - Size / 2f, y - Size / 2f, 2f * Size + KeyWheel.Spacing, Size);
			if (KeyWheel.RestsOn(row, out Rect hub))
			{
				y = hub.yMin - KeyWheel.Spacing - Size / 2f;
				if (y < Size / 2f + 2f)
				{
					y = hub.yMax + KeyWheel.Spacing + Size / 2f;
				}
			}
			return new Vector2(x + button * (Size + KeyWheel.Spacing), y);
		}

		private static Rect RectAt(Vector2 center)
		{
			return new Rect(center.x - Size / 2f, center.y - Size / 2f, Size, Size);
		}

		// ---- drawing ----

		private static void DrawButton(int button)
		{
			var rect = new Rect(0f, 0f, Size, Size);
			bool held = touching == button;
			// What 右键 is, or is about to be: a finger that has rested on it long enough shows the lock before it lifts.
			if (button == Right && RightLook(held && restedLong && !dragged, held ? 1f : 0.95f, out Color fill, out string word))
			{
				KeyWheel.DrawRound(rect, fill, Name(Right) + "\n" + word, GameFont.Tiny);
			}
			else
			{
				// As the wheel's closed button.
				KeyWheel.DrawRound(rect, new Color(0.25f, 0.27f, 0.3f, held ? 0.95f : 0.75f), Name(button), GameFont.Small);
			}
			if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && Mouse.IsOver(rect))
			{
				Pressed(button);
				Event.current.Use();
			}
		}

		/// <summary>A finger came down on a button (its window took the press).</summary>
		private static void Pressed(int button)
		{
			touching = button;
			touchStart = UI.MousePositionOnUIInverted;
			grabOffset = shown[button] - touchStart;
			touchTime = Time.realtimeSinceStartupAsDouble;
			dragged = restedLong = false;
		}

		// ---- Options → Interface ----

		/// <summary>The row on the interface page of the options (after the key wheel's).</summary>
		public static void OptionRow(Listing_Standard listing)
		{
			bool check = enabled;
			listing.CheckboxLabeled(Texts.Chinese ? "显示鼠标左右键" : "Show the mouse buttons", ref check, Texts.Chinese
				? "游戏中屏幕上单独的两个圆钮“左键”和“右键”，拖动可以各自移动位置。点一下“右键”（变黄，写着“一次”）：接下来的一次触摸就是鼠标右键点击，手指抬起后自动回到左键；再点一下“右键”就取消。拖动也算这一次触摸（按着右键拖，地图不会跟着动），所以要先把地图拖到目标附近，再点“右键”。按住“右键”不动半秒（变红，写着“锁定”）：之后每次触摸都是右键，单指拖不动地图，直到再点一下“左键”或“右键”。点“左键”随时回到左键。中键在按键盘里。按键盘的“设置”里也能添加“鼠标左键”“鼠标右键”，用法和这两个圆钮一样（关掉这一项，只用按键盘里的也可以）。接着鼠标时不显示。"
				: "Two round buttons of their own on the screen in a game, \"LMB\" and \"RMB\"; drag either to move it. A tap on RMB (yellow, \"once\"): the next touch is a right click, and touches are left clicks again once that finger lifts; another tap on RMB takes it back. A drag is that touch too (the right button held, the map does not follow): drag the map to the target first, then tap RMB. Hold RMB still for half a second (red, \"locked\"): every touch is a right click, and one finger does not move the map, until LMB or RMB is tapped. A tap on LMB always goes back to left clicks. The middle button is on the key wheel. The key wheel's setup can add a left and a right mouse button too, which work as these do (with this option off, the wheel's alone will do). Not shown while a mouse is connected.", 30f, 0.6f);
			if (check != enabled)
			{
				enabled = check;
				Settings.Set("mouseButtons", check ? "1" : "0");
			}
		}
	}
}
