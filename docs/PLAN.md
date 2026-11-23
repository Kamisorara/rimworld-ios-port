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

**三十一轮（2026-11-22 深夜五更，0.3.0）：JIT 线复活——混合运行时 + 宿主竞技场合流**：方向裁决后按四阶段路线开工。**阶段一（混合构建）**：CMake 门控（静态链接块里解释器仅非 DCC 链入——JIT 占静态位、解释器作为独立 dylib 按需 dlopen，getJit 8 符号冲突从根上消失）；`StaticInitialize` 的 W^X 强制改为仅 `InterpMode==0` 生效（解释器会话=官方包同款零竞技场零握手，JIT 会话才走 `PORT17_JIT_*` 竞技场）；`build-runtime.sh` INTERPRETER=0→1（显式定义避开生成器表达式默认值）；collect 抓 `libclrinterpreter.dylib`；缓存键 c18/c14/c13。**阶段二（宿主合流）**：`host/app/jit26.c/.h`——M1 探针宿主的竞技场获取全套移植（brk 握手→vm_remap RW 别名→detach→无调试器探针 42/43→深页抽查→PORT17_JIT_* 交接，逐行保留 eb1ab5f 终版形态）；engine.c 模式门重写（竞技场就绪→自动 InterpMode=0；否则 InterpMode=2；`DOTNET_InterpreterName` 绝对路径指解释器 dylib——裸名 dlopen 不搜 bundle 目录）；setup.c/探针 host.c 补同一 env（混合运行时下每个 coreclr_initialize 位点必须显式设 InterpMode，未设=0=强制竞技场=无交接即 E_FAIL）；build-ipa.sh 运行时目录参数化（`PORT17_RUNTIME_DIR`，默认自建混合套）+ jit26.c 入编译清单；新增 `scripts/fetch-hybrid-runtime.sh`（从 m1-probe 的 Release 资产取运行时、managed/ 摊平）；m2-host 切换运行时来源。**脚本**：rw17-jit26.js 信号停机从 fail() 改 vCont;S 原样递送（0.2.7 教训：SEH 靠 SIGSEGV，吞=卡死、会话失败=停死黑屏）；port17-jit.js 加 brk 跳过（pc+4，混合 app 配错脚本时优雅降级而非无限重执行）。**时序纪律**：阶段二推送必须等 m1-probe 绿（m2-host 从 Release 取运行时，推早了会打包旧纯 JIT 资产）。

**三十二轮（2026-11-23 凌晨，0.3.1）：JIT 首航真机——竞技场全链贯通至 runtime 收货，SIGBUS 死在"执行走 remap 副本"**：m3-jit-1（0.3.0 首个混合包 + StikDebug/rw17-jit26.js 会话）：**项目首次实证调试器授予页在 iOS 27 真机可执行**——brk 握手 3.4 秒拿到 128MiB（_M 分配+逐页祝福，脚本 v3.x 批量备页比逐包 M 快 20 倍）、别名就绪、detach、探针 42/43 双过、深页三档一致、`PORT17_JIT_*` 交接；runtime 侧 `[rw17-arena] host arena adopted` + `JIT26 backend done` 后 **8ms SIGBUS**（无托管帧、无 reserve/commit 失败日志）。死因定位：**eb1ab5f 终版 Reserve 给每笔保留开新 vm_remap 别名、执行走 remap 副本**——M1 旧注释与 STS2 铁律都点名过"remapped copies are not proven executable"，53f7a4a 曾修回"执行走授予页本体"，但后来的 in-place-flip 重构又把执行视图换回了 remap 副本，而 M1 封存前**这个形态从未上过真机**。Darwin 上执行保护违例呈 SIGBUS，与位置、无声失败（vm_remap/mprotect 都成功返回）全吻合。**0.3.1 修法**：Reserve 改为对授予页的**纯指针算术**（兑现它日志里 "allocation is pointer math" 的原话）；写路径本就全走 GetRWMapping 短命 RW 别名不动；Commit 的 in-place 翻转落到授予页上——代码页本就 RX 永不翻、只有 loader 堆数据翻 RW，内核若拒就有 errno 日志的干净失败（本 build 的开卷问题）。缓存键 c19/c15/c14。dmcheck 垫片补 TargetConditionals.h 与 vm_protect。附带观察：设备 runtime.env 仍残留 `DOTNET_JitDisasm=Program:Main`；jit.on 仍在游戏目录（21 轮遗留——普通启动也报 JIT 模式，建议删掉）。

