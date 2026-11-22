# 移植计划与决策记录

## 已定决策（2026-11-08）

| 决策 | 内容 | 依据 |
|---|---|---|
| 运行时基线 | dotnet/runtime `release/11.0` @ `86ee4f57ebc`（2026-11-05），见 `runtime-pin.txt` | ① v10.0.12 源码里 CoreCLR 没有 Apple mobile 支持（无 `APPLE_MOBILE`、无 MAP_JIT 执行内存路径），`-os ios` 只服务于 crossgen2 测试；② NuGet 上 ios-arm64 没有 10.x 正式包（6.0 preview 直接跳 11.0 preview），iOS CoreCLR 是 .NET 11 才有的；③ `release/11.0` 的 `doublemapping.cpp:174-179` 有完整的 RWX→EACCES→MAP_JIT 探测回退（macOS arm64 JIT 同款），`--dynamiccodecompiled true` 构建开关在。v11.0.0 GA tag 发布后升级 pin。 |
| JIT 路线 | 构建带 JIT 的变体（`--dynamiccodecompiled true`），设备端经 StikDebug 开 JIT；解释器变体作后备 | StikDebug = 调试器 attach 使进程可执行自生成代码；宿主设备已验证可用。风险：iOS 大版本更新可能破坏（iOS 26 已破坏过一轮，2026-06 时仅部分 app 支持）。 |
| JIT 内存（2026-11-13 起，JIT26 协议；2026-11-11 初版为脚本预撒+扫描） | **brk 握手 + 提前分离**（`patches/dualjit-arena.patch`，playport 模式，详见 [PLAN-JIT26.md](PLAN-JIT26.md)）：runtime 在 `coreclr_initialize` 里执行 `mov x16,#1 ; brk #0xf00d`，StikDebug 应答脚本 `rw17-jit26.js` 经 debugserver `_M,rx` 分配 128MiB RX、逐页写"祝福"（分离后仍可执行的凭据）、`x0` 回传地址；runtime `vm_remap` RW 别名后立即 `brk`（`x16=0`）请求分离——**初始化与游戏全程无调试器附着**，fault 归本进程 handler（带调试器时 first-chance 异常先到 debugserver，进程内 sigsetjmp 兜底永远不跑，这就是此前"探针窗口 3ms 死点"的机理）。每次发 brk 前双查 `CS_DEBUGGED`（csops）+ `P_TRACED`（sysctl），无调试器则有界等待 120 秒后干净失败（无人接的 brk = SIGTRAP 杀进程，门必须 fail-closed）。macOS CI 保留种子竞技场扫描 + 自建 RWX 两条路径不动（`RW17_SEED_ARENA=1` 全链路验证别名/探针/Commit）；iOS 模拟器不发 brk、快速干净降级。**容量 128MiB（0x8000000）**：实测本体+移植层 17.9MB IL / 28.9 万方法，小于 STS2（21.6MB IL / 33 万方法，96MiB 实测可跑）；128MiB 是 arm64 bl ±128MB 直达范围内的最大安全竞技场直径，mod 溢出时再议窗口化扩容。| CoreCLR 初始化必须活在自己拥有 fault 派发的世界里（长会话模型已证死锁）；生态三证（universal.js 应用群、STS2 的 sigsetjmp 探针形态、playport "the pool stays executable"）表明逐页祝福在调试器分离后存续。方案参照：playport（`app/PlayportJIT/playport-universal.js` + `wine_host.c` 的 `wine_host_jit_pool_acquire`）；竞技场思路源头：杀戮尖塔2 社区 iOS 重打包逆向。 |
| 构建 | Windows 本地写码 + GitHub Actions `macos-15` 编译，公开仓库 | Apple 目标必须 macOS 宿主（runtime 仓库 CI 注释原话："the iOS jobs run on macOS"）。公开仓库 macOS runner 免费不限量。 |
| 分发 | 未签名 IPA + SideStore 自签侧载 | 无需 Apple 开发者账号；SideStore 签名自动带 `get-task-allow`（StikDebug 列表的准入条件）。 |
| 仓库 | 本仓库独立于安卓仓库，托管层源码 M4 起共享 | M1–M3 用不到安卓仓库的托管层。 |

## 里程碑

### M0 决策与验证（已完成）
runtime 基线选型、CI 方案、分发路线（本文档）。

### M1 运行时探针（✅ 已关闭，2026-11-21）

**结论：官方包路线成立。** `Microsoft.NETCore.App.Runtime.ios-arm64 11.0.0`（GA，混合构建）+ `COMPlus_InterpMode=2` 配方：真机完整跑通探针（`CoreCLR 就绪` → Reflection.Emit → `exit 42`），吞吐 **87.7 M 迭代/秒**（安卓 JIT 基线 ~200 的 44%，远超 20 的可玩阈值）。零补丁、零 StikDebug、零脚本，装完即玩。

机理备注：InterpMode=2 的关键作用是禁 R2R（绕开混合构建初始化里的 SIGABRT 死点，`eeconfig.cpp`：`InterpMode>=2 → fReadyToRun=false`）；测试设备（iOS 27，SideStore 签名自带 RWX 授权）上 JIT 实际工作（机器码速度，探针判定"JIT 生效"）。无授权普通设备走纯解释器——该路径速度未测（待补）。JIT26 竞技场分支（m1-probe.yml + dualjit-arena.patch + rw17-jit26.js）封存为实验性全速分支，未验收。

已知小债：①"重新运行"按钮在进程内二次 coreclr_initialize → 0x80131022（CoreCLR 单实例），需改为提示重启 app 或 shutdown 后重建；②屏幕文案滞后（写 InterpMode=1 实为 2；初始化提示仍提 JIT26 握手）；③无授权设备的解释器吞吐数字待补。

### M1 存档：路线决策记录

**路线决策（2026-11-21）**：解释器路线升为主线，JIT26 竞技场降为可选全速分支。
- **解释器主线**（`interp-probe.yml`）：官方 `Microsoft.NETCore.App.Runtime.ios-arm64 11.0.0`（.NET 11 GA，微软支持形态：Apple mobile = 无 JIT 解释器配置）——零补丁、零 StikDebug、零脚本，装完即玩。真机要回答的唯一问题：解释器吞吐（Hello 探针的 M 迭代/秒）能否撑起 RimWorld。安卓 JIT 基线 ~200 M 迭代/秒（桌面同量级），解释器预期慢 3–15 倍。
- **JIT26 竞技场分支**（`m1-probe.yml`，代码完成待真机验收）：STS2/playport 同款调试器授予竞技场 + brk 握手 + 提前分离，Backend v3（fd-mapper）已修 Module::Create SIGBUS 但未验收。两条路线宿主层/布局/探针全共享，仅 runtime 来源与 jit26 门控（`PORT17_INTERP`）不同；若解释器性能可接受则此分支封存，性能不足再回来验收。

