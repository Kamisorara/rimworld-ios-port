# Port17-iOS：RimWorld 1.7 iOS 移植（非官方，探针阶段）

**中文**

> 非官方玩家项目，与 Ludeon Studios 无关，也未经其认可。本仓库不含 RimWorld 的任何代码、素材或二进制文件，安装包里也没有；运行需要玩家自己拥有的正版游戏。

这是 [Port17 安卓移植](../rimworld-android-port) 的姊妹项目：同一个目标（RimWorld 1.7，Waylith 引擎，.NET + Vulkan），换到 iOS 上。

**当前状态：M1（运行时探针）。** 还没有游戏，只有一个最小宿主 app + 自建的 iOS CoreCLR，用来在真机上回答三个问题：

1. 自建带 JIT 的 CoreCLR（`libcoreclr.dylib`）在 iOS 上能否启动并跑托管代码；
2. 经 StikDebug（调试器 attach）开后，Reflection.Emit + JIT 是否真的以机器码速度执行；
3. 进程里是否真的出现了匿名可执行内存（mach 区域扫描）。

## 技术路线（与安卓版的差异）

| | 安卓版 | iOS 版 |
|---|---|---|
| 运行时 | 微软官方 .NET 10 CoreCLR pack（android-arm64） | **自建**：dotnet/runtime `release/11.0`，`-os ios -arch arm64 --dynamiccodecompiled true`（微软官方 iOS 包 .NET 11 才有，且默认解释器） |
| JIT | 系统允许 | StikDebug 调试器 attach 后允许（`get-task-allow` 签名侧载） |
| 图形 | 系统 Vulkan | MoltenVK（Vulkan over Metal） |
| CoreLib 的 20 个 `WaylithClr.*` 开关模拟 | 补丁对着 10.0.12 CoreLib | **待重移植**到 11.0 CoreLib（M3） |
| 宿主 | Java NativeActivity + `host.c` | ObjC/UIKit + `host.c`（同一套 `coreclr_initialize` 拉起方式） |
| 导入处理 | PortSetup 独立进程 | iOS 无 fork，须进程内 |
| 分发 | APK 直装 | 未签名 IPA，玩家经 SideStore/AltStore 自签侧载 |
| 构建 | 本地 Windows | Windows 写码，GitHub Actions 的 macOS runner 编译 |

托管层（PortHooks / PortMobile / ModPrep / patch.dll 的规则）计划从安卓仓库共享源码，M4 开始。

## 构建

完全在 GitHub Actions 上（公开仓库的 macOS runner 免费额度内）：

- `runtime` job：按 `runtime-pin.txt` 检出 dotnet/runtime 并编译 iOS arm64 JIT 变体，按 pin 哈希缓存（pin 不变不重建）；
- `ipa` job：编译探针 app（clang + UIKit）和 `Hello.dll`（Reflection.Emit 计时探针），组 `Port17Probe.app`，ad-hoc 签名，打成 IPA 上传。

手动触发或推送到 main 都会出包。产物在 Actions 的 Artifacts 里。

## 真机测试（M1 验收）

1. 下载 IPA，经 SideStore（或 AltStore）侧载——侧载自动带 `get-task-allow`；
2. 直接打开 app：应显示 CoreCLR 启动、Hello 执行、耗时数字和"疑似解释器"的结论；
3. StikDebug attach 后再开：耗时数字应降到几十毫秒，结论变为"JIT 生效"；
4. 屏幕上同时打印 mach 匿名 RX 区域统计（JIT 开启时非零且变大）。

## 里程碑

见 [docs/PLAN.md](docs/PLAN.md)。

## 许可

MIT，见 [LICENSE](LICENSE)。
