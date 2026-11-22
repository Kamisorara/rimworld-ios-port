using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Measuring for the scheduling work, on when perftest.txt lies next to the game. Its lines:
	///     load NAME       from the main menu, load this saved game (a test save made with spawntest.txt's "save NAME")
	///     speed N         once the map is there: game speed 0..4 (3 is the triple speed players run at)
	///     camera SIZE     the camera to the middle of the map at this zoom (the same picture every run);
	///                     "camera SIZE X Z": to that cell
	///     interval N      seconds between report lines (5)
	/// Every interval one line goes to the log: frames and ticks per second, the longest frame, how busy the main and
	/// the render thread were and on which cores at what clock, collections and their pauses. perfcmd.txt beside it is
	/// read at every interval and handed to Sched (settings that can be switched while the game runs, for comparing
	/// back to back: the phone's clock limit moves with its temperature, so two runs minutes apart do not compare).
	/// </summary>
	internal static class Perf
	{
		[DllImport("libc")] private static extern int gettid();

		private static bool active;
		private static string loadName;
		private static int speed = -1;
		private static float cameraSize = -1f;
		private static int cameraX = -1, cameraZ = -1;
		private static double interval = 5.0;

		private static readonly Stopwatch clock = Stopwatch.StartNew();
		private static double windowStart;
		private static int frames;
		private static int ticksAtStart = -1;
		private static double lastFrameAt, longestFrame;
		private static readonly List<float> frameTimes = new List<float>(4096);
		private static int mainTid, renderTid;
		private static long mainCpuAtStart, renderCpuAtStart, processCpuAtStart;
		private static int gen0AtStart, gen1AtStart, gen2AtStart;
		private static TimeSpan pauseAtStart;
		private static long allocatedAtStart;
		private static double sceneReadyAt = -1;
		private static bool sceneApplied;
		// How many times the scene was set in this run, and when the last time: each report line carries both (the line
		// "scene set" alone can be lost, the log daemon drops lines in the burst that follows a load).
		private static int sceneCount;
		private static double sceneSetAt;
		private static DateTime commandsRead;

		public static void Start(Harmony harmony)
		{
			string file = Path.Combine(AppContext.BaseDirectory, "perftest.txt");
			if (!File.Exists(file))
			{
				return;
			}
			try
			{
				foreach (string raw in File.ReadAllLines(file))
				{
					string[] parts = raw.Trim().Split(new[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length < 2 || parts[0][0] == '#')
					{
						continue;
					}
					switch (parts[0])
					{
						case "load": loadName = parts[1]; break;
						case "speed": speed = int.Parse(parts[1]); break;
						case "camera":
							cameraSize = float.Parse(parts[1], CultureInfo.InvariantCulture);
							if (parts.Length >= 4)
							{
								cameraX = int.Parse(parts[2]);
								cameraZ = int.Parse(parts[3]);
							}
							break;
						case "interval": interval = Math.Max(1.0, double.Parse(parts[1], CultureInfo.InvariantCulture)); break;
					}
				}
				harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.Update)), postfix: new HarmonyMethod(typeof(Perf), nameof(AfterUpdate)));
				harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)), postfix: new HarmonyMethod(typeof(Perf), nameof(AfterGameInit)));
				active = true;
				Console.WriteLine("[mobile] perf: measuring every " + interval + " s" + (loadName != null ? ", loading '" + loadName + "'" : "") + (speed >= 0 ? ", speed " + speed : "") + (cameraSize > 0 ? ", camera " + cameraSize : ""));
				if (loadName != null)
				{
					LongEventHandler.ExecuteWhenFinished(delegate
					{
						if (Current.ProgramState == ProgramState.Entry)
						{
							Console.WriteLine("[mobile] perf: loading the saved game " + loadName);
							GameDataSaveLoader.LoadGame(loadName);
						}
					});
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] perf not installed: " + e);
			}
		}

		public static void AfterGameInit()
		{
			sceneApplied = false;
			sceneReadyAt = -1;
			LongEventHandler.ExecuteWhenFinished(delegate { sceneReadyAt = clock.Elapsed.TotalSeconds + 3.0; });
		}

		/// <summary>Postfix of Root.Update: once per frame, on the main thread.</summary>
		public static void AfterUpdate()
		{
			if (!active)
			{
				return;
			}
			double now = clock.Elapsed.TotalSeconds;
			if (mainTid == 0)
			{
				mainTid = gettid();
				Sched.MainThreadId = mainTid;
				BeginWindow(now);
				lastFrameAt = now;
				return;
			}
			Sched.OnFrame();
			double frame = now - lastFrameAt;
			lastFrameAt = now;
			frames++;
			if (frame > longestFrame)
			{
				longestFrame = frame;
			}
			if (frameTimes.Count < 4096)
			{
				frameTimes.Add((float)frame);
			}
			if (!sceneApplied && sceneReadyAt > 0 && now >= sceneReadyAt && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
			{
				sceneApplied = true;
				sceneCount++;
				sceneSetAt = now;
				ApplyScene();
				BeginWindow(now);
				return;
			}
			if (now - windowStart >= interval)
			{
				Report(now);
				ReadCommands();
				// The game pauses itself for letters and threats; the scene is for measuring at a set speed.
				if (sceneApplied && speed > 0 && Current.ProgramState == ProgramState.Playing && Find.TickManager != null && (Find.TickManager.Paused || (int)Find.TickManager.CurTimeSpeed != speed))
				{
					Find.TickManager.CurTimeSpeed = (TimeSpeed)speed;
				}
				BeginWindow(clock.Elapsed.TotalSeconds);
			}
		}

		private static void ApplyScene()
		{
			try
			{
				Map map = Find.CurrentMap;
				if (cameraSize > 0)
				{
					Find.CameraDriver.SetRootPosAndSize((cameraX >= 0 ? new IntVec3(cameraX, 0, cameraZ) : map.Center).ToVector3Shifted(), cameraSize);
				}
				// No raids, traders or breakdowns while measuring: every run of the scene should do the same work.
				DebugSettings.enableStoryteller = false;
				DebugSettings.enableRandomMentalStates = false;
				if (speed >= 0)
				{
					Find.TickManager.CurTimeSpeed = (TimeSpeed)speed;
				}
				Console.WriteLine("[mobile] perf: scene set (map " + map.Size.x + "x" + map.Size.z + ", " + map.mapPawns.AllPawnsSpawnedCount + " pawns spawned, " + map.mapPawns.FreeColonistsSpawnedCount + " colonists, speed " + Find.TickManager.CurTimeSpeed + ")");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] perf: scene not set: " + e.Message);
			}
		}

		private static void BeginWindow(double now)
		{
			windowStart = now;
			frames = 0;
			longestFrame = 0;
			frameTimes.Clear();
			ticksAtStart = Current.ProgramState == ProgramState.Playing && Find.TickManager != null ? Find.TickManager.TicksGame : -1;
			if (renderTid == 0)
			{
				renderTid = FindThread("RenderThread");
			}
			mainCpuAtStart = ThreadCpu(mainTid, out _);
			renderCpuAtStart = renderTid != 0 ? ThreadCpu(renderTid, out _) : 0;
			processCpuAtStart = ProcessCpu();
			gen0AtStart = GC.CollectionCount(0);
			gen1AtStart = GC.CollectionCount(1);
			gen2AtStart = GC.CollectionCount(2);
			pauseAtStart = GC.GetTotalPauseDuration();
			allocatedAtStart = GC.GetTotalAllocatedBytes(false);
		}

		private static void Report(double now)
		{
			double seconds = now - windowStart;
			int ticks = -1;
			string speedText = "menu";
			if (Current.ProgramState == ProgramState.Playing && Find.TickManager != null)
			{
				if (ticksAtStart >= 0)
				{
					ticks = Find.TickManager.TicksGame - ticksAtStart;
				}
				speedText = Find.TickManager.Paused ? "paused" : Find.TickManager.CurTimeSpeed.ToString();
			}
			frameTimes.Sort();
			float median = frameTimes.Count > 0 ? frameTimes[frameTimes.Count / 2] : 0f;
			float high = frameTimes.Count > 0 ? frameTimes[Math.Min(frameTimes.Count - 1, (int)(frameTimes.Count * 0.99))] : 0f;
			long mainCpu = ThreadCpu(mainTid, out int mainCore) - mainCpuAtStart;
			int renderCore = -1;
			long renderCpu = renderTid != 0 ? ThreadCpu(renderTid, out renderCore) - renderCpuAtStart : 0;
			long processCpu = ProcessCpu() - processCpuAtStart;
			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
				"[mobile] perf: {0:F1} fps, {1} tps ({2}), frame median {3:F1} ms, 99% {4:F1}, longest {5:F0}; main {6:F0}% on cpu{7} at {8}, render {9:F0}% on cpu{10} at {11}, process {12:F0}%; gc {13}/{14}/{15} pause {16:F0} ms, alloc {18:F1} MB/s, gen0 at last gc {19:F2} MB, promoted {20:F2} MB; {17}",
				frames / seconds, ticks >= 0 ? (ticks / seconds).ToString("F0", CultureInfo.InvariantCulture) : "-", speedText,
				median * 1000f, high * 1000f, longestFrame * 1000.0,
				mainCpu / seconds, mainCore, Clock(mainCore), renderCpu / seconds, renderCore, Clock(renderCore), processCpu / seconds,
				GC.CollectionCount(0) - gen0AtStart, GC.CollectionCount(1) - gen1AtStart, GC.CollectionCount(2) - gen2AtStart,
				(GC.GetTotalPauseDuration() - pauseAtStart).TotalMilliseconds, FrameRate.Describe() + " " + FrameRate.DescribeWait() + " " + Sched.Describe() + "; scene " + sceneCount + " for " + (sceneCount > 0 ? now - sceneSetAt : 0).ToString("F1", CultureInfo.InvariantCulture) + " s",
				(GC.GetTotalAllocatedBytes(false) - allocatedAtStart) / 1048576.0 / seconds, LastGen0Before() / 1048576.0, GC.GetGCMemoryInfo(GCKind.Ephemeral).PromotedBytes / 1048576.0));
		}

		private static long LastGen0Before()
		{
			try
			{
				ReadOnlySpan<GCGenerationInfo> generations = GC.GetGCMemoryInfo(GCKind.Ephemeral).GenerationInfo;
				return generations.Length > 0 ? generations[0].SizeBeforeBytes : 0;
			}
			catch (Exception)
			{
				return 0;
			}
		}

		/// <summary>The clock of a core and the limit the phone holds it to at the moment, in MHz ("2534/3014").</summary>
		private static string Clock(int core)
		{
			if (core < 0)
			{
				return "?";
			}
			try
			{
				string dir = "/sys/devices/system/cpu/cpu" + core + "/cpufreq/";
				return int.Parse(File.ReadAllText(dir + "scaling_cur_freq").Trim()) / 1000 + "/" + int.Parse(File.ReadAllText(dir + "scaling_max_freq").Trim()) / 1000;
			}
			catch (Exception)
			{
				return "?";
			}
		}

		/// <summary>CPU time of a thread of this process in clock ticks (10 ms), and the core it last ran on.</summary>
		internal static long ThreadCpu(int tid, out int core)
		{
			core = -1;
			try
			{
				string stat = File.ReadAllText("/proc/self/task/" + tid + "/stat");
				string[] fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
				core = int.Parse(fields[36]);
				return long.Parse(fields[11]) + long.Parse(fields[12]);
			}
			catch (Exception)
			{
				return 0;
			}
		}

		/// <summary>CPU time of the whole process in clock ticks (10 ms).</summary>
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

		/// <summary>The thread of that name that has used the most CPU time (the system has threads of its own with the same names).</summary>
		internal static int FindThread(string name)
		{
			int best = 0;
			long bestCpu = -1;
			try
			{
				foreach (string dir in Directory.GetDirectories("/proc/self/task"))
				{
					string comm = File.ReadAllText(dir + "/comm").Trim();
					if (comm != name)
					{
						continue;
					}
					int tid = int.Parse(Path.GetFileName(dir));
					long cpu = ThreadCpu(tid, out _);
					if (cpu > bestCpu)
					{
						bestCpu = cpu;
						best = tid;
					}
				}
			}
			catch (Exception)
			{
			}
			return best;
		}

		/// <summary>
		/// "raid=NAME:POINTS" in perfcmd.txt: an enemy raid on the current map by the first hostile faction whose def name
		/// or label contains NAME ("any": whichever the game picks), for seeing the mods' weapons and fighters at work.
		/// </summary>
		private static void Raid(string what)
		{
			try
			{
				string[] parts = what.Split(':');
				float points = parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : 2000f;
				Map map = Find.CurrentMap;
				Faction faction = null;
				if (parts[0] != "any")
				{
					foreach (Faction candidate in Find.FactionManager.AllFactions)
					{
						if (candidate.def.defName.IndexOf(parts[0], StringComparison.OrdinalIgnoreCase) >= 0 || (candidate.Name ?? "").IndexOf(parts[0], StringComparison.OrdinalIgnoreCase) >= 0)
						{
							faction = candidate;
							break;
						}
					}
					if (faction == null)
					{
						Console.WriteLine("[mobile] perf: no faction like '" + parts[0] + "'; the factions: " + string.Join(", ", System.Linq.Enumerable.Select(Find.FactionManager.AllFactions, f => f.def.defName)));
						return;
					}
					if (!faction.HostileTo(Faction.OfPlayer))
					{
						faction.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Hostile, false);
					}
				}
				IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
				parms.points = points;
				parms.faction = faction;
				parms.forced = true;
				bool done = IncidentDefOf.RaidEnemy.Worker.TryExecute(parms);
				Console.WriteLine("[mobile] perf: raid of " + points + " points by " + (parms.faction?.def.defName ?? "?") + ": " + (done ? "started, " + map.mapPawns.AllPawnsSpawnedCount + " pawns on the map now" : "refused"));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] perf: raid failed: " + e);
			}
		}

		/// <summary>
		/// "abilities=NAME" in perfcmd.txt: for the ability defs whose class name contains NAME, what CanCast says for a
		/// colonist who does not have the ability (it is made for the question, not given to the pawn). A mod's ability
		/// class that overrides CanCast by the signature of the game's previous version is only asked when the mod's
		/// preparation gave it the override by the new one (patch/ModPrep.cs, BridgeChangedOverrides).
		/// "abilities=NAME:CLASS" asks an ability of the class CLASS (full name) instead of the def's own: for a class
		/// that no def of the running game uses.
		/// </summary>
		private static void Abilities(string what)
		{
			try
			{
				string name = what.Split(':')[0];
				Type instead = what.Contains(":") ? AccessTools.TypeByName(what.Substring(what.IndexOf(':') + 1)) : null;
				if (what.Contains(":") && instead == null)
				{
					Console.WriteLine("[mobile] perf: no type " + what.Substring(what.IndexOf(':') + 1));
					return;
				}
				Pawn pawn = System.Linq.Enumerable.FirstOrDefault(Find.CurrentMap.mapPawns.FreeColonistsSpawned);
				int shown = 0;
				foreach (AbilityDef def in DefDatabase<AbilityDef>.AllDefs)
				{
					if (def.abilityClass == null || def.abilityClass.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}
					Type type = instead ?? def.abilityClass;
					Ability ability = instead != null ? (Ability)Activator.CreateInstance(instead, pawn, def) : AbilityUtility.MakeAbility(def, pawn);
					AcceptanceReport report = ability.CanCast;
					var getters = new List<string>();
					foreach (System.Reflection.MethodInfo method in type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
					{
						if (method.Name == "get_CanCast")
						{
							getters.Add(method.ReturnType.Name);
						}
					}
					Console.WriteLine("[mobile] perf: ability " + def.defName + " (" + type.FullName + ", level " + def.level + ", heat " + def.EntropyGain.ToString("F1", CultureInfo.InvariantCulture) + ", psyfocus " + def.PsyfocusCost.ToString("F2", CultureInfo.InvariantCulture)
						+ ") asked for " + pawn.LabelShort + " (psylink " + pawn.GetPsylinkLevel() + "): CanCast " + (report.Accepted ? "yes" : "no") + "; the class declares get_CanCast returning " + (getters.Count == 0 ? "nothing" : string.Join(" and ", getters)));
					if (++shown >= 6)
					{
						break;
					}
				}
				if (shown == 0)
				{
					Console.WriteLine("[mobile] perf: no ability class like '" + name + "'");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] perf: abilities failed: " + e);
			}
		}

		private static void ReadCommands()
		{
			try
			{
				string file = Path.Combine(AppContext.BaseDirectory, "perfcmd.txt");
				if (!File.Exists(file))
				{
					return;
				}
				DateTime written = File.GetLastWriteTimeUtc(file);
				if (written == commandsRead)
				{
					return;
				}
				commandsRead = written;
				foreach (string raw in File.ReadAllLines(file))
				{
					string line = raw.Trim();
					if (line.Length == 0 || line[0] == '#')
					{
						continue;
					}
					string[] parts = line.Split(new[] { '=' }, 2);
					if (parts[0] == "reload" && loadName != null)
					{
						// The scene from its start again: at triple speed a game day passes in three minutes, and a colony asleep is not the colony at work.
						Console.WriteLine("[mobile] perf: loading the saved game " + loadName + " again");
						GameDataSaveLoader.LoadGame(loadName);
					}
					else if (parts[0] == "load" && parts.Length == 2)
					{
						// "load=NAME": this saved game, from the main menu or from another game (and "reload" means this one from now on).
						loadName = parts[1].Trim();
						Console.WriteLine("[mobile] perf: loading the saved game " + loadName);
						GameDataSaveLoader.LoadGame(loadName);
					}
					else if (parts[0] == "menu")
					{
						// "menu=1": back to the main menu, as the game's own menu entry does it.
						Console.WriteLine("[mobile] perf: back to the main menu");
						GenScene.GoToMainMenu();
					}
					else if (parts[0] == "camera" && parts.Length == 2 && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
					{
						// "camera=SIZE" or "camera=SIZE:X:Z": the zoom, at the middle of the map or at a cell.
						string[] at = parts[1].Split(':');
						Map map = Find.CurrentMap;
						Vector3 where = at.Length >= 3 ? new IntVec3(int.Parse(at[1]), 0, int.Parse(at[2])).ToVector3Shifted() : map.Center.ToVector3Shifted();
						Find.CameraDriver.SetRootPosAndSize(where, float.Parse(at[0], CultureInfo.InvariantCulture));
					}
					else if (parts[0] == "raid" && parts.Length == 2)
					{
						Raid(parts[1].Trim());
					}
					else if (parts[0] == "abilities" && parts.Length == 2 && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
					{
						Abilities(parts[1].Trim());
					}
					else if (parts[0] == "memdump" && parts.Length == 2)
					{
						// "memdump=LABEL": what the memory consists of, to logs/memdump-LABEL.txt; "memdump=LABEL:nowalk" leaves the managed heap out.
						string[] what = parts[1].Trim().Split(':');
						MemDiag.Dump(what[0], what.Length < 2 || what[1] != "nowalk");
					}
					else if (parts[0] == "controls" && parts.Length == 2)
					{
						// "controls=LABEL": every control of the interface as it was last drawn (kind, window, left, top, width, height in pixels), to logs/controls-LABEL.txt.
						string folder = Path.Combine(AppContext.BaseDirectory, "..", "logs");
						Directory.CreateDirectory(folder);
						File.WriteAllText(Path.Combine(folder, "controls-" + parts[1].Trim() + ".txt"), TapAssist.Describe());
						Console.WriteLine("[mobile] perf: the controls of the interface written (" + parts[1].Trim() + ")");
					}
					else if (parts[0] == "evict" && parts.Length == 2)
					{
						// "evict=all", "evict=SECONDS", "evict=report": see ColdTextures.
						ColdTextures.Command(parts[1].Trim());
					}
					else if (parts[0] == "gc")
					{
						// "gc=1": one full blocking collection; "gc=compact": the same with the large objects moved together.
						MemDiag.Collect(parts.Length == 2 && parts[1].Trim() == "compact");
					}
					else if (parts[0] == "speed" && parts.Length == 2)
					{
						speed = int.Parse(parts[1]);
						if (Current.ProgramState == ProgramState.Playing)
						{
							Find.TickManager.CurTimeSpeed = (TimeSpeed)speed;
						}
					}
					else if (!FrameRate.Set(parts[0].Trim(), parts.Length == 2 ? parts[1].Trim() : "1"))
					{
						Sched.Set(parts[0].Trim(), parts.Length == 2 ? parts[1].Trim() : "1");
					}
				}
				Console.WriteLine("[mobile] perf: commands read: " + Sched.Describe());
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] perf: commands not read: " + e.Message);
			}
		}
	}
}
