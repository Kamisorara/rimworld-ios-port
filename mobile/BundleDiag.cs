using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace PortMobile
{
	/// <summary>
	/// A test of the engine's Unity asset bundle reader on the phone, run when bundletest.txt lies next to the game: every
	/// line names a bundle file or a folder (searched for files that start with "UnityFS"); each bundle is opened and
	/// everything in it loaded, and what came out is written to the log (textures with size and format, shaders with
	/// whether they can be used, materials with their shader). The engine's own complaints about a bundle ("shader ...
	/// can't be used: no Vulkan variant in this bundle") appear in the log beside them. The file is renamed when done, so
	/// the test runs once.
	/// </summary>
	internal static class BundleDiag
	{
		public static void Start()
		{
			string list = Path.Combine(AppContext.BaseDirectory, "bundletest.txt");
			if (!File.Exists(list))
			{
				return;
			}
			// After the load that is under way, on the game's thread: loading makes textures and shaders.
			LongEventHandler.ExecuteWhenFinished(delegate
			{
				try
				{
					Run(File.ReadAllLines(list));
				}
				catch (Exception e)
				{
					Console.WriteLine("[mobile] bundle test failed: " + e);
				}
				try
				{
					File.Delete(list + ".done");
					File.Move(list, list + ".done");
				}
				catch (Exception)
				{
				}
			});
		}

		private static bool IsBundle(string file)
		{
			try
			{
				var head = new byte[8];
				using (FileStream stream = File.OpenRead(file))
				{
					return stream.Read(head, 0, 8) == 8 && head[0] == 'U' && head[1] == 'n' && head[2] == 'i' && head[3] == 't' && head[4] == 'y' && head[5] == 'F' && head[6] == 'S' && head[7] == 0;
				}
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static void Run(string[] lines)
		{
			var bundles = new List<string>();
			foreach (string raw in lines)
			{
				string line = raw.Trim();
				if (line.Length == 0 || line[0] == '#')
				{
					continue;
				}
				if (Directory.Exists(line))
				{
					bundles.AddRange(Directory.GetFiles(line, "*", SearchOption.AllDirectories).Where(IsBundle).OrderBy(f => f, StringComparer.Ordinal));
				}
				else if (File.Exists(line))
				{
					bundles.Add(line);
				}
				else
				{
					Console.WriteLine("[mobile] bundle test: nothing at " + line);
				}
			}
			Console.WriteLine("[mobile] bundle test: " + bundles.Count + " bundles");
			int opened = 0, failed = 0;
			foreach (string file in bundles)
			{
				string shown = file.Length > 70 ? "..." + file.Substring(file.Length - 67) : file;
				var watch = System.Diagnostics.Stopwatch.StartNew();
				AssetBundle bundle = null;
				try
				{
					bundle = AssetBundle.LoadFromFile(file);
					if (bundle == null)
					{
						failed++;
						Console.WriteLine("[mobile] bundle test: " + shown + ": could not be opened");
						continue;
					}
					string[] names = bundle.GetAllAssetNames();
					UnityEngine.Object[] assets = bundle.LoadAllAssets();
					opened++;
					Console.WriteLine("[mobile] bundle test: " + shown + ": " + names.Length + " named assets, " + assets.Length + " objects (" + watch.ElapsedMilliseconds + " ms)");
					var counts = new Dictionary<string, int>();
					foreach (UnityEngine.Object asset in assets)
					{
						string type = asset == null ? "null" : asset.GetType().Name;
						counts.TryGetValue(type, out int n);
						counts[type] = n + 1;
						try
						{
							if (asset is Shader shader)
							{
								Console.WriteLine("[mobile] bundle test:   shader '" + shader.name + "': " + (shader.isSupported ? "usable" : "NOT usable") + ", " + shader.passCount + " passes, queue " + shader.renderQueue);
							}
							else if (asset is Material material)
							{
								Shader of = material.shader;
								Console.WriteLine("[mobile] bundle test:   material '" + material.name + "': shader " + (of == null ? "none" : "'" + of.name + "' " + (of.isSupported ? "usable" : "NOT usable")));
							}
							else if (asset is Texture2D texture && n < 4)
							{
								Console.WriteLine("[mobile] bundle test:   texture '" + texture.name + "': " + texture.width + "x" + texture.height + " " + texture.format);
							}
						}
						catch (Exception e)
						{
							Console.WriteLine("[mobile] bundle test:   " + type + " '" + asset.name + "': " + e.GetType().Name + ": " + e.Message);
						}
					}
					Console.WriteLine("[mobile] bundle test:   " + string.Join(", ", counts.OrderBy(c => c.Key).Select(c => c.Value + " " + c.Key)));
				}
				catch (Exception e)
				{
					failed++;
					Console.WriteLine("[mobile] bundle test: " + shown + ": " + e.GetType().Name + ": " + e.Message);
				}
				finally
				{
					try
					{
						bundle?.Unload(true); // with what was loaded from it: the test is not to fill the memory
					}
					catch (Exception)
					{
					}
				}
			}
			Console.WriteLine("[mobile] bundle test done: " + opened + " opened, " + failed + " failed");
		}
	}
}
