using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The player's words on a moment marked with "记录问题" (the user, 2026-11-03: "在点击记录问题之后，弹出一个文本记录框，测试者
	/// 可以就当下发现的问题添加文字描述，方便后期我们进行bug修复"): the mark itself (Recorder) is a screenshot and the game's state,
	/// which do not say what the player saw go wrong. The box opens once the mark is taken (so that it is not in the
	/// screenshot), at the top of the screen, where the soft keyboard leaves it in sight (it comes up at once: the field has
	/// the focus); the game waits meanwhile. "保存说明" puts the words first into the mark's text and into the record
	/// (Recorder.Note), "不写了" leaves the mark as it was; whatever else closes the box (the back key, the box of another
	/// mark) keeps what was written.
	/// </summary>
	internal sealed class Dialog_ProblemNote : Window
	{
		private const string FieldName = "ProblemNoteField";
		private const int MaxLength = 1000;
		private readonly string mark;
		private string text = "";
		private bool focused, done;
		private Vector2 scroll;
		private int shownLength;

		public Dialog_ProblemNote(string mark)
		{
			this.mark = mark;
			// A tap beside it must not throw the words away.
			closeOnClickedOutside = false;
			absorbInputAroundWindow = true;
			forcePause = true;
			// A keyboard's Enter is a new line in the box.
			closeOnAccept = false;
		}

		public override Vector2 InitialSize => new Vector2(Mathf.Min(780f, UI.screenWidth - 24f), 300f);

		protected override void SetInitialSizeAndPosition()
		{
			// At the top: the soft keyboard covers the lower half of the screen.
			Vector2 size = InitialSize;
			windowRect = new Rect((UI.screenWidth - size.x) / 2f, 12f, size.x, size.y).Rounded();
		}

		public override void DoWindowContents(Rect inRect)
		{
			bool chinese = Texts.Chinese;
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), chinese ? "记录问题：写几句说明" : "Record a problem: a few words");
			Text.Font = GameFont.Small;
			GUI.color = new Color(0.75f, 0.75f, 0.75f);
			Widgets.Label(new Rect(inRect.x, inRect.y + 36f, inRect.width, 46f), chinese
				? "截图和运行数据已存好。写写刚才发生了什么、本来应该怎样、之前做了什么。不想写就点“不写了”。"
				: "The screenshot and the game's state are saved. In a few words: what happened, what you expected, what you did just before. Nothing to add: tap Skip.");
			GUI.color = Color.white;

			// The box: a text area as high as its text, in a list that scrolls (a finger drags it as any list).
			float buttonsY = inRect.yMax - 36f;
			var box = new Rect(inRect.x, inRect.y + 86f, inRect.width, buttonsY - 8f - (inRect.y + 86f));
			float width = box.width - 16f;
			float height = Mathf.Max(box.height, Text.CalcHeight(text, width - 12f) + 16f);
			if (text.Length != shownLength)
			{
				if (text.Length > shownLength)
				{
					// Typing: the end stays in sight.
					scroll.y = height - box.height;
				}
				shownLength = text.Length;
			}
			Widgets.BeginScrollView(box, ref scroll, new Rect(0f, 0f, width, height));
			GUI.SetNextControlName(FieldName);
			string typed = Widgets.TextArea(new Rect(0f, 0f, width, height), text) ?? "";
			Widgets.EndScrollView();
			text = typed.Length > MaxLength ? typed.Substring(0, MaxLength) : typed;
			if (!focused)
			{
				focused = true;
				GUI.FocusControl(FieldName);
			}

			float bw = 160f;
			if (Widgets.ButtonText(new Rect(inRect.x, buttonsY, bw, 36f), chinese ? "不写了" : "Skip"))
			{
				done = true;
				Close();
				Recorder.Recorded(false);
			}
			Text.Font = GameFont.Tiny;
			GUI.color = new Color(0.6f, 0.6f, 0.6f);
			Text.Anchor = TextAnchor.MiddleCenter;
			Widgets.Label(new Rect(inRect.x + bw, buttonsY, inRect.width - 2f * bw, 36f), text.Length + " / " + MaxLength);
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
			Text.Font = GameFont.Small;
			if (Widgets.ButtonText(new Rect(inRect.xMax - bw, buttonsY, bw, 36f), chinese ? "保存说明" : "Save"))
			{
				Save();
			}
		}

		/// <summary>The back key (the game's Esc): what was written is kept.</summary>
		public override void OnCancelKeyPressed()
		{
			Save();
			Event.current.Use();
		}

		public override void PostClose()
		{
			base.PostClose();
			if (!done)
			{
				// Closed by something else (the box of a newer mark): what was written is kept.
				done = true;
				Recorder.Note(mark, text);
			}
		}

		private void Save()
		{
			done = true;
			Recorder.Note(mark, text);
			Close();
			Recorder.Recorded(text.Trim().Length > 0);
		}
	}
}
