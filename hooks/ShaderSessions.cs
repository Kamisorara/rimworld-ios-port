using System;
using System.Reflection;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// The engine compiles the shaders mods bring as source with Slang (Waylith.Rendering.RuntimeShaderCompiler), on the
	/// phone with Slang 2026.19 itself since 2026-10-29, as on the PC. A Slang global session holds Slang's core module:
	/// 215 MB right after it is made on the phone, 256 MB after 372 shaders (measured with the corpus of the game's and
	/// three mods' shaders). The engine keeps one session per compile slot, ProcessorCount / 2 of them (4 on an 8-core
	/// phone), for as long as the game runs; a PC has the memory for that, a phone with a long mod list does not. So on
	/// the phone: one slot (the engine's own SetMaxConcurrentSessions), and the idle session let go 10 s after the last
	/// compile (the next compile makes a new one, 0.19 s). The SPIR-V is the same either way.
	/// </summary>
	public static class ShaderSessions
	{
		private const long IdleMs = 10000;

		private static object availableSessions;
		private static MethodInfo tryTake;
		private static long lastCompile;
		private static Timer timer;

		public static void Start(Harmony harmony)
		{
			try
			{
				Type compiler = Type.GetType("Waylith.Rendering.RuntimeShaderCompiler, Waylith");
				MethodInfo setMax = compiler?.GetMethod("SetMaxConcurrentSessions", BindingFlags.Public | BindingFlags.Static);
				FieldInfo bag = compiler?.GetField("AvailableSessions", BindingFlags.NonPublic | BindingFlags.Static);
				MethodInfo compile = compiler?.GetMethod("CompileEntryPoints", BindingFlags.NonPublic | BindingFlags.Static);
				if (setMax == null || bag == null || compile == null)
				{
					Console.WriteLine("[port17] shader sessions: the engine's session pool is not where it was (" + (setMax == null ? "SetMaxConcurrentSessions " : "") + (bag == null ? "AvailableSessions " : "") + (compile == null ? "CompileEntryPoints" : "") + "): left as it is");
					return;
				}
				setMax.Invoke(null, new object[] { 1 });
				availableSessions = bag.GetValue(null);
				tryTake = availableSessions.GetType().GetMethod("TryTake");
				harmony.Patch(compile, finalizer: new HarmonyMethod(typeof(ShaderSessions), nameof(AfterCompile)));
				timer = new Timer(LetGoIdle, null, 5000, 5000);
				Console.WriteLine("[port17] shader sessions: one Slang session at a time, let go after " + IdleMs / 1000 + " s without a compile");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] shader sessions: " + e.GetType().Name + ": " + e.Message);
			}
		}

		public static void AfterCompile()
		{
			Interlocked.Exchange(ref lastCompile, Environment.TickCount64);
		}

		private static void LetGoIdle(object state)
		{
			try
			{
				if (((System.Collections.ICollection)availableSessions).Count == 0 || Environment.TickCount64 - Interlocked.Read(ref lastCompile) < IdleMs)
				{
					return;
				}
				int count = 0;
				object[] args = new object[1];
				while ((bool)tryTake.Invoke(availableSessions, args))
				{
					// The engine's sessions are COM objects of Brutal.Slang's generated wrappers: FinalRelease frees Slang's
					// session now rather than when the collector finalizes the wrapper.
					(args[0] as ComObject)?.FinalRelease();
					args[0] = null;
					count++;
				}
				if (count > 0)
				{
					Console.WriteLine("[port17] shader sessions: " + count + " idle Slang session" + (count == 1 ? "" : "s") + " let go (about 250 MB each)");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] shader sessions: letting go failed: " + e.GetType().Name + ": " + e.Message);
			}
		}
	}
}
