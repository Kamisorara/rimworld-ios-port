using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// Opens the Steam Workshop window (workshop/PortWorkshop.dll next to the game). It and the libraries it uses
	/// (SteamKit2, protobuf-net, ZstdSharp, System.IO.Hashing, QRCoder) live in workshop/ and are loaded into a load
	/// context of their own, on first use: a mod that brings its own copy of one of those libraries keeps its copy,
	/// and nothing of the Workshop is loaded unless the player asks for it, or has signed in to it before (the main
	/// menu's update check, UpdateNotice).
	/// </summary>
	internal static class WorkshopLoader
	{
		private sealed class Context : AssemblyLoadContext
		{
			private readonly string folder;

			public Context(string folder) : base("PortWorkshop")
			{
				this.folder = folder;
			}

			// The game's and the runtime's assemblies (and this one) come from the default context.
			protected override Assembly Load(AssemblyName name)
			{
				string path = Path.Combine(folder, name.Name + ".dll");
				return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
			}
		}

		private static readonly object loading = new object();
		private static Type entry;

		/// <summary>PortWorkshop.Entry, loaded on first use (on the game's thread, or the update check's).</summary>
		private static Type Entry
		{
			get
			{
				lock (loading)
				{
					if (entry == null)
					{
						// The iOS loader's AppContext.BaseDirectory is the game folder, but the Workshop ships in the
						// .app bundle, beside PortHooks. Android keeps the staged game-folder layout.
						string folder = OperatingSystem.IsIOS()
							? Path.Combine(Path.GetDirectoryName(typeof(PortHooks.HostFiles).Assembly.Location), "workshop")
							: Path.Combine(AppContext.BaseDirectory, "workshop");
						var context = new Context(folder);
						Assembly assembly = context.LoadFromAssemblyPath(Path.Combine(folder, "PortWorkshop.dll"));
						entry = assembly.GetType("PortWorkshop.Entry", true);
					}
					return entry;
				}
			}
		}

		/// <summary>
		/// Whether a Steam sign-in is remembered: PortWorkshop's Session keeps the token Steam gave in the app's private
		/// files (Android: /data/data/(package)/files/steam/login; iOS: Library/Application Support/Port17/steam/login),
		/// and only with it can the update check sign in by itself. Looked at here, so that the Workshop stays unloaded
		/// for a player who never signed in.
		/// </summary>
		private static bool SignInRemembered()
		{
			try
			{
				if (OperatingSystem.IsIOS())
				{
					// Session.PrivateDir, read without loading PortWorkshop and its dependencies.
					string home = IosHome();
					if (!string.IsNullOrEmpty(home) && home != "/" &&
						File.Exists(Path.Combine(home, "Library", "Application Support", "Port17", "steam", "login")))
					{
						return true;
					}
					// 0.4.38's location, before the token moved out of Documents; Session moves it on first use.
					string files = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
					return File.Exists(Path.Combine(files, "steam", "login"));
				}
				string package = File.ReadAllText("/proc/self/cmdline").Split('\0')[0].Trim();
				return File.Exists(Path.Combine("/data/data", package, "files", "steam", "login"));
			}
			catch (Exception)
			{
				return false;
			}
		}

		// Session.IosHome: the game folder is <HOME>/Documents/game, possibly one wrapper deep, or
		// <HOME>/Documents/<name>; Documents' parent is the app's home even when HOME is not set to it.
		private static string IosHome()
		{
			string dir = Path.GetFullPath(AppContext.BaseDirectory);
			for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
			{
				if (string.Equals(Path.GetFileName(dir), "Documents", StringComparison.OrdinalIgnoreCase))
				{
					return Path.GetDirectoryName(dir);
				}
				dir = Path.GetDirectoryName(dir);
			}
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (string.IsNullOrEmpty(home) || home == "/")
			{
				home = Environment.GetEnvironmentVariable("HOME");
			}
			return !string.IsNullOrEmpty(home) && home != "/"
				? home
				: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
		}

		/// <summary>
		/// On a worker thread, when the main menu comes up (UpdateNotice): with a sign-in remembered, PortWorkshop checks
		/// the installed Workshop mods for updates if today's check is due, and tells UpdateNotice what it found.
		/// </summary>
		public static void CheckForUpdates(string mods)
		{
			if (!SignInRemembered())
			{
				Console.WriteLine("[mobile] workshop: no update check, no Steam sign-in is remembered");
				return;
			}
			try
			{
				// A PortWorkshop.dll from before the notice (put on the phone by itself) has no check.
				MethodInfo check = Entry.GetMethod("CheckForUpdates", BindingFlags.Public | BindingFlags.Static);
				if (check == null)
				{
					Console.WriteLine("[mobile] workshop: no update check, this PortWorkshop.dll has none");
					return;
				}
				check.Invoke(null, new object[] { mods });
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
				Console.WriteLine("[mobile] workshop: no update check: " + inner.Message);
			}
		}

		/// <summary>
		/// On a worker thread, after the mod page deleted or imported mods (UpdateNotice.ModsChanged): PortWorkshop tells the
		/// notice again which of the mods its check found still have that newer version. Only if it is loaded already: it is
		/// not loaded for this.
		/// </summary>
		public static void RefreshUpdates(string mods)
		{
			try
			{
				Type loaded;
				lock (loading)
				{
					loaded = entry;
				}
				// A PortWorkshop.dll from before the notice (put on the phone by itself) has nothing to refresh.
				MethodInfo refresh = loaded?.GetMethod("RefreshUpdates", BindingFlags.Public | BindingFlags.Static);
				refresh?.Invoke(null, new object[] { mods });
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
				Console.WriteLine("[mobile] workshop: the update notice was not refreshed: " + inner.Message);
			}
		}

		/// <summary>The update notice's button: the window, with the mods the notice names ticked.</summary>
		public static void OpenUpdates()
		{
			try
			{
				Entry.GetMethod("OpenUpdates", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
				Console.WriteLine("[mobile] workshop: could not open for the updates: " + inner);
				Find.WindowStack.Add(new Dialog_MessageBox((Texts.Chinese ? "创意工坊打不开：" : "The Workshop could not be opened: ") + inner.Message));
			}
		}

		public static void Open()
		{
			try
			{
				Entry.GetMethod("Open", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
				Console.WriteLine("[mobile] workshop: could not open: " + inner);
				Find.WindowStack.Add(new Dialog_MessageBox((Texts.Chinese ? "创意工坊打不开：" : "The Workshop could not be opened: ") + inner.Message));
			}
		}

		/// <summary>
		/// The window for a Workshop link (OpenUrl): the item goes into its list, and its page is offered for the browser.
		/// False when the Workshop could not be opened (the caller then opens the page in the browser).
		/// </summary>
		public static bool OpenItem(string item, string page)
		{
			try
			{
				// A PortWorkshop.dll from before links (put on the phone by itself) has no OpenItem.
				MethodInfo openItem = Entry.GetMethod("OpenItem", BindingFlags.Public | BindingFlags.Static);
				if (openItem == null)
				{
					return false;
				}
				openItem.Invoke(null, new object[] { item, page });
				return true;
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
				Console.WriteLine("[mobile] workshop: could not open for a link: " + inner);
				return false;
			}
		}

		/// <summary>
		/// SteamKit2 stamps its job ids with Process.StartTime, read once when SteamClient is built (its constructor:
		/// GetCurrentProcess().StartTime). CoreCLR has no way to read it on iOS and StartTimeCore throws
		/// PlatformNotSupportedException, so the Workshop window could not open at all (0.4.41 on the device, twice
		/// in one run). The value only tells SteamKit2's jobs apart, so the moment PortMobile started stands in for it.
		/// StartTime and StartTimeCore (internal static, what the getter falls to) get a prefix each, and
		/// GetCurrentProcess a postfix that caches the time in the Process it hands out, which is what a copy of
		/// the getter inlined into a caller reads first. iOS only: Android reads the real one from /proc.
		/// </summary>
		internal static void Start(Harmony harmony)
		{
			if (!OperatingSystem.IsIOS())
			{
				return;
			}
			try
			{
				harmony.Patch(AccessTools.PropertyGetter(typeof(System.Diagnostics.Process), nameof(System.Diagnostics.Process.StartTime)), prefix: new HarmonyMethod(typeof(WorkshopLoader), nameof(BeforeStartTime)));
				Console.WriteLine("[mobile] workshop: Process.StartTime answers the moment PortMobile started");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] workshop: Process.StartTime left as it is: " + e.Message);
			}
			try
			{
				harmony.Patch(AccessTools.Method(typeof(System.Diagnostics.Process), "get_" + nameof(System.Diagnostics.Process.StartTime) + "Core"), prefix: new HarmonyMethod(typeof(WorkshopLoader), nameof(BeforeStartTime)));
				Console.WriteLine("[mobile] workshop: Process.StartTimeCore answers the moment PortMobile started");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] workshop: Process.StartTimeCore left as it is: " + e.Message);
			}
			try
			{
				harmony.Patch(AccessTools.Method(typeof(System.Diagnostics.Process), nameof(System.Diagnostics.Process.GetCurrentProcess), Type.EmptyTypes), postfix: new HarmonyMethod(typeof(WorkshopLoader), nameof(AfterGetCurrentProcess)));
				Console.WriteLine("[mobile] workshop: GetCurrentProcess hands out its start time cached");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] workshop: GetCurrentProcess left as it is: " + e.Message);
			}
		}

		/// <summary>Prefix of Process.StartTime and of Process.StartTimeCore: when PortMobile started; the original does not run.</summary>
		public static bool BeforeStartTime(ref DateTime __result)
		{
			__result = Started();
			return false;
		}

		/// <summary>
		/// Postfix of Process.GetCurrentProcess: the Process it hands out has its start time cached, as the runtime's own
		/// has it after the first read. Only a guard for a copy of the getter inlined before the prefix was in place.
		/// </summary>
		public static void AfterGetCurrentProcess(System.Diagnostics.Process __result)
		{
			try
			{
				AccessTools.Field(typeof(System.Diagnostics.Process), "_startTime")?.SetValue(__result, Started());
			}
			catch (Exception)
			{
				// The prefix of the getter is what does the work; nothing to do here.
			}
		}

		private static DateTime? started;

		/// <summary>
		/// When PortMobile started, as Process.StartTime would tell it: now, less the ticks counted since the startup
		/// hook took its first breath (PortPlatform.StartedAtTick). .NET's is the local time, so is this one.
		/// </summary>
		internal static DateTime Started()
		{
			if (!started.HasValue)
			{
				long tick = PortHooks.PortPlatform.StartedAtTick;
				started = tick > 0 ? DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64 - tick) : DateTime.Now;
			}
			return started.Value;
		}
	}
}
