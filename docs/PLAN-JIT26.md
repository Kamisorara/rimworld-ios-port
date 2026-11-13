# JIT 授予协议改造计划（playport 模式：brk 握手 + 提前分离）

状态：**代码改造已完成（2026-11-13），待真机验收**。步骤 1–3、4（缓存键 c11→c12 / c8→c9 / c7→c8）、6（文档）均已落地；步骤 0 的分离存续复测并入首次真机验收——分离后的探针（`probe call #1 -> 42`）本身就是存续性证明，失败则按"风险与回退"第 1 条走。

与原计划的一处偏差：`Rw17FindArena` 扫描路径**没有删除**，而是 `#if defined(TARGET_OSX)` 限定给 macOS CI（`RW17_SEED_ARENA=1` 种子竞技场全链路验证别名/探针/Commit 机制，CI 用例零改动）；iOS 设备/模拟器走 brk 协议、不再扫描。原计划的"步骤 0 先行复测"因用户要求直接替换而并入验收链。

## 0. 背景与结论

**现状**：脚本预撒竞技场（`_M<size>,rx` + 逐页扫写 + 魔数握手），app 扫描认领——JIT 内存本身已经能建起来，但 `coreclr_initialize` 死在 mapper 探针窗口（进入后 3ms 内）。

**根因（机理，不是玄学）**：现行"永不分离"模型让运行时**全程带调试器**。任何 fault——包括探针自己的 SIGSEGV/SIGBUS 兜底测试——都先派发给 debugserver：进程停机，进程内 sigsetjmp 兜底永远没机会执行，脚本 `c` 循环盲目恢复后重执行故障指令，无限停/复循环。这就是"死点在探针窗口"的完整解释；bd9894a 装的兜底 handler 在附着状态下是空话。CoreCLR 初始化之后更糟：托管异常（空引用即 EXC_BAD_ACCESS/SIGSEGV）每分钟成百上千，全部要先过 debugserver 这一关，Mono 靠运气没踩到，CoreCLR 第一步就踩。

**参照**：playport（iOS 26+ 原生跑 Windows 游戏，同族机制）的路线是：app 在自己的执行流里发 `brk #0xf00d` 请求、脚本同步应答、拿到 RW 别名后**立即分离**、运行时全程无调试器（wine_host.c 注释原话 "the pool stays executable"）。生态三路证据都表明逐页祝福在分离后存续：universal.js 应用群（iOS 26 大众路线）、STS2 重打包（其 `executeProbe` 是 sigsetjmp 形态——该形态只在无调试器时可能工作，反推其运行期是分离的）、playport（iOS 27 实测 early detach）。

**结论**：转向 playport 模式。现有补丁的 vm_remap 别名、执行探针、干净降级**全部保留复用**；改动集中在两点——竞技场怎么来（扫描 → brk 握手）、会话生命周期（长挂 → 提前分离）。

## 1. 目标流程（改造后）

```
StikDebug 带 JIT 拉起 app → 运行 rw17-jit26.js（应答模式，跑完即可关窗口）
  app: run_probe → coreclr_initialize → ExecutableAllocator::Initialize
     → 活调试器门（CS_DEBUGGED && P_TRACED；最多等 120s，15s 一报）
     → mov x16,#1 ; brk #0xf00d                ──停机──▶  脚本：
     │                                                     验 brk 指令 → 跳过(P20=pc+4)
     │                                                     _M8000000,rx → 8192 页逐包扫写
     │                                                     P0=地址 → c
  app: x0 返回 rx 基址
     → vm_remap RW 别名（现补丁 Rw17MapAliasRW 原样）
     → mov x16,#0 ; brk #0xf00d                ──停机──▶  脚本：跳过 → D（分离、结束）
  app（此后无调试器）:
     → Rw17ProbeExecute（sigsetjmp 兜底此时才真正生效）→ 42 / 43 → 采纳
     → coreclr_initialize 余下部分 → Hello.dll 反射探针 → exit 42
```

要点：
- 启动竞态消失——app 停在 brk 里等脚本应答，天然同步，`wait_for_rw17_arena` 整个删除。
- 探针从"附着下必冻结"变成"分离后正常探测"，失败可干净降级（现有 iOS fail-loud 通道）。
- 每次运行 = StikDebug 拉起一次 + 跑一次脚本（祝福按进程存续，与现流程操作习惯一致）。

## 2. 协议规范（与 playport-universal.js 对齐）

