# 移植计划与决策记录

## 已定决策（2026-11-08）

| 决策 | 内容 | 依据 |
|---|---|---|
| 运行时基线 | dotnet/runtime `release/11.0` @ `86ee4f57ebc`（2026-11-05），见 `runtime-pin.txt` | ① v10.0.12 源码里 CoreCLR 没有 Apple mobile 支持（无 `APPLE_MOBILE`、无 MAP_JIT 执行内存路径），`-os ios` 只服务于 crossgen2 测试；② NuGet 上 ios-arm64 没有 10.x 正式包（6.0 preview 直接跳 11.0 preview），iOS CoreCLR 是 .NET 11 才有的；③ `release/11.0` 的 `doublemapping.cpp:174-179` 有完整的 RWX→EACCES→MAP_JIT 探测回退（macOS arm64 JIT 同款），`--dynamiccodecompiled true` 构建开关在。v11.0.0 GA tag 发布后升级 pin。 |
| JIT 路线 | 构建带 JIT 的变体（`--dynamiccodecompiled true`），设备端经 StikDebug 开 JIT；解释器变体作后备 | StikDebug = 调试器 attach 使进程可执行自生成代码；宿主设备已验证可用。风险：iOS 大版本更新可能破坏（iOS 26 已破坏过一轮，2026-06 时仅部分 app 支持）。 |
| JIT 内存（2026-11-11） | **DualJIT 竞技场**（`patches/dualjit-arena.patch`）：StikDebug 启动脚本 `probe/stikdebug/rw17-jit.js` 在调试器附着窗口内经 debugserver `_M,rx` 预分配 128MiB RX 竞技场并盖魔数+pid 握手头；CoreCLR 侧补丁把 `IsDoubleMappingEnabled` 翻回 true（Apple arm64 原为硬编码 false），`doublemapping.cpp` 的 Apple arm64 后端扫描竞技场 → `vm_remap` RX/RW 双视图 → 写执行探针 → Mono 式定额分配。无竞技场时自建 RWX 后备（macOS/CI 全功能；iOS 26 真机后备可能因代码签名不可执行 → 干净降级并日志提示先跑脚本）。**容量 128MiB（0x8000000）**：实测本体+移植层 17.9MB IL / 28.9 万方法，小于 STS2（21.6MB IL / 33 万方法，96MiB 实测可跑）；128MiB 是 arm64 bl ±128MB 直达范围内的最大安全竞技场直径，mod 溢出时再议窗口化扩容。| M1 真机 SIGBUS 链条已闭环：iOS 不导出 `pthread_jit_write_protect_np` → MAP_JIT 页永远不可写 → 写 JIT 代码页必死；`EnableWriteXorExecute` knob 在 Apple arm64 被硬编码绕过（死配置）。方案来源：杀戮尖塔2 社区 iOS 重打包（sts2.0.111.0.iOS.0.2.6.ipa）逆向，其 iOS≥26 分支强制走 debugserver 竞技场，实测可玩。 |
| 构建 | Windows 本地写码 + GitHub Actions `macos-15` 编译，公开仓库 | Apple 目标必须 macOS 宿主（runtime 仓库 CI 注释原话："the iOS jobs run on macOS"）。公开仓库 macOS runner 免费不限量。 |
| 分发 | 未签名 IPA + SideStore 自签侧载 | 无需 Apple 开发者账号；SideStore 签名自动带 `get-task-allow`（StikDebug 列表的准入条件）。 |
| 仓库 | 本仓库独立于安卓仓库，托管层源码 M4 起共享 | M1–M3 用不到安卓仓库的托管层。 |

## 里程碑

### M0 决策与验证（已完成）
runtime 基线选型、CI 方案、分发路线（本文档）。

### M1 运行时探针（进行中，本仓库当前状态）
最小宿主 app + 自建 iOS CoreCLR + `Hello.dll`（Reflection.Emit 计时探针）+ mach RX 区域扫描。
**验收**：真机上，按下方流程开 JIT 后探针报"JIT 生效"（几十毫秒），RX 私有区域非零；port17-ui.log 里出现 `[rw17-arena] arena found / arena adopted`（竞技场模式命中）。
**产出**：可下载的 `port17probe.ipa`。
**真机 JIT 流程（2026-11-11 起）**：
1. SideStore 安装 IPA（自签带 `get-task-allow`）；
2. 直接打开探针 app（无需 StikDebug 拉起）——app 自动等待竞技场（最长 600 秒，每 15 秒打印 RX 区域遥测）；
3. StikDebug 以本 app 为目标运行脚本 `probe/stikdebug/rw17-jit.js`（128MiB RX 竞技场 + 魔数握手，逐页扫写约 1–2 分钟）——**脚本尾部保持会话打开（universal.js 模型）**，app 检测到竞技场后自动继续；
4. 日志期待链：`JIT 竞技场已就绪` → `arena found at …` → `arena adopted: rx=… rw=…` → `CoreCLR 就绪` → `JIT 生效` → `exit 42`。

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
