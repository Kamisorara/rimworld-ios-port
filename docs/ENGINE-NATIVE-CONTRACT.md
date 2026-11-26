# 引擎的原生入口契约（GLFW / Vulkan / 各原生库）

从安卓仓库（`rimworld-android-port`）的垫片源码盘出来的接口清单，是 iOS 后端的实现契约。
安卓侧的语义源头：

- GLFW 垫片：`host/glfw/glfw_android.c`（1713 行，GLFW 3.4 C API 的 Vulkan 应用子集 + `glfwAndroid*` 扩展）
- Vulkan 拦截层：`host/vulkan/vulkan_interpose.c`（1633 行，`libvulkan.so` → 系统 loader 的前向 + 钩子）
- 宿主接线：`host/host.c`（`attach_glfw` 在 runtime 启动前把 activity 交给垫片）
- 原生库构建：`native/build.sh` + `host/fetch-deps.sh`（各库版本与来源）

## 1. 动态库绑定名（引擎 P/Invoke 的名字 → 文件名）

.NET 在 Darwin 上对无扩展名的绑定名依次尝试 `{name}.dylib`、`lib{name}.dylib`（与安卓上
`"glfw"` → `libglfw.so` 同一条规则；CoreCLR 的 `SystemNative_LoadLibrary` 直接 `dlopen` 原名，
候选名扩展在 DllImport 解析层）。iOS 侧全部平铺在 .app 根目录：

| 引擎绑定名（安卓） | 安卓文件 | iOS 文件 | 说明 |
|---|---|---|---|
| `glfw`（Brutal.Glfw） | libglfw.so | **libglfw.dylib** | 自写垫片（本文第 2 节） |
| `vulkan`（Brutal.Vulkan，`NativeLibrary.Load("vulkan")`） | libvulkan.so（自写拦截层） | **libvulkan.dylib**（自写前向层）→ MoltenVK.framework | 前向层 dladdr 定位同目录的 `MoltenVK.framework/MoltenVK` |
| `waylithaudio`（Waylith.Audio.MiniAudio） | libwaylithaudio.so | libwaylithaudio.dylib | miniaudio 壳；iOS 用 CoreAudio 后端（miniaudio 原生支持） |
| `Ktx`（Brutal.Ktx） | libKtx.so | libKtx.dylib | 注意大写 K——绑定名是 `Ktx`，文件名 libKtx.dylib |
| `imgui` / `imgui_freetype`（Brutal.ImGui） | libimgui.so / libimgui_freetype.so | libimgui.dylib / libimgui_freetype.dylib | |
| `vma` / `stb` / `gli` | libvma.so / libstb.so / libgli.so | 同名 .dylib | |
| `slang`（着色器编译） | libslang.so（代理）→ libslang-compiler.so 等 | 同名 .dylib | 版本尾巴跟 PC 一致（2026.19），着色器缓存键才对得上 |
| `portastc`（hooks/Textures.cs） | libportastc.so | libportastc.dylib | astcenc，iOS 同样只需要 arm64 NEON |

libvulkan 前向层必须**直接导出**的全局入口（设备级函数引擎全部经 `vkGetInstanceProcAddr` 取）：

- `vkGetInstanceProcAddr`
- `vkCreateInstance`
- `vkEnumerateInstanceExtensionProperties`
- `vkEnumerateInstanceLayerProperties`
- `vkEnumerateInstanceVersion`（可选，无则引擎按 1.0 处理）

安卓拦截层对 GLFW 垫片暴露的协调入口（`dlsym` 调用，非引擎路径），iOS 前向层保留同名导出：

- `port17_vulkan_render_size(uint32_t w, uint32_t h)`：GLFW 垫片告知渲染分辨率
- `port17_vulkan_window_changed(void)`：窗口重建，要求引擎重建 surface/swapchain
- `port17_vulkan_release_surface(VkInstance, VkSurfaceKHR, const VkAllocationCallbacks*)`：
  旧 surface 还占着窗口时先放掉再重建

iOS 的分辨率缩放机制与安卓不同：CAMetalLayer 的 `drawableSize` 直接决定 swapchain 尺寸，
compositor 自动拉伸到 layer bounds——不需要拦截 surface capabilities 改 currentExtent
（安卓那条 identity-transform 钩子在 iOS 无对应问题，MoltenVK 报告的即 Metal 事实）。

