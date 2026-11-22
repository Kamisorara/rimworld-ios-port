using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// A test of the races mods bring (Humanoid Alien Races and the like), run when spawntest.txt lies next to the game:
	/// once a map is there (start the game with -quicktest), a colonist of every humanlike race that is not the game's
	/// own is generated and put down around the middle of the map, and the camera goes there. Each line of the file is
	/// a part of a race's defName or of its mod's package id that limits the test to those; a line with a number is how
	/// many of each race (1 when there is none). What was generated, and what failed with which exception, goes to the
	/// log. The file stays: the test runs at every start of a map while it is there.
	/// </summary>
	internal static class SpawnDiag
	{
		private static string[] filters = new string[0];
		private static int each = 1;
		private static string saveAs;

		public static void Start(Harmony harmony)
		{
			string list = Path.Combine(AppContext.BaseDirectory, "spawntest.txt");
			if (!File.Exists(list))
			{
				return;
			}
			try
			{
				var wanted = new List<string>();
				foreach (string raw in File.ReadAllLines(list))
				{
					string line = raw.Trim();
					if (line.Length == 0 || line[0] == '#')
					{
						continue;
					}
					if (line.StartsWith("save ", StringComparison.OrdinalIgnoreCase))
					{
						// "save NAME": the game with the pawns in it is saved under this name (a scene for mobile/Perf.cs to load).
						saveAs = line.Substring(5).Trim();
					}
					else if (int.TryParse(line, out int n))
					{
						each = Math.Max(1, Math.Min(20, n));
					}
					else
					{
						wanted.Add(line);
					}
				}
				filters = wanted.ToArray();
				harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)), postfix: new HarmonyMethod(typeof(SpawnDiag), nameof(AfterGameInit)));
				Console.WriteLine("[mobile] spawn test: armed (" + each + " of each race" + (filters.Length > 0 ? ", only " + string.Join(", ", filters) : "") + ")");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] spawn test not installed: " + e);
			}
		}

		public static void AfterGameInit()
		{
			LongEventHandler.ExecuteWhenFinished(delegate
			{
				try
				{
					Run();
				}
				catch (Exception e)
				{
					Console.WriteLine("[mobile] spawn test failed: " + e);
				}
			});
		}

		private static bool Wanted(ThingDef race)
		{
			if (filters.Length == 0)
			{
				return true;
			}
			string id = race.modContentPack?.PackageId ?? "";
			return filters.Any(f => race.defName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
		}

		private static void Run()
		{
			Map map = Find.CurrentMap;
			if (map == null)
			{
				Console.WriteLine("[mobile] spawn test: no map");
				return;
			}
			List<ThingDef> races = DefDatabase<ThingDef>.AllDefsListForReading
				.Where(d => d.race != null && d.race.Humanlike && d.modContentPack != null && !d.modContentPack.IsOfficialMod && Wanted(d))
				.OrderBy(d => d.modContentPack.PackageId).ThenBy(d => d.defName).ToList();
			Console.WriteLine("[mobile] spawn test: " + races.Count + " humanlike races from mods");
			IntVec3 center = map.Center;
			int made = 0, failed = 0, index = 0;
			foreach (ThingDef race in races)
			{
				// The kind a player's own pawn of the race would have, when the mod has one; any kind of the race otherwise.
				List<PawnKindDef> kinds = DefDatabase<PawnKindDef>.AllDefsListForReading.Where(k => k.race == race).ToList();
				PawnKindDef kind = kinds.FirstOrDefault(k => k.defaultFactionDef != null && k.defaultFactionDef.isPlayer) ?? kinds.FirstOrDefault(k => k.defaultFactionDef == null) ?? kinds.FirstOrDefault();
				string label = race.defName + " (" + race.modContentPack.Name + ")";
				if (kind == null)
				{
					Console.WriteLine("[mobile] spawn test: " + label + ": no pawn kind of this race");
					continue;
				}
				for (int i = 0; i < each; i++)
				{
					try
					{
						// The game's own account of how it dressed the pawn goes to the log ("Generating apparel for ...").
						DebugViewSettings.logApparelGeneration = true;
						Pawn pawn;
						try
						{
							pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true));
						}
						finally
						{
							DebugViewSettings.logApparelGeneration = false;
						}
						// A kind that asks for one garment of one material stays naked in the test's faction when that
						// faction (the standard player colony: synthread only) does not allow the material; the garment
						// is put on here, so that it is seen at all.
						string dressed = "";
						if (pawn.apparel != null && pawn.apparel.WornApparelCount == 0 && kind.specificApparelRequirements != null)
						{
							foreach (SpecificApparelRequirement wanted0 in kind.specificApparelRequirements)
							{
								if (wanted0.ApparelDef == null)
								{
									continue;
								}
								ThingDef stuff = wanted0.ApparelDef.MadeFromStuff ? (wanted0.Stuff ?? GenStuff.DefaultStuffFor(wanted0.ApparelDef)) : null;
								pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(wanted0.ApparelDef, stuff), false);
								dressed += " [put on by the test: " + wanted0.ApparelDef.defName + (stuff != null ? "/" + stuff.defName : "") + "]";
							}
						}
						// In rows around the middle, three cells apart, so that every one can be told from its neighbours.
						IntVec3 wanted = center + new IntVec3((index % 8 - 4) * 3, 0, (index / 8 - 1) * 3);
						index++;
						if (!CellFinder.TryFindRandomCellNear(wanted, map, 2, c => c.Standable(map) && !c.Fogged(map), out IntVec3 cell))
						{
							cell = wanted;
						}
						GenSpawn.Spawn(pawn, cell, map);
						made++;
						Console.WriteLine("[mobile] spawn test: " + label + ": " + kind.defName + " -> " + pawn.LabelShortCap + " (" + pawn.gender + ", " + pawn.ageTracker.AgeBiologicalYears + ") at " + cell
							+ "; wears: " + (pawn.apparel == null || pawn.apparel.WornApparelCount == 0 ? "nothing" : string.Join(", ", pawn.apparel.WornApparel.Select(a => a.def.defName + (a.Stuff != null ? "/" + a.Stuff.defName : ""))))
							+ "; holds: " + (pawn.equipment?.Primary?.def.defName ?? "nothing") + dressed);
					}
					catch (Exception e)
					{
						failed++;
						Exception inner = e;
						while (inner.InnerException != null)
						{
							inner = inner.InnerException;
						}
						Console.WriteLine("[mobile] spawn test: " + label + ": " + kind.defName + " FAILED: " + inner.GetType().Name + ": " + inner.Message + "\n" + inner.StackTrace);
					}
				}
			}
			Console.WriteLine("[mobile] spawn test: " + made + " pawns placed, " + failed + " failed");
			if (!string.IsNullOrEmpty(saveAs))
			{
				try
				{
					GameDataSaveLoader.SaveGame(saveAs);
					Console.WriteLine("[mobile] spawn test: saved as " + saveAs);
				}
				catch (Exception e)
				{
					Console.WriteLine("[mobile] spawn test: not saved: " + e);
				}
			}
			try
			{
				Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 16f);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] spawn test: camera not moved: " + e.Message);
			}
		}
	}
}
