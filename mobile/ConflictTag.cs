using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// An error whose stack goes through both the port and a mod is marked in the record ("conflict" lines in
	/// logs/record.txt, and the log): which of the two threw, the port's part, and the mods by name. The user, 2026-10-27:
	/// with hundreds of mods a tester's log has thousands of errors, and the ones where the port and a mod meet were found
	/// by reading stacks by hand. The stack is what the error's text holds: its frames (the port's PortMobile and
	/// PortHooks, a mod's by the assembly its type comes from) and the patches Harmony lists on the methods in it
	/// ("- PREFIX owner: ...") on the method that threw and the two below it. An error of a mod alone, or of the port
	/// alone, is not marked. Once per kind of error. mobile.txt conflictTagTest=1 checks three errors from testers' logs at
	/// the start and logs what they give.
	/// </summary>
	internal static class ConflictTag
	{
		private const int MaxTagged = 200;
		private static readonly Regex Frame = new Regex(@"^\s*at \.?([A-Za-z_][\w`]*(?:[.+][\w`<>]+)*?)\.([\w<>`|$]+)(?:\[[^\]]*\])?\(", RegexOptions.Compiled);
		private static readonly Regex PatchLine = new Regex(@"^\s*- (PREFIX|POSTFIX|TRANSPILER|FINALIZER|ILMANIPULATOR) ([^:]+): ", RegexOptions.Compiled);
		// Namespaces of the game, the engine, the runtime and the port's own tools: not a mod's.
		private static readonly string[] Base = { "Verse", "RimWorld", "LudeonTK", "UnityEngine", "Unity", "UnityStubs", "Waylith", "RimWorldWaylith", "Brutal", "System", "Microsoft", "Mono", "MonoMod", "HarmonyLib", "Harmony", "TMPro", "Steamworks", "Internal", "Interop", "InvokeStub_", "DMD", "<>" };
		// The window and frame loops: patched for every window and every frame.
		private static readonly string[] Hubs = { "Verse.Window.", "Verse.WindowStack.", "Verse.ImmediateWindow.", "UnityEngine.GUI.", "Verse.Root", "Verse.UIRoot", "RimWorld.UIRoot_", "RimWorldWaylith.Imgui.ImguiDispatch." };
		// Harmony's own ids (it lists its exception handler on many methods).
		private static readonly string[] HarmonyOwners = { "net.pardeike.rimworld.lib.harmony", "brrainz.harmony" };
		private static readonly HashSet<string> seen = new HashSet<string>();
		private static Dictionary<Assembly, string> modOfAssembly;
		private static int tagged;

		/// <summary>From Recorder, for an error seen the first time: the line for the record, or null.</summary>
		public static string Check(string text)
		{
			if (text == null || text.IndexOf('\n') < 0 || tagged >= MaxTagged)
			{
				return null;
			}
			try
			{
				var portParts = new List<string>();
				var modNames = new List<string>();
				var frames = new List<string>();
				var patches = new List<(string owner, string target)>();
				string thrower = null;
				bool throwerPort = false;
				foreach (string raw in text.Split('\n'))
				{
					string line = raw.TrimEnd('\r');
					Match patch = PatchLine.Match(line);
					if (patch.Success)
					{
						int arrow = line.IndexOf(": ", patch.Index + patch.Length - 2, StringComparison.Ordinal);
						int back = line.LastIndexOf(" <- ", StringComparison.Ordinal);
						string target = arrow > 0 && back > arrow ? line.Substring(arrow + 2, back - arrow - 2).Trim() : "";
						patches.Add((patch.Groups[2].Value.Trim(), target));
						continue;
					}
					Match frame = Frame.Match(line);
					if (!frame.Success)
					{
						continue;
					}
					string type = frame.Groups[1].Value, method = Regex.Replace(frame.Groups[2].Value, @"_Patch[0-9]+$", "");
					bool port = type.StartsWith("PortMobile.", StringComparison.Ordinal) || type.StartsWith("PortHooks.", StringComparison.Ordinal);
					frames.Add(type + "." + method);
					if (thrower == null)
					{
						thrower = type + "." + method;
						throwerPort = port;
					}
					if (port)
					{
						AddOnce(portParts, type + "." + method);
					}
					else if (ModOfType(type) is string mod)
					{
						AddOnce(modNames, mod);
					}
				}
				// A patch counts on the method that threw and the few that called it, but not on the window and frame loops
				// (the port, and many mods, patch those for every window: they would mark every error drawn in one).
				var near = new HashSet<string>(frames.Take(6).Where(f => !Hubs.Any(h => f.StartsWith(h, StringComparison.Ordinal))));
				// A mod's type the message itself names ("Unable to cast object of type 'Mod.Type'").
				foreach (Match quoted in Regex.Matches(text.Split('\n')[0], @"'([A-Za-z_][\w.+`]*)'"))
				{
					if (quoted.Groups[1].Value.Contains(".") && ModOfType(quoted.Groups[1].Value) is string named)
					{
						AddOnce(modNames, named);
					}
				}
				foreach (var (owner, target) in patches)
				{
					if (!near.Contains(target))
					{
						continue;
					}
					if (owner.StartsWith("port17.", StringComparison.Ordinal))
					{
						AddOnce(portParts, "a patch of " + target);
					}
					else if (!HarmonyOwners.Contains(owner))
					{
						AddOnce(modNames, ModByOwner(owner));
					}
				}
				if (portParts.Count == 0 || modNames.Count == 0)
				{
					return null;
				}
				string first = Recorder.Short(text.Split('\n')[0].Trim(), 120);
				string key = (throwerPort ? "p:" : "m:") + string.Join(",", modNames) + "|" + string.Join(",", portParts.Take(3));
				lock (seen)
				{
					if (!seen.Add(key))
					{
						return null;
					}
				}
				tagged++;
				return (throwerPort ? "the port threw (" + thrower + ")" : "a mod or the game threw (" + thrower + ")")
					+ "; the port: " + string.Join(", ", portParts.Take(4)) + "; mods: " + string.Join(", ", modNames.Take(6)) + " | " + first;
			}
			catch (Exception e)
			{
				return "not read: " + e.Message;
			}
		}

		/// <summary>mobile.txt conflictTagTest=1: three errors from testers' logs (port and mod, mod and port, a mod alone) through Check.</summary>
		public static void SelfTest()
		{
			if (Settings.Get("conflictTagTest", 0) != 1)
			{
				return;
			}
			string[] samples =
			{
				@"Root level exception in OnGUI(): System.InvalidCastException: Unable to cast object of type 'RimworldReadableNumbers.RnGuiBackend' to type 'RimWorldWaylith.Imgui.ImGuiGuiBackend'.
[Ref 84ABDAE0]
   at __refget_Object_fi_rotated(Object )
   at PortMobile.TapAssist.OnScreen(Rect rect, Boolean drawingOnly, Object& backend, Single& x0, Single& y0, Single& x1, Single& y1)
   at PortMobile.TapAssist.NoteText(Rect rect, String text)
   at RimWorldWaylith.Imgui.ImGuiGuiBackend.DrawText_Patch1(ImGuiGuiBackend this, Rect rect, String text)
   at UnityEngine.GUIStyle.DrawTextIn(IGuiBackend be, Rect content, Rect position, String text, Color color, TextAnchor anchor)
   at RimWorld.VersionControl.DrawInfoInCorner_Patch2()
   at RimWorld.MainMenuDrawer.MainMenuOnGUI_Patch2()
    - PREFIX port17.mobile.touch: RimWorldWaylith.Imgui.ImGuiGuiBackend.DrawText <- Void PortMobile.TapAssist:NoteText(Rect rect, String text)
    - PREFIX 7h0m4s.RimworldReadableNumbers: RimWorld.VersionControl.DrawInfoInCorner <- Boolean RimworldReadableNumbers.Patches.Prefix()",
				@"Exception filling window for RimWorld.Dialog_Options: System.AccessViolationException: Attempted to read or write protected memory.
[Ref CE0D153B]
   at ShellaBackgrounds.BackgroundsUtility.AddExtraBackgroundOptions(List`1& options)
   at RimWorld.Dialog_Options.DoUIOptions_Patch4(Dialog_Options this, Listing_Standard listing)
   at RimWorld.Dialog_Options.DoOptions(OptionCategoryDef category, Rect inRect)
   at RimWorld.Dialog_Options.DoWindowContents(Rect inRect)
   at Verse.Window.InnerWindowOnGUI_Patch2(Window this, Int32 x)
    - TRANSPILER Stm.ShellaBackgrounds: RimWorld.Dialog_Options.DoUIOptions <- IEnumerable`1 ShellaBackgrounds.Patch_Dialog_Options:AddBackgroundOptions(IEnumerable`1 instructions)
    - TRANSPILER port17.mobile: RimWorld.Dialog_Options.DoUIOptions <- IEnumerable`1 PortMobile.Counters:RowAfterClock(IEnumerable`1 instructions)
    - PREFIX port17.mobile: Verse.Window.InnerWindowOnGUI <- Void PortMobile.Cutout:BeforeWindowInside(Window __instance)",
				@"Exception filling window for SomeMod.SomeWindow: System.NullReferenceException: Object reference not set to an instance of an object.
[Ref 11111111]
   at SomeMod.SomeWindow.DoWindowContents(Rect inRect)
   at Verse.Window.InnerWindowOnGUI_Patch2(Window this, Int32 x)
    - PREFIX port17.mobile: Verse.Window.InnerWindowOnGUI <- Void PortMobile.Cutout:BeforeWindowInside(Window __instance)
    - PREFIX port17.mobile.touch: Verse.Window.InnerWindowOnGUI <- Void PortMobile.GizmoFit:BeforeWindowInside(Window __instance, Single& __state)",
			};
			foreach (string sample in samples)
			{
				Console.WriteLine("[mobile] conflict tag test: " + (Check(sample) ?? "(not marked)"));
			}
		}

		private static void AddOnce(List<string> list, string item)
		{
			if (item != null && !list.Contains(item))
			{
				list.Add(item);
			}
		}

		/// <summary>A frame's type: the name of the mod whose assembly has it, or null (the game, the engine, the runtime).</summary>
		private static string ModOfType(string type)
		{
			string root = type.Split('.', '+')[0];
			if (Base.Any(b => root == b || root.StartsWith(b, StringComparison.Ordinal) && b.EndsWith("_", StringComparison.Ordinal)))
			{
				return null;
			}
			Type found = AccessTools.TypeByName(type.Replace('+', '.')) ?? AccessTools.TypeByName(type);
			if (found == null)
			{
				int plus = type.IndexOf('+');
				found = plus > 0 ? AccessTools.TypeByName(type.Substring(0, plus)) : null;
			}
			if (found == null)
			{
				return root;
			}
			return ModOfAssembly(found.Assembly) ?? (IsGame(found.Assembly) ? null : found.Assembly.GetName().Name);
		}

		private static bool IsGame(Assembly assembly)
		{
			string name = assembly.GetName().Name;
			return name == "Assembly-CSharp" || name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Waylith", StringComparison.Ordinal) || name == "RimWorld" || name == "mscorlib" || name == "netstandard";
		}

		private static string ModOfAssembly(Assembly assembly)
		{
			if (modOfAssembly == null)
			{
				var map = new Dictionary<Assembly, string>();
				foreach (ModContentPack mod in LoadedModManager.RunningMods)
				{
					foreach (Assembly a in mod.assemblies.loadedAssemblies)
					{
						map[a] = mod.Name;
					}
				}
				modOfAssembly = map;
			}
			return modOfAssembly.TryGetValue(assembly, out string name) ? name : null;
		}

		/// <summary>A Harmony id: the mod whose package id it is, else the id itself.</summary>
		private static string ModByOwner(string owner)
		{
			foreach (ModContentPack mod in LoadedModManager.RunningMods)
			{
				if (string.Equals(mod.PackageId, owner, StringComparison.OrdinalIgnoreCase) || string.Equals(mod.PackageIdPlayerFacing, owner, StringComparison.OrdinalIgnoreCase))
				{
					return mod.Name;
				}
			}
			return owner;
		}
	}
}