## 2. GLFW 垫片必须提供的入口（iOS 后端 = host/glfw/glfw_ios.c）

### 2.1 常量（glfw3.h 3.4 的值，managed Brutal.Glfw 编译期烧死的数字）

`GLFW_RELEASE/PRESS/REPEAT` = 0/1/2；键码：可打印字符 = ASCII（32–96），功能键 256 起
（ESC 256, ENTER 257, TAB 258, BACKSPACE 259, INSERT 260, DELETE 261, RIGHT 262, LEFT 263,
DOWN 264, UP 265, PAGE_UP 266, PAGE_DOWN 267, HOME 268, END 269, CAPS 280, SCROLL 281, NUM 282,
SYSRQ 283, BREAK 284, F1–F12 = 290–301, KP0–9 = 320–329, KP. 330, KP/ 331, KP* 332, KP- 333,
KP+ 334, KP-ENTER 335, KP= 336, L-SHIFT 340 … R-META 347, MENU 348）；mods：SHIFT 0x1,
CONTROL 0x2, ALT 0x4, SUPER 0x8, CAPS_LOCK 0x10, NUM_LOCK 0x20。窗口属性/输入模式等常量
照 glfw3.h（垫片源文件顶部已全列出，iOS 版直接抄）。

### 2.2 GLFW 3.4 C API（引擎用的子集——安卓垫片全部实现过，iOS 全部保留）

- 初始化：`glfwInit / glfwTerminate / glfwInitHint / glfwInitAllocator / glfwInitVulkanLoader /
  glfwGetVersion / glfwGetVersionString / glfwGetError / glfwSetErrorCallback /
  glfwGetPlatform（返回 PLATFORM_NULL=0x00060005）/ glfwPlatformSupported`
- 监视器：`glfwGetMonitors / glfwGetPrimaryMonitor / glfwGetMonitorPos / glfwGetMonitorWorkarea /
  glfwGetMonitorPhysicalSize / glfwGetMonitorContentScale / glfwGetMonitorName /
  glfwSetMonitorUserPointer / glfwGetMonitorUserPointer / glfwSetMonitorCallback /
  glfwGetVideoModes / glfwGetVideoMode / glfwSetGamma / glfwGetGammaRamp / glfwSetGammaRamp`
  （iOS：单一监视器 = UIScreen；vidmode.width/height = nativeBounds 像素，refreshRate =
  maximumFramesPerSecond；physicalSize 由 nativeScale 换算）
- 窗口：`glfwDefaultWindowHints / glfwWindowHint / glfwWindowHintString / glfwCreateWindow /
  glfwDestroyWindow / glfwWindowShouldClose / glfwSetWindowShouldClose / glfwGetWindowTitle /
  glfwSetWindowTitle / glfwSetWindowIcon / glfwGet/SetWindowPos / glfwGetWindowSize /
  glfwSetWindowSize（→ 渲染分辨率请求）/ glfwSetWindowSizeLimits / glfwSetWindowAspectRatio /
  glfwGetFramebufferSize / glfwGetWindowFrameSize / glfwGet/SetWindowContentScale /
  glfwGet/SetWindowOpacity / glfwIconify/Restore/Maximize/Show/Hide/FocusWindow /
  glfwRequestWindowAttention / glfwGetWindowMonitor / glfwSetWindowMonitor（→ 渲染分辨率请求）/
  glfwGetWindowAttrib / glfwSetWindowAttrib / glfwSet/GetWindowUserPointer` 及全部回调 setter
  （pos/size/close/refresh/focus/iconify/maximize/fbsize/contentscale/mousebutton/cursorpos/
  cursorenter/scroll/key/char/charmods/drop/monitor/joystick）
  - 语义要点（安卓实测行为，引擎依赖）：
    - `glfwCreateWindow` 等待真实窗口就绪（≤20s），把 size/fbsize 回调至少送一次
      （managed 侧只从 fbsize 回调得知帧缓冲尺寸，0×0 = 最小化）
    - 窗口消失（安卓 TERM_WINDOW / iOS 进后台）→ 送 size/fbsize 0×0；回来时重新送真实尺寸
    - 失焦时释放全部按住的键与鼠标键（真 GLFW 行为，引擎依赖）
    - 单窗口、单监视器；`GLFW_CLIENT_API` 属性恒为 `GLFW_NO_API`
