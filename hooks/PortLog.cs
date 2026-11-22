using System;
using System.IO;
using System.Text;

namespace PortHooks
{
	/// <summary>
	/// The port's own lines ("[port17] ...", "[mobile] ...", written with Console.WriteLine) also go to Logs/Port17.log
	/// next to the game's Player.log, with the time. On Android the runtime sends Console to logcat, which a tester does
	/// not have; the crash report and "导出日志" (host/java Diagnostics.java) take this file along. The previous run's
	/// file is kept as Port17-prev.log.
	/// </summary>
	internal sealed class PortLog : TextWriter
	{
		private readonly TextWriter console;
		private readonly StreamWriter file;

		private PortLog(TextWriter console, StreamWriter file)
		{
			this.console = console;
			this.file = file;
		}

		public override Encoding Encoding => console.Encoding;

		public static void Start()
		{
			try
			{
				string home = Environment.GetEnvironmentVariable("HOME");
				if (string.IsNullOrEmpty(home))
				{
					return;
				}
				string logs = Path.Combine(home, "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios", "Logs");
				Directory.CreateDirectory(logs);
				string path = Path.Combine(logs, "Port17.log");
				if (File.Exists(path))
				{
					File.Copy(path, Path.Combine(logs, "Port17-prev.log"), true);
				}
				var file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
				Console.SetOut(new PortLog(Console.Out, file));
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] Port17.log not written: " + e.Message);
			}
		}

		private void Keep(string line)
		{
			if (line == null || line.Length == 0 || line[0] != '[')
			{
				return;
			}
			try
			{
				lock (file)
				{
					file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line);
				}
			}
			catch (Exception)
			{
			}
		}

		public override void Write(char value)
		{
			console.Write(value);
		}

		public override void Write(string value)
		{
			console.Write(value);
			Keep(value?.TrimEnd('\r', '\n'));
		}

		public override void WriteLine(string value)
		{
			console.WriteLine(value);
			Keep(value);
		}

		public override void WriteLine()
		{
			console.WriteLine();
		}

		public override void Flush()
		{
			console.Flush();
		}
	}
}
