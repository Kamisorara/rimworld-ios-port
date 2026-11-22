using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Waylith.Graphics;
using Waylith.Rendering;

namespace PortMobile
{
	/// <summary>
	/// Textures that have not been drawn for a while are let go, and come back from the texture cache when they are
	/// drawn again.
	///
	/// The game loads every texture of every def when it starts. The engine keeps them all in graphics memory until its
	/// budget is passed; then it moves the least recently used ones to ordinary memory, and where there is no such
	/// difference (a phone: all memory is the graphics card's) it can only drop those that were never drawn at all.
	/// So what was drawn once stays for good: the main menu's background art (170 MiB) through the whole game, every
	/// pawn, building and icon that ever was on screen. With many mods that fills the budget and stays there.
	/// The engine's own way of dropping a texture (the same it uses for the never drawn ones: only textures it can
	/// read back from its cache, never ones a mod made or changed in memory) is used here for the ones not drawn for
	/// coldTextureMinutes (mobile.txt, 10; 0 switches it off), and after a load for the ones that have never been drawn.
	/// Measured without mods, 100 colonists on screen: of 5 700 textures (739 MiB) the scene draws 276 (41 MiB); bringing
	/// one back takes 0.7 ms on average (the largest, the menu's 8000 x 5000 background, 33 ms).
	/// perfcmd.txt: "evict=all" lets go of everything that can be, "evict=N" of what was not drawn for N seconds; the
	/// log then says how many came back and what that cost.
	/// </summary>
	internal static class ColdTextures
	{
		private static bool ready;
		private static int minutes;
		private static IList registry;
		private static System.Threading.Lock gate;
		private static PropertyInfo canRun, frameSerial;
		private static Func<Texture, bool> tryEvict;
		private static Func<GpuTextureAllocation, long> lastUseFrame;
		private static Func<GpuTextureAllocation, long> allocatedBytes;
		private static Func<GpuTextureAllocation, bool> isDisposed;
		private static int frames;
		private static readonly List<Texture> candidates = new List<Texture>();

		// What bringing textures back cost since the last report.
		private static int broughtBack;
		private static double broughtBackMs, slowestMs;
		private static string slowestName = "";
		[ThreadStatic] private static long ensureStarted;

		public static void Start(Harmony harmony)
		{
			minutes = Settings.Get("coldTextureMinutes", 10);
			try
			{
				Assembly waylith = typeof(Texture).Assembly;
				Type residency = waylith.GetType("Waylith.Rendering.TextureResidency");
				Type service = waylith.GetType("Waylith.Rendering.GpuTextureService");
				BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
				registry = residency.GetField("Registry", any).GetValue(null) as IList;
				gate = residency.GetField("Gate", any).GetValue(null) as System.Threading.Lock;
				canRun = service.GetProperty("CanRunResidencyWork", any);
				frameSerial = service.GetProperty("RenderFrameSerial", any);
				tryEvict = (Func<Texture, bool>)Delegate.CreateDelegate(typeof(Func<Texture, bool>), typeof(Texture).GetMethod("TryEvictForResidency", any));
				lastUseFrame = (Func<GpuTextureAllocation, long>)Delegate.CreateDelegate(typeof(Func<GpuTextureAllocation, long>), typeof(GpuTextureAllocation).GetProperty("LastUseFrame", any).GetGetMethod(true));
				allocatedBytes = (Func<GpuTextureAllocation, long>)Delegate.CreateDelegate(typeof(Func<GpuTextureAllocation, long>), typeof(GpuTextureAllocation).GetProperty("AllocatedBytes", any).GetGetMethod(true));
				isDisposed = (Func<GpuTextureAllocation, bool>)Delegate.CreateDelegate(typeof(Func<GpuTextureAllocation, bool>), typeof(GpuTextureAllocation).GetProperty("IsDisposed", any).GetGetMethod(true));
				ready = registry != null && gate != null && canRun != null && frameSerial != null;
				// How long a texture takes to come back, measured where the engine brings it back.
				MethodInfo ensure = typeof(Texture2D).GetMethod("EnsureResident", any);
				harmony.Patch(ensure, prefix: new HarmonyMethod(typeof(ColdTextures), nameof(BeforeEnsure)), postfix: new HarmonyMethod(typeof(ColdTextures), nameof(AfterEnsure)));
				Console.WriteLine("[mobile] cold textures: " + (ready ? (minutes > 0 ? "let go after " + minutes + " min without being drawn" : "kept (coldTextureMinutes=0)") : "the engine's residency is not as expected, left alone"));
			}
			catch (Exception e)
			{
				ready = false;
				Console.WriteLine("[mobile] cold textures not installed: " + e.Message);
			}
		}

		// For the record's snaps (Recorder): textures put up when they were drawn (the engine loads them at first use, and
		// brings back the ones let go), since the last snap. A tester's "the frame rate jumps since 4990" could not be told
		// apart from heat or the collector without it.
		private static int snapLoads;
		private static double snapLoadsMs, snapSlowestMs;

