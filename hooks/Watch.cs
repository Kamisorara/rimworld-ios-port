using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PortHooks
{
	/// <summary>
	/// logs/watch.txt beside the game folder (the previous run's as watch-1.txt), written by a thread of its own from the
	/// process's start: what the record (PortMobile's Recorder) cannot see, because it starts only once the mods are
	/// loaded and writes on the main thread.
	///   - Until the main menu, every 5 s: the process's memory (resident, swapped, the collector's heap), what the phone
	///     has left, the threads, and the game's last log line, which says what the load was doing. A tester's crash of
	///     2026-10-27 came in the XML patching of 326 mods, before the record had begun: nothing said how far it got or
	///     how much memory it had.
	///   - Afterwards only when the main thread has not finished a frame for 10 s (PortMobile beats once a frame): every
	///     10 s of the stall, the same and the last log line, which names what the frame is stuck in (a mod's first
	///     window took 143 s at another tester's).
	/// Below normal priority; a few file reads each time.
	/// </summary>
	public static class Watch
	{
		private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
		private static long lastBeat = -1;
		private static volatile bool menu;
		private static string playerLog, portLog;
		private static StreamWriter file;

		private static int mainThread;
		/// <summary>The main thread's processor time (1/100 s) while frames still came, and when (Clock ms).</summary>
		private static long cpuBefore = -1, cpuAt;
		private static string trail;

		[System.Runtime.InteropServices.DllImport("libc")]
		private static extern int gettid();

		/// <summary>From PortMobile, once a frame.</summary>
		public static void Beat()
		{
			if (mainThread == 0)
			{
				mainThread = gettid();
			}
			Interlocked.Exchange(ref lastBeat, Clock.ElapsedMilliseconds);
		}

		/// <summary>From PortMobile at the main menu: the load is over, only stalls are written from here on.</summary>
		public static void MainMenu()
		{
			if (!menu)
			{
				// The baseline for a stall right after (the watch looks every 5 s until now).
				cpuBefore = MainThreadState(mainThread).Item2;
				cpuAt = Clock.ElapsedMilliseconds;
				menu = true;
				Write("menu", "the main menu is up");
			}
		}

		public static void Start()
		{
			try
			{
				string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "logs"));
				Directory.CreateDirectory(dir);
				string path = Path.Combine(dir, "watch.txt");
				if (File.Exists(path))
				{
					File.Copy(path, Path.Combine(dir, "watch-1.txt"), true);
				}
				file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
				string home = Environment.GetEnvironmentVariable("HOME") ?? "";
				string logs = Path.Combine(home, "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios", "Logs");
				playerLog = Path.Combine(logs, "Player.log");
				portLog = Path.Combine(logs, "Port17.log");
				// MainActivity's trail (Diagnostics.trail): its last line says whether the app is away from the screen.
				trail = Path.Combine(dir, "import.txt");
				file.WriteLine("watch of " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ": a line every 5 s until the main menu, then one every 10 s while the main thread stalls");
				new Thread(Run) { IsBackground = true, Name = "port17 watch", Priority = ThreadPriority.BelowNormal }.Start();
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] watch not started: " + e.Message);
			}
		}

		private static void Run()
		{
			// The main thread's processor time when it last finished frames: what it used since says whether a stall is
			// work (a long frame, a save loading in it, a loop) or waiting (on a lock, or the frame loop waiting while the
			// app is away, which is no stall and not written). Its state letter at one moment says little: a thread that
			// waits in short sleeps reads "R" now and then (2026-10-27, porttest in the background).
			long stallReported = 0;
			while (true)
			{
				try
				{
					Thread.Sleep(menu ? 2000 : 5000);
					if (!menu)
					{
						Write("load", null);
						continue;
					}
					long beat = Interlocked.Read(ref lastBeat);
					long now = Clock.ElapsedMilliseconds;
					long since = now - beat;
					if (beat < 0 || since <= 10000)
					{
						// Taken while frames still come (the last one under 2.5 s ago): the window then covers the stall.
						if (beat >= 0 && since < 2500)
						{
							cpuBefore = MainThreadState(mainThread).Item2;
							cpuAt = now;
						}
						continue;
					}
					if (now - stallReported < 10000 || file.BaseStream.Length > (4 << 20))
					{
						continue;
					}
					(char state, long cpu, string wchan) = MainThreadState(mainThread);
					bool known = cpuBefore >= 0 && cpu >= 0;
					double used = known ? (cpu - cpuBefore) / 100.0 : 0, window = (now - cpuAt) / 1000.0;
					bool working = known ? used > window / 2 : state == 'R';
					if (!working && Away())
					{
						continue;
					}
					stallReported = now;
					Write("stall", "no frame for " + (since / 1000) + " s, the main thread " + (working ? "working" : "waiting (" + state + (wchan.Length > 0 ? " in " + wchan : "") + ")")
						+ (known ? ", " + used.ToString("0.0", CultureInfo.InvariantCulture) + " s of processor in the last " + window.ToString("0", CultureInfo.InvariantCulture) + " s" : ""));
				}
				catch (Exception)
				{
				}
			}
		}

		/// <summary>/proc/self/task/TID: the state letter, user and system time in 1/100 s, the kernel function it waits in.</summary>
		private static (char, long, string) MainThreadState(int tid)
		{
			try
			{
				string stat = File.ReadAllText("/proc/self/task/" + tid + "/stat");
				string[] fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
				string wchan = "";
				try
				{
					wchan = File.ReadAllText("/proc/self/task/" + tid + "/wchan").Trim();
					if (wchan == "0")
					{
						wchan = "";
					}
				}
				catch (Exception)
				{
				}
				return (fields[0][0], long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture), wchan);
			}
			catch (Exception)
			{
				return ('?', -1, "");
			}
		}

		private static bool Away()
		{
			string last = LastLine(trail, false);
			return last != null && last.EndsWith("the app leaves the screen", StringComparison.Ordinal);
		}

		private static void Write(string kind, string note)
		{
			if (file == null)
			{
				return;
			}
			var line = new StringBuilder();
			line.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(' ').Append(Clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7)).Append(' ').Append(kind.PadRight(5));
			if (note != null)
			{
				line.Append(' ').Append(note).Append(';');
			}
			long rss = StatusKb("VmRSS:"), swap = StatusKb("VmSwap:"), threads = StatusKb("Threads:");
			line.Append(" resident ").Append(rss >> 10).Append(" MB, swapped ").Append(swap >> 10).Append(" MB, the phone has ").Append(MemAvailableKb() >> 10).Append(" MB available");
			try
			{
				GCMemoryInfo gc = GC.GetGCMemoryInfo();
				line.Append(", heap ").Append(gc.HeapSizeBytes >> 20).Append(" MB (").Append(GC.CollectionCount(2)).Append(" full collections)");
			}
			catch (Exception)
			{
			}
			line.Append(", ").Append(threads).Append(" threads, processor ").Append(ProcessCpuSeconds()).Append(" s");
			string game = LastLine(playerLog, true), port = LastLine(portLog, false);
			if (game != null)
			{
				// Without the date of "2026-10-27 10:42:07.609  WARN [Verse] ...".
				line.Append(" | game: ").Append(game.Length > 11 && game[4] == '-' && game[10] == ' ' ? game.Substring(11) : game);
			}
			if (port != null && kind != "load")
			{
				line.Append(" | port: ").Append(port);
			}
			lock (file)
			{
				file.WriteLine(line.ToString());
			}
		}

		/// <summary>The last line of a log that is not a stack frame, from its last 16 KB, cut to 300 characters.</summary>
		private static string LastLine(string path, bool skipFrames)
		{
			try
			{
				using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					long length = stream.Length;
					int take = (int)Math.Min(length, 16384);
					stream.Seek(length - take, SeekOrigin.Begin);
					var bytes = new byte[take];
					int read = 0;
					while (read < take)
					{
						int n = stream.Read(bytes, read, take - read);
						if (n <= 0)
						{
							break;
						}
						read += n;
					}
					string[] lines = Encoding.UTF8.GetString(bytes, 0, read).Split('\n');
					for (int i = lines.Length - 1; i >= 0; i--)
					{
						string text = lines[i].Trim();
						if (text.Length == 0 || (skipFrames && (text.StartsWith("at ", StringComparison.Ordinal) || text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("[Ref", StringComparison.Ordinal))))
						{
							continue;
						}
						return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
					}
				}
			}
			catch (Exception)
			{
			}
			return null;
		}

		private static string ProcessCpuSeconds()
		{
			try
			{
				string stat = File.ReadAllText("/proc/self/stat");
				string[] fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
				return ((long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture)) / 100.0).ToString("0", CultureInfo.InvariantCulture);
			}
			catch (Exception)
			{
				return "?";
			}
		}

		private static long StatusKb(string key)
		{
			try
			{
				foreach (string line in File.ReadLines("/proc/self/status"))
				{
					if (line.StartsWith(key, StringComparison.Ordinal))
					{
						return long.Parse(line.Substring(key.Length).Replace("kB", "").Trim(), CultureInfo.InvariantCulture);
					}
				}
			}
			catch (Exception)
			{
			}
			return 0;
		}

		private static long MemAvailableKb()
		{
			try
			{
				foreach (string line in File.ReadLines("/proc/meminfo"))
				{
					if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
					{
						return long.Parse(line.Substring(13).Replace("kB", "").Trim(), CultureInfo.InvariantCulture);
					}
				}
			}
			catch (Exception)
			{
			}
			return 0;
		}
	}
}
