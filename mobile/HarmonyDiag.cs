using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// For a patch of a mod that fails on the phone ("Common Language Runtime detected an invalid program"): applies the
	/// patch once more, by itself, with Harmony's debug listing on, so that the code Harmony generated for the patched
	/// method can be read. Runs when harmony-diag.txt lies next to the game; each line is
	///     prefix|postfix|transpiler|finalizer  Namespace.Type:Method  Namespace.PatchType:Method
	/// (the original first, then the patch method; an original with overloads takes its parameter types in parentheses,
	/// "Type:Method(System.Int32,Verse.Pawn)"). The listing goes to files/logs/harmony-diag.log, the outcome of each line
	/// to the game's log. The file is renamed when done.
	/// </summary>
	internal static class HarmonyDiag
	{
		public static void Start()
		{
			string list = Path.Combine(AppContext.BaseDirectory, "harmony-diag.txt");
			if (!File.Exists(list))
			{
				return;
			}
			LongEventHandler.ExecuteWhenFinished(delegate
			{
				string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs", "harmony-diag.log"));
				StreamWriter writer = null;
				bool wasDebug = Harmony.DEBUG;
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(output));
					writer = new StreamWriter(output, false) { AutoFlush = true };
					FileLog.LogWriter = writer;
					Harmony.DEBUG = true;
					int n = 0;
					foreach (string raw in File.ReadAllLines(list))
					{
						string line = raw.Trim();
						if (line.Length == 0 || line[0] == '#')
						{
							continue;
						}
						n++;
						writer.WriteLine("######## " + line);
						Console.WriteLine("[mobile] harmony diag " + n + ": " + line + " -> " + Try(line, n));
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("[mobile] harmony diag failed: " + e);
				}
				finally
				{
					Harmony.DEBUG = wasDebug;
					try
					{
						FileLog.FlushBuffer();
					}
					catch (Exception)
					{
					}
					FileLog.LogWriter = null;
					writer?.Dispose();
				}
				try
				{
					File.Delete(list + ".done");
					File.Move(list, list + ".done");
				}
				catch (Exception)
				{
				}
			});
		}

		/// <summary>A transpiler that changes nothing: "transpiler Type:Method PortMobile.HarmonyDiag:Identity" tells whether Harmony can regenerate a method at all.</summary>
		public static System.Collections.Generic.IEnumerable<CodeInstruction> Identity(System.Collections.Generic.IEnumerable<CodeInstruction> instructions)
		{
			return instructions;
		}

		private static MethodBase Original(string name)
		{
			int open = name.IndexOf('(');
			if (open < 0)
			{
				return AccessTools.Method(name);
			}
			string[] typeNames = name.Substring(open + 1).TrimEnd(')').Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
			var types = new Type[typeNames.Length];
			for (int i = 0; i < types.Length; i++)
			{
				types[i] = AccessTools.TypeByName(typeNames[i].Trim());
			}
			return AccessTools.Method(name.Substring(0, open), types);
		}

		private static string Try(string line, int n)
		{
			string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 2 && parts[0] == "ctor")
			{
				// "ctor Namespace.ModClass": the constructor of a mod's Mod class once more (a mod that patches there and failed).
				Type modClass = AccessTools.TypeByName(parts[1]);
				if (modClass == null)
				{
					return "class not found";
				}
				ModContentPack pack = null;
				foreach (ModContentPack running in LoadedModManager.RunningModsListForReading)
				{
					if (running.assemblies != null && running.assemblies.loadedAssemblies.Contains(modClass.Assembly))
					{
						pack = running;
					}
				}
				try
				{
					Activator.CreateInstance(modClass, pack);
					return "constructed";
				}
				catch (Exception e)
				{
					Exception inner = e;
					while (inner.InnerException != null)
					{
						inner = inner.InnerException;
					}
					return "FAILED: " + inner.GetType().Name + ": " + inner.Message;
				}
			}
			if (parts.Length < 3)
			{
				return "not understood";
			}
			MethodBase original;
			MethodInfo patch;
			try
			{
				original = Original(parts[1]);
				patch = AccessTools.Method(parts[2]);
			}
			catch (Exception e)
			{
				return "not found: " + e.Message;
			}
			if (original == null || patch == null)
			{
				return (original == null ? "original not found" : "") + (patch == null ? " patch method not found" : "");
			}
			var method = new HarmonyMethod(patch);
			try
			{
				var harmony = new Harmony("port17.diag." + n);
				switch (parts[0])
				{
					case "prefix": harmony.Patch(original, prefix: method); break;
					case "postfix": harmony.Patch(original, postfix: method); break;
					case "transpiler": harmony.Patch(original, transpiler: method); break;
					case "finalizer": harmony.Patch(original, finalizer: method); break;
					default: return "unknown kind " + parts[0];
				}
				return "applied";
			}
			catch (Exception e)
			{
				Exception inner = e;
				while (inner.InnerException != null)
				{
					inner = inner.InnerException;
				}
				return "FAILED: " + inner.GetType().Name + ": " + inner.Message;
			}
		}
	}
}
