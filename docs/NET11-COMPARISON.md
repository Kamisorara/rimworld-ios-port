# .NET 10.0.12 → v11.0.0 运行时行为对比（CoreLib 重移植前置调研）

- 日期：2026-11-23
- 基线：`dotnet/runtime` tag **v10.0.12**（commit `4271d88e0aeb`）↔ 目标 tag **v11.0.0**（commit `deae2e32bf0e`，即 `runtime-pin.txt` 钉的 GA tag）
- 方法：两 tag 逐文件下载对照（gh api raw），锚点以「安卓 Cecil 补丁实际触碰的成员/指令形状」（`rimworld-android-port/patch/Patch.cs`）为准；`patches/dualjit-arena.patch` 对 v11.0.0 做了真实 `git apply` 实测。原始文件与全部 diff 留在 `test_work/net11/{raw10,raw11,reports}/` 可复核。
- 分级：**平滑**（无需动作）｜**可模拟**（源码补丁吸收）｜**需实测**（真机/PC 才能定案）｜**风险**（可能阻塞）

---

## 1. 总评

**10.0.12 → v11.0.0 是一次平滑迁移，CoreLib 重移植的工作量与安卓版几乎重合。** 20 个 `WaylithClr.*` 开关触碰的全部 CoreLib 补丁锚点在 v11.0.0 里原样存活（`GetHashCode`、`FormatFloat` 函数体、`DynamicMethod.Init/get_DeclaringType`、`ResolveUsingEvent` 的 `GetFirstResolvedAssemblyFromResolvingEvent` 调用、`RuntimeType.GetField` 前缀、`FieldAccessor.VerifyInitOnly`——后者所在文件两版**逐字节相同**）；Ludeon 五个私有 API 在 11 依然没有官方版，补齐项照旧。两个已知的补丁点漂移都是良性的：`FormatFloat` 因新增十六进制浮点格式化在文件里挪了位置（函数体逐字未变），`ResolveUsingEvent` 外层的 VM 回调改成了 `[UnmanagedCallersOnly]` 直调（最终仍进同一托管方法，拦截点不变）。`dualjit-arena.patch` 对 v11.0.0 **实测干净套用**，而且 11 上游反而更贴近我们的 hybrid 形状（`FEATURE_DYNAMIC_CODE_COMPILED` 成了 JIT 静态槽的正名条件、iOS 解释器进了上游 CI）。真正的意外收获只有一条：**安卓那处 libcoreclr 线程亲和性字节补丁，在 11 上游已被 PR #125361 正式修复（容忍 EPERM/EACCES），iOS 不需要，从清单划掉。** 唯一不能在本阶段定案的是 JIT 层的 IL 容忍度与内联策略演进（importer.cpp 一年 3153 行 diff 未逐行审），这本来就归 0.4.0 的 Harmony 探针与 PC 复测，不阻塞 CoreLib 补丁开工。

---

## 2. 差异清单

### A 组：开关行为面（逐条对照 TECHNICAL.md 3.3 表）