**改造计划（2026-11-13 起，进行中）**：JIT 授予协议转 playport 模式——`brk #0xf00d` 同步握手 + 提前分离（不再长挂调试会话），见 [PLAN-JIT26.md](PLAN-JIT26.md)。

最小宿主 app + 自建 iOS CoreCLR + `Hello.dll`（Reflection.Emit 计时探针）+ mach RX 区域扫描。
**验收**：真机上，按下方流程开 JIT 后探针报"JIT 生效"（几十毫秒），RX 私有区域非零；port17-ui.log 里出现 `[rw17-arena] arena adopted`（brk 握手命中）。
**产出**：可下载的 `port17probe.ipa`。
**真机 JIT 流程（2026-11-13 起，JIT26 协议）**：
1. SideStore 安装 IPA（自签带 `get-task-allow`）；
2. 经 StikDebug 拉起探针 app（带 JIT）——runtime 在 `coreclr_initialize` 里发 `brk #0xf00d` 请求竞技场，最多等活调试器 120 秒（`[rw17-arena]` 进度在 stderr 日志）；
3. StikDebug 以本 app 为目标运行应答脚本 `probe/stikdebug/rw17-jit26.js`——在 app 停在 brk 上的窗口里经 debugserver `_M,rx` 分配 128MiB RX 并逐页扫写祝福（约 1–2 分钟），x0 回传地址，随后应答 `x16=0` 分离请求；**分离完成后脚本窗口可关**（祝福存续），app 在无调试器状态下继续初始化；
4. 日志期待链：`live debugger confirmed` → `arena granted at …` → `RW alias at …; detaching …` → `probe call #1 -> 42` → `probe call #2 -> 43` → `arena adopted: rx=… rw=…` → `CoreCLR 就绪` → `JIT 生效` → `exit 42`。

### M2 宿主层成型（进行中，2026-11-21 开工）
- UIKit 宿主（从 M1 探针 app 长出来）：窗口、触摸原始事件、软键盘桥（iOS 版 TextInputView 等价物）；
- GLFW 垫片的 iOS 后端（沿用安卓版"引擎只用 Vulkan 子集"的写法）；
- Vulkan 经 MoltenVK；waylithaudio 编 CoreAudio 后端；
- Slang / ImGui / FreeType / KTX / astcenc 的 iOS 构建。
**验收**：Waylith 引擎在真机上渲染出最小画面（无游戏程序集）。

**骨架与决策（2026-11-21，接口契约全表见 [ENGINE-NATIVE-CONTRACT.md](ENGINE-NATIVE-CONTRACT.md)）**：
- **host/ 目录**：`host/app/`（UIKit 宿主 main.m + 三角形 triangle.c + M1 探针诊断件 host.c 副本）、`host/glfw/glfw_ios.m`（libglfw.dylib）、`host/vulkan/vulkan_ios.c`（libvulkan.dylib）、`host/build-ipa.sh` + `host/fetch-moltenvk.sh`；流水线 `.github/workflows/m2-host.yml`（MoltenVK 按 sha256 缓存）。probe/ 原样封存为 M1 档案。
- **GLFW 后端**：安卓垫片（glfw_android.c，1713 行）的等义移植。`glfwAndroid*` 扩展**保持原名**——PortMobile 按这些名字 P/Invoke "glfw"，M4 共享托管层零改动；宿主接线入口为 `glfwIos*`（attach/viewResized/applicationEvent/touch/key/char/textCommit/editorAction/backspace）。
- **Vulkan 路线**：官方 MoltenVK **v1.4.2**（2026-07-24，Vulkan 1.4，iOS ≥15.0 与宿主 target 一致）动态 `MoltenVK.framework` 放 .app 根 + 自写 `libvulkan.dylib` 前向层（5 个全局入口直转，`vkGetInstanceProcAddr` 包装留钩子位）。surface 走 `VK_EXT_metal_surface`/`vkCreateMetalSurfaceEXT`（老的 `VK_MVK_ios_surface` 已被 MoltenVK 删除）。
- **分辨率缩放与安卓不同**：CAMetalLayer 的 `drawableSize` 即渲染尺寸，compositor 自动拉伸到 layer bounds——**不需要**安卓那条"surface capabilities 拦截改 currentExtent"的钩子；MoltenVK 报告的 transform/extent 即 Metal 事实，identity-transform 钩子也无需对应物。
- **前后台语义**：进后台 = 窗口消失（fbsize 0×0，引擎当最小化停止渲染）+ 失焦释放按键；回前台重新宣布尺寸——对齐安卓 TERM/INIT。
- **键盘桥**：隐藏 UITextField（自动纠错全关）+ Done 工具条；delegate 把提交文本按 UTF-32 拆成 char 事件、空替换即 backspace、Done/Return 即 editor action（managed 模式 type 5）。硬件键盘走 UIKey（HID usage → GLFW 键码表在 main.m）。
- **坐标链**：UIKit 点 ×nativeScale=窗口像素 ×(渲染尺寸/窗口尺寸)=引擎像素，与安卓 scaleX/scaleY 同构。
- **M1 探针进宿主**：同一 app 内按钮触发（CoreCLR 单实例，跑过一次就拒绝），官方运行时包平铺 .app 根不变。

**当前状态（2026-11-21 真机验收，M2 原生半边全绿）**：`port17host-m2.ipa`（run 44903873394）在测试设备（iPad，Apple M4 GPU / Metal 4 / 8GB / 2360×1640@2x / iOS 27）上：
- **三角形全链路通**（GLFW 垫片→libvulkan 前向→MoltenVK 1.4.2→CAMetalLayer）：surface 2360×1640、BGRA8_UNORM、FIFO 呈现，锁 60.5 fps、1200+ 帧 0 失败；
- **触摸路径通**：原始触摸队列拖动顶点，坐标链（点×nativeScale×渲染比）正确；
- **软键盘桥通**（shown）；**.NET 探针回归通**：CoreCLR 就绪 → exit 42，**90.1 M 迭代/秒**（M1 基线 87.7 无回归）；
- **前后台语义通**：inactive/background 的 TERM 事件按设计送达。
- 真机实测注意：MoltenVK 1.4.2 在 iOS 上只给 FIFO 一种呈现模式（MAILBOX 不存在，偏好回退正常）；首测闪退的根因是 gipa 塞设备句柄（MoltenVK 把第一参解引用成 MVKInstance\*，已修并全文件审计）。

待办（M2 后半）：waylithaudio CoreAudio 后端、Slang/ImGui/FreeType/KTX/astcenc 的 iOS 构建、引擎程序集最小画面（M2 总验收线）。

