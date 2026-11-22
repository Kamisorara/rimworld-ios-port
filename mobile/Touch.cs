using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Touch gestures. The Android GLFW (host/glfw) hands every finger event to this class and injects into the
	/// engine's input only what this class asks for, so the game sees a mouse that does what a finger means:
	///
	/// on the map or the world (a touch the interface did not take)
	///   tap            a left click
	///   drag           moves the camera (the ground follows the finger)
	///   hold           with something selected: a right click (orders); otherwise the selection box starts and
	///                  moving the finger then selects an area
	///   (with mobile.txt squadTaps=1 and a drafted squad selected, Squad: a tap is the order, a right click, or, on one
	///                  of the colony's own, adds it to the selection or takes it out; a hold is the left button)
	///   two fingers    pinch to zoom around the fingers, drag to move
	/// anywhere but on the key wheel and the mouse buttons, while the right mouse button is on (MouseButtons: for the next
	/// touch, or locked) or the key wheel's middle one is (KeyWheel)
	///   one finger     that button, pressed from landing to lifting (a tap is a right or a middle click)
	/// on the interface
	///   tap, drag      the left button, as a mouse
	///   in a list      (a scroll view with more in it than it shows) a tap clicks when the finger lifts; a drag
	///                  along the list scrolls it and presses nothing; a drag across it, or a drag after resting
	///                  the finger, is the left button again (a slider, painting a row of cells or a column of
	///                  checkboxes, moving a row)
	///   two fingers    over a window: the wheel (pinch) and a middle-button drag (moving both fingers), which is
	///                  what moves and zooms a mod's own map or graph; the game's map is left alone meanwhile
	/// </summary>
	internal static unsafe class Touch
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct TouchEvent
		{
			public int type, id;
			public float x, y;
			public double time;
		}

		private const int Down = 1, Move = 2, Up = 3, Cancel = 4, EditorAction = 5;
		private const int Press = 1, Release = 0;
		private const int Left = 0, Right = 1, Middle = 2;

		[DllImport("glfw")] private static extern void glfwAndroidSetTouchMode(int managed);
		[DllImport("glfw")] private static extern int glfwAndroidPollTouches(TouchEvent* events, int max);
		[DllImport("glfw")] private static extern void glfwAndroidInjectCursor(double x, double y);
		[DllImport("glfw")] private static extern void glfwAndroidInjectButton(int button, int action, int mods);
		[DllImport("glfw")] private static extern void glfwAndroidInjectScroll(double dx, double dy);
		[DllImport("glfw")] private static extern float glfwAndroidGetDensity();

		private sealed class Finger
		{
			public int id;
			public Vector2 pos, start;
			public double downTime;
			public bool seen; // Step has looked at it (a finger can land and lift between two frames)
		}

		private enum Mode
		{
			None,
			Pending,   // first finger down, the press is on its way to the game; not yet known what the touch will be
			Interface, // the interface took the press: a mouse
			Map,       // the map (or the world) took the press: a selection box is active
			Pan,
			Box,       // a hold on the map with nothing to order: the selection box stays and the finger drags it
			Tool,      // a designator or targeter took the press (as a mouse for now)
			Held,      // a hold on the interface: tooltips, no click
			ListPending, // a finger in a list with something to scroll: nothing is pressed until it is known what the touch is
			ListHeld,  // it rested there: tooltips; a drag from here drags what the finger is on
			ListScroll, // it dragged along the list: the list follows, nothing is pressed at all
			Two,       // two fingers: zoom and pan (for the map) or wheel and middle drag (for a window)
			Mouse,     // the right mouse button or the key wheel's middle one is on: the finger is that button until it lifts, nothing else
			Finished   // nothing more to recognise in this touch; wait for all fingers to lift
		}

		internal const double HoldSeconds = 0.45;
		// How far the wheel turns (GLFW units; a mouse notch is 1) when the fingers of a pinch double their distance.
		private const double WheelPerDoubling = 5.0;
		private static readonly AccessTools.FieldRef<CameraDriver, Vector2> MapDolly = AccessTools.FieldRefAccess<CameraDriver, Vector2>("desiredDollyRaw");
		private static readonly AccessTools.FieldRef<CameraDriver, float> MapSize = AccessTools.FieldRefAccess<CameraDriver, float>("desiredSize");
		private static readonly AccessTools.FieldRef<WorldCameraDriver, Vector2> WorldRotation = AccessTools.FieldRefAccess<WorldCameraDriver, Vector2>("desiredRotationRaw");
		private static readonly AccessTools.FieldRef<WorldCameraDriver, float> WorldAltitude = AccessTools.FieldRefAccess<WorldCameraDriver, float>("desiredAltitude");

		private static readonly List<Finger> fingers = new List<Finger>();
		private static readonly TouchEvent[] buffer = new TouchEvent[512];
		private static Mode mode;
		private static int framesDown;
		private static Vector2 last;
		private static float density = 1f;
		// What the engine believes about the mouse buttons (injected), and what still has to be injected next frame.
		private static bool leftDown, rightDown, middleDown;
		private static int releaseRightNextFrame;
		// The key wheel's middle button waits a frame after the mouse went to the finger: pressed in the frame the mouse
		// jumped there, the game drags the camera by the jump (a middle drag moves the map).
		private static int pendingPress = -1;
		private static bool pressedThisFrame;
		private static float pinchDistance;
		private static Vector2 pinchCentre;
		private static Window gestureWindow;
		private static double pendingWheel;
		// A finger that landed and lifted between two frames: still a tap.
		private static Finger quickTap;
		// Where the press of the current touch went (TapAssist may have moved it onto a control beside the finger), whether
		// the finger has travelled the drag threshold since, and how far the engine's mouse still is from the finger.
		private static Vector2 pressAt, cursorOffset;
		private static bool dragged;
		private static bool steady = true;
		// A finger that rested has begun to drag: the engine's mouse sets off from the press by a few pixels first (MoveCursor).
		private static bool setOff;
		// The list the current touch began in: the directions it scrolls in, and where it begins on the screen.
		private static bool listDown, listAcross;
		private static float listX, listY;
		private static bool touchInList;
		// The touch caught a list that was still moving: lifting the finger then clicks nothing.
		private static bool caughtList;
		// The touch began with a drafted squad selected (Squad): a tap on the map orders, a hold selects.
		private static bool squadTouch;
		// Where the last finger lifted (its last movement often comes in the same batch as the lifting).
		private static Vector2 liftedAt;
		// The frame the current touch began in, and the last frame the map's (or the world's) selector took a press of the
		// left button: a press there belongs to the map even when it was a double click, which starts no selection box.
		private static int touchFrame, mapPressFrame = -1;
		// Where this class last put the engine's mouse, and where it had put it by the end of an earlier frame (window
		// pixels, y down): the engine takes an injected position in its next frame.
		private static Vector2 cursorAt, cursorBefore;
		private static int cursorFrame = -1;
		private static bool cursorPlaced;

		public static bool Verbose;

		public static int FingerCount => fingers.Count;

		/// <summary>A touch is being delivered to the game: a finger is down, or the press of a finger that already lifted (a quick tap) is still on its way.</summary>
		public static bool TouchInProgress => fingers.Count > 0 || leftDown;

		/// <summary>Nothing of a touch is on its way to the game: no finger is down, and every mouse button a finger pressed (the key wheel's right and middle ones too) is up again.</summary>
		public static bool AllReleased => fingers.Count == 0 && !leftDown && !rightDown && !middleDown && pendingPress < 0;

		/// <summary>Whether the touch going on began in this list (a scroll view, by where it begins on the screen).</summary>
		public static bool BeganInList(Rect outRect)
		{
			return touchInList && TouchInProgress && TapAssist.ScreenOrigin(outRect, out float x, out float y) && Mathf.Abs(x - listX) <= 1.5f && Mathf.Abs(y - listY) <= 1.5f;
		}

		/// <summary>The engine's mouse is where a finger put it: a real mouse (which moves it without this class) has not moved it since.</summary>
		public static bool PointerFromFinger
		{
			get
			{
				if (!cursorPlaced)
				{
					return false;
				}
				Vector3 mouse = Input.mousePosition;
				Vector2 now = new Vector2(mouse.x, Screen.height - mouse.y);
				return (now - cursorAt).sqrMagnitude < 4f || (now - cursorBefore).sqrMagnitude < 4f;
			}
		}

		public static void Start()
		{
			density = glfwAndroidGetDensity();
			Verbose = File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "touch-verbose.txt"));
			var harmony = new Harmony("port17.mobile.touch");
			harmony.Patch(AccessTools.Method(typeof(UIRoot), nameof(UIRoot.UIRootUpdate)), prefix: new HarmonyMethod(typeof(Touch), nameof(Update)));
			harmony.Patch(AccessTools.Method(typeof(Selector), "HandleMapClicks"), prefix: new HarmonyMethod(typeof(Touch), nameof(SelectorTakesClicks)));
			harmony.Patch(AccessTools.Method(typeof(WorldSelector), "HandleWorldClicks"), prefix: new HarmonyMethod(typeof(Touch), nameof(SelectorTakesClicks)));
			glfwAndroidSetTouchMode(1);
			Diag.Start(harmony);
			Interface.Start(harmony);
			MapTools.Start(harmony);
			UiScale.Start(harmony);
			steady = Settings.Get("tapSteady", 1) != 0;
			TapAssist.Start(harmony, density);
			if (!Compat.On)
			{
				TouchSizes.Start(harmony);
			}
			Letters.Start(harmony);
			TypingBar.Start(harmony);
			EdgeScroll.Start(harmony);
			KeyWheel.Start();
			MouseButtons.Start();
			if (!Compat.On)
			{
				BarInset.Start(harmony);
			}
			MouseDecorations.Start(harmony);
			DiagFloatMenu.Start(harmony);
			Log("touch layer on (density " + density.ToString("F2") + ", drag threshold " + DragThreshold.ToString("F0") + " px)");
		}

		private static void Log(string text)
		{
			Console.WriteLine("[mobile] " + text);
		}

		private static void Trace(string text)
		{
			if (Verbose)
			{
				Log(text);
			}
		}

		// A movement of about 2 mm separates a drag from an unsteady tap.
		private static float DragThreshold => Mathf.Max(12f, density * 160f * 0.08f);
		// How far the mouse sets off in the frame a hold turns into a drag (pixels): past the 4.5 (a square of 20) within
		// which the game's draggable button and DragSelect's see no drag yet, and well inside the smallest box a finger
		// rests on (a work box is 25 units).
		private const float HeldDragStep = 6f;

		private static bool Playing => Current.ProgramState == ProgramState.Playing;

		private static bool OnWorld
		{
			get
			{
				World world = Current.Game != null ? Find.World : null;
				return world != null && world.renderer != null && WorldRendererUtility.WorldSelected;
			}
		}

		private static bool InGame => Playing || (Current.ProgramState == ProgramState.Entry && Current.Game != null && OnWorld);

		private static bool BoxActive
		{
			get
			{
				if (OnWorld)
				{
					return Find.WorldSelector != null && Find.WorldSelector.dragBox.active;
				}
				return Playing && Find.Selector != null && Find.Selector.dragBox.active;
			}
		}

		private static bool ToolActive
		{
			get
			{
				if (OnWorld)
				{
					return Find.WorldTargeter != null && Find.WorldTargeter.IsTargeting;
				}
				return Playing && (Find.DesignatorManager.SelectedDesignator != null || Find.Targeter.IsTargeting || DebugConfirm.Tool() != null);
			}
		}

		private static bool SomethingToOrder
		{
			get
			{
				if (OnWorld)
				{
					// The route planner (planning a caravan's way) adds a waypoint on a right click, with nothing selected.
					return (Find.WorldSelector != null && Find.WorldSelector.NumSelectedObjects > 0) || (Find.World != null && Find.WorldRoutePlanner.Active);
				}
				return Playing && Find.Selector.SelectedPawns.Count > 0;
			}
		}

		/// <summary>The window at a point of the screen (window pixels, y down), as the game's window stack sees it.</summary>
		private static Window WindowAt(Vector2 pixels)
		{
			if (Current.Root == null || Find.UIRoot == null || Find.WindowStack == null)
			{
				return null;
			}
			return Find.WindowStack.GetWindowAt(pixels / Prefs.UIScale);
		}

		// ---- what the engine is told ----

		private static void Cursor(Vector2 pixels)
		{
			glfwAndroidInjectCursor(pixels.x, pixels.y);
			if (cursorFrame != Time.frameCount)
			{
				cursorBefore = cursorPlaced ? cursorAt : pixels;
				cursorFrame = Time.frameCount;
			}
			cursorAt = pixels;
			cursorPlaced = true;
		}

		/// <summary>
		/// The engine says it is Linux, and on Linux the game moves the map by the middle button whenever it is held, by
		/// how far the mouse went since the game last measured it, which can be from before the mouse jumped to the finger.
		/// Before the key wheel's middle button goes down the game's measure starts from where the mouse is now.
		/// </summary>
		private static void ForgetMouseTravel()
		{
			try
			{
				AccessTools.Field(typeof(UnityGUIBugsFixer), "lastFrameMousePosition").SetValue(null, UI.MousePositionOnUIInverted);
				AccessTools.Field(typeof(UnityGUIBugsFixer), "frameDeltaComputedFrame").SetValue(null, Time.frameCount);
				AccessTools.Field(typeof(UnityGUIBugsFixer), "currentEventDelta").SetValue(null, Vector2.zero);
			}
			catch (Exception e)
			{
				Trace("mouse travel not reset: " + e.Message);
			}
		}

		private static void Button(int button, bool down)
		{
			glfwAndroidInjectButton(button, down ? Press : Release, 0);
			if (button == Left) leftDown = down;
			else if (button == Right) rightDown = down;
			else middleDown = down;
		}

		/// <summary>Prefix of Selector.HandleMapClicks and WorldSelector.HandleWorldClicks: the press reached the map (or the world), whatever its click count.</summary>
		public static void SelectorTakesClicks()
		{
			Event e = Event.current;
			if (e != null && e.type == EventType.MouseDown && e.button == 0 && TouchInProgress)
			{
				mapPressFrame = Time.frameCount;
			}
		}

		// ---- per frame, before the engine dispatches this frame's input (prefix of UIRoot.UIRootUpdate) ----

		public static void Update()
		{
			try
			{
				Step();
			}
			catch (Exception e)
			{
				Log("touch update failed: " + e);
				mode = Mode.None;
			}
		}

		private static void Step()
		{
			if (releaseRightNextFrame > 0 && --releaseRightNextFrame == 0 && rightDown)
			{
				Button(Right, false);
			}
			InputMode.Update();
			pressedThisFrame = pendingPress >= 0;
			if (pressedThisFrame)
			{
				ForgetMouseTravel();
				Button(pendingPress, true);
				pendingPress = -1;
			}
			Agent.Update();
			bool replayed = Replay.Feed();
			TouchSizes.BeforeInterface();
			LatchedKey.UpdateAll();
			KeyWheel.Update();
			MouseButtons.Update();
			UiScale.Update();
			Interface.Update();
			MapTools.Update();
			bool changed = Drain() | replayed;
			int count = fingers.Count;
			if (count == 0)
			{
				if (mode != Mode.None)
				{
					Trace("touch ends (" + mode + ")");
					Recorder.TouchEnded(last);
				}
				if (mode == Mode.Two && gestureWindow != null)
				{
					EndWindowGesture();
				}
				// Whatever the touch became: a hold that a second finger joined is still holding back the left button
				// (testers, 2026-10-22: after that no press reached the map, and no drag on it moved it).
				Interface.HoldEnded();
				if (mode == Mode.ListScroll)
				{
					Interface.EndListScroll(true);
				}
				bool tapInList = mode == Mode.ListPending && !caughtList;
				bool squadTap = mode == Mode.Map && squadTouch;
				if (squadTap)
				{
					// The release that follows selects nothing: the tap is an order, or adds to the squad (below).
					KeepBoxOff();
				}
				if (mode == Mode.Box && dragged)
				{
					// The box ends where the finger lifted, not where it was a frame before.
					Cursor(liftedAt);
				}
				// The last finger has lifted: the release reaches the game where the finger was.
				if (leftDown) Button(Left, false);
				if (pendingPress >= 0)
				{
					// Lifted before the middle button went down: it goes down now, and up next frame.
					ForgetMouseTravel();
					Button(pendingPress, true);
					pendingPress = -1;
					pressedThisFrame = true;
				}
				if (middleDown && !pressedThisFrame) Button(Middle, false);
				if (rightDown && mode == Mode.Mouse) Button(Right, false);
				if (squadTap && !Squad.TapJoins())
				{
					// A right click where the finger was: the squad's order.
					Trace("squad order at " + last);
					Button(Right, true);
					releaseRightNextFrame = 2;
				}
				mode = Mode.None;
				gestureWindow = null;
				if (tapInList)
				{
					// A tap in a list: the press now, the release next frame (above), a click for the game.
					Trace("tap in a list at " + pressAt);
					Cursor(pressAt);
					Button(Left, true);
				}
				if (quickTap != null)
				{
					// The press now, the release next frame (above), a click for the game.
					Trace("quick tap at " + quickTap.pos);
					Recorder.TouchBegan(quickTap.pos, WindowAt(quickTap.pos)?.GetType().Name);
					Recorder.TouchMode("quick tap");
					Recorder.TouchEnded(quickTap.pos);
					Vector2 at = TapAssist.Adjust(quickTap.pos);
					// Its press is in a list as a slower tap's would be (the list keeps a press nothing in it used).
					touchInList = Interface.ListTouch && TapAssist.ListAt(at, out listDown, out listAcross, out listX, out listY);
					Interface.FingerLanded(at);
					Cursor(at);
					int button = MouseButtons.TapButton(at);
					if (button == Middle)
					{
						pendingPress = Middle;
					}
					else
					{
						Button(button, true);
					}
					if (button == Right)
					{
						releaseRightNextFrame = 2;
					}
					quickTap = null;
				}
				return;
			}
			Finger first = fingers[0];
			if (mode == Mode.Finished)
			{
				KeepBoxOff();
				return;
			}
			if (count >= 2)
			{
				TwoFingers(first, fingers[1]);
				return;
			}
			if (mode == Mode.Two)
			{
				// One finger left of a two-finger gesture: not a click, not a pan jump.
				if (gestureWindow != null) EndWindowGesture();
				mode = Mode.Finished;
				KeepBoxOff();
				return;
			}
			if (mode == Mode.None)
			{
				// A new touch: the press goes to the game at once, so that the interface responds like to a mouse.
				mode = Mode.Pending;
				touchInList = false;
				squadTouch = Squad.Active;
				Interface.TouchBegan();
				first.seen = true;
				framesDown = 0;
				touchFrame = Time.frameCount;
				last = first.pos;
				pressAt = TapAssist.Adjust(first.pos);
				Interface.FingerLanded(pressAt);
				Recorder.TouchBegan(first.pos, WindowAt(first.start)?.GetType().Name);
				cursorOffset = Vector2.zero;
				dragged = setOff = false;
				Cursor(pressAt);
				int button = MouseButtons.TapButton(pressAt);
				if (button != Left)
				{
					// The right mouse button (MouseButtons) or the key wheel's middle one: pressed now, let go when the finger lifts.
					mode = Mode.Mouse;
					if (button == Middle)
					{
						pendingPress = Middle;
					}
					else
					{
						Button(button, true);
					}
					Trace("touch begins at " + first.pos + " as the " + (button == Right ? "right" : "middle") + " button");
					Recorder.TouchMode(button == Right ? "right button" : "middle button");
					return;
				}
				bool inList = Interface.ListTouch && TapAssist.ListAt(pressAt, out listDown, out listAcross, out listX, out listY);
				touchInList = inList;
				caughtList = Interface.StopCoasting(inList ? listX : float.NaN, inList ? listY : float.NaN);
				if (inList)
				{
					// The finger may be about to scroll the list: the mouse is there, but nothing is pressed yet.
					mode = Mode.ListPending;
					Trace("touch begins in a list at " + first.pos + (listDown ? ", scrolls down" : "") + (listAcross ? ", scrolls across" : ""));
					Recorder.TouchMode("list");
					return;
				}
				Button(Left, true);
				Trace("touch begins at " + first.pos);
				return;
			}
			Vector2 delta = first.pos - last;
			last = first.pos;
			framesDown++;
			if (!dragged && (first.pos - first.start).magnitude > DragThreshold)
			{
				// From here on the touch is a drag: the engine's mouse goes on from where the press went.
				dragged = true;
				cursorOffset = pressAt - first.pos;
				DoubleTap.Forget();
				setOff = mode == Mode.Held;
			}
			bool moved = dragged;
			bool held = Now - first.downTime > HoldSeconds;
			Mode was = mode;
			bool pressedNow = false;
			if (mode == Mode.Pending && framesDown >= 2)
			{
				// The game handled the press during the previous frame's dispatch: what did it do with it? The map's
				// selector takes a press of a double click too, without starting its box.
				if (InGame && (BoxActive || mapPressFrame >= touchFrame))
				{
					mode = Mode.Map;
				}
				else if (InGame && ToolActive && WindowAt(first.start) == null)
				{
					mode = Mode.Tool;
				}
				else
				{
					mode = Mode.Interface;
				}
			}
			switch (mode)
			{
				case Mode.Map:
					if (moved)
					{
						mode = Mode.Pan;
						KeepBoxOff();
						Pan(first.pos - first.start);
					}
					else if (held)
					{
						if (squadTouch)
						{
							mode = Mode.Box; // the game's own left button: lifting selects what is there, moving draws a box
						}
						else if (SomethingToOrder)
						{
							// A right click where the finger rests: orders for what is selected.
							KeepBoxOff();
							Button(Right, true);
							releaseRightNextFrame = 2;
							mode = Mode.Finished;
						}
						else
						{
							mode = Mode.Box; // the box stays, and moving the finger now selects an area
						}
					}
					break;
				case Mode.Pan:
					KeepBoxOff();
					Pan(delta);
					break;
				case Mode.Tool:
					if (MapTools.Deferred && moved)
					{
						// Looking for the place rather than pointing at it: the held-back press is dropped and the finger pans.
						MapTools.CancelDeferred();
						mode = Mode.Pan;
						KeepBoxOff();
						Pan(first.pos - first.start);
					}
					break;
				case Mode.ListPending:
				case Mode.ListHeld:
					if (moved)
					{
						Vector2 travelled = first.pos - first.start;
						// Along the list unless clearly across it: scrolling is what a drag in a list mostly means.
						bool along = Mathf.Abs(travelled.x) > 1.5f * Mathf.Abs(travelled.y) ? listAcross : listDown;
						if (mode == Mode.ListPending && along)
						{
							mode = Mode.ListScroll;
							Interface.BeginListScroll(listX, listY);
						}
						else
						{
							// Across the list, or after resting on it: the finger drags what it is on, as a mouse does. The
							// press now, where the touch began; the mouse sets off from there next frame.
							if (mode == Mode.ListHeld)
							{
								Interface.HoldBecameDrag();
								setOff = true;
							}
							mode = Mode.Interface;
							Button(Left, true);
							pressedNow = true;
						}
					}
					else if (mode == Mode.ListPending && held)
					{
						mode = Mode.ListHeld;
						Interface.HoldInList();
					}
					break;
				case Mode.Interface:
					if (held && !moved && !DragAndDropWidget.Dragging && !ReorderableWidget.Dragging && !Widgets.Painting)
					{
						// A finger resting on the interface is reading: the tooltips show now and nothing is clicked.
						mode = Mode.Held;
						Interface.HoldOnInterface();
						if (Letters.DismissibleAt(pressAt))
						{
							// On a letter it is what a right click does there, as on the PC: the letter goes away.
							Trace("hold on a letter at " + pressAt + ": a right click");
							Button(Right, true);
							releaseRightNextFrame = 2;
						}
					}
					break;
				default:
					break;
			}
			if (mode != Mode.Pan && !pressedNow)
			{
				MoveCursor(first, changed);
			}
			if (mode != was)
			{
				Trace("touch at " + first.pos + ": " + was + " -> " + mode + " (frame " + framesDown + ", box " + BoxActive + ", tool " + ToolActive + ", window " + (WindowAt(first.start)?.GetType().Name ?? "none") + ")");
				Recorder.TouchMode(mode.ToString());
			}
		}

		/// <summary>
		/// The engine's mouse during a touch of one finger. Until the finger has travelled the drag threshold the mouse
		/// stays where the press went: a tap is released on the control it pressed, however the fingertip rolls while it
		/// lifts (a button fires only when press and release are both on it). Once the finger drags, on the interface the
		/// mouse goes on from where it was and closes in on the finger within a few frames, so that a list or a slider
		/// does not jump by the threshold; after a hold (on the interface, in a list) it sets off a few pixels from the press
		/// first, as a mouse held still does; on the map it is under the finger at once. mobile.txt: tapSteady=0 lets the
		/// mouse follow every movement as before.
		/// </summary>
		private static void MoveCursor(Finger finger, bool changed)
		{
			if (!dragged)
			{
				if (!steady && changed)
				{
					Cursor(finger.pos + (pressAt - finger.start));
				}
				return;
			}
			if (setOff)
			{
				// A finger that rested has begun to drag. A mod that tells a drag from the mouse itself sees one begin once
				// the mouse is 4.5 pixels from where it was pressed, on what is under it then: the mouse that jumped the
				// threshold at once (after a hold on the interface, which pressed nothing) or a third of it and more (after
				// one in a list, which presses now) had left the box the hold began on, and DragSelect set the boxes from the
				// next one on (the side-by-side test, 2026-10-30). A mouse held still and then moved sets off from the press:
				// here it moves a few pixels the way the finger went first, and from the next frame on goes on as before.
				setOff = false;
				if (steady)
				{
					Vector2 to = pressAt + (finger.pos - finger.start).normalized * HeldDragStep;
					cursorOffset = mode == Mode.Held ? Vector2.zero : to - finger.pos;
					Cursor(to);
					return;
				}
			}
			if (mode == Mode.Held)
			{
				// Nothing was pressed: the mouse is under the finger, from the frame after it set off too.
				cursorOffset = Vector2.zero;
				if (cursorAt != finger.pos)
				{
					Cursor(finger.pos);
				}
				return;
			}
			if (mode != Mode.Interface && mode != Mode.Pending && mode != Mode.ListScroll)
			{
				cursorOffset = Vector2.zero;
			}
			if (cursorOffset != Vector2.zero)
			{
				cursorOffset *= 0.7f;
				if (cursorOffset.sqrMagnitude < 1f)
				{
					cursorOffset = Vector2.zero;
				}
				Cursor(finger.pos + cursorOffset);
			}
			else if (changed)
			{
				Cursor(finger.pos);
			}
		}

		private static void TwoFingers(Finger a, Finger b)
		{
			float distance = (a.pos - b.pos).magnitude;
			Vector2 centre = (a.pos + b.pos) / 2f;
			if (mode != Mode.Two)
			{
				// A hold the second finger joined ends here (its press is let go below), and a pinch is not a tap.
				Interface.HoldEnded();
				DoubleTap.Forget();
				mode = Mode.Two;
				CancelWhatTheFirstFingerStarted();
				Window under = WindowAt(centre);
				if (under != null)
				{
					BeginWindowGesture(under, centre);
				}
				Trace("two fingers around " + centre + ": " + (under != null ? "for the window " + under.GetType().Name : "for the map"));
				Recorder.TouchMode("two fingers" + (under != null ? " on " + under.GetType().Name : ""));
			}
			else if (gestureWindow != null)
			{
				Cursor(centre);
				if (pinchDistance > 1f && distance > 1f)
				{
					pendingWheel += WheelPerDoubling * Math.Log(distance / pinchDistance, 2.0);
				}
				if (Math.Abs(pendingWheel) >= 0.25)
				{
					glfwAndroidInjectScroll(0, pendingWheel);
					pendingWheel = 0;
				}
			}
			else if (InGame)
			{
				// The engine's mouse sits between the fingers: the game zooms towards it, so the ground stays under them.
				Cursor(centre);
				if (pinchDistance > 1f && distance > 1f)
				{
					Zoom(pinchDistance / distance);
				}
				Pan(centre - pinchCentre);
			}
			pinchDistance = distance;
			pinchCentre = centre;
			KeepBoxOff();
		}

		private static void BeginWindowGesture(Window window, Vector2 centre)
		{
			gestureWindow = window;
			pendingWheel = 0;
			Cursor(centre);
			Button(Middle, true);
		}

		private static void EndWindowGesture()
		{
			if (middleDown) Button(Middle, false);
			gestureWindow = null;
			pendingWheel = 0;
		}

		private static void CancelWhatTheFirstFingerStarted()
		{
			Interface.EndListScroll(false);
			KeepBoxOff();
			MapTools.CancelDeferred();
			if (Playing && Find.DesignatorManager != null && Find.DesignatorManager.Dragger.Dragging)
			{
				Find.DesignatorManager.Dragger.EndDrag();
			}
			// Nothing may act on the release of the first finger's press (a button fires on the release), and a window it
			// began to drag does not follow the mouse to the middle of the fingers.
			GUIUtility.hotControl = 0;
			Interface.StopWindowDrag();
			if (leftDown) Button(Left, false);
			// The right and the middle button of a finger (a press from a hold waits for its own release).
			pendingPress = -1;
			if (rightDown && releaseRightNextFrame == 0) Button(Right, false);
			if (middleDown) Button(Middle, false);
		}

		private static void KeepBoxOff()
		{
			if (OnWorld)
			{
				if (Find.WorldSelector != null) Find.WorldSelector.dragBox.active = false;
			}
			else if (Playing && Find.Selector != null)
			{
				Find.Selector.dragBox.active = false;
			}
		}

		/// <summary>Moves the camera so that the ground follows the finger (window pixels, y down).</summary>
		private static void Pan(Vector2 pixels)
		{
			if (pixels == Vector2.zero)
			{
				return;
			}
			Vector2 logical = pixels / Prefs.UIScale;
			if (OnWorld)
			{
				WorldRotation(Find.WorldCameraDriver) += new Vector2(-logical.x, logical.y) / GenWorldUI.CurUITileSize() * 0.273f;
			}
			else if (Find.CurrentMap != null)
			{
				MapDolly(Find.CameraDriver) += new Vector2(-logical.x, logical.y) / UI.CurUICellSize();
			}
		}

		/// <summary>Factor above 1 zooms out.</summary>
		private static void Zoom(float factor)
		{
			if (OnWorld)
			{
				ref float altitude = ref WorldAltitude(Find.WorldCameraDriver);
				altitude = Mathf.Clamp(altitude * factor, WorldCameraDriver.MinAltitude, 1100f);
			}
			else if (Find.CurrentMap != null)
			{
				CameraDriver driver = Find.CameraDriver;
				ref float size = ref MapSize(driver);
				size = Mathf.Clamp(size * factor, driver.config.sizeRange.min, driver.config.sizeRange.max);
			}
		}

		private static double Now => Time.realtimeSinceStartupAsDouble;

		// ---- the fingers ----

		/// <summary>Takes the finger events the GLFW shim recorded since the last frame. True if anything moved.</summary>
		private static bool Drain()
		{
			bool changed = false;
			int n;
			fixed (TouchEvent* events = buffer)
			{
				while ((n = glfwAndroidPollTouches(events, buffer.Length)) > 0)
				{
					for (int i = 0; i < n; i++)
					{
						changed |= Apply(events[i].type, events[i].id, new Vector2(events[i].x, events[i].y));
					}
					if (n < buffer.Length) break;
				}
			}
			return changed;
		}

		internal static bool Apply(int type, int id, Vector2 pos)
		{
			Finger finger = fingers.Find(f => f.id == id);
			switch (type)
			{
				case Down:
					if (finger == null)
					{
						fingers.Add(new Finger { id = id, pos = pos, start = pos, downTime = Now });
					}
					// Every finger, a quick tap's too (it lifts before a frame has seen it down).
					InputMode.FingerTouched();
					return true;
				case Move:
					if (finger != null && finger.pos != pos)
					{
						finger.pos = pos;
						return true;
					}
					return false;
				case EditorAction:
					// Not a finger: the soft keyboard's Done (it comes the same way, in order with the fingers)
					Interface.EditorAction();
					return false;
				case Up:
				case Cancel:
					if (finger != null)
					{
						finger.pos = pos;
						fingers.Remove(finger);
						if (fingers.Count == 0)
						{
							liftedAt = pos;
						}
						if (!finger.seen && type == Up && fingers.Count == 0 && mode == Mode.None)
						{
							quickTap = finger;
						}
					}
					return true;
			}
			return false;
		}

		/// <summary>
		/// Scripted touches for testing from the PC (adb cannot do two fingers): a file gesture.txt next to the game,
		/// one event per line "seconds id type x y" (type: down, move, up), played from the moment the file is seen,
		/// then deleted.
		/// </summary>
		private static class Replay
		{
			private static readonly string FilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "gesture.txt");
			private static readonly Queue<(double at, int id, int type, Vector2 pos)> steps = new Queue<(double, int, int, Vector2)>();
			private static double started;
			private static int checkCountdown;
			private static bool verboseBefore;

			/// <summary>Plays the steps that are due; true if a finger moved (as Drain says for real fingers).</summary>
			public static bool Feed()
			{
				if (steps.Count == 0)
				{
					if (--checkCountdown > 0)
					{
						return false;
					}
					checkCountdown = 30;
					if (!File.Exists(FilePath))
					{
						return false;
					}
					var culture = System.Globalization.CultureInfo.InvariantCulture;
					foreach (string line in File.ReadAllLines(FilePath))
					{
						string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
						if (parts.Length < 5 || parts[0].StartsWith("#"))
						{
							continue;
						}
						int type = parts[2] == "down" ? Down : parts[2] == "up" ? Up : Move;
						steps.Enqueue((double.Parse(parts[0], culture), int.Parse(parts[1]), type, new Vector2(float.Parse(parts[3], culture), float.Parse(parts[4], culture))));
					}
					File.Delete(FilePath);
					started = Now;
					// Traced while it plays; afterwards as before (the trace of every frame's float menu while a pawn is drafted is a lot).
					verboseBefore = Verbose;
					Verbose = true;
					Log("replaying " + steps.Count + " scripted touch events");
					return false;
				}
				bool changed = false;
				while (steps.Count > 0 && Now - started >= steps.Peek().at)
				{
					var step = steps.Dequeue();
					changed |= Apply(step.type, 1000 + step.id, step.pos);
				}
				if (steps.Count == 0)
				{
					Verbose = verboseBefore;
				}
				return changed;
			}
		}
	}
}
