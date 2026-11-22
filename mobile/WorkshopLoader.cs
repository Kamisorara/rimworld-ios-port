using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
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
						string folder = Path.Combine(AppContext.BaseDirectory, "workshop");
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
		/// files (/data/data/(package)/files/steam/login), and only with it can the update check sign in by itself. Looked
		/// at here, so that the Workshop stays unloaded for a player who never signed in.
		/// </summary>
		private static bool SignInRemembered()
		{
			try
			{
				string package = File.ReadAllText("/proc/self/cmdline").Split('\0')[0].Trim();
				return File.Exists(Path.Combine("/data/data", package, "files", "steam", "login"));
			}
			catch (Exception)
			{
				return false;
			}
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
	}
}
