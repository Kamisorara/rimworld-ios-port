using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Binding a key without a keyboard. The game's Dialog_DefineBinding (Options → Controls, a click on a binding) waits
	/// for a key press, which a phone cannot give: a binding that comes without a default key (many mods' menus: a
	/// tester's "薄荷" menus) could not be bound, and so not be put on the key wheel either. The dialog now shows the
	/// keys the wheel can press (KeyWheel.GlfwKey), and a tap on one does what a key press does there: the bindings that
	/// conflict with it are cleared (the game's message says which), the key is set. A key that other bindings already
	/// use shows them when held; one whose bindings would be cleared is tinted. A key pressed on a real keyboard still
	/// works. Opened from the key wheel (a key without a binding tapped), the choice is saved at once; from Options it is
	/// saved when the controls window is closed, as before.
	/// </summary>
	internal static class BindKeys
	{
		private static readonly AccessTools.FieldRef<Dialog_DefineBinding, KeyPrefsData> PrefsOf = AccessTools.FieldRefAccess<Dialog_DefineBinding, KeyPrefsData>("keyPrefsData");
		private static readonly AccessTools.FieldRef<Dialog_DefineBinding, KeyBindingDef> DefOf = AccessTools.FieldRefAccess<Dialog_DefineBinding, KeyBindingDef>("keyDef");
		private static readonly AccessTools.FieldRef<Dialog_DefineBinding, KeyPrefs.BindingSlot> SlotOf = AccessTools.FieldRefAccess<Dialog_DefineBinding, KeyPrefs.BindingSlot>("slot");
		private static readonly AccessTools.FieldRef<Dialog_DefineBinding, Vector2> SizeOf = AccessTools.FieldRefAccess<Dialog_DefineBinding, Vector2>("windowSize");

		private static readonly KeyCode[][] Rows =
		{
			new[] { KeyCode.BackQuote, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0, KeyCode.Minus, KeyCode.Equals },
			new[] { KeyCode.Q, KeyCode.W, KeyCode.E, KeyCode.R, KeyCode.T, KeyCode.Y, KeyCode.U, KeyCode.I, KeyCode.O, KeyCode.P, KeyCode.LeftBracket, KeyCode.RightBracket, KeyCode.Backslash },
			new[] { KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.F, KeyCode.G, KeyCode.H, KeyCode.J, KeyCode.K, KeyCode.L, KeyCode.Semicolon, KeyCode.Quote },
			new[] { KeyCode.Z, KeyCode.X, KeyCode.C, KeyCode.V, KeyCode.B, KeyCode.N, KeyCode.M, KeyCode.Comma, KeyCode.Period, KeyCode.Slash },
			new[] { KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12 },
			new[] { KeyCode.Tab, KeyCode.Space, KeyCode.Insert, KeyCode.Delete, KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown, KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow },
			new[] { KeyCode.Keypad0, KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5, KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9, KeyCode.KeypadPlus, KeyCode.KeypadMinus, KeyCode.KeypadMultiply },
		};

		private const int Columns = 13;
		private static System.Reflection.MethodInfo conflicting;

		public static void Start(Harmony harmony)
		{
			try
			{
				conflicting = AccessTools.Method(typeof(KeyPrefsData), "ConflictingBindings");
				harmony.Patch(AccessTools.Constructor(typeof(Dialog_DefineBinding), new[] { typeof(KeyPrefsData), typeof(KeyBindingDef), typeof(KeyPrefs.BindingSlot) }), postfix: new HarmonyMethod(typeof(BindKeys), nameof(AfterConstructed)));
				harmony.Patch(AccessTools.Method(typeof(Dialog_DefineBinding), nameof(Dialog_DefineBinding.DoWindowContents)), prefix: new HarmonyMethod(typeof(BindKeys), nameof(BeforeContents)));
				Console.WriteLine("[mobile] bind keys: the key binding dialog shows the keys to tap");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] bind keys: not installed: " + e.Message);
			}
		}

		public static void AfterConstructed(Dialog_DefineBinding __instance)
		{
			// With a keyboard (InputMode) the game's own dialog: a key press is the answer.
			if (InputMode.Keyboard)
			{
				return;
			}
			SizeOf(__instance) = new Vector2(900f, 470f);
		}

		/// <summary>Instead of the dialog's own contents (whose key handling is kept below).</summary>
		public static bool BeforeContents(Dialog_DefineBinding __instance, Rect inRect)
		{
			if (__instance.GetType() != typeof(Dialog_DefineBinding) || SizeOf(__instance).x < 900f)
			{
				return true;
			}
			try
			{
				Draw(__instance, inRect.ContractedBy(14f));
			}
			catch (Exception e)
			{
				Log.ErrorOnce("[Port17] key binding dialog: " + e, 0x5017b1d);
				return true;
			}
			return false;
		}

		private static void Draw(Dialog_DefineBinding dialog, Rect rect)
		{
			KeyPrefsData prefs = PrefsOf(dialog);
			KeyBindingDef def = DefOf(dialog);
			KeyPrefs.BindingSlot slot = SlotOf(dialog);
			bool chinese = Texts.Chinese;
			Text.Font = GameFont.Small;
			KeyCode now = prefs.GetBoundKeyCode(def, slot);
			string title = chinese
				? "给“" + def.LabelCap + "”选" + (slot == KeyPrefs.BindingSlot.A ? "第一个" : "第二个") + "键（现在：" + (now == KeyCode.None ? "未绑定" : Label(now)) + "）"
				: "A key for \"" + def.LabelCap + "\" (" + (slot == KeyPrefs.BindingSlot.A ? "first" : "second") + "; now: " + (now == KeyCode.None ? "none" : Label(now)) + ")";
			Widgets.Label(new Rect(rect.x, rect.y, rect.width, 28f), title);
			GUI.color = Color.gray;
			Widgets.Label(new Rect(rect.x, rect.y + 26f, rect.width, 24f), chinese ? "点一个键（有实体键盘也可以直接按）。亮的键没人用；灰的已有别的用途，但不冲突；橙色的会顶掉别的绑定。按住键看它用在哪。" : "Tap a key (or press one on a keyboard). Bright: unused; grey: used elsewhere, no conflict; orange: replaces another binding. Hold a key to see where it is used.");
			GUI.color = Color.white;

			float gap = 4f;
			float width = (rect.width - gap * (Columns - 1)) / Columns;
			float height = 36f;
			float y = rect.y + 58f;
			foreach (KeyCode[] row in Rows)
			{
				float x = rect.x;
				foreach (KeyCode key in row)
				{
					var button = new Rect(x, y, width, height);
					Usage usage = UsageOf(dialog, prefs, def, key);
					List<string> replaced = usage.Replaced, users = usage.Users;
					if (key == now)
					{
						Widgets.DrawBoxSolid(button, new Color(0.25f, 0.45f, 0.25f));
					}
					GUI.color = replaced.Count > 0 ? new Color(1f, 0.65f, 0.3f) : users.Count > 0 ? new Color(0.55f, 0.55f, 0.55f) : Color.white;
					bool tapped = Widgets.ButtonText(button, Label(key));
					GUI.color = Color.white;
					if (users.Count > 0)
					{
						TooltipHandler.TipRegion(button, (chinese ? "已用于：" : "Used by: ") + string.Join("、", users) + (replaced.Count > 0 ? (chinese ? "\n选它会清掉：" : "\nChoosing it clears: ") + string.Join("、", replaced) : ""));
					}
					if (tapped)
					{
						Choose(dialog, prefs, def, slot, key);
						return;
					}
					x += width + gap;
				}
				y += height + gap;
			}

			float bottom = rect.yMax - 38f;
			if (Widgets.ButtonText(new Rect(rect.x, bottom, 160f, 38f), chinese ? "取消" : "Cancel"))
			{
				dialog.Close();
				return;
			}
			if (now != KeyCode.None && Widgets.ButtonText(new Rect(rect.x + 172f, bottom, 160f, 38f), chinese ? "清除这个键" : "Clear"))
			{
				prefs.SetBinding(def, slot, KeyCode.None);
				Saved(prefs);
				dialog.Close();
				return;
			}

			// A real keyboard, as the game's own dialog takes it.
			if (Event.current.isKey && Event.current.type == EventType.KeyDown && Event.current.keyCode != KeyCode.None)
			{
				if (Event.current.keyCode == KeyCode.Escape)
				{
					dialog.Close();
				}
				else
				{
					Choose(dialog, prefs, def, slot, Event.current.keyCode);
				}
				Event.current.Use();
			}
		}

		private static void Choose(Dialog_DefineBinding dialog, KeyPrefsData prefs, KeyBindingDef def, KeyPrefs.BindingSlot slot, KeyCode key)
		{
			prefs.EraseConflictingBindingsForKeyCode(def, key, old => Messages.Message("KeyBindingOverwritten".Translate(old.LabelCap), MessageTypeDefOf.TaskCompletion, historical: false));
			prefs.SetBinding(def, slot, key);
			Saved(prefs);
			dialog.Close();
		}

		/// <summary>The game's live bindings (the dialog opened from the key wheel) are saved now; a copy (Options) is saved by its window.</summary>
		private static void Saved(KeyPrefsData prefs)
		{
			if (prefs == KeyPrefs.KeyPrefsData)
			{
				KeyPrefs.Save();
			}
		}

		private sealed class Usage
		{
			public List<string> Users, Replaced;
		}

		// Who uses each key, worked out once for the dialog that is open (the bindings do not change while it is).
		private static Dialog_DefineBinding usageFor;
		private static readonly Dictionary<KeyCode, Usage> usages = new Dictionary<KeyCode, Usage>();

		private static Usage UsageOf(Dialog_DefineBinding dialog, KeyPrefsData prefs, KeyBindingDef def, KeyCode key)
		{
			if (usageFor != dialog)
			{
				usageFor = dialog;
				usages.Clear();
			}
			if (!usages.TryGetValue(key, out Usage usage))
			{
				usage = new Usage
				{
					Users = DefDatabase<KeyBindingDef>.AllDefsListForReading.Where(d => d != def && (prefs.GetBoundKeyCode(d, KeyPrefs.BindingSlot.A) == key || prefs.GetBoundKeyCode(d, KeyPrefs.BindingSlot.B) == key)).Select(d => d.LabelCap.ToString()).ToList(),
					Replaced = new List<string>(),
				};
				try
				{
					if (conflicting != null)
					{
						usage.Replaced = ((IEnumerable<KeyBindingDef>)conflicting.Invoke(prefs, new object[] { def, key })).Select(d => d.LabelCap.ToString()).ToList();
					}
				}
				catch (Exception)
				{
				}
				usages[key] = usage;
			}
			return usage;
		}

		private static string Label(KeyCode key)
		{
			switch (key)
			{
				case KeyCode.BackQuote: return "`";
				case KeyCode.Minus: return "-";
				case KeyCode.Equals: return "=";
				case KeyCode.LeftBracket: return "[";
				case KeyCode.RightBracket: return "]";
				case KeyCode.Backslash: return "\\";
				case KeyCode.Semicolon: return ";";
				case KeyCode.Quote: return "'";
				case KeyCode.Comma: return ",";
				case KeyCode.Period: return ".";
				case KeyCode.Slash: return "/";
				case KeyCode.UpArrow: return "↑";
				case KeyCode.DownArrow: return "↓";
				case KeyCode.LeftArrow: return "←";
				case KeyCode.RightArrow: return "→";
				case KeyCode.KeypadPlus: return "Num+";
				case KeyCode.KeypadMinus: return "Num-";
				case KeyCode.KeypadMultiply: return "Num*";
				default:
					if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
					{
						return ((int)key - (int)KeyCode.Alpha0).ToString();
					}
					if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
					{
						return "Num" + ((int)key - (int)KeyCode.Keypad0);
					}
					return key.ToStringReadable();
			}
		}

		/// <summary>From the key wheel: a key without a binding tapped; the dialog for its first key, on the live bindings.</summary>
		public static void Ask(KeyBindingDef def)
		{
			if (!Find.WindowStack.IsOpen<Dialog_DefineBinding>())
			{
				Find.WindowStack.Add(new Dialog_DefineBinding(KeyPrefs.KeyPrefsData, def, KeyPrefs.BindingSlot.A));
			}
		}
	}
}
