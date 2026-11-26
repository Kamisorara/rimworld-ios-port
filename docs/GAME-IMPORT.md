# 游戏文件导入（M2 收官：引擎最小画面）

**两种玩法，任选**：

- **A. Steam 原件直接拷（推荐，安卓同款体验）**：把 Steam 游戏文件夹里 `Assets.zip`、
  `Version.txt`、`Data/`、`Rendering/` 和 63 个游戏程序集（清单见下）拖进 Port17 的
  Documents 下一个 `game` 文件夹（或者整个打成一个 zip 拖进去自动解压——**无需在 PC 上
  跑任何补丁脚本**）。开 app 后**自动在设备内改写游戏文件**（setup，约半分钟，日志滚动
  `[setup] …`），完成后提示重开 app，再开即引擎模式。机器类型、OS 探测、iOS 硬墙改写
  全部由 app 自己做（setup/Port17Setup.dll，Mono.Cecil）——安卓 SetupService 的对应物。
- **B. PC 侧打包**：`python scripts/pack-game-zip.py` 自动串全部补丁出 `port17-game.zip`
  （设备端零改写直接跑；调试新补丁时用这条）。

**进设备的口子（0.2.6 起，任选其一）**：

- **app 内「导入 zip」按钮（不依赖 Files 文件夹）**：诊断界面点「导入 zip」→ 系统文件
  选择器里挑 `port17-game.zip`（iCloud/隔空投送到手的/别的 app 文件夹都行）→ 拷成
  `Documents/port17-game.zip` 后自动安装。Files 里看不到 Port17 文件夹时这是正路。
- **「打开方式 → Port17」**：文件 app 里点 `port17` 开头的 zip → 分享 → 打开方式 →
  Port17，冷启动收件自动安装（`Documents/Inbox`）。
- **Files 文件夹直拷**：`On My iPad/Port17/Documents` 直接放 zip（文件夹可见时）。

导出：诊断界面「导出」按钮把存档+偏好（`Documents/AppData` 打包成
`port17-saves-backup.zip`）和 `port17-ui.log`、`port17-workshop.log`（0.4.43 起的创意工坊流水）送上分享面板（隔空投送/存储到文件/微信）。

目录约定（两种玩法安装后的形态）：

```
Documents/
  port17-game.zip          ← 你拷进来的安装包（装完自动改名 .installed，可删）
  game/                    ← 宿主解压安装出来的游戏目录
    RimWorld.dll           等游戏程序集（见下）
    Assets.zip
    Version.txt
    Data/                  Core + 各 DLC 目录
    Rendering/             Shaders/
    args.txt               可选：命令行，每行一个参数（默认只有 -NoCrashDialog）
    runtime.env            可选：NAME=value 每行一个，在宿主内建环境变量之后设置（NAME= 单独出现=取消）
    runtime.properties     可选：key=value 每行一个，CoreCLR 属性（可覆盖内建默认）
    PortHooks.dll          M3 之后放这里（STARTUP_HOOKS 自动生效）
  port17-ui.log            ← 宿主 / 启动器日志（在 Documents 根）
  coreclr-stderr.log       ← 游戏与运行时的 stderr，只留最后一次运行
  engine-trace.log         ← 引擎硬打点
  port17-status.txt        ← 引擎状态卡片（jit_arena 等）
  port17-workshop.log      ← 创意工坊流水（0.4.43 起）：开窗、登录方式、下载与安装耗时；不写账号名和 token
```

zip 安装语义：`Documents/game` 已有而新 zip 又到 = **换新版**（旧 game 自动挪成
`game.previous` 保留，确认新版能跑后手动删）；解压失败自动清理半成品并回落诊断界面。

## 在 PC 上准备（玩法 B；玩法 A 不需要任何 PC 步骤）

1. **放游戏文件**：`game-staging/`（gitignored）放上面清单的内容——从 Steam 目录
   （如 `D:\SteamLibrary\steamapps\common\RimWorld`，需 1.7.x）拷 `Assets.zip`、
   `Version.txt`、`Data/`、`Rendering/` 和 63 个游戏程序集（清单见下）。