		/// <summary>"textures put up N (T ms, the slowest S)" since the last snap, or nothing when there were none.</summary>
		public static string SnapLoads()
		{
			if (snapLoads == 0)
			{
				return null;
			}
			string text = string.Format(CultureInfo.InvariantCulture, "textures put up {0} ({1:F0} ms, the slowest {2:F0})", snapLoads, snapLoadsMs, snapSlowestMs);
			snapLoads = 0;
			snapLoadsMs = snapSlowestMs = 0;
			return text;
		}

		public static void BeforeEnsure(Texture2D __instance)
		{
			ensureStarted = __instance.GpuResource == null ? Stopwatch.GetTimestamp() : 0;
		}

		public static void AfterEnsure(Texture2D __instance)
		{
			if (ensureStarted != 0 && __instance.GpuResource != null)
			{
				double ms = (Stopwatch.GetTimestamp() - ensureStarted) * 1000.0 / Stopwatch.Frequency;
				broughtBack++;
				broughtBackMs += ms;
				snapLoads++;
				snapLoadsMs += ms;
				snapSlowestMs = Math.Max(snapSlowestMs, ms);
				if (ms > slowestMs)
				{
					slowestMs = ms;
					GpuTextureAllocation gpu = __instance.GpuResource;
					slowestName = __instance.SourcePath + " (" + (gpu != null ? (allocatedBytes(gpu) >> 10) + " KiB" : "?") + ")";
				}
			}
			ensureStarted = 0;
		}

		/// <summary>Once a frame, on the main thread (where the engine does its own residency work).</summary>
		public static void OnFrame()
		{
			if (!ready || minutes <= 0 || ++frames % 600 != 0)
			{
				return;
			}
			// Sixty frames a second at most: the age is counted in frames the engine rendered.
			Evict(minutes * 3600L, false, 512, false);
		}

		/// <summary>perfcmd "evict=all" or "evict=SECONDS".</summary>
		public static void Command(string value)
		{
			if (!ready)
			{
				return;
			}
			if (value == "report")
			{
				Report();
				return;
			}
			long olderThanFrames = value == "all" ? 0 : (long)(double.Parse(value, CultureInfo.InvariantCulture) * 60);
			Report();
			Evict(olderThanFrames, true, int.MaxValue, true);
		}

		/// <summary>After a load (MemTrim): what has never been drawn, and what has not been for the set time. Megabytes let go.</summary>
		public static long AfterLoad()
		{
			return ready && minutes > 0 ? Evict(minutes * 3600L, true, int.MaxValue, false) >> 20 : 0;
		}

		private static void Report()
		{
			Console.WriteLine("[mobile] cold textures: " + broughtBack + " brought back since the last report, " + broughtBackMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms in all, the slowest "
				+ slowestMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms" + (broughtBack > 0 ? ": " + slowestName : ""));
			broughtBack = 0;
			broughtBackMs = slowestMs = 0;
			slowestName = "";
		}

		private static long Evict(long olderThanFrames, bool neverDrawnToo, int atMost, bool say)
		{
			try
			{
				if (!(bool)canRun.GetValue(null))
				{
					return 0;
				}
				Stopwatch watch = Stopwatch.StartNew();
				long now = (long)frameSerial.GetValue(null);
				candidates.Clear();
				gate.Enter();
				try
				{
					for (int i = 0; i < registry.Count; i++)
					{
						if (registry[i] is WeakReference<Texture> weak && weak.TryGetTarget(out Texture texture))
						{
							GpuTextureAllocation gpu = texture.GpuResource;
							// Game textures only: the atlases are drawn from all the time, and targets and the interface's own cannot be read back.
							if (gpu != null && gpu.Category == GpuMemoryCategory.GameTexture && !isDisposed(gpu))
							{
								long used = lastUseFrame(gpu);
								if (used < 0 ? neverDrawnToo || now >= olderThanFrames : now - used >= olderThanFrames)
								{
									candidates.Add(texture);
								}
							}
						}
					}
				}
				finally
				{
					gate.Exit();
				}
				int count = 0;
				long bytes = 0;
				foreach (Texture texture in candidates)
				{
					if (count >= atMost)
					{
						break;
					}
					GpuTextureAllocation gpu = texture.GpuResource;
					long size = gpu != null ? allocatedBytes(gpu) : 0;
					if (tryEvict(texture))
					{
						count++;
						bytes += size;
					}
				}
				if (say || count > 0)
				{
					Console.WriteLine("[mobile] cold textures: " + count + " of " + candidates.Count + " not drawn for " + (olderThanFrames / 60) + " s" + (neverDrawnToo ? " or never" : "") + " let go, " + (bytes >> 20) + " MiB, in "
						+ watch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
				}
				candidates.Clear();
				return bytes;
			}
			catch (Exception e)
			{
				ready = false;
				Console.WriteLine("[mobile] cold textures switched off: " + e.Message);
				return 0;
			}
		}
	}
}
