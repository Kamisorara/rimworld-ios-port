using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace PortMobile
{
	/// <summary>
	/// Agent command "xmltest": an XmlSerializer for a class shaped like Multiplayer's ReplayInfo (lists of strings, an
	/// int list, a list of objects, "li" items), which threw "An item with the same key has already been added. Key:
	/// (System.Int32, ia)" in XmlSerializationWriterILGen on the phone and not on the PC's .NET 10.0.12.
	/// </summary>
	public static class XmlSerializerTest
	{
		public class Bool { [XmlText] public bool v; }
		public class Sec { public int start; public int end; }
		public class Info
		{
			public string name; public int protocol; public int playerFaction; public int spectatorFaction;
			public List<Sec> sections = new List<Sec>();
			public string rwVersion; public List<string> modIds; public List<string> modNames; public List<int> modAssemblyHashes;
			public Bool asyncTime; public bool multifaction;
		}

		public static string Probe()
		{
			var parts = new List<string>();
			string a = "ia", b = new string(new[] { 'i', 'a' });
			parts.Add("str " + a.GetHashCode() + "/" + b.GetHashCode() + "/" + a.GetHashCode());
			parts.Add("cmp " + EqualityComparer<string>.Default.GetHashCode(a) + "/" + EqualityComparer<string>.Default.GetHashCode(b));
			parts.Add("type " + typeof(int).GetHashCode() + "/" + typeof(int).GetHashCode());
			parts.Add("tuple " + (typeof(int), a).GetHashCode() + "/" + (typeof(int), b).GetHashCode() + "/" + EqualityComparer<(Type, string)>.Default.GetHashCode((typeof(int), b)));
			parts.Add("hc " + HashCode.Combine(1, 2) + "/" + HashCode.Combine(1, 2));
			var seedField = typeof(HashCode).GetField("s_seed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
			parts.Add("seed " + (seedField == null ? "no field" : seedField.GetValue(null) + " initonly " + seedField.IsInitOnly));
			var d = new Dictionary<(Type, string), int>();
			d.Add((typeof(int), a), 1);
			parts.Add("dict found " + d.TryGetValue((typeof(int), b), out _));
			return string.Join("; ", parts);
		}

		public static string Run()
		{
			// Multiplayer's own ReplayInfo when it is loaded.
			Type replay = HarmonyLib.AccessTools.TypeByName("Multiplayer.Common.ReplayInfo");
			if (replay != null)
			{
				try
				{
					object info = Activator.CreateInstance(replay);
					var bytes = (byte[])replay.GetMethod("Write").Invoke(null, new[] { info });
					return "{\"ok\":true,\"replayInfoBytes\":" + bytes.Length + ",\"probe\":" + System.Text.Json.JsonSerializer.Serialize(Probe()) + "}";
				}
				catch (Exception e)
				{
					Exception inner = e.InnerException ?? e;
					return "{\"ok\":false,\"replayInfo\":" + System.Text.Json.JsonSerializer.Serialize(inner.GetType().Name + ": " + inner.Message) + ",\"probe\":" + System.Text.Json.JsonSerializer.Serialize(Probe()) + "}";
				}
			}
			try
			{
				var o = new XmlAttributeOverrides();
				o.Add(typeof(Info), "sections", new XmlAttributes { XmlArrayItems = { new XmlArrayItemAttribute("li") } });
				o.Add(typeof(Info), "modIds", new XmlAttributes { XmlArrayItems = { new XmlArrayItemAttribute("li") } });
				o.Add(typeof(Info), "modNames", new XmlAttributes { XmlArrayItems = { new XmlArrayItemAttribute("li") } });
				var s = new XmlSerializer(typeof(Info), o);
				var w = new StringWriter();
				s.Serialize(w, new Info { name = "x", modIds = new List<string> { "a" }, modNames = new List<string> { "b" }, modAssemblyHashes = new List<int> { 1 } });
				string probe = "(Int32 ia) hashes: " + ("ia".GetHashCode() == "ia".GetHashCode()) + " " + (("ia".GetHashCode()) == new string(new[] { 'i', 'a' }).GetHashCode()) + " tuple " + ((typeof(int), "ia").GetHashCode() == (typeof(int), new string(new[] { 'i', 'a' })).GetHashCode());
				return "{\"ok\":true,\"length\":" + w.ToString().Length + ",\"probe\":\"" + probe + "\"}";
			}
			catch (Exception e)
			{
				string probe = "tuple " + ((typeof(int), "ia").GetHashCode() == (typeof(int), new string(new[] { 'i', 'a' })).GetHashCode()) + " str " + ("ia".GetHashCode() == new string(new[] { 'i', 'a' }).GetHashCode());
				return "{\"ok\":false,\"error\":" + System.Text.Json.JsonSerializer.Serialize(e.GetType().Name + ": " + e.Message) + ",\"probe\":\"" + probe + "\",\"stack\":" + System.Text.Json.JsonSerializer.Serialize(e.StackTrace ?? "") + "}";
			}
		}
	}
}