2. **打包**：`python scripts/pack-game-zip.py`——打包前自动串两道补丁（都幂等）：
   `patch-os-detect.py`（OS 探测字节补丁，详见下节）+ `patch-platform`（Cecil 平台改写，
   安卓 patch 管线的 iOS 雏形，iOS 硬墙逐条记在 scripts/patch-platform/Program.cs 的改写表），
   产出仓库根的 `port17-game.zip`（0.98 GB → 约 0.78 GB；已压缩内容按 stored 存，设备端解压快）。
   设备内 setup（setup/Setup.cs）与这两个 PC 工具是同一副改写表的两个宿主——新硬墙
   两边同步加条目。

拷贝取舍：`Assets.zip`（339 MB）、`Version.txt`、`Data/` 整个（642 MB，Core 必需，
各 DLC 有则全带）、`Rendering/`、63 个 `.dll`。**框架副本不用拷**（app 自带 .NET 11
运行时），**Windows 原生 dll 不用拷**（iOS 用 app 自带的同名 .dylib）。偷懒法：游戏
目录里所有 `.dll` 全拷也无害（框架副本进不了 TPA、原生 dll 不会被装载），但 exe /
steam_api64* / *.vdf / *.html / 说明 txt 不必拷。

### 63 个游戏程序集清单

本体（4）：`Assembly-CSharp.dll`、`RimWorld.dll`、`RimWorldWaylith.PrepatcherCompat.dll`、`UnityStubs.dll`

Waylith 引擎（6）：`Waylith.dll`、`Waylith.AssetBake.dll`、`Waylith.Injection.dll`、`Waylith.UnityCompat.dll`、`Waylith.VideoDecode.dll`、`Waylith.Windowing.dll`

Brutal 底层（22）：`Brutal.Core.Collections/Common/Logging/Maths/Memory/Numerics/Package/Strings.dll`、`Brutal.FreeType.dll`、`Brutal.Gli.dll`、`Brutal.Gltf.dll`、`Brutal.ImGui.dll`、`Brutal.Interop.dll`、`Brutal.Ktx.dll`、`Brutal.Localization.dll`、`Brutal.Slang.dll`、`Brutal.Stb.dll`、`Brutal.Texture.dll`、`Brutal.Vulkan.Abstractions.dll`、`Brutal.Vulkan.Vma.dll`、`Brutal.Vulkan.dll`、`Brutal.ZLib.dll`

Steam（1）：`com.rlabrecque.steamworks.net.dll`

第三方托管库（30）：`AssetRipper.TextureDecoder.dll`、`AssetsTools.NET.dll`、`AssetsTools.NET.Texture.dll`、`CommunityToolkit.HighPerformance.dll`、`Fmod5Sharp.dll`、`ISharpZipLib.dll`、`IndexRange.dll`、`K4os.Compression.LZ4.dll`、`K4os.Compression.LZ4.Streams.dll`、`K4os.Hash.xxHash.dll`、`Microsoft.Bcl.Memory.dll`、`Microsoft.Extensions.Configuration.dll`、`Microsoft.Extensions.Configuration.Binder.dll`、`Microsoft.Extensions.DependencyInjection.dll`、`Microsoft.Extensions.Logging.dll`、`Microsoft.Extensions.Logging.Configuration.dll`、`Microsoft.Extensions.Logging.Console.dll`、`Microsoft.Extensions.ObjectPool.dll`、`Microsoft.Extensions.Options.ConfigurationExtensions.dll`、`NAudio.Core.dll`、`OggVorbisEncoder.dll`、`PsdSharp.dll`、`System.Configuration.ConfigurationManager.dll`、`System.Diagnostics.EventLog.dll`、`System.Diagnostics.EventLog.Messages.dll`、`System.IO.Hashing.dll`、`System.Security.Cryptography.ProtectedData.dll`、`System.Security.Permissions.dll`、`System.Windows.Extensions.dll`、`ZstdSharp.dll`

