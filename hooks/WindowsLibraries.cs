using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace PortHooks
{
	/// <summary>
	/// Windows libraries that PC mods call directly, answered by the phone's stand-ins where there is one. Only when an
	/// assembly asks for one and nothing else gave it (ResolvingUnmanagedDll comes after the normal search), so code that
	/// looks for Windows by trying a library finds none as before. user32: the clipboard and MessageBox
	/// (host/stubs/user32.c, libportuser32.so in the APK): Character Editor's "copy" was a DllNotFoundException.
	/// The runtime asks the load context of the assembly that makes the call, and an assembly loaded by
	/// Assembly.Load(byte[]) or Assembly.LoadFile is in a context of its own (DLLs a mod carries inside itself), so every
	/// context gets the handler, each once: until 2026-10-30 only the default context had it, while on the PC Windows
	/// finds user32 for code in any context.
	/// </summary>
	public static class WindowsLibraries
	{
		private static IntPtr user32;
		private static bool told;
		// Held weakly: a context a mod lets go of is unloaded as before.
		private static readonly ConditionalWeakTable<AssemblyLoadContext, object> handled = new ConditionalWeakTable<AssemblyLoadContext, object>();

		public static void Start()
		{
			try
			{
				// A context is seen when its first assembly loads, before any code of that assembly can call out.
				AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => Handle(args.LoadedAssembly);
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					Handle(assembly);
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] Windows libraries: no stand-ins: " + e.Message);
			}
		}

		/// <summary>The handler on the load context of an assembly, unless that context has it already.</summary>
		private static void Handle(Assembly assembly)
		{
			try
			{
				AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(assembly);
				if (context != null && handled.TryAdd(context, null))
				{
					context.ResolvingUnmanagedDll += Resolve;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] Windows libraries: no stand-ins for the load context of " + assembly?.GetName().Name + ": " + e.Message);
			}
		}

		private static IntPtr Resolve(Assembly assembly, string name)
		{
			string bare = Path.GetFileNameWithoutExtension(name ?? "");
			if (!bare.Equals("user32", StringComparison.OrdinalIgnoreCase))
			{
				return IntPtr.Zero;
			}
			if (user32 == IntPtr.Zero && !NativeLibrary.TryLoad("libportuser32.so", out user32))
			{
				user32 = IntPtr.Zero;
			}
			if (!told)
			{
				told = true;
				Console.WriteLine("[port17] Windows libraries: " + (assembly?.GetName().Name ?? "?") + " asked for " + name + (user32 != IntPtr.Zero ? ": the phone's stand-in (clipboard, MessageBox)" : ": the stand-in did not load"));
			}
			return user32;
		}
	}
}
