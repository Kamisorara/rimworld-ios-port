using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PortMobile
{
	/// <summary>
	/// A wheel of keys for a finger: a round button ("按键") at the edge of the screen, shown while a game is played. A
	/// tap opens a ring of round keys around it (turned towards the middle of the screen, a second ring where one is not
	/// enough), another tap closes it. A key is one of:
	///   the game's key bindings (Options → Controls: pause, the speeds, rotating, drafting, the main tabs, the camera,
	///   and what mods add), shown by the key bound to it;
	///   any key of a PC keyboard, or a combination of one with Ctrl, Shift and Alt (Ctrl+Z), shown by its name (the
	///   user's, 2026-10-20: "all keyboard keys, and some are combinations");
	/// while the finger holds it, the engine sees that key (with the combination's modifiers first) held down, as real
	/// key events through the Android GLFW, so the game and mods do on it what they do on a PC keyboard.
	/// Four keys of the phone's own switch on with a tap and stay on until tapped again: Ctrl, Shift and Alt (LatchedKey:
	/// the key held down meanwhile, so that taps on the screen and the other keys come with it), and the middle mouse
	/// button (a finger on the screen is then the middle button, see MouseButtons and Touch; the wheel itself is always
	/// tapped with the left). The left and the right button were switches of the wheel too until 2026-10-30; they are
	/// buttons of their own now (MouseButtons), and the wheel's keys go round them. Since 2026-11-03 the setup can put a
	/// 左键 and a 右键 on the wheel again, as keys that do what those buttons do (a tap; a hold on 右键 locks it), on the
	/// same state; the ring closes as its 右键 switches a right click on, so that the whole map is free for it. While the
	/// ring is closed the button shows what is on, the right mouse button too. The last key opens the setup: keys added
	/// from the full list (with Ctrl / Shift / Alt ticked: as combinations), taken out, moved; the defaults back; the
	/// button, and the mouse buttons, back to their places. The button is moved by dragging it.
	/// A key can be given a text of the player's own, shown on it instead of the key (the user, 2026-10-24: with many keys
	/// one forgets what each is for): "改名" in the setup, kept in mobile.txt as keyWheelLabel.&lt;key's name&gt;=text.
	/// Left out while a hardware keyboard is in use (InputMode).
	/// mobile.txt: keyWheel=0 leaves it out (Options → Interface has the switch); keyWheelKeys=defName,@Shift,@K:Ctrl+Z,...;
	/// keyWheelAt=X,Y (the button's centre, interface units).
	/// </summary>
	internal static class KeyWheel
	{
		[DllImport("glfw")] private static extern void glfwAndroidInjectKey(int key, int action, int mods);

		private const int Press = 1, Release = 0;
		private const int HubId = 73195801, SlotIdBase = 73195810;
		internal const float HubSize = 54f, Spacing = 8f;
		private const float SlotSize = 64f;
		private const int SetupSlot = -3, NoTouch = -2, HubTouch = -1;

		// The phone's own keys (their names in keyWheelKeys).
		internal const string ShiftName = "@Shift", CtrlName = "@Ctrl", AltName = "@Alt", MouseMiddleName = "@MouseMiddle";
		// The left and the right mouse button: switches of the wheel until keyWheelVersion 3, which took them out of the keys
		// kept; since 2026-11-03 keys again that do what MouseButtons' round buttons do, put on the wheel by the player only.
		internal const string MouseLeftName = "@MouseLeft", MouseRightName = "@MouseRight";
		// Not a switch: a tap records the moment for a report (Recorder.Mark: a screenshot and the game's state, then the
		// player's words on it).
		internal const string ReportName = "@Report";
		internal static readonly string[] Specials = { CtrlName, ShiftName, AltName, MouseLeftName, MouseRightName, MouseMiddleName, ReportName };
		// A keyboard key in keyWheelKeys: "@K:" and the modifiers and the key's name joined by "+" (@K:Z, @K:Ctrl+Shift+S).
		internal const string RawPrefix = "@K:";

		public static readonly string[] Defaults = { "TogglePause", "TimeSpeed_Normal", "TimeSpeed_Fast", "TimeSpeed_Superfast", ShiftName, MouseMiddleName, "Designator_RotateLeft", "Designator_RotateRight", "Command_ColonistDraft", "NextColonist", ReportName };

		/// <summary>A key of the keyboard: GLFW's code, the name it is stored by (letters and digits only), what it shows.</summary>
		internal struct KeyboardKey
		{
			public int Code;
			public string Name, Shown;
		}

		/// <summary>Every key of a PC keyboard that the Android GLFW can give the engine (the modifiers are LatchedKey).</summary>
		internal static readonly KeyboardKey[] Keyboard = BuildKeyboard();

		private static KeyboardKey[] BuildKeyboard()
		{
			var list = new List<KeyboardKey>();
			void Add(int code, string name, string shown = null) => list.Add(new KeyboardKey { Code = code, Name = name, Shown = shown ?? name });
			for (char c = 'A'; c <= 'Z'; c++) Add(c, c.ToString());
			for (char c = '0'; c <= '9'; c++) Add(c, c.ToString());
			for (int f = 1; f <= 12; f++) Add(289 + f, "F" + f);
			Add(32, "Space");
			Add(257, "Enter");
			Add(256, "Esc");
			Add(258, "Tab");
			Add(259, "Backspace");
			Add(260, "Insert");
			Add(261, "Delete");
			Add(268, "Home");
			Add(269, "End");
			Add(266, "PageUp");
			Add(267, "PageDown");
			Add(265, "Up", "↑");
			Add(264, "Down", "↓");
			Add(263, "Left", "←");
			Add(262, "Right", "→");
			Add(96, "BackQuote", "`");
			Add(45, "Minus", "-");
			Add(61, "Equals", "=");
			Add(91, "LeftBracket", "[");
			Add(93, "RightBracket", "]");
			Add(92, "Backslash", "\\");
			Add(59, "Semicolon", ";");
			Add(39, "Quote", "'");
			Add(44, "Comma", ",");
			Add(46, "Period", ".");
			Add(47, "Slash", "/");
			for (int k = 0; k <= 9; k++) Add(320 + k, "Kp" + k);
			Add(330, "KpPeriod", "Kp.");
			Add(331, "KpDivide", "Kp/");
			Add(332, "KpMultiply", "Kp*");
			Add(333, "KpMinus", "Kp-");
			Add(334, "KpPlus", "Kp+");
			Add(335, "KpEnter");
			return list.ToArray();
		}

		private static bool enabled, expanded;
		private static Vector2 hubCenter = new Vector2(-1f, -1f);
		// Where the button is shown: its place, moved off the camera's hole if it lies over it (Cutout). The keys go round
		// this, and touches are taken here (a window moved by Cutout alone left the ring around the old place).
		private static Vector2 shownCenter = new Vector2(-1f, -1f);
		private static List<string> names = new List<string>();
		private static List<WheelKey> keys;
		// The players' texts for keys, by the key's name ("": none), read from mobile.txt as they are first asked for.
		private static readonly Dictionary<string, string> labels = new Dictionary<string, string>();
		private static readonly List<Vector2> slotCenters = new List<Vector2>();
		private static int drawnFrame = -100;

		private static int touching = NoTouch;
		// Where the finger landed, the button's centre from there, and where the button was shown then.
		private static Vector2 touchStart, grabOffset, pickedUpAt;
		private static bool dragged;
		// When the finger landed on a key, and whether it has rested on the wheel's 右键 long enough for the lock.
		private static double touchTime;
		private static bool restedLong;
		// The GLFW keys held for the key the finger is on, in the order they went down.
		private static readonly List<int> held = new List<int>();
		private static int heldFrame;
		private static bool traced;
		private static Texture2D disc;

		/// <summary>Shown: switched on, and no hardware keyboard in use (InputMode), whose keys are all there.</summary>
		public static bool Enabled => enabled && !InputMode.Keyboard;

		public static void Start()
		{
			enabled = Settings.Get("keyWheel", true);
			string list = Settings.Get("keyWheelKeys", null);
			names = list != null ? list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).ToList() : Defaults.ToList();
			int version = Settings.Get("keyWheelVersion", 1);
			if (list != null && version < 3)
			{
				int before = names.Count;
				if (version < 2 && !names.Any(n => n.StartsWith("@")))
				{
					// Keys chosen before the wheel had keys of its own (the Shift button was apart then): those go in, once. Of
					// the mouse buttons that version added, the middle one is left (below).
					names.InsertRange(Math.Min(4, names.Count), new[] { ShiftName, MouseMiddleName });
				}
				// The left and the right mouse button are buttons of their own since version 3 (MouseButtons): they leave the
				// keys chosen before, and the rest stays as the player had it (the order, combinations, names given to keys).
				if (names.RemoveAll(n => n == MouseLeftName || n == MouseRightName) > 0)
				{
					Console.WriteLine("[mobile] key wheel: the left and right mouse buttons are buttons of their own now, out of the keys");
				}
				if (names.Count != before)
				{
					Settings.Set("keyWheelKeys", string.Join(",", names));
				}
			}
			if (version < 3)
			{
				Settings.Set("keyWheelVersion", "3");
			}
			if (TryParsePlace(Settings.Get("keyWheelAt", null), out Vector2 at))
			{
				hubCenter = at;
			}
			Console.WriteLine("[mobile] key wheel " + (enabled ? "on" : "off") + ", " + names.Count + " keys");
		}

		/// <summary>A place kept in mobile.txt ("X,Y", interface units); false when there is none or it does not read.</summary>
		internal static bool TryParsePlace(string text, out Vector2 place)
		{
			place = default(Vector2);
			if (text == null)
			{
				return false;
			}
			string[] parts = text.Split(',');
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			if (parts.Length == 2 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out float x) && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out float y))
			{
				place = new Vector2(x, y);
				return true;
			}
			return false;
		}

		/// <summary>A place as mobile.txt keeps it.</summary>
		internal static string PlaceText(Vector2 place)
		{
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			return place.x.ToString("F0", inv) + "," + place.y.ToString("F0", inv);
		}

		// ---- the keys ----

		internal static List<WheelKey> Keys
		{
			get
			{
				if (keys == null)
				{
					keys = names.Select(WheelKey.Named).Where(k => k != null).ToList();
				}
				return keys;
			}
		}

		/// <summary>The player's text for a key ("" when it has none).</summary>
		internal static string CustomLabel(WheelKey key)
		{
			if (!labels.TryGetValue(key.Name, out string text))
			{
				labels[key.Name] = text = Settings.Get("keyWheelLabel." + key.Name, "");
			}
			return text;
		}

		/// <summary>Sets the player's text for a key (empty: back to the key itself) and keeps it.</summary>
		internal static void SetLabel(WheelKey key, string text)
		{
			text = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
			labels[key.Name] = text;
			Settings.Set("keyWheelLabel." + key.Name, text);
		}

		internal static void SetKeys(List<WheelKey> wheelKeys)
		{
			keys = wheelKeys.ToList();
			names = keys.Select(k => k.Name).ToList();
			Settings.Set("keyWheelKeys", string.Join(",", names));
		}

		/// <summary>The wheel is on the screen: drawn in the last few frames (not in the screenshot mode, nor while the game loads).</summary>
		internal static bool Drawn => Time.frameCount - drawnFrame <= 3;

		/// <summary>Whether the wheel holds a 左键 or a 右键 of its own (MouseButtons keeps the right button on for them).</summary>
		internal static bool HasMouseKeys => names.Contains(MouseLeftName) || names.Contains(MouseRightName);

		/// <summary>Whether this point (interface units) is on the wheel as it was last drawn: its button, and its keys while it is open.</summary>
		internal static bool Covers(Vector2 ui)
		{
			return Drawn && OnWheel(ui);
		}

		/// <summary>Whether a finger at this point (window pixels) is on the wheel: its button, or a key of the open ring.</summary>
		internal static bool At(Vector2 pixels)
		{
			return Enabled && Current.ProgramState == ProgramState.Playing && Time.frameCount - drawnFrame <= 3 && OnWheel(pixels / Prefs.UIScale);
		}

		/// <summary>
		/// The windows' squares, not the discs drawn in them: a window takes every press on it, in its corners too. Told by
		/// circles, a press in a corner of the button's window spent a right click waiting for the next touch on nothing,
		/// and one just beside the window was a left click while the right one went on waiting.
		/// </summary>
		private static bool OnWheel(Vector2 ui)
		{
			if (Square(shownCenter, HubSize).Contains(ui))
			{
				return true;
			}
			if (expanded)
			{
				foreach (Vector2 c in slotCenters)
				{
					if (Square(c, SlotSize).Contains(ui))
					{
						return true;
					}
				}
			}
			return false;
		}

		/// <summary>
		/// Whether the wheel's button lies on this rect (interface units) where it rests: as it was last drawn, or, while a
		/// finger drags it, where the finger picked it up. The mouse buttons' first places make room for it there (it is
		/// drawn above them), and they do not follow the finger.
		/// </summary>
		internal static bool RestsOn(Rect rect, out Rect hub)
		{
			hub = Square(touching == HubTouch && dragged ? pickedUpAt : shownCenter, HubSize);
			return Drawn && hub.Overlaps(rect);
		}

		private static Rect Square(Vector2 center, float size)
		{
			return new Rect(center.x - size / 2f, center.y - size / 2f, size, size);
		}

		private static LatchedKey Latched(WheelKey key)
		{
			switch (key.Name)
			{
				case ShiftName: return LatchedKey.Shift;
				case CtrlName: return LatchedKey.Ctrl;
				case AltName: return LatchedKey.Alt;
				default: return null;
			}
		}

		private static bool IsOn(WheelKey key)
		{
			LatchedKey latched = Latched(key);
			if (latched != null)
			{
				return latched.On;
			}
			return key.Name == MouseMiddleName && MouseButtons.Active == MouseButtons.Middle;
		}

		/// <summary>A tap on one of the phone's own keys: Ctrl, Shift or Alt on or off; the middle mouse button on (tapped again: back to the left one).</summary>
		private static void Toggle(WheelKey key)
		{
			if (key.Name == ReportName)
			{
				// The ring closes first: the screenshot is of the game. Then the player may say what went wrong.
				expanded = false;
				SoundDefOf.Tick_High.PlayOneShotOnCamera();
				Recorder.Mark(askForWords: true);
				return;
			}
			bool on;
			LatchedKey latched = Latched(key);
			if (latched != null)
			{
				on = !latched.On;
				latched.Set(on);
			}
			else
			{
				on = MouseButtons.ToggleMiddle();
			}
			(on ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
		}

		internal static void SetEnabled(bool value)
		{
			if (enabled == value)
			{
				return;
			}
			enabled = value;
			Settings.Set("keyWheel", value ? "1" : "0");
			if (!value)
			{
				ReleaseKey();
				expanded = false;
			}
		}

		internal static void ResetPlace()
		{
			hubCenter = DefaultCenter();
			Settings.Set("keyWheelAt", "");
		}

		private static Vector2 DefaultCenter()
		{
			// The left edge, a little below the middle.
			return new Vector2(14f + HubSize / 2f, Mathf.Round(UI.screenHeight * 0.55f));
		}

		/// <summary>What a key shows on the wheel: the key it is bound to ("" when none), the keyboard key or combination, or the phone key's name.</summary>
		internal static string KeyText(WheelKey key)
		{
			if (key.IsRaw)
			{
				return key.RawShown;
			}
			if (key.Def == null)
			{
				switch (key.Name)
				{
					case ShiftName: return "Shift";
					case CtrlName: return "Ctrl";
					case AltName: return "Alt";
					case ReportName: return Texts.Chinese ? "记录\n问题" : "Report";
					case MouseLeftName: return MouseButtons.Name(MouseButtons.Left);
					case MouseRightName: return MouseButtons.Name(MouseButtons.Right);
					default: return MouseButtons.Name(MouseButtons.Middle);
				}
			}
			KeyCode bound = BoundKey(key.Def);
			return bound == KeyCode.None ? "" : bound.ToStringReadable();
		}

		/// <summary>The modifiers' names in GLFW's mods, in the order they are written and pressed (Ctrl+Shift+Alt+).</summary>
		internal static string ModifierText(int mods)
		{
			string text = "";
			foreach (LatchedKey modifier in LatchedKey.All)
			{
				if ((mods & modifier.Mod) != 0)
				{
					text += modifier.Name + "+";
				}
			}
			return text;
		}

		/// <summary>The key a binding answers to now (the player's first choice, else the second); None when it has none a finger can give.</summary>
		internal static KeyCode BoundKey(KeyBindingDef def)
		{
			KeyCode key = KeyCode.None;
			try
			{
				key = KeyPrefs.KeyPrefsData.GetBoundKeyCode(def, KeyPrefs.BindingSlot.A);
				if (GlfwKey(key) < 0)
				{
					key = KeyPrefs.KeyPrefsData.GetBoundKeyCode(def, KeyPrefs.BindingSlot.B);
				}
			}
			catch (Exception)
			{
			}
			return GlfwKey(key) < 0 ? KeyCode.None : key;
		}

		/// <summary>The GLFW key code of a Unity key code (what the engine turns back into the same KeyCode); -1 for none (mouse buttons, keys GLFW does not have).</summary>
		internal static int GlfwKey(KeyCode key)
		{
			int k = (int)key;
			if (key >= KeyCode.A && key <= KeyCode.Z) return 65 + (k - (int)KeyCode.A);
			if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return 48 + (k - (int)KeyCode.Alpha0);
			if (key >= KeyCode.F1 && key <= KeyCode.F15) return 290 + (k - (int)KeyCode.F1);
			if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) return 320 + (k - (int)KeyCode.Keypad0);
			switch (key)
			{
				case KeyCode.Space: return 32;
				case KeyCode.Quote: return 39;
				case KeyCode.Comma: return 44;
				case KeyCode.Minus: return 45;
				case KeyCode.Period: return 46;
				case KeyCode.Slash: return 47;
				case KeyCode.Semicolon: return 59;
				case KeyCode.Equals: return 61;
				case KeyCode.LeftBracket: return 91;
				case KeyCode.Backslash: return 92;
				case KeyCode.RightBracket: return 93;
				case KeyCode.BackQuote: return 96;
				case KeyCode.Escape: return 256;
				case KeyCode.Return: return 257;
				case KeyCode.Tab: return 258;
				case KeyCode.Backspace: return 259;
				case KeyCode.Insert: return 260;
				case KeyCode.Delete: return 261;
				case KeyCode.RightArrow: return 262;
				case KeyCode.LeftArrow: return 263;
				case KeyCode.DownArrow: return 264;
				case KeyCode.UpArrow: return 265;
				case KeyCode.PageUp: return 266;
				case KeyCode.PageDown: return 267;
				case KeyCode.Home: return 268;
				case KeyCode.End: return 269;
				case KeyCode.KeypadPeriod: return 330;
				case KeyCode.KeypadDivide: return 331;
				case KeyCode.KeypadMultiply: return 332;
				case KeyCode.KeypadMinus: return 333;
				case KeyCode.KeypadPlus: return 334;
				case KeyCode.KeypadEnter: return 335;
				case KeyCode.KeypadEquals: return 336;
				case KeyCode.LeftShift: return 340;
				case KeyCode.LeftControl: return 341;
				case KeyCode.LeftAlt: return 342;
				case KeyCode.RightShift: return 344;
				case KeyCode.RightControl: return 345;
				case KeyCode.RightAlt: return 346;
				default: return -1;
			}
		}

		private static void HoldKey(KeyBindingDef def)
		{
			ReleaseKey();
			int glfw = GlfwKey(BoundKey(def));
			if (glfw < 0)
			{
				// No key to press: the player picks one now (BindKeys), and the next tap presses it.
				Messages.Message(Texts.Chinese ? "“" + def.LabelCap + "”还没有绑定键盘按键：先选一个，之后点这个键就会按它。" : "\"" + def.LabelCap + "\" has no key yet: choose one, then this button presses it.", MessageTypeDefOf.NeutralEvent, false);
				BindKeys.Ask(def);
				return;
			}
			HoldCombination(0, glfw);
		}

		/// <summary>
		/// A key and its modifiers down: the modifiers first (Ctrl, Shift, Alt; those switched on in the wheel are down
		/// already), then the key, as fingers on a keyboard do; the engine reads the modifiers from the keys it sees held.
		/// </summary>
		private static void HoldCombination(int mods, int key)
		{
			ReleaseKey();
			int down = LatchedKey.Mods;
			foreach (LatchedKey modifier in LatchedKey.All)
			{
				if ((mods & modifier.Mod) != 0 && (down & modifier.Mod) == 0)
				{
					glfwAndroidInjectKey(modifier.Glfw, Press, down);
					held.Add(modifier.Glfw);
					down |= modifier.Mod;
				}
			}
			glfwAndroidInjectKey(key, Press, down);
			held.Add(key);
			heldFrame = Time.frameCount;
			traced = false;
		}

		/// <summary>A binding's key pressed and let go at once, for a button that stands in for it (LandingConfirm's rotate buttons).</summary>
		internal static void TapKey(KeyBindingDef def)
		{
			int glfw = GlfwKey(BoundKey(def));
			if (glfw < 0)
			{
				return;
			}
			int mods = LatchedKey.Mods;
			glfwAndroidInjectKey(glfw, Press, mods);
			glfwAndroidInjectKey(glfw, Release, mods);
		}

		/// <summary>The keys of the last key up again, the other way round.</summary>
		private static void ReleaseKey()
		{
			for (int i = held.Count - 1; i >= 0; i--)
			{
				glfwAndroidInjectKey(held[i], Release, 0);
			}
			held.Clear();
		}

		// ---- every frame ----

		/// <summary>Once a frame: the finger that holds a key or drags the button followed; everything let go when no game is played.</summary>
		public static void Update()
		{
			if (!Enabled && (expanded || touching != NoTouch))
			{
				// Put away (a keyboard came): what a finger held on it goes up.
				expanded = false;
				touching = NoTouch;
				ReleaseKey();
			}
			if (touching == NoTouch)
			{
				if (held.Count > 0)
				{
					ReleaseKey();
				}
				return;
			}
			bool down = Input.GetMouseButton(0);
			if (touching == HubTouch && down)
			{
				Vector2 at = UI.MousePositionOnUIInverted;
				if (!dragged && (at - touchStart).magnitude > 14f)
				{
					dragged = true;
				}
				if (dragged)
				{
					hubCenter = Clamp(at + grabOffset, HubSize / 2f);
				}
				return;
			}
			if (down && Current.ProgramState == ProgramState.Playing)
			{
				if (!restedLong && IsMouseKey(touching, MouseRightName) && Time.realtimeSinceStartupAsDouble - touchTime >= Touch.HoldSeconds)
				{
					// As on the round 右键: the lock comes when the finger lifts, the sound now.
					restedLong = true;
					MouseButtons.HoldSound();
				}
				if (Touch.Verbose && held.Count > 0 && !traced && Time.frameCount > heldFrame + 2)
				{
					// What the engine sees of a key held from the wheel (touch-verbose.txt).
					traced = true;
					Console.WriteLine("[mobile] key wheel holds " + string.Join(",", held) + ": the engine sees Ctrl " + Input.GetKey(KeyCode.LeftControl) + ", Shift " + Input.GetKey(KeyCode.LeftShift) + ", Alt " + Input.GetKey(KeyCode.LeftAlt) + ", any key " + Input.anyKey);
				}
				return;
			}
			// The finger lifted.
			int was = touching;
			touching = NoTouch;
			ReleaseKey();
			if (was == HubTouch)
			{
				if (dragged)
				{
					Settings.Set("keyWheelAt", PlaceText(hubCenter));
				}
				else
				{
					expanded = !expanded;
					(expanded ? SoundDefOf.Tick_High : SoundDefOf.Tick_Low).PlayOneShotOnCamera();
				}
			}
			else if (was == SetupSlot && Current.ProgramState == ProgramState.Playing)
			{
				// The ring closes: it would lie over the setup window (it is drawn above every window).
				expanded = false;
				Find.WindowStack.Add(new Dialog_KeyWheel());
			}
			else if (IsMouseKey(was, MouseLeftName) && Current.ProgramState == ProgramState.Playing)
			{
				MouseButtons.TapLeft("a tap on the key wheel's 左键");
			}
			else if (IsMouseKey(was, MouseRightName) && Current.ProgramState == ProgramState.Playing)
			{
				bool on = true;
				if (restedLong)
				{
					MouseButtons.LockRight("a hold on the key wheel's 右键");
				}
				else
				{
					on = MouseButtons.TapRight("a tap on the key wheel's 右键");
				}
				if (on)
				{
					// The right click is for the map: the ring closes (its button says "右键" meanwhile), as for 记录问题.
					expanded = false;
				}
			}
		}

		/// <summary>Whether this key of the ring (an index of Keys) is the wheel's 左键 or 右键 (MouseLeftName, MouseRightName).</summary>
		private static bool IsMouseKey(int index, string name)
		{
			return index >= 0 && index < Keys.Count && Keys[index].Name == name;
		}

		/// <summary>A round button's centre kept on the screen (two units from its edges).</summary>
		internal static Vector2 Clamp(Vector2 center, float radius)
		{
			return new Vector2(Mathf.Clamp(center.x, radius + 2f, UI.screenWidth - radius - 2f), Mathf.Clamp(center.y, radius + 2f, UI.screenHeight - radius - 2f));
		}

		/// <summary>From the end of the play screen's interface, every frame: the button's window, and the keys' while it is open.</summary>
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
			if (hubCenter.x < 0f)
			{
				hubCenter = DefaultCenter();
			}
			hubCenter = Clamp(hubCenter, HubSize / 2f);
			shownCenter = Cutout.ClearOf(new Rect(hubCenter.x - HubSize / 2f, hubCenter.y - HubSize / 2f, HubSize, HubSize)).center;
			drawnFrame = Time.frameCount;
			if (expanded)
			{
				// One window for each key: the gaps between them stay the map's.
				List<WheelKey> defs = Keys;
				Layout(defs.Count + 1);
				for (int i = 0; i < slotCenters.Count; i++)
				{
					int index = i < defs.Count ? i : SetupSlot;
					Vector2 c = slotCenters[i];
					var rect = new Rect(c.x - SlotSize / 2f, c.y - SlotSize / 2f, SlotSize, SlotSize);
					Find.WindowStack.ImmediateWindow(SlotIdBase + i, rect, WindowLayer.Super, () => DrawSlot(index), doBackground: false, absorbInputAroundWindow: false, shadowAlpha: 0f);
				}
			}
			var hubRect = new Rect(shownCenter.x - HubSize / 2f, shownCenter.y - HubSize / 2f, HubSize, HubSize);
			Find.WindowStack.ImmediateWindow(HubId, hubRect, WindowLayer.Super, DrawHub, doBackground: false, absorbInputAroundWindow: false, shadowAlpha: 0f);
		}

		/// <summary>
		/// The keys' centres: on rings around the button, the first one beside the button and the rest alternately to
		/// either side of the direction of the middle of the screen; places off the screen, over the camera's hole or on
		/// the mouse buttons (MouseButtons: a key there would take their touches, or they the key's) are passed over, and a
		/// ring that is full gives way to a wider one.
		/// </summary>
		private static void Layout(int count)
		{
			slotCenters.Clear();
			var middle = new Vector2(UI.screenWidth / 2f, UI.screenHeight / 2f);
			Vector2 towards = middle - shownCenter;
			float baseAngle = Mathf.Atan2(towards.y, towards.x);
			float margin = SlotSize / 2f + 4f;
			for (int ring = 0; ring < 5 && slotCenters.Count < count; ring++)
			{
				float radius = HubSize / 2f + SlotSize / 2f + 10f + ring * (SlotSize + Spacing);
				int places = Mathf.Max(1, Mathf.FloorToInt(2f * Mathf.PI * radius / (SlotSize + Spacing)));
				float step = 2f * Mathf.PI / places;
				for (int k = 0; k < places && slotCenters.Count < count; k++)
				{
					int side = (k % 2 == 1) ? (k + 1) / 2 : -(k / 2);
					float angle = baseAngle + side * step;
					Vector2 p = shownCenter + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
					var slot = new Rect(p.x - SlotSize / 2f, p.y - SlotSize / 2f, SlotSize, SlotSize);
					if (p.x < margin || p.y < margin || p.x > UI.screenWidth - margin || p.y > UI.screenHeight - margin || Cutout.OverHole(slot) || MouseButtons.Overlaps(slot))
					{
						continue;
					}
					slotCenters.Add(p);
				}
			}
		}

		// ---- drawing ----

		private static Texture2D Disc
		{
			get
			{
				if (disc == null)
				{
					const int size = 96;
					disc = new Texture2D(size, size, TextureFormat.RGBA32, false);
					var pixels = new Color32[size * size];
					float r = size / 2f - 1f;
					for (int y = 0; y < size; y++)
					{
						for (int x = 0; x < size; x++)
						{
							float d = new Vector2(x + 0.5f - size / 2f, y + 0.5f - size / 2f).magnitude;
							float alpha = Mathf.Clamp01(r - d + 0.5f);
							// A lighter rim, three pixels wide.
							byte shade = (byte)(d > r - 3f ? 255 : 200);
							pixels[y * size + x] = new Color32(shade, shade, shade, (byte)(alpha * 255f));
						}
					}
					disc.SetPixels32(pixels);
					disc.Apply();
				}
				return disc;
			}
		}

		/// <summary>A round key or button: the disc in its colour, the label in the middle (the mouse buttons are drawn so too).</summary>
		internal static void DrawRound(Rect rect, Color fill, string label, GameFont font)
		{
			if (Event.current.type != EventType.Repaint)
			{
				return;
			}
			Color colour = GUI.color;
			TextAnchor anchor = Text.Anchor;
			GameFont oldFont = Text.Font;
			bool wrap = Text.WordWrap;
			GUI.color = fill;
			GUI.DrawTexture(rect, Disc);
			GUI.color = Color.white;
			Text.Font = font;
			Text.Anchor = TextAnchor.MiddleCenter;
			Text.WordWrap = true;
			Widgets.Label(rect.ContractedBy(rect.width * 0.12f), label);
			Text.WordWrap = wrap;
			Text.Anchor = anchor;
			Text.Font = oldFont;
			GUI.color = colour;
		}

		private static void DrawHub()
		{
			var rect = new Rect(0f, 0f, HubSize, HubSize);
			bool held = touching == HubTouch;
			// Closed, it says what is switched on: Ctrl, Shift, Alt, a mouse button other than the left (the right one is the
			// mouse buttons' own, MouseButtons).
			string status = "";
			if (!expanded)
			{
				status = ModifierText(LatchedKey.Mods).TrimEnd('+');
				if (MouseButtons.Active != MouseButtons.Left)
				{
					status += (status.Length > 0 ? "\n" : "") + MouseButtons.Name(MouseButtons.Active);
				}
			}
			Color fill = expanded ? new Color(0.35f, 0.45f, 0.6f, held ? 1f : 0.95f) : status.Length > 0 ? new Color(0.85f, 0.65f, 0.2f, held ? 1f : 0.95f) : new Color(0.25f, 0.27f, 0.3f, held ? 0.95f : 0.75f);
			DrawRound(rect, fill, expanded ? (Texts.Chinese ? "收起" : "Close") : status.Length > 0 ? status : (Texts.Chinese ? "按键" : "Keys"), status.Length > 0 ? GameFont.Tiny : GameFont.Small);
			if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && Mouse.IsOver(rect))
			{
				touching = HubTouch;
				touchStart = UI.MousePositionOnUIInverted;
				grabOffset = shownCenter - touchStart;
				pickedUpAt = shownCenter;
				dragged = false;
				Event.current.Use();
			}
		}

		private static void DrawSlot(int index)
		{
			var rect = new Rect(0f, 0f, SlotSize, SlotSize);
			bool held = touching == index;
			string label;
			Color fill;
			if (index == SetupSlot)
			{
				label = Texts.Chinese ? "设置" : "Setup";
				fill = new Color(0.3f, 0.3f, 0.32f, held ? 1f : 0.85f);
			}
			else
			{
				WheelKey key = Keys[index];
				label = KeyText(key);
				string custom = CustomLabel(key);
				// 左键 and 右键 are not switches of the wheel (MouseButtons holds what they switch): they light up as they are pressed.
				bool on = key.IsSwitch && key.Name != ReportName && key.Name != MouseLeftName && key.Name != MouseRightName ? IsOn(key) : held;
				fill = label.Length == 0 ? new Color(0.3f, 0.2f, 0.2f, 0.7f) : on ? new Color(0.85f, 0.65f, 0.2f, 1f) : new Color(0.18f, 0.2f, 0.22f, 0.88f);
				if (label.Length == 0)
				{
					label = "—";
				}
				else if (key.Name == MouseRightName && MouseButtons.RightLook(held && restedLong, 1f, out Color rightFill, out string word))
				{
					// As the round 右键 shows it: for the next touch, or locked (a finger that has rested long enough shows the
					// lock before it lifts); the player's text for it in place of its name.
					fill = rightFill;
					label = (custom.Length > 0 ? custom : label) + "\n" + word;
					custom = "";
				}
				else if (key.IsRaw && key.RawMods != 0)
				{
					// A combination a line a key: "Ctrl", "Shift", "A" (written in one line it wraps anywhere in the round key).
					string modifiers = ModifierText(key.RawMods);
					label = modifiers.TrimEnd('+').Replace("+", "\n") + "\n" + key.RawShown.Substring(modifiers.Length);
				}
				if (custom.Length > 0)
				{
					// The player's text, and the key itself small and grey under it.
					string keyText = key.IsRaw ? key.RawShown : KeyText(key).Replace("\n", "");
					bool both = keyText.Length > 0 && keyText != custom;
					DrawRound(rect, fill, both ? custom + "\n<color=#b4b4b4>" + keyText + "</color>" : custom, !both && custom.Length <= 3 ? GameFont.Small : GameFont.Tiny);
					label = null;
				}
			}
			if (label != null)
			{
				DrawRound(rect, fill, label, label.Length <= 5 ? GameFont.Small : GameFont.Tiny);
			}
			if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && Mouse.IsOver(rect))
			{
				touching = index;
				if (index >= 0)
				{
					WheelKey key = Keys[index];
					if (key.Def != null)
					{
						HoldKey(key.Def);
					}
					else if (key.IsRaw)
					{
						HoldCombination(key.RawMods, key.RawKey);
					}
					else if (key.Name == MouseLeftName || key.Name == MouseRightName)
					{
						// As on the round buttons, what they do comes as the finger lifts (Update): a hold on 右键 locks it.
						touchTime = Time.realtimeSinceStartupAsDouble;
						restedLong = false;
					}
					else
					{
						Toggle(key);
					}
				}
				Event.current.Use();
			}
		}

		// ---- Options → Interface ----

		public static void OptionRow(Listing_Standard listing)
		{
			bool check = enabled;
			listing.CheckboxLabeled(Texts.Chinese ? "显示自定义按键盘" : "Show the key wheel", ref check, Texts.Chinese
				? "游戏中屏幕边上的圆形“按键”按钮：点一下展开一圈按键，每个按键上写着它对应的键盘按键或组合键（按住就是按住那个键），再点一下收起；拖动可以移动位置。Ctrl、Shift、Alt 和鼠标中键是开关，点一下打开、再点一下关闭：开着 Ctrl 时，点屏幕和按别的键都带着 Ctrl；开着中键时，在屏幕上点一下就是鼠标中键点击。鼠标左键、右键是屏幕上单独的两个圆钮（见下一项），也可以在“设置”里加到按键盘上，用法一样（点按键盘上的“右键”后这一圈会收起，好点地图）。展开后最后一个按键“设置”可以从游戏快捷键和键盘全部按键里增删按键，勾选 Ctrl / Shift / Alt 可以添加组合键，点“改名”给按键起个名字（比如“征召”），显示在按键上。接着实体键盘时不显示。"
				: "The round \"Keys\" button at the edge of the screen in a game: a tap opens a ring of keys, each showing the keyboard key or combination it stands for (holding it holds the key); another tap closes it; drag it to move it. Ctrl, Shift, Alt and the middle mouse button are switches, on with a tap and off with another: with Ctrl on, taps on the screen and the other keys come with Ctrl; with the middle button on, a tap on the screen is a middle click. The left and right mouse buttons are two round buttons of their own (the next option); the setup can put them on the wheel too, where they work the same (the ring closes after RMB, to leave the map free). The last key, \"Setup\", adds and removes keys from the game's key bindings and the whole keyboard; with Ctrl / Shift / Alt ticked it adds combinations. Not shown while a hardware keyboard is connected.", 30f, 0.6f);
			SetEnabled(check);
		}
	}

	/// <summary>The setup of the key wheel: its keys (moved, taken out), the keys to add from, the defaults, the round buttons' places.</summary>
	internal sealed class Dialog_KeyWheel : Window
	{
		private Vector2 chosenScroll, allScroll;
		private string search = "";
		// Ticked: what is added from the list is a combination with these (GLFW's mods).
		private bool withCtrl, withShift, withAlt;

		public Dialog_KeyWheel()
		{
			doCloseX = true;
			doCloseButton = false;
			closeOnClickedOutside = true;
			absorbInputAroundWindow = true;
			forcePause = true;
		}

		public override Vector2 InitialSize => new Vector2(900f, 620f);

		/// <summary>A row of the list to add from: its heading, what it says, the key it adds (null: nothing to add).</summary>
		private struct Choice
		{
			public string Category, Text;
			public WheelKey Key;
		}

		public override void DoWindowContents(Rect inRect)
		{
			bool chinese = Texts.Chinese;
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 36f), chinese ? "自定义按键盘" : "Key wheel");
			Text.Font = GameFont.Small;
			float top = inRect.y + 42f, bottom = inRect.yMax - 44f;
			float half = (inRect.width - 16f) / 2f;
			var left = new Rect(inRect.x, top, half, bottom - top);
			var right = new Rect(inRect.x + half + 16f, top, half, bottom - top);

			// The wheel's keys.
			List<WheelKey> keys = KeyWheel.Keys.ToList();
			Widgets.Label(new Rect(left.x, left.y, left.width, 26f), chinese ? "按键盘上的按键（按顺序从圆钮旁往两边排）" : "The wheel's keys (in order, from beside the button outwards)");
			var listRect = new Rect(left.x, left.y + 28f, left.width, left.height - 28f);
			float rowH = 38f;
			var view = new Rect(0f, 0f, listRect.width - 16f, Math.Max(listRect.height, keys.Count * rowH + 8f));
			Widgets.DrawMenuSection(listRect);
			Widgets.BeginScrollView(listRect, ref chosenScroll, view);
			bool changed = false;
			for (int i = 0; i < keys.Count; i++)
			{
				var row = new Rect(0f, i * rowH, view.width, rowH);
				if (i % 2 == 1)
				{
					Widgets.DrawLightHighlight(row);
				}
				string custom = KeyWheel.CustomLabel(keys[i]);
				Widgets.Label(new Rect(row.x + 6f, row.y, row.width - 196f, rowH), (custom.Length > 0 ? "<color=#f2c14e>「" + custom + "」</color> " : "") + keys[i].Label + "  <color=#999999>" + Bound(keys[i], chinese) + "</color>");
				float x = row.xMax - 132f;
				if (Widgets.ButtonText(new Rect(x - 56f, row.y + 4f, 52f, rowH - 8f), chinese ? "改名" : "Name"))
				{
					Find.WindowStack.Add(new Dialog_KeyLabel(keys[i]));
				}
				if (i > 0 && Widgets.ButtonText(new Rect(x, row.y + 4f, 40f, rowH - 8f), "↑"))
				{
					WheelKey d = keys[i];
					keys[i] = keys[i - 1];
					keys[i - 1] = d;
					changed = true;
				}
				if (i < keys.Count - 1 && Widgets.ButtonText(new Rect(x + 44f, row.y + 4f, 40f, rowH - 8f), "↓"))
				{
					WheelKey d = keys[i];
					keys[i] = keys[i + 1];
					keys[i + 1] = d;
					changed = true;
				}
				if (Widgets.ButtonText(new Rect(x + 88f, row.y + 4f, 40f, rowH - 8f), "×"))
				{
					keys.RemoveAt(i);
					changed = true;
					break;
				}
			}
			Widgets.EndScrollView();

			// What can be added: the phone's own keys, the keyboard, the game's key bindings by category.
			Widgets.Label(new Rect(right.x, right.y, 120f, 26f), chinese ? "点一项添加：" : "Tap one to add:");
			search = Widgets.TextField(new Rect(right.x + 124f, right.y, right.width - 124f, 26f), search);
			var comboRow = new Rect(right.x, right.y + 30f, right.width, 28f);
			Widgets.Label(new Rect(comboRow.x, comboRow.y, 60f, 28f), chinese ? "组合：" : "With:");
			Widgets.CheckboxLabeled(new Rect(comboRow.x + 60f, comboRow.y, 90f, 28f), "Ctrl", ref withCtrl);
			Widgets.CheckboxLabeled(new Rect(comboRow.x + 160f, comboRow.y, 90f, 28f), "Shift", ref withShift);
			Widgets.CheckboxLabeled(new Rect(comboRow.x + 260f, comboRow.y, 90f, 28f), "Alt", ref withAlt);
			int mods = (withCtrl ? LatchedKey.Ctrl.Mod : 0) | (withShift ? LatchedKey.Shift.Mod : 0) | (withAlt ? LatchedKey.Alt.Mod : 0);
			var allRect = new Rect(right.x, comboRow.yMax + 2f, right.width, right.yMax - comboRow.yMax - 2f);
			string needle = search.Trim();
			var chosen = new HashSet<string>(keys.Select(k => k.Name));
			List<Choice> all = Choices(mods, chinese)
				.Where(c => c.Key != null && !chosen.Contains(c.Key.Name) && (needle.Length == 0 || c.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 || c.Key.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
				.ToList();
			// The list's height as it is drawn below: a heading of 30 where the category changes, rows of 32 (with many mods
			// there are dozens of categories, so an estimate leaves the end out of reach).
			float allHeight = (mods != 0 ? 30f : 0f) + all.Count * 32f + 8f;
			for (int i = 0; i < all.Count; i++)
			{
				if (i == 0 || all[i].Category != all[i - 1].Category)
				{
					allHeight += 30f;
				}
			}
			var allView = new Rect(0f, 0f, allRect.width - 16f, Math.Max(allRect.height, allHeight));
			Widgets.DrawMenuSection(allRect);
			Widgets.BeginScrollView(allRect, ref allScroll, allView);
			float y = 0f;
			if (mods != 0)
			{
				GUI.color = new Color(0.7f, 0.85f, 1f);
				Widgets.Label(new Rect(6f, y + 4f, allView.width - 12f, 26f), chinese ? "现在点下面的按键会添加组合键（" + KeyWheel.ModifierText(mods) + "…）" : "A tap below adds a combination (" + KeyWheel.ModifierText(mods) + "...)");
				GUI.color = Color.white;
				y += 30f;
			}
			string lastCategory = null;
			foreach (Choice choice in all)
			{
				if (choice.Category != lastCategory)
				{
					lastCategory = choice.Category;
					GUI.color = new Color(1f, 0.85f, 0.5f);
					Widgets.Label(new Rect(6f, y + 4f, allView.width, 26f), choice.Category);
					GUI.color = Color.white;
					y += 30f;
				}
				var row = new Rect(0f, y, allView.width, 32f);
				Widgets.Label(new Rect(row.x + 14f, row.y + 3f, row.width - 20f, 28f), choice.Text);
				if (Widgets.ButtonInvisible(row))
				{
					keys.Add(choice.Key);
					changed = true;
				}
				y += 32f;
			}
			Widgets.EndScrollView();

			if (changed)
			{
				KeyWheel.SetKeys(keys);
			}

			// The buttons at the bottom.
			float bw = 160f;
			if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 38f, bw, 36f), chinese ? "恢复默认按键" : "Default keys"))
			{
				KeyWheel.SetKeys(KeyWheel.Defaults.Select(WheelKey.Named).Where(k => k != null).ToList());
			}
			var back = new Rect(inRect.x + bw + 10f, inRect.yMax - 38f, bw, 36f);
			if (Widgets.ButtonText(back, chinese ? "圆钮放回原处" : "Buttons back"))
			{
				// The mouse buttons too: round buttons like the wheel's, and nothing else in a game puts them back.
				KeyWheel.ResetPlace();
				MouseButtons.ResetPlaces();
			}
			TooltipHandler.TipRegion(back, chinese ? "“按键”“左键”“右键”三个圆钮都放回最初的位置。" : "Puts the three round buttons, Keys, LMB and RMB, back where they first were.");
			if (Widgets.ButtonText(new Rect(inRect.xMax - bw, inRect.yMax - 38f, bw, 36f), chinese ? "完成" : "Done"))
			{
				Close();
			}
		}

		/// <summary>
		/// Every key that can be added. With modifiers ticked: no switches, the keyboard keys as combinations, and a key
		/// binding as the combination of its key bound now (it is the key that is stored, not the binding).
		/// </summary>
		private static IEnumerable<Choice> Choices(int mods, bool chinese)
		{
			string grey(string s) => "  <color=#999999>" + s + "</color>";
			if (mods == 0)
			{
				foreach (string name in KeyWheel.Specials)
				{
					WheelKey key = WheelKey.Named(name);
					yield return new Choice { Category = chinese ? "手机按键" : "Phone keys", Text = key.Label + grey(Bound(key, chinese)), Key = key };
				}
			}
			string keyboard = mods == 0 ? (chinese ? "键盘按键" : "Keyboard") : (chinese ? "组合键" : "Combinations");
			for (int i = 0; i < KeyWheel.Keyboard.Length; i++)
			{
				WheelKey key = WheelKey.Raw(mods, KeyWheel.Keyboard[i]);
				yield return new Choice { Category = keyboard, Text = key.RawShown, Key = key };
			}
			foreach (KeyBindingDef def in DefDatabase<KeyBindingDef>.AllDefsListForReading.OrderBy(d => d.category != null ? d.category.index : 0))
			{
				string category = def.category != null ? def.category.LabelCap.ToString() : "";
				if (mods == 0)
				{
					var key = new WheelKey(def.defName, def);
					yield return new Choice { Category = category, Text = def.LabelCap + grey(Bound(key, chinese)), Key = key };
					continue;
				}
				int code = KeyWheel.GlfwKey(KeyWheel.BoundKey(def));
				int index = Array.FindIndex(KeyWheel.Keyboard, k => k.Code == code);
				if (index >= 0)
				{
					WheelKey key = WheelKey.Raw(mods, KeyWheel.Keyboard[index]);
					yield return new Choice { Category = category, Text = def.LabelCap + grey(key.RawShown), Key = key };
				}
			}
		}

		/// <summary>The grey text after a key's name: the key it is bound to, or what one of the phone's own does.</summary>
		private static string Bound(WheelKey key, bool chinese)
		{
			if (key.IsRaw)
			{
				return chinese ? "按住就是按住 " + key.RawShown : "held: " + key.RawShown + " held";
			}
			if (key.Def == null)
			{
				switch (key.Name)
				{
					case KeyWheel.ShiftName:
					case KeyWheel.CtrlName:
					case KeyWheel.AltName:
						return chinese ? "开关，开着时一直按住" : "switch: held while on";
					case KeyWheel.ReportName:
						return chinese ? "出问题时点一下：截图并保存当时的运行数据，再写几句说明，随“导出日志”发出" : "a tap saves a screenshot and the game's state, then a few words on it, for the logs";
					case KeyWheel.MouseLeftName:
						return chinese ? "同“左键”圆钮：点一下回到左键" : "as the round LMB: back to left clicks";
					case KeyWheel.MouseRightName:
						return chinese ? "同“右键”圆钮：点一下用一次，按住半秒锁定" : "as the round RMB: a tap for one touch, a hold locks";
					default:
						return chinese ? "开关，点屏幕即为此键" : "switch: taps are this button";
				}
			}
			string text = KeyWheel.KeyText(key);
			return text.Length == 0 ? (chinese ? "未绑定" : "unbound") : text;
		}
	}

	/// <summary>
	/// A key of the wheel: one of the game's key bindings (Def), a keyboard key with its modifiers (RawKey, RawMods), or
	/// one of the phone's own switches (KeyWheel.Specials).
	/// </summary>
	/// <summary>The text a key of the wheel shows: the player's own (KeyWheel.SetLabel).</summary>
	internal sealed class Dialog_KeyLabel : Window
	{
		private const string FieldName = "KeyWheelLabelField";
		private const int MaxLength = 12;
		private readonly WheelKey key;
		private string text;
		private bool focused;

		public Dialog_KeyLabel(WheelKey key)
		{
			this.key = key;
			text = KeyWheel.CustomLabel(key);
			doCloseX = true;
			closeOnClickedOutside = true;
			absorbInputAroundWindow = true;
			forcePause = true;
		}

		public override Vector2 InitialSize => new Vector2(560f, 260f);

		public override void DoWindowContents(Rect inRect)
		{
			bool chinese = Texts.Chinese;
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 36f), chinese ? "按键上显示的文字" : "Text on the key");
			Text.Font = GameFont.Small;
			string keyText = key.IsRaw ? key.RawShown : KeyWheel.KeyText(key).Replace("\n", "");
			Widgets.Label(new Rect(inRect.x, inRect.y + 40f, inRect.width, 50f), (chinese ? "按键：" : "Key: ") + key.Label + (keyText.Length > 0 ? "  <color=#999999>" + keyText + "</color>" : "")
				+ "\n<color=#999999>" + (chinese ? "建议 2～4 个字，比如“征召”“暂停”。留空就显示原来的按键。" : "Two to four characters fit best. Empty: the key itself.") + "</color>");
			GUI.SetNextControlName(FieldName);
			text = Widgets.TextField(new Rect(inRect.x, inRect.y + 98f, inRect.width, 36f), text);
			if (text.Length > MaxLength)
			{
				text = text.Substring(0, MaxLength);
			}
			if (!focused)
			{
				focused = true;
				GUI.FocusControl(FieldName);
			}
			float bw = 160f, y = inRect.yMax - 38f;
			if (Widgets.ButtonText(new Rect(inRect.x, y, bw, 36f), chinese ? "用功能名" : "Its function"))
			{
				text = key.Label.Length > MaxLength ? key.Label.Substring(0, MaxLength) : key.Label;
			}
			if (Widgets.ButtonText(new Rect(inRect.x + bw + 10f, y, bw, 36f), chinese ? "清除" : "Clear"))
			{
				text = "";
			}
			if (Widgets.ButtonText(new Rect(inRect.xMax - bw, y, bw, 36f), chinese ? "确定" : "OK"))
			{
				Save();
			}
		}

		public override void OnAcceptKeyPressed()
		{
			Save();
		}

		private void Save()
		{
			KeyWheel.SetLabel(key, text);
			Close();
		}
	}

	internal sealed class WheelKey
	{
		public readonly string Name;
		public readonly KeyBindingDef Def;
		public readonly int RawKey = -1, RawMods;
		public readonly string RawShown;

		public WheelKey(string name, KeyBindingDef def)
		{
			Name = name;
			Def = def;
		}

		private WheelKey(string name, int mods, KeyWheel.KeyboardKey key)
		{
			Name = name;
			RawKey = key.Code;
			RawMods = mods;
			RawShown = KeyWheel.ModifierText(mods) + key.Shown;
		}

		public bool IsRaw => RawKey >= 0;

		public bool IsSwitch => Def == null && !IsRaw;

		/// <summary>A keyboard key with these modifiers (GLFW's mods).</summary>
		public static WheelKey Raw(int mods, KeyWheel.KeyboardKey key)
		{
			return new WheelKey(KeyWheel.RawPrefix + KeyWheel.ModifierText(mods) + key.Name, mods, key);
		}

		/// <summary>The key of that name (a def's name, one of KeyWheel.Specials, "@K:..."); null when there is none (a mod's key, the mod gone).</summary>
		public static WheelKey Named(string name)
		{
			if (KeyWheel.Specials.Contains(name))
			{
				return new WheelKey(name, null);
			}
			if (name.Length == 5 && name.StartsWith("@Key", StringComparison.Ordinal) && char.IsDigit(name[4]))
			{
				// The number keys as the first build that had them stored them.
				return Raw(0, KeyWheel.Keyboard.First(k => k.Name == name.Substring(4)));
			}
			if (name.StartsWith(KeyWheel.RawPrefix, StringComparison.Ordinal))
			{
				string[] parts = name.Substring(KeyWheel.RawPrefix.Length).Split('+');
				int mods = 0;
				for (int i = 0; i < parts.Length - 1; i++)
				{
					LatchedKey modifier = LatchedKey.All.FirstOrDefault(m => string.Equals(m.Name, parts[i], StringComparison.OrdinalIgnoreCase));
					if (modifier == null)
					{
						return null;
					}
					mods |= modifier.Mod;
				}
				int index = Array.FindIndex(KeyWheel.Keyboard, k => string.Equals(k.Name, parts[parts.Length - 1], StringComparison.OrdinalIgnoreCase));
				return index >= 0 ? Raw(mods, KeyWheel.Keyboard[index]) : null;
			}
			KeyBindingDef def = DefDatabase<KeyBindingDef>.GetNamedSilentFail(name);
			return def != null ? new WheelKey(name, def) : null;
		}

		public string Label
		{
			get
			{
				if (Def != null)
				{
					return Def.LabelCap;
				}
				bool chinese = Texts.Chinese;
				if (IsRaw)
				{
					return (RawMods != 0 ? (chinese ? "组合键 " : "Combination ") : (chinese ? "键盘 " : "Key ")) + RawShown;
				}
				switch (Name)
				{
					case KeyWheel.ShiftName: return "Shift";
					case KeyWheel.CtrlName: return "Ctrl";
					case KeyWheel.AltName: return "Alt";
					case KeyWheel.ReportName: return chinese ? "记录问题" : "Record a problem";
					case KeyWheel.MouseLeftName: return chinese ? "鼠标左键" : "Left mouse button";
					case KeyWheel.MouseRightName: return chinese ? "鼠标右键" : "Right mouse button";
					default: return chinese ? "鼠标中键" : "Middle mouse button";
				}
			}
		}
	}
}
