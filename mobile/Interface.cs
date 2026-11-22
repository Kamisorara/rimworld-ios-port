using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.Steam;

namespace PortMobile
{
	/// <summary>
	/// The interface under a finger:
	///   lists scroll under a dragging finger, with the game's own Steam Deck code (which it only runs on a Deck),
	///   and such a drag does not drag the window along;
	///   a hold shows the tooltips of what is under the finger at once, keeps them after the finger lifts until
	///   the next touch, and clicks nothing;
	///   the tooltips of a tap do not linger (the engine's mouse stays where the last finger was);
	///   the soft keyboard comes up while a text field has the focus;
	///   a search box (the game's QuickSearchWidget: the info card's, the load dialog's; a mod's, by "search" in its name),
	///   and the name field of a save dialog (the game's, a scenario's, an ideoligion's, a mod list's: Dialog_FileList;
	///   those of mods made like it: NameFields), take the focus only from a finger on them: given the focus by code (as
	///   when their window opens), it is let go at once, so that the keyboard does not come up by itself (testers,
	///   2026-10-22: saving by hand brought it up over the save dialog; the name is typed only to change it). Other text
	///   fields (a name to type in a rename window) keep such a focus until the player has put them away (Done, a finger
	///   elsewhere): from then on they too take it back only from a finger (RimTalk's talk window gives its field the
	///   focus whenever nothing has it, and the keyboard came straight back up). mobile.txt: searchFocusOnTap=0 lets them
	///   all keep it;
	///   a finger that lands anywhere but on a text field, the typing bar or the key wheel takes the focus from the field
	///   that has it once the touch is over, and the keyboard goes down (the game means to do that for its search boxes,
	///   but under this engine the press has already been used by the window when the search box looks at it). Until
	///   then the field keeps it, as under a mouse on the PC: a button that reads it when it is clicked finds it, and the
	///   key wheel's keys go to the field;
	///   the soft keyboard's Done (or Enter) finishes the typing: the field lets go of the focus and the keyboard goes
	///   down. It is not the game's Enter, which accepts or closes the window the field is in (testers, 2026-10-22: a
	///   number typed in the caravan's window cancelled it, a search box kept the keyboard, the debug actions' search ran
	///   the first action). Only a chat's field gets the Enter, which sends (EnterFields).
	/// With a mouse or a keyboard connected the tooltips, the search boxes' focus and the soft keyboard are the game's
	/// again (InputMode).
	/// </summary>
	internal static class Interface
	{
		[System.Runtime.InteropServices.DllImport("glfw")] private static extern void glfwAndroidShowKeyboard(int show);
		[System.Runtime.InteropServices.DllImport("glfw")] private static extern void glfwAndroidInjectKey(int key, int action, int mods);
		[System.Runtime.InteropServices.DllImport("glfw")] private static extern void glfwAndroidKeyboardForced(int forced);

		private const int GlfwKeyEnter = 257, GlfwPress = 1, GlfwRelease = 0;
		// The chats' fields, by the names they are given: there the keyboard's Done is the Enter that sends (Multiplayer's
		// chat; Phinix's chat and RimTalk's talk window, which send on Enter or with their button: the Done only took the
		// focus away, and they gave it straight back; the compatibility audit, 2026-10-30).
		private static readonly HashSet<string> EnterFields = new HashSet<string> { "chat_input", "Phinix_chatMessageField", "CustomTalkTextField" };

		/// <summary>The soft keyboard is up (asked for: a text field has the focus).</summary>
		public static bool KeyboardShown => keyboardShown;

		/// <summary>The soft keyboard's action key: typing is finished, or, in a chat's field, the Enter that sends.</summary>
		public static void EditorAction()
		{
			string name = GUI.GetNameOfFocusedControl();
			if (Touch.Verbose) Console.WriteLine("[mobile] the keyboard's Done (control " + GUIUtility.keyboardControl + ", " + name + ")");
			if (name != null && EnterFields.Contains(name))
			{
				glfwAndroidInjectKey(GlfwKeyEnter, GlfwPress, 0);
				glfwAndroidInjectKey(GlfwKeyEnter, GlfwRelease, 0);
				return;
			}
			PutAway();
			GUIUtility.keyboardControl = 0;
			// A focus that code gives the field back before the next look (LetGoOfGivenSearchFocus) is a change too: given back
			// in the same frame (RimTalk's talk window does), it looked as if the field had kept it, and the keyboard stayed up.
			lastFocus = 0;
		}

		/// <summary>The player puts away the field that has the focus: code that gives it the focus again is not followed until a finger is on it.</summary>
		private static void PutAway()
		{
			putAwayFocus = GUIUtility.keyboardControl;
			putAwayWindow = GUI.GetFocusedControlWindowId();
		}

		private static readonly IDictionary ActiveTips = (IDictionary)AccessTools.Field(typeof(TooltipHandler), "activeTips").GetValue(null);
		private static readonly FieldInfo TipFrame = AccessTools.Field(typeof(TooltipHandler), "frame");
		private static readonly FieldInfo DraggingWindow = AccessTools.Field(typeof(GUI), "_draggingWindowId");
		private static readonly FieldInfo ButtonDown = AccessTools.Field(typeof(GUI), "_buttonDownActive");
		private static readonly FieldInfo CurrentScrollView = AccessTools.Field(typeof(SteamDeck), "currentScrollView");

