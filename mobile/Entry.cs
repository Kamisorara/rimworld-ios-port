using System;
using HarmonyLib;

namespace PortMobile
{
	/// <summary>
	/// The phone side of the game: loaded by PortHooks when the entry UI initializes (every def is loaded by then),
	/// patches the game with Harmony (the 1.7 build from the Harmony mod, 0Harmony.dll in the game folder).
	/// </summary>
	public static class Entry
	{
		private static int frames;
		private static bool started;

		public static void Start()
		{
			// The hook that calls this sits in UIRoot_Entry.Init, and that runs again every time the game comes back to the main
			// menu. The patches are put in once: put in again, every one of them would run twice from then on.
			if (started)
			{
				MemTrim.AfterReturnToMenu();
				UnreadableMods.Tell();
				UpdateNotice.AtMainMenu();
				return;
			}
			started = true;
			Console.WriteLine("[mobile] starting; Harmony " + typeof(Harmony).Assembly.GetName().Version + ", runtime " + Environment.Version);
			var harmony = new Harmony("port17.mobile");
			// Before any of the port's patches: they are watched like every later one (see InlineGuard).
			InlineGuard.Start(harmony);
			harmony.Patch(AccessTools.Method(typeof(Verse.UIRoot), "UIRootOnGUI"), postfix: new HarmonyMethod(typeof(Entry), nameof(AfterUIRootOnGUI)));
			Console.WriteLine("[mobile] patched UIRoot.UIRootOnGUI");
			// First of the rest: what goes wrong while the others start is in the record too.
			Recorder.Start(harmony);
			try
			{
				string missing = PortHooks.EngineTypes.Verify();
				Console.WriteLine("[mobile] engine types: " + (missing == null ? "all found" : "NOT FOUND: " + missing));
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] engine types not checked: " + e.Message);
			}
			Touch.Start();
			ModImport.Start(harmony);
			ModSort.Start(harmony);
			BundleDiag.Start();
			HarmonyDiag.Start();
			SpawnDiag.Start(harmony);
			Sched.Start(harmony);
			FrameRate.Start(harmony);
			Perf.Start(harmony);
			WorldLabels.Start(harmony);
			if (!Compat.On)
			{
				ResearchTree.Start(harmony);
			}
			Squad.Start(harmony);
			DoubleTap.Start(harmony);
			Counters.Start(harmony);
			MemTrim.Start(harmony);
			BakeDepth.Start(harmony);
			Temporaries.Start(harmony);
			if (!Compat.On)
			{
				Cutout.Start(harmony);
			}
			else
			{
				Console.WriteLine("[mobile] " + Compat.Describe());
			}
			ColdTextures.Start(harmony);
			AtlasSeams.Start(harmony);
			PlainIcons.Start(harmony);
			BindKeys.Start(harmony);
			if (!Compat.On)
			{
				MintMenus.Start(harmony);
				FactionList.Start(harmony);
			}
			WinterShadow.Start(harmony);
			MultiplayerFiles.Start(harmony);
			VideoUrls.Start(harmony);
			OpenUrl.Start(harmony);
			UpdateNotice.Start(harmony);
			Screenshots.Start(harmony);
			AndroidVideo.Start(harmony);
			SaveImport.Start(harmony);
			HugsLogFile.Start(harmony);
			ResolutionChoice.Start(harmony);
			// The atlases are baked by now (once, a second at most; then by cache key).
			AtlasCheck.Start(harmony);
			// Last: the mods' replacements that hold their own copy of a method patched above.
			InlineGuard.EndOfStart();
			UnreadableMods.Tell();
			VersionNotice.Tell();
			UpdateNotice.AtMainMenu();
		}

		private static bool menuTimed;

		/// <summary>
		/// Seconds since the startup hook ran. (The kernel's start time of the process is no use: Android hands an app a
		/// process it forked minutes earlier.)
		/// </summary>
		internal static double SecondsSinceProcessStart()
		{
			return PortHooks.PortPlatform.StartedAtTick > 0 ? (Environment.TickCount64 - PortHooks.PortPlatform.StartedAtTick) / 1000.0 : -1;
		}

