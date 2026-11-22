using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using TMPro;
using UnityStubs.Gui;

namespace PortMobile
{
	/// <summary>
	/// The names on the world map (mountain ranges, seas...) when the game's language is not written in Latin letters.
	///
	/// The game draws such a name with TextMeshPro when the font has every character of it, and with Unity's old
	/// TextMesh otherwise. In this engine the TextMeshPro stand-in knows the characters 32-126 and 160-255 of each font
	/// (baked once at 48 px), and TextMesh is an empty class: WorldFeatureTextMesh_Legacy.Init fails on the MeshRenderer
	/// that is not there, the list of texts stays shorter than the list of features, and from then on
	/// WorldFeatures.UpdateFeatures throws in every frame the world is shown. In a running game that takes the rest of
	/// Game.UpdatePlay with it (the maps' updates, the game components), and the log gets an error per frame. Nothing
	/// about that is special to the phone: the PC build does the same with the game in Chinese.
	///
	/// Here the stand-in is given every character its font can draw: the engine's own lookup by size, the one the
	/// interface text goes through, at the size of the baked characters. The game's question "can TextMeshPro draw this
	/// name" is answered from that. A name that still cannot be drawn gets the old text object without the failure:
	/// nothing is drawn for it, and the world goes on.
	/// </summary>
	internal static class WorldLabels
	{
		private static FieldInfo atlasPixels;
		private static readonly HashSet<int> supplied = new HashSet<int>();
		private static int versionWithOurs = -1;
		private static bool saidSupplied, saidLegacy;

		public static void Start(Harmony harmony)
		{
			try
			{
				ITextMeshProBackend backend = TextMeshProBackend.Current;
				if (backend == null)
				{
					Console.WriteLine("[mobile] world names: no TextMeshPro backend, left alone");
					return;
				}
				Type type = backend.GetType();
				atlasPixels = AccessTools.Field(type, "atlasPixels");
				MethodInfo tryGetGlyph = AccessTools.Method(type, "TryGetGlyph");
				if (atlasPixels == null || tryGetGlyph == null)
				{
					Console.WriteLine("[mobile] world names: " + type.FullName + " is not the backend this was written for, left alone");
					return;
				}
				harmony.Patch(tryGetGlyph, postfix: new HarmonyMethod(typeof(WorldLabels), nameof(AnyCharacterOfTheFont)));
				harmony.Patch(AccessTools.Method(typeof(WorldFeatures), "HasCharactersUnsupportedByTextMeshPro"), prefix: new HarmonyMethod(typeof(WorldLabels), nameof(AskTheFont)));
				harmony.Patch(AccessTools.Method(typeof(WorldFeatureTextMesh_Legacy), nameof(WorldFeatureTextMesh_Legacy.Init)), finalizer: new HarmonyMethod(typeof(WorldLabels), nameof(WithoutRenderer)));
				Console.WriteLine("[mobile] world names: TextMeshPro gets any character of its font");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] world names: not patched: " + e);
			}
		}

		/// <summary>
		/// A character that is not among the baked ones: looked up in the font at the baked size, which draws it into the
		/// font atlas if it is not there yet. The stand-in draws from a copy of the atlas, taken at the end of a frame
		/// only when the atlas was replaced; a character drawn into it since is missing from the copy. So another copy is
		/// asked for whenever a character is supplied for the first time, and once after every copy that was not asked
		/// for here (the atlas was rebuilt, and the characters supplied here were drawn into it again after that copy).
		/// </summary>
		private static void AnyCharacterOfTheFont(object __instance, int fontIndex, char c, ref TmpGlyphMetrics glyph, ref bool __result)
		{
			if (__result || c < 256 || char.IsSurrogate(c))
			{
				return;
			}
			var backend = (ITextMeshProBackend)__instance;
			if (!backend.TryGetGlyphAtSize(fontIndex, c, backend.BakePixelSize, out glyph, out _) || !glyph.found)
			{
				glyph = default(TmpGlyphMetrics);
				return;
			}
			__result = true;
			if (supplied.Add(fontIndex << 16 | c) || backend.AtlasVersion != versionWithOurs)
			{
				atlasPixels.SetValue(__instance, IntPtr.Zero);
				versionWithOurs = backend.AtlasVersion + 1;
				if (!saidSupplied)
				{
					saidSupplied = true;
					Console.WriteLine("[mobile] world names: font " + fontIndex + " supplies characters beyond the baked ones (first: U+" + ((int)c).ToString("X4") + ")");
				}
			}
		}

		/// <summary>False (TextMeshPro can draw it) when the font has every character of the name; otherwise the game's own answer.</summary>
		private static bool AskTheFont(string str, ref bool __result)
		{
			try
			{
				ITextMeshProBackend backend = TextMeshProBackend.Current;
				if (backend == null || str == null)
				{
					return true;
				}
				TextMeshPro prefab = WorldFeatureTextMesh_TextMeshPro.WorldTextPrefab?.GetComponent<TextMeshPro>();
				int fontIndex = (prefab?.font ?? TMP_FontAsset.Default).fontIndex;
				foreach (char c in str)
				{
					if (!char.IsWhiteSpace(c) && !(backend.TryGetGlyph(fontIndex, c, out TmpGlyphMetrics glyph) && glyph.found))
					{
						return true;
					}
				}
				__result = false;
				return false;
			}
			catch (Exception)
			{
				return true;
			}
		}

		/// <summary>The old text object has no renderer in this engine: it stays as it is, drawing nothing.</summary>
		private static Exception WithoutRenderer(Exception __exception)
		{
			if (__exception is NullReferenceException)
			{
				if (!saidLegacy)
				{
					saidLegacy = true;
					Console.WriteLine("[mobile] world names: a name the font cannot draw is left out");
				}
				return null;
			}
			return __exception;
		}
	}
}