**三十三轮（2026-11-23，0.3.2）：m3-jit-2 真机——JIT 全链贯通至引擎生命周期深处，SIGSEGV 根因 = 宿主深页抽查自埋 "JUNK"**：0.3.1 的指针算术修复生效（`CoreCLR 就绪`，上轮死点已过），托管代码在 JIT 下大规模正确执行 500ms——Program.Main → EngineApplication.Run → IOSPlatform.RunCore → UIApplicationMain → 音频 CoreAudio → Vulkan 实例+设备（Metal 4/Apple 9 实测）→ WillEnterForeground 前台回调触发；死于 `RaiseNow +0x23 → Deliver(EventRecord ByRef)` 的调用指令处（SIGSEGV=11，与上轮 SIGBUS=10 已不同）。**.ips 裁决**：`KERN_INVALID_ADDRESS at 0x000000004a554e4d`——ASCII 解码 "JUN"，正是宿主深页抽查写的标记 `0x4a554e4b`（"JUNK"）+2 拼出的假指针：标记写在竞技场 +4MiB/+32MiB/尾部，runtime 按偏移分配越过后落进活结构的指针槽，间接跳转读到 `4e 4d`+零页 = 0x4a554e4d。M1 探针时代 Hello.dll 分配到不了 4MiB 故从未踩响。**整套竞技场/JIT 机制本身已被证明工作正常**——H1 分层编译等假设无证据，全部撤销。0.3.2 修法：删深页抽查块（一致性早被探针 #1/#2 端到端证明：经 RW 别名写码、经竞技场本体执行）；纯宿主一行级提交，不动 runtime 补丁/脚本/缓存键。

**三十四轮（2026-11-23 二更，0.3.3 修正）："GA pin" 判断撤销——真因 = collect 回退链缺 net11.0-unix**：用户追问"GA 对比 11-05 更新了什么"触发了复核——compare API 返回 behind/total 0，日期实锤：**GA tag v11.0.0 的提交在 10-21**（tag 先切、打包测试到 11-10 才发 NuGet），本 pin 86ee4f57ebc（11-05）比 GA tag **新** 15 天——三十三轮的"pin 太老、升 GA"前提完全错误，ef9a2bd 已撤销（pin 回 86ee4f57ebc，v11.0.0 构建 45034404780 已取消）。**真因**：c19 构建日志显示 -os ios 下 MemoryMappedFiles/System.IO.Compression 等 unix 族库只产出 net11.0（TargetPlatformIdentifier 空 = PNSE 桩）与 net11.0-unix（真实现）两个变体、无 net11.0-ios；collect 的回退链 ios→net11.0 缺中间一级，真实现躺在 net11.0-unix 无人拿。官方 NuGet 包由微软打包管线构建所以全真实现——**源码版本从来无关，M2 跑通游戏与自建包的差异就是打包管线差异**。修法：collect 回退链 ios→**unix**→net11.0（unix 即 Darwin 真实现；Windows 专属库的桩保持，与官方包一致）；缓存键 c20/c16/c15（旧缓存的 runtime-out 是旧 collect 产物，必须作废重收集）。**pin 定版（用户裁决）**：撤销 79f1d2d 的 pin 回退，改 pin v11.0.0——理由：M2 三十轮真机证明的就是这个 tag 的源码（官方包同源），源码级对齐被证明的工件使未知数最小；"预览 pin 新 15 天"的服务修复并不需要。用户同时实测确认 0.3.2 普通启动（解释器）也进不了游戏——桩与执行模式无关的预言坐实。collect 修复独立于 pin 必须保留（自建 -os ios 构建在 GA 上同样产 net11.0-unix 变体）。

### M3 游戏程序集上机
- `patch.dll` 增加 iOS 目标（平台检查改写、Harmony 适配 darwin）；




- PortHooks 的 WaylithClr.* 替身已随 M2 收官进包（见上）；剩 CoreLib 补丁对 11.0 CoreLib 的重移植（20 个 `WaylithClr.*` 开关，安卓版是对 10.0.12 做的）与 PortHooks 的启动钩子/UnityStubs/ModPrep 件；
- `libcoreclr.dylib` 二进制补丁的 iOS 版（安卓版是 `libcoreclr.so` 两处）。
**验收**：玩家导入的游戏副本在真机进主菜单。

