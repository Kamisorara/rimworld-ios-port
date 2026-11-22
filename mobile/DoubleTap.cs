using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using Waylith.Input;

namespace PortMobile
{
	/// <summary>
	/// A double tap for a finger. The engine counts a press of a mouse button as the second click of a double click when
	/// it comes within 0.3 s of the one before and less than 6 pixels from it (Waylith.Input.InputState.OnMouseButton):
	/// a mouse's distances, about half a millimetre on a phone, which a fingertip lifted and put down again hardly ever
	/// hits (testers, 2026-10-20: a double tap, which selects every thing of the kind tapped on the screen, was hard to
	/// get). For the left button the count is made again after the engine's, with 0.5 s and 36 dp (about 6 mm). mobile.txt:
	/// doubleTapSeconds=, doubleTapDp=; doubleTapDp=0 leaves the engine's count alone.
	/// </summary>
	internal static class DoubleTap
	{
		[DllImport("glfw")] private static extern float glfwAndroidGetDensity();

		private static bool[] buttonDown;
		private static int[] consecutiveClicks;
		private static List<InputEvent> eventsThisFrame;
		private static readonly Stopwatch clock = Stopwatch.StartNew();
		private static double window = 0.5, lastTime = -10.0;
		private static float slop;
		private static System.Numerics.Vector2 lastPos;
		private static int lastCount;

		public static void Start(Harmony harmony)
		{
			try
			{
				var inv = System.Globalization.CultureInfo.InvariantCulture;
				float dp = 36f;
				if (float.TryParse(Settings.Get("doubleTapDp", null), System.Globalization.NumberStyles.Float, inv, out float d))
				{
					dp = d;
				}
				if (double.TryParse(Settings.Get("doubleTapSeconds", null), System.Globalization.NumberStyles.Float, inv, out double s) && s > 0.0)
				{
					window = s;
				}
				if (dp <= 0f)
				{
					return;
				}
				slop = dp * Math.Max(1f, glfwAndroidGetDensity());
				Type state = typeof(InputState);
				buttonDown = (bool[])AccessTools.Field(state, "buttonDown").GetValue(null);
				consecutiveClicks = (int[])AccessTools.Field(state, "consecutiveClicks").GetValue(null);
				eventsThisFrame = (List<InputEvent>)AccessTools.Field(state, "eventsThisFrame").GetValue(null);
				harmony.Patch(AccessTools.Method(state, "OnMouseButton"), prefix: new HarmonyMethod(typeof(DoubleTap), nameof(Before)), postfix: new HarmonyMethod(typeof(DoubleTap), nameof(After)));
				underMouse = AccessTools.MethodDelegate<Func<RimWorld.Selector, IEnumerable<object>>>(AccessTools.Method(typeof(RimWorld.Selector), "SelectableObjectsUnderMouse"));
				harmony.Patch(AccessTools.Method(typeof(RimWorld.Selector), "HandleMapClicks"), prefix: new HarmonyMethod(typeof(DoubleTap), nameof(BeforeMapClick)));
				Console.WriteLine("[mobile] double tap: within " + window.ToString("F2", inv) + " s and " + slop.ToString("F0", inv) + " px");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] double tap: not patched: " + e.Message);
			}
		}

		/// <summary>Whether the engine takes this event (a change of the button), as it decides itself.</summary>
		public static void Before(MouseButton button, bool pressed, ref bool __state)
		{
			__state = (uint)button < (uint)buttonDown.Length && buttonDown[(int)button] != pressed;
		}

		/// <summary>
		/// The touch of the last press dragged or became two fingers: not a tap, so the next press starts a new count. (A
		/// quick second swipe from the same place was a double click: the game neither started its selection box, so the
		/// finger did not move the map, nor kept from selecting everything of the kind under it.)
		/// </summary>
		public static void Forget()
		{
			lastTime = -10.0;
			lastCount = 0;
		}

		public static void After(MouseButton button, bool pressed, System.Numerics.Vector2 position, bool __state)
		{
			// A real mouse's press (no touch going on) keeps the engine's count.
			if (!__state || !pressed || button != MouseButton.Left || eventsThisFrame.Count == 0 || !Touch.TouchInProgress)
			{
				return;
			}
			double now = clock.Elapsed.TotalSeconds;
			int count = now - lastTime < window && System.Numerics.Vector2.Distance(position, lastPos) < slop ? lastCount + 1 : 1;
			presses++;
			lastTime = now;
			lastPos = position;
			lastCount = count;
			InputEvent added = eventsThisFrame[eventsThisFrame.Count - 1];
			if (added.mouseButton == MouseButton.Left && added.clickCount != count)
			{
				added.clickCount = count;
				consecutiveClicks[(int)MouseButton.Left] = count;
			}
		}

		private static Func<RimWorld.Selector, IEnumerable<object>> underMouse;
		// Presses counted above, the one of the last press on the map, and what was under it.
		private static int presses, mapPress = -1;
		private static object mapTarget;

		/// <summary>
		/// Prefix of Selector.HandleMapClicks: a second tap is the map's double tap (everything of the kind on the screen
		/// selected) only when the tap before it was on the map, on the same thing. Within the wider distance above, a tap on
		/// the ground (to clear the selection) and a quick tap on a colonist next to it came as a double tap, and every
		/// colonist was selected (the user, 2026-10-26: "点击空地取消选择，再点击小人选择单个小人……一下选中所有小人"). Such a
		/// press is the game's single click instead, and the next tap may make a double tap with it.
		/// </summary>
		public static void BeforeMapClick(RimWorld.Selector __instance)
		{
			UnityEngine.Event current = UnityEngine.Event.current;
			if (current == null || current.type != UnityEngine.EventType.MouseDown || current.button != 0 || !Touch.TouchInProgress)
			{
				return;
			}
			object target = Target(__instance);
			if (current.clickCount == 2 && (mapPress != presses - 1 || !Equals(target, mapTarget)))
			{
				current.clickCount = 1;
				lastCount = 1;
				if (Touch.Verbose) Console.WriteLine("[mobile] double tap: the tap before was not on " + (target ?? "the ground") + "; a single tap");
			}
			mapPress = presses;
			mapTarget = target;
		}

		/// <summary>What a double tap here would select the kind of (as Selector.SelectAllMatchingObjectUnderMouseOnScreen picks it); null for the ground.</summary>
		private static object Target(RimWorld.Selector selector)
		{
			try
			{
				object first = null, pawn = null;
				foreach (object o in underMouse(selector))
				{
					if (o is Verse.Pawn p && p.Faction == RimWorld.Faction.OfPlayer && !p.IsPrisoner)
					{
						return o;
					}
					first = first ?? o;
					pawn = pawn ?? (o is Verse.Pawn ? o : null);
				}
				return pawn ?? first;
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
