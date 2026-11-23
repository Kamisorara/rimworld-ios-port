using System;
using System.Runtime.InteropServices;

namespace PortHooks
{
	/// <summary>
	/// The current OS thread id where the platform has Linux's gettid, otherwise the managed thread id.
	/// The phone code uses these values only to recognise the same thread over time (main, render,
	/// workers); Android's /proc and affinity helpers still need and receive the real Linux tid.
	/// </summary>
	public static class ThreadId
	{
		[DllImport("libc", EntryPoint = "gettid")]
		private static extern int LinuxGettid();

		public static int Current
		{
			get
			{
				if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
				{
					try
					{
						return LinuxGettid();
					}
					catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
					{
					}
				}
				return Environment.CurrentManagedThreadId;
			}
		}
	}
}