**三十五轮（2026-11-23 二更，0.3.5）：M4 首航发现混合运行时的解释器会话从未通过 coreclr_initialize——分配器模式门控改确定性信号**：m4-touch-1 首航：setup 的 coreclr_initialize E_FAIL（`[rw17-arena] no host arena`）——**0.3.x 全系普通启动（解释器）其实都死在这一步**（此前误判为 MMF 桩的资产检查崩溃；玩家实测的 0.3.3 全速体验全是 JIT 会话）。根因链：setup.c 在 coreclr_initialize 前 setenv COMPlus_InterpMode=2（M1 契约同款），但分配器门控读 CLRConfig 的 InterpMode 在 StaticInitialize 时仍得 0 → 强制竞技场 → setup 无 PORT17_JIT_* → fail-loud E_FAIL。**教训：CLRConfig 的 env 读取在 StaticInitialize 时刻不是可依赖的契约**（同进程 getenv 对竞技场交接 env 是可见的、真机双向验证过——机制不同不可类比推理）。**修法（结构性）**：分配器模式改由宿主确定性信号决定——`PORT17_JIT_RX` env 在（jit26_acquire 成功后、coreclr_initialize 前设置）= 竞技场 JIT；不在 = legacy 解释器配置（setup/普通启动）。macOS 保留 CI 种子竞技场机制（TARGET_OSX 分支）。InterpMode 门控删除。缓存键 c21/c17/c16。附带修正：setup.c 的 setenv InterpMode 保留（引擎语义仍需它——eeconfig 的解释器开关），只是分配器不再依赖它。**三十六轮补（0.3.6，同日三更）：m4-touch-2 仍 E_FAIL——真正的驱动者揪出**：PORT17_JIT_RX 门控在包（两版 libcoreclr 哈希不同实证新构建）但竞技场路径依旧——真凶 = `EnableWriteXorExecute` **旋钮默认值在非 RISCV64 全平台为 1**（clrconfigvalues.h 646/648 双声明，Apple arm64 走 1）——我的两版门控（InterpMode 门、信号门）都只管"额外强制"，而旋钮默认先把 g_isWXorXEnabled 置真，门控从未真正生效过。修法（0.3.6）：iOS 上完全不读旋钮，PORT17_JIT_RX 二值决定（在=true 竞技场 JIT；不在=false legacy 确定性）。缓存键 c22/c18/c17。

### M4 托管层共享（2026-11-23 开工：安卓触控优化完全对齐）

**M3 JIT 收官背景**：0.3.3 真机裁决——JIT 会话进世界、装载显著提速、零崩溃（用户实测 + 日志验收：竞技场链 6 行全绿、30fps 满速心跳、零 Fatal）。M3 的 JIT 大项关闭，CoreLib 20 开关随 mod 管线并入 M4。

**目标**：安卓 `mobile/`（PortMobile，23,380 行 Harmony 补丁）在 iOS 真机行为完全一致。功能面：长按右键、双指缩放、TapAssist、列表惯性滚动、虚拟鼠标键、KeyWheel。

**实施顺序（五步，每步真机可裁决）**：

1. **Harmony 可用性探针（成败手，最先做）**：JIT 下 HarmonyX 的 detour 机制实测——Harmony 动态方法编译走 CoreCLR 的 JIT 通道 = 我们竞技场路径的天然客户，前景远好于解释器（detour 在解释器下未知，JIT 下是主流形态）；但 HarmonyX 还用 Reflection.Emit + 内存补丁写入两处要实测。探针件：最小 patch.dll 加载（一个 Prefix + 一个方法替换 + 帧内调用断言），产物走现成 m2-host 流水线加诊断按钮。裁决行：探针日志 `harmony probe: patched X, called Y`。
2. **PortMobile 源码同步**：`scripts/sync-mobile.py` 从安卓仓库复制 `mobile/` 全量（保持相对路径），MSBuild 入游戏 TPA。**GLFW 接口契约零改动**——iOS 垫片的 `glfwAndroid*` 扩展名与安卓完全同名（ENGINE-NATIVE-CONTRACT.md 既定），PortMobile 的 P/Invoke 原样工作。
3. **iOS 化清单审计**：扫 mobile/ 的安卓专属 API 面（GLFW Android 原生活动钩、JNI 调用、Android OS 服务：振动/剪贴板/软键盘拉起），逐条列 iOS 等价物。已知映射：软键盘桥已有（隐藏 UITextField）；振动 → UIImpactFeedbackGenerator；其余按审计结果定。
4. **补丁应用管线**：安卓 patch/Patch.cs 的启动钩子形态对 iOS 落位（Harmony patch 在 Bootstrap 前应用；与 setup 八道 Cecil 补丁的先后关系：Cecil 改 IL 先行，Harmony 运行期叠上）。
5. **逐功能真机验收**：长按右键 → 双指缩放 → TapAssist → 惯性滚动 → 虚拟鼠标键 → KeyWheel，每项同存档同操作与安卓版行为对比；全绿 = M4 触控线关闭。