| # | 开关 / 行为面 | 10.0.12 | v11.0.0 | 差异与影响 | 结论 |
|---|---|---|---|---|---|
| A1 | `MonoStringHashCode`（`String.GetHashCode`） | `String.Comparison.cs:733`，体 = `Marvin.ComputeHash32(_firstChar, …)` | `String.Comparison.cs:691`，函数体逐字相同 | 该区域**无 diff**；`_firstChar` 字段仍在。补丁点原样平移。RimWorld 本体：无感；mod：Mono 哈希习惯照常模拟 | **平滑** |
| A2 | `MonoFloatFormatPrecision`（浮点默认格式 7/15 位） | `Number.Formatting.cs:561` `FormatFloat<TNumber,TChar>`，锚点 `fmt=='\0' → precision = TNumber.MaxPrecisionCustomFormat`（596-598） | 同函数挪到 `:999`，锚点在 `:1042-1045`；**函数体除新增 hex-float 早退分支外逐字相同**；`MaxPrecisionCustomFormat`（float 7/double 15）未变 | 位置漂移、语义未变。源码补丁反而比 Cecil 锚定更稳（直接改 `if` 条件）。附带微差：v11 新增浮点 `"X"` 十六进制格式（`ecc5874fb` #124139；Ludeon fork 基于 10，没有此特性）——`float.ToString("X")` 在我们的 11 上会出 hex 而电脑 Ludeon 抛格式异常，极边缘，不处理 | **平滑** |
| A3 | `LaxDynamicMethodAttributes` + `DynamicMethodOwnerIsDeclaringType` | `Emit/DynamicMethod.cs`：`Init`（:238 起）校验 attributes/callingConvention；`get_DeclaringType/get_ReflectedType => null`（`_typeOwner` 在旁） | 同文件 diff 仅琐项（`volatile` 删除、`[]` 集合表达式）；`Init` 在 v11:213，`DeclaringType/ReflectedType => null` 在 v11:317/319 | 校验逻辑与字段名未动；原生侧 `vm/dynamicmethod.cpp` 的 892 行 diff 是 CONTRACT 宏清扫 + `UnmanagedCallersOnlyCaller` 托管卸载（主题见 C 组注），无行为变化。补丁点原样平移 | **平滑** |
| A4 | `IsolatedLoadsResolveByName`（`Load(byte[])`/`LoadFile` 按名解析） | `AssemblyLoadContext.cs:672` `ResolveUsingEvent` → `:677` 调 `GetFirstResolvedAssemblyFromResolvingEvent`（:616）；`Assembly.cs` `Load(byte[],byte[])` 与 `LoadFile` | `ResolveUsingEvent` v11:712-717 **同一调用形状**（`GetFirstResolvedAssemblyFromResolvingEvent` v11:656）；`Assembly.cs:257/272` 两方法未被 diff 触碰（仅空程序集异常文案换 key） | 外层链路变了：VM→托管回调从 QCall 字符串编组改为 `[UnmanagedCallersOnly]` 指针直调（`eb6833928` #125697，ALC.cs:773+ 新增包装区），但**最终仍进同一实例方法**，拦截点不变。另新增 `SetAssemblyLocationOverride`（`89884375e` #129773，v11:312-348）——进程级 `Assembly.Location` 覆写，与本开关无关，无碰撞 | **平滑** |
| A5 | `LaxPrivateFieldNames`（反射 `GetField` 的 Mono 字段名） | `RuntimeType.CoreCLR.cs:2904-2908` 2 参 `GetField`，前三条指令 `ThrowIfNull` | v11:2964-2967，**该区域无 diff hunk**（diff 跳过 1858→3280） | 2 参重载仍是「`ThrowIfNull` → `FilterHelper` → `GetFieldList`」形状，改名 + 包装的源码改法照做 | **平滑** |
| A6 | `AllowInitOnlyStaticWrites`（反射写 `static readonly`） | CoreLib：`FieldAccessor.cs` `VerifyInitOnly()`；原生：`vm/invokeutil.cpp:742` `SetValidField`，initonly 检查在 `:811`（`IsStatic && IsClassInited && IsFdInitOnly`） | **`FieldAccessor.cs` 两版逐字节相同**（diff=0），`VerifyInitOnly` v11:382；`invokeutil.cpp` 的 `SetValidField` v11:661，同一检查形状在 `:730` | 两处检查点都没挪没改名；文件其余部分的变化是异常构造托管化（`CreateTargetExcept` 改调 `EXCEPTION::CREATE_TARGET_INVOCATION_EXCEPTION`），与 initonly 无关。安卓的 Cecil 清空 + 二进制 nop 双管齐下，iOS 版直接改两处源码 | **平滑** |
| A7 | `LaxAssemblyBinding` / `AssemblyNameRedirects` / `LaxAssemblyArchitecture` / `LaxTypeRefResolution` / `LaxMemberSignatures` / `LaxInheritedFieldRefs` / `LaxInterfaceDispatch` | 安卓侧全都不靠 runtime 补丁（类型转发门面 / `Resolving` 返简单名 / PE 改写 / ModPrep 规则 / 未模拟），与运行时版本弱耦合 | `vm/binder.cpp` 两版 diff 仅 41 行：泛型调用约定签名解析顺序修复（先 genericArgCount 后 argCount）+ include 调整 | binder 解析行为无实质变化；这些项按安卓原方案随 hooks 走，不进 runtime patch | **平滑** |
| A8 | `MissingAssemblyTypeLoadException`（缺程序集时抛 TypeLoadException） | 安卓方案 = 同名同版本空替身程序集放独立加载上下文（`hooks/MissingAssemblies.cs`），不依赖 runtime 行为 | binder 侧无相关变化（同 A7） | 机制版本无关，照搬即可 | **平滑** |
| A9 | JIT 的 `LaxILStack` / `LaxRetInTry`（栈深不一致 / try 内 `ret` 容忍度，Harmony 发射依赖） | 标准 JIT 拒绝两者；安卓在 `HarmonyCompat` 发射前把 IL 规整成平衡形状，跟踪不了的形状（calli/变参/条件跳转带值）原样放行 | `jit/importer.cpp` 一年 3153 行 diff（121 个 hunk）**未逐行审**；抽查：三处 static-field `InvalidProgramException` 判定原样（v11:9613/9928/10849）；void-`ret` 带栈值的 hunk 是 async tail-await 专用（#125697 相关期），非通用容忍度变化 | HarmonyCompat 产出的是平衡 IL，理论上 10 能过 11 就能过；但「跟踪不了的残留形状在 11 也同拒」未经源码定案。用安卓仓的办法（PC 上同一 Harmony 实测 21 种写法）在标准 11.0 重跑一次即可闭环 | **需实测**（PC 层面，非真机） |
| A10 | `JitNoInlinePatchableIntoDynamicMethods` / `JitPatchableAssemblies`（InlineGuard 的内联模型） | InlineGuard 的「可能内联了什么」模型用 **10.0.12** 内联事件实测（128 字节 / 直线代码放宽 1024 / try-finally 会内联等，TECHNICAL.md 3.7） | JIT 持续演进（importer diff 大、`clrconfigvalues.h` 的 `TC_CallCounting` 默认仍 1（v11:498）、`TieredCompilation` 默认仍 1（v11:485）——分层数延迟的前提未变） | 内联阈值/策略可能漂移，模型需在标准 11 上用 `InlineWatch` 重测一遍（PC 层面）；分层数计数延迟仍在，启动期防内联窗口的前提不变 | **需实测**（PC 层面） |