- 事件：`glfwPollEvents / glfwWaitEvents / glfwWaitEventsTimeout / glfwPostEmptyEvent`
  （drain 队列后在场线程回调——回调永远跑在轮询线程）
- 输入查询：`glfwGetInputMode / glfwSetInputMode / glfwRawMouseMotionSupported(=FALSE) /
  glfwGetKeyName(NULL) / glfwGetKeyScancode(=key) / glfwGetKey / glfwGetMouseButton /
  glfwGetCursorPos / glfwSetCursorPos / glfwCreateCursor / glfwCreateStandardCursor /
  glfwDestroyCursor / glfwSetCursor`；游戏杆全家（present=FALSE，无 MFI 支持暂留）
- 剪贴板：`glfwSetClipboardString / glfwGetClipboardString`（iOS：UIPasteboard）
- 时间：`glfwGetTime / glfwSetTime / glfwGetTimerValue / glfwGetTimerFrequency`（1e9）
- OpenGL 桩：`glfwMakeContextCurrent / glfwGetCurrentContext / glfwSwapBuffers /
  glfwSwapInterval / glfwExtensionSupported(FALSE) / glfwGetProcAddress(NULL)`
- Vulkan：`glfwVulkanSupported / glfwGetRequiredInstanceExtensions / glfwGetInstanceProcAddress /
  glfwGetPhysicalDevicePresentationSupport(TRUE) / glfwCreateWindowSurface`
  - **iOS 的 required extensions = `{ "VK_KHR_surface", "VK_EXT_metal_surface" }`**
    （MoltenVK 1.4.2 只支持 VK_EXT_metal_surface；老的 VK_MVK_ios_surface 已删除）
  - `glfwCreateWindowSurface` 经 `vkCreateMetalSurfaceEXT`，`pLayer` = 宿主的 CAMetalLayer
- 各平台原生访问桩（Win32/Cocoa/X11/Wayland/EGL/OSMesa 全 NULL）

### 2.3 移植层扩展（PortMobile P/Invoke "glfw"；**名字保持 glfwAndroid\* 不改**——M4 共享托管层零改动）

| 入口 | iOS 实现 |
|---|---|
| `glfwAndroidSetTouchMode(int)` | 原始触摸队列 vs 鼠标模拟开关 |
| `glfwAndroidPollTouches(TouchEvent*, int)` | 拉原始触摸事件（type 1 down/2 move/3 up/4 cancel/5 软键盘动作键；id、float x/y、double 时间戳） |
| `glfwAndroidInjectCursor/Button/Scroll/Key/Char` | 注入事件进同一队列 |
| `glfwAndroidGetDensity()` | `UIScreen.nativeScale`（安卓语义 = dp 缩放） |
| `glfwAndroidViewSize(int*, int*)` | 视图自身尺寸（窗口像素，非渲染尺寸） |
| `glfwAndroidCutout(char*, int)` | safeAreaInsets → "l,t,r,b;…" 格式（渲染像素坐标） |
| `glfwAndroidShowKeyboard(int)` | 键盘桥：show → 隐藏 UITextField becomeFirstResponder |
| `glfwAndroidKeyboardForced(int)` | iOS 恒直上软键盘（无 IME 策略差异），记录即返回 |
| `glfwAndroidLastDevices(int*, int*)` | 外接鼠标/键盘设备 id；iOS 暂 -1（外接键盘经 UIKey 识别后可补） |
| `glfwAndroidInputDevices(char*, int)` | iOS 暂回 ""（无 addInputDeviceListener 等价物；GameController API 留后） |
| `glfwAndroidOpenUrl(const char*)` | UIApplication.openURL（不再先问 canOpenURL；URL 解析失败时百分号编码重试）。在主线程调用时内联执行，否则 dispatch_sync 到主队列 |
| `glfwAndroidFilesDir(char*, int)` | 返回 `NSHomeDirectory()/Documents` 的 UTF-8 路径（即 `ModImport`/`ModImport.state` 所在目录），返回长度；缓冲区不够或参数无效返回 0。托管 `ModImport.FilesDir` 以它为准，避免两边各猜一个 Documents |
| `glfwAndroidPickFiles` | `UIDocumentPickerViewController`：zip/.rws 用 import-as-copy（可多选），文件夹用 security-scoped opening；zip 复用 `host/app/unzip.c` 解到 `Documents/ModImport`，状态文件与安卓同协议。**不依赖 `glfwIosAttach`**（正式引擎路从不调用它，`g_view` 恒为 NULL）；从场景 key window 的 rootViewController（引擎的 WaylithViewController）弹出；1 秒后 sheet 仍未挂上且无回调 → 写 `PICK_FAILED "the system's file picker did not come up"`；上一次选择仍活动时返回 0 |
| `glfwAndroidOpenFolder / ScanMedia / ProxyFor` | M2 桩（返回 0/""），按需再议 |
| `glfwAndroidPerfHint / PerfHintConfig / PerfReport / SetFrameRate` | iOS 无 APerformanceHint 等价；SetFrameRate 可用 CADisplayLink/`CAMetalLayer.preferredFramesPerSecond`… M2 桩 |
| `glfwAndroidDisplayRate()` | `UIScreen.maximumFramesPerSecond` |
| `glfwAndroidPrepareExit()` | atexit(_exit) 同款（iOS 无 finishGame 对应物） |

