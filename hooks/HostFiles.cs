using System;
using System.IO;

namespace PortHooks
{
	/// <summary>
	/// Locates a file shipped by the port itself. Android's staged game folder is AppContext.BaseDirectory;
	/// iOS's BaseDirectory is still the game folder, but PortHooks/PortMobile and their data files live in
	/// the .app bundle (TPA), whose directory is this assembly's Location. An empty Location or a missing
	/// file falls back to the game folder so a non-TPA test can still find its file.
	/// </summary>
	public static class HostFiles
	{
		public static string Resolve(string fileName)
		{
			string assemblyDir = Path.GetDirectoryName(typeof(HostFiles).Assembly.Location);
			if (!string.IsNullOrEmpty(assemblyDir))
			{
				string bundled = Path.Combine(assemblyDir, fileName);
				if (File.Exists(bundled))
				{
					return bundled;
				}
			}
			return Path.Combine(AppContext.BaseDirectory, fileName);
		}
	}
}