**M2 后半开工（2026-11-21 晚）**：
- **正式包名定版 `com.samilry.port17`**（显示名 Port17）——数据容器绑包名，游戏资源将来经 Files app 放进本 app 的 Documents，一次放好不再迁移；探针 app（com.samilry.port17.probe）保持 M1 档案身份不动。
- waylithaudio 已移植（host/audio，CoreAudio 后端，`MA_ENABLE_COREAUDIO`），依赖源码版本与 sha256 沿用安卓仓库的钉（miniaudio 0.11.22 + extras stb_vorbis、stb@013ac3be、VMA 3.3.0，host/fetch-native-deps.sh 按 .fetched 标记 + CI 缓存）；宿主加「音频」自检按钮（走引擎 wa_\* API 面：建设备→PCM 正弦→voice 播放→位置推进断言）。
- vma（host/native/vma.cpp，vulkan 函数指针从 libvulkan.dylib 前向层填）与 stb（stb_image/write）一并进包——引擎三件先行库到位。
- **真机验收（run 44905361529 的包，2026-11-21 20:48 会话）全绿**：音频 `miniaudio 0.11.22 on Core Audio, 48000 Hz, 2 声道, period 512`，voice 位置 12800→86016 帧实时推进、2 秒整自动结束零卡滞；三角形回归 60.3–63.2 fps 零失败（中途多次 inactive/active 切换存活）；.NET 探针 90.9 M 迭代/秒。waylithaudio 编译要点：miniaudio 的 iOS 路径引 AVFoundation 头（AVAudioSession），必须 `-x objective-c` 编译并链 AVFoundation。
- **gli/KTX/astcenc 三库 iOS 化（run 44906116030，一次过全绿）**：libgli（header-only+glm 单元）、libKtx（libktx 4.4.0 读+转码子集，安卓文件清单与宏逐文件编译）、libportastc（astcenc 5.7.0 NEON、隐藏可见性）；fetch-native-deps 升 v2。宿主加「库」按钮：全部引擎 dylib 逐个 dlopen+解析已知符号的设备端装载自检。
- **Slang 2026.19 iOS 构建（run 44914866460 全绿，六轮修齐）**：官方源码 v2026.19（commit 0fb6b758）两段式——宿主 generators（macOS）→ iOS arm64 三件库（`CMAKE_SYSTEM_NAME=iOS` 手动喂参，iOS 无现成 preset）。产物名在 Apple 上：本体 `libslang-compiler.dylib`（源名带 0.2026.19 版本尾，收集时收净名），伴生模块 **`.so` 后缀**（Slang 自己的模块命名，运行时按名装载，保持原样）；代理 libslang.dylib 用**锚点函数**（取 slang_createGlobalSession 地址留下未解析符号）挂依赖。编译要点：照抄 android-base preset 的全套关停清单（GFX 不关的话 tools/ 的 gfx 块在 configure 期死）+ iOS 三特有：`CMAKE_MACOSX_BUNDLE=OFF`、`SLANG_ENABLE_SPLIT_DEBUG_INFO=OFF`（macOS 无 objcopy）、收集无条件执行（.built 标记只短路编译）。strip -S -x 后本体 26MB。缓存键 `slang-2026.19-0fb6b758-bv1`。
- **ImGui/FreeType 进包（原生清单 8/8 齐）**：平铺 C 面 `host/native/imgui_c.cpp` 从玩家 PC 副本（1.7.5014）本地生成——安卓同款生成器三输入（ImGui 1.92.2-docking 头的 clang AST + imgui.dll 导出表 1152 名 + Brutal.ImGui.dll 反编译参数表），**1146 包装/115 尺寸校验/0 未解析**；仅生成件入仓（反编译本体不进，红线内）。安卓验证过的两补丁（上游 1.92.3 矩槽复用修复、FreeType 线程锁）+ port_fonts 映射装载照搬。**关键旗 `-mms-bitfields`**：不是 x86 专用——强制 MSVC 位域布局规则，托管侧尺寸按 PC 版 imgui.dll 布局编译，iOS 默认 Itanium 规则下位域结构差 8 字节被尺寸校验当场抓住；安卓版一直带着这面旗。FreeType 2.13.3 静态链进 imgui_freetype。
- **M2 原生半边收官（2026-11-22，真机「库」自检 15/15 全过）**：修掉最后两环——`__asm__("名")` 别名在 Mach-O 原样落符号（无下划线）而 dlsym 自动补 `_`，iOS 侧编译用 sed 变换副本补下划线（生成件保持安卓同源）；构建自检的 `grep -q | nm` 在 pipefail 下把"找到了"误报成失败（grep 命中即退出→nm 吃 SIGPIPE），改用读完整个输入的 grep。**至此原生栈全链闭合**：垫片/Vulkan/MoltenVK/音频/vma/stb/gli/Ktx/astc/Slang/ImGui+FreeType 全部真机装载验证。**M2 唯一剩余：引擎程序集最小画面（总验收线）**。

