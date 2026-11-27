# WaylithClr 模拟面验证计划（11.0 CoreLib ↔ Ludeon 10.0.12 行为差异）

- 2026-11-27 立。对应 PLAN.md「开放风险」第 3 行（11.0 CoreLib 与 10.0.12 行为差异）。
- 前置阅读：`docs/NET11-COMPARISON.md`（2026-11-23 逐文件锚点对照）、安卓仓 TECHNICAL.md 3.3（20 开关清单）。
- 结论入口：本文件 §2 盘点表的「状态」列；全部闭环后回写 PLAN.md 风险行。

## 1. 背景与问题定义

- 我们自建 runtime pin 在 v11.0.0；Ludeon fork 基于 10.0.12 且带 lax 私有改动。模拟面 = 两层差异叠加：标准 10↔11 上游差异 + Ludeon 对 10.0 的私有行为。现有实现三处：
  - `patches/waylithclr-corelib.patch`（12 项，v11.0.0 基线）
  - hooks 层开关（ModPrep / StartupHook / HarmonyCompat / InlineGuard / MissingAssemblies）
  - `hooks/WaylithClr.cs` 替身面（血缘跟到 1.7.5014）
- `NET11-COMPARISON.md` 已判定：20 开关的 CoreLib 补丁锚点全部存活、C 组（Apple/iOS 内部）全平滑、D 组官方 breaking changes 交集为空。但**「锚点存活」≠「行为逐项核对过」**，且该文第 5 节明确标注的三处「需实测」项从未执行。
- 0.4.45 的 15-mod 真机通过是抽样证据，不是系统验证。
- 本计划 = 把 NET11-COMPARISON 第 5 节的待实测项落地 + 给已实现开关补一张行为核对矩阵，最终裁决风险行要不要扩模拟面。

## 2. 模拟面盘点（验证对象）

| # | 开关 | 实现位置 | 验证手段 | 状态 |
|---|---|---|---|---|
| 1 | MonoStringHashCode（Mono 哈希，h1+h2*1566083941） | patch #12（String.GetHashCode） | P2 探针 | 未核对 |
| 2 | MonoFloatFormatPrecision（G 无精度=7/15） | patch #6（Number.Formatting） | P2 探针 | 未核对 |
| 3 | LaxDynamicMethodAttributes + DynamicMethodOwnerIsDeclaringType | patch #8（DynamicMethod.Init） | P2 探针 | 未核对 |
| 4 | IsolatedLoadsResolveByName | patch #7/#11（ALC 简名表） | P2 探针 | 未核对 |
| 5 | LaxPrivateFieldNames（m_↔_，框架类型限定） | patch #5 + ModPrep + StartupHook | P2 探针 | 未核对 |
| 6 | AllowInitOnlyStaticWrites（托管 FieldAccessor + 原生 invokeutil.cpp） | patch #9/#4 | P2 探针 | 未核对 |
| 7 | AppDomain.DefineDynamicAssembly / DefineDynamicModule(name,fileName) | patch #5/#2 | P2 探针 | 未核对 |
| 8 | JitHelpers.UnsafeEnumCast/Long（宽度保真 + 无符号截断） | patch #10（JitHelpers.Waylith.cs） | P2 探针 | 未核对 |
| 9 | LaxAssemblyBinding / AssemblyNameRedirects | StartupHook | P2 探针 | 未核对 |
| 10 | LaxAssemblyArchitecture / LaxTypeRefResolution / LaxMemberSignatures / LaxInheritedFieldRefs / LaxInterfaceDispatch | ModPrep | P2 探针（借真实 mod 样本） | 未核对 |
| 11 | MissingAssemblyTypeLoadException（空替身程序集） | hooks/MissingAssemblies | P2 探针 | 未核对 |
| 12 | WaylithClr 替身面（MethodBodyUpdater / MethodBodyTransformer / FieldInjection / SelfProfiler） | hooks/WaylithClr.cs | V1 扫描（scan-waylithclr 基线 103/103）+ P2 异常消息逐字符比对 | 面已扫（103/103），语义未对照 |
| 13 | LaxILStack / LaxRetInTry（Harmony 残留 IL 整形） | hooks/HarmonyCompat.cs | P3a 21-shape 矩阵 | 2026-10-30 PC 测过（11 过/10 拒），**标准 11 未重跑** |
| 14 | 内联模型（InlineGuard 阈值：128 字节/直线放宽 1024/try-finally 会内联） | InlineGuard + InlineWatch | P3b 设备重测 | 安卓 2026-10-23 基线在，**11 未测** |
| 15 | 帧分析器时钟桩内联可达性 | patch B 组（Arm64.ReadVirtualCounter 等） | P3c 真机开帧分析器一次（低概率） | 未测 |

## 3. 验证方法（三层）