### B 组：Ludeon 私有 API 在 v11 的状态

| API | 10.0.12 | v11.0.0 | 结论 |
|---|---|---|---|
| `X86Base.ReadTimeStampCounter()` | 无 | **仍无**（v11 `X86/X86Base.cs` diff 只有内部 `BigMul` 新增 + `CpuId` 实现改走 `X86Base_CpuId` QCall，公共面未变） | 仍需补（PNSE 桩，arm64 永不触达，帧分析器内联需要它存在）——**可模拟** |
| `ArmBase.Arm64.ReadVirtualCounter()` / `…SelfSynchronized()` | 无 | **仍无**（v11 diff 只有 `nint/nuint` 重载新增） | 仍需补（`Stopwatch.GetTimestamp()` 桩）——**可模拟** |
| `AppDomain.DefineDynamicAssembly(name, access[, attributes])` | 无（AppDomain.cs 无此成员） | **仍无**（v11 AppDomain.cs diff 仅 FirstChanceException 武装开关 + 琐项，见 D 组） | 仍需补；包装目标 `AssemblyBuilder.DefineDynamicAssembly` 2/3 参公共 API 在 `src/coreclr/System.Private.CoreLib/src/System/Reflection/Emit/RuntimeAssemblyBuilder.cs:24/29`，**两版同文件同 12482 字节（diff=0）**——**可模拟** |
| `AssemblyBuilder.DefineDynamicModule(name, fileName)` | 无（只有单参） | **仍无**（`Emit/AssemblyBuilder.cs` 两版 diff=0，单参在 v11:20） | 仍需补——**可模拟** |
| `JitHelpers.UnsafeEnumCast` / `UnsafeEnumCastLong`（Mono 语义，公共化） | 内部类存在于 `src/coreclr/System.Private.CoreLib/src/System/Runtime/CompilerServices/JitHelpers.CoreCLR.cs` | **该文件两版 diff=0** | 仍需补公共替身；源码补丁下也可以直接在内部类上加公共入口（比安卓的 TypeCode 分发替身更省，实现时二选一）——**可模拟** |

