using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// When the game's load fails with mods on, it switches every mod off, loads again and shows "some active mods caused
	/// errors" (PlayDataLoader.LoadAllPlayData). The exception that made it do so goes to Log.Warning, which writes nothing
	/// once the game has logged its maximum of messages; with many mods that have errors it has by then (a tester's
	/// report of 2026-10-27: the reason was lost, only the switched-off mod list was left). These warnings are copied
	/// to Port17.log whatever the game's log does.
	///
	/// The same patch has a postfix that counts the calls of Log.Warning, for the warnings of PortMobile's record
	/// (logs/record.txt), as the record's own postfix did: every call that returns, also one a mod's prefix skipped or
	/// the game's log did not write. This patch is the first of Log.Warning, put on as the game's assembly loads, before
	/// any code that could hold a copy of it is compiled: no call passes it by, whatever InlineGuard does. The record's
	/// own postfix, put on at the main menu, had InlineGuard make again every replacement that may hold Log.Warning (a
	/// lock, 83 IL bytes; 271 with the owner's 130 mods), about 200 of them for it alone, until the guard went by the
	/// order in which the replacements were made: all of them come after this patch, and the guard leaves them as they
	/// are either way. The count spares those 200 only where it does not go by the order (mobile.txt inlineGuardOrder=0).
	/// </summary>
	public static class LoadRecovery
	{
		private static bool patched;
		private static volatile bool counting;
		private static long warnings;

		/// <summary>The calls of Log.Warning so far; -1 where they are not counted (its patch failed).</summary>
		public static long Warnings => counting ? Interlocked.Read(ref warnings) : -1;

		public static void Start()
		{
			try
			{
				AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => TryPatch(args.LoadedAssembly);
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					TryPatch(assembly);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] load recovery: not watched: " + e.Message);
			}
		}

		private static void TryPatch(Assembly assembly)
		{
			if (patched || assembly.GetName().Name != "Assembly-CSharp")
			{
				return;
			}
			patched = true;
			try
			{
				MethodInfo warning = assembly.GetType("Verse.Log")?.GetMethod("Warning", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
				if (warning == null)
				{
					return;
				}
				new Harmony("port17.loadrecovery").Patch(warning, prefix: new HarmonyMethod(typeof(LoadRecovery), nameof(BeforeWarning)), postfix: new HarmonyMethod(typeof(LoadRecovery), nameof(AfterWarning)));
				counting = true;
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] load recovery: not watched: " + e.Message);
			}
		}

		public static void BeforeWarning(string text)
		{
			if (text != null && (text.StartsWith("Caught exception while loading play data", StringComparison.Ordinal)
				|| text.StartsWith("Could not recover from errors", StringComparison.Ordinal)
				|| text.StartsWith("Caught exception while recovering from errors", StringComparison.Ordinal)))
			{
				Console.WriteLine("[port17] the game's load failed; it switches the mods off and loads again: " + (text.Length > 6000 ? text.Substring(0, 6000) + " …" : text));
			}
		}

		// A postfix, not the prefix above: once an earlier prefix has skipped the original, Harmony leaves out a later one
		// that takes an object of the original's (the text, here), and it still runs every postfix (measured with a mod's
		// prefix that keeps some warnings out of the log).
		public static void AfterWarning()
		{
			Interlocked.Increment(ref warnings);
		}
	}
}