		// While a hold on the interface lasts, and a frame or two after it, the left button's events are swallowed.
		private static int suppressClickUntilFrame = -1;
		// What a hold showed stays until the next touch.
		private static bool keepTips;
		private static bool keyboardShown, askKeyboardAgain;
		// The text field that took the focus from a press on it, and the focus as last seen.
		private static int tappedFocus, lastFocus;
		private static bool searchOnlyFromTap = true;
		// The text field that had the focus when a finger landed away from it (0: none), the window it is drawn in, and the
		// last frame something of that touch was still on its way to the game (LetGoAfterTouch).
		private static int letGoFocus, letGoWindow, letGoTouchFrame;
		// The text field the player last put away (the keyboard's Done, a finger elsewhere; 0: none) and its window: a
		// focus that code gives it again is let go until a finger is on it (LetGoOfGivenSearchFocus, AfterTextField).
		private static int putAwayFocus, putAwayWindow;

		// A finger that begins in a list with something to scroll presses nothing until it is known to be a tap (see
		// Touch): while it scrolls, the list that begins at this point of the screen follows it, and goes on for a
		// while after it lifts. mobile.txt: listTouch=0 gives the press at once again, and the scrolling of the game's
		// own Steam Deck code with it.
		private static bool listTouch = true;
		private static bool listScrolling, coasting;
		private static float activeX, activeY;

		public static bool ListTouch => listTouch && TapAssist.Ready;

