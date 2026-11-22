using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PortMobile
{
	/// <summary>
	/// At the main menu, once: how many textures and sounds each running mod's content holder found and how many of
	/// them came out empty (a path whose file could not be read: drawn red, or no sound), the official ones each, the
	/// others summed with the worst five named, and the asset packs mounted. Written for a tablet whose second start
	/// after a reboot lost every texture, vanilla's too, and its sounds (2026-10-27); its game log had grown to 372 MB and
	/// the report kept only its end, so it was not known where the content went. Port17.log is small and kept whole.
	/// </summary>
	internal static class ContentCheck
	{
		public static void Run()
		{
			try
			{
				var others = new List<(string Name, int Textures, int EmptyTextures, int Sounds, int EmptySounds)>();
				int textures = 0, emptyTextures = 0, sounds = 0, emptySounds = 0;
				foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
				{
					Count(mod.GetContentHolder<Texture2D>()?.contentList, out int t, out int te);
					Count(mod.GetContentHolder<AudioClip>()?.contentList, out int s, out int se);
					if (mod.IsCoreMod)
					{
						// Core's own holders stay empty in 1.7: its textures and sounds come from the game's asset pack, as base resources.
						Console.WriteLine("[mobile] content: Core (" + mod.RootDir + "), from the game's asset pack: " + BaseResources());
					}
					else if (mod.IsOfficialMod)
					{
						Console.WriteLine("[mobile] content: " + mod.Name + " (" + mod.RootDir + "): " + t + " textures" + (te > 0 ? " (" + te + " empty)" : "") + ", " + s + " sounds" + (se > 0 ? " (" + se + " empty)" : ""));
					}
					else
					{
						others.Add((mod.Name, t, te, s, se));
						textures += t;
						emptyTextures += te;
						sounds += s;
						emptySounds += se;
					}
				}
				string worst = string.Join("; ", others.Where(o => o.EmptyTextures + o.EmptySounds > 0).OrderByDescending(o => o.EmptyTextures + o.EmptySounds).Take(5)
					.Select(o => o.Name + " " + o.EmptyTextures + "/" + o.Textures + " textures, " + o.EmptySounds + "/" + o.Sounds + " sounds empty"));
				Console.WriteLine("[mobile] content: " + others.Count + " other mods: " + textures + " textures" + (emptyTextures > 0 ? " (" + emptyTextures + " empty)" : "") + ", " + sounds + " sounds" + (emptySounds > 0 ? " (" + emptySounds + " empty)" : "")
					+ (worst.Length > 0 ? "; most empty: " + worst : ""));
				Console.WriteLine("[mobile] content: asset packs " + Packs());
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] content check failed: " + e.GetType().Name + ": " + e.Message);
			}
		}

		/// <summary>A few of Core's textures (the folders the tablet's bad starts could not find) and its sounds' grains.</summary>
		private static string BaseResources()
		{
			var parts = new List<string>();
			foreach (string folder in new[] { "Things/Item/Drug/Beer", "Things/Item/Resource/WoodLog", "Things/Building/Furniture/Bed" })
			{
				int found = ContentFinder<Texture2D>.GetAllInFolder(folder).Count(x => x != null && x != BaseContent.BadTex);
				parts.Add(folder + " " + found);
			}
			int defs = 0, silent = 0;
			foreach (SoundDef sound in DefDatabase<SoundDef>.AllDefsListForReading)
			{
				if (sound.modContentPack == null || !sound.modContentPack.IsCoreMod || sound.subSounds == null)
				{
					continue;
				}
				foreach (SubSoundDef sub in sound.subSounds)
				{
					defs++;
					if (sub.grains != null && sub.grains.Count > 0 && !sub.grains.Any(g => g.GetResolvedGrains().Any()))
					{
						silent++;
					}
				}
			}
			parts.Add(defs + " sound parts, " + silent + " without a sound file");
			return string.Join(", ", parts);
		}

		private static void Count(IDictionary list, out int all, out int empty)
		{
			all = empty = 0;
			if (list == null)
			{
				return;
			}
			foreach (DictionaryEntry entry in list)
			{
				all++;
				if (entry.Value == null || (entry.Value is UnityEngine.Object o && (o == null || o == BaseContent.BadTex)))
				{
					empty++;
				}
			}
		}

		/// <summary>Waylith.Assets.ContentPack.Mounts: each pack's root and how many entries it shows (its manifest's).</summary>
		private static string Packs()
		{
			Type contentPack = typeof(Waylith.Rendering.GpuTextureService).Assembly.GetType("Waylith.Assets.ContentPack");
			if (!(contentPack?.GetProperty("Mounts", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is IEnumerable mounts))
			{
				return "(not readable)";
			}
			var parts = new List<string>();
			string game = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory));
			foreach (object mount in mounts)
			{
				string root = mount.GetType().GetProperty("Root")?.GetValue(mount) as string ?? "?";
				object pack = mount.GetType().GetProperty("Pack")?.GetValue(mount);
				int shown = (pack?.GetType().GetProperty("ContentHashes")?.GetValue(pack) as ICollection)?.Count ?? -1;
				parts.Add((root.StartsWith(game, StringComparison.Ordinal) ? root.Substring(game.Length).TrimStart('/') : root) + " " + shown);
			}
			// The game's own pack (not below Mods) first: the mounts are ordered longest root first, which puts it last.
			parts = parts.OrderBy(p => p.StartsWith("Mods/", StringComparison.Ordinal) ? 1 : 0).ToList();
			return parts.Count + ": " + string.Join(", ", parts.Take(12)) + (parts.Count > 12 ? ", …" : "");
		}
	}
}
