# Port17 — RimWorld 1.7 的 iOS 移植（非官方）

[![Build](https://github.com/Kamisorara/rimworld-ios-port/actions/workflows/m2-host.yml/badge.svg)](https://github.com/Kamisorara/rimworld-ios-port/actions/workflows/m2-host.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[English](README.md) | **简体中文**

> **非官方的爱好者项目，与 Ludeon Studios 无关。**
> Port17 不附带任何游戏文件，也不出售任何东西。它只运行你**已经在 Steam 上购买**的那一份 RimWorld 的文件；没有这些文件，它无从运行。见[法律说明](#法律说明)。

Port17 是一个开源兼容层，让 **RimWorld 1.7** 的 Windows（Steam）版本——Ludeon 的 Waylith 引擎，即跑在 Vulkan 渲染器上的 .NET 代码——在 iPhone 和 iPad 上运行。无需越狱，也没有重新打包的游戏：你把自己 Steam 安装目录里的文件拷过来，其余由 Port17 提供：自带的 .NET 运行时、Metal 之上的 Vulkan、触控操作、模组支持，以及一个 Steam 创意工坊下载器。

**当前版本：0.4.45**（2026 年 11 月 26 日）。项目仍在积极开发中；[docs/PLAN.md](docs/PLAN.md) 是逐轮的工作记录。

## 现状

已在 iPad（M4，iOS 27）上实测。

| 方面 | 状态 |
|---|---|
| 游戏 | 能进主菜单、生成新世界、运行殖民地地图、存档和读档——本体加上你拥有的 DLC |
| 速度 | .NET 代码在设备上即时编译（JIT），见[工作原理](#工作原理) |
| 操作 | 触控：点按、双击、长按右键、双指缩放、TapAssist、列表惯性滚动、屏幕上的“左键 / 右键”按钮和 KeyWheel 按键轮盘 |
| 语言 | 游戏自带的语言；内置简体中文字体和表情符号后备字体，保证中日韩文字能显示 |
| 模组 | 随游戏加载，含 Harmony 模组；可导入 `.zip` 或文件夹形式的模组；随包附带一套社区加载顺序规则 |
| Steam 创意工坊 | 用你的 Steam 账号登录（扫码或密码），浏览订阅，下载并安装模组 |
| 存档 | 导入存档文件（`.rws`）；把存档和设置备份成一个 zip |

### 尚未做到

- **每次启动都需要 JIT 会话。** iOS 不允许普通应用创建可执行内存，所以 Port17 必须经 StikDebug 拉起（见[安装](#安装)）。按普通方式打开时，它会停在一则提示上，游戏不会启动。
- **没有 Steam 集成。** 成就等 Steam 功能不可用；Steam 只用来下载创意工坊模组。
- **没有文件监视。** 等待文件变化的模组不会收到通知。
- **内存。** iOS 超出内存上限时会直接杀掉应用，目前还没有针对这一点的调校。自 0.4.45 起应用会记一份内存账本（见 `port17-status.txt`），安卓那套回收也会在 iOS 上跑，但什么时候释放什么还没定，模组多、会话长的场景尚未实测。
- **少数 PC 专有 API 缺失。** 不能启动外部程序（链接改为在 Safari 中打开）；依赖它们的模组可能出错——请附上 `coreclr-stderr.log` 反馈。

## 工作原理

- **运行时。** 自建的 .NET 11 混合运行时（`dotnet/runtime` v11.0.0）：CoreCLR，链接进了 RyuJIT 编译器，另带解释器。在此之上有两个源码补丁：[`patches/dualjit-arena.patch`](patches/dualjit-arena.patch) 让 JIT 从预先授予的内存竞技场里分配，[`patches/waylithclr-corelib.patch`](patches/waylithclr-corelib.patch) 重建引擎依赖的 `WaylithClr.*` 运行时开关。微软自家的 iOS 运行时只有解释器。
- **可执行内存。** 应用启动时发出 `brk` 请求。一个运行 [`probe/stikdebug/rw17-jit26.js`](probe/stikdebug/rw17-jit26.js) 的调试会话替它分配 128 MiB 的可执行内存竞技场（带一份可写别名），交出地址后分离；此后运行时都从这块竞技场分配。
- **图形与音频。** 通过 MoltenVK 在 Metal 上跑 Vulkan。引擎的窗口和输入代码对接一个带 UIKit 后端的 GLFW 垫片（[`host/glfw/`](host/glfw/)）；音频走 Core Audio。
- **设备内初始化。** 首次运行时用 Mono.Cecil 原地修补游戏自己的程序集（[`setup/`](setup/)）：机器类型、系统检测，以及一小批 iOS 上不存在的 API。不需要电脑端工具。
- **托管层。** [`hooks/`](hooks/) 和 [`mobile/`](mobile/) 以启动钩子的形式随游戏加载：面向引擎的运行时垫片、触控输入、模组导入与准备、Harmony/MonoMod 支持，以及一些针对 iOS 的小修补。
- **创意工坊。** [`workshop/`](workshop/) 是一个基于 SteamKit2、在游戏内部运行的 Steam 创意工坊下载器。
- **构建。** IPA 完全由 GitHub Actions（macOS runner）通过 [`host/build-ipa.sh`](host/build-ipa.sh) 生成。

## 安装

### 1. 获取应用

- **最新构建：** GitHub Actions → [M2 Host](https://github.com/Kamisorara/rimworld-ios-port/actions/workflows/m2-host.yml) → 最新一次成功的运行 → 产物 `port17host-m2-ipa`。下载产物需要 GitHub 账号。
- [Releases](https://github.com/Kamisorara/rimworld-ios-port/releases) 页面只有里程碑版本（目前是 v0.3.3），比 Actions 里的产物旧。

### 2. 自签安装

用 SideStore、AltStore、LiveContainer 或任何能用你的 Apple ID 给应用签名的工具安装 IPA。不需要 Apple 开发者账号。

### 3. 拷入游戏文件

Port17 需要**你自己**那份 RimWorld（Steam → `steamapps/common/RimWorld`，1.7.x 版本）里的 `Assets.zip`、`Version.txt`、`Data/`、`Rendering/` 和游戏的托管程序集。最省事的做法是把它们打成一个 zip。把目录里所有 `.dll` 都放进去没有问题；`.exe`、`steam_api64*` 以及 `.vdf` / `.html` 文件不需要。DLC 的文件夹只放你拥有的 DLC。

把 zip（或这些文件）弄到设备上，任选其一：

- Port17 界面上的 `导入 zip` 按钮——系统文件选择器；iCloud 云盘、隔空投送和其他应用的文件夹都可用；
- “文件”App：*我的 iPad/iPhone → Port17 → Documents*；
- 用 USB 线，在访达里拷；
- 在“文件”App 里对 `port17…zip` *共享 → 用 Port17 打开*。

首次启动会为 iOS 原地改写这些游戏文件（约半分钟），之后需要再启动一次。

### 4. 启动（JIT 会话）

iOS 不给普通应用可执行内存，而这个运行时的 JIT 代码需要它。所以每次启动都要经过一个调试会话，由它交给 Port17 一块可执行竞技场：

1. **一次性准备：** 安装 StikDebug 并导入它的设备配对文件；安装一个能提供 JIT 的 VPN 应用（例如 LocalDevVPN），打开它，并让设备连上和电脑相同的 Wi-Fi；把 [`rw17-jit26.js`](probe/stikdebug/rw17-jit26.js) 隔空投送到设备并导入 StikDebug。
2. **每次启动：** 在 StikDebug 里以 **Port17** 为目标运行 `rw17-jit26`。Port17 会等待竞技场就绪（最多 120 秒），然后启动游戏；脚本显示 `detached` 后就可以关掉它的窗口。

### 5. 模组与存档

- **模组：** 通过 Port17 导入 `.zip` 或文件夹，或在游戏的模组界面打开创意工坊窗口。
- **工坊登录：** 扫码或密码；Steam 要求时需输入 Steam Guard 验证码。登录凭据保存在应用的私有目录里，更新后依然有效。你的密码和登录令牌都不会写进任何日志或导出包。
- **存档：** `导出` 会分享一个包含存档、设置和日志文件的 zip。

## 排错

Port17 把日志放在 *文件 → 我的 iPad/iPhone → Port17 → Documents*：

| 文件 | 内容 |
|---|---|
| `port17-ui.log` | Port17 启动器：安装、初始化和引擎启动 |
| `coreclr-stderr.log` | 游戏和 .NET 运行时的输出——只保留最近一次运行 |
| `engine-trace.log` | 引擎的计时标记，另有每次内存告警一行和本次会话的内存峰值 |
| `port17-status.txt` | 上一次会话的单行状态：JIT 竞技场、执行模式，自 0.4.45 起还有内存占用、剩余额度和峰值（`mem_*`） |
| `port17-workshop.log` | 创意工坊的登录、下载和安装 |

反馈问题时请附上这些文件。遇到黑屏，先看 `port17-ui.log` 和 `coreclr-stderr.log`。

## 从源码构建

iOS 应用在云端构建；你的电脑上不需要任何 Apple 相关的东西。

- **IPA：** 推送到 `main`（`host/` 下的改动）或手动运行 **M2 Host** 工作流；产物是 `port17host-m2-ipa`。其他工作流构建打过补丁的运行时（**m1-probe**，同时发布为 Release 资产）、解释器基线（**interp-probe**）和 macOS 调试构建（**mac-debug**）。
- **托管层：** `python scripts/build-portlayers.py` 把 `hooks/`、`mobile/` 和 `workshop/` 编译到 `prebuilt/`，并改写运行时的文件监视存根。它需要 .NET SDK、Mono.Cecil、Harmony 程序集和本机的 RimWorld 1.7 安装；首次运行需要联网拉取创意工坊的 NuGet 依赖（与 `host/build-ipa.sh` 相同的固定版本和包哈希；路径可以用环境变量覆盖，包缓存目录是 `WORKSHOP_PKG`）。产物与已提交的 DLL 逐字节相同。


## 仓库结构

| 路径 | 内容 |
|---|---|
| `host/` | iOS 原生宿主：UIKit 应用和启动器、GLFW 垫片、转发到 MoltenVK 的 Vulkan、音频、Windows 库的替代实现，以及 `build-ipa.sh` |
| `hooks/`、`mobile/` | 托管层：启动钩子、运行时垫片、触控输入、模组支持 |
| `setup/` | 设备内初始化，修补游戏的程序集 |
| `workshop/` | Steam 创意工坊下载器 |
| `scripts/` | 构建与修补工具（`build-portlayers.py`、`check-facades.py`、`patch-harmony`、`patch-platform`、`unstub-watcher`） |
| `patches/` | CoreCLR 源码补丁 |
| `probe/` | 运行时探针和 StikDebug 启动脚本 |
| `runtime-out-hybrid/`、`prebuilt/` | 打进 IPA 的运行时部件、托管二进制和 facade |
| `docs/` | 决策记录和指南 |

## 法律说明

**你必须拥有这款游戏。** 本仓库及其安装包不含任何 RimWorld 游戏文件：游戏的代码、数据、美术、音频和文本都不存放在这里，也不随 Port17 分发。应用只运行从你自己在 Steam 上购买的那份 RimWorld 里拷出的文件，并在你自己的设备上对它做适配。这里没有任何东西能让谁不买游戏就玩上——Port17 依赖那次购买，而不是取代它。

**为什么我们认为这是正当的。** Port17 是给游戏拥有者用的兼容工具，就像让现有程序能在另一个系统上运行的转译层。它免费、开源、不出售，也不转分发 RimWorld 的任何内容。游戏内容的唯一来源是玩家自己的购买。

**商标。** RimWorld 和 RimWorld 标志是 Ludeon Studios, Inc. 的商标。Steam 和 Steam 标志是 Valve Corporation 的商标。这是一个非官方的爱好者项目：与 Ludeon Studios 和 Valve 没有关联，也未获其认可或赞助。创意工坊功能用你自己的 Steam 账号登录，并受你与 Valve 之间的 Steam 订户协议约束。

如果你是权利人并有顾虑，请提 issue——我们会回应。

## 许可证

Port17 自己的代码以 [MIT 许可证](LICENSE)发布。

其余部分保留各自的许可证：.NET 运行时和 CoreCLR（MIT）、MoltenVK（Apache-2.0）、Harmony 和 MonoMod（MIT）、SteamKit2（LGPL-2.1-only）、protobuf-net（Apache-2.0）、QRCoder 和 ZstdSharp（MIT，见 [workshop/LICENSES.txt](workshop/LICENSES.txt)）、Noto Sans SC（[OFL](host/app/fonts/LICENSE-OFL.txt)），以及构建时获取或编译的其他库（`host/build-ipa.sh`、`host/fetch-*.sh`）。

## 文档

- [docs/PLAN.md](docs/PLAN.md) —— 决策、每一轮工作、真机裁决和包落位记录
- [docs/GAME-IMPORT.md](docs/GAME-IMPORT.md) —— 游戏文件、导入和目录布局的详细说明
- [docs/ENGINE-NATIVE-CONTRACT.md](docs/ENGINE-NATIVE-CONTRACT.md) —— 引擎与宿主之间的原生 ABI
- [docs/NET11-COMPARISON.md](docs/NET11-COMPARISON.md)、[docs/PLAN-JIT26.md](docs/PLAN-JIT26.md)、[docs/PERF-30FPS-PLAN.md](docs/PERF-30FPS-PLAN.md) —— 运行时研究、JIT 竞技场设计、性能计划