**风险与缓解**：
- Harmony detour 若在 JIT 下有静态链接特异性（method thunk 生成路径）→ 探针定位，备选 = 手写 detour 桩（哈希表 + trampoline，安卓版 InlineGuard 的形态）；
- PortMobile 若引用安卓 mod 管线的类型（ModPrep/Workshop）→ 同步时一并复制或桩化，审计步产出清单；
- mod 管线 + CoreLib 20 开关与触控线并行推进（触控不依赖 mod，但 PortMobile 的 mod 兼容面依赖）——顺序：探针 → 触控主线，mod 管线随后。

**0.4.x 真机连载（2026-11-23 晚，0.4.0→0.4.5 五轮装载工程）：游戏本体完整进入，手机层装载链五连修，触控六件套进入可裁决状态**

**0.4.0（回退裁决，commit 3beddb9，run 45100332222）**：v2（AndroidPlatform + 大线程 + 桥）六个环节零真机数据，正式引擎路回 IOS 后端 + 主线程自动进引擎（0.3.6 验证链），启动器降为非引擎状态前门，v10 补丁按"休眠无害"假设保留。首测即连环炸，且每一轮的炸点都不重样——0.4.1/0.4.2/0.4.3/0.4.4/0.4.5 五轮各修一处。

**三十七轮（0.4.1）：JIT/普通启动双黑屏——setup 跑在竞技场之前，.ips 裁决 CODESIGNING Invalid Page**：真机双症状：普通启动闪退、StikDebug 会话黑屏。port17-ui 四段会话全部停在「setup：初始化 CoreCLR」，.ips 裁决：`CorHost2::CreateAppDomainWithManager` 里 `blr` 跳进 `rw-/rwx SM=COW` 页（max rwx 当前 rw-，不属于任何已加载镜像），termination = `CODESIGNING / Invalid Page`，ktriage「Failed to fault in a page with execute permissions」——**执行了没有执行权限的页，内核代码签名直接判死**。0.4.0 重构把 `jit26_acquire` 留在 `run_engine` 里，setup 的 coreclr_initialize 无竞技场硬闯：0.3.6 的 PORT17_JIT_RX 门让 W^X=false 走 legacy 单映射，iOS 27 上无签名页不可执行（JIT 会话里 debugserver 吞 fault → 黑屏不闪退，0.2.7 教训的第三次重演）。**修法**：main.m 启动路径先拿竞技场再决定 setup/引擎，拿不到不硬闯（普通启动停启动器并写明"本版只能走 JIT 会话"）；setup 执行模式与引擎门对齐（PORT17_JIT_RX 在位 = InterpMode=0 + R2R 关）；engine.c 见 PORT17_JIT_* 在位不重复握手。**教训（规则化）：混合运行时下任何 coreclr_initialize 位点（setup/engine/探针）都必须先有竞技场，无竞技场一律干净降级。**

**三十八轮（0.4.2）：引擎只试 Android 后端——setup v10 的 IsAndroid 补丁劫持 AutoCandidates**：setup 成功（竞技场就绪后改写通过、改写戳落盘），进引擎即 `PlatformNotSupportedException: No windowing backend could start (tried Android)`，内层 `AndroidHost.Initialize has not been called (it should be called from Application.onCreate)`。根因：v10 的 PatchAndroidGate 把 Waylith.Windowing 全程序集 `OperatingSystem::IsAndroid()` 调用点改成 ldc.i4.1（v2 路线的地基），0.4.0 回退裁决只撤了 WAYLITH_BACKEND env、没撤补丁——反编译核对 `AutoCandidates`：IsAndroid 分支排在 IsIOS 之前，候选表只剩 Android；宿主桥在非 Android 模式不启动 → AndroidBackend.Open 必死，IOS 后端从未进候选。**修法（零 IL 手术）**：反编译确认 `ResolveCandidates` 先读 `WAYLITH_BACKEND` env（Enum.TryParse ignoreCase），engine.c 显式 `setenv WAYLITH_BACKEND=IOS` 绕开被劫持的候选表；补丁留在游戏文件里不再有话语权，runtime.env 仍可覆盖，v2 重开闸保留。附带：探针 run_probe 补同款竞技场闸门（它也是 coreclr_initialize 位点）；engine.c 的改写戳查询 v9→v10 对齐（一直误报 v7）。