### 2.4 宿主接线（iOS 版：main.m ↔ glfw_ios.c）

- `glfwIosAttach(CAMetalLayer*, UIView*)`：app 启动时主线程调用（对应安卓 `glfwAndroidAttach(app)`）。
  视图须 `multipleTouchEnabled = YES`。**仅启动器的诊断线程模型（`launcher.m`）调用**；正式引擎路（IOS 后端 + 主线程直接跑引擎）不调用它，垫片里 `g_view`/`g_layer` 保持 NULL，`glfwAndroid*` 导出不得以此为前提。
- 线程规则：正式引擎路的游戏帧跑在进程主线程（run-loop 回调），`glfwAndroid*` 里需要 UIKit 的代码统一走 `on_main_sync`：`pthread_main_np()` 为真时直接执行，否则 `dispatch_sync` 到主队列——主线程对自己 `dispatch_sync` 主队列会死锁。
- `glfwIosTouch(int type, int id, float x, float y)`：视图 touches* 逐指上报（UIKit 点坐标 →
  垫片内部换算 drawable 像素；对应安卓 on_input_event 的 record_touch + 鼠标模拟）
- `glfwIosKey(int glfwKey, int action, int mods)` / `glfwIosTextCommit(const uint32_t*, int)`：
  软键盘桥与硬件键盘（UIKey，HID usage → GLFW 键码）入口
- `glfwIosEditorAction()`：软键盘 Done/换行 → managed 模式记 type 5 / 普通模式 Enter 按下+抬起
- `glfwIosApplicationEvent(int)`：前后台/焦点通知（后台 = EV_WINDOW_TERM + 失焦；回前台 = INIT + 得焦）
- `glfwIosViewResized()`：旋转/分屏改变 bounds 后调用

线程模型与安卓相同：UIKit 主线程只入队（mutex），引擎/轮询线程 drain 并回调；
glfwWaitEvents 用条件变量阻塞。

## 3. Vulkan 路径（iOS）

```
引擎 NativeLibrary.Load("vulkan") ──▶ libvulkan.dylib（自写前向层）
                                        │ dlopen 同目录 MoltenVK.framework/MoltenVK
                                        ▼
                                     MoltenVK 1.4.2（Vulkan 1.4 over Metal）──▶ CAMetalLayer
```

- 前向层薄：5 个全局入口直转 MoltenVK，`vkGetInstanceProcAddr` 包装一层留钩子位
  （M2 首版纯前向；安卓侧的 coherent-memory/descriptor-pool/identity-transform 钩子在 iOS
  按需再加——Metal 的内存模型与 ICD 池行为不同，预计不需要或需要不同的）
