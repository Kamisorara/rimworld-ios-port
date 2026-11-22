using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal.Numerics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// A finger that lands just beside a small control presses that control.
	///
	/// The game's interface is the PC's: a window's close cross is 18 units, most icon buttons 24, at this phone's
	/// scale 1.9 and 2.5 mm. Nothing is moved or resized here. While the interface is drawn, the rectangle of every
	/// control that can be pressed is noted in screen pixels (the engine's button, toggle, text field and slider, the
	/// game's draggable button behind checkboxes and text buttons, its sliders and ranges, the work boxes and the
	/// colonist bar, which test the mouse themselves) - a mod's window draws with the same calls, so its controls are
	/// noted like the game's. When a touch begins, Touch asks where the press should go:
	///   beside the controls, on nothing    to the nearest control whose missing size reaches the finger: a control
	///                                      smaller than tapWantMm counts as that large, by at most tapSlopMm a side;
	///   on a large control (a list row)    to a much smaller control lying on it (an icon in the row), if the finger
	///                                      is within tapNestedMm of it;
	///   on a control and near no smaller   where the finger is.
	/// A control under another window is never chosen, and a finger on one window never presses into another.
	/// mobile.txt: tapAssist=0 turns it off; tapWantMm, tapSlopMm, tapNestedMm (5.2, 1.8, 0.9); tapAssistLog=1 logs
	/// every adjusted press.
	/// </summary>
	internal static class TapAssist
	{
		private struct Target
		{
			public float x0, y0, x1, y1;
			public int window; // index into the pass's windows (drawn later = on top), -1: not in a window
			public byte kind;
		}

		private const byte Button = 0, Field = 1, Slider = 2, Other = 3;
		private static readonly string[] KindNames = { "button", "text field", "slider", "control" };

		// The pass being drawn, and the last one that was drawn to its end.
		private static Target[] drawing = new Target[1024], shown = new Target[1024];
		private static int drawingCount, shownCount, drawingFrame = -1;
		private static float4[] shownWindows = new float4[32];
		// Windows that only go along with the pointer (MouseDecorations): never the window a touch is in.
		private static bool[] shownFollowPointer = new bool[32];
		private static int shownWindowCount;

		// The lists (scroll views) of a pass that have more in them than they show.
		private struct ListView
		{
			public float x0, y0, x1, y1;       // the part of the screen it shows in, less its scrollbars
			public float originX, originY;     // where its own rectangle begins on the screen: what it is known by
			public int window;
			public bool vertical, horizontal;  // the directions it has something to scroll in
			public int end;                    // the controls noted before it ended (-1: it did not end in the pass); later ones are drawn over it
		}

		private static ListView[] drawingLists = new ListView[64], shownLists = new ListView[64];
		private static int drawingListCount, shownListCount;

		// The texts of a pass and where they are on the screen: only while the agent (Agent.cs) is on, for whoever drives
		// the game from the PC ("which button says Confirm").
		private struct TextMark
		{
			public float x0, y0, x1, y1;
			public int window;
			public string text;
		}

		private static TextMark[] drawingTexts = new TextMark[512], shownTexts = new TextMark[512];
		private static int drawingTextCount, shownTextCount;
		private const int MaxTexts = 6000;

		/// <summary>Set by the agent: the texts are noted too, and the controls even when tap assist is off.</summary>
		public static volatile bool RecordForAgent;

		/// <summary>The backend of the engine is as this class expects it: rectangles can be placed on the screen.</summary>
		public static bool Ready { get; private set; }

		private static bool on;
		private static bool log;
		private static float want, slop, nestedSlop; // pixels
		private static int adjusted, asked;

		private static AccessTools.FieldRef<object, float> M00, M11;
		private static AccessTools.FieldRef<object, bool> Rotated;
		private static AccessTools.FieldRef<object, float2> Translate, Offset;
		private static AccessTools.FieldRef<object, Stack<float4>> HitClip;
		private static AccessTools.FieldRef<object, List<float4>> WindowRects;
		private static AccessTools.FieldRef<object, int> WindowDepth;
		private static Type backendType;
		private static object lastCurrent, lastEngine;

		/// <summary>
		/// The engine's own interface backend, whose fields are read here. A mod may put its own in GuiBackend.Current, with
		/// the engine's inside (Readable Numbers formats every number it draws or measures): its fields are not the engine's,
		/// and reading them as such threw in every control's prefix, so that nothing of the interface was drawn (a tester,
		/// 2026-10-26: "这个mod加上之后会导致UI崩溃"). The engine's is looked for in such a backend's fields, a few levels
		/// deep; without one, null (nothing is noted).
		/// </summary>
		private static object EngineBackend()
		{
			object current = UnityStubs.Gui.GuiBackend.Current;
			if (current == null || backendType.IsInstanceOfType(current))
			{
				return current;
			}
			if (!ReferenceEquals(current, lastCurrent))
			{
				lastCurrent = current;
				lastEngine = Inside(current, 4);
				Console.WriteLine("[mobile] tap assist: the interface backend is " + current.GetType().FullName + (lastEngine != null ? ", the engine's inside it" : ", without the engine's inside it found: nothing is noted"));
			}
			return lastEngine;
		}

		private static object Inside(object wrapper, int depth)
		{
			for (Type type = wrapper.GetType(); type != null && type != typeof(object); type = type.BaseType)
			{
				foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					if (field.FieldType.IsValueType || field.FieldType == typeof(string))
					{
						continue;
					}
					object value = field.GetValue(wrapper);
					if (value == null || ReferenceEquals(value, wrapper))
					{
						continue;
					}
					if (backendType.IsInstanceOfType(value))
					{
						return value;
					}
					if (depth > 1 && value is UnityStubs.Gui.IGuiBackend && Inside(value, depth - 1) is object found)
					{
						return found;
					}
				}
			}
			return null;
		}

		public static void Start(Harmony harmony, float density)
		{
			try
			{
				Type backend = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImGuiGuiBackend");
				backendType = backend;
				M00 = AccessTools.FieldRefAccess<object, float>(AccessTools.Field(backend, "m00"));
				M11 = AccessTools.FieldRefAccess<object, float>(AccessTools.Field(backend, "m11"));
				Rotated = AccessTools.FieldRefAccess<object, bool>(AccessTools.Field(backend, "rotated"));
				Translate = AccessTools.FieldRefAccess<object, float2>(AccessTools.Field(backend, "translate"));
				Offset = AccessTools.FieldRefAccess<object, float2>(AccessTools.Field(backend, "offset"));
				HitClip = AccessTools.FieldRefAccess<object, Stack<float4>>(AccessTools.Field(backend, "hitClipStack"));
				WindowRects = AccessTools.FieldRefAccess<object, List<float4>>(AccessTools.Field(backend, "windowRectsAccum"));
				WindowDepth = AccessTools.FieldRefAccess<object, int>(AccessTools.Field(backend, "windowContentDepth"));
				// The windows have all been drawn when the engine is through with them: the pass is complete.
				harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.ProcessPendingWindows)), postfix: new HarmonyMethod(typeof(TapAssist), nameof(AfterWindows)));
				// Every text of the interface ends here (an interface method: never copied into its callers).
				harmony.Patch(AccessTools.Method(backend, "DrawText"), prefix: new HarmonyMethod(typeof(TapAssist), nameof(NoteText)));
				Ready = true;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] tap assist: the engine's interface backend is not as expected, left out: " + e.Message);
				return;
			}
			if (Settings.Get("tapAssist", 1) == 0)
			{
				return;
			}
			float perMm = density * 160f / 25.4f;
			want = Number("tapWantMm", 5.2f) * perMm;
			slop = Number("tapSlopMm", 1.8f) * perMm;
			nestedSlop = Number("tapNestedMm", 0.9f) * perMm;
			log = Settings.Get("tapAssistLog", 0) != 0;

			int kinds = 0;
			kinds += Patch(harmony, () => AccessTools.Method(typeof(GUI), "ButtonImpl"), nameof(NoteButton));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(GUI), "RepeatButtonImpl"), nameof(NoteButton));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(GUI), "TextFieldImpl"), nameof(NoteField));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(GUI), nameof(GUI.HorizontalSlider), new[] { typeof(Rect), typeof(float), typeof(float), typeof(float) }), nameof(NoteSlider));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(GUI), nameof(GUI.VerticalSlider), new[] { typeof(Rect), typeof(float), typeof(float), typeof(float) }), nameof(NoteSlider));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(Widgets), nameof(Widgets.ButtonInvisibleDraggable)), nameof(NoteDraggable));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(Widgets), nameof(Widgets.HorizontalSlider), new[] { typeof(Rect), typeof(float), typeof(float), typeof(float), typeof(bool), typeof(string), typeof(string), typeof(string), typeof(float) }), nameof(NoteGameSlider));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(Widgets), nameof(Widgets.FrequencyHorizontalSlider)), nameof(NoteRange));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(Widgets), nameof(Widgets.FloatRange)), nameof(NoteRange));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(Widgets), nameof(Widgets.IntRange)), nameof(NoteRange));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(Widgets), nameof(Widgets.QualityRange)), nameof(NoteRange));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(WidgetsWork), nameof(WidgetsWork.DrawWorkBoxFor)), nameof(NoteWorkBox));
			kinds += Patch(harmony, () => AccessTools.Method(typeof(ColonistBarColonistDrawer), nameof(ColonistBarColonistDrawer.DrawColonist)), nameof(NoteColonist));
			on = true;
			Console.WriteLine("[mobile] tap assist: " + kinds + " kinds of control noted; a control smaller than " + (want / perMm).ToString("F1") + " mm (" + want.ToString("F0") + " px) is pressed from up to " + (slop / perMm).ToString("F1") + " mm beside it, " + (nestedSlop / perMm).ToString("F1") + " mm where it lies on a larger one");
		}

		private static float Number(string key, float fallback)
		{
			string text = Settings.Get(key, null);
			return text != null && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value) && value >= 0f ? value : fallback;
		}

		private static int Patch(Harmony harmony, Func<MethodInfo> find, string prefix)
		{
			try
			{
				MethodInfo method = find();
				if (method == null)
				{
					Console.WriteLine("[mobile] tap assist: a control of the kind " + prefix + " was not found");
					return 0;
				}
				harmony.Patch(method, prefix: new HarmonyMethod(typeof(TapAssist), prefix));
				return 1;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] tap assist: " + prefix + " not installed: " + e.Message);
				return 0;
			}
		}

		// ---- while the interface is drawn ----

		public static void NoteButton(Rect position) => Note(position, Button);

		public static void NoteField(Rect rect) => Note(rect, Field);

		public static void NoteSlider(Rect position) => Note(position, Slider);

		public static void NoteDraggable(Rect butRect) => Note(butRect, Button);

		public static void NoteRange(Rect rect) => Note(rect, Slider);

		/// <summary>The game's slider moves its rectangle down before it tests the mouse (the label sits above the rail).</summary>
		public static void NoteGameSlider(Rect rect, bool middleAlignment, string label)
		{
			bool labelled = !string.IsNullOrEmpty(label);
			if (middleAlignment || labelled)
			{
				rect.y += Mathf.Round((rect.height - 10f) / 2f);
			}
			if (labelled)
			{
				rect.y += 5f;
			}
			Note(rect, Slider);
		}

		public static void NoteWorkBox(float x, float y) => Note(new Rect(x, y, 25f, 25f), Other);

		public static void NoteColonist(Rect rect) => Note(rect, Other);

		/// <summary>
		/// Where a rectangle of the interface being drawn is on the screen (pixels, y down), before any clipping. False
		/// outside the drawing pass (if only that is asked for), or where the interface is drawn turned.
		/// </summary>
		private static bool OnScreen(Rect rect, bool drawingOnly, out object backend, out float x0, out float y0, out float x1, out float y1)
		{
			x0 = y0 = x1 = y1 = 0f;
			backend = null;
			Event current = Event.current;
			if (!Ready || current == null || (drawingOnly && current.rawType != EventType.Repaint))
			{
				return false;
			}
			backend = EngineBackend();
			if (backend == null || Rotated(backend))
			{
				return false;
			}
			float m00 = M00(backend), m11 = M11(backend);
			float2 translate = Translate(backend), offset = Offset(backend);
			x0 = m00 * (offset.X + rect.x) + translate.X;
			x1 = m00 * (offset.X + rect.x + rect.width) + translate.X;
			y0 = m11 * (offset.Y + rect.y) + translate.Y;
			y1 = m11 * (offset.Y + rect.y + rect.height) + translate.Y;
			return true;
		}

		/// <summary>The first thing noted in a new frame begins a new pass: a pass that never reached its end (an exception in a window) leaves nothing behind.</summary>
		private static void NewPassIfNewFrame()
		{
			int frame = Time.frameCount;
			if (frame != drawingFrame)
			{
				drawingFrame = frame;
				drawingCount = 0;
				drawingListCount = 0;
				drawingTextCount = 0;
			}
		}

		/// <summary>The scale the interface is being drawn at just now (the scale of the game, times that of a window made smaller to fit).</summary>
		public static float CurrentScale()
		{
			object backend = Ready ? EngineBackend() : null;
			return backend != null ? M00(backend) : Prefs.UIScale;
		}

		/// <summary>Where a rectangle of the interface begins on the screen, in whatever pass this is.</summary>
		public static bool ScreenOrigin(Rect rect, out float x, out float y)
		{
			return OnScreen(rect, false, out _, out x, out y, out _, out _);
		}

		/// <summary>
		/// Told by Interface before every scroll view of the game: one that has more in it than it shows is a list a finger
		/// scrolls. Its number in the pass (for EndList), -1 when it was not noted.
		/// </summary>
		public static int NoteList(Rect outRect, bool vertical, bool horizontal)
		{
			if (!OnScreen(outRect, true, out object backend, out float x0, out float y0, out float x1, out float y1))
			{
				return -1;
			}
			NewPassIfNewFrame();
			float originX = x0, originY = y0;
			// The scrollbars (16 units) are dragged like any control.
			if (vertical) x1 -= 16f * M00(backend);
			if (horizontal) y1 -= 16f * M11(backend);
			Stack<float4> clips = HitClip(backend);
			if (clips.Count > 0)
			{
				float4 clip = clips.Peek();
				if (x0 < clip.X) x0 = clip.X;
				if (y0 < clip.Y) y0 = clip.Y;
				if (x1 > clip.Z) x1 = clip.Z;
				if (y1 > clip.W) y1 = clip.W;
			}
			if (x1 - x0 < 3f || y1 - y0 < 3f)
			{
				return -1;
			}
			if (drawingListCount == drawingLists.Length)
			{
				Array.Resize(ref drawingLists, drawingLists.Length * 2);
			}
			ref ListView list = ref drawingLists[drawingListCount++];
			list.x0 = x0;
			list.y0 = y0;
			list.x1 = x1;
			list.y1 = y1;
			list.originX = originX;
			list.originY = originY;
			list.window = WindowDepth(backend) > 0 ? WindowRects(backend).Count - 1 : -1;
			list.vertical = vertical;
			list.horizontal = horizontal;
			list.end = -1;
			return drawingListCount - 1;
		}

		/// <summary>Told by Interface after the scroll view NoteList noted (its number): the controls noted from here on are drawn over it.</summary>
		public static void EndList(int index)
		{
			if (index >= 0 && index < drawingListCount && drawingFrame == Time.frameCount)
			{
				drawingLists[index].end = drawingCount;
			}
		}

		/// <summary>
		/// The list a finger at this point of the screen would scroll: the innermost one there that has something to
		/// scroll, in the window that gets the touch. With the directions it scrolls in and where it begins on the screen.
		/// </summary>
		public static bool ListAt(Vector2 finger, out bool vertical, out bool horizontal, out float originX, out float originY)
		{
			vertical = horizontal = false;
			originX = originY = 0f;
			if (!Ready)
			{
				return false;
			}
			int under = WindowAt(finger.x, finger.y);
			// A list inside another is drawn after it.
			for (int i = shownListCount - 1; i >= 0; i--)
			{
				ref ListView list = ref shownLists[i];
				if (list.window == under && finger.x >= list.x0 && finger.x <= list.x1 && finger.y >= list.y0 && finger.y <= list.y1)
				{
					if (CoveredAfter(ref list, under, finger))
					{
						// Something drawn after the list lies over it here (the inspect pane's tabs over the resource
						// readout, which reaches down to 200 units above the bottom): the finger is on that, not the list.
						return false;
					}
					vertical = list.vertical;
					horizontal = list.horizontal;
					originX = list.originX;
					originY = list.originY;
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Whether a control noted after the list ended, in the same window (or outside windows), is under the finger. One
		/// that takes in the whole list is a background, not something over it: the mod page's invisible button over the
		/// whole page that clears the selection, drawn after its two lists (the user, 2026-10-24: a swipe there moved mods
		/// instead of scrolling).
		/// </summary>
		private static bool CoveredAfter(ref ListView list, int window, Vector2 finger)
		{
			if (list.end < 0)
			{
				return false;
			}
			for (int t = list.end; t < shownCount; t++)
			{
				ref Target target = ref shown[t];
				if (target.window == window && finger.x >= target.x0 && finger.x <= target.x1 && finger.y >= target.y0 && finger.y <= target.y1
					&& !(target.x0 <= list.x0 + 1f && target.y0 <= list.y0 + 1f && target.x1 >= list.x1 - 1f && target.y1 >= list.y1 - 1f))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>Prefix of the interface backend's DrawText: while the agent is on, the text and where it is on the screen.</summary>
		public static void NoteText(Rect rect, string text)
		{
			if (!RecordForAgent || string.IsNullOrEmpty(text) || !OnScreen(rect, true, out object backend, out float x0, out float y0, out float x1, out float y1))
			{
				return;
			}
			NewPassIfNewFrame();
			Stack<float4> clips = HitClip(backend);
			if (clips.Count > 0)
			{
				float4 clip = clips.Peek();
				if (x0 < clip.X) x0 = clip.X;
				if (y0 < clip.Y) y0 = clip.Y;
				if (x1 > clip.Z) x1 = clip.Z;
				if (y1 > clip.W) y1 = clip.W;
			}
			if (x0 < 0f) x0 = 0f;
			if (y0 < 0f) y0 = 0f;
			if (x1 > Screen.width) x1 = Screen.width;
			if (y1 > Screen.height) y1 = Screen.height;
			if (x1 - x0 < 1f || y1 - y0 < 1f || drawingTextCount >= MaxTexts)
			{
				return;
			}
			if (drawingTextCount == drawingTexts.Length)
			{
				Array.Resize(ref drawingTexts, drawingTexts.Length * 2);
			}
			ref TextMark mark = ref drawingTexts[drawingTextCount++];
			mark.x0 = x0;
			mark.y0 = y0;
			mark.x1 = x1;
			mark.y1 = y1;
			mark.window = WindowDepth(backend) > 0 ? WindowRects(backend).Count - 1 : -1;
			mark.text = text;
		}

		private static void Note(Rect rect, byte kind)
		{
			if ((!on && !RecordForAgent && kind != Field) || !OnScreen(rect, true, out object backend, out float x0, out float y0, out float x1, out float y1))
			{
				return;
			}
			NewPassIfNewFrame();
			Stack<float4> clips = HitClip(backend);
			if (clips.Count > 0)
			{
				// A list shows only part of what is in it.
				float4 clip = clips.Peek();
				if (x0 < clip.X) x0 = clip.X;
				if (y0 < clip.Y) y0 = clip.Y;
				if (x1 > clip.Z) x1 = clip.Z;
				if (y1 > clip.W) y1 = clip.W;
			}
			if (x0 < 0f) x0 = 0f;
			if (y0 < 0f) y0 = 0f;
			if (x1 > Screen.width) x1 = Screen.width;
			if (y1 > Screen.height) y1 = Screen.height;
			if (x1 - x0 < 3f || y1 - y0 < 3f)
			{
				return;
			}
			if (drawingCount == drawing.Length)
			{
				Array.Resize(ref drawing, drawing.Length * 2);
			}
			ref Target target = ref drawing[drawingCount++];
			target.x0 = x0;
			target.y0 = y0;
			target.x1 = x1;
			target.y1 = y1;
			target.window = WindowDepth(backend) > 0 ? WindowRects(backend).Count - 1 : -1;
			target.kind = kind;
		}

		/// <summary>Postfix of GUI.ProcessPendingWindows: at the end of the drawing pass, what was noted becomes what a touch is compared with.</summary>
		public static void AfterWindows()
		{
			Event current = Event.current;
			if (current == null || current.rawType != EventType.Repaint)
			{
				return;
			}
			object backend = EngineBackend();
			if (backend == null)
			{
				return;
			}
			if (drawingFrame != Time.frameCount)
			{
				drawingCount = 0;
				drawingListCount = 0;
				drawingTextCount = 0;
			}
			TextMark[] swapTexts = shownTexts;
			shownTexts = drawingTexts;
			shownTextCount = drawingTextCount;
			drawingTexts = swapTexts;
			drawingTextCount = 0;
			ListView[] swapLists = shownLists;
			shownLists = drawingLists;
			shownListCount = drawingListCount;
			drawingLists = swapLists;
			drawingListCount = 0;
			Target[] swap = shown;
			shown = drawing;
			shownCount = drawingCount;
			drawing = swap;
			drawingCount = 0;
			drawingFrame = -1;
			List<float4> windows = WindowRects(backend);
			if (windows.Count > shownWindows.Length)
			{
				shownWindows = new float4[windows.Count * 2];
			}
			windows.CopyTo(shownWindows);
			shownWindowCount = windows.Count;
			if (shownFollowPointer.Length < shownWindows.Length)
			{
				shownFollowPointer = new bool[shownWindows.Length];
			}
			MouseDecorations.Mark(shownFollowPointer, shownWindowCount);
		}

		// ---- when a touch begins ----

		/// <summary>The topmost window at a point of the screen, -1 for none.</summary>
		private static int WindowAt(float x, float y)
		{
			for (int i = shownWindowCount - 1; i >= 0; i--)
			{
				float4 rect = shownWindows[i];
				if (!shownFollowPointer[i] && x >= rect.X && y >= rect.Y && x <= rect.Z && y <= rect.W)
				{
					return i;
				}
			}
			return -1;
		}

		/// <summary>Where a rectangle of the interface being drawn now lies on the screen (window pixels, y down).</summary>
		public static bool ScreenRect(Rect rect, out Rect screen)
		{
			screen = default(Rect);
			if (!OnScreen(rect, true, out _, out float x0, out float y0, out float x1, out float y1))
			{
				return false;
			}
			screen = Rect.MinMaxRect(x0, y0, x1, y1);
			return true;
		}

		/// <summary>Whether a text field was drawn at this point of the screen in the last pass.</summary>
		public static bool FieldAt(Vector2 point)
		{
			for (int i = 0; i < shownCount; i++)
			{
				ref Target target = ref shown[i];
				if (target.kind == Field && point.x >= target.x0 && point.x <= target.x1 && point.y >= target.y0 && point.y <= target.y1)
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>Where the press of a finger at this point (window pixels, y down) goes.</summary>
		public static Vector2 Adjust(Vector2 finger)
		{
			if (!on || shownCount == 0)
			{
				return finger;
			}
			asked++;
			float x = finger.x, y = finger.y;
			int under = WindowAt(x, y);
			// The smallest control the finger is on, in the layer that gets the press.
			int inside = -1;
			float insideArea = float.MaxValue;
			for (int i = 0; i < shownCount; i++)
			{
				ref Target target = ref shown[i];
				if (target.window == under && x >= target.x0 && x <= target.x1 && y >= target.y0 && y <= target.y1)
				{
					float area = (target.x1 - target.x0) * (target.y1 - target.y0);
					if (area < insideArea)
					{
						insideArea = area;
						inside = i;
					}
				}
			}
			int best = -1;
			float bestDistance = float.MaxValue, bestArea = float.MaxValue, bestX = x, bestY = y;
			for (int i = 0; i < shownCount; i++)
			{
				if (i == inside)
				{
					continue;
				}
				ref Target target = ref shown[i];
				// A finger on a window presses nothing outside it; a finger beside every window may have meant a
				// control at the edge of one.
				if (target.window != under && under != -1)
				{
					continue;
				}
				float width = target.x1 - target.x0, height = target.y1 - target.y0, area = width * height;
				// A text field takes the focus and brings the keyboard up: it is reached only from close by.
				float most = target.kind == Field ? nestedSlop : slop;
				if (inside >= 0)
				{
					// On a control already: only a much smaller one that lies on it, or against it, takes the press away.
					ref Target outer = ref shown[inside];
					if (area > insideArea * 0.4f || target.x1 < outer.x0 || target.x0 > outer.x1 || target.y1 < outer.y0 || target.y0 > outer.y1)
					{
						continue;
					}
					most = nestedSlop;
				}
				float reachX = Mathf.Min(most, Mathf.Max(0f, (want - width) / 2f)), reachY = Mathf.Min(most, Mathf.Max(0f, (want - height) / 2f));
				float dx = x < target.x0 ? target.x0 - x : x > target.x1 ? x - target.x1 : 0f;
				float dy = y < target.y0 ? target.y0 - y : y > target.y1 ? y - target.y1 : 0f;
				if (dx > reachX || dy > reachY || (dx == 0f && dy == 0f))
				{
					continue;
				}
				float distance = dx * dx + dy * dy;
				if (distance > bestDistance || (distance == bestDistance && area >= bestArea))
				{
					continue;
				}
				// The press goes a little inside the control, and only where the control is the topmost thing.
				float insetX = Mathf.Min(4f, width * 0.25f), insetY = Mathf.Min(4f, height * 0.25f);
				float toX = Mathf.Clamp(x, target.x0 + insetX, target.x1 - insetX), toY = Mathf.Clamp(y, target.y0 + insetY, target.y1 - insetY);
				if (WindowAt(toX, toY) != target.window)
				{
					continue;
				}
				best = i;
				bestDistance = distance;
				bestArea = area;
				bestX = toX;
				bestY = toY;
			}
			if (best < 0)
			{
				return finger;
			}
			adjusted++;
			if (log || Touch.Verbose)
			{
				ref Target target = ref shown[best];
				Console.WriteLine("[mobile] tap assist: " + x.ToString("F0") + "," + y.ToString("F0") + " -> " + bestX.ToString("F0") + "," + bestY.ToString("F0") + ", a " + KindNames[target.kind] + " of " + (target.x1 - target.x0).ToString("F0") + " x " + (target.y1 - target.y0).ToString("F0") + " px at " + target.x0.ToString("F0") + "," + target.y0.ToString("F0") + (target.window >= 0 ? " in window " + target.window : "") + (inside >= 0 ? ", lying on a larger control" : "") + " (" + adjusted + " of " + asked + " presses adjusted, " + shownCount + " controls, " + shownWindowCount + " windows)");
			}
			return new Vector2(bestX, bestY);
		}

		/// <summary>What the last complete pass showed, for the agent: (kind, rectangle in pixels, window), texts likewise, and the windows in drawing order.</summary>
		public static void Snapshot(List<(string kind, float x0, float y0, float x1, float y1, int window)> controls, List<(string text, float x0, float y0, float x1, float y1, int window)> texts, List<(float x0, float y0, float x1, float y1)> windows)
		{
			for (int i = 0; i < shownCount; i++)
			{
				ref Target t = ref shown[i];
				controls.Add((KindNames[t.kind], t.x0, t.y0, t.x1, t.y1, t.window));
			}
			for (int i = 0; i < shownTextCount; i++)
			{
				ref TextMark t = ref shownTexts[i];
				texts.Add((t.text, t.x0, t.y0, t.x1, t.y1, t.window));
			}
			for (int i = 0; i < shownWindowCount; i++)
			{
				float4 w = shownWindows[i];
				windows.Add((w.X, w.Y, w.Z, w.W));
			}
		}

		/// <summary>For the diagnostics: what was noted in the last pass, one control per line.</summary>
		public static string Describe()
		{
			var text = new System.Text.StringBuilder();
			for (int i = 0; i < shownListCount; i++)
			{
				ref ListView list = ref shownLists[i];
				text.Append("# list in window ").Append(list.window).Append(": ").Append(list.x0.ToString("F0")).Append(',').Append(list.y0.ToString("F0")).Append(" - ").Append(list.x1.ToString("F0")).Append(',').Append(list.y1.ToString("F0")).Append(list.vertical ? " scrolls down" : "").Append(list.horizontal ? " scrolls across" : "").Append('\n');
			}
			text.Append(shownCount).Append(" controls, ").Append(shownWindowCount).Append(" windows, ").Append(adjusted).Append(" of ").Append(asked).Append(" presses adjusted\n");
			for (int i = 0; i < shownWindowCount; i++)
			{
				float4 rect = shownWindows[i];
				text.Append("window ").Append(i).Append(": ").Append(rect.X.ToString("F0")).Append(',').Append(rect.Y.ToString("F0")).Append(" - ").Append(rect.Z.ToString("F0")).Append(',').Append(rect.W.ToString("F0")).Append('\n');
			}
			for (int i = 0; i < shownCount; i++)
			{
				ref Target target = ref shown[i];
				text.Append(KindNames[target.kind]).Append(' ').Append(target.window).Append(' ').Append(target.x0.ToString("F0")).Append(' ').Append(target.y0.ToString("F0")).Append(' ').Append((target.x1 - target.x0).ToString("F0")).Append(' ').Append((target.y1 - target.y0).ToString("F0")).Append('\n');
			}
			return text.ToString();
		}
	}
}