**M2 收官开工（2026-11-22，引擎装载件 host/app/engine.c + 导入文档 docs/GAME-IMPORT.md）**：
- **run_engine**：安卓 host.c `run_runtime` 的 iOS 等义移植（16MB 栈线程上跑）。TPA = bundle 运行时 dll 优先 + `Documents/game/*.dll`（与 bundle 同名的框架副本跳过——安卓 add_assemblies 的 skipDir 去重；TPA 按简单名解析不受程序集版本影响，安卓 10.0.12 官方包跑 10.0.15-waylithclr 引用已证此路）。属性组照安卓 14 键，两处 iOS 事实：RUNTIME_IDENTIFIER=ios-arm64；`System.Globalization.Invariant=false`——ios-arm64 官方包**带** libSystem.Globalization.Native.dylib（链系统 libicucore.A.dylib），先按 PC 语义试 ICU，runtime.properties 可在设备端翻回 true。
- **环境变量**：GCgen0size=32MiB / legacyCorruptedStateExceptions=1 / Thread_DefaultStackSize=16MB（Darwin 默认线程栈 512KB，比 bionic 1MB 更紧）/ COMPlus_InterpMode=2（M1 契约）等全套 + `<game>/runtime.env` 覆盖钩子 + `<game>/runtime.properties` 属性覆盖（键去重逻辑照安卓）+ `<game>/args.txt`（默认 `-NoCrashDialog`）。
- **机器类型补写（必须）**：PC 副本里 `RimWorld.dll` 和 `com.rlabrecque.steamworks.net.dll` 是 AMD64 标记的纯 IL（其余 61 个游戏程序集是 I386/AnyCPU）——arm64 运行时拒载 AMD64 程序集。宿主 run_engine 里对 game/*.dll 做**原地** 2 字节补写 0x8664→0xaa64（幂等，安卓 Setup.MarkArm64 同款；本地 scripts/copy-list.py 双跑核实过机器类型分布）。
- **exePath = 宿主可执行**（M1 契约，本运行时包真机验证过；安卓传的是 game/RimWorld.dll，完整属性组下等价——engine.c 里一行可翻，路径类症状出现时先试这个）。
- **stdout/stderr**：stdout 经管道泵逐行上屏（+落 port17-ui.log，安卓 logcat 泵同构；覆盖层加 200KB 尾部裁剪防 UI 卡死）；stderr → Documents/coreclr-stderr.log（崩溃抢救文件沿用；结束时尾部 4KB 回放）。宿主自身的 host_log 回写 stderr 文件会造成回声——可接受（错误行总在最新位置）。
- **VulkanEx.dll 不做**：PC 侧原生 PE 只导出 vxDestroyBuffers/vxDestroyImages/vxDestroyBufferViews/vxDestroyImageViews 四个批量销毁函数；**安卓仓库没有它的任何对应物而游戏跑通**（全仓检索+git 史确认，perf-profile.js 的正则只是模块名分类）。真机若报 VulkanEx 的 DllNotFoundException 再补一个真实现（签名从 Brutal.Vulkan.dll 反编译确认）。
- **拷贝清单**：63 个托管游戏程序集 + Assets.zip + Version.txt + Data/ + Rendering/（规则 = 安卓 Setup.cs 取舍：托管、不在 ios 运行包里、非 .Native./DiaSymReader；scripts/copy-list.py 生成，docs/GAME-IMPORT.md 全表 + 传输步骤）。目录约定 `Documents/game/`。
- **zip 自动安装（host/app/unzip.c + scripts/pack-game-zip.py，用户要求的一次拖拽）**：PC 侧把 game-staging 打成 `port17-game.zip`（已压缩内容按 stored 存），拖一个文件进 Documents；宿主开 app 先安装再判引擎模式——自写 C zip 解压器（EOCD→中央目录→本地头定位数据；stored 直拷 + deflate 走系统 libz 的 raw inflate；zip64 不支持 <4GB 用不上；路径防 zip-slip），解压到 `.game-unzip` 暂存、验 RimWorld.dll 在（兼容 zip 里多套一层 game/ 目录的形态）、旧 game 挪 `game.previous` 保留、rename 就位、zip 改名 `.installed`（一份只装一次，留在原地当用户记录）。**主线程上解压放子线程 + CFRunLoopRunInMode 切片泵等**（launch watchdog 数的是 app 不伺候 runloop 的秒数，1GB 解压十几秒赌不起 20s 红线）；诊断模式的「引擎」按钮也先试安装（非主线程直做）。仓库 `game-staging/` 与 `port17-game.zip` 均 gitignored（红线：游戏文件不进 git）。
- **窗口后端反转（2026-11-22 反编译 PC 1.7.5014 的 Waylith.Windowing 坐实，本机 build/decomp）**：5014 引擎**没有 GLFW 后端**（全程序集无 glfw P/Invoke；Backend 枚举 = Headless/Offscreen/Wayland/X11/Win32/MacOS/**Android/IOS**），AutoCandidates 按 OS 选后端，iOS → Backends.IOS。安卓的 GLFW 垫片是 ≤1.7.4998 时代引擎还用 GLFW 时的产物（安卓仓库只跟到 4998；4999+ 换成原生后端，其 Android 后端要求 Ludeon 自家的 `com.ludeon.waylith.WaylithActivity` JNI 注册——正是安卓移植垫片顶掉的位置）。**iOS 后端的事实**（decompile 直读）：`IOSPlatform` 构造要求 `NSThread.IsMainThread` 否则抛异常；`RunCore` **自己调 UIApplicationMain**（WaylithApplicationDelegate，运行时 ClassBuilder 注册）走 scene 生命周期（configurationForConnectingSceneSession → WaylithSceneDelegate → UIWindowScene + 一窗一 scene）；RequiredVulkanInstanceExtensions = `VK_KHR_surface + VK_EXT_metal_surface`（与我们的三角形同一条 Vulkan 路，经 Brutal.Vulkan 的 `NativeLibrary.Load("vulkan")` 落到 libvulkan.dylib→MoltenVK）。
- **OS 探测补丁（两处硬墙，scripts/patch-os-detect.py，PC 上对 game 副本原地打）**：① `Brutal.Vulkan.dll` 的 Vulkan 静态构造只按 IsWindows(vulkan-1)/IsMacOS(**VulkanEx**——PC 版 vulkan/ 目录与 VulkanEx.dll 之谜的谜底：Mac 版的自带 Vulkan 装载层)/IsLinux(vulkan) 选库，iOS 三假 → `Dll=0` → NullReferenceException("Failed to load Vulkan library")；② `Brutal.Core.Common.dll` 的 `Platform` 静态构造三族全不中 → PlatformNotSupportedException("Unsupported platform")。两文件各**只有一个**目标调用点（ilspy 核实：两 dll 的 OperatingSystem.Is* 全部位于目标构造内），补丁 = `28 <token>`（call）→ `17 00 00 00 00`（ldc.i4.1+nop×4）同宽原地改写，dnfile 解析 MemberRef 定 token（System.OperatingSystem 父类核验），幂等 + 写 `os-detect.port17` 戳（宿主读戳提醒没打补丁的拷贝）。**家族选择 = MacOS**（不是安卓的 Linux 谎）：Darwin 真相走对 kqueue 文件监视（Linux 族会碰 Darwin 没有的 inotify）、引擎自己的 `MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS=0`/`MVK_CONFIG_RESUME_LOST_DEVICE=1` 也在这条路上自配；代价是 EngineApplication 里多一次 `NativeLibrary.Load("vulkan.1")`——bundle 附 **libvulkan.1.dylib**（前向层第二副本）满足它。Mac 族还会设 VK_DRIVER_FILES 指向不存在的 Resources/vulkan/icd.d——我们的前向层不读 ICD 变量，无害。补丁验证：本地打完反编译见 `if (true)` 精确落位两处；Brutal.Vulkan 的 IsLinux→true 只动装载库选择（"vulkan"→我们的前向层），VulkanEx 分支照旧不进（真 IsMacOS 在 iOS 为假）。此补丁形态是 M3 patch.dll（ Cecil 全量平台改写）的第一块砖。
- **宿主架构随之改（引擎模式）**：Documents/game 在 RimWorld.dll → main() **主线程直接 run_engine**，宿主自己的 UIApplicationMain/HostVC 退位（一进程一次 UIApplicationMain，主线程是 iOS 后端的硬要求）；引擎返回（失败或游戏退出）再落回诊断界面（ui.log 回放摆上屏）。为此 Info.plist 开 `UIApplicationSceneManifest` 空 dict（scene 生命周期，引擎的 delegate 按连接配 scene），诊断 UI 也迁到 SceneDelegate（清单在而窗口不走 scene = 黑屏）。日志 sink 提前到 main() 装（引擎期行进 port17-ui.log，UI 部分排队等回落后补上屏）。**代价：主线程栈 = 系统默认（约 1MB）**，M1 的 16MB 规律在这里结构性不可得（UIKit 主线程不可换栈；PC 版主线程 1.5MB）——游戏主线程深递归溢出风险记入开放风险，M2 验收看装载/首屏是否踩线。诊断模式「引擎」按钮保留：在 16MB 线程验证装载链全段（TPA/CLR/程序集/引擎 Main），到窗口后端处预期干净报错（"UIKit must run on the process main thread" → "No windowing backend could start"）——这条红字本身就是"引擎托管代码真的跑起来了"的证明。
- **GLFW 垫片的去留**：5014 引擎原生路径不经过它；保留（三角形验证、M4 PortMobile 的 glfwAndroid* 输入注入面是否仍需要由 M4 决策——引擎 iOS 后端自带触摸/手势/键盘处理，触摸适配可能改走 Harmony 层注入引擎输入事件）。
- **UI**：「引擎」按钮（首键）：先 `triangle_stop()`（三角形渲染循环加 g_stop 对外入口 + 完成等待 + glfwDestroyWindow/glfwTerminate 归还垫片单窗口槽；早退路径也归还，否则引擎撞"only one window"红鲱鱼）再起 16MB 线程。与探针互斥提示（CoreCLR 单实例）。
- **M3 输入（PC RimWorld.runtimeconfig.json 的 20 个 WaylithClr 开关全表，本路线经 coreclr host API 不读 runtimeconfig 故全不生效，裸引擎行为差异归这里）**：AssemblyNameRedirects（UnityEngine*=UnityStubs 等映射）、FieldInjection（+GCDescReserve=4）、LaxAssemblyBinding、SkipAccessChecks、LaxDynamicMethodAttributes、AllowInitOnlyStaticWrites、LaxCallvirt、LaxAssemblyArchitecture、LaxTypeRefResolution、LaxMemberSignatures、DynamicMethodOwnerIsDeclaringType、CatchableAccessViolations（已用 env 近似）、JitNoInlinePatchableIntoDynamicMethods、JitPatchableAssemblies=Assembly-CSharp、LaxInterfaceDispatch、IsolatedLoadsResolveByName、LaxPrivateFieldNames、MonoStringHashCode、MonoFloatFormatPrecision、LaxInheritedFieldRefs、MissingAssemblyTypeLoadException。安卓 patch/Patch.cs 的 AddLudeonMembers/AddLudeonSwitches（在 CoreLib 里补成员/开关）是 iOS 对 11.0 重移植的参考。

**引擎真机连载（2026-11-22，m2-engine-3…13 十轮，从"程序集能不能跑"打到 M2/M3 边界）**：
- **设备内 setup 成立（安卓 SetupService 的 iOS 对应物，setup/ + host/app/setup.c）**：玩家 Steam 原件直接拷进 Documents（或拖 `pack-game-zip.py --raw` 的 zip 自动解压安装），宿主发现没有改写戳就在本进程开 CoreCLR 跑 Port17Setup.dll（iOS 无 fork，单实例配额花在 setup 上），完成后 4 秒自动退出、重开即引擎。五道改写全 Cecil、全幂等（复跑 0 改 N 留，本地 + 真机双验）：OS 探测、RegisterPosixSignal→null、ProcessLifetime Hook/Dispose 哑化、ConfigureRuntime 路径源改 CurrentDirectory、机器类型补写。版本化戳 `port17-setup.vN.stamp` 让每升一版自动增量重跑。**幂等是硬要求**：真机上 v4 的文件要被 v5 重跑，每道补丁都认得出自己已改的形态。
- **三到五轮的墙全在 Waylith.dll 的优雅退出一族**：RegisterPosixSignal（iOS 无 PosixSignalRegistration）→ null；删掉订阅后 Console.add_CancelKeyPress 同族再炸（System.Console 在 POSIX 用同一个不支持 API）→ 订阅 IL 删除；再后 AppDomain.ProcessExit += 触发 Delegate.Combine→InternalEqualTypes 的**原生 SIGSEGV**（无 Stack overflow 字样，非溢出形态）→ Hook/Dispose 整体哑化（这一族只服务 Ctrl+C/SIGTERM 收尾，iOS 无此交互）。安卓 Patch.cs 改写表同名条目的 iOS 落位。
- **六到七轮：黑屏无事发生 = scene 被交给了诊断界面的 SceneDelegate**。UIKit 按 Info.plist 的 UIApplicationSceneManifest 把 window scene 连给我们的类；引擎 IOSPlatform 的 RunCore 自调 UIApplicationMain（runtime ClassBuilder 注册 WaylithSceneDelegate）在宿主 UIApplicationMain 退位后接管——探测日志坐实"scene 到了错误的 delegate"。修法：我们 SceneDelegate 的 willConnect 里检测 `NSClassFromString(@"WaylithSceneDelegate")` 在（= 引擎托管代码已注册），实例化并转交（scene.delegate = 引擎 delegate + 转发 willConnect）。⚠️ 转交打点行在日志出现 = 手术生效。
- **八轮：Assets.zip 解析到 .app 根**。`Environment.ProcessPath` 在 iOS 读真可执行路径（`_NSGetExecutablePath`），host API 的 exePath 影响不到它；宿主 setenv 的变量 managed 侧 `Environment.GetEnvironmentVariable` 也看不到（Apple 移动上 CoreCLR 不认宿主侧 setenv 的那套）。修法：Cecil 把 ConfigureRuntime 里 `get_ProcessPath`/`GetEnvironmentVariable("PORT17_GAME_DLL")` 改道 `Environment.CurrentDirectory`（宿主 chdir 过 game，getcwd 可靠），后随的 `Path.GetDirectoryName` nop 掉保栈平。env 方案（v3）在此平台不可用就是这两条的实证。
- **十轮（m2-engine-13）：`WaylithClr.FieldInjectionBatch` TypeLoadException——M2/M3 的正式边界**。Bootstrap.Initialize 第一步 PrepatcherCompat.Install → Waylith.Injection.Injection 静态构造：静态字段 `ConcurrentDictionary<FieldInjectionBatch, Action<FieldInjectionBatch>>` 在 cctor 体执行前就强制加载类型。WaylithClr.* 是 Ludeon 定制运行时（10.0.15-waylithclr）加进自家 System.Runtime 的私货，官方 11.0 没有，引用按简单名统一到 11.0 后按类型名找→炸。**改写调用绕不过（类型加载先于一切），解法 = 占位类型让引擎自己降级**（安卓 TECHNICAL.md 3.3 末行的路线）：`if (!FieldInjection.IsSupported) { InjectionLog.Error("turned off…accessors will throw"); return; }` 是引擎现成的关闭分支。**落位（2026-11-22）**：`hooks/WaylithClr.cs`（安卓 hooks/WaylithClr.cs 同源 587 行 + 5014 新增的 `MethodBodyUpdater.GetDefinitionHandle(MethodBase)→RuntimeMethodHandle`，安卓版跟 4998 还没有）编成 **PortHooks.dll** 进 .app 根（平铺即进引擎 TPA）；setup v5 新增 RetargetWaylithClr——把 scope=System.Runtime 的 WaylithClr.* TypeRef 改道 PortHooks（安卓 Patch.cs 的 10 行手术；先按 "WaylithClr" 字节扫 #Strings 快筛再 Cecil 打开，System.* 前缀跳过）。**本地三重验证**：5014 引用面扫描（恰好 3 个引用者：RimWorldWaylith.PrepatcherCompat / Waylith / Waylith.Injection，33 处 TypeRef、103 处成员引用，全部 scope=System.Runtime）；Steam 原件全量 setup 试跑（改道 3 程序集 33 处 + 复跑幂等 0 改）；**逐成员解析校验**（scripts/scan-waylithclr 第二参数模式：改道后的每个成员引用真实 Resolve 到 PortHooks，103/103 全绿——这就是运行时装载行为的机械证明；游戏每轮更新后都该重跑它对替身面）。

**十七跑：游戏本体在真机跑通（2026-11-22，m2-engine-20，run 44998324091）**：v7 setup 真执行（5 秒 40 处改写）→ 引擎 boot 快路径 → 菜单 30fps 稳定 → **15:51 创建新世界（全部 6 个 DLC）** → 世界生成+进入游戏 12-30fps 持续运行、游戏音效在响（7-10 voice）、会话 17 分钟零崩溃。遗留小项：boot 中段一次 45 秒停摆（首轮贴图缓存构建，自愈）；Application.OpenURL 不支持（wiki/背景故事链接，无害）；M2 验收线（引擎最小画面）已超额完成——**M3/M4 的性能与 mod 工作直接在可玩基线上展开**。M3 性能关键事实：游戏当前纯解释器执行（崩溃栈中 Vector128.ShuffleFallback = 内建未内联化的铁证；安卓是 JIT），设备有 RWX 授权，官方混合包开 JIT 是下一个大项。

**JIT 真机可用确认（2026-11-22 晚，m2-engine-21，runtime.env 实验首轮）**：InterpMode=0 + ReadyToRun=0（runtime.env 两行，覆盖内置默认）→ 纯 JIT 配置下游戏稳定运行 3 分钟：主菜单、读解释器会话创建的存档（跨模式存档继承 ✓）、3D 定位音效、零 W^X 死点。**源码语义钉死（runtime-110 eeconfig.cpp）**：InterpMode!=0 才开解释器（0=纯 JIT）、>=2 时 R2R/解释器互斥（M1 用 2 的原因）、!=0 还会关分层编译（0 时 TieredCompilation+PGO 正常）。**帧率没变（菜单 +450/15s=30fps）的原因**：引擎经 CADisplayLink 按窗口 PreferredFrameRate 钉帧率（诊断三角形的 328fps 证明硬件能力远高于此）——菜单帧率区分不了两种执行模式；JIT 收益在 CPU-bound 阶段（def 装载、存档解析、殖民地 sim tick、寻路），需 A/B 定量（同存档两模式各跑，比载入秒数 + 帧心跳分布）。jit.on 开关已进宿主（engine.c，Files app 可建空文件切换）。

**M2 总验收线已过（2026-11-22 十一跑，m2-engine-14，run 44986348215 的包）**：WaylithClr 替身生效——引擎完整跑过 Bootstrap/PrepatcherCompat（降级日志按预期）、自建窗口、Vulkan 全链、**主菜单可见可操作，用户推进到创建新世界**。引擎会话持续 14 分钟后死于 iOS 的 GPU 后台限制（详见下一条）。同轮暴露的次级问题：GpuProbe 的 slang 装载失败 → GPU 档降保守（根因见十二跑）。

**十二跑诊断（m2-engine-14 日志，熄屏闪退链）**：世界生成装载中熄屏/后台 → iOS 拒绝执行在途 Metal 命令缓冲（`kIOGPUCommandBufferCallbackErrorBackgroundExecutionNotPermitted`）→ MoltenVK 判 `VK_ERROR_DEVICE_LOST`（**v1.4.2 与上游 main 同：`handleMTLCommandBufferError` 的 `NotPermitted` 分支把 markDeviceLoss 强制 true，无视 `MVK_CONFIG_RESUME_LOST_DEVICE`**——引擎自配的该开关救不了，MoltenVK 无配置可绕）→ 引擎按致命退出，退出路径 `RenderThread.Stop()` 再撞 vkDeviceWaitIdle 同错 → UIKit 循环未处理异常 → FailFast。**修法（十二跑包）**：
- **main.m scene 转交时持有 UIKit 后台任务断言**（`beginBackgroundTaskWithExpirationHandler`，过期干净归还 + 回前台重挂）：iOS 给 ~30 秒"完成在途 GPU 工作"窗口，正是 Metal 后台的标准姿势；引擎自己的 HoldBackgroundTask 只在抑制节流时才拿，装载期是空的。显示链接在熄屏即停（引擎后续不再提交），唯一在途的那帧有窗口完成。
- **build-ipa.sh 给 slang 四件钉 LC_RPATH @executable_path**：dyld 对主程序自己的 @rpath 依赖有"可执行文件目录"兜底（Port17Host 无任何 LC_RPATH，libglfw 照样装载——本地解包 Mach-O 证实），**对 dlopen 链上的库没有**——`NativeLibrary.Load("slang")` 命中 libslang.dylib 后其 `@rpath/libslang-compiler.dylib` 只试了 .app/Frameworks/ 就放弃。此修复同时解开 GpuProbe（GPU 档）与运行时着色器编译（世界生成的 compute kernel 需要——slang 死则后续 kernel 编译必炸）。

**十二/十三跑（m2-engine-15/16，2026-11-22）**：两修复验证生效——slang 装载通（GpuProbe 215ms 实测 2327 GFLOP/s 不再降保守档）、boot 13 分钟（Assets.zip 首解缓存）→3 秒。**新回归：卡 Initializing**（用户对照实验：m2-engine-14 的包可玩、15/16 卡——15 相对 14 仅两变量：slang 装载修复 + 后台断言，头号嫌疑 slang：装载成功后装载期 ~30-45s 处**主队列卡死**（💓 主 runloop 心跳 14:06:01 起停跳、引擎帧计数停 740、进程活——音频线程心跳持续）；GpuProbe 自己能编 kernel，死点在更深的运行时着色器编译路径）。**处置（十四跑包）：slang 恢复"不可装载"已知好态**（依赖留 @rpath 不解析，GpuProbe 降保守档、游戏着色器走预编译 SPIR-V 不受影响）；**加帧停摆看门狗**（waylithaudio 心跳线程：前台连续 4×15s 零帧增量 → abort → COMPlus_EnableCrashReport 的 .NET 崩溃报告把全部线程托管栈写进 stderr——卡死从此自带诊断数据；后台不算停摆，宿主经 wa_port17_set_active 通报前后台）。M3 议题：slang 死点定位（看门狗栈转储）→ 修复后恢复 @loader_path 装载修复（配方在 build-ipa.sh 注释里）。

**引擎首跑情报（2026-11-22 真机，run 44973988560 的包）**：
- **托管 Main 已在真机执行**：目录自动识别（用户文件夹名 " game" 带前导空格 + folder-app 数据容器路径 `Documents/Data/Application/<UUID>/Documents/`——SideStore folder-app 的 NSHomeDirectory 即该数据容器，文件 app 的 Port17 同源）、TPA 245（运行时 182 + 游戏 63）、CoreCLR 就绪、`RimWorldWaylith.Program.Main` 进入。
- **.NET 11 不拒 AMD64 标记的纯 IL**（机器补写 0 个也装载执行）——安卓 10.0 的拒载行为在 11 不复现，MarkArm64 变成无害保留。
- **死点一：`Bootstrap.ResolvePersistentDataRoot`** 三路分支（IsOSPlatform(OSX)/IsOSPlatform(Linux)/兜底=Windows），iOS 全假掉兜底 → `SHGetKnownFolderPath`（P/Invoke shell32.dll）→ DllNotFoundException → SIGABRT。**修法 = host/stubs/shell32.c 返回非 0**（该函数自带失败 fallback：`UserProfile/AppData/LocalLow`，落在容器里可写）——文件名精确 `libshell32.dll`（无 .dylib：Darwin 的 dlopen 不去 ".dll" 后缀，runtime 试名就是原样）。
- **死点二（非致命）：Steamworks.NET** `SteamAPI_RestartAppIfNecessary` DllNotFoundException——被 SteamManager 吞掉但每次刷十几行 dlopen 狩猎日志。**修法 = host/stubs/steam_api64.c**（安卓同款静默版 → libsteam_api64.dylib）。"Used UnityData before it's initialized" WARN 无碍继续。
- **Cecil 平台改写管线成立（2026-11-22 二跑后，scripts/patch-platform/，用户点名对齐安卓路线）**：第二个替身包（shell32/steam）后引擎死于 `PosixSignalRegistration.Create` PlatformNotSupportedException（`Waylith.Diagnostics.ProcessLifetime::RegisterPosixSignal`——被我们 Platform.IsMacOS=true 补丁领进 iOS 不支持的 API；安卓 Patch.cs 改写表的第一条正是它）。立起 Mono.Cecil 0.11.5 的改写器（`dotnet run --project scripts/patch-platform -- <game>`），**安卓 Patch.cs 的实测配方**（AssemblyResolver 指 game 目录 + ReadingMode.Immediate；InMemory 路径有 PatchRawCode 空引用坑；写临时文件再替换防截断——直写中途崩会把原文件清成 0 字节，staging 的 Waylith.dll 曾因此从 Steam 重拷）。首条改写：RegisterPosixSignal → `return null`（反编译验证落位；Windows 一直走的路径，字段可空 Dispose 全 ?.）。**pack-game-zip.py 自动串** patch-os-detect + patch-platform，用户侧维持"一条命令打包"。**这副骨架就是 M3 patch.dll 的 iOS 形态**：后续每个 iOS 硬墙往 Program.cs 的改写表加条目（null 化/调用替换/字符串改写——安卓 StringRewrites 的字体路径表迟早也要来）。

**二十八轮（2026-11-22 深夜，0.2.6）：Files 文件夹结案暂缓 + JIT 授权工程化（M3 开工）**：
- **文件夹之谜的 plist 层彻底排除**：设备上被服务 plist 比源 plist 多出的 `CFBundleURLTypes`/`UTExportedTypeDeclarations`/`CFBundleNumericVersion` 全部来自 SideStore 重签管线（ResignAppOperation.swift：给每个 app 注入自家 URL scheme 第 85-94 行 + "AltStore Installed App" UTI 第 169-178 行 + InfoPlistParser 整体重写）——StikDebug/DR3/SDViOS 走同一条流水线，键集合无差异可言。三个阳性对照（StikDebug、DR3 Mobile、SDViosport 的 SDViOS）plist 互不相同却都出文件夹。**新假说=容器身世**：`UIFileSharingEnabled` 是 0.2.3 才进 plist 的，而 SideStore 同 bundle id 重装=升级（数据容器保留，跨轮存档继承即是证明）——容器首次创建（0.2.0，无该键）时烙下的"不共享"元数据重装/重启都不刷新。**判别实验（待做）**：彻底删除 app（连容器）→ 同一 IPA 全新安装 → 查 Files。**兜底已进包**：诊断界面「导入 zip」（UIDocumentPicker，asCopy 拷成 port17-game.zip 接现有自动安装）+「导出」（Documents/AppData 打包 port17-saves-backup.zip（unzip.c 新增 STORE 型 zip 写端 port17_zip_tree，读端的镜像）+ port17-ui.log 走 UIActivityViewController）+「下次诊断」保持开关（port17-diag.on，引擎让路）——文件夹从此不是依赖。
- **JIT 授权判定工程化（engine.c）**：授权=可测量的两件事——`csops` 自查 CS_DEBUGGED（xnu cs_blobs.h 0x10000000；MeloNX checkDebugged 同款）+ 进程内双映射探针（mmap RX → vm_remap RW 别名 → vm_protect → 写 `mov x0,#42;ret` → icache → 走 RX 调用，sigsetjmp 兜底）。两者皆过 → **自动** InterpMode=0+ReadyToRun=0；否则解释器；jit.on 仍可强制（未授权时运行时退回解释器执行——21 轮实测形态，不崩但无收益）。**21/22 轮困惑的根源就此关掉**：21 轮 runtime.env 的 InterpMode=0 生效但无授权（跑的是解释器回退），22 轮 LiveContainer 拉起连 env 都没带过去（生效模式=解释器）——"生效执行模式"行从此携带授权检测行，两嘴合一。
- **为什么双映射是正路（证据链）**：官方 ios-arm64 libcoreclr 导 `_vm_remap`+`_sys_icache_invalidate`（无 `pthread_jit_write_protect_np`——iOS 不导出）；`IsWXORXEnabled()` 在 Apple arm64 无条件为真（runtime-110 executableallocator.cpp）——**运行时自己的 JIT 代码页就是双映射**。iOS 26+/TXM 拒绝的是自建 RWX 的执行（M1 注释正确），但双映射在 CS_DEBUGGED 下成立——MeloNX（M4 级 TXM 设备实测，`jitEnabled() = checkDebugged() && attemptToMapDualMapping()`）。universal.js 的 `_M`/brk 合作协议是 Mono/arena 需求（STS2 线），CoreCLR 不需要。
- **StikDebug 会话脚本重写（probe/stikdebug/port17-jit.js）**：修掉 vAttach 后双重 `c`（对齐 universal.js 的单 c 循环）；停机信号原样递送（`vCont;S`）——CoreCLR 的 SEH 靠 SIGSEGV 实现 NRE，吞信号=卡死游戏；stop 包寄存器值小端字节序修正。拉起流程 = StikDebug 会话先 spawn app 再 vAttach → CS_DEBUGGED 在我们第一条指令前就位。**用户侧前置**：配对文件 + LocalDevVPN 回环 VPN + WiFi（StikDebug README）。

**二十九轮（2026-11-22 深夜三更，0.2.7）：JIT 首航黑屏——探针执行段在 attach 态不可 fault，重做为纯分配链探针**：m2-jit-1 首次 StikDebug 会话拉起：日志停在「引擎启动」横幅后一字不出（连授权检测行都没到），黑屏不回落诊断界面＝进程卡在异常现场。死点定位＝0.2.6 探针的**执行段**（`mov x0,#42;ret` 走 RX 调用）——**attach 态下 mach 异常先送 debugserver，进程内 SIGSEGV 处理器（sigsetjmp 兜底）永远不跑**，fault 即卡死（M1 fault-loop 教训的重犯：那次是探针 fault→停→脚本 c→再 fault 无限循环）。0.2.7 探针重做：**全程不可 fault**——只验分配链（mmap RX→vm_remap 别名→vm_protect RW→经别名写、经 RX 读回比对），每步即时落日志（死点要留名）；执行可行性交运行时本体裁决（其失败形态是 21 轮实测的解释器回退，不崩）。授权检测行也拆开：CS_DEBUGGED 先打、探针各步自打。若 0.2.7 探针链路全通而运行时自身在代码页执行上 fault-loop（脚本窗口将刷 stopped 行），则转 M1/STS2 竞技场线（debugserver `_M` 代分配）。

**三十轮（2026-11-22 深夜四更，0.2.7 真机裁决）：官方 iOS 包 = 解释器专用构建，JIT 物理不可能——M1"混合构建"结论正式推翻**：m2-jit-2（StikDebug 会话拉起 0.2.7）：授权链三行全绿（`CS_DEBUGGED=1` → `探针链路通（RX/RW 别名一致=1）` → `生效执行模式 = JIT（InterpMode=0）`），0.2.6 黑屏修复验证 ✓——**但装载停摆与解释器逐秒一致**（对照 m2-engine-20：两轮都是 +397/+382、+332/+367、**三连 +0 共 45 秒**、+66/+82 恢复窗——def 装载是纯托管 CPU 活，真 JIT 不可能一秒不差）。源码+二进制双重定位：vanilla `eeconfig.cpp` 里 InterpMode=0 且未设 `DOTNET_Interpreter` 时解释器仍运行的解释唯一剩 `!FEATURE_DYNAMIC_CODE_COMPILED`；二进制判据 `"Failed to load JIT compiler"`（UTF-16，DCC=1 必有/DCC=0 编译出局）：**安卓官方包=1（判据阳性对照，安卓实测 JIT 可用）vs iOS 官方包=0**。`jitinterface.cpp UnsafeJitFunction` 结构：解释器一旦装载即接管一切编译请求；JIT 路径失败是逐方法抛 InvalidProgram（游戏会坏，不会慢）——"以解释器速度稳定运行"只有解释器一条路。libclrjit.dylib（真 RyuJIT arm64，Clang 17 构建）是包清单随附件。**JIT 的唯一出路 = M1 自建 runtime 线（--dynamiccodecompiled true，封存于 53f7a4a 单 remap 改造后、真机验证前）**。附带观察：设备 runtime.env 残留 21 轮的 `DOTNET_JitDisasm=Program:Main`（checked-build 专属，release 无害）。

### M3 游戏程序集上机
- `patch.dll` 增加 iOS 目标（平台检查改写、Harmony 适配 darwin）；

- PortHooks 的 WaylithClr.* 替身已随 M2 收官进包（见上）；剩 CoreLib 补丁对 11.0 CoreLib 的重移植（20 个 `WaylithClr.*` 开关，安卓版是对 10.0.12 做的）与 PortHooks 的启动钩子/UnityStubs/ModPrep 件；
- `libcoreclr.dylib` 二进制补丁的 iOS 版（安卓版是 `libcoreclr.so` 两处）。
**验收**：玩家导入的游戏副本在真机进主菜单。

### M4 托管层共享
- 从安卓仓库共享 PortMobile / ModPrep / PortWorkshop 源码（初期：同步脚本复制；稳定后：git subtree 或抽出公共包）；
- 触控、界面适配、mod 导入排序、创意工坊下载器（后台下载须前台亮屏，iOS 无前台服务）。

### M5 玩家可用 beta
- 导入流程进程内化（iOS 无 fork，PortSetup 不能独立进程）；
- 内存整理策略对齐 jetsam（8GB 设备前台约 6–7GB 上限，无 swap、超限即杀）；
- 玩家文档（SideStore 安装、StikDebug 开 JIT 步骤）。

## 开放风险

| 风险 | 影响 | 缓解 |
|---|---|---|
| 引擎主线程跑在系统默认栈（约 1MB，UIKit 硬约束） | 游戏主线程深递归（PC 版按 1.5MB 编）可能溢出 | M2 验收看装载/首屏；真溢出时 M3 评估（managed 侧减递归或 libcoreclr 侧处理） |
| iOS 大版本升级破坏 StikDebug JIT | 玩家无法开 JIT，游戏不可玩 | 解释器变体兜底（性能存疑）；跟进 StikDebug 更新 |
| 11.0 CoreLib 与 10.0.12 行为差异 | `WaylithClr.*` 开关模拟面变大（Ludeon 改的是 10.0 运行时，标准 11.0 与标准 10.0 本身就有差异要一并模拟） | M3 逐开关核对；必要时评估 cherry-pick Apple mobile 支持回 release/10.0（工作量大，仅当差异不可收拾时） |
| `--dynamiccodecompiled true` 的 iOS 构建微软 CI 不测 | 编译或运行时意外 | M1 就是为它而来；坏则逐个修 |
| MoltenVK 与引擎用的 Vulkan 特性面不匹配 | 渲染故障 | M2 早期跑引擎最小渲染验证 |

## 运行时 pin 升级规程

1. 改 `runtime-pin.txt` 第一行（commit 全 sha 或 `v11.0.0` 等 tag）；
2. push 后 Actions 自动重建 runtime 缓存（新 key）；
3. 在 PR/commit 里记录新 pin 对应的日期与来源分支。