### C 组：Apple arm64 / iOS 运行时内部

| # | 面 | 10.0.12 | v11.0.0 | 结论 |
|---|---|---|---|---|
| C1 | **PAL 线程启动的 `sched_setaffinity` 失败处理** | `pal/src/thread/thread.cpp:1554` 失败 → `ASSERT("sched_setaffinity failed!\n")` 硬失败（安卓被迫打字节补丁 @0x56a3bc） | **上游已修**：`b78173c38` **#125361** "Fix fatal sched_setaffinity EPERM failure during PAL initialization on restricted kernels"；v11 `thread.cpp:1441-1451` 对 `EPERM/EACCES` WARN+继续（注释点名 vendor Android 内核/沙箱场景） | **平滑**——安卓两项二进制补丁里的 affinity 补丁在 iOS 11 **不需要**（另一项 initonly 见 A6，原生检查点仍需源码删除） |
| C2 | **`FEATURE_MAP_THUNKS_FROM_IMAGE`（dualjit patch 第 1 hunk 触碰）** | `clrdefinitions.cmake:214-217` 该 define **被注释禁用**（等 diagnostics PR） | **重新启用**：v11 `clrdefinitions.cmake:239-241` 对全部 Apple 目标 `add_definitions(-DFEATURE_MAP_THUNKS_FROM_IMAGE)`（`f7ccc3cd2` #124168 "Make CoreCLR + crossgen2 ioslike via --dynamiccodecompiled false" 一带） | 若不打 patch，v11 的 iOS 构建会走 vm_remap 模板路径（iOS 26 不可行）——**dualjit patch 的 Apple→Apple&&!ARM64 条件从「预防」变成「必需」**。使用点 `vm/precode.cpp`（v11:533/683 的 ifdef 结构与 10 同形）、`vm/callcounting.cpp:276`、`vm/arm64/thunktemplates.S` 均保持 ifdef 双路，define 关掉即回经典分配路径。**可模拟（已在 patch 里）** |
| C3 | **`dualjit-arena.patch` 对 v11.0.0 的合上性** | patch 基线是 `release/11.0 @ 86ee4f57ebc`（2026-11-05，比 GA 早 15 天） | **实测**：把 v11.0.0 五个触碰文件摆进树里 `git apply` → **exit 0，无 fuzz**；`inc/executableallocator.h` 两版 diff=0；`minipal/Unix/doublemapping.cpp` 的 10→11 变化全在非 Apple 分支（WASI/OpenBSD 守卫、`TARGET_ANDROID` 移出 memfd 条件），patch 插入上下文原样 | **平滑**——pin 已在 GA tag，补丁不用动；将来 11.0.x 服务版升级用同一实测法验一遍 |
| C4 | **W^X / `--dynamiccodecompiled` 支持面** | `clrconfigvalues.h:645` `EnableWriteXorExecute` 默认非 RISCV64=1；`dlls/.../CMakeLists.txt` 里 JIT 静态槽条件是 `FEATURE_JIT` | W^X 默认值不变（v11:646-648）。**v11 把 JIT 静态槽条件改成 `FEATURE_DYNAMIC_CODE_COMPILED`**（`dlls/.../CMakeLists.txt:153-155`，且 `coreclr_static` 也走 `${CLRJIT_STATIC}/${CORECLR_STATIC_CLRJIT_STATIC}`）；上游新增 iOS 解释器功能测试（`07bc73e3c` **#119177**）与 clrinterpreter 入 iOS 包（`b7db8f4b6` **#117277**）；coreclr_static 的 mobile 安装目标从 sharedFramework 改为 `.`（`2cb91fd33` **#118951**——我们 CI collect 已在 v11 pin 上跑通，无需动作） | **平滑**——我们的 hybrid（DCC 开 + JIT 占静态槽 + 解释器按需 dylib）从「Microsoft CI 从不构建的矩阵格」变成上游设计形状；dualjit patch 第 2 hunk（`FEATURE_INTERPRETER AND NOT DCC`）与 v11 新逻辑语义一致、文本能套用 |
| C5 | **tiered compilation / R2R 默认值** | `TieredCompilation=1`、`TC_CallCounting=1` | 两者默认不变（v11:485/498）；官方 breaking change「Minimum hardware requirements updated」明文 **Apple arm64 最低硬件与 R2R 目标均不变**（x86/x64 与 Windows/Linux arm64 的 R2R 目标才升级）；新增 `UseGCWriteBarrierCopy` 在 DCC 下默认 0（可变写屏障优化关闭，纯 perf） | **平滑**——「包内 CoreLib 是 IL 直发（源码构建、无 crossgen2）」前提不受任何 11 变化影响 |
| C6 | 新运行时机制：`FEATURE_PORTABLE_ENTRYPOINTS`（v11 新，`clrfeatures.cmake:71-73` 仅 WASM 默认开；vm 侧 `precode.cpp:946+` 新增 `GetInterpreterCodeFromEntryPointIfPresent`、`precode_portable.hpp` 等） | 不存在 | iOS 构建不开 → 解释器预码路径与 10 相同。注记：将来若想尝鲜 portable entrypoints，stub/入口行为会变，需单独立项实测 | **平滑** |
| C7 | VM→托管回调全面 `[UnmanagedCallersOnly]` 化（#125697）：ALC 解析事件（ALC.cs:773+）、`invokeutil.cpp` 异常构造（`UnmanagedCallersOnlyCaller`）、`dynamicmethod.cpp` 等 | QCall + 字符串编组 | 行为等价（异常经 `Exception*` 回传），少一次编组；Harmony/MonoMod 不触碰这些内部入口 | **平滑** |
| C8 | `AssemblyNative_LoadFromPath` 丢掉 `niPath`（native image path）参数（`18696adec` #129597，`AssemblyLoadContext.CoreCLR.cs` diff 头两块） | 双路径 | 我们不传 NI 路径（CoreLib IL 直发） | **平滑**（注记：managed↔native 接口变了，若哪天给 CoreLib 上 R2R 需重查此链） |

