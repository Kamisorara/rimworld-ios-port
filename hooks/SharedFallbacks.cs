using System;
using System.Reflection;
using HarmonyLib;

namespace PortHooks
{
	/// <summary>
	/// The white fallback textures (Core.WhiteFallback, Core.WhiteArrayFallback) are made once by GpuTextureService and
	/// shared by every MeshBatchRenderer, which binds them for a material's empty texture slots and keeps the descriptor
	/// sets it wrote; yet MeshBatchRenderer.Dispose disposes the ones it took. The engine lets its own renderers go only
	/// when it shuts down, but a mod's renderer let go while the game runs takes every other renderer's fallback with it:
	/// the map's descriptor sets then point at a freed image (on Adreno very likely VK_ERROR_DEVICE_LOST in the next frame),
	/// and sets made afterwards find none ("Texture 'Core.WhiteFallback' does not have a sampled GPU image", which ends the
	/// game). RimUnity (inside Nivarian Race) lets its screen effects' renderers go whenever no effect plays, and those and
	/// its HDR map renderer whenever the map pass's render targets are rebuilt, which on the phone is every return from the
	/// background: a tester's vivo (Adreno 740, 2026-11-06) lost the device three times with Nivarian's research window
	/// open (the two in the logs just after taps in it), and once ended with the exception right after coming back to the
	/// game. Agent fallbacktest reproduces the exception; the device loss is the same cause by reasoning, not reproduced.
	/// Here a renderer that is let go forgets the shared textures instead
	/// of disposing them; GpuTextureService still disposes them when it shuts down. mobile.txt sharedFallbacks=0 leaves the
	/// engine as it is (Agent fallbacktest tries it).
	/// </summary>
	internal static class SharedFallbacks
	{
		private static FieldInfo white, whiteArray;
		private static bool told;

		public static void Start(Harmony harmony)
		{
			try
			{
				if (Textures.Setting("sharedFallbacks") == "0")
				{
					Console.WriteLine("[port17] shared fallbacks: left as the engine has them (sharedFallbacks=0 in mobile.txt)");
					return;
				}
				Type renderer = Type.GetType("Waylith.Rendering.MeshBatchRenderer, Waylith");
				MethodInfo dispose = renderer?.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
				white = renderer?.GetField("fallbackWhiteTexture", BindingFlags.NonPublic | BindingFlags.Instance);
				whiteArray = renderer?.GetField("fallbackWhiteArrayTexture", BindingFlags.NonPublic | BindingFlags.Instance);
				if (dispose == null || white == null || whiteArray == null)
				{
					Console.WriteLine("[port17] shared fallbacks: MeshBatchRenderer is not as expected; left as it is");
					return;
				}
				harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(SharedFallbacks), nameof(BeforeDispose)));
				Console.WriteLine("[port17] shared fallbacks: a mesh renderer let go keeps the white fallback textures the others use");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] shared fallbacks: not installed: " + e.Message);
			}
		}

		public static void BeforeDispose(object __instance)
		{
			if (white.GetValue(__instance) == null && whiteArray.GetValue(__instance) == null)
			{
				return;
			}
			white.SetValue(__instance, null);
			whiteArray.SetValue(__instance, null);
			if (!told)
			{
				told = true;
				// Who let it go: the first caller that is not a Dispose (Harmony's replacement is named
				// "Waylith.Rendering.MeshBatchRenderer.Dispose_Patch1", without a declaring type).
				string by = "?";
				foreach (System.Diagnostics.StackFrame frame in new System.Diagnostics.StackTrace(1, false).GetFrames())
				{
					MethodBase method = frame.GetMethod();
					if (method != null && method.Name.IndexOf("Dispose", StringComparison.Ordinal) < 0)
					{
						by = (method.DeclaringType != null ? method.DeclaringType.FullName + "." : "") + method.Name;
						break;
					}
				}
				Console.WriteLine("[port17] shared fallbacks: a mesh renderer let go by " + by + " kept the white fallback textures the others use (logged once)");
			}
		}
	}
}
