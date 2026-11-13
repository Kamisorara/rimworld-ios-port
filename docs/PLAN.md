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

### M1 运行时探针（进行中，本仓库当前状态）

**改造计划（2026-11-13 起，进行中）**：JIT 授予协议转 playport 模式——`brk #0xf00d` 同步握手 + 提前分离（不再长挂调试会话），见 [PLAN-JIT26.md](PLAN-JIT26.md)。

最小宿主 app + 自建 iOS CoreCLR + `Hello.dll`（Reflection.Emit 计时探针）+ mach RX 区域扫描。
**验收**：真机上，按下方流程开 JIT 后探针报"JIT 生效"（几十毫秒），RX 私有区域非零；port17-ui.log 里出现 `[rw17-arena] arena adopted`（brk 握手命中）。
**产出**：可下载的 `port17probe.ipa`。
**真机 JIT 流程（2026-11-13 起，JIT26 协议）**：
1. SideStore 安装 IPA（自签带 `get-task-allow`）；
2. 经 StikDebug 拉起探针 app（带 JIT）——runtime 在 `coreclr_initialize` 里发 `brk #0xf00d` 请求竞技场，最多等活调试器 120 秒（`[rw17-arena]` 进度在 stderr 日志）；
3. StikDebug 以本 app 为目标运行应答脚本 `probe/stikdebug/rw17-jit26.js`——在 app 停在 brk 上的窗口里经 debugserver `_M,rx` 分配 128MiB RX 并逐页扫写祝福（约 1–2 分钟），x0 回传地址，随后应答 `x16=0` 分离请求；**分离完成后脚本窗口可关**（祝福存续），app 在无调试器状态下继续初始化；
4. 日志期待链：`live debugger confirmed` → `arena granted at …` → `RW alias at …; detaching …` → `probe call #1 -> 42` → `probe call #2 -> 43` → `arena adopted: rx=… rw=…` → `CoreCLR 就绪` → `JIT 生效` → `exit 42`。

### M2 宿主层成型
- UIKit 宿主（从 M1 探针 app 长出来）：窗口、触摸原始事件、软键盘桥（iOS 版 TextInputView 等价物）；
- GLFW 垫片的 iOS 后端（沿用安卓版"引擎只用 Vulkan 子集"的写法）；
- Vulkan 经 MoltenVK；waylithaudio 编 CoreAudio 后端；
- Slang / ImGui / FreeType / KTX / astcenc 的 iOS 构建。
**验收**：Waylith 引擎在真机上渲染出最小画面（无游戏程序集）。

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
