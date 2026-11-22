using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace PortHooks
{
	/// <summary>
	/// The phone's HTTP proxy for HttpClient and what is built on it (WebClient, ClientWebSocket, the engine's
	/// UnityWebRequest), as the PC's runtime follows Windows' proxy settings (HttpWindowsProxy: the manual proxy or a PAC
	/// script, read again whenever they change). The runtime for Android reads only http_proxy, https_proxy and all_proxy,
	/// so a proxy set for the Wi-Fi network, a global one or a VPN app's was never used: mods' requests (RimTalk's model
	/// servers, HugsLib's log upload) went straight out where on the PC they go through Clash or v2rayN in "system proxy"
	/// mode. Each request now asks Android which proxy it gives that address (MainActivity.proxyForUtf8: Java's
	/// ProxySelector, which the system sets up for the app and again on every change, a PAC script included). As on the
	/// PC, http_proxy, https_proxy or all_proxy in runtime.env come first: the runtime's own handling of them stays.
	/// </summary>
	public sealed class SystemProxy : IWebProxy
	{
		[DllImport("glfw")] private static extern int glfwAndroidProxyFor([MarshalAs(UnmanagedType.LPUTF8Str)] string url, byte[] proxy, int size);

		private static readonly string[] PlayerVariables = { "http_proxy", "HTTP_PROXY", "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY" };
		private static readonly HashSet<string> said = new HashSet<string>();

		public ICredentials Credentials { get; set; }

		public static void Start()
		{
			try
			{
				foreach (string name in PlayerVariables)
				{
					if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
					{
						Console.WriteLine("[port17] proxy: " + name + " is set, which the runtime uses, as on the PC; the phone's proxy is not asked");
						return;
					}
				}
				// A libglfw.so older than this PortHooks.dll cannot ask (push-dll.sh puts new DLLs beside an older APK).
				if (!NativeLibrary.TryLoad("libglfw.so", out IntPtr glfw) || !NativeLibrary.TryGetExport(glfw, "glfwAndroidProxyFor", out _))
				{
					Console.WriteLine("[port17] proxy: this APK cannot ask the phone for its proxy; web requests go direct");
					return;
				}
				HttpClient.DefaultProxy = new SystemProxy();
				Console.WriteLine("[port17] proxy: web requests use the phone's proxy when it has one (Wi-Fi, global, a VPN app's)");
			}
			catch (Exception e)
			{
				Console.WriteLine("[port17] proxy: the phone's proxy is not used: " + e.Message);
			}
		}

		public Uri GetProxy(Uri destination)
		{
			// The phone's own addresses go direct, as the PC's proxy tools put them in the bypass list (<local>, 127.*): a
			// proxy on another machine would reach that machine instead. A server on the phone (a local model) is reached
			// as before.
			if (destination == null || !destination.IsAbsoluteUri || destination.IsLoopback)
			{
				return null;
			}
			// A WebSocket's ws and wss are asked as http and https, as the PC's runtime asks Windows (WinInetProxyHelper):
			// Android's selector has an entry for neither and answers "direct", so SteamKit's and mods' WebSockets went
			// around the proxy.
			string scheme = destination.Scheme == "wss" ? "https" : destination.Scheme == "ws" ? "http" : destination.Scheme;
			// Only the scheme, host and port: the path and query (an API key in a URL) are no business of a PAC script.
			string host = destination.IdnHost;
			string address = scheme + "://" + (host.Contains(':') ? "[" + host + "]" : host) + ":" + destination.Port + "/";
			var answer = new byte[512];
			int length = glfwAndroidProxyFor(address, answer, answer.Length);
			if (length <= 0 || !Uri.TryCreate(Encoding.UTF8.GetString(answer, 0, length), UriKind.Absolute, out Uri proxy))
			{
				return null;
			}
			lock (said)
			{
				if (said.Add(proxy.OriginalString))
				{
					Console.WriteLine("[port17] proxy: web requests go through the phone's proxy " + proxy.OriginalString);
				}
			}
			return proxy;
		}

		// As the PC's: what goes direct, GetProxy says.
		public bool IsBypassed(Uri host)
		{
			return false;
		}
	}
}