**三十九轮（0.4.3）：IOS 后端全链首次活到首帧——Bootstrap.Initialize 死于 PortHooks 版本不匹配**：真机里程碑：**游戏完整装载进入**——Metal 交换链建立（2360×1640 双图像）、CoreAudio 音频（含 3D 定位音效）、VMA、引擎帧心跳 ~30fps、mods=7 装载、主菜单可操作、世界可进、键盘可转视角。死点：`Waylith.Injection.Injection` cctor → `FileNotFoundException: PortHooks, Version=1.0.0.0` → PrepatcherCompat.Install → Environment.FailFast（信号 6；COMPlus_EnableCrashReport 首次完整抓到托管栈，抢救管线按设计工作）。根因：**CoreCLR 的 TPA 绑定要求版本精确匹配**——setup 写进游戏程序集的 PortHooks 引用钉 1.0.0.0（RetargetWaylithClr/ImportPortPlatform），而 prebuilt 全量构建的 PortHooks.dll 是裸 csc 默认的 **0.0.0.0**（0.3.3 时代是 CI 占位件、csproj 钉 1.0.0.0 所以没炸；M4 换本地全量构建后身份变了）。安卓没炸是因为安卓的 PortHooks 在游戏目录走 LoadFromAssemblyPath 路径装载、不查版本。**修法**：build-portlayers.py 生成 AssemblyVersion 特性文件钉 1.0.0.0 重出两个 dll。**插曲（方法论级）**：第一版补丁用了 csc 的 `-version:` 开关——那是"打印编译器版本并退出"，rc=0 静默无产物，靠文件时间戳没变才识破；程序集版本只能走特性。

**四十轮（0.4.4）：手机层静默不装载——OnEntryUIInit 只在游戏目录找 PortMobile.dll**：0.4.3 的包游戏可玩但触控六件套全部静默缺席（只能单击，与未适配完全一致）。日志一行定案：`[port17] no PortMobile.dll next to the game; running without the phone layer`——setup 注入 UIRoot_Entry.Init 的 OnEntryUIInit 用 AppContext.BaseDirectory（游戏目录）找 PortMobile.dll，iOS 包里它在 .app bundle（与 PortHooks 同目录）。**修法**：查不到时回退 PortHooks 程序集所在目录（TPA 装载的 Location 即 bundle）；安卓路径保持原样。**共性教训立项：安卓"游戏目录 next to the game"式查找在 iOS 都要加 bundle 回退（PortHooks/PortMobile/Mono.Cecil 同族）。**

**四十一轮（0.4.5）：手机层启动即死于 Harmony 第一针——mscorlib 引用程序集毒文件**：0.4.4 包真机：`[mobile] starting; Harmony 3.0.0.0, runtime 11.0.0` 后 `HarmonyLib.HarmonySharedState` cctor → `BadImageFormatException 0x80131058: Cannot load a reference assembly for execution 'mscorlib, 4.0.0.0'` → Harmony.Patch → 手机层死（触控仍是单击）。解包实测：bundle 的 mscorlib.dll 带 ReferenceAssembly 标记——自建 CoreCLR 无 mscorlib 实现（真 CoreLib 是 System.Private.CoreLib），collect 从构建产物抓到的是 ref 件；而创意工坊 HarmonyMod/0Harmony 3.0 是 Framework 风格编译，引用 mscorlib 4.0.0.0，TPA 精确命中毒文件。mod 那条 `Lib.Harmony could not be initialized: Patching exception in VersionControl::DrawInfoInCorner` 同根（ModAssemblyHandler 反射 HarmonyMod.RuntimeIL 已先 TypeLoad，见下方新账）。**修法**：官方 ios runtime pack 的 mscorlib facade（同 4.0.0.0 身份、可执行、ReadyToRun 关闭走 JIT）入仓 prebuilt/mscorlib.dll，build-ipa.sh 运行时拷贝后原位覆盖；新包解包验证已是官方形态。附带发现：混合包的 netstandard.dll 是好件（可执行 facade），未中毒。

