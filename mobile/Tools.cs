using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace PortMobile
{
	/// <summary>Settings from mobile.txt next to the game ("key=value" lines).</summary>
	internal static class Settings
	{
		private static readonly Dictionary<string, string> values = new Dictionary<string, string>();

		static Settings()
		{
			try
			{
				string path = System.IO.Path.Combine(AppContext.BaseDirectory, "mobile.txt");
				if (File.Exists(path))
				{
					foreach (string line in File.ReadAllLines(path))
					{
						int eq = line.IndexOf('=');
						if (eq > 0 && !line.StartsWith("#"))
						{
							values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
						}
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mobile.txt not read: " + e.Message);
			}
		}

		public static bool Get(string key, bool fallback)
		{
			return values.TryGetValue(key, out string v) ? v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase) : fallback;
		}

		public static string Get(string key, string fallback)
		{
			return values.TryGetValue(key, out string v) ? v : fallback;
		}

		public static int Get(string key, int fallback)
		{
			return values.TryGetValue(key, out string v) && int.TryParse(v, out int n) ? n : fallback;
		}

		/// <summary>A setting chosen in the game: kept, and written to mobile.txt (its line replaced or added; the rest of the file stays).</summary>
		public static void Set(string key, string value)
		{
			values[key] = value;
			try
			{
				string path = System.IO.Path.Combine(AppContext.BaseDirectory, "mobile.txt");
				var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path) : new string[0]);
				int at = lines.FindIndex(l => !l.StartsWith("#") && l.IndexOf('=') > 0 && l.Substring(0, l.IndexOf('=')).Trim() == key);
				if (at >= 0)
				{
					lines[at] = key + "=" + value;
				}
				else
				{
					lines.Add(key + "=" + value);
				}
				File.WriteAllLines(path, lines.ToArray());
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] mobile.txt not written: " + e.Message);
			}
		}

		/// <summary>Buildings and drags are placed by a confirm button rather than by the finger lifting (not with a mouse: InputMode).</summary>
		public static bool PlaceConfirm => placeConfirm && !InputMode.Mouse;

		private static readonly bool placeConfirm = Get("placeConfirm", true);
	}

	/// <summary>
	/// The map tools (designators, targeters) under a finger. A press reaches them at once (the touch layer sends
	/// it like a mouse's), so what acts on the press is held back here until the finger lifts:
	///   placing a building   nothing happens; the outline moves to where the finger lifts (Placement), the
	///   (or any tool that    blueprint is put down by the confirm button
	///   acts on one cell)
	///   picking a target     the choice is made where the finger lifts, so that a drag can still look around
	///   designating by drag  the drag marks its cells (DragConfirm); the confirm button designates them
	///   a dev mode tool      the cell where the finger lifts is marked (DebugConfirm); the confirm button acts there
	/// A drag with such a deferred press turns into a pan (Touch tells us: CancelDeferred).
	/// </summary>
	internal static class MapTools
	{
		private static bool deferred, deferredOnWorld, deferredIsPlacing, deferredCancelled, converted;
		private static bool deferredOnlyMoves, deferredDebug;

		public static bool Deferred => deferred;

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(MapInterface), nameof(MapInterface.HandleMapClicks)), prefix: new HarmonyMethod(typeof(MapTools), nameof(BeforeMapTools)), postfix: new HarmonyMethod(typeof(MapTools), nameof(AfterMapTools)));
			harmony.Patch(AccessTools.Method(typeof(WorldTargeter), nameof(WorldTargeter.ProcessInputEvents)), prefix: new HarmonyMethod(typeof(MapTools), nameof(BeforeWorldTargeter)), postfix: new HarmonyMethod(typeof(MapTools), nameof(AfterMapTools)));
			Placement.Start(harmony);
			WorldConfirm.Start(harmony);
			DragConfirm.Start(harmony);
			LandingConfirm.Start(harmony);
			DebugConfirm.Start(harmony);
		}

		/// <summary>Every frame (from Touch).</summary>
		public static void Update()
		{
			Placement.Update();
			DragConfirm.Update();
			DebugConfirm.Update();
		}

		/// <summary>The finger that pressed is now panning the map (or a second finger joined): the held-back press is dropped.</summary>
		public static void CancelDeferred()
		{
			deferredCancelled = true;
			Placement.TouchEnded(true);
			DragConfirm.DragCancelled();
			if (Current.ProgramState == ProgramState.Playing && Find.DesignatorManager != null && Find.DesignatorManager.Dragger.Dragging)
			{
				Find.DesignatorManager.Dragger.EndDrag();
			}
		}

		/// <summary>Prefix of MapInterface.HandleMapClicks: before the designator and the targeter get the event.</summary>
		public static void BeforeMapTools()
		{
			Event current = Event.current;
			if (current == null || current.button != 0)
			{
				return;
			}
			if (current.type == EventType.MouseDown)
			{
				deferred = deferredOnWorld = deferredDebug = false;
				if (Find.CurrentMap == null || !WorldRendererUtility.DrawingMap || !Touch.TouchInProgress || !Settings.PlaceConfirm)
				{
					return;
				}
				DesignatorManager tools = Find.DesignatorManager;
				// A tool that reads the mouse button itself gets the press now, as from a mouse (RawMouseTools).
				bool placing = tools.SelectedDesignator != null && tools.SelectedStyle == null && !RawMouseTools.Is(tools.SelectedDesignator);
				bool targeting = tools.SelectedDesignator == null && Find.Targeter.IsTargeting;
				// A dev mode tool acts on the press (DebugTool.DebugToolOnGUI, after the map's clicks): the first touch of a
				// drag across the map acted where it began.
				bool debugging = tools.SelectedDesignator == null && !targeting && DebugConfirm.Tool() != null;
				if (tools.SelectedDesignator != null && tools.SelectedStyle != null)
				{
					DragConfirm.DragStarted();
				}
				if (Touch.Verbose) Console.WriteLine("[mobile] map tools get a press: placing " + placing + ", targeting " + targeting + ", dragging " + (tools.SelectedDesignator != null && tools.SelectedStyle != null) + ", dev tool " + debugging + ", tool " + (tools.SelectedDesignator?.GetType().Name ?? "none"));
				if (placing || targeting || debugging)
				{
					// All act the moment the button goes down, at a place the player could not see beforehand.
					deferred = true;
					deferredIsPlacing = placing;
					deferredDebug = debugging;
					deferredCancelled = false;
					Designator building = placing ? Placement.Tool() : null;
					deferredOnlyMoves = building != null;
					if (deferredOnlyMoves)
					{
						Placement.TouchBegan(building);
					}
					current.Use();
				}
			}
			else if (current.rawType == EventType.MouseUp && deferred && !deferredOnWorld)
			{
				deferred = false;
				if (deferredDebug)
				{
					deferredDebug = false;
					if (!deferredCancelled && current.type == EventType.MouseUp)
					{
						// Marked where the finger lifted; the confirm button acts there.
						DebugConfirm.Tapped();
						current.Use();
					}
				}
				else if (deferredOnlyMoves)
				{
					deferredOnlyMoves = false;
					Placement.TouchEnded(deferredCancelled);
				}
				else if (!deferredCancelled && current.type == EventType.MouseUp)
				{
					if (!deferredIsPlacing && LandingConfirm.Mark())
					{
						// A landing spot: marked here, chosen by the confirm button.
						current.Use();
						return;
					}
					// The choice is made now, where the finger lifted: the tool sees the press it was kept from.
					current.type = EventType.MouseDown;
					converted = true;
				}
			}
			else if (current.type == EventType.MouseUp && !deferred && Find.CurrentMap != null && DragConfirm.DragEnded())
			{
				// The drag's cells are marked, not designated: the game must not see the release.
				current.Use();
			}
		}

		public static void AfterMapTools()
		{
			if (converted)
			{
				converted = false;
				// Left over (the tool went away meanwhile): it must not start a selection box that no release ends.
				if (Event.current.type == EventType.MouseDown)
				{
					Event.current.Use();
				}
			}
		}

		/// <summary>The same for the world: picking a tile acts when the button goes down, so the first touch of a drag across the globe would be the choice.</summary>
		private static readonly AccessTools.FieldRef<WorldTargeter, Action> WorldTargeterUpdate = AccessTools.FieldRefAccess<WorldTargeter, Action>("onUpdate");

		public static void BeforeWorldTargeter(WorldTargeter __instance)
		{
			Event current = Event.current;
			if (current == null || current.button != 0)
			{
				return;
			}
			if (current.type == EventType.MouseDown)
			{
				deferred = deferredOnWorld = false;
				if (!__instance.IsTargeting || !Touch.TouchInProgress || !Settings.PlaceConfirm)
				{
					return;
				}
				if (RawMouseTools.Reads(WorldTargeterUpdate(__instance)))
				{
					// A targeting that watches the held button itself (God Hands' axis fall charges while it is held on a
					// tile): the press now, as from a mouse, and no second tap.
					return;
				}
				deferred = deferredOnWorld = true;
				deferredIsPlacing = false;
				deferredCancelled = false;
				current.Use();
			}
			else if (current.rawType == EventType.MouseUp && deferred && deferredOnWorld)
			{
				deferred = deferredOnWorld = false;
				if (!deferredCancelled && current.type == EventType.MouseUp && __instance.IsTargeting)
				{
					if (WorldConfirm.FirstTap(__instance))
					{
						current.Use();
						return;
					}
					current.type = EventType.MouseDown;
					converted = true;
				}
			}
		}
	}

	/// <summary>
	/// A target on the world is chosen by two taps on it (testers, 2026-10-20: a tap on the globe sent a shuttle off at
	/// once, an empty tile included). The first tap marks it (the marker stays on it, a message says to tap again); a
	/// second tap on the same tile or world object is the choice, a tap elsewhere marks that instead. With a mouse (no
	/// touch) the game's own single click. mobile.txt: worldConfirm=0 leaves it out.
	/// </summary>
	internal static class WorldConfirm
	{
		private static readonly bool On = Settings.Get("worldConfirm", true);
		private static readonly AccessTools.FieldRef<WorldTargeter, Func<GlobalTargetInfo, bool>> CanSelect = AccessTools.FieldRefAccess<WorldTargeter, Func<GlobalTargetInfo, bool>>("canSelectTarget");
		private static readonly Func<WorldTargeter, GlobalTargetInfo> UnderMouse = AccessTools.MethodDelegate<Func<WorldTargeter, GlobalTargetInfo>>(AccessTools.Method(typeof(WorldTargeter), "CurrentTargetUnderMouse"));
		private static readonly AccessTools.FieldRef<WorldTargeter, Func<GlobalTargetInfo, bool>> Action = AccessTools.FieldRefAccess<WorldTargeter, Func<GlobalTargetInfo, bool>>("action");
		private static GlobalTargetInfo marked = GlobalTargetInfo.Invalid;
		// The targeting the mark belongs to (a new one, begun without a stop the patches saw, starts unmarked).
		private static Func<GlobalTargetInfo, bool> markedFor;

		/// <summary>A tap (lifted) while the world targeter is on: true when it only marked the target (the event is not the targeter's).</summary>
		public static bool FirstTap(WorldTargeter targeter)
		{
			if (!On)
			{
				return false;
			}
			GlobalTargetInfo target = UnderMouse(targeter);
			Func<GlobalTargetInfo, bool> canSelect = CanSelect(targeter);
			if (!target.IsValid || (canSelect != null && !canSelect(target)))
			{
				// Not a target: the targeter does with the tap what it does (nothing, or says why not).
				return false;
			}
			if (marked.IsValid && marked == target && markedFor == Action(targeter))
			{
				Clear();
				return false;
			}
			marked = target;
			markedFor = Action(targeter);
			SoundDefOf.Tick_Low.PlayOneShotOnCamera();
			Messages.Message(Texts.Chinese ? "再点一次同一个地方确认目标（点别处换目标，返回键取消）" : "Tap the same place again to confirm the target (elsewhere to change it, Back to cancel)", MessageTypeDefOf.SilentInput, false);
			return true;
		}

		public static void Start(Harmony harmony)
		{
			if (!On)
			{
				return;
			}
			try
			{
				var clear = new HarmonyMethod(typeof(WorldConfirm), nameof(Clear));
				harmony.Patch(AccessTools.Method(typeof(WorldTargeter), nameof(WorldTargeter.BeginTargeting)), postfix: clear);
				harmony.Patch(AccessTools.Method(typeof(WorldTargeter), nameof(WorldTargeter.StopTargeting)), postfix: clear);
				harmony.Patch(AccessTools.Method(typeof(WorldTargeter), nameof(WorldTargeter.TargeterUpdate)), postfix: new HarmonyMethod(typeof(WorldConfirm), nameof(DrawMarked)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] world confirm: not patched: " + e.Message);
			}
		}

		public static void Clear()
		{
			marked = GlobalTargetInfo.Invalid;
			markedFor = null;
		}

		/// <summary>Postfix of WorldTargeter.TargeterUpdate: the marked target keeps its marker wherever the finger goes.</summary>
		public static void DrawMarked(WorldTargeter __instance)
		{
			if (!marked.IsValid || !__instance.IsTargeting || markedFor != Action(__instance))
			{
				return;
			}
			if (marked.HasWorldObject && marked.WorldObject.Destroyed)
			{
				marked = GlobalTargetInfo.Invalid;
				return;
			}
			Vector3 pos = marked.HasWorldObject ? marked.WorldObject.DrawPos : Find.WorldGrid.GetTileCenter(marked.Tile);
			WorldRendererUtility.DrawQuadTangentialToPlanet(pos, 0.8f * Find.WorldGrid.AverageTileSize, 0.05f, WorldMaterials.CurTargetingMat);
		}
	}

	/// <summary>
	/// Placing a building in two steps. With a mouse the building's outline follows the pointer and a click puts
	/// the blueprint down; on a touch screen every touch is a click, so a finger that only meant to look around
	/// left blueprints behind. Here a touch on the map only moves the outline (a tap puts it there, a drag
	/// carries it along), and the blueprint is placed by a "confirm" button next to the game's rotate buttons.
	///
	/// The outline is wherever the game thinks the mouse is. While a place is chosen, the game is told that the
	/// mouse is over that map cell, whatever the fingers do meanwhile (press buttons, move the camera).
	/// </summary>
	/// <summary>
	/// Picking a spot on the map for something to land (a shuttle's landing spot, drop pods' destination, any targeting
	/// of locations that draws a ghost of what will come down; not one that picks a thing) in two steps, like placing a building (the user, 2026-10-23: "穿梭机无法
	/// 在降落的时候旋转，没有旋转按钮", "降落也没有确定键"; a tap landed the shuttle at once). A tap marks the cell: the
	/// targeting's ghost stays there whatever the finger does next, a tap elsewhere moves it. "Confirm" lands there (the
	/// game's own checks, e.g. "blocked by a tree", still say no), "cancel" ends the targeting. A targeting that turns its
	/// ghost with the rotate keys (MapParent's shuttle landing reads Q/E in its GUI action) gets the game's rotate buttons,
	/// which press those keys. With a mouse (no touch) nothing is marked: the game's own single click.
	/// </summary>
	internal static class LandingConfirm
	{
		private static readonly AccessTools.FieldRef<Targeter, Action<LocalTargetInfo>> Action = AccessTools.FieldRefAccess<Targeter, Action<LocalTargetInfo>>("action");
		private static readonly AccessTools.FieldRef<Targeter, Action<LocalTargetInfo>> Highlight = AccessTools.FieldRefAccess<Targeter, Action<LocalTargetInfo>>("highlightAction");
		private static readonly AccessTools.FieldRef<Targeter, Action<LocalTargetInfo>> OnGui = AccessTools.FieldRefAccess<Targeter, Action<LocalTargetInfo>>("onGuiAction");
		private static readonly AccessTools.FieldRef<Targeter, Func<LocalTargetInfo, bool>> Validator = AccessTools.FieldRefAccess<Targeter, Func<LocalTargetInfo, bool>>("targetValidator");
		private static readonly AccessTools.FieldRef<Targeter, TargetingParameters> Params = AccessTools.FieldRefAccess<Targeter, TargetingParameters>("targetParams");
		private static readonly AccessTools.FieldRef<Targeter, bool> PlaySound = AccessTools.FieldRefAccess<Targeter, bool>("playSoundOnAction");
		private static readonly AccessTools.FieldRef<Targeter, bool> NeedsStop = AccessTools.FieldRefAccess<Targeter, bool>("needsStopTargetingCall");
		private static LocalTargetInfo marked = LocalTargetInfo.Invalid;
		private static Map markedMap;
		// The targeting the mark belongs to (a new one starts unmarked).
		private static Action<LocalTargetInfo> markedFor, toldFor;
		private static readonly Dictionary<System.Reflection.MethodInfo, bool> rotating = new Dictionary<System.Reflection.MethodInfo, bool>();

		public static void Start(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(Targeter), "CurrentTargetUnderMouse"), prefix: new HarmonyMethod(typeof(LandingConfirm), nameof(MarkedTarget)));
			harmony.Patch(AccessTools.Method(typeof(Targeter), nameof(Targeter.TargeterOnGUI)), postfix: new HarmonyMethod(typeof(LandingConfirm), nameof(Controls)));
			// A new targeting starts unmarked. The mark belongs to the targeting's action, and a mod that passes the same
			// method every time (Monolyn Race's "relocate", CompTeleport.OnTargetSelected: equal delegates) began the next
			// relocation of that building with the last spot already marked.
			foreach (System.Reflection.MethodInfo begin in AccessTools.GetDeclaredMethods(typeof(Targeter)))
			{
				if (begin.Name == nameof(Targeter.BeginTargeting))
				{
					harmony.Patch(begin, postfix: new HarmonyMethod(typeof(LandingConfirm), nameof(Unmark)));
				}
			}
		}

		/// <summary>Postfix of Targeter.BeginTargeting: nothing is marked yet.</summary>
		public static void Unmark()
		{
			marked = LocalTargetInfo.Invalid;
			markedFor = null;
		}

		/// <summary>The targeting picks a landing spot on the map.</summary>
		private static bool Applies(Targeter targeter)
		{
			if (!Settings.PlaceConfirm || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap
				|| targeter == null || targeter.targetingSource != null || Action(targeter) == null || Find.DesignatorManager.SelectedDesignator != null)
			{
				return false;
			}
			// A spot on the ground only: a targeting that picks a thing is the game's tap. The Anomaly holding platforms'
			// "transfer" (TargetingParameters.ForBuilding, with a highlight) was marked as a cell, and "confirm" then gave its
			// validator a cell, not a platform: nothing happened (a tester, 2026-10-24: "收容平台没法换").
			TargetingParameters p = Params(targeter);
			if (p == null || !p.canTargetLocations)
			{
				return false;
			}
			// A ghost of what comes down (the shuttle's ForCell leaves canTargetPawns on), or nothing but locations.
			return Highlight(targeter) != null || (!p.canTargetPawns && !p.canTargetBuildings && !p.canTargetItems);
		}

		private static bool IsMarked(Targeter targeter)
		{
			return marked.IsValid && markedFor != null && markedFor == Action(targeter) && markedMap == Find.CurrentMap;
		}

		/// <summary>The finger lifted on the map while targeting (MapTools): true when it only marked a landing spot.</summary>
		public static bool Mark()
		{
			Targeter targeter = Find.Targeter;
			if (!Applies(targeter))
			{
				return false;
			}
			IntVec3 cell = UI.MouseCell();
			if (!cell.InBounds(Find.CurrentMap))
			{
				return true;
			}
			marked = new LocalTargetInfo(cell);
			markedMap = Find.CurrentMap;
			markedFor = Action(targeter);
			SoundDefOf.Tick_Low.PlayOneShotOnCamera();
			if (toldFor != markedFor)
			{
				toldFor = markedFor;
				// Not only landings: mods move and build things this way (Monolyn Race's relocate and transform).
				Messages.Message(Texts.Chinese ? "点左下角的“确定”选定这个位置（点别处换位置；能旋转的用旋转按钮）" : "Tap Confirm (bottom left) to choose this spot (tap elsewhere to move it; rotate with the arrows where it turns)", MessageTypeDefOf.SilentInput, false);
			}
			return true;
		}

		/// <summary>Prefix of Targeter.CurrentTargetUnderMouse: the marked spot, wherever the finger went after marking it.</summary>
		public static bool MarkedTarget(Targeter __instance, ref LocalTargetInfo __result)
		{
			if (!IsMarked(__instance))
			{
				return true;
			}
			__result = marked;
			return false;
		}

		/// <summary>Postfix of Targeter.TargeterOnGUI: the buttons, above the bottom bar (above the inspect pane when it is open).</summary>
		public static void Controls(Targeter __instance)
		{
			if (!Applies(__instance))
			{
				return;
			}
			float bottomY = Find.MainTabsRoot.OpenTab == MainButtonDefOf.Inspect ? MainButtonDefOf.Inspect.TabWindow.windowRect.y - 8f : UI.screenHeight - 35 - 10;
			float leftX = 0f;
			if (Rotates(OnGui(__instance)))
			{
				DesignatorUtility.GUIDoRotationControls(0f, bottomY, Rot4.North, delegate(Rot4 rot)
				{
					// The targeting turns its own ghost when it sees the key: the button presses it.
					KeyWheel.TapKey(rot == Rot4.East ? KeyBindingDefOf.Designator_RotateRight : KeyBindingDefOf.Designator_RotateLeft);
				});
				leftX = 200f;
			}
			var window = new Rect(leftX, bottomY - 90f, 200f, 90f);
			Find.WindowStack.ImmediateWindow(73195734, window, WindowLayer.GameUI, delegate
			{
				Text.Font = GameFont.Small;
				var inner = new Rect(0f, 0f, window.width, window.height).ContractedBy(10f);
				float width = (inner.width - 10f) / 2f;
				if (Widgets.ButtonText(new Rect(inner.x, inner.y, width, inner.height), "Confirm".Translate()))
				{
					Confirm();
				}
				if (Widgets.ButtonText(new Rect(inner.xMax - width, inner.y, width, inner.height), "CancelButton".Translate()))
				{
					SoundDefOf.CancelMode.PlayOneShotOnCamera();
					Find.Targeter.StopTargeting();
				}
			});
		}

		/// <summary>What the game does for a click (Targeter.ProcessInputEvents, a targeting without a verb), at the marked spot.</summary>
		private static void Confirm()
		{
			Targeter targeter = Find.Targeter;
			if (!Applies(targeter) || !IsMarked(targeter))
			{
				Messages.Message(Texts.Chinese ? "先在地图上点一下要选的位置。" : "Tap the spot on the map first.", MessageTypeDefOf.RejectInput, false);
				return;
			}
			LocalTargetInfo target = marked;
			Action<LocalTargetInfo> action = Action(targeter);
			Func<LocalTargetInfo, bool> validator = Validator(targeter);
			NeedsStop(targeter) = true;
			if (validator != null)
			{
				if (validator(target))
				{
					action(target);
				}
				else
				{
					NeedsStop(targeter) = false;
				}
			}
			else if (target.IsValid)
			{
				action(target);
			}
			if (PlaySound(targeter))
			{
				SoundDefOf.Tick_High.PlayOneShotOnCamera();
			}
			if (NeedsStop(targeter))
			{
				targeter.StopTargeting();
			}
		}

		/// <summary>The targeting's GUI action reads the rotate keys (KeyBindingDefOf.Designator_RotateLeft/Right in its IL).</summary>
		private static bool Rotates(Action<LocalTargetInfo> gui)
		{
			if (gui == null)
			{
				return false;
			}
			System.Reflection.MethodInfo method = gui.Method;
			if (!rotating.TryGetValue(method, out bool turns))
			{
				try
				{
					System.Reflection.FieldInfo left = AccessTools.Field(typeof(KeyBindingDefOf), nameof(KeyBindingDefOf.Designator_RotateLeft));
					System.Reflection.FieldInfo right = AccessTools.Field(typeof(KeyBindingDefOf), nameof(KeyBindingDefOf.Designator_RotateRight));
					byte[] il = method.GetMethodBody()?.GetILAsByteArray();
					for (int i = 0; il != null && i + 4 < il.Length && !turns; i++)
					{
						if (il[i] != 0x7E)
						{
							continue;
						}
						try
						{
							System.Reflection.FieldInfo field = method.Module.ResolveField(BitConverter.ToInt32(il, i + 1));
							turns = field == left || field == right;
						}
						catch (Exception)
						{
						}
					}
				}
				catch (Exception)
				{
				}
				rotating[method] = turns;
			}
			return turns;
		}
	}

	/// <summary>
	/// A tool that acts on one cell (a building, and any tool without a drawing style: a mod's prefab or projection, the
	/// game's plan paste or gravship move) is put down in two steps: a tap marks the cell, the tool's outline stays there,
	/// and "confirm" acts there. The user, 2026-10-26, of Universal Blueprints: "手机一下误触就放了很多个" - its prefab
	/// and projection tools stay selected and acted on every tap.
	/// </summary>
	internal static class Placement
	{
		private static Designator tool;
		private static bool chosen, following, hadChoice;
		private static IntVec3 cell, cellBefore;
		private static int controlsFrame = -10;
		private static bool lastResort, drew, complained;
		private static Harmony harmony;
		private static readonly HashSet<System.Reflection.MethodInfo> patchedControls = new HashSet<System.Reflection.MethodInfo>();
		private static readonly HashSet<Type> checkedTypes = new HashSet<Type>();

		public static void Start(Harmony harmony)
		{
			Placement.harmony = harmony;
			harmony.Patch(AccessTools.Method(typeof(UI), nameof(UI.MouseCell)), prefix: new HarmonyMethod(typeof(Placement), nameof(MouseCell)));
			harmony.Patch(AccessTools.Method(typeof(UI), nameof(UI.MouseMapPosition)), prefix: new HarmonyMethod(typeof(Placement), nameof(MouseMapPosition)));
			harmony.Patch(AccessTools.Method(typeof(Designator_Place), nameof(Designator_Place.DoExtraGuiControls)), prefix: new HarmonyMethod(typeof(Placement), nameof(KeepControlsOnScreen)), postfix: new HarmonyMethod(typeof(Placement), nameof(Controls)));
			harmony.Patch(AccessTools.Method(typeof(DesignatorManager), nameof(DesignatorManager.DesignationManagerOnGUI)), postfix: new HarmonyMethod(typeof(Placement), nameof(ControlsOfLastResort)));
			harmony.Patch(AccessTools.Method(typeof(DesignatorUtility), nameof(DesignatorUtility.GUIDoRotationControls)), prefix: new HarmonyMethod(typeof(Placement), nameof(SeeRotationControls)));
		}

		/// <summary>The selected tool, if it is one that acts on the single cell under the pointer.</summary>
		public static Designator Tool()
		{
			if (!Settings.PlaceConfirm || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
			{
				return null;
			}
			DesignatorManager manager = Find.DesignatorManager;
			Designator selected = manager.SelectedDesignator;
			if (selected == null || manager.SelectedStyle != null)
			{
				return null;
			}
			// Tools that follow the mouse themselves act as with a mouse (RawMouseTools, DragConfirm.KeepsOwnDrag); a tool
			// without a cell of its own (a mod's library browser selected while its window is open) has nothing to confirm.
			return selected is Designator_Place || (ActsOnCells(selected) && !RawMouseTools.Is(selected) && !DragConfirm.KeepsOwnDrag(selected)) ? selected : null;
		}

		private static readonly Dictionary<Type, bool> actsOnCells = new Dictionary<Type, bool>();

		/// <summary>
		/// A tool that puts something down at a cell (its own DesignateSingleCell). One that marks the thing under the
		/// finger (its own DesignateThing: release to the wild) or picks a colour (an eyedropper) acts on the tap.
		/// </summary>
		private static bool ActsOnCells(Designator designator)
		{
			Type type = designator.GetType();
			if (!actsOnCells.TryGetValue(type, out bool acts))
			{
				acts = AccessTools.Method(type, nameof(Designator.DesignateSingleCell))?.DeclaringType != typeof(Designator)
					&& AccessTools.Method(type, nameof(Designator.DesignateThing))?.DeclaringType == typeof(Designator)
					&& !(designator is Designator_Eyedropper);
				actsOnCells[type] = acts;
			}
			return acts;
		}

		/// <summary>Prefix of UI.MouseCell.</summary>
		public static bool MouseCell(ref IntVec3 __result)
		{
			if (chosen && !following)
			{
				__result = cell;
				return false;
			}
			if (DebugConfirm.Marked(out IntVec3 marked))
			{
				__result = marked;
				return false;
			}
			return true;
		}

		/// <summary>Prefix of UI.MouseMapPosition: the middle of the marked cell (dev mode tools that place by position).</summary>
		public static bool MouseMapPosition(ref Vector3 __result)
		{
			if (chosen && !following)
			{
				__result = cell.ToVector3Shifted();
				return false;
			}
			if (DebugConfirm.Marked(out IntVec3 marked))
			{
				__result = marked.ToVector3Shifted();
				return false;
			}
			return true;
		}

		public static void Update()
		{
			if (!chosen && !following && tool == null)
			{
				Designator selected = Tool();
				if (selected != null && !(selected is Designator_Place))
				{
					PatchControls(selected);
				}
				return;
			}
			if (Tool() != tool)
			{
				tool = null;
				chosen = following = false;
			}
		}

		/// <summary>
		/// A tool's own DoExtraGuiControls (the game's plan paste and gravship move draw rotate buttons there, without the
		/// base method that has the buttons' postfix) gets the confirm buttons after what it draws, once per method.
		/// </summary>
		private static void PatchControls(Designator selected)
		{
			if (!checkedTypes.Add(selected.GetType()))
			{
				return;
			}
			System.Reflection.MethodInfo method = AccessTools.Method(selected.GetType(), nameof(Designator.DoExtraGuiControls));
			if (method == null || method.DeclaringType == typeof(Designator) || !patchedControls.Add(method))
			{
				return;
			}
			try
			{
				harmony.Patch(method, prefix: new HarmonyMethod(typeof(Placement), nameof(KeepOwnControlsOnScreen)), postfix: new HarmonyMethod(typeof(Placement), nameof(OwnControls)));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] the controls of " + method.DeclaringType.FullName + " get no confirm buttons: " + e.Message);
			}
		}

		// The parameters by position: an override may name them otherwise.
		public static void KeepOwnControlsOnScreen(ref float __0, ref float __1)
		{
			KeepControlsOnScreen(ref __0, ref __1);
		}

		public static void OwnControls(Designator __instance, float __0, float __1)
		{
			NoteControls();
			if (!(__instance is Designator_Place) && Tool() == __instance)
			{
				Draw(AfterGameControls(__0, __1));
			}
		}

		/// <summary>
		/// Where the buttons go for a tool that is not a building: right of the window the game (or the tool) drew its
		/// controls in this frame, at (leftX, bottomY - 90), or there when it drew none.
		/// </summary>
		internal static Rect AfterGameControls(float leftX, float bottomY)
		{
			float x = leftX;
			IList<Window> windows = Find.WindowStack.Windows;
			for (int i = 0; i < windows.Count; i++)
			{
				Window window = windows[i];
				// At leftX, or further right when it was moved with the architect's menu off the camera's hole (Cutout). Not the
				// buttons' own window: the stack holds an immediate window under the negative of its id, and until 2026-10-27
				// the buttons took their own window of the last frame for the tool's, moved 230 units right each frame and back
				// (a tester's video: Ascension Megacorp's air-dropped buildings, the buttons jumping and flickering).
				int id = -window.ID;
				if (window is ImmediateWindow && (id < OwnWindows || id > OwnWindows + 9) && window.windowRect.x > leftX - 1f && window.windowRect.x < leftX + 400f && Mathf.Abs(window.windowRect.yMax - bottomY) < 1f)
				{
					x = Mathf.Max(x, window.windowRect.xMax + Gap);
				}
			}
			return new Rect(x, bottomY - 90f, 200f, 90f);
		}

		// The immediate windows of the confirm buttons (73195731 ... 73195739).
		internal const int OwnWindows = 73195730;

		/// <summary>A finger landed on the map with the tool active: the outline goes with it.</summary>
		public static void TouchBegan(Designator selected)
		{
			if (selected != tool)
			{
				tool = selected;
				chosen = false;
			}
			hadChoice = chosen;
			cellBefore = cell;
			following = true;
		}

		/// <summary>The finger lifted (the outline stays there) or the touch became something else.</summary>
		public static void TouchEnded(bool cancelled)
		{
			if (!following)
			{
				return;
			}
			following = false;
			if (cancelled)
			{
				chosen = hadChoice;
				cell = cellBefore;
			}
			else
			{
				cell = UI.UIToMapPosition(UI.MousePositionOnUI).ToIntVec3();
				chosen = true;
			}
		}

		// The immediate window of an open inspect tab (InspectTabBase.DoTabGUI), as the window stack numbers it.
		private const int InspectTabWindowId = -235086;

		// Between the game's controls and the confirm buttons. The game's right arrow (rotate, next drawing style) reaches
		// the right edge of its window, and the confirm button began ten units further: a finger meant for "confirm" turned
		// the plan's shape to a hollow rectangle (the user, 2026-10-26: "点确认好像和旋转按钮重合了"). A tap in the gap
		// presses nothing (TapAssist reaches only small controls).
		internal const float Gap = 30f;

		/// <summary>
		/// Prefix of DoExtraGuiControls (Designator_Place's and the base one). The game puts a tool's extra controls (rotate buttons, drawing styles) in a
		/// small window above the description box; when that is above the top of the screen (a tall Architect menu
		/// at a large UI scale) they go to the right of the description box instead, and to the right of an open
		/// inspect tab that would cover them.
		/// </summary>
		public static void KeepControlsOnScreen(ref float leftX, ref float bottomY)
		{
			if (bottomY - 90f < 0f)
			{
				leftX += 208f;
				bottomY = Mathf.Max(0f, bottomY) + 90f;
			}
			var controls = new Rect(leftX, bottomY - 90f, 400f + Gap, 90f);
			IList<Window> windows = Find.WindowStack.Windows;
			for (int i = 0; i < windows.Count; i++)
			{
				Window window = windows[i];
				if (window.ID == InspectTabWindowId && window is ImmediateWindow && window.windowRect.Overlaps(controls))
				{
					leftX = window.windowRect.xMax + 8f;
					break;
				}
			}
		}

		/// <summary>A tool's extra controls were drawn by the game this frame.</summary>
		internal static void NoteControls()
		{
			if (!lastResort)
			{
				controlsFrame = Time.frameCount;
			}
		}

		internal static void NoteDrew()
		{
			drew = true;
		}

		private static bool Rotates(Designator_Place selected)
		{
			return selected.PlacingDef is ThingDef def && def.rotatable;
		}

		/// <summary>Postfix of Designator_Place.DoExtraGuiControls: the buttons, right of the rotate buttons.</summary>
		public static void Controls(Designator_Place __instance, float leftX, float bottomY)
		{
			NoteControls();
			if (Tool() == __instance)
			{
				Draw(ButtonsRect(__instance, leftX, bottomY));
			}
			else if (Rotates(__instance) && DragConfirm.Tool() == __instance)
			{
				DragConfirm.Draw(DragConfirm.ButtonsRect(__instance, leftX, bottomY, true));
			}
		}

		private static Rect ButtonsRect(Designator_Place selected, float leftX, float bottomY)
		{
			// Right of the rotate buttons where they were drawn in this frame. A tool or mod that puts them somewhere else
			// than the place it was given (its own column under the info box) had the buttons 200 units off to the right
			// (2026-10-27, a tester's screenshot: "确定按钮漂移了"). Their asked-for place, not the window's: the camera hole
			// moves both windows alike afterwards.
			Rect at = rotationFrame == Time.frameCount ? new Rect(rotationAt.x + 200f + Gap, rotationAt.y - 90f, 200f, 90f)
				: new Rect(leftX + (Rotates(selected) ? 200f + Gap : 0f), bottomY - 90f, 200f, 90f);
			if (selected != toldFor)
			{
				toldFor = selected;
				Console.WriteLine("[mobile] confirm buttons for " + selected.GetType().Name + " " + (selected.PlacingDef?.defName ?? "?") + ": controls asked at (" + leftX + ", " + bottomY + ")"
					+ (rotationFrame == Time.frameCount ? ", rotate buttons at (" + rotationAt.x + ", " + rotationAt.y + ")" : ", no rotate buttons") + ", buttons at " + at);
			}
			return at;
		}

		// Where DesignatorUtility.GUIDoRotationControls was asked to draw last (leftX, bottomY), and in which frame.
		private static Vector2 rotationAt;
		private static int rotationFrame = -10;
		private static Designator toldFor;

		/// <summary>Where the rotate buttons were asked for in this frame (leftX, bottomY), if they were.</summary>
		internal static bool RotationDrawn(out Vector2 at)
		{
			at = rotationAt;
			return rotationFrame == Time.frameCount;
		}

		/// <summary>Prefix of DesignatorUtility.GUIDoRotationControls: where the rotate buttons go.</summary>
		public static void SeeRotationControls(float leftX, float bottomY)
		{
			rotationAt = new Vector2(leftX, bottomY);
			rotationFrame = Time.frameCount;
		}

		/// <summary>
		/// Postfix of DesignatorManager.DesignationManagerOnGUI: the tool's controls when the game does not show them: the
		/// Architect menu closed with the tool still selected, or open with no category open (the game draws them only
		/// from the open category; tapping the category again closes it and leaves the tool selected).
		/// </summary>
		public static void ControlsOfLastResort()
		{
			if (controlsFrame >= Time.frameCount - 2 || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
			{
				return;
			}
			Designator selected = Find.DesignatorManager.SelectedDesignator;
			if (selected == null)
			{
				return;
			}
			bool placing = Tool() == selected;
			bool dragging = DragConfirm.Tool() == selected;
			bool ownDrag = DragConfirm.OwnDragTool() == selected;
			if (!(selected is Designator_Place) && !placing && !dragging && !ownDrag)
			{
				return;
			}
			float bottomY = ControlsBottom();
			lastResort = true;
			drew = false;
			try
			{
				selected.DoExtraGuiControls(0f, bottomY);
			}
			catch (Exception e)
			{
				if (!complained)
				{
					complained = true;
					Log.Error("PortMobile tool controls failed: " + e);
				}
			}
			finally
			{
				lastResort = false;
			}
			if (drew)
			{
				return;
			}
			if (Tool() == selected)
			{
				Draw(selected is Designator_Place place ? ButtonsRect(place, 0f, bottomY) : AfterGameControls(0f, bottomY));
			}
			else if (dragging)
			{
				DragConfirm.Draw(DragConfirm.ButtonsRect(selected, 0f, bottomY, selected is Designator_Place rotating && Rotates(rotating)));
			}
			else
			{
				DragConfirm.DrawPutAway(DragConfirm.ButtonsRect(selected, 0f, bottomY, false));
			}
		}

		/// <summary>
		/// Where tool controls go when the game does not place them: with the Architect menu open its categories cover the
		/// bottom left, so above them, as the game puts them; the inspect pane (things selected, as Select similar leaves
		/// them) is there too: above it.
		/// </summary>
		internal static float ControlsBottom()
		{
			MainButtonDef openTab = Find.MainTabsRoot.OpenTab;
			return openTab == MainButtonDefOf.Architect ? UI.screenHeight - 35 - ((MainTabWindow_Architect)MainButtonDefOf.Architect.TabWindow).WinHeight - 270f
				: openTab == MainButtonDefOf.Inspect ? MainButtonDefOf.Inspect.TabWindow.windowRect.y - 8f
				: UI.screenHeight - 35 - 10;
		}

		private static void Draw(Rect window)
		{
			NoteDrew();
			Find.WindowStack.ImmediateWindow(73195731, window, WindowLayer.GameUI, delegate
			{
				Text.Font = GameFont.Small;
				var inner = new Rect(0f, 0f, window.width, window.height).ContractedBy(10f);
				float width = (inner.width - 10f) / 2f;
				if (Widgets.ButtonText(new Rect(inner.x, inner.y, width, inner.height), "Confirm".Translate()))
				{
					Confirm();
				}
				if (Widgets.ButtonText(new Rect(inner.xMax - width, inner.y, width, inner.height), "CancelButton".Translate()))
				{
					SoundDefOf.CancelMode.PlayOneShotOnCamera();
					Find.DesignatorManager.Deselect();
				}
			});
			BesideRotation(73195731);
		}

		private static bool snapSaid;

		/// <summary>
		/// The buttons' window (immediate window id) right of the rotate buttons' window as the window stack holds both
		/// now, when the rotate buttons were drawn in this frame: whatever moved either (the camera hole's shift of the
		/// architect, a mod placing its controls itself), they stay side by side. With the hole avoided, the buttons were
		/// the architect's shift further right than the rotate buttons (2026-10-27, on this phone and a tester's).
		/// </summary>
		internal static void BesideRotation(int id)
		{
			if (rotationFrame != Time.frameCount || Event.current.type != EventType.Repaint)
			{
				return;
			}
			Window rotation = null, own = null;
			IList<Window> windows = Find.WindowStack.Windows;
			for (int i = 0; i < windows.Count; i++)
			{
				if (windows[i].ID == -73095)
				{
					rotation = windows[i];
				}
				else if (windows[i].ID == -id)
				{
					own = windows[i];
				}
			}
			if (rotation == null || own == null)
			{
				return;
			}
			var beside = new Rect(rotation.windowRect.xMax + Gap, rotation.windowRect.y, own.windowRect.width, own.windowRect.height);
			if (!snapSaid && (Mathf.Abs(beside.x - own.windowRect.x) > 0.5f || Mathf.Abs(beside.y - own.windowRect.y) > 0.5f))
			{
				snapSaid = true;
				Console.WriteLine("[mobile] confirm buttons moved beside the rotate buttons: rotate " + rotation.windowRect + ", buttons " + own.windowRect + " -> " + beside);
			}
			own.windowRect = beside;
		}

		private static void Confirm()
		{
			Designator selected = Tool();
			if (selected == null)
			{
				return;
			}
			if (Touch.Verbose) Console.WriteLine("[mobile] confirm placement: chosen " + chosen + ", same tool " + (selected == tool) + ", cell " + cell + ", " + selected.GetType().Name);
			if (!chosen || selected != tool)
			{
				Messages.Message(selected is Designator_Place ? (Texts.Chinese ? "先在地图上点一下要放的位置。" : "Tap the map where it should go first.")
					: (Texts.Chinese ? "先在地图上点一下位置。" : "Tap the place on the map first."), MessageTypeDefOf.RejectInput, false);
				return;
			}
			// What the game does for a click at that place.
			AcceptanceReport report = selected.CanDesignateCell(cell);
			if (report.Accepted)
			{
				selected.DesignateSingleCell(cell);
				selected.Finalize(true);
			}
			else
			{
				Messages.Message(report.Reason, MessageTypeDefOf.SilentInput, false);
				selected.Finalize(false);
			}
		}
	}

	/// <summary>
	/// A mod's tool that reads the mouse button itself every frame, in its SelectedUpdate or in what that calls in the mod
	/// (Input.GetMouseButton, GetMouseButtonDown, GetMouseButtonUp): God Hands' grab, wrench, poke, cleaning... pick up a
	/// pawn under the pressed button, carry it while the button is held and throw it when it is let go. Such a tool gets
	/// the mouse as on the PC: the press at once, the mouse under the finger while it drags (not the map moving), the
	/// release when it lifts, and no confirm step (MapTools, DragConfirm.KeepsOwnDrag). A tester, 2026-10-24: "神之手mod不
	/// 适配手机操作" — a tool without a drawing style was held back like placing a building, and a moving finger panned.
	/// Two fingers still move the map. The same for a world targeting whose update reads the button (Reads; God Hands'
	/// axis fall: the button held on a tile for 1.8 s, which the two taps on the world never gave it).
	/// </summary>
	internal static class RawMouseTools
	{
		private const int Depth = 6;
		private static readonly Dictionary<Type, bool> known = new Dictionary<Type, bool>();

		public static bool Is(Designator designator)
		{
			if (designator == null)
			{
				return false;
			}
			Type type = designator.GetType();
			if (!known.TryGetValue(type, out bool raw))
			{
				try
				{
					var seen = new HashSet<System.Reflection.MethodBase>();
					for (Type t = type; t != null && t != typeof(Designator) && !raw; t = t.BaseType)
					{
						// The game's own tools never poll the mouse: only a mod's types are looked into.
						if (t.Assembly == typeof(Designator).Assembly)
						{
							break;
						}
						System.Reflection.MethodInfo update = t.GetMethod(nameof(Designator.SelectedUpdate), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
						raw = update != null && ReadsMouse(update, type.Assembly, 0, seen);
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("[mobile] tools: could not look into " + type.FullName + ": " + e.Message);
				}
				known[type] = raw;
				if (raw)
				{
					Console.WriteLine("[mobile] tools: " + type.FullName + " reads the mouse button itself: a mouse's press, drag and release");
				}
			}
			return raw;
		}

		private static readonly Dictionary<System.Reflection.MethodInfo, bool> knownMethods = new Dictionary<System.Reflection.MethodInfo, bool>();

		/// <summary>A mod's delegate (a targeting's update) that reads the mouse button, as Is does for a tool.</summary>
		public static bool Reads(Delegate callback)
		{
			System.Reflection.MethodInfo method = callback?.Method;
			if (method == null || method.DeclaringType == null || method.DeclaringType.Assembly == typeof(Designator).Assembly)
			{
				return false;
			}
			if (!knownMethods.TryGetValue(method, out bool raw))
			{
				try
				{
					raw = ReadsMouse(method, method.DeclaringType.Assembly, 0, new HashSet<System.Reflection.MethodBase>());
				}
				catch (Exception e)
				{
					Console.WriteLine("[mobile] tools: could not look into " + method.DeclaringType.FullName + "." + method.Name + ": " + e.Message);
				}
				knownMethods[method] = raw;
				if (raw)
				{
					Console.WriteLine("[mobile] tools: " + method.DeclaringType.FullName + "." + method.Name + " reads the mouse button itself: a mouse's press and hold");
				}
			}
			return raw;
		}

		private static bool ReadsMouse(System.Reflection.MethodBase method, System.Reflection.Assembly mod, int depth, HashSet<System.Reflection.MethodBase> seen)
		{
			if (!seen.Add(method) || method.GetMethodBody() == null)
			{
				return false;
			}
			foreach (KeyValuePair<System.Reflection.Emit.OpCode, object> instruction in PatchProcessor.ReadMethodBody(method))
			{
				if (!(instruction.Value is System.Reflection.MethodBase called))
				{
					continue;
				}
				if (called.DeclaringType?.FullName == "UnityEngine.Input" && called.Name.StartsWith("GetMouseButton", StringComparison.Ordinal))
				{
					return true;
				}
				if (depth < Depth && called.DeclaringType?.Assembly == mod && ReadsMouse(called, mod, depth + 1, seen))
				{
					return true;
				}
			}
			return false;
		}
	}

	/// <summary>
	/// Designating by dragging (mining, cancelling, cutting plants, zones, walls, floors and every other tool with a
	/// drawing style) in two steps, like placing a building. A drag only marks its cells: they stay highlighted,
	/// further drags add to them, and a "confirm" button next to the tool's own controls designates them, each drag
	/// (or tap) on its own, one after the other, as the releases would have with a mouse. "Cancel" drops the marked
	/// cells, and with none marked it puts the tool away. With a single-cell drawing style a tap on a marked cell
	/// unmarks it.
	/// </summary>
	internal static class DragConfirm
	{
		private static readonly AccessTools.FieldRef<DesignationDragger, IntVec3> StartCell = AccessTools.FieldRefAccess<DesignationDragger, IntVec3>("startDragCell");
		private static Texture2D outlineTex;
		private static Designator tool;
		private static Map map;
		private static readonly List<IntVec3> cells = new List<IntVec3>();
		private static readonly HashSet<IntVec3> cellSet = new HashSet<IntVec3>();
		/// <summary>The marked cells by the release that marked them, in order.</summary>
		private static readonly List<List<IntVec3>> releases = new List<List<IntVec3>>();
		private static readonly List<CellRect> outlines = new List<CellRect>();
		private static readonly List<IntVec3> visible = new List<IntVec3>();
		private static bool cancelled;

		public static void Start(Harmony harmony)
		{
			// The controls of every tool are kept on the screen, not only a building's: a tall Architect menu (many mods' categories) put them above its top.
			harmony.Patch(AccessTools.Method(typeof(Designator), nameof(Designator.DoExtraGuiControls)), prefix: new HarmonyMethod(typeof(Placement), nameof(Placement.KeepControlsOnScreen)), postfix: new HarmonyMethod(typeof(DragConfirm), nameof(Controls)));
			harmony.Patch(AccessTools.Method(typeof(DesignatorManager), nameof(DesignatorManager.DesignatorManagerUpdate)), postfix: new HarmonyMethod(typeof(DragConfirm), nameof(AfterUpdate)));
			harmony.Patch(AccessTools.Method(typeof(DesignatorManager), nameof(DesignatorManager.DesignationManagerOnGUI)), postfix: new HarmonyMethod(typeof(DragConfirm), nameof(OnGUI)));
		}

		/// <summary>The selected tool, if it designates the cells of a drag (or of single taps with a drawing style).</summary>
		public static Designator Tool()
		{
			if (!Settings.PlaceConfirm || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
			{
				return null;
			}
			DesignatorManager manager = Find.DesignatorManager;
			return manager.SelectedStyle != null && !KeepsOwnDrag(manager.SelectedDesignator) ? manager.SelectedDesignator : null;
		}

		/// <summary>
		/// The selected tool, if it acts by itself when the finger lifts (KeepsOwnDrag): it gets only a cancel button, the
		/// mouse's right click (the user, 2026-10-22: after "选择相似" there was no quick way to put the tool away).
		/// </summary>
		public static Designator OwnDragTool()
		{
			if (!Settings.PlaceConfirm || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
			{
				return null;
			}
			Designator selected = Find.DesignatorManager.SelectedDesignator;
			return KeepsOwnDrag(selected) ? selected : null;
		}

		private static readonly Dictionary<Type, bool> ownDrag = new Dictionary<Type, bool>();

		/// <summary>
		/// A tool that follows the drag itself and designates from that, not from the cells it is handed: Allow Tool's
		/// (on its Designator_UnlimitedDragger: Select similar, Allow / Forbid all, Haul urgently, ...). By the time a
		/// confirmation hands it the marked cells its own area is gone, and nothing was selected (the user, 2026-10-22: a
		/// tree, "选择相似", a box around other trees). These act when the finger lifts, as with a mouse.
		/// </summary>
		internal static bool KeepsOwnDrag(Designator designator)
		{
			if (designator == null)
			{
				return false;
			}
			if (RawMouseTools.Is(designator))
			{
				return true;
			}
			Type type = designator.GetType();
			if (!ownDrag.TryGetValue(type, out bool own))
			{
				for (Type t = type; t != null && t != typeof(Designator) && !own; t = t.BaseType)
				{
					own = t.Name == "Designator_UnlimitedDragger";
				}
				ownDrag[type] = own;
			}
			return own;
		}

		/// <summary>Every frame: marked cells belong to one tool on one map; a drag ends with its finger.</summary>
		public static void Update()
		{
			if (tool != null && (Tool() != tool || Find.CurrentMap != map))
			{
				Clear();
			}
			EndStrandedDrag();
		}

		private static int strandedFrames;

		/// <summary>
		/// A drag whose finger lifted over a window (the confirm buttons, the menu): the window took the release, the map
		/// never saw it, and the drag went on with the next touch. The user, 2026-10-26: a plan dragged up to the
		/// confirm button went on following the finger, and every "confirm" said to drag first. Two frames after the
		/// release went out the drag ends here, where the finger lifted, as a release on the map would have ended it.
		/// </summary>
		private static void EndStrandedDrag()
		{
			// A mouse's button held down (touch chosen in the options with a mouse connected) is a drag going on.
			if (Touch.TouchInProgress || Input.GetMouseButton(0) || Tool() == null || !Find.DesignatorManager.Dragger.Dragging)
			{
				strandedFrames = 0;
				return;
			}
			if (++strandedFrames < 2)
			{
				return;
			}
			strandedFrames = 0;
			Console.WriteLine("[mobile] drag: the release went to a window; the drag ends at " + UI.MouseCell());
			DragEnded();
		}

		private static void Clear()
		{
			tool = null;
			map = null;
			cells.Clear();
			cellSet.Clear();
			releases.Clear();
			outlines.Clear();
		}

		internal static void DragStarted()
		{
			cancelled = false;
		}

		internal static void DragCancelled()
		{
			cancelled = true;
		}

		/// <summary>The finger lifted. True when the drag's cells were taken here, so that the game must not designate them itself.</summary>
		internal static bool DragEnded()
		{
			Designator selected = Tool();
			if (selected == null)
			{
				return false;
			}
			DesignatorManager manager = Find.DesignatorManager;
			DesignationDragger dragger = manager.Dragger;
			bool single = manager.SelectedStyle.DrawStyleWorker.SingleCell;
			if (!dragger.Dragging && !single)
			{
				return false;
			}
			if (cancelled)
			{
				dragger.EndDrag();
				return true;
			}
			if (selected != tool || Find.CurrentMap != map)
			{
				Clear();
				tool = selected;
				map = Find.CurrentMap;
			}
			List<IntVec3> dragged = dragger.DragCells;
			if (dragged.Count > 0)
			{
				if (single && dragged.TrueForAll(cellSet.Contains))
				{
					foreach (IntVec3 c in dragged)
					{
						cellSet.Remove(c);
						cells.Remove(c);
						foreach (List<IntVec3> release in releases)
						{
							release.Remove(c);
						}
					}
					releases.RemoveAll(release => release.Count == 0);
				}
				else
				{
					var release = new List<IntVec3>();
					foreach (IntVec3 c in dragged)
					{
						if (cellSet.Add(c))
						{
							cells.Add(c);
							release.Add(c);
						}
					}
					if (release.Count > 0)
					{
						releases.Add(release);
					}
					if (!single && manager.SelectedStyle.drawOutline)
					{
						IntVec3 start = StartCell(dragger);
						IntVec3 end = UI.MouseCell();
						outlines.Add(CellRect.FromLimits(Math.Min(start.x, end.x), Math.Min(start.z, end.z), Math.Max(start.x, end.x), Math.Max(start.z, end.z)));
					}
				}
			}
			else
			{
				// Nothing there the tool takes: what the game does on a release (Designator.Finalize), the failure sound and
				// the reason ("already in storage", "must designate haulable things"). Without it a drag over things already
				// stored or already marked left nothing and said nothing, and "confirm" asked for a drag (a tester,
				// 2026-10-24: "批量搬运不能用").
				selected.Finalize(false);
			}
			dragger.EndDrag();
			return true;
		}

		/// <summary>Postfix of DesignatorManager.DesignatorManagerUpdate: the marked cells, drawn as the tool draws a drag.</summary>
		public static void AfterUpdate()
		{
			if (cells.Count == 0 || Tool() != tool || !tool.DrawHighlight)
			{
				return;
			}
			CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(3).ClipInsideMap(map);
			visible.Clear();
			foreach (IntVec3 c in cells)
			{
				if (view.Contains(c))
				{
					visible.Add(c);
				}
			}
			if (visible.Count > 0)
			{
				tool.RenderHighlight(visible);
			}
		}

		/// <summary>Postfix of DesignatorManager.DesignationManagerOnGUI: the outline of each drag, as during the drag.</summary>
		public static void OnGUI()
		{
			if (outlines.Count == 0 || Event.current.type != EventType.Repaint || Tool() != tool)
			{
				return;
			}
			if (outlineTex == null)
			{
				outlineTex = SolidColorMaterials.NewSolidColorTexture(new Color32(109, 139, 79, 100));
			}
			foreach (CellRect box in outlines)
			{
				if (box.Width <= 1 && box.Height <= 1)
				{
					continue;
				}
				Vector2 min = new Vector3(box.minX, 0f, box.minZ).MapToUIPosition();
				Vector2 max = new Vector3(box.maxX + 1, 0f, box.maxZ + 1).MapToUIPosition();
				Widgets.DrawBox(Rect.MinMaxRect(min.x, min.y, max.x, max.y), 1, outlineTex);
			}
		}

		/// <summary>Postfix of Designator.DoExtraGuiControls: the buttons, right of the drawing style picker when the game shows one.</summary>
		public static void Controls(Designator __instance, float leftX, float bottomY)
		{
			Placement.NoteControls();
			if (OwnDragTool() == __instance)
			{
				DrawPutAway(ButtonsRect(__instance, leftX, bottomY, false));
				return;
			}
			if (Tool() != __instance)
			{
				// A tool that acts on one cell (not a building: Designator_Place has a postfix of its own).
				Placement.OwnControls(__instance, leftX, bottomY);
				return;
			}
			Draw(ButtonsRect(__instance, leftX, bottomY, false));
		}

		internal static Rect ButtonsRect(Designator selected, float leftX, float bottomY, bool rotates)
		{
			List<DrawStyleDef> styles = selected.DrawStyleCategory?.styles;
			bool picker = !rotates && Find.DesignatorManager.SelectedStyle != null && styles != null && styles.Count > 1;
			// Right of the rotate buttons where they were drawn in this frame (Placement.ButtonsRect).
			if (rotates && Placement.RotationDrawn(out Vector2 at))
			{
				return new Rect(at.x + 200f + Placement.Gap, at.y - 90f, 200f, 90f);
			}
			return new Rect(leftX + (rotates || picker ? 200f + Placement.Gap : 0f), bottomY - 90f, 200f, 90f);
		}

		internal static void Draw(Rect window)
		{
			Placement.NoteDrew();
			Find.WindowStack.ImmediateWindow(73195732, window, WindowLayer.GameUI, delegate
			{
				Text.Font = GameFont.Small;
				var inner = new Rect(0f, 0f, window.width, window.height).ContractedBy(10f);
				float width = (inner.width - 10f) / 2f;
				if (Widgets.ButtonText(new Rect(inner.x, inner.y, width, inner.height), "Confirm".Translate()))
				{
					Confirm();
				}
				if (Widgets.ButtonText(new Rect(inner.xMax - width, inner.y, width, inner.height), "CancelButton".Translate()))
				{
					Cancel();
				}
			});
			Placement.BesideRotation(73195732);
		}

		/// <summary>The cancel button alone, in the left half of the buttons' place.</summary>
		internal static void DrawPutAway(Rect window)
		{
			Placement.NoteDrew();
			window.width = window.width / 2f + 5f;
			Find.WindowStack.ImmediateWindow(73195733, window, WindowLayer.GameUI, delegate
			{
				Text.Font = GameFont.Small;
				if (Widgets.ButtonText(new Rect(0f, 0f, window.width, window.height).ContractedBy(10f), "CancelButton".Translate()))
				{
					SoundDefOf.CancelMode.PlayOneShotOnCamera();
					Find.DesignatorManager.Deselect();
				}
			});
		}

		private static void Confirm()
		{
			Designator selected = Tool();
			if (selected == null)
			{
				return;
			}
			if (cells.Count == 0 || selected != tool || Find.CurrentMap != map)
			{
				Messages.Message(Texts.Chinese ? "先在地图上拖出要指定的范围。" : "Drag over the area on the map first.", MessageTypeDefOf.RejectInput, false);
				return;
			}
			var marked = new List<List<IntVec3>>(releases);
			Clear();
			// What the game does when the mouse button is released after a drag, once for each release. The PC never hands a
			// tool the cells of several releases in one call, and a mod may count on that: a confirm of two or more taps on
			// ED-Embrasures' buildings threw "An item with the same key has already been added" in DesignateMultiCell, which
			// Mine Sight patches (a tester, 2026-10-30: 6 of 6 such errors, none in 133 confirms of one tap). A tool that put
			// itself away stops there, as it would with a mouse. The tool's success sound plays once, as before: the
			// releases' sounds in one frame would make one loud click.
			SoundDef sound = selected.soundSucceeded;
			try
			{
				for (int i = 0; i < marked.Count; i++)
				{
					if (Find.DesignatorManager.SelectedDesignator != selected)
					{
						break;
					}
					selected.soundSucceeded = i == 0 ? sound : null;
					selected.DesignateMultiCell(marked[i]);
				}
			}
			finally
			{
				selected.soundSucceeded = sound;
			}
		}

		private static void Cancel()
		{
			if (cells.Count > 0)
			{
				SoundDefOf.Tick_Low.PlayOneShotOnCamera();
				Clear();
				return;
			}
			SoundDefOf.CancelMode.PlayOneShotOnCamera();
			Find.DesignatorManager.Deselect();
		}
	}

	/// <summary>
	/// Dev mode map tools (a debug action that makes a tool: edit roof, spawn, kill, damage, explosion...) under a finger.
	/// The game runs such a tool on every press of the button and keeps it until a right click: every drag of the map
	/// began with a press, so looking around ran the tool where the finger landed, and with nobody selected nothing gave
	/// a right click to put it away (the user, 2026-10-26: "开发者模式中的选项添加确定取消按钮，比如移除房顶的命令，执行之后
	/// 取消比较麻烦"). As when placing a building: a tap marks the cell (MapTools holds the press back; a drag pans), the
	/// tool takes that cell for the mouse's (UI.MouseCell, UI.MouseMapPosition), "confirm" runs it there, as often as
	/// tapped, and "cancel" puts the tool away. The first corner of a rectangle (the game's GenericRectTool) is taken at
	/// once, as it only remembers the cell; the second is marked and confirmed. Not with a mouse (Settings.PlaceConfirm);
	/// mobile.txt debugConfirm=0 leaves it out.
	/// </summary>
	internal static class DebugConfirm
	{
		private const int WindowId = Placement.OwnWindows + 4;
		private static readonly bool On = Settings.Get("debugConfirm", true);
		private static AccessTools.FieldRef<LudeonTK.DebugTool, Action> ClickAction;
		private static AccessTools.FieldRef<LudeonTK.DebugTool, string> Label;
		private static LudeonTK.DebugTool markedFor;
		private static bool marked, told;
		private static IntVec3 cell;
		private static Texture2D markTex;

		public static void Start(Harmony harmony)
		{
			try
			{
				ClickAction = AccessTools.FieldRefAccess<LudeonTK.DebugTool, Action>("clickAction");
				Label = AccessTools.FieldRefAccess<LudeonTK.DebugTool, string>("label");
				harmony.Patch(AccessTools.Method(typeof(LudeonTK.DebugTools), nameof(LudeonTK.DebugTools.DebugToolsOnGUI)), postfix: new HarmonyMethod(typeof(DebugConfirm), nameof(OnGUI)));
			}
			catch (Exception e)
			{
				ClickAction = null;
				Console.WriteLine("[mobile] dev mode tools: no confirm step (" + e.Message + ")");
			}
		}

		/// <summary>The dev mode tool in use, if it is to be confirmed.</summary>
		public static LudeonTK.DebugTool Tool()
		{
			if (!On || ClickAction == null || !Settings.PlaceConfirm || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
			{
				return null;
			}
			return LudeonTK.DebugTools.curTool;
		}

		/// <summary>The marked cell, while the tool it was marked for is in use (UI.MouseCell gives it then).</summary>
		public static bool Marked(out IntVec3 at)
		{
			at = cell;
			return marked && LudeonTK.DebugTools.curTool == markedFor;
		}

		/// <summary>Every frame: a mark belongs to one tool.</summary>
		public static void Update()
		{
			if (marked && Tool() != markedFor)
			{
				marked = false;
				markedFor = null;
			}
		}

		/// <summary>From MapTools: the finger lifted on the map without dragging it; the press was held back.</summary>
		public static void Tapped()
		{
			LudeonTK.DebugTool tool = Tool();
			if (tool == null)
			{
				return;
			}
			cell = UI.UIToMapPosition(UI.MousePositionOnUI).ToIntVec3();
			marked = true;
			markedFor = tool;
			if (Touch.Verbose) Console.WriteLine("[mobile] dev mode tool \"" + Label(tool) + "\": marked " + cell);
			if ((Label(tool) ?? "").EndsWith("First corner...", StringComparison.Ordinal))
			{
				Run(tool);
			}
		}

		private static void Run(LudeonTK.DebugTool tool)
		{
			if (Touch.Verbose) Console.WriteLine("[mobile] dev mode tool \"" + Label(tool) + "\": runs at " + cell);
			try
			{
				ClickAction(tool)();
			}
			catch (Exception e)
			{
				Log.Error("Exception in dev mode tool " + Label(tool) + ": " + e);
			}
		}

		/// <summary>Postfix of DebugTools.DebugToolsOnGUI: the marked cell and the buttons.</summary>
		public static void OnGUI()
		{
			LudeonTK.DebugTool tool = Tool();
			if (tool == null)
			{
				return;
			}
			if (!told)
			{
				told = true;
				Messages.Message(Texts.Chinese ? "开发者工具：点地图标记位置，点左下角“" + "Confirm".Translate() + "”在那里执行，“" + "CancelButton".Translate() + "”退出工具。"
					: "Dev mode tool: tap the map to mark the place, " + "Confirm".Translate() + " (bottom left) runs the tool there, " + "CancelButton".Translate() + " puts it away.", MessageTypeDefOf.SilentInput, false);
			}
			if (Event.current.type == EventType.Repaint && Marked(out IntVec3 at))
			{
				if (markTex == null)
				{
					markTex = SolidColorMaterials.NewSolidColorTexture(new Color(1f, 0.82f, 0.2f, 0.9f));
				}
				Vector2 min = new Vector3(at.x, 0f, at.z).MapToUIPosition();
				Vector2 max = new Vector3(at.x + 1, 0f, at.z + 1).MapToUIPosition();
				Widgets.DrawBox(Rect.MinMaxRect(min.x, max.y, max.x, min.y), 2, markTex);
			}
			var window = new Rect(0f, Placement.ControlsBottom() - 90f, 200f, 90f);
			Find.WindowStack.ImmediateWindow(WindowId, window, WindowLayer.GameUI, delegate
			{
				Text.Font = GameFont.Small;
				var inner = new Rect(0f, 0f, window.width, window.height).ContractedBy(10f);
				float width = (inner.width - 10f) / 2f;
				if (Widgets.ButtonText(new Rect(inner.x, inner.y, width, inner.height), "Confirm".Translate()))
				{
					Confirm();
				}
				if (Widgets.ButtonText(new Rect(inner.xMax - width, inner.y, width, inner.height), "CancelButton".Translate()))
				{
					SoundDefOf.CancelMode.PlayOneShotOnCamera();
					LudeonTK.DebugTools.curTool = null;
					marked = false;
					markedFor = null;
				}
			});
		}

		private static void Confirm()
		{
			LudeonTK.DebugTool tool = Tool();
			if (tool == null)
			{
				return;
			}
			if (!Marked(out _))
			{
				Messages.Message(Texts.Chinese ? "先在地图上点一下位置。" : "Tap the place on the map first.", MessageTypeDefOf.RejectInput, false);
				return;
			}
			Run(tool);
		}
	}

	internal static class Texts
	{
		public static bool Chinese => LanguageDatabase.activeLanguage != null && LanguageDatabase.activeLanguage.folderName.StartsWith("Chinese");
	}
}
