using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PortWorkshop
{
	/// <summary>
	/// What the Workshop did, for the logs players send in. Every line also goes to the console, which is
	/// the app's stderr log and keeps the last run only; Documents/port17-workshop.log keeps the runs
	/// before it and sits beside port17-ui.log, which the launcher already exports. No account name and
	/// no token ever comes here (Session.cs says why); ids, results and timings do.
	/// </summary>
	internal static class WorkshopLog
	{
		private static readonly object gate = new object();
		private static readonly UTF8Encoding utf8 = new UTF8Encoding(false);
		private static string path;
		private static bool located;
		private static bool headed;
		private static int failures;

		/// <summary>Once the file passes 1 MB it is cut back to its last 128 KB: room for many sessions.</summary>
		private const long Most = 1 << 20;
		private const long Kept = 128 << 10;

		internal static void Write(string text)
		{
			Console.WriteLine("[workshop] " + text);
			lock (gate)
			{
				if (!located)
				{
					located = true;
					path = Locate();
				}
				if (path == null)
				{
					return;
				}
				try
				{
					if (!headed)
					{
						Trim();
						Append("--- " + Stamp() + " ---");
						headed = true;
					}
					Append(Stamp() + " " + text);
					failures = 0;
				}
				catch (Exception)
				{
					// A log must not be the thing that breaks the Workshop: after three misses this run stays console-only.
					if (++failures >= 3)
					{
						path = null;
					}
				}
			}
		}

		private static string Locate()
		{
			if (!OperatingSystem.IsIOS())
			{
				return null;
			}
			return Path.Combine(Session.IosHome(), "Documents", "port17-workshop.log");
		}

		private static string Stamp()
		{
			return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
		}

		private static void Append(string line)
		{
			File.AppendAllText(path, line + "\n", utf8);
		}

		private static void Trim()
		{
			if (!File.Exists(path) || new FileInfo(path).Length <= Most)
			{
				return;
			}
			byte[] all = File.ReadAllBytes(path);
			int start = Math.Max(0, all.Length - (int)Kept);
			int newline = Array.IndexOf(all, (byte)'\n', start);
			if (newline >= 0 && newline + 1 < all.Length)
			{
				start = newline + 1;
			}
			byte[] kept = new byte[all.Length - start];
			Array.Copy(all, start, kept, 0, kept.Length);
			File.WriteAllBytes(path, kept);
		}
	}
}
