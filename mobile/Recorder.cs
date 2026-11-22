using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Waylith.Diagnostics;
using Waylith.Graphics;

namespace PortMobile
{
	/// <summary>
	/// The run's record: logs/record.txt beside the game folder, in the zip of "导出日志" together with the two runs
	/// before it (record-1.txt, record-2.txt), so that a player's report can be read against what the game was doing
	/// when it happened, without the player having to know what to tell.
	///   - every recordSeconds (mobile.txt, 10; 0 switches the record off) one "snap" line: what is on (menu, loading,
	///     map or world, map size, pawns, camera zoom, speed, the windows), the frames (per second, median, 99 %, the
	///     longest) and ticks per second, the processor (main and render thread, the whole process, the main thread's
	///     core and its clock, the phone's clock caps when it holds any), memory (the collector's heap, resident, what
	///     the system has available, the engine's graphics memory against its budget and its largest kinds, what the
	///     driver has mapped), temperatures, and the errors, warnings and slow frames since the line before;
	///   - events as they happen: the program state, loading (and how long it took), new games, loads and saves, maps
	///     made, letters, windows opened and closed (a message box with its text), frames longer than half a second
	///     outside loading, the first three of every kind of error, every touch (one line: what it became, where, how
	///     long, on which window, the zoom before and after);
	///   - "记录问题" (KeyWheel.ReportName, a key of the key wheel): a screenshot, the snap of that moment, the selection,
	///     the camera, the windows and the engine's full memory reports go to logs/marks/ (the last six kept), and the
	///     moment is marked in the record; then a box asks for the player's words on it (Dialog_ProblemNote), which go
	///     first into the mark's text and into the record (a "note" line).
	/// Every line is written through at once (a crash loses nothing). A run's record stops at 8 MB.
	/// </summary>
	internal static class Recorder
	{
		[DllImport("libc")] private static extern int gettid();

		private const long MaxBytes = 8L << 20;
		private const int MarksKept = 6;
		private static readonly object gate = new object();
		private static readonly Stopwatch clock = Stopwatch.StartNew();
		private static FileStream file;
		private static long written;
		private static bool on;
		private static double seconds;
		private static string dir;

		// The frames since the last snap.
		private static double lastFrameAt, windowStart, longest;
		private static int frames, ticksAtStart = -1;
		private static readonly List<float> frameTimes = new List<float>(2048);
		private static int mainTid, renderTid;
		private static long mainCpuAtStart, renderCpuAtStart, processCpuAtStart;
		private static int gen2AtStart;
		private static TimeSpan pauseAtStart;
		// The compiler since the last snap: methods, its time on all threads and on the main thread.
		private static long jitMethodsAtStart;
		private static TimeSpan jitAtStart, jitMainAtStart;
		private static int errors, warnings, slowFrames;
		// Whether PortHooks counts the calls of Log.Warning (hooks/LoadRecovery.cs; where it does not, the record's own
		// postfix counts them in warnings), and its count when the window began.
		private static bool portWarnings;
		private static long warningsAtStart;
		private static double lastSlowAt = -100;
		private static int slowUnsaid;
		private static readonly Dictionary<string, int> errorKinds = new Dictionary<string, int>();

		// What changed between frames.
		private static string lastState = "";
		private static float lastScale;
		private static bool loading;
		private static double loadingSince;
		private static readonly Stopwatch saveWatch = new Stopwatch(), mapWatch = new Stopwatch();

		// The touch going on (Touch tells).
		private static double touchAt;
		private static Vector2 touchFrom;
		private static string touchWindow, touchModes = "";
		private static float touchZoom;
		private static bool touching;

		// The phone's sensors and clock policies, found once.
		private static readonly List<KeyValuePair<string, string>> thermalZones = new List<KeyValuePair<string, string>>();
		private static readonly List<string> clockPolicies = new List<string>();
		private static string batteryTemp, gpuMapped;

		// "记录问题": frames until the screenshot (the wheel has closed by then) and until the message to the player, or the
		// box for the player's words (asked for by the key wheel's key, not by the agent's "mark" unless told); the mark taken.
		private static int markIn, messageIn;
		private static bool askWords;
		private static string lastMark;

		/// <summary>The section of a mark's text that holds the player's words (ReportSummary.java looks for it).</summary>
		internal const string WordsTitle = "== 玩家的说明";

