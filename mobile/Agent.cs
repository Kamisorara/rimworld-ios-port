using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace PortMobile
{
	/// <summary>
	/// A way for a program on the PC (host/mcp/rimworld-mcp.js, through adb's port forwarding) to ask the game what it
	/// shows and to move it along: the texts, controls and windows on the screen and where they are, the state of the
	/// game, things on the map; jumping the camera, selecting, the time speed, closing a window. Touches are not made
	/// here: the PC sends real ones (adb), so that the touch layer is what gets tested.
	///
	/// Off unless game/agent.txt exists ("port token"); it is looked for about once a second, and the agent stops when
	/// the file goes. It listens on the phone's loopback address only, and every request must carry the token (other
	/// apps on the phone cannot read the game's folder). One request per line, JSON: {"token":..,"cmd":..,"args":{..}};
	/// one answer per line: {"ok":true,...} or {"ok":false,"error":..}. The commands run on the game's main thread,
	/// between frames.
	/// </summary>
	internal static class Agent
	{
		private sealed class Request
		{
			public string Cmd;
			public JsonElement Args;
			public string Answer;
			public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
		}

		private static readonly ConcurrentQueue<Request> queue = new ConcurrentQueue<Request>();
		private static TcpListener listener;
		private static Thread thread;
		private static volatile string token;
		private static int port;
		private static int checkCountdown;
		private static volatile bool stopping;

		public static bool Active => listener != null;

		private static string SwitchFile => Path.Combine(AppContext.BaseDirectory, "agent.txt");

		/// <summary>Every frame, on the main thread (from Touch.Update): the switch file now and then, and the waiting requests.</summary>
		public static void Update()
		{
			if (--checkCountdown <= 0)
			{
				checkCountdown = 60;
				CheckSwitch();
				RunCommandFile();
			}
			while (queue.TryDequeue(out Request request))
			{
				try
				{
					request.Answer = Run(request.Cmd, request.Args);
				}
				catch (Exception e)
				{
					request.Answer = Failure(e.GetType().Name + ": " + e.Message);
				}
				request.Done.Set();
			}
		}

		/// <summary>
		/// While the agent is on: a file agentcmd.txt next to the game, "command {json args}", is run once and deleted, the
		/// answer logged. For a command no MCP tool sends (the connection takes one client, the MCP server's).
		/// </summary>
		private static void RunCommandFile()
		{
			if (listener == null)
			{
				return;
			}
			try
			{
				string file = Path.Combine(AppContext.BaseDirectory, "agentcmd.txt");
				if (!File.Exists(file))
				{
					return;
				}
				string line = File.ReadAllText(file).Trim();
				File.Delete(file);
				int space = line.IndexOf(' ');
				string cmd = space > 0 ? line.Substring(0, space) : line;
				using (JsonDocument args = JsonDocument.Parse(space > 0 ? line.Substring(space + 1) : "{}"))
				{
					Console.WriteLine("[mobile] agent: command file " + cmd + ": " + Run(cmd, args.RootElement.Clone()));
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] agent: command file failed: " + e.Message);
			}
		}

		private static void CheckSwitch()
		{
			try
			{
				string file = SwitchFile;
				if (!File.Exists(file))
				{
					if (listener != null)
					{
						Stop("the switch file is gone");
					}
					return;
				}
				string[] parts = File.ReadAllText(file).Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length < 2 || !int.TryParse(parts[0], out int wantedPort) || parts[1].Length < 16)
				{
					return;
				}
				if (listener != null && wantedPort == port && parts[1] == token)
				{
					return;
				}
				if (listener != null)
				{
					Stop("new settings");
				}
				token = parts[1];
				port = wantedPort;
				stopping = false;
				listener = new TcpListener(IPAddress.Loopback, port);
				// The port of the listener just stopped (a new token) is still held by its last connection (TIME_WAIT):
				// without this every restart failed with "Address already in use" until the game was restarted.
				listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
				listener.Start();
				thread = new Thread(Serve) { IsBackground = true, Name = "port17 agent" };
				thread.Start();
				TapAssist.RecordForAgent = true;
				Console.WriteLine("[mobile] agent listening on 127.0.0.1:" + port);
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] agent not started: " + e.Message);
				listener = null;
			}
		}

		private static void Stop(string why)
		{
			stopping = true;
			TapAssist.RecordForAgent = false;
			try
			{
				listener?.Stop();
			}
			catch (Exception)
			{
			}
			listener = null;
			Console.WriteLine("[mobile] agent stopped (" + why + ")");
		}

		// ---- the connection (its own thread) ----

		private static void Serve()
		{
			TcpListener own = listener;
			while (!stopping && own != null)
			{
				TcpClient client;
				try
				{
					client = own.AcceptTcpClient();
				}
				catch (Exception)
				{
					return;
				}
				try
				{
					using (client)
					using (NetworkStream stream = client.GetStream())
					using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
					using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
					{
						string line;
						while (!stopping && (line = reader.ReadLine()) != null)
						{
							writer.WriteLine(Handle(line));
						}
					}
				}
				catch (Exception e)
				{
					if (!stopping)
					{
						Console.WriteLine("[mobile] agent connection ended: " + e.Message);
					}
				}
			}
		}

		private static string Handle(string line)
		{
			JsonDocument document;
			try
			{
				document = JsonDocument.Parse(line);
			}
			catch (Exception)
			{
				return Failure("not JSON");
			}
			using (document)
			{
				JsonElement root = document.RootElement;
				if (!root.TryGetProperty("token", out JsonElement given) || given.GetString() != token)
				{
					return Failure("wrong token");
				}
				string cmd = root.TryGetProperty("cmd", out JsonElement c) ? c.GetString() : "";
				if (cmd == "ping")
				{
					// Answered here: the main thread may be busy (loading).
					return Answer(w =>
					{
						w.WriteString("program", Current.ProgramState.ToString());
						w.WriteBoolean("loading", LongEventHandler.AnyEventNowOrWaiting);
						w.WriteNumber("frame", Time.frameCount);
					});
				}
				var request = new Request { Cmd = cmd, Args = root.TryGetProperty("args", out JsonElement a) ? a.Clone() : default };
				queue.Enqueue(request);
				int wait = cmd == "screen" ? 3000 : 8000;
				if (!request.Done.Wait(wait))
				{
					return Failure("the game did not get to it within " + wait / 1000 + " s (loading, or a long frame)");
				}
				return request.Answer;
			}
		}

		// ---- answers ----

		private static string Answer(Action<Utf8JsonWriter> body)
		{
			using (var buffer = new MemoryStream())
			{
				using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
				{
					w.WriteStartObject();
					w.WriteBoolean("ok", true);
					body(w);
					w.WriteEndObject();
				}
				return Encoding.UTF8.GetString(buffer.ToArray());
			}
		}

		private static string Failure(string error)
		{
			return Answer(w =>
			{
				w.WriteBoolean("ok", false);
				w.WriteString("error", error);
			}).Replace("\"ok\":true,", "");
		}

		private static string Str(JsonElement args, string name, string fallback = null)
		{
			return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;
		}

		private static int Int(JsonElement args, string name, int fallback)
		{
			return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : fallback;
		}

		private static float Float(JsonElement args, string name, float fallback)
		{
			return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : fallback;
		}

		private static bool Has(JsonElement args, string name)
		{
			return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null;
		}

		private static bool Playing => Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null;

		private static string Run(string cmd, JsonElement args)
		{
			switch (cmd)
			{
				case "state": return State();
				case "screen": return ScreenContents(args);
				case "find": return FindThings(args);
				case "camera": return Camera(args);
				case "select": return SelectThing(args);
				case "speed": return Speed(args);
				case "close": return CloseWindow(args);
				case "messages": return RecentMessages(args);
				case "cell": return CellOnScreen(args);
				case "zones": return Zones();
				case "incident": return FireIncident(args);
				case "world": return WorldObjects(args);
				case "pawn": return PawnInfo(args);
				case "menu": return MenuAt(args);
				case "research": return FinishResearch(args);
				case "spawn": return SpawnThing(args);
				case "monolith": return MonolithLevel(args);
				case "makepawn": return MakePawn(args);
				case "heal": return HealPawn(args);
				case "kill": return KillThings(args);
				case "save": return SaveGame(args);
				case "hash": return StringHashes(args);
				case "relation": return AddRelation(args);
				case "rituals": return Rituals();
				case "marry": return StartMarriage(args);
				case "tune": return Tune(args);
				case "worldtarget": return TestWorldTarget();
				case "compat": return TestCompat(args);
				case "fallbacktest": return TestFallbacks();
				case "xmltest": return XmlSerializerTest.Run();
				case "mpctl": return MultiplayerPawnControl(args);
				case "onscreen": return ThingsOnScreen();
				case "mark":
					// As the key wheel's "记录问题" does; the box for the player's words only with {"ask":true}.
					Recorder.Mark(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ask", out JsonElement ask) && ask.ValueKind == JsonValueKind.True);
					return Answer(w => w.WriteString("mark", "in three frames"));
				default: return Failure("unknown command '" + cmd + "' (state, screen, find, camera, select, speed, close, messages, cell, zones, incident, world, pawn, menu, research, spawn, makepawn, heal, kill, save, hash, relation, rituals, marry, tune, worldtarget, compat, mpctl, mark, ping)");
			}
		}

		// Multiplayer's pawn control (the 1.7 test build): its test entry, which answers JSON
		private static string MultiplayerPawnControl(JsonElement args)
		{
			var run = HarmonyLib.AccessTools.Method("Multiplayer.Client.PawnControlTest:Run");
			if (run == null)
			{
				return Failure("Multiplayer with pawn control is not loaded");
			}
			string json = (string)run.Invoke(null, new object[] { Str(args, "cmd", "state"), Str(args, "a"), Str(args, "b") });
			return Answer(w =>
			{
				w.WritePropertyName("result");
				w.WriteRawValue(json);
			});
		}

		/// <summary>
		/// What a double click's "everything of the kind on the screen" works with: the screen in map cells as
		/// ThingSelectionUtility computes it, the camera's own view, and the colonists the screen rectangle yields.
		/// </summary>
		private static string ThingsOnScreen()
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			var rect = new UnityEngine.Rect(0f, 0f, UI.screenWidth, UI.screenHeight);
			var mapRect = (CellRect)AccessTools.Method(typeof(ThingSelectionUtility), "GetMapRect").Invoke(null, new object[] { rect });
			CellRect view = Find.CameraDriver.CurrentViewRect;
			var things = ThingSelectionUtility.MultiSelectableThingsInScreenRectDistinct(rect).ToList();
			var colonists = things.OfType<Pawn>().Where(p => p.Faction == Faction.OfPlayer).ToList();
			return Answer(w =>
			{
				w.WriteNumber("uiScale", Prefs.UIScale);
				w.WriteNumber("uiWidth", UI.screenWidth);
				w.WriteNumber("uiHeight", UI.screenHeight);
				w.WriteNumber("screenWidth", UnityEngine.Screen.width);
				w.WriteNumber("screenHeight", UnityEngine.Screen.height);
				w.WriteString("screenRectCells", mapRect.minX + "," + mapRect.minZ + " - " + mapRect.maxX + "," + mapRect.maxZ);
				w.WriteString("cameraViewCells", view.minX + "," + view.minZ + " - " + view.maxX + "," + view.maxZ);
				w.WriteNumber("selectable", things.Count);
				w.WriteStartArray("colonists");
				foreach (Pawn p in colonists)
				{
					w.WriteStringValue(p.LabelShort + " " + p.Position.x + "," + p.Position.z);
				}
				w.WriteEndArray();
			});
		}

		private static void Rect(Utf8JsonWriter w, string name, float x0, float y0, float x1, float y1)
		{
			// In the screen's pixels (adb taps them) when the game renders at a lower resolution (ResolutionChoice).
			float k = ResolutionChoice.ToScreen;
			x0 *= k;
			y0 *= k;
			x1 *= k;
			y1 *= k;
			w.WriteStartArray(name);
			w.WriteNumberValue(Mathf.Round(x0));
			w.WriteNumberValue(Mathf.Round(y0));
			w.WriteNumberValue(Mathf.Round(x1 - x0));
			w.WriteNumberValue(Mathf.Round(y1 - y0));
			w.WriteEndArray();
		}

		private static string State()
		{
			return Answer(w =>
			{
				w.WriteString("program", Current.ProgramState.ToString());
				w.WriteBoolean("loading", LongEventHandler.AnyEventNowOrWaiting);
				w.WriteNumber("screenWidth", Screen.width);
				w.WriteNumber("screenHeight", Screen.height);
				w.WriteNumber("uiScale", Prefs.UIScale);
				w.WriteNumber("fps", Mathf.Round(1f / Mathf.Max(0.001f, Time.smoothDeltaTime)));
				w.WriteStartArray("windows");
				foreach (Window window in Find.WindowStack.Windows)
				{
					w.WriteStartObject();
					w.WriteString("type", window.GetType().FullName);
					if (!window.optionalTitle.NullOrEmpty())
					{
						w.WriteString("title", window.optionalTitle);
					}
					w.WriteNumber("id", window.ID);
					float s = Prefs.UIScale;
					Rect(w, "approxRect", window.windowRect.x * s, window.windowRect.y * s, window.windowRect.xMax * s, window.windowRect.yMax * s);
					w.WriteEndObject();
				}
				w.WriteEndArray();
				if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
				{
					return;
				}
				TickManager ticks = Find.TickManager;
				w.WriteBoolean("paused", ticks.Paused);
				w.WriteString("speed", ticks.CurTimeSpeed.ToString());
				w.WriteNumber("ticks", ticks.TicksGame);
				w.WriteBoolean("worldView", !WorldRendererUtility.DrawingMap);
				Map map = Find.CurrentMap;
				if (map != null)
				{
					w.WriteStartObject("map");
					w.WriteNumber("index", map.Index);
					w.WriteString("label", map.Parent?.LabelCap ?? "");
					w.WriteNumber("sizeX", map.Size.x);
					w.WriteNumber("sizeZ", map.Size.z);
					Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
					w.WriteString("date", GenDate.DateFullStringWithHourAt(Find.TickManager.TicksAbs, longLat));
					IntVec3 center = Find.CameraDriver.MapPosition;
					w.WriteStartArray("cameraCell");
					w.WriteNumberValue(center.x);
					w.WriteNumberValue(center.z);
					w.WriteEndArray();
					w.WriteNumber("cameraZoom", Find.CameraDriver.RootSize);
					w.WriteEndObject();
				}
				w.WriteString("openTab", Find.MainTabsRoot.OpenTab?.defName);
				w.WriteString("designator", Find.DesignatorManager.SelectedDesignator?.LabelCap.ToString());
				w.WriteStartArray("selected");
				foreach (object o in Find.Selector.SelectedObjectsListForReading)
				{
					w.WriteStartObject();
					if (o is Thing thing)
					{
						w.WriteString("id", thing.ThingID);
						w.WriteString("label", thing.LabelCap.ToString());
						w.WriteString("def", thing.def.defName);
						if (thing.Spawned)
						{
							w.WriteStartArray("cell");
							w.WriteNumberValue(thing.Position.x);
							w.WriteNumberValue(thing.Position.z);
							w.WriteEndArray();
						}
					}
					else
					{
						w.WriteString("label", (o as ISelectable)?.ToString() ?? o.ToString());
						w.WriteString("type", o.GetType().Name);
					}
					w.WriteEndObject();
				}
				w.WriteEndArray();
				if (Find.Selector.SingleSelectedThing is Thing single)
				{
					string inspect = "";
					try
					{
						inspect = single.GetInspectString();
					}
					catch (Exception e)
					{
						inspect = "(inspect string failed: " + e.GetType().Name + ")";
					}
					w.WriteString("inspect", inspect.Length > 800 ? inspect.Substring(0, 800) + "..." : inspect);
				}
				w.WriteStartArray("letters");
				List<Letter> letters = Find.LetterStack.LettersListForReading;
				for (int i = Math.Max(0, letters.Count - 6); i < letters.Count; i++)
				{
					w.WriteStringValue(letters[i].Label.ToString());
				}
				w.WriteEndArray();
			});
		}

		/// <summary>
		/// What the last drawn frame showed: the windows (drawing order, later ones on top), the controls with the text
		/// that lies on them, and the other texts. args: contains (only texts with this, any case), window (an index, or
		/// "top" for the topmost window only, "map" for what is drawn outside windows), max (texts, default 250).
		/// </summary>
		private static string ScreenContents(JsonElement args)
		{
			var controls = new List<(string kind, float x0, float y0, float x1, float y1, int window)>();
			var texts = new List<(string text, float x0, float y0, float x1, float y1, int window)>();
			var windows = new List<(float x0, float y0, float x1, float y1)>();
			TapAssist.Snapshot(controls, texts, windows);
			string contains = Str(args, "contains");
			string which = Str(args, "window");
			// "top": the topmost window that is more than a small overlay (the key wheel, a message, a tooltip).
			int topWindow = windows.Count - 1;
			while (topWindow > 0 && (windows[topWindow].x1 - windows[topWindow].x0) * (windows[topWindow].y1 - windows[topWindow].y0) < 300f * 300f)
			{
				topWindow--;
			}
			int onlyWindow = which == "top" ? topWindow : which == "map" ? -1 : int.TryParse(which, out int n) ? n : int.MinValue;
			int max = Int(args, "max", 250);
			bool Wanted(int window) => onlyWindow == int.MinValue || window == onlyWindow;
			return Answer(w =>
			{
				w.WriteStartArray("windows");
				for (int i = 0; i < windows.Count; i++)
				{
					w.WriteStartArray();
					w.WriteNumberValue(Mathf.Round(windows[i].x0));
					w.WriteNumberValue(Mathf.Round(windows[i].y0));
					w.WriteNumberValue(Mathf.Round(windows[i].x1 - windows[i].x0));
					w.WriteNumberValue(Mathf.Round(windows[i].y1 - windows[i].y0));
					w.WriteEndArray();
				}
				w.WriteEndArray();
				// A control's label: the texts whose middle lies on it.
				var usedTexts = new HashSet<int>();
				w.WriteStartArray("controls");
				int written = 0;
				foreach (var c in controls)
				{
					if (!Wanted(c.window))
					{
						continue;
					}
					var label = new StringBuilder();
					for (int i = 0; i < texts.Count; i++)
					{
						var t = texts[i];
						float mx = (t.x0 + t.x1) / 2f, my = (t.y0 + t.y1) / 2f;
						if (t.window == c.window && mx >= c.x0 && mx <= c.x1 && my >= c.y0 && my <= c.y1)
						{
							if (label.Length > 0) label.Append(" | ");
							label.Append(t.text.Length > 60 ? t.text.Substring(0, 60) : t.text);
							usedTexts.Add(i);
						}
					}
					string text = label.ToString();
					if (contains != null && text.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}
					if (written++ >= max)
					{
						break;
					}
					w.WriteStartObject();
					w.WriteString("k", c.kind);
					Rect(w, "r", c.x0, c.y0, c.x1, c.y1);
					w.WriteNumber("w", c.window);
					if (text.Length > 0)
					{
						w.WriteString("t", text);
					}
					w.WriteEndObject();
				}
				w.WriteEndArray();
				w.WriteStartArray("texts");
				written = 0;
				for (int i = 0; i < texts.Count; i++)
				{
					var t = texts[i];
					if (usedTexts.Contains(i) || !Wanted(t.window) || (contains != null && t.text.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0))
					{
						continue;
					}
					if (written++ >= max)
					{
						w.WriteStartObject();
						w.WriteString("t", "... more texts left out (ask with contains or window)");
						w.WriteEndObject();
						break;
					}
					w.WriteStartObject();
					w.WriteString("t", t.text.Length > 200 ? t.text.Substring(0, 200) + "..." : t.text);
					Rect(w, "r", t.x0, t.y0, t.x1, t.y1);
					w.WriteNumber("w", t.window);
					w.WriteEndObject();
				}
				w.WriteEndArray();
			});
		}

		/// <summary>Where a point of the map is on the screen, in pixels (null when off the screen).</summary>
		private static bool ScreenPoint(Vector3 world, out float x, out float y)
		{
			Vector2 ui = world.MapToUIPosition();
			x = ui.x * Prefs.UIScale;
			y = ui.y * Prefs.UIScale;
			bool on = x >= 0f && y >= 0f && x < Screen.width && y < Screen.height;
			float k = ResolutionChoice.ToScreen;
			x *= k;
			y *= k;
			return on;
		}

		/// <summary>Things on the current map whose id, def name or label holds the query. args: query, kind (pawn, building, item, plant, any), limit.</summary>
		private static string FindThings(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			string query = Str(args, "query", "");
			string kind = Str(args, "kind", "any");
			int limit = Int(args, "limit", 20);
			Map map = Find.CurrentMap;
			IntVec3 center = Find.CameraDriver.MapPosition;
			var found = new List<Thing>();
			foreach (Thing thing in map.listerThings.AllThings)
			{
				bool kindFits = kind == "hostile" ? thing is Pawn hp && hp.HostileTo(Faction.OfPlayer) && !hp.Dead : kind == "pawn" ? thing is Pawn : kind == "building" ? thing.def.category == ThingCategory.Building : kind == "item" ? thing.def.category == ThingCategory.Item : kind == "plant" ? thing is Plant : true;
				if (!kindFits)
				{
					continue;
				}
				if (query.Length == 0 || thing.ThingID.Equals(query, StringComparison.OrdinalIgnoreCase) || thing.def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || thing.LabelCap.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					found.Add(thing);
				}
			}
			int total = found.Count;
			found.Sort((a, b) => a.Position.DistanceToSquared(center).CompareTo(b.Position.DistanceToSquared(center)));
			return Answer(w =>
			{
				w.WriteNumber("total", total);
				w.WriteStartArray("things");
				foreach (Thing thing in found.Take(limit))
				{
					w.WriteStartObject();
					w.WriteString("id", thing.ThingID);
					w.WriteString("label", thing.LabelCap.ToString());
					w.WriteString("def", thing.def.defName);
					w.WriteStartArray("cell");
					w.WriteNumberValue(thing.Position.x);
					w.WriteNumberValue(thing.Position.z);
					w.WriteEndArray();
					if (thing.Faction != null)
					{
						w.WriteString("faction", thing.Faction == Faction.OfPlayer ? "player" : thing.Faction.Name);
					}
					if (thing is Pawn pawn)
					{
						if (pawn.HostileTo(Faction.OfPlayer))
						{
							w.WriteBoolean("hostile", true);
						}
						if (pawn.Downed)
						{
							w.WriteBoolean("downed", true);
						}
						if (pawn.Drafted)
						{
							w.WriteBoolean("drafted", true);
						}
					}
					if (ScreenPoint(thing.DrawPos, out float x, out float y))
					{
						w.WriteStartArray("screen");
						w.WriteNumberValue(Mathf.Round(x));
						w.WriteNumberValue(Mathf.Round(y));
						w.WriteEndArray();
					}
					w.WriteEndObject();
				}
				w.WriteEndArray();
			});
		}

		private static Thing ThingById(string id)
		{
			if (string.IsNullOrEmpty(id) || Find.CurrentMap == null)
			{
				return null;
			}
			foreach (Thing thing in Find.CurrentMap.listerThings.AllThings)
			{
				if (thing.ThingID.Equals(id, StringComparison.OrdinalIgnoreCase))
				{
					return thing;
				}
			}
			return null;
		}

		/// <summary>The camera to a thing (args: id) or a cell (args: x, z); args: zoom (the root size, 11 close ... 60 far) optional.</summary>
		private static string Camera(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			IntVec3 cell;
			if (Has(args, "id"))
			{
				Thing thing = ThingById(Str(args, "id"));
				if (thing == null)
				{
					return Failure("no thing with that id on this map");
				}
				cell = thing.Position;
			}
			else
			{
				cell = new IntVec3(Int(args, "x", Find.CameraDriver.MapPosition.x), 0, Int(args, "z", Find.CameraDriver.MapPosition.z));
			}
			Find.CameraDriver.JumpToCurrentMapLoc(cell);
			if (Has(args, "zoom"))
			{
				Find.CameraDriver.SetRootSize(Mathf.Clamp(Float(args, "zoom", 24f), 8f, 80f));
			}
			return Answer(w => w.WriteString("camera", cell.x + "," + cell.z));
		}

		/// <summary>Selects a thing (args: id; add: true keeps what is selected); args: jump (true) moves the camera to it.</summary>
		private static string SelectThing(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			Thing thing = ThingById(Str(args, "id"));
			if (thing == null)
			{
				return Failure("no thing with that id on this map");
			}
			bool add = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("add", out JsonElement a) && a.ValueKind == JsonValueKind.True;
			if (!add)
			{
				Find.Selector.ClearSelection();
			}
			Find.Selector.Select(thing);
			if (!(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("jump", out JsonElement j) && j.ValueKind == JsonValueKind.False))
			{
				Find.CameraDriver.JumpToCurrentMapLoc(thing.Position);
			}
			return Answer(w => w.WriteString("selected", thing.LabelCap.ToString()));
		}

		/// <summary>args: speed = paused, normal, fast, superfast, ultrafast (or 0 ... 4).</summary>
		private static string Speed(JsonElement args)
		{
			if (Current.ProgramState != ProgramState.Playing)
			{
				return Failure("not in a game");
			}
			string wanted = (Str(args, "speed") ?? Int(args, "speed", 0).ToString()).ToLowerInvariant();
			TimeSpeed speed;
			switch (wanted)
			{
				case "0": case "paused": case "pause": speed = TimeSpeed.Paused; break;
				case "1": case "normal": speed = TimeSpeed.Normal; break;
				case "2": case "fast": speed = TimeSpeed.Fast; break;
				case "3": case "superfast": speed = TimeSpeed.Superfast; break;
				case "4": case "ultrafast": speed = TimeSpeed.Ultrafast; break;
				default: return Failure("speed: paused, normal, fast, superfast or ultrafast");
			}
			Find.TickManager.CurTimeSpeed = speed;
			return Answer(w => w.WriteString("speed", speed.ToString()));
		}

		/// <summary>Closes the topmost window that can be closed (args: type = part of its type name, to pick another).</summary>
		private static string CloseWindow(JsonElement args)
		{
			string type = Str(args, "type");
			IList<Window> windows = Find.WindowStack.Windows;
			for (int i = windows.Count - 1; i >= 0; i--)
			{
				Window window = windows[i];
				if (window is ImmediateWindow || (type != null && window.GetType().FullName.IndexOf(type, StringComparison.OrdinalIgnoreCase) < 0))
				{
					continue;
				}
				Find.WindowStack.TryRemove(window, doCloseSound: true);
				return Answer(w => w.WriteString("closed", window.GetType().FullName));
			}
			return Failure("no such window");
		}

		/// <summary>The last letters and messages of the game's archive (args: count, default 10).</summary>
		private static string RecentMessages(JsonElement args)
		{
			if (Current.Game == null)
			{
				return Failure("not in a game");
			}
			int count = Int(args, "count", 10);
			List<IArchivable> archived = Find.Archive.ArchivablesListForReading;
			return Answer(w =>
			{
				w.WriteStartArray("messages");
				for (int i = archived.Count - 1; i >= 0 && i >= archived.Count - count; i--)
				{
					IArchivable item = archived[i];
					w.WriteStartObject();
					w.WriteString("label", item.ArchivedLabel);
					w.WriteNumber("tick", item.CreatedTicksGame);
					string tip = item.ArchivedTooltip ?? "";
					w.WriteString("text", tip.Length > 300 ? tip.Substring(0, 300) + "..." : tip);
					w.WriteEndObject();
				}
				w.WriteEndArray();
			});
		}

		/// <summary>
		/// For testing: fires an incident on the current map as the storyteller would (args: def = an IncidentDef name, e.g.
		/// TraderCaravanArrival, RaidEnemy, VisitorGroup, ManhunterPack; points optional). Without def: the incident defs
		/// whose name holds args.query, so that one can be found.
		/// </summary>
		private static string FireIncident(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			string name = Str(args, "def");
			if (string.IsNullOrEmpty(name))
			{
				string query = Str(args, "query", "");
				var found = DefDatabase<IncidentDef>.AllDefsListForReading.Where(d => d.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Select(d => d.defName).Take(60).ToList();
				return Answer(w =>
				{
					w.WriteStartArray("incidents");
					foreach (string n in found)
					{
						w.WriteStringValue(n);
					}
					w.WriteEndArray();
				});
			}
			IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail(name);
			if (def == null)
			{
				return Failure("no incident " + name + " (ask with query)");
			}
			IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, Find.CurrentMap);
			if (Has(args, "points"))
			{
				parms.points = Float(args, "points", parms.points);
			}
			parms.forced = true;
			bool fired = def.Worker.TryExecute(parms);
			float points = parms.points;
			return Answer(w =>
			{
				w.WriteString("incident", def.defName);
				w.WriteBoolean("fired", fired);
				w.WriteNumber("points", Mathf.Round(points));
			});
		}

		/// <summary>
		/// The world: the player's caravans (pawns, where, moving to), and the settlements nearest to the home map (or to
		/// args.from, a caravan's label) with faction, distance, whether one can trade there; with the screen position of
		/// each when the world is shown. args: limit (settlements, default 12).
		/// </summary>
		private static string WorldObjects(JsonElement args)
		{
			if (Current.ProgramState != ProgramState.Playing)
			{
				return Failure("not in a game");
			}
			int limit = Int(args, "limit", 12);
			bool worldShown = !WorldRendererUtility.DrawingMap;
			Map home = Find.AnyPlayerHomeMap;
			PlanetTile origin = home != null ? home.Tile : PlanetTile.Invalid;
			string from = Str(args, "from");
			foreach (Caravan c in Find.WorldObjects.Caravans)
			{
				if (from != null && c.IsPlayerControlled && c.Label.IndexOf(from, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					origin = c.Tile;
				}
			}
			void Screen(Utf8JsonWriter w, WorldObject o)
			{
				if (!worldShown)
				{
					return;
				}
				Vector3 world = o.DrawPos;
				Vector3 toCamera = Find.WorldCamera.transform.position - world;
				if (Vector3.Dot(toCamera, world) <= 0f)
				{
					return; // the other side of the planet
				}
				Vector2 ui = GenWorldUI.WorldToUIPosition(world);
				float x = ui.x * Prefs.UIScale, y = ui.y * Prefs.UIScale;
				if (x >= 0f && y >= 0f && x < UnityEngine.Screen.width && y < UnityEngine.Screen.height)
				{
					w.WriteStartArray("screen");
					w.WriteNumberValue(Mathf.Round(x * ResolutionChoice.ToScreen));
					w.WriteNumberValue(Mathf.Round(y * ResolutionChoice.ToScreen));
					w.WriteEndArray();
				}
			}
			return Answer(w =>
			{
				w.WriteBoolean("worldShown", worldShown);
				if (home != null)
				{
					w.WriteNumber("homeTile", home.Tile.tileId);
				}
				w.WriteStartArray("caravans");
				foreach (Caravan c in Find.WorldObjects.Caravans)
				{
					if (!c.IsPlayerControlled)
					{
						continue;
					}
					w.WriteStartObject();
					w.WriteString("label", c.Label);
					w.WriteNumber("tile", c.Tile.tileId);
					w.WriteBoolean("moving", c.pather != null && c.pather.Moving);
					if (c.pather != null && c.pather.Moving)
					{
						w.WriteNumber("destination", c.pather.Destination.tileId);
					}
					w.WriteStartArray("pawns");
					foreach (Pawn pawn in c.PawnsListForReading)
					{
						w.WriteStringValue(pawn.LabelShort);
					}
					w.WriteEndArray();
					string inspect = "";
					try
					{
						inspect = c.GetInspectString();
					}
					catch (Exception e)
					{
						inspect = "(" + e.GetType().Name + ")";
					}
					w.WriteString("inspect", inspect.Length > 500 ? inspect.Substring(0, 500) : inspect);
					Screen(w, c);
					w.WriteEndObject();
				}
				w.WriteEndArray();
				w.WriteStartArray("settlements");
				if (origin.Valid)
				{
					var near = Find.WorldObjects.Settlements.Where(st => st.Faction != Faction.OfPlayer && st.Tile.Layer == origin.Layer)
						.Select(st => (st, d: Find.WorldGrid.ApproxDistanceInTiles(origin, st.Tile)))
						.OrderBy(t => t.d).Take(limit).ToList();
					foreach (var (st, d) in near)
					{
						w.WriteStartObject();
						w.WriteString("label", st.LabelCap);
						w.WriteString("faction", st.Faction?.Name);
						w.WriteBoolean("hostile", st.Faction != null && st.Faction.HostileTo(Faction.OfPlayer));
						w.WriteBoolean("canTrade", st.CanTradeNow);
						w.WriteNumber("tile", st.Tile.tileId);
						w.WriteNumber("distance", Mathf.Round(d));
						Screen(w, st);
						w.WriteEndObject();
					}
				}
				w.WriteEndArray();
			});
		}

		/// <summary>
		/// What the game thinks of one pawn (args: id): downed, in bed, mental state, faction, current job and queue, lord,
		/// hediffs; with args.by (another pawn's id) also whether that one can reserve and reach it, as orders check.
		/// </summary>
		private static string PawnInfo(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			if (!(ThingById(Str(args, "id")) is Pawn pawn))
			{
				return Failure("no pawn with that id on this map");
			}
			Pawn by = Has(args, "by") ? ThingById(Str(args, "by")) as Pawn : null;
			return Answer(w =>
			{
				w.WriteString("label", pawn.LabelShortCap);
				w.WriteString("faction", pawn.Faction?.Name);
				w.WriteBoolean("downed", pawn.Downed);
				w.WriteBoolean("dead", pawn.Dead);
				w.WriteBoolean("inBed", pawn.InBed());
				w.WriteBoolean("drafted", pawn.Drafted);
				w.WriteString("mentalState", pawn.MentalStateDef?.defName);
				w.WriteBoolean("prisoner", pawn.IsPrisonerOfColony);
				w.WriteString("job", pawn.CurJob?.def.defName);
				w.WriteString("jobReport", SafeReport(pawn));
				w.WriteNumber("queued", pawn.jobs?.jobQueue?.Count ?? 0);
				w.WriteString("lord", pawn.GetLord()?.LordJob?.GetType().Name);
				w.WriteString("carriedBy", pawn.CarriedBy?.LabelShortCap);
				w.WriteBoolean("forbidden", pawn.IsForbidden(Faction.OfPlayer));
				w.WriteStartArray("hediffs");
				foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
				{
					w.WriteStringValue(hediff.LabelCap + (hediff.Part != null ? " (" + hediff.Part.LabelCap + ")" : ""));
				}
				w.WriteEndArray();
				if (by != null)
				{
					w.WriteBoolean("byCanReach", by.CanReach(pawn, Verse.AI.PathEndMode.OnCell, Danger.Deadly));
					w.WriteBoolean("byCanReserve", by.CanReserve(pawn, 1, -1, null, true));
					w.WriteString("reservedBy", by.Map.reservationManager.FirstRespectedReserver(pawn, by)?.LabelShortCap);
					w.WriteBoolean("wantsRescue", HealthAIUtility.WantsToBeRescued(pawn));
					w.WriteBoolean("canRescueNow", HealthAIUtility.CanRescueNow(by, pawn, true));
					w.WriteBoolean("willJoinIfRescued", pawn.mindState.WillJoinColonyIfRescued);
					w.WriteBoolean("canSuckle", ChildcareUtility.CanSuckle(pawn, out _));
					w.WriteBoolean("shouldSeekMedicalRest", HealthAIUtility.ShouldSeekMedicalRest(pawn));
					w.WriteBoolean("alwaysDowned", pawn.ageTracker.CurLifeStage.alwaysDowned);
					w.WriteString("medCare", pawn.playerSettings?.medCare.ToString());
					w.WriteString("bedFor", RestUtility.FindBedFor(pawn, by, checkSocialProperness: false)?.LabelShortCap);
				}
				var patched = Harmony.GetPatchInfo(AccessTools.Method(typeof(FloatMenuOptionProvider_RescuePawn), "GetSingleOptionFor", new[] { typeof(Pawn), typeof(FloatMenuContext) }));
				if (patched != null)
				{
					w.WriteStartArray("rescuePatches");
					foreach (Patch patch in patched.Prefixes.Concat(patched.Postfixes).Concat(patched.Transpilers).Concat(patched.Finalizers))
					{
						w.WriteStringValue(patch.owner + " " + patch.PatchMethod.DeclaringType?.FullName + "." + patch.PatchMethod.Name);
					}
					w.WriteEndArray();
				}
			});
		}

		private static readonly AccessTools.FieldRef<List<FloatMenuOptionProvider>> Providers = AccessTools.StaticFieldRefAccess<List<FloatMenuOptionProvider>>(AccessTools.Field(typeof(FloatMenuMakerMap), "providers"));

		/// <summary>
		/// The orders the selected pawns would get for a right click on a cell (args: x, z), as the game makes them, and for
		/// each menu provider whether it applies there and what it gave for each thing on the cell: to find out why an
		/// order is missing. Nothing is ordered.
		/// </summary>
		private static string MenuAt(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			var pawns = Find.Selector.SelectedPawns.ToList();
			if (pawns.Count == 0)
			{
				return Failure("select a pawn first");
			}
			Vector3 at = new IntVec3(Int(args, "x", 0), 0, Int(args, "z", 0)).ToVector3Shifted();
			List<FloatMenuOption> options = FloatMenuMakerMap.GetOptions(new List<Pawn>(pawns), at, out FloatMenuContext context);
			return Answer(w =>
			{
				w.WriteStartArray("options");
				foreach (FloatMenuOption option in options)
				{
					w.WriteStringValue(option.Label + (option.Disabled ? " [disabled]" : ""));
				}
				w.WriteEndArray();
				if (context == null)
				{
					return;
				}
				w.WriteStartArray("providers");
				foreach (FloatMenuOptionProvider provider in Providers())
				{
					string name = provider.GetType().Name;
					string result;
					try
					{
						FloatMenuMakerMap.currentProvider = provider;
						if (!context.ValidSelectedPawns.Any())
						{
							result = "no valid selected pawn";
						}
						else if (!provider.Applies(context))
						{
							continue; // most do not: left out
						}
						else
						{
							var parts = new List<string>();
							int own = provider.GetOptions(context).Count();
							if (own > 0)
							{
								parts.Add(own + " for the cell");
							}
							foreach (Pawn clicked in context.ClickedPawns)
							{
								if (!provider.TargetPawnValid(clicked, context))
								{
									parts.Add(clicked.LabelShort + ": target not valid");
									continue;
								}
								var got = provider.GetOptionsFor(clicked, context).Where(o => o != null).Select(o => o.Label).ToList();
								parts.Add(clicked.LabelShort + ": " + (got.Count == 0 ? "nothing" : string.Join(" / ", got)));
							}
							result = parts.Count == 0 ? "applies, nothing" : string.Join("; ", parts);
						}
					}
					catch (Exception e)
					{
						result = "threw " + e.GetType().Name + ": " + e.Message;
					}
					finally
					{
						FloatMenuMakerMap.currentProvider = null;
					}
					w.WriteStringValue(name + " -> " + result);
				}
				w.WriteEndArray();
			});
		}

		// ---- for testing: changes to the game being played (only ever reached with the agent switched on) ----

		/// <summary>Finishes a research project (args: def, a ResearchProjectDef name) and every one it needs first. Without def: the names that hold args.query.</summary>
		private static string FinishResearch(JsonElement args)
		{
			if (Current.Game == null)
			{
				return Failure("not in a game");
			}
			string name = Str(args, "def");
			if (string.IsNullOrEmpty(name))
			{
				string query = Str(args, "query", "");
				var found = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(d => d.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || d.label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Select(d => d.defName + " (" + d.label + (d.IsFinished ? ", done" : "") + ")").Take(40).ToList();
				return Answer(w => { w.WriteStartArray("projects"); foreach (string f in found) w.WriteStringValue(f); w.WriteEndArray(); });
			}
			ResearchProjectDef target = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
			if (target == null)
			{
				return Failure("no research project " + name);
			}
			var done = new List<string>();
			void Finish(ResearchProjectDef proj)
			{
				if (proj.IsFinished)
				{
					return;
				}
				foreach (ResearchProjectDef pre in proj.prerequisites ?? new List<ResearchProjectDef>())
				{
					Finish(pre);
				}
				foreach (ResearchProjectDef pre in proj.hiddenPrerequisites ?? new List<ResearchProjectDef>())
				{
					Finish(pre);
				}
				Find.ResearchManager.FinishProject(proj, false, null, false);
				done.Add(proj.defName);
			}
			Finish(target);
			return Answer(w => { w.WriteStartArray("finished"); foreach (string d in done) w.WriteStringValue(d); w.WriteEndArray(); });
		}

		/// <summary>Anomaly: sets the monolith's level (args: level; entities are studied and captured from 1 on).</summary>
		private static string MonolithLevel(JsonElement args)
		{
			if (!Playing || !ModsConfig.AnomalyActive || Find.Anomaly == null)
			{
				return Failure("no game with Anomaly");
			}
			int level = Int(args, "level", 1);
			MonolithLevelDef def = DefDatabase<MonolithLevelDef>.AllDefs.FirstOrDefault(d => d.level == level);
			if (def == null)
			{
				return Failure("no monolith level " + level);
			}
			Find.Anomaly.SetLevel(def, true);
			return Answer(w => { w.WriteNumber("level", Find.Anomaly.Level); w.WriteNumber("highest", Find.Anomaly.HighestLevelReached); });
		}

		/// <summary>Spawns a thing on the current map (args: def, stuff, count, x, z, rot 0-3). A building is the colony's and complete. Without def: the def names that hold args.query.</summary>
		private static string SpawnThing(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			string name = Str(args, "def");
			if (string.IsNullOrEmpty(name))
			{
				string query = Str(args, "query", "");
				var found = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (d.label ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Select(d => d.defName + " (" + d.label + ", " + d.category + ")").Take(40).ToList();
				return Answer(w => { w.WriteStartArray("defs"); foreach (string f in found) w.WriteStringValue(f); w.WriteEndArray(); });
			}
			ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
			if (def == null)
			{
				return Failure("no thing def " + name);
			}
			ThingDef stuff = Has(args, "stuff") ? DefDatabase<ThingDef>.GetNamedSilentFail(Str(args, "stuff")) : (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
			Map map = Find.CurrentMap;
			var cell = new IntVec3(Int(args, "x", Find.CameraDriver.MapPosition.x), 0, Int(args, "z", Find.CameraDriver.MapPosition.z));
			int count = Math.Max(1, Int(args, "count", 1));
			var placed = new List<string>();
			while (count > 0)
			{
				Thing thing = ThingMaker.MakeThing(def, stuff);
				int n = Math.Min(count, def.stackLimit);
				thing.stackCount = n;
				count -= n;
				if (def.category == ThingCategory.Building)
				{
					if (thing.def.CanHaveFaction)
					{
						thing.SetFaction(Faction.OfPlayer);
					}
					GenSpawn.Spawn(thing, cell, map, new Rot4(Int(args, "rot", 0)), WipeMode.Vanish);
					placed.Add(thing.ThingID);
					break;
				}
				if (!GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Near))
				{
					return Failure("no room near " + cell);
				}
				placed.Add(thing.ThingID + " x" + n);
			}
			return Answer(w => { w.WriteStartArray("spawned"); foreach (string t in placed) w.WriteStringValue(t); w.WriteEndArray(); });
		}

		/// <summary>Makes a pawn on the current map (args: kind = PawnKindDef, faction = player / hostile / a FactionDef name, x, z, downed).</summary>
		private static string MakePawn(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(Str(args, "kind", "Colonist"));
			if (kind == null)
			{
				return Failure("no pawn kind " + Str(args, "kind"));
			}
			string f = Str(args, "faction", "player");
			Faction faction = f == "player" ? Faction.OfPlayer
				: f == "hostile" ? Find.FactionManager.RandomEnemyFaction(false, false, false)
				: f == "none" ? null
				: Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x.def.defName == f);
			Pawn pawn = PawnGenerator.GeneratePawn(kind, faction);
			var cell = new IntVec3(Int(args, "x", Find.CameraDriver.MapPosition.x), 0, Int(args, "z", Find.CameraDriver.MapPosition.z));
			GenSpawn.Spawn(pawn, CellFinder.StandableCellNear(cell, Find.CurrentMap, 5f), Find.CurrentMap);
			if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("downed", out JsonElement d) && d.ValueKind == JsonValueKind.True)
			{
				HealthUtility.DamageUntilDowned(pawn, allowBleedingWounds: false);
			}
			return Answer(w =>
			{
				w.WriteString("id", pawn.ThingID);
				w.WriteString("label", pawn.LabelShortCap);
				w.WriteString("faction", pawn.Faction?.Name);
				w.WriteBoolean("downed", pawn.Downed);
			});
		}

		/// <summary>Heals a pawn's injuries (args: id; all: true for every colonist on the map).</summary>
		private static string HealPawn(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			var pawns = new List<Pawn>();
			if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("all", out JsonElement a) && a.ValueKind == JsonValueKind.True)
			{
				pawns.AddRange(Find.CurrentMap.mapPawns.FreeColonistsSpawned);
			}
			else if (ThingById(Str(args, "id")) is Pawn one)
			{
				pawns.Add(one);
			}
			foreach (Pawn pawn in pawns)
			{
				HealthUtility.HealNonPermanentInjuriesAndRestoreLegs(pawn);
			}
			return Answer(w => w.WriteNumber("healed", pawns.Count));
		}

		/// <summary>Kills a pawn or destroys a thing (args: id; or kind: hostile for every hostile pawn and turret on the map).</summary>
		private static string KillThings(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			var things = new List<Thing>();
			if (Str(args, "kind") == "hostile")
			{
				things.AddRange(Find.CurrentMap.mapPawns.AllPawnsSpawned.Where(x => x.HostileTo(Faction.OfPlayer) && !x.Dead));
				things.AddRange(Find.CurrentMap.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).Where(x => x.def.building != null && x.def.building.IsTurret && x.HostileTo(Faction.OfPlayer)));
			}
			else if (ThingById(Str(args, "id")) is Thing one)
			{
				things.Add(one);
			}
			foreach (Thing thing in things)
			{
				thing.Kill(null, null);
			}
			return Answer(w => w.WriteNumber("killed", things.Count));
		}

		/// <summary>Saves the game being played under a name (args: name), as the save menu does.</summary>
		private static string SaveGame(JsonElement args)
		{
			if (Current.Game == null)
			{
				return Failure("not in a game");
			}
			string name = Str(args, "name");
			if (string.IsNullOrEmpty(name))
			{
				return Failure("a name is needed");
			}
			GameDataSaveLoader.SaveGame(name);
			return Answer(w => w.WriteString("saved", name));
		}

		/// <summary>string.GetHashCode() of each of args.texts (the runtime fork's Mono hash, host/corelib.sh): the same numbers as on the PC.</summary>
		private static string StringHashes(JsonElement args)
		{
			var texts = new List<string>();
			if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("texts", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement t in list.EnumerateArray())
				{
					texts.Add(t.GetString() ?? "");
				}
			}
			return Answer(w =>
			{
				w.WriteStartArray("hashes");
				foreach (string t in texts)
				{
					w.WriteNumberValue(t.GetHashCode());
				}
				w.WriteEndArray();
			});
		}

		/// <summary>
		/// For measuring: settings of the frame rate and the scheduling switched while the game runs (args.set, e.g.
		/// ["frames=90", "schedule=2", "wait=300"]: what perfcmd.txt takes; not saved to mobile.txt), and the report of the
		/// frame wait since the last call.
		/// </summary>
		private static string Tune(JsonElement args)
		{
			if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("set", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in list.EnumerateArray())
				{
					string[] parts = (item.GetString() ?? "").Split(new[] { '=' }, 2);
					string name = parts[0].Trim(), value = parts.Length == 2 ? parts[1].Trim() : "1";
					if (!FrameRate.Set(name, value))
					{
						Sched.Set(name, value);
					}
				}
			}
			return Answer(w =>
			{
				w.WriteString("frameRate", FrameRate.Describe());
				w.WriteString("wait", FrameRate.DescribeWait());
				w.WriteString("sched", Sched.Describe());
			});
		}

		/// <summary>Adds a relation between two pawns (args: a, b = ids; def = Lover, Fiance, Spouse, ...).</summary>
		private static string AddRelation(JsonElement args)
		{
			if (!(ThingById(Str(args, "a")) is Pawn a) || !(ThingById(Str(args, "b")) is Pawn b))
			{
				return Failure("two pawn ids are needed (a, b)");
			}
			PawnRelationDef def = DefDatabase<PawnRelationDef>.GetNamedSilentFail(Str(args, "def", "Lover"));
			if (def == null)
			{
				return Failure("no relation " + Str(args, "def"));
			}
			a.relations.AddDirectRelation(def, b);
			return Answer(w => w.WriteString("relation", a.LabelShort + " - " + def.defName + " - " + b.LabelShort));
		}

		/// <summary>The rituals of the colony's primary ideoligion: label, def, and what they take place at.</summary>
		/// <summary>TEST ONLY: shows the world and picks a tile there as a shuttle does (the choice is only logged).</summary>
		/// <summary>
		/// TEST ONLY: what RimUnity's screen effects do (PortHooks.SharedFallbacks): a mesh renderer of its own takes the
		/// white fallback textures every renderer shares and is let go while the game runs. Answers whether the shared
		/// texture still has its image afterwards (the engine's own Dispose takes it, mobile.txt sharedFallbacks=0).
		/// </summary>
		private static string TestFallbacks()
		{
			Waylith.EngineApplication app = Waylith.EngineApplication.Instance;
			if (app?.GraphicsDevice == null || app.RenderContext == null)
			{
				return Failure("no graphics device yet");
			}
			Type rendererType = typeof(Waylith.Rendering.MeshBatchRenderer);
			System.Reflection.MethodInfo take = AccessTools.Method(rendererType, "GetFallbackTexture");
			Type kind = take.GetParameters()[1].ParameterType;
			// By reflection: 1.7.4998 added an optional third parameter to the constructor.
			System.Reflection.ConstructorInfo make = rendererType.GetConstructors().First(c => c.GetParameters().Length >= 2 && c.GetParameters()[0].ParameterType == typeof(Waylith.Graphics.GraphicsDevice));
			object[] values = make.GetParameters().Select((p, i) => i == 0 ? app.GraphicsDevice : i == 1 ? (object)app.RenderContext : p.DefaultValue).ToArray();
			var renderer = (Waylith.Rendering.MeshBatchRenderer)make.Invoke(values);
			object shared = take.Invoke(renderer, new object[] { false, Enum.Parse(kind, "White") });
			object sharedArray = take.Invoke(renderer, new object[] { true, Enum.Parse(kind, "White") });
			renderer.Dispose();
			bool kept = ((Waylith.Rendering.Texture)shared).GpuResource != null, arrayKept = ((Waylith.Rendering.Texture)sharedArray).GpuResource != null;
			Console.WriteLine("[mobile] agent: fallback test: a renderer was let go; Core.WhiteFallback " + (kept ? "kept" : "lost its image") + ", Core.WhiteArrayFallback " + (arrayKept ? "kept" : "lost its image"));
			return Answer(w =>
			{
				w.WriteBoolean("whiteKept", kept);
				w.WriteBoolean("whiteArrayKept", arrayKept);
			});
		}

		/// <summary>
		/// Test: what the phone's CoreLib has from Ludeon's (patch/Patch.cs AddLudeonMembers), called the way mods and the
		/// engine call them, and the engine's frame profiler switched on ("profiler": true; false switches it off again),
		/// which needs X86Base.ReadTimeStampCounter to exist and, from 1.7.4995 on, reads ArmBase.Arm64's counter through
		/// ProfilerClock (a 20 ms sleep has to measure about 20 ms by it).
		/// </summary>
		private static string TestCompat(JsonElement args)
		{
			var results = new List<string>();
			void Try(string name, Func<object> call)
			{
				try
				{
					results.Add(name + ": " + call());
				}
				catch (Exception e)
				{
					results.Add(name + ": " + (e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e).GetType().Name + " " + (e.InnerException ?? e).Message);
				}
			}
			Type jit = Type.GetType("System.Runtime.CompilerServices.JitHelpers");
			Try("JitHelpers.UnsafeEnumCast(Intelligence.Humanlike)", () => jit.GetMethod("UnsafeEnumCast").MakeGenericMethod(typeof(Intelligence)).Invoke(null, new object[] { Intelligence.Humanlike }));
			Try("JitHelpers.UnsafeEnumCastLong((byte)200)", () => jit.GetMethod("UnsafeEnumCastLong").MakeGenericMethod(typeof(byte)).Invoke(null, new object[] { (byte)200 }));
			Try("JitHelpers.UnsafeEnumCast((long)-5)", () => jit.GetMethod("UnsafeEnumCast").MakeGenericMethod(typeof(long)).Invoke(null, new object[] { -5L }));
			Try("AppDomain.DefineDynamicAssembly(RunAndSave) + DefineDynamicModule(name, file)", () =>
			{
				var name = new System.Reflection.AssemblyName("port17compat");
				object builder = typeof(AppDomain).GetMethod("DefineDynamicAssembly", new[] { typeof(System.Reflection.AssemblyName), typeof(System.Reflection.Emit.AssemblyBuilderAccess) })
					.Invoke(AppDomain.CurrentDomain, new object[] { name, (System.Reflection.Emit.AssemblyBuilderAccess)3 });
				object module = builder.GetType().GetMethod("DefineDynamicModule", new[] { typeof(string), typeof(string) }).Invoke(builder, new object[] { "port17compat", "port17compat.dll" });
				return builder.GetType().Name + ", " + module.GetType().Name;
			});
			Try("X86Base.ReadTimeStampCounter exists", () => typeof(System.Runtime.Intrinsics.X86.X86Base).GetMethod("ReadTimeStampCounter") != null);
			// The clock takes one of the two by the CPU's features (the second with FEAT_ECV): both are called here.
			foreach (string counter in new[] { "ReadVirtualCounter", "ReadVirtualCounterSelfSynchronized" })
			{
				Try("ArmBase.Arm64." + counter + "()", () => typeof(System.Runtime.Intrinsics.Arm.ArmBase.Arm64).GetMethod(counter)?.Invoke(null, null) ?? "missing");
			}
			Try("ProfilerClock", () =>
			{
				long start = Waylith.Profiling.ProfilerClock.GetTimestamp();
				System.Threading.Thread.Sleep(20);
				double ms = (Waylith.Profiling.ProfilerClock.GetTimestamp() - start) * 1000.0 / Waylith.Profiling.ProfilerClock.Frequency;
				return "counter " + Waylith.Profiling.ProfilerClock.UsesTimeStampCounter + ", frequency " + Waylith.Profiling.ProfilerClock.Frequency + ", a 20 ms sleep measured " + ms.ToString("F1") + " ms";
			});
			if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("profiler", out JsonElement on))
			{
				Try("FrameProfiler.SetEnabled(" + on.GetBoolean() + ")", () =>
				{
					Waylith.Profiling.FrameProfiler.SetEnabled(on.GetBoolean());
					return "done (frames from now go through BeginFrame)";
				});
			}
			foreach (string line in results)
			{
				Console.WriteLine("[mobile] agent: compat: " + line);
			}
			return Answer(w =>
			{
				w.WriteStartArray("results");
				foreach (string line in results)
				{
					w.WriteStringValue(line);
				}
				w.WriteEndArray();
			});
		}

		private static string TestWorldTarget()
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			CameraJumper.TryShowWorld();
			Find.WorldTargeter.BeginTargeting(target =>
			{
				Console.WriteLine("[mobile] agent: world target chosen: " + target);
				return true;
			}, true, closeWorldTabWhenFinished: true);
			return Answer(w => w.WriteString("targeting", "world"));
		}

		private static string Rituals()
		{
			Ideo ideo = Faction.OfPlayer?.ideos?.PrimaryIdeo;
			if (ideo == null)
			{
				return Failure("no ideoligion");
			}
			return Answer(w =>
			{
				w.WriteString("ideo", ideo.name);
				w.WriteBoolean("classic", Find.IdeoManager.classicMode);
				w.WriteStartArray("rituals");
				foreach (Precept precept in ideo.PreceptsListForReading)
				{
					if (!(precept is Precept_Ritual ritual))
					{
						continue;
					}
					w.WriteStartObject();
					w.WriteString("label", ritual.LabelCap);
					w.WriteString("def", ritual.def.defName);
					w.WriteBoolean("playerCanStart", ritual.def.mergeRitualGizmosFromAllIdeos || !ritual.isAnytime || true);
					var targets = new List<string>();
					try
					{
						if (ritual.obligationTargetFilter != null)
						{
							targets.AddRange(ritual.obligationTargetFilter.GetTargetInfos(null));
						}
					}
					catch (Exception e)
					{
						targets.Add("(" + e.GetType().Name + ")");
					}
					w.WriteString("targets", string.Join(", ", targets));
					w.WriteEndObject();
				}
				w.WriteEndArray();
			});
		}

		/// <summary>Starts the marriage ceremony of two fiancés now (args: a, b = ids), as the game does after ten days of engagement.</summary>
		private static string StartMarriage(JsonElement args)
		{
			if (!(ThingById(Str(args, "a")) is Pawn a) || !(ThingById(Str(args, "b")) is Pawn b))
			{
				return Failure("two pawn ids are needed (a, b)");
			}
			if (!a.relations.DirectRelationExists(PawnRelationDefOf.Fiance, b))
			{
				a.relations.AddDirectRelation(PawnRelationDefOf.Fiance, b);
			}
			bool started = a.Map.lordsStarter.TryStartMarriageCeremony(a, b);
			return Answer(w => w.WriteBoolean("started", started));
		}

		private static string SafeReport(Pawn pawn)
		{
			try
			{
				return pawn.jobs?.curDriver?.GetReport();
			}
			catch (Exception e)
			{
				return "(" + e.GetType().Name + ")";
			}
		}

		/// <summary>The zones of the current map: label, kind, cell count, bounds, and what grows or is stored.</summary>
		private static string Zones()
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			return Answer(w =>
			{
				w.WriteStartArray("zones");
				foreach (Zone zone in Find.CurrentMap.zoneManager.AllZones)
				{
					w.WriteStartObject();
					w.WriteString("label", zone.label);
					w.WriteString("type", zone.GetType().Name);
					w.WriteNumber("cells", zone.Cells.Count);
					CellRect bounds = CellRect.FromCellList(zone.Cells);
					w.WriteString("bounds", bounds.minX + "," + bounds.minZ + " - " + bounds.maxX + "," + bounds.maxZ);
					if (zone is Zone_Growing growing)
					{
						w.WriteString("plant", growing.GetPlantDefToGrow()?.label);
					}
					w.WriteEndObject();
				}
				w.WriteEndArray();
			});
		}

		/// <summary>Where a cell of the map (args: x, z) is on the screen, in pixels.</summary>
		private static string CellOnScreen(JsonElement args)
		{
			if (!Playing)
			{
				return Failure("no map is shown");
			}
			var cell = new IntVec3(Int(args, "x", 0), 0, Int(args, "z", 0));
			bool on = ScreenPoint(cell.ToVector3Shifted(), out float x, out float y);
			return Answer(w =>
			{
				w.WriteBoolean("onScreen", on);
				w.WriteNumber("x", Mathf.Round(x));
				w.WriteNumber("y", Mathf.Round(y));
			});
		}
	}
}
