using HarmonyLib;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// What is being typed, shown at the top of the screen while the soft keyboard covers the text field (testers,
	/// 2026-10-22: a search box at the bottom right lay under the keyboard, and what was typed could not be seen). Only
	/// for a field in the lower part of the screen, where the keyboard is; a field higher up shows itself.
	/// </summary>
	internal static class TypingBar
	{
		private const int WindowId = 0x50547962;
		// A field whose lower edge is below this part of the screen height is taken to be under the keyboard.
		private const float CoveredFrom = 0.4f;

		private static Rect fieldOnScreen;
		private static int fieldFrame = -1;
		private static string text = "";
		private static int caret;
		// The control id of the field being drawn (-1: not known yet in this call; 0: the engine does not tell).
		private static int drawnId;
		private static bool idsKnown;

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(GUI), "TrackControlWindow"), prefix: new HarmonyMethod(typeof(TypingBar), nameof(NoteId)));
				idsKnown = true;
			}
			catch (System.Exception e)
			{
				System.Console.WriteLine("[mobile] typing bar: field ids not known (" + e.Message + ")");
			}
			harmony.Patch(AccessTools.Method(typeof(GUI), "TextFieldImpl"), prefix: new HarmonyMethod(typeof(TypingBar), nameof(BeforeTextField)), postfix: new HarmonyMethod(typeof(TypingBar), nameof(AfterTextField)));
		}

		public static void BeforeTextField()
		{
			drawnId = -1;
		}

		/// <summary>Prefix of the engine's GUI.TrackControlWindow: a text field calls it first with its own id.</summary>
		public static void NoteId(int id)
		{
			if (drawnId == -1)
			{
				drawnId = id;
			}
		}

		/// <summary>Postfix of the engine's GUI.TextFieldImpl: the field with the focus, where it is and what it holds.</summary>
		public static void AfterTextField(Rect rect, string __result)
		{
			int focus = GUIUtility.keyboardControl;
			Event current = Event.current;
			if (focus == 0 || current == null || current.type != EventType.Repaint)
			{
				return;
			}
			// The rectangle alone does not tell the field: the rows of a list are drawn in groups of their own, and the
			// count fields of the trade dialog all had one rectangle; the bar showed the last row's 0 whatever was typed
			// (the user, 2026-10-26: "用小键盘填写数字好像不生效").
			if (idsKnown && drawnId != focus)
			{
				return;
			}
			if (!(GUIUtility.GetStateObject(typeof(TextEditor), focus) is TextEditor editor) || editor.position != rect || !TapAssist.ScreenRect(rect, out fieldOnScreen))
			{
				return;
			}
			fieldFrame = Time.frameCount;
			text = __result ?? "";
			caret = Mathf.Clamp(editor.cursorIndex, 0, text.Length);
		}

		/// <summary>Whether a finger at this point (window pixels) is on the bar.</summary>
		public static bool At(Vector2 pixels)
		{
			if (Current.Root == null || Find.UIRoot == null || Find.WindowStack == null)
			{
				return false;
			}
			return Find.WindowStack.GetWindowAt(pixels / Prefs.UIScale) is ImmediateWindow window && window.ID == -WindowId;
		}

		/// <summary>Once a frame, after the game's interface (Entry): the bar, while the keyboard covers the field.</summary>
		public static void OnGui()
		{
			if (!Interface.KeyboardShown || InputMode.Keyboard || GUIUtility.keyboardControl == 0 || Time.frameCount - fieldFrame > 3 || fieldOnScreen.yMax < Screen.height * CoveredFrom)
			{
				return;
			}
			float width = Mathf.Min(UI.screenWidth * 0.6f, 760f);
			var rect = new Rect((UI.screenWidth - width) / 2f, 10f, width, 46f);
			Find.WindowStack.ImmediateWindow(WindowId, rect, WindowLayer.Super, () => Draw(rect.AtZero()), doBackground: true, absorbInputAroundWindow: false, shadowAlpha: 1f);
		}

		private static void Draw(Rect inRect)
		{
			Text.Font = GameFont.Small;
			Text.Anchor = TextAnchor.MiddleLeft;
			Rect label = inRect.ContractedBy(12f, 0f);
			if (text.Length == 0)
			{
				GUI.color = Color.gray;
				Widgets.Label(label, Texts.Chinese ? "正在输入…" : "Typing…");
				GUI.color = Color.white;
			}
			else
			{
				// The caret's end stays in sight: a long text loses its beginning
				string shown = text.Substring(0, caret) + "|" + text.Substring(caret);
				bool cut = false;
				while (shown.Length > 1 && Text.CalcSize((cut ? "…" : "") + shown).x > label.width)
				{
					shown = shown.Substring(1);
					cut = true;
				}
				Widgets.Label(label, (cut ? "…" : "") + shown);
			}
			Text.Anchor = TextAnchor.UpperLeft;
		}
	}
}