### D 组：官方 breaking changes（dotnet/docs `docs/core/compatibility/11.md`）与 A/B/C 的交集

官方 .NET 11 清单逐条过——**与 A/B/C 三组交集为空**。沾边的只有：

| 条目 | 影响 | 结论 |
|---|---|---|
| JIT「Floating-point conversions to small integral types are saturating」（#128604） | 8/16 位整型饱和化；**arm64 本就按惯例饱和**（fcvtzs/fcvtzu），对我们 iOS 构建无行为变化（x86 才是重点） | 平滑 |
| 「Math.Round / Decimal·BigInteger 浮点转换正确舍入」 | 数值边缘微差，Ludeon fork（基于 10）与我们的 11 在这两个面上不再逐位同——RimWorld/mod 观感层面无已知依赖 | 平滑（注记） |
| 「Nullable.GetUnderlyingType throws for custom Type subclasses」 | 只影响自定义 `Type` 子类（Cecil 型工具库），RimWorld mod 生态无已知命中 | 平滑 |
| JIT「Minimum hardware requirements」 | Apple arm64 不变 | 平滑 |

另注：`AppDomain.FirstChanceException` 订阅后武装原生全量通知开关（v11 `AppDomain.cs:80-95`，`AppContext.SetFirstChanceExceptionHandler`）——帧分析器订阅场景行为不变、通知路径更快，归 C7 主题。

---

## 3. 并入 `patches/waylithclr-corelib.patch` 的改动清单

实施路线沿用既定决策：**源码补丁**（基线 = `runtime-pin.txt` 的 v11.0.0 GA tag，CI 自动重建缓存），不走安卓的 Cecil 后处理。每条给出：改哪个文件（v11.0.0 行号）、怎么改、安卓版对应实现位置（`patch/Patch.cs` 段号 / `host/patch-coreclr.js`）。

**托管侧（CoreLib C# 源）：**

