using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace PortWorkshop
{
	internal enum SessionState
	{
		Off,
		Connecting,
		SignedOut,       // connected, nobody signed in: the QR code and the password form are offered
		Authenticating,  // a password is being checked
		NeedCode,        // Steam Guard wants a code (CodePrompt says which)
		WaitingApproval, // Steam Guard waits for the sign-in to be approved in the Steam app
		LoggingOn,
		Online,
	}

	/// <summary>
	/// The connection to Steam (SteamKit2), on a thread of its own that runs SteamKit2's callbacks. Signing in is done
	/// with Steam's own authentication: a QR code that the Steam app approves, or the account name and password, which
	/// are used for this one sign-in and not kept. What is kept (in the app's private files, which no other app can
	/// read) is the refresh token Steam hands out for a remembered sign-in, so that later sessions sign in by themselves;
	/// signing out deletes it.
	/// </summary>
	internal static class Session
	{
		public const uint App = 294100;

		private static SteamClient client;
		private static CallbackManager manager;
		private static SteamUser user;
		private static SteamApps apps;
		private static SteamContent content;
		private static PublishedFile publishedFiles;
		private static Client cdn;
		private static Thread pump;
		private static volatile bool wanted, connected;
		// The update check's use of the connection (Updates, at the main menu, with or without the window): it signs in
		// with the remembered token or not at all (no QR code; a token Steam does not take stays, for the window to try),
		// and gives the connection up when it is done (EndCheck), unless the window uses it.
		private static volatile bool checking;
		// Why the update check's sign-in did not come, for its line in the log.
		private static volatile string refusal;
		// Set when this side ends the connection (Stop, EndCheck): a window opened again before the end has come finds
		// the connection still up and makes no new one, so it is made once the end has come (OnDisconnected).
		private static volatile bool endAsked;
		// The window (on the game's thread) and the update check (on a worker) may both be the first to connect.
		private static readonly object starting = new object();
		private static CancellationTokenSource authCancel;
		private static TaskCompletionSource<string> codeSource;
		private static uint cellId;
		private static byte[] depotKey;
		private static List<Server> servers;

		public static volatile SessionState State = SessionState.Off;
		public static volatile string Message = "";
		public static volatile string Account = "";
		public static volatile string CodePrompt = "";
		public static volatile string QrUrl = "";

		internal static string PrivateDir
		{
			get
			{
				string dir;
				if (OperatingSystem.IsIOS())
				{
					// UIFileSharingEnabled shows Documents in Files/Finder, so a renewable Steam sign-in must not be
					// kept there. Library/Application Support is private to the app (under LiveContainer's relocated
					// HOME as well); 0.4.38's Documents/steam copy is moved here once.
					dir = Path.Combine(IosHome(), "Library", "Application Support", "Port17", "steam");
					MoveLegacyPrivateDir(dir);
				}
				else
				{
					dir = Path.Combine("/data/data", File.ReadAllText("/proc/self/cmdline").Split('\0')[0].Trim(), "files", "steam");
				}
				Directory.CreateDirectory(dir);
				return dir;
			}
		}

		internal static string IosHome()
		{
			// The game folder is <HOME>/Documents/game, possibly one wrapper deep, or <HOME>/Documents/<name>.
			// Walking up to Documents and taking its parent keeps the private folder in the app's home even when
			// HOME is not set to it (LiveContainer relocates the whole container).
			string dir = Path.GetFullPath(AppContext.BaseDirectory);
			for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
			{
				if (string.Equals(Path.GetFileName(dir), "Documents", StringComparison.OrdinalIgnoreCase))
				{
					return Path.GetDirectoryName(dir);
				}
				dir = Path.GetDirectoryName(dir);
			}
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (string.IsNullOrEmpty(home) || home == "/")
			{
				home = Environment.GetEnvironmentVariable("HOME");
			}
			return !string.IsNullOrEmpty(home) && home != "/"
				? home
				: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
		}

		private static int legacyMoved;

		/// <summary>
		/// 0.4.38 kept the remembered sign-in in Documents/steam, which the Files app shows. A login file found there
		/// is moved to the private folder once, so the upgrade does not sign the player out and the visible copy does
		/// not stay behind. Only the private folder is used from then on; a move that fails is logged and left alone.
		/// </summary>
		private static void MoveLegacyPrivateDir(string dir)
		{
			if (Interlocked.CompareExchange(ref legacyMoved, 1, 0) != 0)
			{
				return;
			}
			try
			{
				string legacy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "steam"));
				if (string.Equals(legacy, dir, StringComparison.Ordinal) || !File.Exists(Path.Combine(legacy, "login")))
				{
					return;
				}
				if (File.Exists(Path.Combine(dir, "login")))
				{
					// A sign-in is already kept privately; the visible old copy is not left behind.
					try { File.Delete(Path.Combine(legacy, "login")); } catch { }
					try { Directory.Delete(legacy, false); } catch { }
					Log("the old sign-in copy in Documents/steam was removed");
					return;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(dir));
				if (!Directory.Exists(dir))
				{
					Directory.Move(legacy, dir);
				}
				else
				{
					foreach (string entry in Directory.GetFileSystemEntries(legacy))
					{
						string to = Path.Combine(dir, Path.GetFileName(entry));
						if (!File.Exists(to) && !Directory.Exists(to))
						{
							Directory.Move(entry, to);
						}
					}
					try { Directory.Delete(legacy, false); } catch { }
				}
				Log("the remembered sign-in was moved from Documents/steam to Library/Application Support/Port17/steam");
			}
			catch (Exception e)
			{
				Log("the remembered sign-in could not be moved out of Documents: " + e.Message);
			}
		}

		private static string LoginFile => Path.Combine(PrivateDir, "login");

		private static void Log(string text)
		{
			WorkshopLog.Write(text);
		}

		/// <summary>An identity for this installation, made once: what Steam sees as this machine.</summary>
		private sealed class Machine : IMachineInfoProvider
		{
			private readonly byte[] guid;

			public Machine()
			{
				string file = Path.Combine(PrivateDir, "machine");
				if (File.Exists(file) && new FileInfo(file).Length == 16)
				{
					guid = File.ReadAllBytes(file);
				}
				else
				{
					guid = RandomNumberGenerator.GetBytes(16);
					File.WriteAllBytes(file, guid);
				}
			}

			public uint LoginId => BitConverter.ToUInt32(guid, 0);
			public byte[] GetMachineGuid() => guid;
			public byte[] GetMacAddress() => guid.Take(6).ToArray();
			public byte[] GetDiskId() => guid.Reverse().ToArray();
		}

		private static Machine machine;

		// SteamKit2 knows Android as nothing at all (EOSType.Unknown); some of Steam's servers then refuse the sign-in with
		// TryAnotherCM. The phone's kernel is a Linux 6.
		private const EOSType OsType = EOSType.Linux6x;

		// A sign-in Steam asked to try on another of its servers: the remembered token is good, the server is not.
		private static volatile bool logOnElsewhere;
		private static int logOnAttempts;

		/// <summary>Connects (and signs in with the remembered token, if there is one). Idempotent.</summary>
		public static void Start()
		{
			wanted = true;
			Log("window opened; a sign-in is remembered: " + (TryReadLogin(out _, out _) ? "yes" : "no"));
			Connect();
			// Connected by the update check, which found no sign-in to use and asks for none: the window offers one.
			if (connected && State == SessionState.SignedOut && authCancel == null)
			{
				StartQr();
			}
		}

		private static void Connect()
		{
			lock (starting)
			{
				if (client == null)
				{
					if (File.Exists(Path.Combine(AppContext.BaseDirectory, "workshop-debug.txt")))
					{
						DebugLog.AddListener((category, message) => Console.WriteLine("[steamkit] " + category + ": " + message));
						DebugLog.Enabled = true;
					}
					machine = new Machine();
					SteamConfiguration configuration = SteamConfiguration.Create(b => b.WithMachineInfoProvider(machine).WithProtocolTypes(ProtocolTypes.WebSocket | ProtocolTypes.Tcp).WithServerListProvider(new SteamKit2.Discovery.FileStorageServerListProvider(Path.Combine(PrivateDir, "servers"))));
					client = new SteamClient(configuration);
					manager = new CallbackManager(client);
					user = client.GetHandler<SteamUser>();
					apps = client.GetHandler<SteamApps>();
					content = client.GetHandler<SteamContent>();
					publishedFiles = client.GetHandler<SteamUnifiedMessages>().CreateService<PublishedFile>();
					cdn = new Client(client);
					manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
					manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
					manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
					manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
					pump = new Thread(() =>
					{
						while (true)
						{
							try
							{
								manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));
							}
							catch (Exception e)
							{
								Log("callback failed: " + e);
							}
						}
					}) { IsBackground = true, Name = "SteamKit2" };
					pump.Start();
				}
				if (!connected && State != SessionState.Connecting)
				{
					State = SessionState.Connecting;
					Message = "";
					client.Connect();
				}
			}
		}

		/// <summary>Disconnects (the remembered sign-in stays), unless the update check still uses the connection.</summary>
		public static void Stop()
		{
			wanted = false;
			CancelAuth();
			if (client != null && connected && !checking)
			{
				Disconnect();
			}
		}

		private static void Disconnect()
		{
			endAsked = true;
			client.Disconnect();
		}

		private static void OnConnected(SteamClient.ConnectedCallback callback)
		{
			connected = true;
			Message = "";
			Log("connected");
			if (!wanted && !checking)
			{
				// The window was closed while the connection was being made (the first one from China takes about 45 s), or
				// the update check stopped waiting for it: nobody needs it now, and signed in it would stay for the run.
				Disconnect();
				return;
			}
			if (TryReadLogin(out string account, out string token))
			{
				Log("signing in with the remembered sign-in");
				LogOn(account, token);
			}
			else
			{
				State = SessionState.SignedOut;
				// Without the window (the update check alone) no sign-in is asked for.
				if (wanted)
				{
					StartQr();
				}
			}
		}

		private static void OnDisconnected(SteamClient.DisconnectedCallback callback)
		{
			connected = false;
			bool asked = endAsked;
			endAsked = false;
			CancelAuth();
			Log("disconnected" + (callback.UserInitiated ? " (asked for)" : ""));
			if ((wanted || checking) && logOnElsewhere)
			{
				logOnElsewhere = false;
				State = SessionState.Connecting;
				Task.Delay(1000).ContinueWith(_ => ConnectAgain());
			}
			else if (wanted && !callback.UserInitiated)
			{
				State = SessionState.Connecting;
				Message = Texts.Say("和 Steam 的连接断了，正在重连……", "The connection to Steam was lost, reconnecting...");
				Task.Delay(3000).ContinueWith(_ => ConnectAgain());
			}
			else if (wanted && asked)
			{
				// Ended for a window that was closed (or by the update check, done), and wanted by one opened before the end
				// came: without this the new window stayed at "connecting" for good.
				State = SessionState.Connecting;
				Message = "";
				client.Connect();
			}
			else
			{
				State = SessionState.Off;
			}
		}

		/// <summary>
		/// The end of the wait before connecting again (to another server after TryAnotherCM, or after a lost connection):
		/// connects if the window or the update check still wants the connection, else the session goes off. It was at
		/// Connecting with no attempt under way, and Connect makes none while it is at Connecting: a window closed during
		/// the wait and opened later stayed at "connecting" for good, and an update check waited out its patience for
		/// nothing.
		/// </summary>
		private static void ConnectAgain()
		{
			// Under Connect's lock: a window or a check that starts meanwhile has either said it wants the connection before
			// it is looked at here, or finds the session off and connects itself.
			lock (starting)
			{
				if (connected)
				{
					return;
				}
				if (wanted || checking)
				{
					client.Connect();
				}
				else
				{
					State = SessionState.Off;
				}
			}
		}

		private static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
		{
			if (callback.Result == EResult.OK)
			{
				logOnAttempts = 0;
				cellId = callback.CellID;
				State = SessionState.Online;
				Message = "";
				// The account's number, not its name: the name is half of what signs in, and this log goes out with crash
				// reports and the logs players export (the update check signs in once a day, without the window).
				Log("signed in (account " + client.SteamID.AccountID + ", cell " + cellId + ")");
				return;
			}
			Log("sign-in refused: " + callback.Result + " / " + callback.ExtendedResult);
			bool transient = callback.Result == EResult.TryAnotherCM || callback.Result == EResult.ServiceUnavailable || callback.Result == EResult.Busy || callback.Result == EResult.Timeout || callback.Result == EResult.Fail;
			if (transient && logOnAttempts < 6 && TryReadLogin(out _, out _))
			{
				logOnAttempts++;
				Message = Texts.Say("这台 Steam 服务器没接受，换一台再试……", "That Steam server did not take it, trying another...");
				MarkCurrentServerBad();
				logOnElsewhere = true;
				client.Disconnect();
				return;
			}
			refusal = "Steam did not take the remembered sign-in (" + callback.Result + ")";
			if (!wanted)
			{
				// The update check alone: nothing is shown, and the token stays. The window tries it again when the player
				// opens it, with its own tries on other servers, and drops it then if Steam still does not take it.
				logOnAttempts = 0;
				Account = "";
				State = SessionState.SignedOut;
				return;
			}
			// A remembered token that no longer works (expired, revoked, the password changed): it goes.
			DeleteLogin();
			Account = "";
			State = SessionState.SignedOut;
			Message = Texts.Say("Steam 没有接受登录（" + callback.Result + "），请重新登录。", "Steam did not accept the sign-in (" + callback.Result + "); please sign in again.");
			StartQr();
		}

		/// <summary>The server of this connection goes to the back of the list (SteamKit2 keeps the marking to itself).</summary>
		private static void MarkCurrentServerBad()
		{
			try
			{
				var endPoint = client.CurrentEndPoint;
				var mark = typeof(SteamKit2.Discovery.SmartCMServerList).GetMethod("TryMark", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
				if (endPoint != null && mark != null)
				{
					mark.Invoke(client.Servers, new object[] { endPoint, ProtocolTypes.WebSocket | ProtocolTypes.Tcp, SteamKit2.Discovery.ServerQuality.Bad });
					Log("server " + endPoint + " marked bad");
				}
			}
			catch (Exception e)
			{
				Log("could not mark the server: " + e.Message);
			}
		}

		private static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
		{
			Log("signed off by Steam: " + callback.Result);
			refusal = "signed off by Steam (" + callback.Result + ")";
			State = SessionState.SignedOut;
			Message = Texts.Say("Steam 让这次登录下线了（" + callback.Result + "）。", "Steam signed this session off (" + callback.Result + ").");
		}

		private static void LogOn(string account, string token)
		{
			Account = account;
			State = SessionState.LoggingOn;
			user.LogOn(new SteamUser.LogOnDetails
			{
				Username = account,
				AccessToken = token,
				ShouldRememberPassword = true,
				LoginID = machine.LoginId,
				ClientOSType = OsType,
			});
		}

		// ---- signing in ----

		private static AuthSessionDetails Details(string account = null, string password = null)
		{
			return new AuthSessionDetails
			{
				Username = account,
				Password = password,
				IsPersistentSession = true,
				DeviceFriendlyName = OperatingSystem.IsIOS() ? "RimWorld (iOS)" : "RimWorld (Android)",
				ClientOSType = OsType,
				// A QR code is approved by scanning it; Steam Guard only takes part in a password sign-in.
				Authenticator = account != null ? new Guard() : null,
			};
		}

		private static void CancelAuth()
		{
			authCancel?.Cancel();
			authCancel = null;
			codeSource?.TrySetCanceled();
			codeSource = null;
		}

		/// <summary>A QR code for the Steam app to approve; it renews itself until it is used.</summary>
		private static void StartQr()
		{
			CancelAuth();
			if (!connected)
			{
				return;
			}
			var cancel = authCancel = new CancellationTokenSource();
			Task.Run(async () =>
			{
				try
				{
					Log("asking for a QR code");
					QrAuthSession session = await client.Authentication.BeginAuthSessionViaQRAsync(Details());
					QrUrl = session.ChallengeURL;
					Log("QR code ready");
					session.ChallengeURLChanged = () => QrUrl = session.ChallengeURL;
					AuthPollResult result = await session.PollingWaitForResultAsync(cancel.Token);
					Signed(result);
				}
				catch (Exception e) when (cancel.IsCancellationRequested || e is OperationCanceledException)
				{
				}
				catch (Exception e)
				{
					Log("QR sign-in failed: " + e.Message);
					QrUrl = "";
					if (State == SessionState.SignedOut)
					{
						Message = Texts.Say("二维码登录出错：", "The QR sign-in failed: ") + e.Message;
					}
				}
			});
		}

		/// <summary>The account name and password, typed by the player: used for this sign-in only.</summary>
		public static void SignInWithPassword(string account, string password)
		{
			CancelAuth();
			QrUrl = "";
			var cancel = authCancel = new CancellationTokenSource();
			State = SessionState.Authenticating;
			Message = "";
			Task.Run(async () =>
			{
				try
				{
					CredentialsAuthSession session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(Details(account.Trim(), password));
					AuthPollResult result = await session.PollingWaitForResultAsync(cancel.Token);
					Signed(result);
				}
				catch (Exception e) when (cancel.IsCancellationRequested || e is OperationCanceledException)
				{
				}
				catch (Exception e)
				{
					Log("password sign-in failed: " + e.Message);
					State = SessionState.SignedOut;
					Message = e is AuthenticationException auth && auth.Result == EResult.InvalidPassword
						? Texts.Say("账号或密码不对。", "Wrong account name or password.")
						: Texts.Say("登录出错：", "Signing in failed: ") + e.Message;
					StartQr();
				}
			});
		}

		/// <summary>The code Steam Guard asked for.</summary>
		public static void SubmitCode(string code)
		{
			State = SessionState.Authenticating;
			codeSource?.TrySetResult(code.Trim());
		}

		/// <summary>Back to the QR code and the password form (a sign-in under way is given up).</summary>
		public static void Restart()
		{
			State = SessionState.SignedOut;
			Message = "";
			StartQr();
		}

		private static void Signed(AuthPollResult result)
		{
			Log("signing in with the sign-in just made (QR code or password)");
			WriteLogin(result.AccountName, result.RefreshToken);
			LogOn(result.AccountName, result.RefreshToken);
		}

		public static void SignOut()
		{
			Log("signed out; the remembered sign-in is deleted");
			DeleteLogin();
			Account = "";
			if (connected)
			{
				user.LogOff();
			}
			depotKey = null;
			State = SessionState.SignedOut;
			Message = "";
			if (connected)
			{
				StartQr();
			}
		}

		/// <summary>Steam Guard, through the window.</summary>
		private sealed class Guard : IAuthenticator
		{
			public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
			{
				return Ask(previousCodeWasIncorrect ? Texts.Say("验证码不对，请再输入一次 Steam 手机令牌里的验证码：", "The code was wrong; enter the code from the Steam Mobile Authenticator again:") : Texts.Say("输入 Steam 手机令牌（Steam App 里）的验证码：", "Enter the code from the Steam Mobile Authenticator (in the Steam app):"));
			}

			public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
			{
				return Ask((previousCodeWasIncorrect ? Texts.Say("验证码不对。", "The code was wrong. ") : "") + Texts.Say("输入 Steam 发到 " + email + " 的验证码：", "Enter the code Steam sent to " + email + ":"));
			}

			public Task<bool> AcceptDeviceConfirmationAsync()
			{
				State = SessionState.WaitingApproval;
				return Task.FromResult(true);
			}

			private static Task<string> Ask(string prompt)
			{
				var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
				codeSource = source;
				CodePrompt = prompt;
				State = SessionState.NeedCode;
				return source.Task;
			}
		}

		private static bool TryReadLogin(out string account, out string token)
		{
			account = token = null;
			try
			{
				if (!File.Exists(LoginFile))
				{
					return false;
				}
				string[] lines = File.ReadAllLines(LoginFile);
				if (lines.Length < 2 || lines[0].Length == 0 || lines[1].Length == 0)
				{
					return false;
				}
				account = lines[0];
				token = lines[1];
				return true;
			}
			catch (Exception e)
			{
				Log("the remembered sign-in could not be read: " + e.Message);
				return false;
			}
		}

		private static void WriteLogin(string account, string token)
		{
			try
			{
				File.WriteAllLines(LoginFile, new[] { account, token });
				Log("the sign-in is remembered");
			}
			catch (Exception e)
			{
				Log("the sign-in could not be remembered: " + e.Message);
			}
		}

		private static void DeleteLogin()
		{
			try
			{
				File.Delete(LoginFile);
			}
			catch (Exception)
			{
			}
		}

		// ---- the update check ----

		/// <summary>
		/// The update check's sign-in (Updates): connected if need be and signed in with the remembered token, without a
		/// word on the screen. Null once signed in, else why not, for the log: no remembered sign-in, a token Steam does not
		/// take, no connection, or not signed in within the time given. EndCheck follows, whatever came of it.
		/// </summary>
		public static async Task<string> SignInForCheck(TimeSpan patience)
		{
			checking = true;
			refusal = null;
			Connect();
			var waited = System.Diagnostics.Stopwatch.StartNew();
			while (true)
			{
				SessionState state = State;
				if (state == SessionState.Online && connected)
				{
					return null;
				}
				if (state == SessionState.SignedOut || state == SessionState.Off)
				{
					return refusal ?? (state == SessionState.Off ? "no connection to Steam" : "no remembered sign-in");
				}
				if (waited.Elapsed > patience)
				{
					return "not signed in after " + (int)patience.TotalSeconds + " s";
				}
				await Task.Delay(250);
			}
		}

		/// <summary>The update check is done with the connection: it goes, unless the window uses it.</summary>
		public static void EndCheck()
		{
			checking = false;
			if (!wanted && client != null && connected)
			{
				Disconnect();
			}
		}

		// ---- what the downloads need ----

		public static bool Online => State == SessionState.Online && connected;

		private static void RequireOnline()
		{
			if (!Online)
			{
				throw new InvalidOperationException(Texts.Say("没有登录 Steam。", "Not signed in to Steam."));
			}
		}

		/// <summary>The details of workshop items (with their required items or, for a collection, its items).</summary>
		public static Task<List<PublishedFileDetails>> Details(IReadOnlyList<ulong> ids)
		{
			return Details(ids, true);
		}

		/// <summary>
		/// What the update check compares (Updates): the items' versions, without their required items and with their
		/// descriptions cut short, so that a look at every installed mod costs little data.
		/// </summary>
		public static Task<List<PublishedFileDetails>> Versions(IReadOnlyList<ulong> ids)
		{
			return Details(ids, false);
		}

		private static async Task<List<PublishedFileDetails>> Details(IReadOnlyList<ulong> ids, bool full)
		{
			RequireOnline();
			var all = new List<PublishedFileDetails>();
			for (int at = 0; at < ids.Count; at += 50)
			{
				var request = new CPublishedFile_GetDetails_Request { includechildren = full, short_description = !full, appid = App };
				request.publishedfileids.AddRange(ids.Skip(at).Take(50));
				var response = await publishedFiles.GetDetails(request);
				if (response.Result != EResult.OK)
				{
					throw new Exception(Texts.Say("读取 mod 信息失败：", "Reading the items failed: ") + response.Result);
				}
				all.AddRange(response.Body.publishedfiledetails);
			}
			return all;
		}

		/// <summary>The account's subscriptions for RimWorld.</summary>
		public static async Task<List<PublishedFileDetails>> Subscriptions()
		{
			RequireOnline();
			var all = new List<PublishedFileDetails>();
			for (uint page = 1; page <= 50; page++)
			{
				var request = new CPublishedFile_GetUserFiles_Request
				{
					steamid = client.SteamID.ConvertToUInt64(),
					appid = App,
					type = "mysubscriptions",
					page = page,
					numperpage = 100,
					return_children = true,
				};
				var response = await publishedFiles.GetUserFiles(request);
				if (response.Result != EResult.OK)
				{
					throw new Exception(Texts.Say("读取订阅失败：", "Reading the subscriptions failed: ") + response.Result);
				}
				List<PublishedFileDetails> got = response.Body.publishedfiledetails;
				Log("subscriptions page " + page + ": " + got.Count + " of " + response.Body.total);
				all.AddRange(got);
				if (got.Count < 100 || all.Count >= response.Body.total)
				{
					break;
				}
			}
			return all;
		}

		public static async Task<byte[]> DepotKey()
		{
			RequireOnline();
			if (depotKey == null)
			{
				SteamApps.DepotKeyCallback key = await apps.GetDepotDecryptionKey(App, App);
				if (key.Result != EResult.OK)
				{
					throw new Exception(Texts.Say("这个账号没有下载 RimWorld 内容的权限（" + key.Result + "）。", "This account may not download RimWorld's content (" + key.Result + ")."));
				}
				depotKey = key.DepotKey;
			}
			return depotKey;
		}

		/// <summary>Steam's content servers for this place, the least loaded first.</summary>
		public static async Task<List<Server>> Servers()
		{
			RequireOnline();
			if (servers == null || servers.Count == 0)
			{
				// Through the connection to Steam (the web API behind ContentServerDirectoryService is slow from some places).
				IReadOnlyCollection<Server> found;
				try
				{
					found = await content.GetServersForSteamPipe(cellId);
				}
				catch (Exception e)
				{
					Log("content servers through the connection failed (" + e.Message + "), asking the web API");
					found = await ContentServerDirectoryService.LoadAsync(client.Configuration, cellId, CancellationToken.None);
				}
				List<Server> usable = found.Where(s => (s.Type == "SteamCache" || s.Type == "CDN") && (s.AllowedAppIds == null || s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(App))).OrderBy(s => s.Type == "SteamCache" ? 0 : 1).ThenBy(s => s.WeightedLoad).ToList();
				servers = usable.Count > 0 ? usable : found.ToList();
				Log(servers.Count + " content servers: " + string.Join(", ", servers.Take(4).Select(s => s.Host)));
			}
			return servers;
		}

		private static readonly Dictionary<string, (string token, DateTime until)> cdnTokens = new Dictionary<string, (string, DateTime)>();

		/// <summary>
		/// The token a content server of one of Steam's partners wants with every request (the servers in China are all
		/// partners'; without it they answer 403). Valve's own servers want none. Kept per host until it expires.
		/// </summary>
		public static async Task<string> CdnToken(Server server)
		{
			if (server.Type != "CDN" || string.IsNullOrEmpty(server.Host))
			{
				return null;
			}
			lock (cdnTokens)
			{
				if (cdnTokens.TryGetValue(server.Host, out var known) && known.until > DateTime.UtcNow.AddMinutes(2))
				{
					return known.token;
				}
			}
			RequireOnline();
			SteamContent.CDNAuthToken answer = await content.GetCDNAuthToken(App, App, server.Host);
			if (answer.Result != EResult.OK)
			{
				Log("no token for " + server.Host + ": " + answer.Result);
				return null;
			}
			lock (cdnTokens)
			{
				cdnTokens[server.Host] = (answer.Token, answer.Expiration);
			}
			return answer.Token;
		}

		public static Task<ulong> RequestCode(ulong manifest)
		{
			RequireOnline();
			return content.GetManifestRequestCode(App, App, manifest, "public");
		}

		public static Client Cdn => cdn;
	}
}