| 项 | 值 |
|---|---|
| 陷阱指令 | `brk #0xf00d` = 0xd43e01a0；GDB 内存包 `m<pc>,4` 读回小端字节串 `a0013ed4` |
| 寄存器编号（Apple debugserver） | x0=`00`、x1=`01`、x16=`10`、pc=`20`（P 包写法 `P20=<leHex>`） |
| x16=1 prepare | x0=NULL（脚本分配）、x1=0x8000000；脚本 `_M8000000,rx` → 逐页 `M...,1:00` 共 8192 包（值不重要，覆盖到每页才重要；每 1024 页报进度）→ `P0=<地址>` → 恢复 |
| x16=0 detach | 跳过 brk 后直接 `D`，脚本结束 |
| 其余 x16 | 报错退出；MAX_STOPS=256 封顶（playport 同款纪律） |
| 发 brk 前提 | `csops(CS_OPS_STATUS)` 的 `CS_DEBUGGED` **且** `sysctl(KERN_PROC_PID)` 的 `P_TRACED`，双查、fail-closed（无人接的 brk = SIGTRAP 杀进程，这是唯一硬失败点，必须门死） |

## 3. 分步改造

### 步骤 0（先行验证，半天）：分离存续复测

当前唯一支持"永不分离"的证据是 M1/iOS 26 那次观察（发 D 后竞技场对 vm_region 扫描不可见），与生态三证相反，必须先钉死。

- 做法：现网构建 + 现脚本，按现流程等到 `JIT 竞技场已就绪`（host.c 等待循环已有 15s 遥测），然后从 StikDebug 侧结束会话（等价 D），观察遥测里该 RX 区域是否持续存在、保护位是否变化。必要时在 host.c 等待循环里加一行：命中时打印竞技场区域的 `(addr, prot, size)`，之后每 15s 重打印同一区域状态。
- 判定：
  - **存续** → 本计划全部成立，继续步骤 1；
  - 区域在但保护位变 → 旧观察是扫描过滤条件问题，照样继续（brk 模式不依赖扫描）；
  - 区域真没了 → 该设备/系统版本不支持提前分离：M1 保留长会话旧脚本 + 旧构建兜底（旧脚本补 fault 上报），换 iOS 27 / A 系设备复测，结论记入 PLAN.md 风险表。

### 步骤 1：新脚本 `probe/stikdebug/rw17-jit26.js`（应答模式）

- 结构照抄 playport-universal.js（本地参考：`D:\Data\Code\OpenSourceProjects\playport\app\PlayportJIT\playport-universal.js`），宿主 API 只用现脚本已在用的三个：`get_pid` / `send_command` / `log`。不依赖 playport 的 `prepare_memory_region`（那是 StikJIT 宿主函数，StikDebug 不保证有）；逐页扫写用 `M` 包，STS2 同款。
- 流程：`vAttach` → 循环 `{ c → 停 → m<pc>,4 验 a0013ed4 → P20=pc+4 → 按 x16 分支 }`。
- 安全网：停机原因若为 SIGSEGV/SIGBUS/SIGILL（分离前任何阶段的 fault），打印信号 + pc 后停止盲目 `c`——诊断信息直接上屏，不再无限停/复。
- 旧 `rw17-jit.js` 保留不删（步骤 0 与回退用）。

### 步骤 2：runtime 补丁改造（`patches/dualjit-arena.patch`）

改 `minipal/Unix/doublemapping.cpp` 段：

- 删 `Rw17FindArena` / `Rw17ReadArenaHeader` / 魔数常量（x0 直接传址，魔数握手一并取消）。
- 新增两个裸函数：`jit26_prepare_region` / `jit26_detach`（`noinline, optnone, naked`，asm `mov x16,#1/0 ; brk #0xf00d ; ret`）。
- 新增活调试器门 + 入口 120s 有界等待（15s 一报，超时或无调试器 → 返回 false → `Initialize()` 失败 → coreclr_initialize 显式报错，stderr 提示"经 StikDebug 启动并运行 rw17-jit26.js"——走现 bd9894a 通道）。
- `Rw17CreateArenaMapper`：删扫描，改为 门 → prepare brk → 取 x0 → `Rw17MapAliasRW`（原样）→ detach brk → `Rw17ProbeExecute`（原样，此时无调试器）→ 采纳。
- `ReserveDoubleMappedMemory` / `GetRWMapping` 等算式里的 `Rw17ArenaHeaderSize` 取消（无握手头，整 128 MiB 可用；`pMaxExecutableCodeSize` = 全容量）。
- **不动**：`executableallocator.cpp`（Apple arm64 强制双映射、iOS fail-loud）、`clrdefinitions.cmake`（禁 `FEATURE_MAP_THUNKS_FROM_IMAGE`）、单映射 RWX 分支（macOS 保留、iOS 无后备）、128 MiB 容量决策（本体 17.9MB IL / 28.9 万方法，全量 JIT 代码同住一片，±128MiB 直达窗口仍成立）。
- 注记：detach 之后**禁止再发任何 brk**（playport 的 FEX 装了 stray-brk trap handler，我们没有再发点，写明纪律即可）。

### 步骤 3：探针宿主改造（`probe/app/host.c`）