		/// <summary>
		/// What the runtime itself can say about the time until now: how long the compiler ran (on every thread together)
		/// and for how many methods, how long the collector held the program, and the processor time of the whole process
		/// (more than the wall time where threads worked side by side).
		/// </summary>
		private static string WhereTheStartWent()
		{
			try
			{
				var inv = System.Globalization.CultureInfo.InvariantCulture;
				// /proc/self/stat: after the name in parentheses, the 12th and 13th fields are the user and system time in 1/100 s.
				string stat = System.IO.File.ReadAllText("/proc/self/stat");
				string[] fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
				TimeSpan cpu = TimeSpan.FromSeconds((long.Parse(fields[11]) + long.Parse(fields[12])) / 100.0);
				return "compiling " + System.Runtime.JitInfo.GetCompilationTime().TotalSeconds.ToString("F1", inv) + " s (" + System.Runtime.JitInfo.GetCompilationTime(true).TotalSeconds.ToString("F1", inv) + " s of it on this thread) for " + System.Runtime.JitInfo.GetCompiledMethodCount() + " methods (" + (System.Runtime.JitInfo.GetCompiledILBytes() / 1048576.0).ToString("F1", inv) + " MB of IL)"
					+ ", collector pauses " + GC.GetTotalPauseDuration().TotalSeconds.ToString("F1", inv) + " s in " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2) + " collections of " + (GC.GetTotalAllocatedBytes() / 1073741824.0).ToString("F1", inv) + " GB allocated"
					+ ", processor time " + cpu.TotalSeconds.ToString("F1", inv) + " s, " + AppDomain.CurrentDomain.GetAssemblies().Length + " assemblies";
			}
			catch (Exception e)
			{
				return e.GetType().Name;
			}
		}

		/// <summary>
		/// mobile.txt avTest=1: List.Add on an object reference that points nowhere, inside a try, the way a 1.6 mod's patch
		/// did it (Shella Backgrounds, in the options window: "Fatal error. System.AccessViolationException ... List`1.Add").
		/// The log says whether the runtime let the catch have it (host.c: legacyCorruptedStateExceptionsPolicy) or the
		/// process ends here.
		/// </summary>
		private static void AccessViolationTest()
		{
			if (Settings.Get("avTest", 0) != 1)
			{
				return;
			}
			try
			{
				IntPtr nowhere = new IntPtr(0x5A5A5A5A0000);
				System.Collections.Generic.List<object> list = System.Runtime.CompilerServices.Unsafe.As<IntPtr, System.Collections.Generic.List<object>>(ref nowhere);
				list.Add(null);
				Console.WriteLine("[mobile] access violation test: no fault");
			}
			catch (Exception e)
			{
				Console.WriteLine("[mobile] access violation test: caught " + e.GetType().FullName);
			}
		}

		/// <summary>
		/// mobile.txt noReturnTest=1: the crash of a tester's HONOR (2026-11-03, a SIGTRAP in compiled code, twice). A method
		/// compiled with optimisation calls one whose code only throws; the JIT takes that one for a call that never returns
		/// and puts a breakpoint after it. A Harmony prefix then makes it return, and the breakpoint ends the process. For
		/// the crash report: the runs after such a crash list the compiled methods (host.c), and the report of the next
		/// one names the method (here Entry.CallsFail).
		/// </summary>
		private static void NoReturnTest()
		{
			if (Settings.Get("noReturnTest", 0) != 1)
			{
				return;
			}
			Console.WriteLine("[mobile] no-return test: " + CallsFail("", new[] { 1, 2 }, 1));
			new Harmony("port17.test.noreturn").Patch(AccessTools.Method(typeof(Entry), nameof(Fail)), prefix: new HarmonyMethod(typeof(Entry), nameof(SkipFail)));
			Console.WriteLine("[mobile] no-return test: Fail returns now; the next call ends the process at the breakpoint after it");
			Console.Out.Flush();
			Console.WriteLine("[mobile] no-return test: " + CallsFail("x", new[] { 1, 2 }, 1) + ", no breakpoint (the JIT did not take Fail for a call that never returns)");
		}

		private static void Fail(string why)
		{
			throw new InvalidOperationException(why);
		}

		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
		private static int CallsFail(string why, int[] values, int index)
		{
			if (why.Length > 0)
			{
				Fail(why);
			}
			return values[index];
		}

		private static bool SkipFail()
		{
			return false;
		}

		/// <summary>
		/// mobile.txt stackTest=1: on a thread started without a size, as LongEventHandler starts the loading thread, a
		/// recursion 4 MB deep (1 KB a frame); the log says how deep it went. Bionic's 1 MB ended the process here before
		/// host.c gave such threads 16 MB (DOTNET_Thread_DefaultStackSize). stackTest=2 recurses until the stack runs out,
		/// for the crash report (exit.txt "its stack", stderr-1.txt's "Stack overflow" lines in summary.txt).
		/// </summary>
		private static void StackTest()
		{
			int mode = Settings.Get("stackTest", 0);
			if (mode != 1 && mode != 2)
			{
				return;
			}
			int depth = 0;
			var thread = new System.Threading.Thread(() => depth = Recurse(mode == 1 ? 4096 : int.MaxValue));
			thread.Start();
			thread.Join();
			Console.WriteLine("[mobile] stack test: a new thread went " + depth + " KB deep");
		}

		private static unsafe int Recurse(int left)
		{
			byte* frame = stackalloc byte[1000];
			frame[0] = (byte)left;
			return left <= 1 ? frame[0] & 1 : 1 + Recurse(left - 1) + (frame[999] & 0);
		}

		/// <summary>
		/// mobile.txt corelibTest=1: the WaylithClr switches the phone's CoreLib has as Ludeon's (patch/Patch.cs
		/// AddLudeonSwitches) and MonoMod's instruction cache flush, each against what the PC gives. One line each in the
		/// log, "ok" or what came instead.
		/// </summary>
		private static void CoreLibTest()
		{
			if (Settings.Get("corelibTest", 0) != 1)
			{
				return;
			}
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			void Line(string what, Func<string> got, string want)
			{
				string result;
				try
				{
					result = got();
				}
				catch (Exception e)
				{
					result = e.GetType().Name + ": " + e.Message;
				}
				Console.WriteLine("[mobile] corelib test: " + what + " -> " + result + (result == want ? " ok" : " (the PC: " + want + ")"));
			}
			float f = (float)Math.PI;
			double d = 0.1;
			d += 0.2;
			Line("((float)Math.PI).ToString()", () => f.ToString(inv), "3.141593");
			Line("Math.PI.ToString()", () => Math.PI.ToString(inv), "3.14159265358979");
			Line("(0.1 + 0.2).ToString()", () => d.ToString(inv), "0.3");
			Line("(0.1 + 0.2).ToString(\"G\")", () => d.ToString("G", inv), "0.3");
			Line("interpolated 0.1 + 0.2", () => FormattableString.Invariant($"{d}"), "0.3");
			Line("(0.1 + 0.2).ToString(\"R\")", () => d.ToString("R", inv), "0.30000000000000004");
			Line("DynamicMethod public static hidebysig", () =>
			{
				var method = new System.Reflection.Emit.DynamicMethod("probe", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static | System.Reflection.MethodAttributes.HideBySig, System.Reflection.CallingConventions.Standard, typeof(int), Type.EmptyTypes, typeof(Entry), true);
				System.Reflection.Emit.ILGenerator il = method.GetILGenerator();
				il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4, 42);
				il.Emit(System.Reflection.Emit.OpCodes.Ret);
				return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))().ToString(inv);
			}, "42");
			Line("DynamicMethod.DeclaringType", () => new System.Reflection.Emit.DynamicMethod("owner", typeof(void), Type.EmptyTypes, typeof(Entry)).DeclaringType?.FullName ?? "null", typeof(Entry).FullName);
			Line("Assembly.Load(bytes), then the type by name", () =>
			{
				var builder = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName("WaylithIsolatedProbe"), typeof(object).Assembly);
				builder.DefineDynamicModule("WaylithIsolatedProbe").DefineType("WaylithIsolatedProbe.Probe", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Class).CreateType();
				var bytes = new System.IO.MemoryStream();
				builder.Save(bytes);
				System.Reflection.Assembly loaded = System.Reflection.Assembly.Load(bytes.ToArray());
				Type found = Type.GetType("WaylithIsolatedProbe.Probe, WaylithIsolatedProbe");
				return found == null ? "not found" : found.Assembly == loaded ? "found" : "another assembly";
			}, "found");
			// LaxPrivateFieldNames: Mono's names of the framework's private fields (Humanoid Alien Races' XmlElement.name).
			const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
			Line("XmlElement.GetField(\"name\")", () => typeof(System.Xml.XmlElement).GetField("name", instance)?.Name ?? "null", "_name");
			Line("DynamicMethod.GetField(\"m_returnType\")", () => typeof(System.Reflection.Emit.DynamicMethod).GetField("m_returnType", instance)?.Name ?? "null", "_returnType");
			Line("SignatureHelper.GetField(\"_module\")", () => typeof(System.Reflection.Emit.SignatureHelper).GetField("_module", instance)?.Name ?? "null", "null");
			// AllowInitOnlyStaticWrites: static readonly fields of the framework written back with their own values, where
			// CoreLib checks (a write after a read) and where libcoreclr.so does (a field's first write; a struct's).
			Line("DBNull.Value written back after a read", () =>
			{
				System.Reflection.FieldInfo field = typeof(DBNull).GetField("Value");
				field.SetValue(null, field.GetValue(null));
				return "written";
			}, "written");
			Line("Missing.Value written back", () =>
			{
				object value = System.Reflection.Missing.Value;
				typeof(System.Reflection.Missing).GetField("Value").SetValue(null, value);
				return "written";
			}, "written");
			Line("TimeSpan.Zero written back", () =>
			{
				typeof(TimeSpan).GetField("Zero").SetValue(null, TimeSpan.Zero);
				return "written";
			}, "written");
			// MonoMod flushes the instruction cache after it writes code, as on Windows: the game folder's 0Harmony.dll calls
			// mm_clear_cache of the APK's libmonomodc.so (patch/Patch.cs FlushAfterPatchData). PatchData goes on without a
			// word when either half is older, so this line is where that shows.
			Line("MonoMod's instruction cache flush (mm_clear_cache)", () =>
			{
				System.Reflection.MethodInfo clearCache = typeof(Harmony).Assembly.GetType("MonoMod.Core.Interop.Unix")?.GetMethod("ClearCache");
				if (clearCache == null)
				{
					return "not in 0Harmony.dll";
				}
				// Over compiled code, as PatchData does after a detour's jump.
				IntPtr code = new Action(CoreLibTest).Method.MethodHandle.GetFunctionPointer();
				clearCache.Invoke(null, System.Reflection.BindingFlags.DoNotWrapExceptions, null, new object[] { code, (UIntPtr)16 }, null);
				return "flushed";
			}, "flushed");
		}

		/// <summary>
		/// mobile.txt parityTest=1, with the test mod "port17.paritytest" active (its assemblies built against an older
		/// library and an optional one that is not there): what the PC's runtime does for each, measured there (moved
		/// field, missing optional assembly, newer library reference).
		/// </summary>
		private static void ParityTest()
		{
			if (Settings.Get("parityTest", 0) != 1)
			{
				return;
			}
			foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				string name = assembly.GetName().Name;
				Type tests = name == "Port17ParityMod" ? assembly.GetType("ParityMod.Tests") : name == "Port17ParityHigh" ? assembly.GetType("ParityHigh.Tests") : null;
				if (tests == null)
				{
					continue;
				}
				foreach (System.Reflection.MethodInfo method in tests.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
				{
					string result;
					try
					{
						result = (string)method.Invoke(null, null);
					}
					catch (System.Reflection.TargetInvocationException e)
					{
						result = "escaped: " + e.InnerException.GetType().Name + ": " + e.InnerException.Message;
					}
					Console.WriteLine("[mobile] parity test: " + method.Name + ": " + result);
				}
			}
			bool listed = false;
			foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				listed |= assembly.GetName().Name == "Port17ParityOptional";
			}
			Console.WriteLine("[mobile] parity test: the missing assembly " + (listed ? "IS listed by GetAssemblies" : "is not listed by GetAssemblies"));
		}

		private static bool clipboardProbeSeen;

		[System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
		[System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseClipboard();
		[System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

		/// <summary>
		/// mobile.txt clipboardTest=1: at the main menu the game copies a test text (paste it in another app to see it got
		/// to the phone's clipboard); afterwards, text "port17probe" copied in another app is noticed once (OnFrame). What
		/// else is on the phone's clipboard is never written down.
		/// </summary>
		private static void ClipboardTest(bool menu)
		{
			if (Settings.Get("clipboardTest", 0) != 1 || clipboardProbeSeen)
			{
				return;
			}
			if (menu)
			{
				// Character Editor's way (hooks/WindowsLibraries.cs, host/stubs/user32.c).
				string viaWindows = "Port17 user32 测试 ✓";
				try
				{
					OpenClipboard(IntPtr.Zero);
					SetClipboardData(13, System.Runtime.InteropServices.Marshal.StringToHGlobalUni(viaWindows));
					CloseClipboard();
					Console.WriteLine("[mobile] clipboard test: user32 SetClipboardData " + (UnityEngine.GUIUtility.systemCopyBuffer == viaWindows ? "ok" : "did not arrive"));
				}
				catch (Exception e)
				{
					Console.WriteLine("[mobile] clipboard test: user32 " + e.GetType().Name + ": " + e.Message);
				}
				UnityEngine.GUIUtility.systemCopyBuffer = "Port17 剪贴板测试 ✓ 🚀";
				Console.WriteLine("[mobile] clipboard test: copied the test text; now copy \"port17probe\" in another app and come back");
				return;
			}
			if (frames % 120 == 0 && UnityEngine.GUIUtility.systemCopyBuffer == "port17probe")
			{
				clipboardProbeSeen = true;
				Console.WriteLine("[mobile] clipboard test: the text copied in another app arrived");
			}
		}

		public static void AfterUIRootOnGUI()
		{
			PortHooks.Watch.Beat();
			// The mouse buttons first: the wheel lays its keys round them.
			MouseButtons.OnGui();
			KeyWheel.OnGui();
			TypingBar.OnGui();
			if (!menuTimed && Verse.Current.ProgramState == Verse.ProgramState.Entry && !Verse.LongEventHandler.AnyEventNowOrWaiting)
			{
				// Once: how long the start took, for comparing builds and settings.
				menuTimed = true;
				Console.WriteLine("[mobile] main menu " + SecondsSinceProcessStart().ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s after the runtime started");
				ConflictTag.SelfTest();
				PortHooks.Watch.MainMenu();
				Console.WriteLine("[mobile] of that: " + WhereTheStartWent());
				Temporaries.Test();
				AccessViolationTest();
				StackTest();
				NoReturnTest();
				CoreLibTest();
				AndroidVideo.Test();
				ParityTest();
				KtxTest.Run();
				PortHooks.MissingAssemblies.Test();
				ContentCheck.Run();
				ClipboardTest(true);
				// mobile.txt stallTest=1: the main thread busy for 15 s, for the watch's stall lines (logs/watch.txt).
				if (Settings.Get("stallTest", 0) == 1)
				{
					var busy = System.Diagnostics.Stopwatch.StartNew();
					while (busy.ElapsedMilliseconds < 15000)
					{
					}
					Console.WriteLine("[mobile] stall test: the main thread was busy for 15 s");
				}
				MemTrim.AtMainMenu();
				PortHooks.JitProfile.AtMainMenu();
				PortHooks.InlineWatch.Report("main menu", Harmony.GetAllPatchedMethods());
			}
			frames++;
			if (menuTimed)
			{
				ClipboardTest(false);
			}
			MemTrim.OnFrame();
			ColdTextures.OnFrame();
			UpdateNotice.OnFrame();
			if (frames == 1 || frames == 600)
			{
				Console.WriteLine("[mobile] UIRootOnGUI postfix ran (" + frames + ")");
			}
		}
	}
}