- 0.4.28 起钩子位承载性能观测：`vkGetInstanceProcAddr` 与 `vkGetDeviceProcAddr` 包装
  metal export、queue submit/present、acquire、fence wait、swapchain create/image query；
  聚合 5 秒输出一次。0.4.29 起引擎实例默认把 `minImageCount` 抬到 3；caps 必须匹配同一
  surface 且 max 允许，否则只记录 `cannot force`。`PORT17_SWAPCHAIN_IMAGES=0` 显式关闭，
  `=2` 回双缓冲对照；`port17-triangle` 实例不吃这个默认，三角形自身仍用
  `PORT17_TRIANGLE_IMAGES` 做 2/3 图对照。
- 宿主在 MoltenVK 装载前读 `Documents/game/port17-perf.txt`。可用键：
  `swapchainImages=0|2|3`（0.4.29 起缺省即 3）、`triangleImages=2|3`、`mvkPerformanceTracking`、
  `mvkSynchronousQueueSubmits`、`mvkPrefillMetalCommandBuffers`。文件不在或键不在时默认关闭。
- MoltenVK 官方发布包：`MoltenVK-ios.tar`（v1.4.2，2026-07-20，iOS ≥15.0）里的
  `MoltenVK/MoltenVK/dynamic/MoltenVK.xcframework/ios-arm64/MoltenVK.framework`（动态，放 .app 根，
  @executable_path rpath 已由宿主链接参数覆盖）与 `MoltenVK/MoltenVK/include`（完整 Vulkan 头，
  编译垫片/前向层/三角形共用）
- MoltenVK 环境变量（`MVK_CONFIG_*`）可在宿主 setenv 调优（如 `MVK_CONFIG_RESUME_LOST_DEVICE`），
  留作 M2 真机调试旋钮。**已定死的一条**（宿主 main() 里设，必须在 MoltenVK 装载前）：
  `MVK_CONFIG_RESIDENCY_SETS=0` + `MVK_CONFIG_USE_MTLRESIDENCYSETS=0`——同为 MoltenVK 上架游戏
  MHGURecomp-iOS 的实战教训（Apple Silicon / iOS 18+ 上 Metal residency sets 不稳，关掉保稳，只损失
  一点性能优化路径）
- 参考：MHGURecomp-iOS（`D:\Data\Code\reverse\MHGURecomp-iOS`，静态重编译的 Switch 游戏上架 iOS，
  SDL3 + MoltenVK 1.3.0 静态库 + Vulkan 1.2）。引擎阶段可再借的写法在 `runtime/nvn/vulkan/device.c`：
  compositeAlpha 的 OPAQUE→INHERIT→PRE_MULTIPLIED 回退、swapchain 重建（OUT_OF_DATE/SUBOPTIMAL 都
  触发）、退出纪律（atexit → vkDeviceWaitIdle → _exit(0)，不跑后续 teardown——与 glfwAndroidPrepareExit
  同一哲学）、Vulkan 1.2 的 timeline semaphore 帧同步

## 4. 音频（waylithaudio → iOS）

miniaudio 0.11.22 的 CoreAudio 后端覆盖 iOS（`MA_ENABLE_COREAUDIO`）；waylithaudio.c 的引擎 API
面（sounds/voices/groups/device + wa_* 入口，安卓版文件头有完整清单）不动，只换后端宏
（安卓 `MA_ENABLE_AAUDIO/OPENSL` → iOS `MA_ENABLE_COREAUDIO`）+ 去掉 android/log.h。M2 后期做。

## 5. 版本事实（2026-11-21 核验）

- MoltenVK 最新 = **v1.4.2**（2026-07-24 发布，tar 内 Whats_New 落款 2026-07-20），
  sha256(MoltenVK-ios.tar) = `b5d947b1660e6e9fed40b9cd2387e160aaab9e80b775c0cef7e14059405178c1`
- MoltenVK 最低部署目标 iOS 15.0（与宿主 `-target arm64-apple-ios15.0` 一致）
- MoltenVK 1.4.2 支持 Vulkan 1.4；头文件含 `VK_EXT_metal_surface` / `vkCreateMetalSurfaceEXT`
- CI 上 SPIR-V 编译器：homebrew `glslang` 公式存在（macos-15 runner 可 `brew install glslang`）