- 删 `wait_for_rw17_arena` 及配套（`rw17_scan_t` / `rw17_scan` / `rw17_arena_header_ok`）。
- `mmap` RWX 授权探测保留，标注"仅诊断"。
- 提示文案改为："经 StikDebug 拉起本应用并运行 rw17-jit26.js；脚本分离完成后窗口可关"。
- 其余全部不动（TPA、stderr 捕获、`dump_coreclr_stderr`、`scan_exec_regions`、exit 42 验收链）。

### 步骤 4：构建与缓存

- runtime 补丁变更 → CI 缓存键升级（现为 c7/c4/osx-c3，本次 +1）。
- `probe/build-ipa.sh` 流程不变。

### 步骤 5：真机验收（日志链）

成功链（port17-ui.log / coreclr-stderr.log）：
1. `[rw17-arena] live debugger confirmed (…)`（或等待 N 秒后确认）
2. `[rw17-arena] prepare brk issued (128 MiB)` → 脚本侧逐 1024 页进度
3. `[rw17-arena] RW alias at …`
4. `[rw17-arena] detach brk issued; session ended`
5. `probe bytes: alias=52800540 arena=52800540` → `probe call #1 -> 42` → `probe call #2 -> 43`
6. `[rw17-arena] arena adopted: rx=… rw=… capacity=…`
7. `CoreCLR 就绪` → 可执行内存差值数十 MiB → `Hello 退出码：42`

负面链（都要干净）：
- 不经 StikDebug 直接启动 → 门等 120s 超时 → `coreclr_initialize 失败：0x…` + 提示文案（不闪退）；
- 分离前误关脚本窗口 → 停机会话断 → 干净报错，重跑一次流程；
- 分离前任何 fault → 脚本打印信号 + pc 并停止盲转。

### 步骤 6：文档收尾

- `PLAN.md`：JIT 内存行与 M1 真机流程段更新为本协议；开放风险表"iOS 大版本升级破坏 StikDebug JIT"补充"分离存续依赖"半句。
- CHANGELOG 补条目（沿安卓仓习惯）。

## 4. 文件改动清单

| 文件 | 动作 |
|---|---|
| `probe/stikdebug/rw17-jit26.js` | 新增（应答模式脚本） |
| `probe/stikdebug/rw17-jit.js` | 保留（步骤 0 / 回退用） |
| `patches/dualjit-arena.patch` | 改 doublemapping.cpp 段（扫描→brk、去握手头、加门与有界等待）；executableallocator / clrdefinitions 两段不动 |
| `probe/app/host.c` | 删等待循环与扫描；改提示文案 |
| `probe/app/main.m` | 不动（文案若引用旧流程顺手改） |
| `docs/PLAN.md` | 步骤 6 更新 |
| `docs/PLAN-JIT26.md` | 本计划（新增） |

## 5. 风险与回退

| 风险 | 影响 | 缓解 |
|---|---|---|
| 步骤 0 证伪：M1/iOS 26 分离即失竞技场 | 提前分离在该设备不成立 | 该设备沿用长会话旧脚本 + 旧构建；旧脚本补 fault 上报；换设备复测；结论进 PLAN.md |
| 无调试器时误发 brk | SIGTRAP 杀进程 | 双查门（CS_DEBUGGED + P_TRACED）+ 120s 有界等待，fail-closed |
| StikDebug 脚本宿主与 playport 脚本假设不符 | 脚本跑不动 | 只用 `get_pid/send_command/log` 三个已在用的 API；不依赖 `prepare_memory_region` |
| prepare 停机窗口内逐页扫写耗时 1–2 分钟 | 用户以为卡死 | 每 1024 页报进度（沿用现文案）；停机窗口无看门狗，安全 |
| detach 后初始化期 fault | 探针兜底捕获，干净失败 | sigsetjmp 此时才真正生效；失败链上屏可诊断 |
| 微软不测 `--dynamiccodecompiled true` 的 iOS 构建 | 编译/初始化意外 | 验收链即为此设（沿 PLAN.md 既有条目） |

## 6. 决策记录（完成后并入 PLAN.md）

| 决策 | 内容 | 依据 |
|---|---|---|
| JIT 授予协议（2026-11-13 起） | `brk #0xf00d` 同步握手（x16=1 prepare / x16=0 detach），app 驱动、脚本应答、拿到别名即分离，运行时全程无调试器 | playport-universal.js + wine_host.c（同族机制，生态验证）；长会话下 fault 派发被 debugserver 截走，进程内兜底失效——"探针窗口 3ms 死点"的机理 |
| 分离存续前提 | 逐页祝福在调试器分离后保留 | 生态三证：universal.js 应用群、STS2 探针形态（sigsetjmp 只在无调试器时可工作）、playport "the pool stays executable"；步骤 0 在本机复测钉死 |
