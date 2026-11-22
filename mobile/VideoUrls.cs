using System;
using HarmonyLib;

namespace PortMobile
{
	/// <summary>
	/// A video a mod plays from a file, named the way that works on Windows: "file:///" + the file's path. On the phone
	/// the path begins with a slash already, so the address has four ("file:////storage/emulated/0/..."), which .NET
	/// reads as a network share (\\storage\emulated\0\...), and the engine's VideoPlayer looked for that next to the
	/// game: nothing played. God Hands' demonstration clips in its help windows (the user, 2026-10-24: "神之手还有点
	/// 问题"; Player.log "演示视频解码失败 ... Could not find file"). Before the player opens its address, four slashes in
	/// front of an absolute path become three. An address the PC gets is never like that.
	/// </summary>
	internal static class VideoUrls
	{
		private const string Doubled = "file:////";

		public static void Start(Harmony harmony)
		{
			try
			{
				Type player = AccessTools.TypeByName("UnityEngine.Video.VideoPlayer");
				// SourceChanged opens the address the url setter (small enough to be copied into its callers) stored.
				harmony.Patch(AccessTools.Method(player, "SourceChanged"), prefix: new HarmonyMethod(typeof(VideoUrls), nameof(BeforeSourceChanged)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] video urls: not patched: " + e.Message);
			}
		}

		/// <summary>Prefix of VideoPlayer.SourceChanged: "file:////path" is "file:///path".</summary>
		public static void BeforeSourceChanged(ref string ____url)
		{
			if (____url != null && ____url.StartsWith(Doubled, StringComparison.OrdinalIgnoreCase) && ____url.Length > Doubled.Length && ____url[Doubled.Length] != '/')
			{
				string fixedUrl = "file:///" + ____url.Substring(Doubled.Length);
				Console.WriteLine("[mobile] video urls: " + ____url + " -> " + fixedUrl);
				____url = fixedUrl;
			}
		}
	}
}
