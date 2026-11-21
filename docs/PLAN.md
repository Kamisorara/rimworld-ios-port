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

### M3 游戏程序集上机
- `patch.dll` 增加 iOS 目标（平台检查改写、Harmony 适配 darwin）；
- PortHooks 移植 + CoreLib 补丁对 11.0 CoreLib 的重移植（20 个 `WaylithClr.*` 开关，安卓版是对 10.0.12 做的）；
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
| iOS 大版本升级破坏 StikDebug JIT | 玩家无法开 JIT，游戏不可玩 | 解释器变体兜底（性能存疑）；跟进 StikDebug 更新 |
| 11.0 CoreLib 与 10.0.12 行为差异 | `WaylithClr.*` 开关模拟面变大（Ludeon 改的是 10.0 运行时，标准 11.0 与标准 10.0 本身就有差异要一并模拟） | M3 逐开关核对；必要时评估 cherry-pick Apple mobile 支持回 release/10.0（工作量大，仅当差异不可收拾时） |
| `--dynamiccodecompiled true` 的 iOS 构建微软 CI 不测 | 编译或运行时意外 | M1 就是为它而来；坏则逐个修 |
| MoltenVK 与引擎用的 Vulkan 特性面不匹配 | 渲染故障 | M2 早期跑引擎最小渲染验证 |

## 运行时 pin 升级规程

1. 改 `runtime-pin.txt` 第一行（commit 全 sha 或 `v11.0.0` 等 tag）；
2. push 后 Actions 自动重建 runtime 缓存（新 key）；
3. 在 PR/commit 里记录新 pin 对应的日期与来源分支。
