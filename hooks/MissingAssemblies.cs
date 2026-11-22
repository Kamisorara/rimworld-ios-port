using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// A reference to an assembly that is not there (a mod's optional integration with a mod that is not installed) fails
	/// on the PC with TypeLoadException, on this runtime with FileNotFoundException (WaylithClr.MissingAssemblyTypeLoadException,
	/// measured on the PC 2026-10-27). Mods written for Mono catch TypeLoadException around such calls; here the
	/// exception went past their catch and their constructor or patch failed. When nothing (no handler of the game's or a
	/// mod's) resolves such a reference, an empty assembly of that name and version stands in, and the type looked for in
	/// it fails with TypeLoadException, word for word as on the PC ("Could not load type 'X' from assembly 'Y'"). Only for
	/// references the runtime follows itself: an explicit Assembly.Load, Type.GetType or LoadFromAssemblyName fails as
	/// before. Stand-ins are left out of AppDomain.GetAssemblies, so a mod that looks for another mod's assembly there
	/// still finds none.
	/// Stand-ins live in a load context of their own, not in the default one, so the name stays free there: a mod that
	/// loads the real assembly later (RimUnity loads Nivarian_Race from a .rimunity file after NivarianMechCompat had
	/// asked for it) gets it, as on the PC. Until 2026-10-27 15:30 the stand-in was in the default context, that load
	/// failed ("The located assembly's manifest definition does not match"), the race's types were missing and the game's
	/// load gave up and switched all mods off. Until 2026-10-28 a mod's own assembly loaded after such a stand-in was
	/// taken for already loaded (PortPlatform.LoadModAssembly looked in AppDomain.GetAssemblies, which listed stand-ins
	/// while the mods loaded): a vivo tester's RimTalk, asked for by an add-on sorted before it, never loaded. mobile.txt
	/// standInTest=1 tries this at the start (EarlyTest) and at the main menu (Test).
	/// </summary>
	public static class MissingAssemblies
	{
		private static readonly object gate = new object();
		private static readonly Dictionary<string, Assembly> standIns = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<Assembly> hidden = new HashSet<Assembly>();
		[ThreadStatic] private static bool making;
		private static readonly Lazy<AssemblyLoadContext> standInContext = new Lazy<AssemblyLoadContext>(() => new AssemblyLoadContext("port17.standins"));

		public static void Start(Harmony harmony)
		{
			try
			{
				MethodInfo resolve = typeof(AssemblyLoadContext).GetMethod("OnAssemblyResolve", BindingFlags.NonPublic | BindingFlags.Static);
				// AppDomain.GetAssemblies only calls this, but its precompiled (ReadyToRun) code has it inlined: until the
				// method is compiled again at the main menu, a stand-in made while the mods load was listed (measured with
				// standInTest=1, 2026-10-28). Both are patched: the public method for every caller, this one for the
				// framework's own inlined copies of the public one.
				MethodInfo list = typeof(AssemblyLoadContext).GetMethod("GetLoadedAssemblies", BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
				MethodInfo domainList = typeof(AppDomain).GetMethod("GetAssemblies", Type.EmptyTypes);
				if (resolve == null || list == null || domainList == null)
				{
					Console.WriteLine("[port17] missing assemblies: this runtime's resolver is not as expected, left as it is");
					return;
				}
				harmony.Patch(resolve, postfix: new HarmonyMethod(typeof(MissingAssemblies), nameof(AfterResolve)));
				harmony.Patch(list, postfix: new HarmonyMethod(typeof(MissingAssemblies), nameof(HideStandIns)));
				harmony.Patch(domainList, postfix: new HarmonyMethod(typeof(MissingAssemblies), nameof(HideStandIns)));
				Console.WriteLine("[port17] missing assemblies: references to one fail with TypeLoadException, as on the PC");
				if (Textures.Setting("standInTest") == "1")
				{
					EarlyTest();
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] missing assemblies left as they are: " + e.Message);
			}
		}

		// The result as the runtime's own type (RuntimeAssembly): what is stored is always one of those (LoadFromStream's).
		public static void AfterResolve(object assembly, string assemblyFullName, ref Assembly __result)
		{
			if (__result != null || making || string.IsNullOrEmpty(assemblyFullName))
			{
				return;
			}
			AssemblyName name;
			try
			{
				name = new AssemblyName(assemblyFullName);
			}
			catch (Exception)
			{
				return;
			}
			string simple = name.Name ?? "";
			if (simple.Length == 0 || simple.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) || Explicit())
			{
				return;
			}
			lock (gate)
			{
				if (!standIns.TryGetValue(simple, out Assembly standIn))
				{
					standIn = Make(name);
					standIns[simple] = standIn;
					if (standIn != null)
					{
						hidden.Add(standIn);
					}
					string by = assembly is Assembly asking ? asking.GetName().Name : "?";
					Console.WriteLine("[port17] missing assembly " + simple + " (" + name.Version + ", asked for by " + by + ")" + (standIn != null ? ": an empty stand-in, its types fail with TypeLoadException as on the PC" : ": no stand-in could be made"));
				}
				if (standIn != null)
				{
					__result = standIn;
				}
			}
		}

		/// <summary>An explicit load in progress (Assembly.Load, Type.GetType...): those fail as they always did.</summary>
		private static bool Explicit()
		{
			StackFrame[] frames = new StackTrace(2, false).GetFrames();
			foreach (StackFrame frame in frames)
			{
				MethodBase method = frame.GetMethod();
				Type type = method?.DeclaringType;
				if (type == null)
				{
					continue;
				}
				string owner = type.FullName ?? "";
				string m = method.Name;
				if ((owner == "System.Reflection.Assembly" || owner == "System.Reflection.RuntimeAssembly") && m.StartsWith("Load", StringComparison.Ordinal)
					|| owner == "System.Runtime.Loader.AssemblyLoadContext" && m.StartsWith("LoadFrom", StringComparison.Ordinal)
					|| (owner == "System.Type" || owner == "System.RuntimeType" || owner == "System.Reflection.TypeNameResolver" || owner == "System.Reflection.TypeNameParser") && (m == "GetType" || m.StartsWith("Resolve", StringComparison.Ordinal))
					|| owner == "System.AppDomain" && m == "Load"
					|| owner == "System.Activator" && m.StartsWith("Create", StringComparison.Ordinal))
				{
					return true;
				}
				if (owner.StartsWith("System.", StringComparison.Ordinal) || owner.StartsWith("PortHooks.", StringComparison.Ordinal) || owner.StartsWith("HarmonyLib.", StringComparison.Ordinal))
				{
					continue;
				}
				// The first frame outside the framework: the code whose reference is being followed.
				return false;
			}
			return false;
		}

		private static Assembly Make(AssemblyName wanted)
		{
			making = true;
			try
			{
				var name = new AssemblyName(wanted.Name) { Version = wanted.Version ?? new Version(0, 0, 0, 0) };
				var builder = new PersistedAssemblyBuilder(name, typeof(object).Assembly);
				builder.DefineDynamicModule(wanted.Name);
				var bytes = new MemoryStream();
				builder.Save(bytes);
				bytes.Position = 0;
				return standInContext.Value.LoadFromStream(bytes);
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] missing assembly " + wanted.Name + ": " + e.GetType().Name + ": " + e.Message);
				return null;
			}
			finally
			{
				making = false;
			}
		}

		/// <summary>
		/// mobile.txt standInTest=1, at the main menu: a caller of port17.standintest.lib (not loaded yet) is called (a
		/// stand-in, TypeLoadException), then the library is loaded as a mod's assembly is (must load the real one), then a second
		/// caller and the first once more (whether references followed after the real load find it).
		/// </summary>
		public static void Test()
		{
			if (Textures.Setting("standInTest") != "1")
			{
				return;
			}
			try
			{
				byte[] lib = Library();
				// The callers need the library's method when they are made: from a context of its own, let go before they run.
				byte[] caller1, caller2;
				WeakReference scratch = MakeCallers(lib, out caller1, out caller2);
				for (int i = 0; i < 20 && scratch.IsAlive; i++)
				{
					GC.Collect();
					GC.WaitForPendingFinalizers();
				}
				bool left = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "port17.standintest.lib");
				Console.WriteLine("[port17] stand-in test: scratch context " + (scratch.IsAlive ? "still alive" : "gone") + (left ? ", the library is still listed" : ""));
				Assembly first = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(caller1));
				Console.WriteLine("[port17] stand-in test: first caller before the library: " + Run(first));
				bool listed = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "port17.standintest.lib");
				Console.WriteLine("[port17] stand-in test: AppDomain.GetAssemblies " + (listed ? "lists the stand-in" : "does not list the stand-in"));
				string path = Path.Combine(Path.GetTempPath(), "port17.standintest.lib.dll");
				File.WriteAllBytes(path, lib);
				try
				{
					// The way a mod's assembly loads (ModAssemblyHandler.ReloadAll): the real library, not the stand-in.
					Assembly real = PortPlatform.LoadModAssembly(path);
					Console.WriteLine("[port17] stand-in test: the library loaded as a mod's assembly: " + (IsStandIn(real) ? "the stand-in came back" : "loaded") + " (" + AssemblyLoadContext.GetLoadContext(real)?.Name + ")");
				}
				catch (Exception e)
				{
					Console.WriteLine("[port17] stand-in test: the library loaded as a mod's assembly: " + e.GetType().Name + ": " + e.Message);
				}
				Assembly second = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(caller2));
				Console.WriteLine("[port17] stand-in test: second caller after the library: " + Run(second));
				Console.WriteLine("[port17] stand-in test: first caller again: " + Run(first));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] stand-in test failed: " + e);
			}
		}

		/// <summary>standInTest=1 at the start, while the mods load: whether AppDomain.GetAssemblies lists a stand-in then.</summary>
		private static void EarlyTest()
		{
			Assembly early = Make(new AssemblyName("port17.standintest.early") { Version = new Version(1, 0, 0, 0) });
			if (early == null)
			{
				return;
			}
			lock (gate)
			{
				standIns["port17.standintest.early"] = early;
				hidden.Add(early);
			}
			bool listed = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "port17.standintest.early");
			Console.WriteLine("[port17] stand-in test (start): AppDomain.GetAssemblies " + (listed ? "lists the stand-in" : "does not list the stand-in"));
		}

		private static byte[] Library()
		{
			var builder = new PersistedAssemblyBuilder(new AssemblyName("port17.standintest.lib") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
			TypeBuilder type = builder.DefineDynamicModule("port17.standintest.lib").DefineType("Port17StandInTest.Lib", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
			ILGenerator il = type.DefineMethod("Value", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes).GetILGenerator();
			il.Emit(OpCodes.Ldc_I4, 42);
			il.Emit(OpCodes.Ret);
			type.CreateType();
			var bytes = new MemoryStream();
			builder.Save(bytes);
			return bytes.ToArray();
		}

		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		private static WeakReference MakeCallers(byte[] lib, out byte[] caller1, out byte[] caller2)
		{
			var context = new AssemblyLoadContext("port17.standintest.scratch", true);
			MethodInfo value = context.LoadFromStream(new MemoryStream(lib)).GetType("Port17StandInTest.Lib").GetMethod("Value");
			caller1 = Caller("port17.standintest.caller1", value);
			caller2 = Caller("port17.standintest.caller2", value);
			context.Unload();
			return new WeakReference(context);
		}

		private static byte[] Caller(string name, MethodInfo value)
		{
			var builder = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
			TypeBuilder type = builder.DefineDynamicModule(name).DefineType("Port17StandInTest.Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
			ILGenerator il = type.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes).GetILGenerator();
			il.Emit(OpCodes.Call, value);
			il.Emit(OpCodes.Ret);
			type.CreateType();
			var bytes = new MemoryStream();
			builder.Save(bytes);
			return bytes.ToArray();
		}

		private static string Run(Assembly caller)
		{
			try
			{
				return "returned " + caller.GetType("Port17StandInTest.Caller").GetMethod("Call").Invoke(null, null);
			}
			catch (TargetInvocationException e) when (e.InnerException != null)
			{
				return e.InnerException.GetType().Name + ": " + e.InnerException.Message;
			}
			catch (Exception e)
			{
				return e.GetType().Name + ": " + e.Message;
			}
		}

		/// <summary>Whether an assembly is one of the stand-ins (never a real assembly of that name).</summary>
		public static bool IsStandIn(Assembly assembly)
		{
			if (hidden.Count == 0 || assembly == null)
			{
				return false;
			}
			lock (gate)
			{
				return hidden.Contains(assembly);
			}
		}

		public static void HideStandIns(ref Assembly[] __result)
		{
			if (hidden.Count == 0 || __result == null)
			{
				return;
			}
			lock (gate)
			{
				__result = __result.Where(a => !hidden.Contains(a)).ToArray();
			}
		}
	}
}