		// The lists being drawn in the current press event (they nest): whether the press landed in each.
		private static readonly List<bool> open = new List<bool>();
		// The scroll views being drawn in the current repaint (they nest): the number TapAssist gave each (-1: not noted).
		private static readonly List<int> drawn = new List<int>();
		// The scroll view the game's BeginScrollView (or the developer tools') noted (BeforeScrollView) and moved (FingerScroll)
		// just before it begins the engine's: that one is not noted or moved again.
		private static bool notedBefore, scrolledBefore;
		private static Rect notedRect, scrolledRect;

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SteamDeck), nameof(SteamDeck.HandleTouchScreenScrollViewScroll)), prefix: new HarmonyMethod(typeof(Interface), nameof(FingerScroll)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.BeginScrollView), new[] { typeof(Rect), typeof(Vector2).MakeByRefType(), typeof(Rect), typeof(bool) }), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeScrollView)));
			// The developer tools' windows (debug actions, the log, tweak values) have scroll views of their own: they call the
			// finger scrolling above but were not noted as lists, so a swipe dragged instead (a tester, 2026-10-24: "开发者选项有些
			// 在下面划不动"; he swiped at the scrollbar on the screen's right edge).
			harmony.Patch(AccessTools.Method(typeof(LudeonTK.DevGUI), nameof(LudeonTK.DevGUI.BeginScrollView)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeScrollView)));
			// Lists drawn with the engine's scroll view straight away (GUI.BeginScrollView: the game's starting pawns, RimTalk's
			// event filter, Adaptive Storage's def editor, GUILayout's scroll views) scrolled by the wheel only, which a finger does
			// not give: a swipe was a drag. They are noted and follow the finger at the engine's level, unless one of the two above
			// has begun them. Every scroll view ends there too, once, whichever way a mod begins and ends it. An engine without
			// these as they are now (the types of their parameters, the names the prefix takes) leaves the lists to end where the
			// game ends them, as before: a patch that fails here does not stop the rest of the port's start.
			MethodInfo engineBegin = AccessTools.Method(typeof(GUI), "BeginScrollViewImpl", new[] { typeof(Rect), typeof(Vector2), typeof(Rect), typeof(GUIStyle), typeof(GUIStyle) });
			MethodInfo engineEnd = AccessTools.Method(typeof(GUI), nameof(GUI.EndScrollView), new[] { typeof(bool) });
			MethodInfo beforeEngine = AccessTools.Method(typeof(Interface), nameof(BeforeEngineScrollView));
			bool begun = false, atEngine = false;
			try
			{
				if (engineBegin != null && engineEnd != null)
				{
					harmony.Patch(engineBegin, prefix: new HarmonyMethod(beforeEngine));
					begun = true;
					harmony.Patch(engineEnd, postfix: new HarmonyMethod(typeof(Interface), nameof(AfterScrollView)));
					atEngine = true;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] the engine's scroll view could not be patched: " + e.Message);
				// Begun there but not ended there, the lists drawn with it straight away would never end.
				if (begun)
				{
					harmony.Unpatch(engineBegin, beforeEngine);
				}
			}
			if (!atEngine)
			{
				Console.WriteLine("[mobile] the engine's scroll view is not as expected: lists drawn with it straight away are not swiped");
				harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.EndScrollView)), postfix: new HarmonyMethod(typeof(Interface), nameof(AfterScrollView)));
				harmony.Patch(AccessTools.Method(typeof(LudeonTK.DevGUI), nameof(LudeonTK.DevGUI.EndScrollView)), postfix: new HarmonyMethod(typeof(Interface), nameof(AfterScrollView)));
			}
			harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.OnGUI)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeGui)));
			// The engine hands a mouse event to the window under the mouse before the game's OnGUI sees it.
			harmony.Patch(AccessTools.Method(typeof(GUI), nameof(GUI.DispatchInputToWindows)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeGui)));
			harmony.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.ButtonInvisibleDraggable)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeDraggableButton)));
			KeepHoldFromDragSelect(harmony);
			harmony.Patch(AccessTools.Method(typeof(TooltipHandler), nameof(TooltipHandler.DoTooltipGUI)), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeTooltips)));
			listTouch = Settings.Get("listTouch", 1) != 0;
			searchOnlyFromTap = Settings.Get("searchFocusOnTap", 1) != 0;
			harmony.Patch(AccessTools.Method(typeof(GUI), "TextFieldImpl"), prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeTextField)), postfix: new HarmonyMethod(typeof(Interface), nameof(AfterTextField)));
		}

		// ---- told by Touch ----

		/// <summary>A new touch begins: what the last hold showed goes away.</summary>
		public static void TouchBegan()
		{
			// The keyboard may have been dismissed by the system (Back) while the field kept the focus: a touch asks for it
			// again. (Forgetting that it was shown would also keep it up when the touch takes the focus away.)
			askKeyboardAgain = true;
			if (keepTips)
			{
				keepTips = false;
				ActiveTips.Clear();
			}
		}

		/// <summary>A finger has rested on the interface: for reading, not for clicking.</summary>
		public static void HoldOnInterface()
		{
			ShowTipsNow();
			keepTips = true;
			GUIUtility.hotControl = 0;
			// The engine's button remembers its press by itself: forgotten, so that the release finds nothing to fire.
			ButtonDown?.SetValue(null, false);
			suppressClickUntilFrame = int.MaxValue;
		}

		/// <summary>A list still moving on its own stops under a finger. True if the list that begins at this point of the screen was moving.</summary>
		public static bool StopCoasting(float originX, float originY)
		{
			bool was = coasting && Mathf.Abs(originX - activeX) <= 1.5f && Mathf.Abs(originY - activeY) <= 1.5f;
			coasting = false;
			scrollViewVelocity = default(Vector2);
			return was;
		}

		/// <summary>A finger has rested on a list (nothing was pressed): the tooltips show now, and stay after it lifts.</summary>
		public static void HoldInList()
		{
			ShowTipsNow();
			keepTips = true;
		}

		/// <summary>The finger that rested on a list now drags what it is on: the tooltips of the rest go away.</summary>
		public static void HoldBecameDrag()
		{
			if (keepTips)
			{
				keepTips = false;
				ActiveTips.Clear();
			}
		}

		/// <summary>The finger drags the list that begins at this point of the screen.</summary>
		public static void BeginListScroll(float originX, float originY)
		{
			listScrolling = true;
			coasting = false;
			activeX = originX;
			activeY = originY;
			scrollVelocityRecords.Clear();
			scrollViewVelocity = default(Vector2);
		}

		/// <summary>The finger has lifted (keepSpeed), or the scrolling was broken off: the list keeps the speed of the last moments, as the game does it on a Steam Deck.</summary>
		public static void EndListScroll(bool keepSpeed)
		{
			if (!listScrolling)
			{
				return;
			}
			listScrolling = false;
			scrollViewVelocity = default(Vector2);
			if (keepSpeed)
			{
				for (int i = 0; i < scrollVelocityRecords.Count; i++)
				{
					if (Time.time - scrollVelocityRecords[i].time < 0.057f)
					{
						scrollViewVelocity += scrollVelocityRecords[i].delta;
					}
				}
			}
			coasting = scrollViewVelocity != default(Vector2);
		}

		/// <summary>The finger of a hold has lifted: its release (still on its way) is swallowed too.</summary>
		public static void HoldEnded()
		{
			if (suppressClickUntilFrame == int.MaxValue)
			{
				suppressClickUntilFrame = Time.frameCount + 2;
			}
		}

		/// <summary>Once per frame: the soft keyboard follows the text focus.</summary>
		public static void Update()
		{
			// A press that a control in a list used is no longer seen as a press when the list ends: nothing is carried over.
			open.Clear();
			drawn.Clear();
			notedBefore = scrolledBefore = false;
			LetGoAfterTouch();
			LetGoOfGivenSearchFocus();
			bool wanted = UnityStubs.Internal.TextFocus.CapturesText;
			bool again = askKeyboardAgain && wanted;
			askKeyboardAgain = false;
			if (wanted != keyboardShown || again)
			{
				keyboardShown = wanted;
				if (Touch.Verbose) Console.WriteLine("[mobile] soft keyboard " + (wanted ? "wanted" : "not wanted") + " (control " + GUIUtility.keyboardControl + ")");
				if (wanted)
				{
					KeyboardForced(!InputMode.Keyboard);
				}
				glfwAndroidShowKeyboard(wanted ? 1 : 0);
			}
		}

		private static bool forcedUnknown;

		/// <summary>Whether the soft keyboard is forced up for the field (no hardware keyboard), or left to the input method.</summary>
		private static void KeyboardForced(bool forced)
		{
			if (forcedUnknown)
			{
				return;
			}
			try
			{
				glfwAndroidKeyboardForced(forced ? 1 : 0);
			}
			catch (Exception e)
			{
				// An older APK: always forced, as before.
				forcedUnknown = true;
				Console.WriteLine("[mobile] soft keyboard: always forced up (" + e.GetType().Name + ")");
			}
		}

		/// <summary>
		/// A hardware keyboard came or went (InputMode): a soft keyboard that is up for a field is asked for again the new
		/// way (without one forced up, with one left to the input method, which usually puts it away).
		/// </summary>
		public static void KeyboardModeChanged(bool keyboard)
		{
			KeyboardForced(!keyboard);
			if (keyboardShown)
			{
				glfwAndroidShowKeyboard(0);
				keyboardShown = false;
			}
		}

		/// <summary>
		/// A second finger has landed: the window the first one began to drag stays where it is. (The engine moves a
		/// dragged window with the mouse on every pass until the button is up, and the mouse is about to jump to the
		/// middle of the two fingers: the window jumped with it.)
		/// </summary>
		public static void StopWindowDrag()
		{
			if (DraggingWindow != null && (int)DraggingWindow.GetValue(null) != -1)
			{
				DraggingWindow.SetValue(null, -1);
				if (Touch.Verbose) Console.WriteLine("[mobile] a second finger: the window being dragged stays");
			}
		}

		/// <summary>
		/// Told by Touch when a finger lands (window pixels, y down). A finger anywhere but on the text field that has the
		/// focus, the typing bar that shows it, or the key wheel (whose keys are for the field) takes the focus from the
		/// field once the touch is over (LetGoAfterTouch). Taken when the finger landed, it was gone before the game had the
		/// press: a button that reads the focus when it is clicked found none (RimTalk's "insert variable" put the variable
		/// at the start of the prompt instead of at the caret), and the key wheel's keys went nowhere (the compatibility
		/// audit, 2026-10-30).
		/// </summary>
		public static void FingerLanded(Vector2 at)
		{
			int focus = GUIUtility.keyboardControl;
			if (focus == 0 || !TapAssist.Ready)
			{
				return;
			}
			if (TapAssist.FieldAt(at) || TypingBar.At(at))
			{
				// What an earlier touch away from the field was to take stays: the finger is back with the field.
				letGoFocus = 0;
				return;
			}
			// No reason to bring back a keyboard the system put away (TouchBegan): the touch is to take the focus away, or it
			// is on the key wheel. One that leaves another field with the focus (the field of a window it opened) asks for
			// it once it is over (LetGoAfterTouch).
			askKeyboardAgain = false;
			if (KeyWheel.At(at))
			{
				// The wheel's keys are for the field, which keeps the focus while they are pressed. It is no tap on the field:
				// what an earlier touch away from the field was to take still goes once this touch is over too.
				return;
			}
			if (Touch.Verbose && focus != letGoFocus) Console.WriteLine("[mobile] a finger away from the text field: its focus goes when the touch is over (" + GUI.GetNameOfFocusedControl() + ")");
			letGoFocus = focus;
			letGoWindow = GUI.GetFocusedControlWindowId();
			letGoTouchFrame = Time.frameCount;
		}

		/// <summary>
		/// Once a frame, before the keyboard follows the focus: the field a finger landed away from (FingerLanded) lets go of
		/// the focus once nothing of that touch is on its way to the game any more and the game has handled the last of it
		/// (a release goes out in the last frame something was on its way, and is handled in the next frame or the one after,
		/// as HoldEnded counts). A focus that went elsewhere meanwhile stays where it went: to a field the touch pressed, to
		/// the field of a window the touch opened (a field of another window is another, whatever its id), and the keyboard
		/// is asked for again for it.
		/// </summary>
		private static void LetGoAfterTouch()
		{
			if (letGoFocus == 0)
			{
				return;
			}
			if (!Touch.AllReleased)
			{
				letGoTouchFrame = Time.frameCount;
				return;
			}
			if (Time.frameCount <= letGoTouchFrame + 2)
			{
				return;
			}
			if (GUIUtility.keyboardControl == letGoFocus && GUI.GetFocusedControlWindowId() == letGoWindow)
			{
				if (Touch.Verbose) Console.WriteLine("[mobile] the touch away from the text field is over: its focus goes (" + GUI.GetNameOfFocusedControl() + ")");
				PutAway();
				GUI.FocusControl(null);
			}
			else if (GUIUtility.keyboardControl != 0)
			{
				// The touch left another field with the focus, as a window it opened gives its own: the keyboard is asked for
				// again, as for a field a finger presses. The system may have put it away (Back) while the old field had the
				// focus; the port still counts it as up then, and nothing else would bring it back (when the focus went as
				// the finger landed, the new field brought it up).
				askKeyboardAgain = true;
			}
			letGoFocus = 0;
		}

		/// <summary>Prefix of the engine's TextFieldImpl: the focus before the field had its turn.</summary>
		public static void BeforeTextField(out int __state)
		{
			__state = GUIUtility.keyboardControl;
		}

		/// <summary>Postfix of the engine's TextFieldImpl: a field that took the focus while handling a press took it from a finger, and one pressed while it has the focus keeps it (put away, it is the player's again).</summary>
		public static void AfterTextField(int __state, string __result)
		{
			int focus = GUIUtility.keyboardControl;
			if (focus == 0 || OriginalEventUtility.EventType != EventType.MouseDown)
			{
				return;
			}
			if (focus == GUIUtility.hotControl)
			{
				// The press is on the field that has the focus (it holds the press now). A field the player put away is
				// theirs again, and code may give it the focus as before. The press does not always change the focus, which
				// is how LetGoOfGivenSearchFocus knows a finger's (fromFinger): Phinix's chat gives its field the focus in the
				// layout pass that comes before the press, so, once put away, the chat was let go again in every frame
				// however often a finger tapped it.
				if (focus == putAwayFocus && GUI.GetFocusedControlWindowId() == putAwayWindow)
				{
					putAwayFocus = 0;
				}
				if (Touch.TouchInProgress)
				{
					// The focus stays after the touch, and a keyboard the system put away comes back, also where TapAssist
					// does not know the field (tapAssist=0).
					letGoFocus = 0;
					askKeyboardAgain = true;
				}
			}
			if (focus != __state)
			{
				tappedFocus = focus;
				if (Touch.TouchInProgress && !InputMode.Mouse && IsNumber(__result))
				{
					SelectAllNextTime(focus);
				}
			}
		}

		private static AccessTools.FieldRef<int> selectAllOnFocusId;
		private static bool noSelectAll;

		/// <summary>
		/// A number field tapped with a finger is selected whole: what is typed replaces it. The caret went after the 0 of
		/// a trade count, and the game takes "0-" for no number and turns "02" for a thing the colony only sells back into
		/// 0, so typed counts seemed to do nothing (the user, 2026-10-26: "用小键盘填写数字好像不生效"). A mouse places
		/// the caret where it clicks, as on the PC; the engine selects a field whole when the game focuses it by name.
		/// </summary>
		private static void SelectAllNextTime(int id)
		{
			if (noSelectAll)
			{
				return;
			}
			try
			{
				if (selectAllOnFocusId == null)
				{
					selectAllOnFocusId = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(GUI), "_selectAllOnFocusId"));
				}
				selectAllOnFocusId() = id;
			}
			catch (Exception e)
			{
				noSelectAll = true;
				Console.WriteLine("[mobile] number fields: not selected whole on a tap (" + e.Message + ")");
			}
		}

		private static bool IsNumber(string text)
		{
			if (string.IsNullOrEmpty(text) || text.Length > 16)
			{
				return false;
			}
			bool digit = false;
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (c >= '0' && c <= '9')
				{
					digit = true;
				}
				else if (!(c == '.' || c == ',' || (c == '-' && i == 0)))
				{
					return false;
				}
			}
			return digit;
		}

		/// <summary>Once a frame, before the keyboard follows the focus: a search box, a save dialog's name (NameFields) or the field the player put away, that got the focus some other way than from a finger, lets it go.</summary>
		private static void LetGoOfGivenSearchFocus()
		{
			int focus = GUIUtility.keyboardControl;
			if (focus == lastFocus)
			{
				return;
			}
			lastFocus = focus;
			// A finger's focus counts for the change it made, once. Control ids are handed out in drawing order, so a window
			// opened again gives its fields the ids they had: kept, a save dialog's name tapped once took the focus the
			// dialog gives it by code the next time it opened, and the keyboard came up by itself (the user, 2026-10-25:
			// "存档的时候，有时候小键盘还是会自己弹出来").
			bool fromFinger = focus != 0 && focus == tappedFocus;
			tappedFocus = 0;
			if (Touch.Verbose) Console.WriteLine("[mobile] the keyboard focus is now " + focus + " (" + GUI.GetNameOfFocusedControl() + ", from a finger: " + fromFinger + ")");
			// The field the player put away, in its window: another field of its name (one id in every window) is another.
			bool putAway = focus != 0 && focus == putAwayFocus && GUI.GetFocusedControlWindowId() == putAwayWindow;
			if (putAway && fromFinger)
			{
				// Tapped again: the player's to type in, and code may give it the focus as before.
				putAwayFocus = 0;
			}
			// With a hardware keyboard the focus stays where the game puts it, so that typing goes there at once.
			if (!searchOnlyFromTap || InputMode.Keyboard || focus == 0 || fromFinger)
			{
				return;
			}
			string name = GUI.GetNameOfFocusedControl();
			if (IsSearchBox(name) || NameFields.Contains(name) || putAway)
			{
				GUI.FocusControl(null);
				lastFocus = 0;
				if (Touch.Verbose) Console.WriteLine("[mobile] the field " + name + " was given the focus without a finger on it" + (putAway ? " after it was put away" : "") + ": let go");
			}
		}

		/// <summary>
		/// The name fields of save dialogs, which focus them when they open: the game's (Dialog_FileList.DoTypeInField), and
		/// those that mods made after it, RimHUD's for a layout preset (Dialog_SavePreset, again when its docked or floating
		/// box is ticked) and UF Light Industries' for a configuration (Dialog_UFConfigList): the keyboard came up over them
		/// by itself (the compatibility audit, 2026-10-30).
		/// </summary>
		private static readonly HashSet<string> NameFields = new HashSet<string> { "MapNameField", "PresetName", "UFConfigNameField" };

		/// <summary>
		/// A search box by the name it was given: the game's QuickSearchWidget, and the mods' own (Dubs Mint Menus focuses
		/// "Search", "BuildSearch", "BenchSearch", "ReSearch" when its windows open), and filter boxes (the debug actions'
		/// "DebugFilter", focused when the window opens: the soft keyboard covered half of it).
		/// </summary>
		private static bool IsSearchBox(string name)
		{
			return name != null && (name.StartsWith("QuickSearchWidget_", StringComparison.Ordinal) || name.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("filter", StringComparison.OrdinalIgnoreCase) >= 0);
		}

		public static bool FingerDown => Touch.TouchInProgress;

		// ---- scrolling ----

		private static readonly List<(float time, Vector2 delta)> scrollVelocityRecords = new List<(float, Vector2)>();
		private static Rect? currentScrollView, scrollViewWithVelocity;
		private static Vector2 currentScrollViewStartMousePos, scrollViewVelocity;
		private static bool scrollMouseTraveledEnoughDist;
		private static int consumeAllMouseUpEventsOnFrame = -1;
		// The frame whose coasting step is done; the scroll view the last BeginScrollView was for (its end, for Clamp).
		private static int coastFrame = -1;
		private static Rect lastOutRect, lastViewRect;
		// The size of each list's contents as it was last drawn, by the scroll view's rectangle (see Clamp).
		private static readonly Dictionary<Rect, Vector2> drawnViews = new Dictionary<Rect, Vector2>();

		/// <summary>
		/// Stands in for SteamDeck.HandleTouchScreenScrollViewScroll (the game calls it from every scroll view and
		/// returns at once off a Deck): the list follows the finger, keeps some speed when it lifts, and the release
		/// after a scroll clicks nothing. The game's own logic, with the Deck check left out.
		/// </summary>
		public static bool FingerScroll(Rect outRect, ref Vector2 scrollPosition)
		{
			scrolledRect = outRect;
			scrolledBefore = true;
			if (ListTouch)
			{
				// Only the list the finger began in moves; it is known by where it begins on the screen.
				if ((!listScrolling && !coasting) || !TapAssist.ScreenOrigin(outRect, out float x, out float y) || Mathf.Abs(x - activeX) > 1.5f || Mathf.Abs(y - activeY) > 1.5f)
				{
					return false;
				}
				if (listScrolling)
				{
					// How far the mouse (which is with the finger) moved in this frame; the game hands it out once a frame.
					Vector2 moved = UnityGUIBugsFixer.CurrentEventDelta;
					if (moved != Vector2.zero)
					{
						// The game counts in units of its scale; a window made smaller to fit has smaller ones.
						moved *= Prefs.UIScale / TapAssist.CurrentScale();
						scrollPosition -= moved;
						if (scrollVelocityRecords.Count > 60)
						{
							scrollVelocityRecords.RemoveRange(0, 40);
						}
						scrollVelocityRecords.Add((Time.time, -moved));
						Clamp(outRect, ref scrollPosition);
					}
				}
				else if (coastFrame != Time.frameCount)
				{
					Coast(outRect, ref scrollPosition);
					coasting = scrollViewVelocity != default(Vector2);
				}
				return false;
			}
			if (DragAndDropWidget.Dragging || Widgets.Painting || ReorderableWidget.Dragging)
			{
				return false;
			}
			if (Input.GetMouseButtonDown(0) && FingerDown && Mouse.IsOver(outRect) && !Mouse.IsOver(new Rect(outRect.xMax - 16f, outRect.y, 16f, outRect.height)) && !Mouse.IsOver(new Rect(outRect.x, outRect.yMax - 16f, outRect.width, 16f)))
			{
				currentScrollView = outRect;
				currentScrollViewStartMousePos = UI.MousePositionOnUIInverted;
				scrollMouseTraveledEnoughDist = false;
				scrollVelocityRecords.Clear();
				if (scrollViewWithVelocity == currentScrollView)
				{
					scrollViewWithVelocity = null;
					scrollViewVelocity = default(Vector2);
				}
			}
			else if (Input.GetMouseButton(0) && currentScrollView == outRect)
			{
				if (!scrollMouseTraveledEnoughDist && (UI.MousePositionOnUIInverted - currentScrollViewStartMousePos).sqrMagnitude > 100f)
				{
					scrollMouseTraveledEnoughDist = true;
				}
				if (scrollMouseTraveledEnoughDist)
				{
					Vector2 delta = UnityGUIBugsFixer.CurrentEventDelta;
					scrollPosition -= delta;
					scrollVelocityRecords.Add((Time.time, -delta));
					Clamp(outRect, ref scrollPosition);
				}
			}
			else if (!Input.GetMouseButton(0) && currentScrollView == outRect)
			{
				if (scrollMouseTraveledEnoughDist)
				{
					scrollViewWithVelocity = currentScrollView;
					scrollViewVelocity = default(Vector2);
					for (int i = 0; i < scrollVelocityRecords.Count; i++)
					{
						if (Time.time - scrollVelocityRecords[i].time < 0.057f)
						{
							scrollViewVelocity += scrollVelocityRecords[i].delta;
						}
					}
					consumeAllMouseUpEventsOnFrame = Time.frameCount;
				}
				currentScrollView = null;
			}
			if (coastFrame != Time.frameCount && scrollViewWithVelocity.HasValue && scrollViewWithVelocity.Value == outRect && scrollViewVelocity != default(Vector2))
			{
				Coast(outRect, ref scrollPosition);
			}
			if (consumeAllMouseUpEventsOnFrame == Time.frameCount && Event.current.type == EventType.MouseUp)
			{
				Event.current.Use();
			}
			return false;
		}

		/// <summary>
		/// A list that keeps the speed of the finger moves on: once a frame, in the frame's first pass (its layout), before
		/// the list decides which rows it draws. In the repaint (as the game does it on a Steam Deck) the step came after
		/// that: the rows it brought in stayed blank for the frame, a band at the bottom while the list slid (a tester,
		/// 2026-10-24: a storage filter "very long" and blank while it slid on, and a tap on the blank opened the card of an
		/// item: Dubs Mint Menus puts an invisible button on every row's name). The speed is the distance of 1/60 s, as on
		/// a Deck, and the step follows the frame's length: at 120 frames a fling went twice as fast and as far.
		/// </summary>
		private static void Coast(Rect outRect, ref Vector2 scrollPosition)
		{
			coastFrame = Time.frameCount;
			scrollPosition += scrollViewVelocity * Mathf.Min(Time.deltaTime * 60f, 4f);
			scrollViewVelocity = Vector2.MoveTowards(scrollViewVelocity, default(Vector2), Time.deltaTime * 90f);
			Vector2 before = scrollPosition;
			Clamp(outRect, ref scrollPosition);
			// At an end it stops.
			if (scrollPosition.x != before.x)
			{
				scrollViewVelocity.x = 0f;
			}
			if (scrollPosition.y != before.y)
			{
				scrollViewVelocity.y = 0f;
			}
		}

		/// <summary>
		/// Keeps a scroll position in its list: from 0 to the end the engine's scroll view clamps it to, when the list's
		/// size is known (the same BeginScrollView call). The size is the larger of this pass's and the one the list was
		/// last drawn with: the finger's move comes in the frame's first pass, the layout, and some lists measure their
		/// contents only while drawing them. The debug actions (Dialog_Debug) count their options' height afresh in every
		/// layout pass, so there the list was as high as the window, and every move was clamped back to the top (the user,
		/// 2026-10-25: "开发者界面不能用手指滑动了").
		/// </summary>
		private static void Clamp(Rect outRect, ref Vector2 pos)
		{
			if (pos.x < 0f) pos.x = 0f;
			if (pos.y < 0f) pos.y = 0f;
			if (outRect == lastOutRect)
			{
				Vector2 view = lastViewRect.size;
				if (drawnViews.TryGetValue(outRect, out Vector2 drawnView))
				{
					view = Vector2.Max(view, drawnView);
				}
				pos.x = Mathf.Min(pos.x, Mathf.Max(0f, view.x - outRect.width));
				pos.y = Mathf.Min(pos.y, Mathf.Max(0f, view.y - outRect.height));
			}
		}

		/// <summary>
		/// Prefix of Widgets.BeginScrollView and DevGUI.BeginScrollView, and called by BeforeEngineScrollView for a scroll view
		/// begun with GUI.BeginScrollView straight away: remembers whether the press landed in a list with something to scroll.
		/// </summary>
		public static void BeforeScrollView(Rect outRect, Rect viewRect)
		{
			notedRect = outRect;
			notedBefore = true;
			lastOutRect = outRect;
			lastViewRect = viewRect;
			Event current = Event.current;
			if (current == null)
			{
				return;
			}
			bool down = viewRect.height > outRect.height + 1f, across = viewRect.width > outRect.width + 1f;
			bool scrolls = down || across;
			if (current.rawType == EventType.Repaint)
			{
				drawn.Add(scrolls && listTouch ? TapAssist.NoteList(outRect, down, across) : -1);
				if (drawnViews.Count > 256)
				{
					drawnViews.Clear();
				}
				drawnViews[outRect] = viewRect.size;
			}
			if (current.rawType != EventType.MouseDown)
			{
				return;
			}
			// Only the list the touch began in (Touch) keeps the press: a list that something drawn after it covers there (the
			// resource readout under the inspect pane's tabs) took the tab's press (a tester, 2026-10-24: "日志" did nothing).
			open.Add(scrolls && current.type == EventType.MouseDown && current.button == 0 && FingerDown && Mouse.IsOver(outRect) && Touch.BeganInList(outRect));
		}

		/// <summary>
		/// Postfix of the engine's GUI.EndScrollView (Widgets.EndScrollView's and DevGUI.EndScrollView's when it cannot be
		/// patched): a press in a scrolling list that nothing in the list used (a button would have) is used now, so that
		/// whatever comes after the list (the window's own dragging) does not take it.
		/// </summary>
		public static void AfterScrollView()
		{
			Event current = Event.current;
			if (current != null && current.rawType == EventType.Repaint && drawn.Count > 0)
			{
				TapAssist.EndList(drawn[drawn.Count - 1]);
				drawn.RemoveAt(drawn.Count - 1);
			}
			if (current == null || current.rawType != EventType.MouseDown || open.Count == 0)
			{
				return;
			}
			bool pressedHere = open[open.Count - 1];
			open.RemoveAt(open.Count - 1);
			if (pressedHere && current.type == EventType.MouseDown)
			{
				current.Use();
			}
		}

		/// <summary>
		/// Prefix of the engine's GUI.BeginScrollViewImpl (every GUI.BeginScrollView): a scroll view begun straight away is
		/// noted and follows the finger as the game's are. One that the game's BeginScrollView (or the developer tools') begins
		/// was noted and moved there just before, by the same rect, and is left alone.
		/// </summary>
		public static void BeforeEngineScrollView(Rect position, ref Vector2 scrollPosition, Rect viewRect)
		{
			bool noted = notedBefore && position == notedRect, scrolled = scrolledBefore && position == scrolledRect;
			if (!noted)
			{
				BeforeScrollView(position, viewRect);
			}
			if (!scrolled)
			{
				FingerScroll(position, ref scrollPosition);
			}
			// What the calls above marked is this scroll view: the next one is looked at afresh.
			notedBefore = scrolledBefore = false;
		}

		// ---- holds and tooltips ----

		/// <summary>The tooltips of what is under the finger show at once, without their usual delay.</summary>
		private static void ShowTipsNow()
		{
			foreach (object value in ActiveTips.Values)
			{
				if (value is ActiveTip tip)
				{
					tip.firstTriggerTime = Time.realtimeSinceStartup - tip.signal.delay - 1f;
				}
			}
		}

		/// <summary>
		/// Prefix of Root.OnGUI and of the engine's GUI.DispatchInputToWindows (which runs the window under the mouse
		/// first): the left button's events are swallowed around a hold on the interface.
		/// </summary>
		public static void BeforeGui()
		{
			Event current = Event.current;
			if (current != null && current.button == 0 && (current.type == EventType.MouseDown || current.type == EventType.MouseUp) && Time.frameCount <= suppressClickUntilFrame)
			{
				current.Use();
			}
		}

		/// <summary>Prefix of Widgets.ButtonInvisibleDraggable (checkboxes): it reads the mouse straight from Input, so a hold is kept from it here.</summary>
		public static bool BeforeDraggableButton(ref Widgets.DraggableResult __result)
		{
			if (Time.frameCount <= suppressClickUntilFrame)
			{
				__result = Widgets.DraggableResult.Idle;
				return false;
			}
			return true;
		}

		/// <summary>
		/// DragSelect puts a button of its own in place of the game's on the copy and paste icons (bills, storage, policies,
		/// schedules), the trade counts and the work tab's boxes, and it reads the mouse straight from Input as well: the
		/// release of a hold clicked it, so that a finger resting on a paste icon to read its tooltip pasted, and one on a
		/// work box outside a scrolling list changed the priority (the compatibility audit, 2026-10-30). The release of a
		/// hold that did not drag is no press for it now. Unlike the game's draggable button, which sees nothing of a hold,
		/// it keeps the rest: a finger that rests and then drags on sets the boxes it goes over, as a mouse pressed, held
		/// still and dragged does on the PC (and as it did here before; the UI smoke test, 2026-10-27). Nothing is done
		/// when the mod is not loaded.
		/// </summary>
		private static void KeepHoldFromDragSelect(Harmony harmony)
		{
			Type utility = AccessTools.TypeByName("DragSelect.DraggingUtility");
			if (utility == null)
			{
				return;
			}
			try
			{
				MethodInfo handle = AccessTools.Method(utility, "HandleButton");
				ParameterInfo[] parameters = handle?.GetParameters();
				if (handle == null || handle.ReturnType != typeof(bool) || parameters.Length == 0 || parameters[0].Name != "mouseButton"
					|| !Array.Exists(parameters, p => p.Name == "dragging" && p.ParameterType == typeof(bool).MakeByRefType()))
				{
					Console.WriteLine("[mobile] DragSelect: its button is not as expected; a hold may click it");
					return;
				}
				harmony.Patch(handle, prefix: new HarmonyMethod(typeof(Interface), nameof(BeforeDragSelectButton)));
				Console.WriteLine("[mobile] DragSelect: a hold clicks nothing");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] DragSelect: a hold may click its buttons: " + e.Message);
			}
		}

		/// <summary>Prefix of DragSelect's DraggingUtility.HandleButton (one mouse button of its draggable button): the release of a hold that did not drag is not a press.</summary>
		public static bool BeforeDragSelectButton(int mouseButton, ref bool dragging, ref bool __result)
		{
			if (mouseButton == 0 && !dragging && Time.frameCount <= suppressClickUntilFrame && Input.GetMouseButtonUp(0))
			{
				__result = false;
				return false;
			}
			return true;
		}

		/// <summary>
		/// Prefix of TooltipHandler.DoTooltipGUI. A tooltip lives only while its region is reported under the mouse on
		/// every repaint. With no finger down nothing should show (the engine's mouse stays where the last finger
		/// was), except what a hold showed, which is kept alive here until the next touch. A real mouse (InputMode)
		/// shows the tooltips under it as on a PC, unless a finger moved the pointer last.
		/// </summary>
		public static void BeforeTooltips()
		{
			Event current = Event.current;
			if (current == null || current.type != EventType.Repaint || FingerDown || ActiveTips.Count == 0 || (InputMode.Mouse && !Touch.PointerFromFinger))
			{
				return;
			}
			if (!keepTips)
			{
				ActiveTips.Clear();
				return;
			}
			int frame = (int)TipFrame.GetValue(null);
			foreach (object value in ActiveTips.Values)
			{
				if (value is ActiveTip tip)
				{
					tip.lastTriggerFrame = frame;
				}
			}
		}
	}
}
