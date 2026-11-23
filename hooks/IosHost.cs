using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace PortHooks
{
	/// <summary>
	/// iOS 宿主桥（窗口后端反转 v2）：把宿主（启动器/shim）的事件喂给引擎自带的 AndroidPlatform。
	/// 5014 引擎的 Android 后端（Waylith.Windowing.AndroidPlatform + AndroidHost）是 Ludeon 官方
	/// Android 运行时的宿主驱动形态：无 RunCore、无主线程要求，帧与输入全部由宿主经 AndroidHost
	/// 的事件队列喂（安卓 patch/Patch.cs 顶掉 WaylithActivity 的同一条契约，iOS 版）。
	///
	/// 选路：宿主 setenv WAYLITH_BACKEND=Android（WindowingPlatform.ResolveCandidates 优先读）；
	/// setup v10 补丁开 AndroidBackend.Open 的 IsAndroid 门并换 Vulkan 扩展/surface 类型；
	/// AndroidHost 的两道门（IsInitialized/IsApplicationThread）由这里反射直写字段点亮
	/// （s_javaVm=1、s_applicationThread=本线程——StartupHook 与引擎 Main 同线程）。
	///
	/// 全部走 MethodInfo.Invoke：Native* 是 [UnmanagedCallersOnly]（JNI 回调），CoreCLR 禁止
	/// 对它们 CreateDelegate。JNI 相关的两个入口绕开：Configuration 反射直写
	/// s_currentConfiguration（NativeConfiguration 体里 new Jni(env) 会对 null env 炸）；
	/// surface 经 Post(Action&lt;AndroidPlatform&gt;) 队列投 OnSurfaceChanged（NativeSurfaceChanged
	/// 体里的 ANativeWindow_fromSurface 指针为空）。
	///
	/// AndroidHost.ReleaseNativeWindow / SetNativeWindowFrameRate 两个函数指针在真 Initialize
	/// 里从 libandroid.so 解析——iOS 没有：反射写入 no-op 桩（GetFunctionPointerForDelegate +
	/// 委托保活；本地 net8 实测 fnptr 字段收 boxed IntPtr 可写可调）。
	///
	/// 事件流：shim（glfw_ios.m）的 sink 回调（touch/key/char/app/resize，主线程）→ Native*
	/// Invoke（内部只 Post 进队列）→ 引擎线程 PumpEvents→Drain 消费。帧：60Hz 心跳线程 →
	/// NativeFrame（ComputeTimeout 的 WaitForEvents park 唯一唤醒源）。初始实况从宿主 env 读
	/// （PORT17_METAL_LAYER/PORT17_SCREEN_SCALE/PORT17_SCREEN_SIZE，启动器点启动时落）。
	/// </summary>
	internal static class IosHost
	{
		// ---- shim 的事件结构（glfw_ios.m 的 port17_ios_event，字段顺序与自然对齐定死）----
		[StructLayout(LayoutKind.Sequential)]
		private struct PortIosEvent
		{
			public int Kind;      // 1 touch / 2 key / 3 char / 4 app / 5 resize
			public long I0, I1, I2;
			public float F0, F1;
			public long TimeMs;
		}

		// ---- 引擎侧的枚举常数（decomp 对齐）----
		private const int PointerDown = 0, PointerUp = 1, PointerMove = 2, PointerCancel = 3;
		private const int ToolFinger = 1;
		private const int LifecycleStart = 0, LifecycleResume = 1, LifecyclePause = 2, LifecycleStop = 3;

		// GLFW modifier mask（shim 的 mods_from_flags 表）→ Android metaState（AndroidPlatform
		// .ReportMetaState 的表：shift 1 / alt 2 / ctrl 0x1000 / meta 0x10000 / caps 0x100000）
		private static int MetaState(int glfwMods)
		{
			int m = 0;
			if ((glfwMods & 0x0001) != 0) m |= 0x000001; // shift
			if ((glfwMods & 0x0002) != 0) m |= 0x001000; // control
			if ((glfwMods & 0x0004) != 0) m |= 0x000002; // alt
			if ((glfwMods & 0x0008) != 0) m |= 0x010000; // meta
			if ((glfwMods & 0x0010) != 0) m |= 0x100000; // caps lock
			return m;
		}

		// GLFW key code → AKEYCODE（AndroidPlatform.KeyFor 的 scanCode==0 分支读 keyCode 表，
		// AndroidKeys.FromKeyCode）。GLFW 值来自 main.m 的 key_from_hid（HID usage 的 GLFW 码）。
		private static int AKeyCode(int glfwKey)
		{
			if (glfwKey >= 'A' && glfwKey <= 'Z') return 29 + (glfwKey - 'A');  // KEYCODE_A..Z
			if (glfwKey >= '0' && glfwKey <= '9') return 7 + (glfwKey - '0');   // KEYCODE_0..9
			if (glfwKey >= 290 && glfwKey <= 301) return 131 + (glfwKey - 290); // F1..F12
			switch (glfwKey)
			{
				case 256: return 111;  // escape
				case 257: return 66;   // enter
				case 258: return 61;   // tab
				case 259: return 67;   // backspace (DEL)
				case 32: return 62;    // space
				case 39: return 75;    // apostrophe
				case 44: return 55;    // comma
				case 45: return 69;    // minus
				case 46: return 56;    // period
				case 47: return 76;    // slash
				case 59: return 74;    // semicolon
				case 61: return 70;    // equals
				case 91: return 71;    // left bracket
				case 92: return 73;    // backslash
				case 93: return 72;    // right bracket
				case 96: return 68;    // grave
				case 261: return 112;  // delete (forward del)
				case 260: return 124;  // insert
				case 262: return 22;   // dpad right
				case 263: return 21;   // dpad left
				case 264: return 20;   // dpad down
				case 265: return 19;   // dpad up
				case 266: return 92;   // page up
				case 267: return 93;   // page down
				case 268: return 122;  // move home
				case 269: return 123;  // move end
				case 280: return 115;  // caps lock
				case 320: return 144;  // keypad 0
				case 321: return 145;  // keypad 1
				case 322: return 146;  // keypad 2
				case 323: return 147;  // keypad 3
				case 324: return 148;  // keypad 4
				case 325: return 149;  // keypad 5
				case 326: return 150;  // keypad 6
				case 327: return 151;  // keypad 7
				case 328: return 152;  // keypad 8
				case 329: return 153;  // keypad 9
				case 330: return 158;  // keypad .
				case 331: return 154;  // keypad /
				case 332: return 155;  // keypad *
				case 333: return 156;  // keypad -
				case 334: return 157;  // keypad +
				case 335: return 160;  // keypad enter
				case 336: return 161;  // keypad =
			}
			return 0; // KEYCODE_UNKNOWN——OnKey 走 Unknown 物理键，无害
		}

		// ---- 状态 ----
		private static readonly Stopwatch Clock = Stopwatch.StartNew();
		private static IntPtr s_layer;
		private static int s_w, s_h;          // 物理像素（env/resize 事件）
		private static float s_density = 3f;
		private static float s_refresh = 60f;
		private static long s_lastFrameMs;
		private static SinkDelegate s_sinkKeepAlive;   // shim 持裸指针，本引用防 GC
		private static readonly object[] Zero2 = { IntPtr.Zero, IntPtr.Zero }; // (env, clazz)

		// 反射绑定的 AndroidHost 件
		private static MethodInfo s_lifecycle, s_focus, s_pointer, s_key, s_frame; // Native*（Invoke 用）
		private static MethodInfo s_post;                // AndroidHost.Post(Action<AndroidPlatform>)
		private static MethodInfo s_onSurfaceChanged;    // AndroidPlatform.OnSurfaceChanged（实例）
		private static Type s_platformType, s_physicalSizeType;
		private static FieldInfo s_queueCount;           // s_events（背压）
		private static object s_queue;                   // s_events 的值（Count 读取用）
		private static PropertyInfo s_queueCountProp;

		// fnptr 桩的委托保活（字段写进去的指针要一直可调）
		private delegate void DRelease(IntPtr window);
		private delegate int DFrameRate(IntPtr window, float rate, sbyte reserved);
		private static readonly DRelease NoopRelease = _ => { };
		private static readonly DFrameRate NoopFrameRate = (_, _, _) => 0;

		private delegate void SinkDelegate(IntPtr ev);

		[DllImport("glfw")]
		private static extern void glfwIosSetEventSink(IntPtr sink);

		// ---- Post 的闭包桩：Action<AndroidPlatform> 在编译期写不出这个类型，泛型特化现场造 ----
		private sealed class Thunk<T>
		{
			public MethodInfo Method;
			public object[] Args;
			public void Run(T platform)
			{
				try { Method.Invoke(platform, Args); }
				catch (Exception e) { Console.WriteLine("[ioshost] 回调投递失败：" + (e.InnerException?.Message ?? e.Message)); }
			}
		}

		public static void Start()
		{
			try
			{
				Assembly asm = Assembly.Load("Waylith.Windowing");
				Type host = asm.GetType("Waylith.Windowing.Backends.Android.AndroidHost")
					?? throw new TypeLoadException("Backends.Android.AndroidHost 不在——游戏版本变了？");
				s_platformType = asm.GetType("Waylith.Windowing.Backends.Android.AndroidPlatform")
					?? throw new TypeLoadException("Backends.Android.AndroidPlatform 不在");
				s_physicalSizeType = FindNestedOrByCtor(s_platformType);
				const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

				// 门 1：AndroidHost.IsInitialized => s_javaVm != 0（跳过真 Initialize 的 JNI 装载）
				host.GetField("s_javaVm", F).SetValue(null, new IntPtr(1));
				// 门 2：IsApplicationThread => Thread.CurrentThread == s_applicationThread
				//（StartupHook 与引擎 Main 同线程——WindowingPlatform.Run 也在这条线程）
				host.GetField("s_applicationThread", F).SetValue(null, Thread.CurrentThread);
				// 函数指针桩：ReleaseNativeWindow（同窗口重发/退出路径都调）与
				// SetNativeWindowFrameRate（ApplyPreferredFrameRate 调）在真 Initialize 里从
				// libandroid.so 解析——这里给 no-op，防 null fnptr 调用炸。
				SetFnPtr(host, "ReleaseNativeWindow", Marshal.GetFunctionPointerForDelegate((Delegate)NoopRelease));
				SetFnPtr(host, "SetNativeWindowFrameRate", Marshal.GetFunctionPointerForDelegate((Delegate)NoopFrameRate));

				s_lifecycle = GetM(host, "NativeLifecycle", F);
				s_focus = GetM(host, "NativeFocus", F);
				s_pointer = GetM(host, "NativePointer", F);
				s_key = GetM(host, "NativeKey", F);
				s_frame = GetM(host, "NativeFrame", F);
				s_post = GetPostMethod(host, F);
				s_onSurfaceChanged = s_platformType.GetMethod("OnSurfaceChanged",
					BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
				FieldInfo ev = host.GetField("s_events", F)
					?? throw new MissingFieldException("AndroidHost", "s_events");
				s_queue = ev.GetValue(null);
				s_queueCountProp = ev.FieldType.GetProperty("Count");

				ReadEnv();
				// 初始 Configuration（AndroidPlatform 构造直读 CurrentConfiguration；
				// NativeConfiguration 体里有 new Jni(env)——反射直写绕开）
				BuildAndStoreConfiguration(host, F);

				s_sinkKeepAlive = Sink;
				glfwIosSetEventSink(Marshal.GetFunctionPointerForDelegate(s_sinkKeepAlive));
				new Thread(FrameLoop) { IsBackground = true, Name = "port17-ioshost-frame" }.Start();
				Console.WriteLine("[ioshost] 桥就绪（Android 后端；layer=0x" + s_layer.ToString("X") +
					"，" + s_w + "x" + s_h + " @" + s_density + "x，" + s_refresh + "Hz 心跳）");
				if (s_layer != IntPtr.Zero && s_w > 0 && s_h > 0)
					PostSurface(); // 入队：平台一构造、首次 Drain 即收到（窗口创建前后都合法）
			}
			catch (Exception e)
			{
				Console.WriteLine("[ioshost] 桥初始化失败：" + e + "——引擎将无窗口后端（No windowing backend could start）");
			}
		}

		// 初始实况（启动器 engineTapped 落的 env）：层指针 / 屏幕比例 / 窗口物理尺寸
		private static void ReadEnv()
		{
			string layerHex = Environment.GetEnvironmentVariable("PORT17_METAL_LAYER");
			if (!string.IsNullOrEmpty(layerHex))
				s_layer = (IntPtr)Convert.ToInt64(layerHex, 16);
			string scale = Environment.GetEnvironmentVariable("PORT17_SCREEN_SCALE");
			if (!string.IsNullOrEmpty(scale) && float.TryParse(scale, out float d) && d > 0)
				s_density = d;
			string size = Environment.GetEnvironmentVariable("PORT17_SCREEN_SIZE");
			if (!string.IsNullOrEmpty(size))
			{
				int x = size.IndexOf('x');
				if (x > 0 && int.TryParse(size.Substring(0, x), out int w) &&
					int.TryParse(size.Substring(x + 1), out int h) && w > 0 && h > 0)
				{
					s_w = w; s_h = h;
				}
			}
		}

		private static void SetFnPtr(Type host, string name, IntPtr fn)
		{
			FieldInfo f = host.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
				?? host.GetField("<" + name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);
			if (f == null)
				throw new MissingFieldException("AndroidHost", name);
			f.SetValue(null, fn); // net8 本地实测：fnptr 静态字段收 boxed IntPtr 可写、可调
		}

		private static MethodInfo GetM(Type host, string name, BindingFlags F)
		{
			MethodInfo m = host.GetMethod(name, F)
				?? throw new MissingMethodException("Waylith.Windowing.AndroidHost", name);
			return m;
		}

		// Post 有两个重载（Post(Action<AndroidPlatform>) 与 Post(in HostEvent, string)）——
		// 取单参、非 ref/byref、泛型 Action 的那个
		private static MethodInfo GetPostMethod(Type host, BindingFlags F)
		{
			foreach (MethodInfo m in host.GetMethods(F))
			{
				if (m.Name != "Post")
					continue;
				ParameterInfo[] ps = m.GetParameters();
				if (ps.Length == 1 && !ps[0].ParameterType.IsByRef &&
					ps[0].ParameterType.IsGenericType &&
					ps[0].ParameterType.GetGenericTypeDefinition() == typeof(Action<>))
					return m;
			}
			throw new MissingMethodException("Waylith.Windowing.AndroidHost", "Post(Action<AndroidPlatform>)");
		}

		// PhysicalSize 类型：从 OnSurfaceChanged 的第 2 参拿（不猜命名空间）
		private static Type FindNestedOrByCtor(Type platformType)
		{
			foreach (MethodInfo m in platformType.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				if (m.Name != "OnSurfaceChanged")
					continue;
				ParameterInfo[] ps = m.GetParameters();
				if (ps.Length >= 2)
					return ps[1].ParameterType;
			}
			throw new MissingMethodException("AndroidPlatform", "OnSurfaceChanged");
		}

		private static void BuildAndStoreConfiguration(Type host, BindingFlags F)
		{
			FieldInfo cfgField = host.GetField("s_currentConfiguration", F)
				?? throw new MissingFieldException("AndroidHost", "s_currentConfiguration");
			Type strongBox = cfgField.FieldType;
			Type configType = strongBox.GetGenericArguments()[0];
			// Configuration(Density, RefreshRate, PhysicalSize, Night, FontScale, AccentArgb,
			//               Animations, DoubleTapMillis, TouchSlop, Languages)——record 声明序
			object size = Activator.CreateInstance(s_physicalSizeType, s_w, s_h);
			object cfg = Activator.CreateInstance(configType,
				s_density, s_refresh, size, false, 1f, 0, true, 400, 8, "");
			cfgField.SetValue(null, Activator.CreateInstance(strongBox, cfg));
			Console.WriteLine("[ioshost] Configuration 已就位（" + s_w + "x" + s_h + " @" + s_density + "x）");
		}

		// surface 投递：Post(platform => platform.OnSurfaceChanged(layer, size))——队列投递，
		// 平台线程上执行（NativeSurfaceChanged 体里的 ANativeWindow_fromSurface 在 iOS 无意义）
		private static void PostSurface()
		{
			Type actionType = typeof(Action<>).MakeGenericType(s_platformType);
			Type thunkType = typeof(Thunk<>).MakeGenericType(s_platformType);
			object thunk = Activator.CreateInstance(thunkType);
			thunkType.GetField("Method").SetValue(thunk, s_onSurfaceChanged);
			thunkType.GetField("Args").SetValue(thunk,
				new object[] { s_layer, Activator.CreateInstance(s_physicalSizeType, s_w, s_h) });
			Delegate d = Delegate.CreateDelegate(actionType, thunk, thunkType.GetMethod("Run"));
			s_post.Invoke(null, new object[] { d });
		}

		// ---- 帧心跳：ComputeTimeout 的 WaitForEvents park 唯一唤醒源（安卓 Choreographer 位）----
		private static void FrameLoop()
		{
			while (true)
			{
				long now = Clock.ElapsedMilliseconds;
				long target = (long)(1000f / s_refresh);
				long delta = now - s_lastFrameMs;
				if (delta < target - 1)
				{
					Thread.Sleep((int)Math.Max(1, target - delta - 1));
					continue;
				}
				// 背压：引擎没在泵（未启动/挂起）时队列会积——上限约 5 秒的帧，让路再发
				if (QueueCount() > 300)
				{
					Thread.Sleep(50);
					continue;
				}
				s_lastFrameMs = now;
				Invoke(s_frame, now);
			}
		}

		private static int QueueCount()
		{
			try { return s_queueCountProp != null ? (int)s_queueCountProp.GetValue(s_queue) : 0; }
			catch { return 0; }
		}

		// Native* 是 [UnmanagedCallersOnly]——CreateDelegate 被 CoreCLR 拒，Invoke 全程
		private static void Invoke(MethodInfo m, params object[] args)
		{
			if (m == null)
				return;
			try { m.Invoke(null, Concat2(args)); }
			catch (Exception e) { Console.WriteLine("[ioshost] " + m.Name + " 投递失败：" + (e.InnerException?.Message ?? e.Message)); }
		}

		// (env, clazz) 前缀 + 尾参（Native* 的 JNI 形态前两参在托管化路径里不参与逻辑）
		private static object[] Concat2(object[] args)
		{
			var r = new object[2 + args.Length];
			r[0] = IntPtr.Zero; r[1] = IntPtr.Zero;
			for (int i = 0; i < args.Length; i++)
				r[2 + i] = args[i];
			return r;
		}

		private static object[] Concat2(this object[] tail, object[] args)
		{
			var r = new object[2 + args.Length];
			r[0] = IntPtr.Zero; r[1] = IntPtr.Zero;
			for (int i = 0; i < args.Length; i++)
				r[2 + i] = args[i];
			return r;
		}

		// ---- shim 的事件入口（主线程；Native* 内部只进队列，线程安全）----
		private static void Sink(IntPtr evPtr)
		{
			PortIosEvent ev;
			try { ev = Marshal.PtrToStructure<PortIosEvent>(evPtr); }
			catch { return; }
			long now = ev.TimeMs > 0 ? ev.TimeMs : Clock.ElapsedMilliseconds;
			switch (ev.Kind)
			{
			case 1: // touch：i0=type(1..4)，i1=id，f0/f1=窗口物理像素（shim 已乘 nativeScale）
				int action = ev.I0 == 1 ? PointerDown : ev.I0 == 3 ? PointerUp
					: ev.I0 == 4 ? PointerCancel : PointerMove;
				Invoke(s_pointer, action, (int)ev.I1, ToolFinger, ev.F0, ev.F1, 1f, 0f, 0f, 0, 0, now);
				break;
			case 2: // key：i0=GLFW key，i1=action(0 release/1 press/2 repeat)，i2=GLFW mods
				Invoke(s_key, (byte)(ev.I1 != 0 ? 1 : 0), AKeyCode((int)ev.I0), 0,
					MetaState((int)ev.I2), 0, now);
				break;
			case 3: // char：文本走 unicodeChar 通道（OnKey 的 FromCharacter 分支）
				Invoke(s_key, (byte)1, 0, 0, MetaState((int)ev.I2), (int)ev.I0, now);
				break;
			case 4: // app 生命周期：shim 归一 1=active 2=resign 3=background 4=foreground
				Invoke(s_lifecycle, ev.I0 == 1 ? LifecycleResume : ev.I0 == 2 ? LifecyclePause
					: ev.I0 == 3 ? LifecycleStop : LifecycleStart);
				Invoke(s_focus, (byte)(ev.I0 == 1 || ev.I0 == 4 ? 1 : 0));
				break;
			case 5: // resize：i1/i2=物理像素（drawable）
				s_w = (int)ev.I1; s_h = (int)ev.I2;
				if (s_layer != IntPtr.Zero)
					PostSurface();
				break;
			}
		}
	}
}
