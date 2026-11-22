# Port17-iOS：RimWorld 1.7 iOS 移植（非官方，探针阶段）

**中文**

> 非官方玩家项目，与 Ludeon Studios 无关，也未经其认可。本仓库不含 RimWorld 的任何代码、素材或二进制文件，安装包里也没有；运行需要玩家自己拥有的正版游戏。

这是 [Port17 安卓移植](../rimworld-android-port) 的姊妹项目：同一个目标（RimWorld 1.7，Waylith 引擎，.NET + Vulkan），换到 iOS 上。

**当前状态（2026-11-23）：M3 里程碑达成——JIT 成功，游戏全速可玩。** 真机实测：iPad M4 / iOS 27，
全 DLC 创建世界 → 世界生成 → 进入殖民地地图，装载速度较解释器显著提升，零崩溃。解释器模式（普通
启动，装完即玩）与 JIT 模式（StikDebug 会话拉起）同包双模：自建混合运行时（dotnet/runtime `v11.0.0`
GA tag，`--dynamiccodecompiled true` + 静态 RyuJIT + 按需解释器 dylib）+ StikDebug 竞技场协议
（app 发 `brk` 请求、脚本经 debugserver `_M` 代分配 128MiB RX 并逐页祝福、分离后照常执行）。
M2（UIKit 宿主 + GLFW 垫片 iOS 后端 + MoltenVK 1.4.2 + 触摸/软键盘桥，接口契约见
[docs/ENGINE-NATIVE-CONTRACT.md](docs/ENGINE-NATIVE-CONTRACT.md)）与设备内 setup（8 道幂等 Cecil
补丁，安卓 SetupService 的对应物）均已收官。下一站 M4：托管层共享（安卓触控优化完全对齐）。

## 技术路线（与安卓版的差异）

| | 安卓版 | iOS 版 |
|---|---|---|
| 运行时 | 微软官方 .NET 10 CoreCLR pack（android-arm64） | **自建混合运行时**：dotnet/runtime `v11.0.0` GA tag，`-os ios -arch arm64 --dynamiccodecompiled true` + `FEATURE_INTERPRETER=1`（JIT 静态链入 + 解释器按需 dylib；微软官方 iOS 包是解释器专用构建，无 JIT） |
| JIT | 系统允许 | StikDebug 调试器会话拉起后允许：app 发 brk 请求、脚本经 debugserver `_M` 代分配 128MiB RX 竞技场（普通启动自动回退解释器，同包双模） |
| 图形 | 系统 Vulkan | MoltenVK（Vulkan over Metal） |
| CoreLib 的 20 个 `WaylithClr.*` 开关模拟 | 补丁对着 10.0.12 CoreLib | **M4 重移植**到 11.0 CoreLib（随 mod 管线） |
| 宿主 | Java NativeActivity + `host.c` | ObjC/UIKit + `host.c`（同一套 `coreclr_initialize` 拉起方式） |
| 导入处理 | PortSetup 独立进程 | iOS 无 fork，须进程内 |
| 分发 | APK 直装 | 未签名 IPA，玩家经 SideStore/AltStore 自签侧载 |
| 构建 | 本地 Windows | Windows 写码，GitHub Actions 的 macOS runner 编译 |

托管层（PortHooks / PortMobile / ModPrep / patch.dll 的规则）计划从安卓仓库共享源码，M4 开始。

## 构建

完全在 GitHub Actions 上（公开仓库的 macOS runner 免费额度内）：

- `interp-probe`：官方 .NET 11 ios 运行时包 + M1 探针（解释器路线的回归基线）；
- `m2-host`：M2 宿主（UIKit + CAMetalLayer + GLFW 垫片 + libvulkan 前向层 + MoltenVK 1.4.2 按
  sha256 缓存 + glslang 现编 SPIR-V 三角形 + M1 探针诊断件），产物 `port17host-m2.ipa`；
- `m1-probe`：自建 CoreCLR（JIT26 分支，封存的实验性全速路线）；
- `mac-debug`：macOS 上复现宿主层问题。

手动触发或推送到 main 都会出包。产物在 Actions 的 Artifacts 里。

## 真机测试（0.3.3，双模）

1. 下载 [Release](../../releases) 里的 IPA，经 SideStore（或 AltStore）侧载——侧载自动带 `get-task-allow`；
2. **解释器模式（默认，装完即玩）**：普通打开 app。首次会自动在设备内跑 setup（改写游戏文件，约半分钟）后自动退出，再打开即引擎。性能为解释器基线；
3. **JIT 模式（同包，需一次会话仪式）**：
   - 前置（一次性）：StikDebug 导入设备配对文件；App Store 装 LocalDevVPN 并开启；连 WiFi；
   - 把 [`probe/stikdebug/rw17-jit26.js`](probe/stikdebug/rw17-jit26.js) AirDrop 到 iPad，StikDebug → Scripts 导入；
   - StikDebug 运行 `rw17-jit26`、目标选 Port17——app 自己发 `brk #0xf00d` 请求，脚本经 debugserver `_M,rx` 分配 128MiB RX 并逐页祝福（3-4 秒），把地址经 x0 还给 app，随后应答分离；**脚本显示 detached 后窗口可关**；
   - 日志（Files → Port17 → `port17-ui.log`）应出现：`JIT 授权检测 CS_DEBUGGED=1` → `[jit26] 竞技场拿到 …` → `[jit26] 探针 #1 → 42` / `#2 → 43` → `竞技场就绪` → `生效执行模式 = JIT`；
   - JIT 会话内创建/加载存档，存档与解释器模式互通；
4. 游戏资源导入：诊断界面「导入 zip」选 `port17-game.zip`（PC 上 `python scripts/pack-game-zip.py` 打包），或 Files 文件夹直放、「打开方式 → Port17」——三种口子任选，详见 [docs/GAME-IMPORT.md](docs/GAME-IMPORT.md)。

## 里程碑

见 [docs/PLAN.md](docs/PLAN.md)。

## 许可

MIT，见 [LICENSE](LICENSE)。
