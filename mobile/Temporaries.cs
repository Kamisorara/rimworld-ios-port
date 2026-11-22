using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Verse;
using Waylith.Graphics;

namespace PortMobile
{
	/// <summary>
	/// Temporary render textures given back by the dozen in one frame are let go before the frame ends.
	///
	/// Unity keeps the textures given back with RenderTexture.ReleaseTemporary in a pool and hands them out again at once.
	/// The engine makes a new texture for every GetTemporary and lets go of the given-back ones only when the next frame
	/// starts (FrameStart.Run), so that whatever the frame still draws with them is drawn. A mod that works through a
	/// list in one frame keeps them all: Show Me Your Hands, on the main menu, blits every weapon's texture into a temporary
	/// one to read its pixels; with 352 mods (a vivo with the Snapdragon 8 Elite) that was 645 textures and 1.3 GB, on a
	/// phone where the graphics memory is the system's memory and 1 GB was left. The system pushed 2 to 4 GB of the game
	/// into swap for it, and everything that touched those pages afterwards waited (the trim after the load: 11 s).
	/// Here, when more than "keepTemporaries" (32) given-back textures wait for the next frame, the oldest go now. What a
	/// frame draws with a temporary texture it gave back is drawn with one of the last 32; nothing in the game or the mods
	/// seen so far draws with more. The ones let go wait for the graphics card like any other, and once 256 MiB of
	/// those wait the engine itself waits for the card and frees them. Measured on the OnePlus (tempTest=600,
	/// tempTestSize=128): the render targets grew by 802 MiB (615 textures) in that frame as the engine is, 123 MiB with
	/// this, in about the same time (4.2 / 4.0 s).
	/// mobile.txt: keepTemporaries=0 leaves the engine as it is; tempTest=N makes N temporaries in one frame at the main menu
	/// (tempTestSize=S: sides S, 2S, 4S and 8S, 64 by default).
	/// </summary>
	internal static class Temporaries
	{
		private static List<RenderTexture> released;
		private static int keep = 32;
		private static int letGo;
		private static bool told;

		public static void Start(Harmony harmony)
		{
			keep = Settings.Get("keepTemporaries", 32);
			if (keep <= 0)
			{
				return;
			}
			try
			{
				released = AccessTools.Field(typeof(RenderTexture), "_releasedTemporaries")?.GetValue(null) as List<RenderTexture>;
				if (released == null)
				{
					Console.WriteLine("[mobile] temporaries: the engine's list of given-back render textures is not there, left alone");
					return;
				}
				harmony.Patch(AccessTools.Method(typeof(RenderTexture), nameof(RenderTexture.ReleaseTemporary)), postfix: new HarmonyMethod(typeof(Temporaries), nameof(AfterRelease)));
				Console.WriteLine("[mobile] temporaries: at most " + keep + " given-back render textures wait for the next frame");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] temporaries not installed: " + e.Message);
			}
		}

		/// <summary>Postfix of RenderTexture.ReleaseTemporary: the engine has put the texture on its list.</summary>
		public static void AfterRelease()
		{
			if (released.Count <= keep)
			{
				return;
			}
			int over = released.Count - keep;
			for (int i = 0; i < over; i++)
			{
				released[i].Release();
			}
			released.RemoveRange(0, over);
			letGo += over;
			if (!told)
			{
				told = true;
				Console.WriteLine("[mobile] temporaries: more than " + keep + " render textures given back in one frame; the oldest let go at once (frame " + Time.frameCount + ")");
			}
		}

		/// <summary>mobile.txt tempTest=N: N temporaries of four sizes, each blitted into and read back, in this one frame.</summary>
		public static void Test()
		{
			int count = Settings.Get("tempTest", 0), smallest = Settings.Get("tempTestSize", 64);
			if (count <= 0)
			{
				return;
			}
			try
			{
				(long bytes, long textures) before = RenderTargets();
				Stopwatch watch = Stopwatch.StartNew();
				(long bytes, long textures) most = before;
				for (int i = 0; i < count; i++)
				{
					int size = smallest << (i % 4);
					RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Linear);
					Graphics.Blit(BaseContent.WhiteTex, rt);
					RenderTexture previous = RenderTexture.active;
					RenderTexture.active = rt;
					var readable = new Texture2D(size, size);
					readable.ReadPixels(new Rect(0, 0, size, size), 0, 0);
					readable.Apply();
					RenderTexture.active = previous;
					RenderTexture.ReleaseTemporary(rt);
					UnityEngine.Object.Destroy(readable);
					if (i % 50 == 49)
					{
						(long bytes, long textures) now = RenderTargets();
						if (now.bytes > most.bytes)
						{
							most = now;
						}
					}
				}
				(long bytes, long textures) after = RenderTargets();
				Console.WriteLine("[mobile] temporaries: test: " + count + " in one frame in " + watch.ElapsedMilliseconds + " ms; render targets " + (before.bytes >> 20) + " MiB (" + before.textures + ") before, at most "
					+ (most.bytes >> 20) + " MiB (" + most.textures + ") during, " + (after.bytes >> 20) + " MiB (" + after.textures + ") after; " + letGo + " let go early so far");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] temporaries: test failed: " + e);
			}
		}

		private static (long, long) RenderTargets()
		{
			var kinds = new List<GpuMemoryTracker.TopLevelStat>();
			GpuMemoryTracker.CollectTopLevel(kinds);
			foreach (GpuMemoryTracker.TopLevelStat kind in kinds)
			{
				if (kind.Label == "RenderTarget")
				{
					return (kind.Bytes, kind.Count);
				}
			}
			return (0, 0);
		}
	}
}