		public static void Start(Harmony harmony)
		{
			try
			{
				dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"));
				Directory.CreateDirectory(dir);
				harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.Update)), postfix: new HarmonyMethod(typeof(Recorder), nameof(AfterUpdate)));
				seconds = Settings.Get("recordSeconds", 10);
				if (seconds <= 0)
				{
					Console.WriteLine("[mobile] record: off (recordSeconds=0); 记录问题 still works");
					return;
				}
				Rotate("record.txt", "record-1.txt", "record-2.txt");
				file = new FileStream(Path.Combine(dir, "record.txt"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
				on = true;
				FindSensors();
				Patch(harmony, AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame), new[] { typeof(string) }), nameof(BeforeSave), nameof(AfterSave));
				Patch(harmony, AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.LoadGame), new[] { typeof(string) }), nameof(BeforeLoad), null);
				Patch(harmony, AccessTools.Method(typeof(Game), nameof(Game.InitNewGame)), nameof(BeforeNewGame), null);
				Patch(harmony, AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)), null, nameof(AfterGameReady));
				Patch(harmony, AccessTools.Method(typeof(MapGenerator), nameof(MapGenerator.GenerateMap)), nameof(BeforeMap), nameof(AfterMap));
				Patch(harmony, AccessTools.Method(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) }), null, nameof(AfterLetter));
				Patch(harmony, AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), null, nameof(AfterWindowAdd));
				Patch(harmony, AccessTools.Method(typeof(WindowStack), nameof(WindowStack.TryRemove), new[] { typeof(Window), typeof(bool) }), null, nameof(AfterWindowRemove));
				Patch(harmony, AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), null, nameof(AfterError));
				// Log.Warning's calls are counted by PortHooks' postfix of it, part of its first patch, which no compiled code
				// can have passed by (hooks/LoadRecovery.cs); the record's own postfix only where PortHooks does not count them,
				// and InlineGuard then makes every replacement again that may hold Log.Warning.
				portWarnings = PortWarnings() >= 0;
				if (!portWarnings)
				{
					Patch(harmony, AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), null, nameof(AfterWarning));
				}
				Patch(harmony, AccessTools.Method(typeof(Root), nameof(Root.Shutdown)), nameof(BeforeQuit), null);
				Write("record of " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ": RimWorld " + VersionControl.CurrentVersionStringWithRev + ", " + Screen.width + "x" + Screen.height
					+ ", a snap every " + seconds + " s; sensors: " + thermalZones.Count + " thermal zones" + (batteryTemp != null ? ", battery" : "")
					+ (gpuMapped != null ? ", driver's graphics memory" : "") + ", " + clockPolicies.Count + " clock policies");
				Write("mobile.txt: " + MobileSettings());
				Write("mods: " + Mods());
				Console.WriteLine("[mobile] record: logs/record.txt, a snap every " + seconds + " s; " + (portWarnings ? "warnings as PortHooks counts them" : "warnings counted by its own patch of Log.Warning"));
			}
			catch (Exception e)
			{
				on = false;
				Console.WriteLine("[mobile] record not started: " + e.Message);
			}
		}

		private static void Patch(Harmony harmony, System.Reflection.MethodInfo method, string prefix, string postfix)
		{
			if (method == null)
			{
				Console.WriteLine("[mobile] record: a method to watch is missing (" + (prefix ?? postfix) + ")");
				return;
			}
			try
			{
				harmony.Patch(method, prefix: prefix != null ? new HarmonyMethod(typeof(Recorder), prefix) : null, postfix: postfix != null ? new HarmonyMethod(typeof(Recorder), postfix) : null);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] record: " + method.Name + " not watched: " + e.Message);
			}
		}

		private static void Rotate(string current, string previous, string older)
		{
			try
			{
				string a = Path.Combine(dir, current), b = Path.Combine(dir, previous), c = Path.Combine(dir, older);
				if (File.Exists(b))
				{
					File.Move(b, c, true);
				}
				if (File.Exists(a))
				{
					File.Move(a, b, true);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] record: the older records not moved: " + e.Message);
			}
		}

		// ---- writing ----

		/// <summary>One line with the time of day and the seconds of the run.</summary>
		private static void Write(string text)
		{
			lock (gate)
			{
				if (file == null || written > MaxBytes)
				{
					return;
				}
				try
				{
					string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture).PadLeft(7) + " " + text.Replace('\n', ' ').Replace('\r', ' ') + "\n";
					if (written + line.Length > MaxBytes)
					{
						line = "the record stops here (8 MB)\n";
						written = MaxBytes;
					}
					byte[] bytes = Encoding.UTF8.GetBytes(line);
					file.Write(bytes, 0, bytes.Length);
					file.Flush();
					written += bytes.Length;
				}
				catch (Exception)
				{
				}
			}
		}

		/// <summary>An event: its kind, then what happened.</summary>
		internal static void Event(string kind, string text)
		{
			if (on)
			{
				Write(kind.PadRight(8) + " " + text);
			}
		}

		// ---- every frame ----

		/// <summary>Postfix of Root.Update: once a frame, on the main thread.</summary>
		public static void AfterUpdate()
		{
			try
			{
				if (markIn > 0 && --markIn == 0)
				{
					TakeMark();
				}
				if (messageIn > 0 && --messageIn == 0)
				{
					// The screenshot is long written: the box does not show in it.
					if (askWords && lastMark != null && Find.WindowStack != null)
					{
						Find.WindowStack.Add(new Dialog_ProblemNote(lastMark));
					}
					else
					{
						Recorded(false);
					}
				}
				if (!on)
				{
					return;
				}
				double now = clock.Elapsed.TotalSeconds;
				if (mainTid == 0)
				{
					mainTid = gettid();
					lastFrameAt = now;
					BeginWindow(now);
					return;
				}
				double frame = now - lastFrameAt;
				lastFrameAt = now;
				frames++;
				if (frame > longest)
				{
					longest = frame;
				}
				if (frameTimes.Count < 2048)
				{
					frameTimes.Add((float)frame);
				}
				bool busy = LongEventHandler.AnyEventNowOrWaiting;
				if (busy != loading)
				{
					loading = busy;
					if (busy)
					{
						loadingSince = now;
						Event("loading", LoadingText());
					}
					else
					{
						Event("loaded", (now - loadingSince).ToString("F1", CultureInfo.InvariantCulture) + " s");
					}
				}
				else if (!busy && frame > 0.5)
				{
					SlowFrame(now, frame);
				}
				string state = StateName();
				if (state != lastState)
				{
					Event("state", (lastState.Length > 0 ? lastState + " -> " : "") + state);
					lastState = state;
				}
				float scale = (float)Screen.width / UI.screenWidth;
				if (Math.Abs(scale - lastScale) > 0.005f)
				{
					// The interface scale the screen is drawn at (the port sets the player's own once the menu is up, and the options change it).
					Event("ui", "scale " + scale.ToString("0.##", CultureInfo.InvariantCulture) + " (" + UI.screenWidth + "x" + UI.screenHeight + " units)");
					lastScale = scale;
				}
				if (now - windowStart >= seconds)
				{
					Write("snap     " + Snap(now));
					BeginWindow(now);
				}
			}
			catch (Exception e)
			{
				on = false;
				Console.WriteLine("[mobile] record stopped: " + e);
			}
		}

		private static void SlowFrame(double now, double frame)
		{
			slowFrames++;
			if (frame > 5.0)
			{
				// The app was away (the frame loop waits while it is not on the screen), or the game stood still.
				Event("gap", frame.ToString("F1", CultureInfo.InvariantCulture) + " s without a frame (the app away, or a stall), " + Context());
				return;
			}
			if (now - lastSlowAt < 5.0)
			{
				slowUnsaid++;
				return;
			}
			lastSlowAt = now;
			Event("slow", (frame * 1000.0).ToString("F0", CultureInfo.InvariantCulture) + " ms frame" + (slowUnsaid > 0 ? " (and " + slowUnsaid + " more since the last said)" : "") + ", gen2 " + GC.CollectionCount(2) + ", " + Context());
			slowUnsaid = 0;
		}

		private static string StateName()
		{
			switch (Current.ProgramState)
			{
				case ProgramState.Entry: return "menu";
				case ProgramState.Playing: return WorldRendererUtility.WorldSelected ? "world" : "map";
				default: return Current.ProgramState.ToString();
			}
		}

		private static string LoadingText()
		{
			try
			{
				object current = AccessTools.Field(typeof(LongEventHandler), "currentEvent").GetValue(null);
				string text = current != null ? Traverse.Create(current).Field("eventText").GetValue<string>() : null;
				return string.IsNullOrEmpty(text) ? "(queued)" : text;
			}
			catch (Exception)
			{
				return "";
			}
		}

		/// <summary>Where the game is, in a few words (for an event line).</summary>
		private static string Context()
		{
			var text = new StringBuilder(StateName());
			try
			{
				if (Current.ProgramState == ProgramState.Playing && Find.TickManager != null)
				{
					text.Append(' ').Append(Find.TickManager.Paused ? "paused" : Find.TickManager.CurTimeSpeed.ToString());
					if (Find.CameraDriver != null && !WorldRendererUtility.WorldSelected)
					{
						text.Append(" zoom ").Append(Find.CameraDriver.RootSize.ToString("F1", CultureInfo.InvariantCulture));
					}
				}
				string windows = Windows();
				if (windows.Length > 0)
				{
					text.Append(", windows ").Append(windows);
				}
			}
			catch (Exception)
			{
			}
			return text.ToString();
		}

		// ---- the snap ----

		private static void BeginWindow(double now)
		{
			windowStart = now;
			frames = 0;
			longest = 0;
			frameTimes.Clear();
			errors = warnings = slowFrames = 0;
			if (portWarnings)
			{
				warningsAtStart = LoadRecoveryWarnings();
			}
			ticksAtStart = Current.ProgramState == ProgramState.Playing && Find.TickManager != null ? Find.TickManager.TicksGame : -1;
			if (renderTid == 0)
			{
				renderTid = Perf.FindThread("RenderThread");
			}
			mainCpuAtStart = Perf.ThreadCpu(mainTid, out _);
			renderCpuAtStart = renderTid != 0 ? Perf.ThreadCpu(renderTid, out _) : 0;
			processCpuAtStart = ProcessCpu();
			gen2AtStart = GC.CollectionCount(2);
			pauseAtStart = GC.GetTotalPauseDuration();
			jitMethodsAtStart = System.Runtime.JitInfo.GetCompiledMethodCount();
			jitAtStart = System.Runtime.JitInfo.GetCompilationTime();
			jitMainAtStart = System.Runtime.JitInfo.GetCompilationTime(true);
		}

		private static string Snap(double now)
		{
			var line = new StringBuilder(512);
			double span = Math.Max(0.001, now - windowStart);
			Part(line, () => Where());
			Part(line, () =>
			{
				frameTimes.Sort();
				float median = frameTimes.Count > 0 ? frameTimes[frameTimes.Count / 2] : 0f;
				float high = frameTimes.Count > 0 ? frameTimes[Math.Min(frameTimes.Count - 1, (int)(frameTimes.Count * 0.99))] : 0f;
				string ticks = "";
				if (ticksAtStart >= 0 && Current.ProgramState == ProgramState.Playing && Find.TickManager != null)
				{
					ticks = ", " + ((Find.TickManager.TicksGame - ticksAtStart) / span).ToString("F0", CultureInfo.InvariantCulture) + " tps";
				}
				return string.Format(CultureInfo.InvariantCulture, "{0:F1} fps (median {1:F1} ms, 99% {2:F1}, longest {3:F0}){4}", frames / span, median * 1000f, high * 1000f, longest * 1000.0, ticks);
			});
			Part(line, () =>
			{
				long main = Perf.ThreadCpu(mainTid, out int mainCore) - mainCpuAtStart;
				long render = renderTid != 0 ? Perf.ThreadCpu(renderTid, out _) - renderCpuAtStart : 0;
				long process = ProcessCpu() - processCpuAtStart;
				return string.Format(CultureInfo.InvariantCulture, "cpu main {0:F0}% (cpu{1} {2}), render {3:F0}%, process {4:F0}%{5}", main / span, mainCore, CoreClock(mainCore), render / span, process / span, Caps());
			});
			Part(line, () =>
			{
				GCMemoryInfo info = GC.GetGCMemoryInfo();
				return string.Format(CultureInfo.InvariantCulture, "heap {0}/{1} MB, gen2 {2}, pauses {3:F0} ms", info.HeapSizeBytes >> 20, info.TotalCommittedBytes >> 20, GC.CollectionCount(2) - gen2AtStart, (GC.GetTotalPauseDuration() - pauseAtStart).TotalMilliseconds);
			});
			Part(line, () => string.Format(CultureInfo.InvariantCulture, "jit {0} methods, {1:F0} ms ({2:F0} on the main thread)", System.Runtime.JitInfo.GetCompiledMethodCount() - jitMethodsAtStart,
				(System.Runtime.JitInfo.GetCompilationTime() - jitAtStart).TotalMilliseconds, (System.Runtime.JitInfo.GetCompilationTime(true) - jitMainAtStart).TotalMilliseconds));
			Part(line, () => ColdTextures.SnapLoads());
			Part(line, () => ProcessMemory());
			Part(line, () => GraphicsMemory());
			Part(line, () => Temperatures());
			Part(line, () => "errors " + errors + ", warnings " + (portWarnings ? LoadRecoveryWarnings() - warningsAtStart : warnings) + ", slow frames " + slowFrames);
			return line.ToString();
		}

		/// <summary>The calls of Log.Warning so far as PortHooks counts them (hooks/LoadRecovery.cs); -1 where it does not.</summary>
		private static long PortWarnings()
		{
			try
			{
				return LoadRecoveryWarnings();
			}
			catch (Exception)
			{
				// An older PortHooks, which keeps no count.
				return -1;
			}
		}

		// Apart, and not inlined: with an older PortHooks the call fails where this is compiled, and only here.
		[MethodImpl(MethodImplOptions.NoInlining)]
		private static long LoadRecoveryWarnings()
		{
			return PortHooks.LoadRecovery.Warnings;
		}

		/// <summary>One part of a snap: a part that fails says so and the others are still written.</summary>
		private static void Part(StringBuilder line, Func<string> part)
		{
			string text;
			try
			{
				text = part();
			}
			catch (Exception e)
			{
				text = "(" + e.GetType().Name + ")";
			}
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			if (line.Length > 0)
			{
				line.Append(" | ");
			}
			line.Append(text);
		}

		private static string Where()
		{
			var text = new StringBuilder(StateName());
			if (loading)
			{
				text.Append(" loading");
			}
			if (Current.ProgramState == ProgramState.Playing && Find.TickManager != null)
			{
				Map map = Find.CurrentMap;
				if (map != null)
				{
					text.Append(' ').Append(map.Size.x).Append('x').Append(map.Size.z).Append(" (").Append(Find.Maps.Count).Append(Find.Maps.Count == 1 ? " map" : " maps").Append("), ")
						.Append(map.mapPawns.AllPawnsSpawnedCount).Append(" pawns, ").Append(map.mapPawns.FreeColonistsSpawnedCount).Append(" colonists, ")
						.Append(map.listerThings.AllThings.Count).Append(" things");
					if (Find.CameraDriver != null)
					{
						text.Append(", zoom ").Append(Find.CameraDriver.RootSize.ToString("F1", CultureInfo.InvariantCulture));
					}
				}
				text.Append(", ").Append(Find.TickManager.Paused ? "paused" : Find.TickManager.CurTimeSpeed.ToString()).Append(", tick ").Append(Find.TickManager.TicksGame);
			}
			string windows = Windows();
			if (windows.Length > 0)
			{
				text.Append(", windows ").Append(windows);
			}
			return text.ToString();
		}

		/// <summary>The windows open, the topmost last (the engine's immediate ones left out).</summary>
		private static string Windows()
		{
			WindowStack stack = Find.WindowStack;
			if (stack == null)
			{
				return "";
			}
			var names = new List<string>();
			foreach (Window window in stack.Windows)
			{
				if (!(window is ImmediateWindow))
				{
					names.Add(window.GetType().Name);
				}
			}
			return string.Join(",", names);
		}

		private static string ProcessMemory()
		{
			string rss = "", anon = "", fileBacked = "", swap = "";
			foreach (string l in File.ReadAllLines("/proc/self/status"))
			{
				if (l.StartsWith("VmRSS:", StringComparison.Ordinal)) rss = Kb(l);
				else if (l.StartsWith("RssAnon:", StringComparison.Ordinal)) anon = Kb(l);
				else if (l.StartsWith("RssFile:", StringComparison.Ordinal)) fileBacked = Kb(l);
				else if (l.StartsWith("VmSwap:", StringComparison.Ordinal)) swap = Kb(l);
			}
			string available = "";
			foreach (string l in File.ReadLines("/proc/meminfo"))
			{
				if (l.StartsWith("MemAvailable:", StringComparison.Ordinal))
				{
					available = Kb(l);
					break;
				}
			}
			return "resident " + rss + " MB (anon " + anon + ", file " + fileBacked + (swap != "0" && swap.Length > 0 ? ", swapped " + swap : "") + "), the phone has " + available + " MB available";
		}

		/// <summary>A /proc line's kB as MB.</summary>
		private static string Kb(string line)
		{
			string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
			return parts.Length > 1 && long.TryParse(parts[1], out long kb) ? (kb >> 10).ToString(CultureInfo.InvariantCulture) : "?";
		}

		private static string GraphicsMemory()
		{
			var text = new StringBuilder();
			if (GpuMemoryReport.TryGetSummary(out GpuMemorySummary summary))
			{
				text.Append("gpu ").Append(summary.DeviceLocalDriverUsageBytes >> 20).Append('/').Append(summary.DeviceLocalBudgetBytes >> 20).Append(" MB");
			}
			var kinds = new List<GpuMemoryTracker.TopLevelStat>();
			GpuMemoryTracker.CollectTopLevel(kinds);
			kinds.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
			if (kinds.Count > 0)
			{
				text.Append(text.Length > 0 ? ": " : "gpu: ").Append(string.Join(", ", kinds.Take(5).Select(k => k.Label + " " + (k.Bytes >> 20) + " MB (" + k.Count + ")")));
			}
			if (gpuMapped != null)
			{
				try
				{
					text.Append(", driver mapped ").Append(long.Parse(File.ReadAllText(gpuMapped).Trim(), CultureInfo.InvariantCulture) >> 20).Append(" MB");
				}
				catch (Exception)
				{
					gpuMapped = null;
				}
			}
			return text.ToString();
		}

		// ---- the phone's sensors ----

		private static void FindSensors()
		{
			try
			{
				foreach (string zone in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*"))
				{
					try
					{
						string type = File.ReadAllText(Path.Combine(zone, "type")).Trim();
						string temp = Path.Combine(zone, "temp");
						string group = Group(type);
						// A zone that reads 95 degrees or more when the game starts is not a temperature (a HONOR with a
						// Snapdragon 8 Elite Gen 5 has a "cpu" zone that reads 105 the whole time, shell 37, GPU 40).
						if (group != null && File.Exists(temp) && int.TryParse(File.ReadAllText(temp).Trim(), out int value) && value > 0 && (value > 1000 ? value / 1000 : value) < 95)
						{
							thermalZones.Add(new KeyValuePair<string, string>(group, temp));
						}
					}
					catch (Exception)
					{
					}
				}
			}
			catch (Exception)
			{
			}
			try
			{
				string battery = "/sys/class/power_supply/battery/temp";
				if (int.TryParse(File.ReadAllText(battery).Trim(), out _))
				{
					batteryTemp = battery;
				}
			}
			catch (Exception)
			{
			}
			try
			{
				string mapped = "/sys/class/kgsl/kgsl/proc/" + Environment.ProcessId + "/gpumem_mapped";
				if (long.TryParse(File.ReadAllText(mapped).Trim(), out _))
				{
					gpuMapped = mapped;
				}
			}
			catch (Exception)
			{
			}
			try
			{
				foreach (string policy in Directory.GetDirectories("/sys/devices/system/cpu/cpufreq", "policy*").OrderBy(p => p, StringComparer.Ordinal))
				{
					if (File.Exists(Path.Combine(policy, "scaling_max_freq")) && File.Exists(Path.Combine(policy, "cpuinfo_max_freq")))
					{
						clockPolicies.Add(policy);
					}
				}
			}
			catch (Exception)
			{
			}
		}

		/// <summary>Which of the temperatures shown a thermal zone counts for: the processor, the graphics, the phone's shell; null for the rest.</summary>
		private static string Group(string type)
		{
			string t = type.ToLowerInvariant();
			if (t.Contains("gpu"))
			{
				return "gpu";
			}
			if (t.StartsWith("cpu", StringComparison.Ordinal) || t.Contains("cpuss") || t.Contains("cpu-"))
			{
				return "cpu";
			}
			if (t.Contains("skin") || t.Contains("shell") || t.Contains("quiet") || t.Contains("sys-therm") || t.Contains("board"))
			{
				return "shell";
			}
			return null;
		}

		private static string Temperatures()
		{
			var highest = new Dictionary<string, int>();
			foreach (KeyValuePair<string, string> zone in thermalZones)
			{
				try
				{
					if (int.TryParse(File.ReadAllText(zone.Value).Trim(), out int value))
					{
						// Millidegrees on most phones, degrees on a few.
						int degrees = value > 1000 ? value / 1000 : value;
						if (degrees > 0 && degrees < 150 && (!highest.TryGetValue(zone.Key, out int was) || degrees > was))
						{
							highest[zone.Key] = degrees;
						}
					}
				}
				catch (Exception)
				{
				}
			}
			var text = new StringBuilder();
			foreach (string group in new[] { "cpu", "gpu", "shell" })
			{
				if (highest.TryGetValue(group, out int degrees))
				{
					text.Append(text.Length > 0 ? ", " : "").Append(group).Append(' ').Append(degrees).Append(" C");
				}
			}
			if (batteryTemp != null)
			{
				try
				{
					text.Append(text.Length > 0 ? ", " : "").Append("battery ").Append((int.Parse(File.ReadAllText(batteryTemp).Trim(), CultureInfo.InvariantCulture) / 10.0).ToString("F1", CultureInfo.InvariantCulture)).Append(" C");
				}
				catch (Exception)
				{
					batteryTemp = null;
				}
			}
			return text.Length > 0 ? text.ToString() : "";
		}

		/// <summary>The clock of a core in GHz.</summary>
		private static string CoreClock(int core)
		{
			if (core < 0)
			{
				return "?";
			}
			try
			{
				return (int.Parse(File.ReadAllText("/sys/devices/system/cpu/cpu" + core + "/cpufreq/scaling_cur_freq").Trim(), CultureInfo.InvariantCulture) / 1e6).ToString("F2", CultureInfo.InvariantCulture) + " GHz";
			}
			catch (Exception)
			{
				return "?";
			}
		}

		/// <summary>The clock policies the phone holds below what the cores can do (heat, a power mode): ", caps policy4 2.40/3.19 GHz".</summary>
		private static string Caps()
		{
			var caps = new StringBuilder();
			foreach (string policy in clockPolicies)
			{
				try
				{
					long max = long.Parse(File.ReadAllText(Path.Combine(policy, "scaling_max_freq")).Trim(), CultureInfo.InvariantCulture);
					long hardware = long.Parse(File.ReadAllText(Path.Combine(policy, "cpuinfo_max_freq")).Trim(), CultureInfo.InvariantCulture);
					if (max < hardware)
					{
						caps.Append(caps.Length > 0 ? ", " : ", caps ").Append(Path.GetFileName(policy)).Append(' ').Append((max / 1e6).ToString("F2", CultureInfo.InvariantCulture)).Append('/').Append((hardware / 1e6).ToString("F2", CultureInfo.InvariantCulture)).Append(" GHz");
					}
				}
				catch (Exception)
				{
				}
			}
			return caps.ToString();
		}

		private static long ProcessCpu()
		{
			try
			{
				string stat = File.ReadAllText("/proc/self/stat");
				string[] fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
				return long.Parse(fields[11]) + long.Parse(fields[12]);
			}
			catch (Exception)
			{
				return 0;
			}
		}

		// ---- what the header says ----

		private static string MobileSettings()
		{
			try
			{
				string path = Path.Combine(AppContext.BaseDirectory, "mobile.txt");
				return File.Exists(path) ? string.Join("; ", File.ReadAllLines(path).Where(l => l.Trim().Length > 0 && !l.StartsWith("#"))) : "(none)";
			}
			catch (Exception e)
			{
				return "(" + e.Message + ")";
			}
		}

		/// <summary>How many mods run, and which of them do not say they are for this version of the game.</summary>
		private static string Mods()
		{
			try
			{
				List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
				var notForThis = mods.Where(m => m.ModMetaData != null && !m.IsOfficialMod && !m.ModMetaData.VersionCompatible).Select(m => m.PackageIdPlayerFacing).ToList();
				return mods.Count + " running (" + mods.Count(m => m.IsOfficialMod) + " official)" + (notForThis.Count > 0 ? "; not marked for " + VersionControl.CurrentMajor + "." + VersionControl.CurrentMinor + ": " + string.Join(", ", notForThis) : "");
			}
			catch (Exception e)
			{
				return "(" + e.Message + ")";
			}
		}

		// ---- the game's events ----

		public static void BeforeSave(string fileName)
		{
			saveWatch.Restart();
			Event("saving", fileName);
		}

		public static void AfterSave(string fileName)
		{
			Event("saved", fileName + " in " + saveWatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
		}

		public static void BeforeLoad(string saveFileName)
		{
			Event("load", saveFileName);
		}

		public static void BeforeNewGame()
		{
			Event("new game", Find.Scenario != null ? Find.Scenario.name : "");
		}

		public static void AfterGameReady()
		{
			try
			{
				Event("game", "ready: " + Find.Maps.Count + " maps, " + PawnsFinder.AllMaps_FreeColonistsSpawned.Count + " colonists on maps, " + (Find.World != null ? Find.World.worldObjects.AllWorldObjects.Count + " world objects" : "no world"));
			}
			catch (Exception e)
			{
				Event("game", "ready (" + e.Message + ")");
			}
		}

		public static void BeforeMap()
		{
			mapWatch.Restart();
		}

		public static void AfterMap(Map __result)
		{
			Event("map", (__result != null ? __result.Size.x + "x" + __result.Size.z + " " + (__result.Parent != null ? __result.Parent.Label : "") : "none") + " made in " + mapWatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
		}

		public static void AfterLetter(Letter let)
		{
			if (let != null)
			{
				Event("letter", let.Label.ToString() + " (" + (let.def != null ? let.def.defName : "") + ")");
			}
		}

		public static void AfterWindowAdd(Window window)
		{
			if (window == null || window is ImmediateWindow)
			{
				return;
			}
			string text = "+ " + window.GetType().Name;
			if (window is Dialog_MessageBox box)
			{
				text += ": " + Short(box.text.ToString(), 200);
			}
			Event("window", text);
		}

		public static void AfterWindowRemove(Window window, bool __result)
		{
			if (__result && window != null && !(window is ImmediateWindow))
			{
				Event("window", "- " + window.GetType().Name);
			}
		}

		public static void AfterError(string text)
		{
			int seen;
			string key = Short(text, 200);
			lock (gate)
			{
				errors++;
				errorKinds.TryGetValue(key, out seen);
				if (errorKinds.Count < 2000 || seen > 0)
				{
					errorKinds[key] = seen + 1;
				}
			}
			if (seen < 3)
			{
				Event("error", (seen > 0 ? "(again, " + (seen + 1) + ") " : "") + key);
			}
			if (seen == 0 && ConflictTag.Check(text) is string conflict)
			{
				Event("conflict", conflict);
				Console.WriteLine("[mobile] the port and a mod meet in an error: " + conflict);
			}
		}

		public static void AfterWarning()
		{
			lock (gate)
			{
				warnings++;
			}
		}

		public static void BeforeQuit()
		{
			Event("quit", "the game shuts down");
		}

		internal static string Short(string text, int length)
		{
			if (text == null)
			{
				return "";
			}
			int end = text.IndexOf('\n');
			string line = (end >= 0 ? text.Substring(0, end) : text).Trim();
			return line.Length > length ? line.Substring(0, length) + "…" : line;
		}

		// ---- touches (Touch tells) ----

		internal static void TouchBegan(Vector2 at, string window)
		{
			if (!on)
			{
				return;
			}
			touching = true;
			touchAt = clock.Elapsed.TotalSeconds;
			touchFrom = at;
			touchWindow = window;
			touchModes = "";
			touchZoom = Zoom();
		}

		internal static void TouchMode(string mode)
		{
			if (on && touching && touchModes.Length < 80)
			{
				touchModes += (touchModes.Length > 0 ? ">" : "") + mode;
			}
		}

		internal static void TouchEnded(Vector2 at)
		{
			if (!on || !touching)
			{
				return;
			}
			touching = false;
			var text = new StringBuilder();
			text.Append((clock.Elapsed.TotalSeconds - touchAt).ToString("F2", CultureInfo.InvariantCulture)).Append(" s ").Append(touchModes.Length > 0 ? touchModes : "tap")
				.Append(" at ").Append(touchFrom.x.ToString("F0", CultureInfo.InvariantCulture)).Append(',').Append(touchFrom.y.ToString("F0", CultureInfo.InvariantCulture));
			if ((at - touchFrom).magnitude > 20f)
			{
				text.Append(" to ").Append(at.x.ToString("F0", CultureInfo.InvariantCulture)).Append(',').Append(at.y.ToString("F0", CultureInfo.InvariantCulture));
			}
			if (touchWindow != null)
			{
				text.Append(" on ").Append(touchWindow);
			}
			float zoom = Zoom();
			if (touchZoom > 0f && zoom > 0f && Math.Abs(zoom - touchZoom) > 0.05f)
			{
				text.Append(", zoom ").Append(touchZoom.ToString("F1", CultureInfo.InvariantCulture)).Append(" -> ").Append(zoom.ToString("F1", CultureInfo.InvariantCulture));
			}
			Event("touch", text.ToString());
		}

		private static float Zoom()
		{
			try
			{
				return Current.ProgramState == ProgramState.Playing && Find.CameraDriver != null ? Find.CameraDriver.RootSize : -1f;
			}
			catch (Exception)
			{
				return -1f;
			}
		}

		// ---- 记录问题 ----

		/// <summary>
		/// From the key wheel's "记录问题" (and the agent's "mark"): in a few frames (the wheel closes first) the screenshot
		/// and the reports; then, when asked for, the box for the player's words on it.
		/// </summary>
		internal static void Mark(bool askForWords)
		{
			markIn = 3;
			askWords = askForWords;
		}

		/// <summary>The message once a mark is done: where it went, and how it reaches the author.</summary>
		internal static void Recorded(bool withWords)
		{
			Messages.Message(Texts.Chinese
				? (withWords ? "已记录问题（截图、运行数据和说明）。" : "已记录问题（截图和运行数据）。") + "之后长按桌面 Port17 图标选“导出日志”发给作者。"
				: "Problem recorded: a screenshot and the game's state" + (withWords ? ", with your words," : "") + " were saved. Long-press the Port17 icon, choose \"导出日志\" and send the logs.", MessageTypeDefOf.NeutralEvent, historical: false);
		}

		private static string MarksDir => Path.Combine(dir ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs")), "marks");

		/// <summary>
		/// The player's words on a mark (Dialog_ProblemNote): first in the mark's text, right under its first line, and a
		/// "note" line in the record. The summary of the logs' zip lists every mark kept with them (ReportSummary.java).
		/// </summary>
		internal static void Note(string mark, string words)
		{
			words = (words ?? "").Replace("\r", "").Trim();
			if (words.Length == 0)
			{
				return;
			}
			try
			{
				string file = Path.Combine(MarksDir, mark + ".txt");
				// The mark may have gone meanwhile (six newer ones): the words then stand alone.
				string text = File.Exists(file) ? File.ReadAllText(file) : "记录问题 " + mark + "\n";
				int end = text.IndexOf('\n') + 1;
				if (end == 0)
				{
					text += "\n";
					end = text.Length;
				}
				File.WriteAllText(file, text.Substring(0, end) + "\n" + WordsTitle + "\n" + words + "\n" + text.Substring(end));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] record: the words on " + mark + " not written: " + e.Message);
			}
			Console.WriteLine("[mobile] record: the player's words on " + mark + ", " + words.Length + " characters");
			Event("note", mark + ": " + words.Replace("\n", " / "));
		}

		private static void TakeMark()
		{
			string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			string marks = MarksDir;
			Directory.CreateDirectory(marks);
			string name = "mark-" + stamp;
			string shot = "";
			try
			{
				ScreenCapture.CaptureScreenshot(Path.Combine(marks, name + ".png"));
				shot = name + ".png";
			}
			catch (Exception e)
			{
				shot = "no screenshot: " + e.Message;
			}
			var text = new StringBuilder();
			text.AppendLine("记录问题 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ", " + clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s into the run; " + shot);
			text.AppendLine();
			Section(text, "Now", () => on ? Snap(clock.Elapsed.TotalSeconds) : Where());
			Section(text, "Camera", () => Current.ProgramState == ProgramState.Playing && Find.CameraDriver != null ? "cell " + Find.CameraDriver.MapPosition + ", zoom " + Find.CameraDriver.RootSize.ToString("F1", CultureInfo.InvariantCulture) + (WorldRendererUtility.WorldSelected ? ", the world is shown" : "") : "");
			Section(text, "Selected", () => Find.Selector == null ? "" : string.Join("\n", Find.Selector.SelectedObjectsListForReading.Take(20).Select(o => o is Thing t ? t.LabelCap + " (" + t.def.defName + ") at " + t.Position : o.ToString())));
			Section(text, "How the selected things are drawn", () => Find.Selector == null || Current.ProgramState != ProgramState.Playing ? "" : MarkThings.Describe(marks, name));
			Section(text, "Windows", () => Find.WindowStack == null ? "" : string.Join("\n", Find.WindowStack.Windows.Select(w => w.GetType().FullName + " " + w.windowRect)));
			Section(text, "Graphics memory", () => GpuMemoryReport.BuildCompactSummary());
			Section(text, "Process memory", () => ProcessMemoryReport.BuildReport());
			try
			{
				File.WriteAllText(Path.Combine(marks, name + ".txt"), text.ToString());
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] record: the mark not written: " + e.Message);
			}
			Prune(marks);
			Console.WriteLine("[mobile] record: 记录问题 " + name);
			if (on)
			{
				Write("mark     记录问题 " + name + " (logs/marks: " + shot + ", " + name + ".txt), " + Context());
			}
			lastMark = name;
			messageIn = 30;
		}

		private static void Section(StringBuilder text, string title, Func<string> body)
		{
			string content;
			try
			{
				content = body();
			}
			catch (Exception e)
			{
				content = "(" + e.GetType().Name + ": " + e.Message + ")";
			}
			if (!string.IsNullOrEmpty(content))
			{
				text.AppendLine("== " + title).AppendLine(content.TrimEnd()).AppendLine();
			}
		}

		/// <summary>The last MarksKept marks are kept (a mark is its .png, its .txt and the pictures of MarkThings).</summary>
		private static void Prune(string marks)
		{
			try
			{
				List<string> names = Directory.GetFiles(marks, "mark-*.txt").Select(Path.GetFileNameWithoutExtension).OrderByDescending(n => n, StringComparer.Ordinal).ToList();
				foreach (string old in names.Skip(MarksKept))
				{
					File.Delete(Path.Combine(marks, old + ".txt"));
					File.Delete(Path.Combine(marks, old + ".png"));
					// Its pictures of the selected things (MarkThings).
					foreach (string picture in Directory.GetFiles(marks, old + "-*.png"))
					{
						File.Delete(picture);
					}
				}
			}
			catch (Exception)
			{
			}
		}
	}
}