清单由 `scripts/copy-list.py <PC游戏目录> <官方 ios-arm64 运行时包 nupkg> [输出文件]` 生成
（规则 = 安卓 Setup.cs 的取舍：托管、不在运行时包里、非 .Native./DiaSymReader）。

### OS 探测补丁与平台改写（pack 脚本自动串，无需手动）

5014 引擎的原生绑定只认 Windows/Linux/macOS，iOS 上两处静态构造是硬墙：
`Brutal.Vulkan.dll` 的 Vulkan 装载链（iOS 全假 → "Failed to load Vulkan library"）与
`Brutal.Core.Common.dll` 的 Platform 家族判定（→ PlatformNotSupportedException）。
`patch-os-detect.py` 原地 5 字节/处改写（dnfile 定 token，幂等，写 `os-detect.port17` 戳）。
此外 iOS 的 .NET 构建里没有 `PosixSignalRegistration`（ProcessLifetime 注册信号即炸），
`patch-platform`（Mono.Cecil，安卓 Patch.cs 同款配方）把
`Waylith.Diagnostics.ProcessLifetime::RegisterPosixSignal` 改成 `return null`（Windows
一直走的路径；后续每个 iOS 硬墙都往这个改写表里加条目）。两者 `pack-game-zip.py`
都会自动跑；单独重跑也行：

```
python scripts/patch-os-detect.py <game文件夹>          # 或 --check 只看状态
dotnet run --project scripts/patch-platform -- <game文件夹>
```

## 传到 iPad

把 **`port17-game.zip` 这一个文件**拖进 Port17 的 Documents：

- **USB + Finder**：iPad 连线 → Finder → 设备 → 文件 → Port17 → 拖 zip 进去；
- **文件 app**：zip 经隔空投送/iCloud/网盘进 文件 app，移到「我的 iPhone/iPad →
  Port17 → Documents」。

然后开 Port17：自动安装（解压约十几秒，日志可见 `安装：…`）→ 引擎模式。

## 启动（引擎模式）

**Documents/game 里放了 RimWorld.dll，app 一打开就直接进引擎**（引擎模式；zip 安装完
自动满足）：5014 引擎的 iOS 窗口后端（Waylith.Windowing 的 Backends.IOS）要求主线程、
自己调 UIApplicationMain 建 UIWindowScene/CAMetalLayer，走 VK_EXT_metal_surface +
我们的 libvulkan→MoltenVK——宿主的诊断界面为此退位。预期过程：黑屏较久（引擎装载 +
Assets/Data 加载，stdout 顺序落 port17-ui.log），然后出画面。

引擎返回（失败或游戏退出）自动落回诊断界面，port17-ui.log 回放把过程摆上屏。
删掉/改名 Documents/game 再开 app = 回到纯诊断界面（三角形/探针/音频/库自检）。

预期日志链（port17-ui.log）：`安装：port17-game.zip 找到…` → `安装：game 就位` →
`引擎模式：Documents/game 在…` → `==== 引擎启动` → `机器类型补写 N 个程序集`（首次会写
RimWorld.dll 和 com.rlabrecque.steamworks.net.dll，幂等）→ `TPA 共 N（运行时 … + 游戏 …）`
→ `初始化 CoreCLR` → `CoreCLR 就绪` → 引擎 stdout 滚动 → 画面。

黑屏/闪退时：`Documents/port17-ui.log` 与 `Documents/coreclr-stderr.log` 拷回给开发（创意工坊的登录 / 下载问题另看 `port17-workshop.log`）
（整个 Documents 目录即可）。宿主对 Documents/game 的程序集做**原地** AMD64→ARM64
机器类型补写（纯 IL 的 2 字节标记修正，安卓 Setup.MarkArm64 同款）——介意的话重新拷
一份覆盖回来即可复原。

设备端实验旋钮（都放 Documents/game/）：`args.txt`（命令行）、`runtime.env`（环境变量）、
`runtime.properties`（CoreCLR 属性，可覆盖默认）。