**四十二轮（0.4.6）：宿主没带 Harmony 本体，触控层找 3.0.0.0 找不到**：0.4.5 包真机两次 `PortMobile.Entry.Start()` 直接 `FileNotFoundException: 0Harmony, Version=3.0.0.0`（20:31:38 / 20:31:58），同一会话游戏与存档本身继续正常装载——所以用户看到的仍是"无触控优化，只能单击"。根因是打包依赖缺口，不是触控逻辑失效，也不是玩家手动安装 Harmony mod 的问题：PortMobile 静态引用 `0Harmony 3.0.0.0`；安卓 staging 会把改写过的 0Harmony 放进游戏目录，iOS build-ipa 只拷了 PortHooks/PortMobile 没拷 Harmony；m4-touch-9 的激活列表只有官方 6 个 DLC，`0 mod assemblies asked for`，玩家后来手动放进来的 Harmony 也没机会被加载。**修法**：新增 `scripts/patch-harmony`，从创意工坊 1.7 Harmony 生成 iOS 兼容副本并执行设备 setup 同款 14 处改写（GetFieldLax、LaxCallOpcode、NormalizeHarmonyStack），产物入仓 prebuilt/0Harmony.dll；build-ipa.sh 校验并拷进 .app 根。StartupHook 已有的逻辑会把任何 mod 自带的 0Harmony 统一改道到宿主这份。版本升 0.4.6。

**四十二轮裁决（0.4.6，test_work/m4-touch-10，run 45176863568）**：Harmony 库装载链通过，detour 本体暴露真正死点。IPA 内 `0Harmony.dll` 与版本 0.4.6 已核验（SHA256 `B6BE14EE…DF09`）。

**四十三轮（0.4.7）：MonoMod 平台探测把 iPad 当 Unknown ARM——detour 第一跳就死**：0.4.6 日志已到 `[mobile] starting; Harmony 3.0.0.0`，第一个 patch（InlineGuard 守卫 `PatchFunctions.UpdateWrapper`）在 `HarmonyLib.PatchTools.DetourMethodDefault` 进入 MonoMod；`CreateCurrentSystem()` 判到 OSX，但 `MacOSSystem..ctor` 抛 `NotImplementedException`。反编译定案：MacOSSystem 只支持 x86_64/Arm64，iOS 的 uname 机器名是 `iPad16,8` 而不是 `arm64`，MonoMod 的通用探测留给 Unknown；启动期按 `RuntimeInformation.ProcessArchitecture` 兜底也错过（嫌疑：iOS host 报 Arm，不是 Arm64）。**修法**：patch-harmony 在离线出包时把 `PlatformDetection.DetectPlatformInfo()` 整体钉成 `(OSKind.OSX, ArchitectureKind.Arm64)`——IOS 分支本身也是 MonoMod 的 NotImplementedException，只能走 macOS ARM64 路线；幂等抽检 14 处 PortPlatform 兼容改写不变。另设 `MONOMOD_HelperDropPath=<game>/mm-helpers`，把 MonoMod macOS exhelper 的 mkstemp 默认 `/tmp/...` 拉回 iOS 容器内。版本升 0.4.7。

**当前裁决状态（0.4.7 待测，test_work/m4-touch-11，run 45179179709）**：IPA SHA256 `97A904AA…473E`；解包核验版本 0.4.7，`0Harmony.dll` 尺寸 2,026,496，反编译确认 `DetectPlatformInfo()` 已直接返回 `(OSX, Arm64)`。MonoMod 平台三件套（OSX + Arm64 + CoreCLR）钉死后，detour 才能真正完成。裁决行不变：日志必须不再有 `MacOSSystem..ctor` / `NotImplementedException` / `the phone layer failed to start`，且长按右键/双指缩放/TapAssist 有反应 = M4 触控主线裁决通过。