| # | 文件（v11.0.0）与位置 | 改动 | 安卓对应 |
|---|---|---|---|
| 1 | `src/libraries/System.Private.CoreLib/src/System/String.Comparison.cs:691-697` | `GetHashCode()` 体换成 Mono 固定算法（双 h1/h2 5381 循环 + `h1 + h2*1566083941`，截至首个 `\0`），可写成私有 `GetMonoHashCode()` + `GetHashCode()` 调它（保留 AggressiveInlining），直接复用 `_firstChar` 做固定定位 | `Patch.cs:626-742`（`PatchCoreLib` 里 IL 注入 `GetMonoHashCode` + 重写 `GetHashCode`） |
| 2 | `…/System/Number.Formatting.cs:1042-1045` | `if (fmt == '\0')` → `if (fmt == '\0' || ((fmt | 0x20) == 'g' && precision <= 0))`（即 "G"/"g"/空格式无精度时取 `MaxPrecisionCustomFormat` = 7/15） | `Patch.cs:953-988`（`AddLudeonSwitches` 开头的 FormatFloat 条件改写，语义相同） |
| 3 | `…/Reflection/Emit/DynamicMethod.cs:213+ / :317 / :319` | `Init` 去掉 attributes/callingConvention 强校验；`DeclaringType/ReflectedType` 在无 type owner 时返回 declaring type（保留 `_typeOwner` 语义并按安卓替身对齐） | `Patch.cs:743-851` |
| 4 | `…/System/Loader/AssemblyLoadContext.cs:712-717` + `…/System/Reflection/Assembly.cs:257/272` | ALC 加 `static Hashtable s_waylithIsolatedLoads`（`.cctor` 里 `Hashtable.Synchronized(new())`）+ `internal WaylithRegisterIsolatedLoad(Assembly)` + `private WaylithResolveFirst(AssemblyName)`（先查表、miss 落 `GetFirstResolvedAssemblyFromResolvingEvent`，v11:656）；`ResolveUsingEvent:717` 的调用点换成 `WaylithResolveFirst`；`Assembly.Load(byte[], byte[])(:257)` 与 `LoadFile(:272)` 返回处注册。源码级写法比 Cecil 版直白：三个方法手写 C# 即可 | `Patch.cs:1013-1122`（Hashtable 字段、register/resolve 方法、`ResolveUsingEvent` 调用点替换、两处 Load 注册） |
| 5 | `…/System/RuntimeType.CoreCLR.cs:2964-2967` | `GetField(string,BindingFlags)` 前插 Mono 名匹配（strip `m_`/`_`，大小写不敏感、唯一匹配才替换；命中数 >1 或类型冲突不猜） | `Patch.cs:1123-1225`（`LaxPrivateFieldNames`） |
| 6 | `…/Reflection/FieldAccessor.cs:382`（`VerifyInitOnly`） | 静态 readonly 的反射写直接跳过检查；实例 initonly 语义保留 | `Patch.cs:1226+`（清检查 + 原生侧双保险） |
| 7 | 原生 `src/coreclr/vm/invokeutil.cpp:661/730` | `SetValidField` 的 `IsFdInitOnly && IsStatic && IsClassInited` 分支按安卓语义移除/放行，和 #6 同一裁决 | 安卓 host 字节补丁 |
| 8 | `…/Reflection/Emit/RuntimeAssemblyBuilder.cs:24/29` | 给 `AppDomain.DefineDynamicAssembly` 2/3 参补公共包装（PC mod 常用签名） | `Patch.cs:539-625` |
| 9 | `…/Reflection/Emit/AssemblyBuilder.cs:20` | 给 `DefineDynamicModule(name, fileName)` 补公共单参包装 | `Patch.cs:626-742` 中相邻段 |
| 10 | `…/Reflection/ResolveEventHolders.cs` / `Assembly.cs`（按 v11 锚点） | `ResolveUsingEvent` 先走 Android 的简单名注册表 | `Patch.cs:925-1012` |
| 11 | 公共 `System.Runtime.CompilerServices.JitHelpers` | 二选一：按安卓做公共替身（`UnsafeEnumCast/Long`：按 `Type.GetTypeCode(GetUnderlyingType)` 分发宽度读位）；或（源码补丁优势）直接在内部 `JitHelpers.CoreCLR.cs`（v11 与 10 diff=0）的对应方法上加公共包装类。mod 面是 Fishery 的枚举键比较 | `Patch.cs:859-924` |

