using System;
using System.Reflection;
using Brutal.Numerics;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// No gaps between the nine pieces a button's or a window's background is drawn from.
	///
	/// The engine's interface backend puts every textured rectangle on whole pixels by rounding its origin and its size
	/// separately (ImGuiGuiBackend.AddImageRect: the size of a picture then never changes with its position). Two
	/// rectangles that share an edge can so end up a pixel apart: the left one ends at round(origin) + round(width),
	/// the right one starts at round(origin + width). The game itself aligns the pieces to the pixel grid of its
	/// interface scale, which is enough where that scale times the interface's size is the screen's size exactly; here
	/// the interface is 1498 x 688 for a screen of 2772 x 1272 (1.85 would give 1498.4 x 687.6), so the true factor is
	/// 1.8505 across and 1.8488 down, and the grid the game aligns to drifts off the screen's by a pixel every 4 000.
	/// Seen as a dark vertical line a few pixels inside one end of some buttons (the mod page's "Delete mods..." and
	/// "Save and apply"), where the background shows through between the end piece and the middle piece.
	/// While the game draws such a background (Widgets.DrawAtlas, DrawAtlasWithMaterial, and the engine's own
	/// GUIStyle backgrounds), each rectangle's two corners are rounded each by its own position instead, and an edge
	/// that was already rounded within the same background gets the same answer again. Everything else is drawn as
	/// the engine draws it. mobile.txt: atlasSeams=0 leaves this out.
	/// </summary>
	internal static class AtlasSeams
	{
		private static int inside;
		private static readonly float[] seenX = new float[8], roundedX = new float[8], seenY = new float[8], roundedY = new float[8];
		private static int countX, countY;

		public static void Start(Harmony harmony)
		{
			if (Settings.Get("atlasSeams", 1) == 0)
			{
				return;
			}
			try
			{
				Type backend = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImGuiGuiBackend");
				MethodInfo addImageRect = backend != null ? AccessTools.Method(backend, "AddImageRect") : null;
				if (addImageRect == null)
				{
					Console.WriteLine("[mobile] atlas seams: the engine's interface backend is not as expected, left alone");
					return;
				}
				HarmonyMethod begin = new HarmonyMethod(typeof(AtlasSeams), nameof(Begin)), end = new HarmonyMethod(typeof(AtlasSeams), nameof(End));
				int backgrounds = 0;
				foreach (Func<MethodInfo> find in new Func<MethodInfo>[]
				{
					() => AccessTools.Method(typeof(Widgets), nameof(Widgets.DrawAtlas), new[] { typeof(Rect), typeof(Texture2D), typeof(bool) }),
					() => AccessTools.Method(typeof(Widgets), nameof(Widgets.DrawAtlasWithMaterial)),
					() => AccessTools.Method(typeof(GUIStyle), "DrawBackground"),
				})
				{
					try
					{
						MethodInfo method = find();
						if (method != null)
						{
							harmony.Patch(method, prefix: begin, finalizer: end);
							backgrounds++;
						}
					}
					catch (Exception e)
					{
						Console.WriteLine("[mobile] atlas seams: one kind of background left as it is: " + e.Message);
					}
				}
				harmony.Patch(addImageRect, prefix: new HarmonyMethod(typeof(AtlasSeams), nameof(BeforeAddImageRect)));
				Console.WriteLine("[mobile] atlas seams: the pieces of a background share their edges (" + backgrounds + " kinds of background)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] atlas seams not installed: " + e.Message);
			}
		}

		public static void Begin()
		{
			if (inside++ == 0)
			{
				countX = countY = 0;
			}
		}

		public static void End()
		{
			if (inside > 0)
			{
				inside--;
			}
		}

		// The whole pixel an edge at this screen coordinate goes to: rounded as the engine rounds an origin, and the same
		// as any edge of this background that lies within a fiftieth of a pixel (the same edge, reached by another sum).
		private static float Edge(float at, float[] seen, float[] rounded, ref int count)
		{
			for (int i = 0; i < count; i++)
			{
				float apart = seen[i] - at;
				if (apart < 0.02f && apart > -0.02f)
				{
					return rounded[i];
				}
			}
			float whole = MathF.Floor(at + 0.51f);
			if (count < seen.Length)
			{
				seen[count] = at;
				rounded[count] = whole;
				count++;
			}
			return whole;
		}

		/// <summary>
		/// Prefix of ImGuiGuiBackend.AddImageRect. Inside a background: the rectangle is replaced by the one whose origin
		/// and size the engine's own rounding turns into exactly these two corners; a piece that comes out empty is not drawn.
		/// </summary>
		public static bool BeforeAddImageRect(ref Rect dst, float ___m00, float ___m11, bool ___rotated, float2 ___translate, float2 ___offset)
		{
			if (inside == 0 || ___rotated || ___m00 <= 0f || ___m11 <= 0f)
			{
				return true;
			}
			// The game makes the corners at least as large as half the background, rounded up to its pixel grid: the pieces
			// between them then have no size, or less than none. The engine would turn such a piece around and draw it one
			// pixel wide; between corners that meet there is nothing for it to fill.
			if (dst.width <= 0f || dst.height <= 0f)
			{
				return false;
			}
			float left = Edge(___m00 * (___offset.X + dst.x) + ___translate.X, seenX, roundedX, ref countX);
			float right = Edge(___m00 * (___offset.X + dst.x + dst.width) + ___translate.X, seenX, roundedX, ref countX);
			float top = Edge(___m11 * (___offset.Y + dst.y) + ___translate.Y, seenY, roundedY, ref countY);
			float bottom = Edge(___m11 * (___offset.Y + dst.y + dst.height) + ___translate.Y, seenY, roundedY, ref countY);
			if (right <= left || bottom <= top)
			{
				return false;
			}
			dst.x = (left - ___translate.X) / ___m00 - ___offset.X;
			dst.y = (top - ___translate.Y) / ___m11 - ___offset.Y;
			dst.width = (right - left) / ___m00;
			dst.height = (bottom - top) / ___m11;
			return true;
		}
	}
}
