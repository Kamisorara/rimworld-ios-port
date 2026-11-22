using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// The game's own screenshots (F10: ScreenshotTaker.TakeNonSteamShot, without Steam) land in the data folder's
	/// Screenshots, which a PC opens in its file manager; on the phone that folder is private to the app (Android/data:
	/// the gallery does not see it, nor do most file managers since Android 11). Each screenshot written there is also
	/// copied into Pictures/RimWorld, which the gallery shows (since Android 11 an app may create its own pictures there
	/// by path; the media store lists such a file only once it is scanned, MainActivity.scanMediaUtf8). Captures elsewhere
	/// (a mod's own folder) are left as they are.
	/// </summary>
	public static class Screenshots
	{
		private const string Gallery = "/storage/emulated/0/Pictures/RimWorld";

		[DllImport("glfw")] private static extern int glfwAndroidScanMedia(byte[] path);

		public static void Start(Harmony harmony)
		{
			try
			{
				harmony.Patch(AccessTools.Method(typeof(ScreenCapture), "CaptureScreenshot", new[] { typeof(string), typeof(int), typeof(ScreenCapture.StereoScreenCaptureMode) }), prefix: new HarmonyMethod(typeof(Screenshots), nameof(BeforeCapture)));
				Console.WriteLine("[mobile] screenshots: also copied into " + Gallery);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] screenshots: left as they are: " + e.Message);
			}
		}

		/// <summary>A file the game made in the phone's shared storage told to the media store, which lists it only once scanned (the gallery, Downloads, a PC by cable).</summary>
		internal static bool ScanMedia(string path)
		{
			try
			{
				return glfwAndroidScanMedia(Encoding.UTF8.GetBytes(path + "\0")) != 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] media scan of " + path + ": " + e.Message);
				return false;
			}
		}

		public static void BeforeCapture(string filename)
		{
			if (string.IsNullOrEmpty(filename))
			{
				return;
			}
			string folder = Path.GetFullPath(GenFilePaths.ScreenshotFolderPath);
			string file = Path.GetFullPath(filename);
			if (!file.StartsWith(folder, StringComparison.Ordinal))
			{
				return;
			}
			ThreadPool.QueueUserWorkItem(_ => CopyWhenWritten(file));
		}

		/// <summary>The engine writes the picture after the frame ends, on a task of its own: the copy waits until the file has stopped growing.</summary>
		private static void CopyWhenWritten(string file)
		{
			try
			{
				long size = -1;
				for (int i = 0; i < 120; i++)
				{
					Thread.Sleep(250);
					long now = File.Exists(file) ? new FileInfo(file).Length : -1;
					if (now > 0 && now == size)
					{
						break;
					}
					size = now;
				}
				if (size <= 0)
				{
					Console.WriteLine("[mobile] screenshots: " + file + " was not written");
					return;
				}
				Directory.CreateDirectory(Gallery);
				string target = Path.Combine(Gallery, Path.GetFileName(file));
				try
				{
					File.Copy(file, target);
				}
				catch (IOException)
				{
					// The name is taken: by an earlier screenshot1.png, or by a gallery entry whose file is gone (the media
					// store refuses that name too, although File.Exists says false).
					target = Path.Combine(Gallery, Path.GetFileNameWithoutExtension(file) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + Path.GetExtension(file));
					File.Copy(file, target);
				}
				bool scanned = ScanMedia(target);
				Console.WriteLine("[mobile] screenshots: " + Path.GetFileName(file) + " also in the gallery, " + target + (scanned ? "" : " (the media store was not told)"));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] screenshots: could not copy " + file + " into the gallery: " + e.GetType().Name + ": " + e.Message);
			}
		}
	}
}