**原生侧（CoreCLR vm/build）：**

| # | 文件（v11.0.0） | 改动 | 安卓对应 |
|---|---|---|---|
| 12 | `clrdefinitions.cmake` / `dlls/mscoree/coreclr/CMakeLists.txt` / `inc/executableallocator.h` / `minipal/Unix/doublemapping.cpp` / `utilcode/executableallocator.cpp` | 先套现有 `patches/dualjit-arena.patch`（实测 v11.0.0 无 fuzz）；CoreLib 重移植时在同一基线上追加 initonly 检查放行 | `patches/dualjit-arena.patch` + 安卓 libcoreclr 字节补丁的 initonly 部分 |

维护提示：patch 基线随 `runtime-pin.txt` 走；本次实测 `dualjit-arena.patch` 对 GA tag `git apply` 干净通过，同一 `git apply --check` 实测法留作每次 pin 升级的验机步骤（原始 v11.0.0 文件与全部 diff 在 `test_work/net11/` 可复用）。

## 4. 不用再模拟的项（11 上游变化直接吸收或被既有机制覆盖）

1. **线程亲和性失败**（安卓 libcoreclr 补丁之一）：#125361 已在上游容忍 EPERM/EACCES（C1）。安卓侧当年是钉核 ROM 特有环境，iOS 本就「大概率不需要」——现在源码级确认不需要。
2. **iOS 解释器包支持**：#117277 后官方 iOS 包带 clrinterpreter；我们的 hybrid 仍自己构建，但解释器 ABI/装配不再是民间猜测。
3. **ioslike 构建形状**：#124168 的 MAP_THUNKS/DCC 变化让我们要做的 hybrid 从「对抗上游」变成「上游已有闸门」，dualjit patch 只需要把闸门拨到我们需要的组合。

## 5. 需真机/PC 实测才能定案的项

1. **Harmony detour 在 v11 运行时上的成败**（含 MonoMod 对 11 的运行时识别/降级）——0.4.0 探针件既定项，CoreLib 补丁开工前先看它。
2. **HarmonyCompat 的 IL 残留形状在 11 JIT 上**（A9）：importer.cpp 未逐行审，用安卓仓「PC 上同一 Harmony 实测 21 种写法」的办法在标准 11.0 重跑一次（PC 层面即可闭环，不必等真机）。
3. **InlineGuard 内联模型在 11 JIT 上**（A10）：用 `InlineWatch`（EventListener 内联事件）在标准 11 重测 10.0.12 得出的阈值模型（128/1024 字节、try-finally 内联等）是否漂移。
4. **帧分析器时钟桩的内联可达性**（B 组 #7/#8）：安卓在 10 上实证「方法存在即内联编译通过」；11 JIT 对小方法内联的决策有持续演进，真机开帧分析器验一次（低概率问题）。
5. （远期）若考虑 `FEATURE_PORTABLE_ENTRYPOINTS` 或给 CoreLib 上 R2R：两者在 11 都有结构性新面（C6/C8），立项时单独实测。

## 6. 证据与可复核性

- 两 tag 原文件：`test_work/net11/raw10/`（v10.0.12）、`test_work/net11/raw11/`（v11.0.0）；逐文件 diff：`test_work/net11/reports/`。
- `dualjit-arena.patch` apply 实测：`test_work/net11/applytest/`（v11.0.0 五文件树 + `git apply` exit 0）。
- PR 归因：#125361（affinity 容忍）、#124168（ioslike 构建/MAP_THUNKS 重启用/DCC 闸门期）、#125697（VM 回调 UnmanagedCallersOnly 化）、#129597（LoadFromPath 丢 niPath）、#129773（SetAssemblyLocationOverride）、#124139+#135282（浮点十六进制格式化）、#119177/#117277（iOS 解释器 CI 与打包）、#118951（mobile coreclr_static 安装目标）、#128604（fp→小整型饱和，官方 breaking change 文）。