**四十四轮（0.4.8）：对照安卓后修平台钉死的 tuple 引用与 exhelper 签名**：0.4.7 日志越过 `MacOSSystem..ctor`，但钉平台的方法在运行期抛 `MissingMethodException: ValueTuple<OSKind,ArchitectureKind>..ctor`。根因：Cecil 新建的泛型构造器引用缺运行期需要的完整上下文；更稳的姿势是保留原 0Harmony 里 ValueTuple 构造指令本体，只重排成 `OSX/Arm64 -> 原构造 -> ret`。同时按安卓 `patch/Patch.cs` 对照：安卓不让 MonoMod 把 exhelper 解到临时目录，而是随包 `libmm_exhelper.so` 并把 `CreateNativeExceptionHelper` 改成直接加载；iOS 对运行期写出的 dylib 同样有签名问题，因此提取 Harmony 内嵌 `exhelper_macos_arm64.dylib` 入 `prebuilt/`，IPA 原位拷入并随 bundle ad-hoc 签名，`MacOSSystem.CreateNativeExceptionHelper` 改加载 `<bundle>/exhelper_macos_arm64.dylib` 且 `deleteAfterLoad=false`。平台对照结论：安卓是把误判 Android 改走 LinuxSystem；MonoMod 没有 IOSSystem，iOS 只能走 macOS ARM64 路线。版本升 0.4.8。

**当前裁决状态（0.4.8 待测）**：MonoMod 平台钉死已能过构造器，原生 exhelper 改为 bundle 已签名件。裁决链：日志不得再出现 `MissingMethodException` / `MacOSSystem..ctor` / `NotImplementedException` / `the phone layer failed to start`；若 MemoryAllocator 或 dlopen 再失败，即是 MonoMod 在 iOS 上的下一层原生内存/签名账；长按右键/双指缩放/TapAssist 有反应 = M4 触控主线通过。

**0.4.8 包落位（test_work/m4-touch-12，run 45182008089）**：IPA SHA256 `F52824D4…048CC`；解包核验版本 0.4.8，`0Harmony.dll` 反编译为 `(OSX, Arm64)` 且 `CreateNativeExceptionHelper` 直接加载 `<bundle>/exhelper_macos_arm64.dylib`；`exhelper_macos_arm64.dylib` 已随 IPA 签名后在位（签名后 204,848 字节）。

**装载连载沉淀的通用规则（后续任何"装了不生效"先查这三条）**：
1. 任何 coreclr_initialize 位点必须先有竞技场，无竞技场一律干净降级不硬闯；
2. iOS 与安卓的宿主件位置不同（bundle vs 游戏目录），"next to the game"式查找必须加 bundle 回退；
3. TPA 绑定按名字+版本+公钥精确匹配，且引用程序集不可执行——Framework 风格 mod 依赖的 mscorlib/netstandard facade 必须是可执行件（mscorlib 已换官方 facade；netstandard 实测本就是好件）。

**新账（本轮连载产出）**：
- mod 程序集的 WaylithClr.* 引用改道未覆盖 Mods 目录：创意工坊 HarmonyMod 的 RuntimeIL 程序集引用 WaylithClr.MethodBodyClause/MethodBodyTransformer（指 System.Private.CoreLib）→ TypeLoadException → 该 mod 的 Harmony 初始化失败。属差距 #4 的实施范围，M4 后半做 CoreLib 开关时把 Mods 目录纳入改道（ModPrep 管线）；
- collect-runtime.sh 的 ref 件长期修：mscorlib 已在包层兜底（官方 facade 覆盖），正解是 collect 时排除 ref 件或改取官方 pack；其余 305 个 dll 游戏本体已在用 = 实现件无恙。

### M5 玩家可用 beta
- 导入流程进程内化（iOS 无 fork，PortSetup 不能独立进程）；
- 内存整理策略对齐 jetsam（8GB 设备前台约 6–7GB 上限，无 swap、超限即杀）；
- 玩家文档（SideStore 安装、StikDebug 开 JIT 步骤）。

## 与安卓畅玩版的差距清点（2026-11-23，0.4.0 时点）

参照物 = 安卓畅玩版：samilry 真机 130+ mods 游玩、57 FPS / 359 TPS、创意工坊下载器（2.5GB 合集约 17 分钟）、ModPrep 规则 v14、34 个热门界面 mod 冒烟测试零移植层报错。

### 已对齐（不再入账）

- **托管层全量同源**：hooks 25 个共享文件全在（WaylithClr 替身 / HarmonyCompat / InlineWatch / Textures+Astc 的 BC 兜底 / AndroidFonts / ModPrep 机制…）+ IosHost；`mobile/` 65 个文件与安卓逐一同名（触控六件套在内）。
- **原生库逐项对齐**：Slang 2026.19（着色器缓存键与 PC 绑定）、KTX 4.4.0、astcenc 5.7 NEON、VMA、stb、gli、waylithaudio、GLFW 垫片、Vulkan 前向层（MoltenVK）。
- **触控契约**：`glfwAndroid*` 扩展与安卓完全同名，PortMobile 的 P/Invoke 原样工作（ENGINE-NATIVE-CONTRACT 既定）。