- **V1 静态核对（PC 上，无真机）**：本文件矩阵文档维护；`test_work/net11/{raw10,raw11}` 锚点复检（每开关一锚点，diff 可复核）；`scripts/scan-waylithclr/` 对当前 Steam 版本重跑。
- **V2 行为探针（oracle = PC RimWorld 自带的 Ludeon 10.0.12 fork）**：一个探针 mod（RuntimeIL 程序集，走 ModPrep）在 PC 游戏进程和 iOS 游戏进程各跑一遍，输出结构化 key=value，逐行 diff。**核心思想：不猜源码语义，拿 PC 版当 oracle**——差异项才需要扩模拟面；一致项记入矩阵闭环。
- **V3 JIT 面**：P3a = 21-shape transpiler 矩阵 mod（2026-10-30 那批）真机重跑；P3b = InlineWatch（`mobile.txt inlineWatch=1`，听 MethodJitInliningSucceeded）设备实测；P3c = 帧分析器真机开一次。

## 4. 工作项

| # | 内容 | 验收标准 | 在哪做 |
|---|---|---|---|
| W1 | 矩阵文档（本文件 §2 状态列随 W2–W7 更新） | 每行有状态与证据指针 | Windows 离线 |
| W2 | 探针 mod `WaylithProbe`：每开关一组用例（§5 清单），全部 try/catch，输出 `test_work/waylith-probe/result-{pc,ios}.txt`（含异常类型+消息前 120 字符） | 两端各出一份结果文件，diff 脚本出红绿表 | Windows 写码；PC 跑 oracle |
| W3 | A9 收口：21-shape 矩阵 mod 真机重跑 | 每形状「过/拒」与 10.0.12 fork 一致，或差异落在 HarmonyCompat 明确不支持的形状（带值跳出） | 真机（并 W7） |
| W4 | A10 收口：真机 inlineWatch=1，对照安卓 2026-10-23 基线（默认 8/36-37 处、TC_CallCounting=0→9/89、TieredPGO=0→3/7） | patched in X 计数与内联对象集不漂移；漂移则复核 InlineGuard 拦截面 | 真机（并 W7） |
| W5 | D 组裁决：float "X" 十六进制格式（11 独有，Ludeon 10 无）→ 堵或记账；Math.Round 正确舍入 → 记账；Nullable.GetUnderlyingType → 记账（mod 生态无命中） | 每项有明确决定写入矩阵 | PC/离线 |
| W6 | 替身面保鲜：scan-waylithclr 对当前 Steam 1.7.50xx 重跑（基线 103/103） | 103/103 或差异清单入矩阵 | Windows 本机 |
| W7 | 真机裁决：一次会话装 WaylithProbe + 21-shape mod，inlineWatch=1，顺带开一次帧分析器（#15） | 三份日志归 `test_work/waylith-probe/` | 真机 |
| W8 | 回写：NET11-COMPARISON 第 5 节逐项标状态；PLAN.md 风险行更新；缺口项按既有路线扩 patch/hook（锚点在 raw11 可复核）→ W2 探针回归 | 文档三处一致 | 离线 |

## 5. 探针用例清单（W2 细则）

每开关 2–4 条，全部带标签输出：
1. hash：`""`、`"a"`、中文`"边缘"`、含 `\0`、1KB 长串 → 十六进制哈希值逐字符比对。
2. float/double：`(0.1f).ToString()`、`ToString("G")`、`"G7"`、`"G15"`、RimWorld 常见坐标量级值；顺带测 `ToString("X")`（D 组 #1）。
3. DynamicMethod：非标准 attributes / callingConvention 构造不抛；`DeclaringType`/`ReflectedType` 读取。
4. `Assembly.Load(byte[])` 同简名加载两次 → 第二次解析指向第一个。
5. `typeof(某框架类型).GetField("m_XX")` lax 命中；非框架类型不命中。
6. static readonly 字段反射写成功。
7. `DefineDynamicAssembly(AssemblyBuilderAccess.RunAndCollect)` → 实际 access 归一化结果。
8. `DefineDynamicModule("x.dll")` 不抛。
9. `UnsafeEnumCast((uint)-1)`、long 负值、ulong 大值截断 → 位型对照。
10. `WaylithClr.FieldInjection.Commit` → 异常消息逐字符比对；`SelfProfiler.DrainRuntimePauses()` → 0。
11. 缺失程序集引用触发 MissingAssemblyTypeLoadException 路径。
12. ModRules/签名 lax：借真实 mod 样本（MinifyEverything / HAR / Gravship Expanded）加载日志。

## 6. 判定标准与出路

- **全绿**：模拟面足够；PLAN.md 风险行降级为「已核对（见本文件）」。
- **有红**：这是预期中的「模拟面要扩」正路——逐项进 patch/hook（patch 加项按 NET11-COMPARISON 的锚点法，raw11 原文件可复核），修完 W2 探针回归。
- **不可收拾**（预计不会；需大面积红且锚点修不动）：才评估 cherry-pick Apple mobile 支持回 release/10.0——原缓解保留为最后手段。

## 7. 维护规程（并入既有升级规程）

- Steam 游戏每升一轮：scan-waylithclr 重跑（W6）+ 探针 mod PC 侧重跑 diff。
- runtime pin 每升一轮：dualjit-arena.patch 对新 GA tag `git apply --check` 验机（NET11-COMPARISON C3 的实测法）+ 本文件 §2 全表复检。
- Ludeon 出新 fork 线（1.8+）：`hooks/WaylithClr.cs` 头部血缘跟进 + 本计划整体重跑。