### 差距（按畅玩影响排序）

| # | 项 | 状态 | 影响 | 收口动作 |
|---|---|---|---|---|
| 1 | **Harmony detour 在 iOS 未实测**（M4 五步之第一步「成败手」） | **detour 本体首次暴露死点**：0.4.1–0.4.7 连修七关（竞技场时序 / Android 后端劫持 / PortHooks 版本 / PortMobile 查找路径 / mscorlib 毒 facade / 宿主缺 0Harmony / MonoMod 平台探测，详见 M4 的 0.4.x 连载）；0.4.7 钉死 OSX + Arm64 + CoreCLR 三件套 | 成败手，一切触控裁决的前提 | 0.4.7 裁决：长按右键有反应 = detour 通过；仍失败则日志给 detour 后续新异常，做探针件定位 |
| 2 | **M4 触控六件套真机裁决**（长按右键/双指缩放/TapAssist/惯性滚动/虚拟鼠标键/KeyWheel） | 工程 + 装载链完成；键盘输入已验通（0.4.3 真机），触控待 0.4.7 起逐项裁决 | 触控体验对齐安卓 | 0.4.7 起逐项裁决清单 |
| 3 | **JIT 常开** | 平台约束：每次启动经 StikDebug + rw17-jit26.js 拉起才全速；普通打开 = 解释器（能玩、慢数倍） | 畅玩第一差距，平台天花板无工程解 | 玩家文档把 StikDebug 流程写清（M5） |
| 4 | **CoreLib 补丁重移植 + libcoreclr 二进制补丁 iOS 版** | **调研已完成（2026-11-23，docs/NET11-COMPARISON.md）**：10.0.12→v11.0.0 判定平滑——20 开关的全部 CoreLib 锚点在 v11 原样存活（抽查复核过）；affinity 字节补丁上游 #125361 已修（iOS 不需要，划掉）；dualjit patch 对 GA tag 实测干净套用，且 v11 上游反而更贴我们的 hybrid 形状（DCC 闸门/iOS 解释器进 CI）。**12 项改动清单已成文**（托管 6 开关 + 原生 initonly + 私有 API 五件），待实施 | 游戏本体不受阻（0.3.6 实证可玩）；影响集中在 PC mod 的反射/DynamicMethod 边缘用法与两个显示细节（小人昵称哈希漂移、浮点位数） | M4 后半开工即做：源码补丁路线（patches/ 同 dualjit 机制），实施顺序见 NET11-COMPARISON.md §3 |
| 5 | **ModRules.txt（RimSort 社区加载顺序库）未入包** | ModPrep 机制及其规则（v14）随 hooks 已入库——缺的是另一回事：`communityRules.json` 转换的紧凑文本（after/before/top/bottom/incompatible），给 mod 排序界面用；游戏有网时也能自己下载转换 | 已知冲突对的顺序修正缺失，特定 mod 组合可能排序不当；小列表几乎无感 | 打包件（modrules/build.js 产物入 IPA）或运行期自下载兜底 |
| 6 | **PortWorkshop 创意工坊下载器未移植** | 安卓有（workshop/：Downloads/Session/Updates/WorkshopWindow）；iOS 无 | 便利大件；zip 导入链兜底 | M5 候选 |
| 7 | **NotoEmoji 兜底字体未入包** | 已知未清项 | 游戏文本 emoji/特殊字形缺字 | 打包件 |
| 8 | **内存/jetsam 对齐** | MemTrim shared 已编译；jetsam 策略（8GB 设备 6–7GB 上限、无 swap 超限即杀）未做 | 大 mod 列表长会话可能被杀 | M5 |

平台性差异（不算缺口）：后台执行仅 ~30 秒窗口（安卓前台服务可挂后台下载，iOS 做不到）；崩溃报告为 signal + .ips + stderr 抢救（安卓为 Java 报告体系）。

### 建议顺序

0.4.0 真机裁决（#1/#2 同场）→ #1 挂则 Harmony 探针件 → #5/#7 打包件顺手一次 CI → #4 CoreLib/libcoreclr 对齐 → #6 工坊移植 → #8 内存策略。

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
