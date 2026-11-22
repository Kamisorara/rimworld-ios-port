# 游戏文件导入（M2 收官：引擎最小画面）

游戏文件经 **文件 app → 我的 iPhone/iPad → Port17 → Documents** 放进 app 容器
（Info.plist 已开 UIFileSharingEnabled）。目录约定：

```
Documents/
  game/                 ← 游戏目录（名字固定）
    RimWorld.dll        等游戏程序集（见下）
    Assets.zip
    Version.txt
    Data/               Core + 各 DLC 目录
    Rendering/          Shaders/
    args.txt            可选：命令行，每行一个参数（默认只有 -NoCrashDialog）
    runtime.env         可选：NAME=value 每行一个，在宿主内建环境变量之后设置（NAME= 单独出现=取消）
    runtime.properties  可选：key=value 每行一个，CoreCLR 属性（可覆盖内建默认）
    PortHooks.dll       M3 之后放这里（STARTUP_HOOKS 自动生效）
```

## 在 PC 上准备 game 文件夹

从 Steam 游戏目录（如 `D:\SteamLibrary\steamapps\common\RimWorld`，需 1.7.x）拷出：

| 拷什么 | 说明 |
|---|---|
| `Assets.zip`（339 MB） | 引擎资源包 |
| `Version.txt` | 版本号（宿主校验用不上，引擎读） |
| `Data/` 整个目录（642 MB） | Core 必需；Royalty/Ideology/Biotech/Anomaly/Odyssey 有则全带 |
| `Rendering/` 整个目录 | 引擎着色器源 |
| 下面 63 个 `.dll` | 游戏程序集（框架副本不用拷——app 自带 .NET 11 运行时；Windows 原生 dll 不用拷——iOS 用 app 自带的同名 .dylib） |

**偷懒法**：把游戏目录里所有 `.dll` 都拷进去也无害（框架副本进不了 TPA、原生 dll 不会被装载），
但 exe / steam_api64* / *.vdf / *.html / 说明 txt 不必拷。

### 63 个游戏程序集清单

本体（4）：`Assembly-CSharp.dll`、`RimWorld.dll`、`RimWorldWaylith.PrepatcherCompat.dll`、`UnityStubs.dll`

Waylith 引擎（6）：`Waylith.dll`、`Waylith.AssetBake.dll`、`Waylith.Injection.dll`、`Waylith.UnityCompat.dll`、`Waylith.VideoDecode.dll`、`Waylith.Windowing.dll`

Brutal 底层（22）：`Brutal.Core.Collections/Common/Logging/Maths/Memory/Numerics/Package/Strings.dll`、`Brutal.FreeType.dll`、`Brutal.Gli.dll`、`Brutal.Gltf.dll`、`Brutal.ImGui.dll`、`Brutal.Interop.dll`、`Brutal.Ktx.dll`、`Brutal.Localization.dll`、`Brutal.Slang.dll`、`Brutal.Stb.dll`、`Brutal.Texture.dll`、`Brutal.Vulkan.Abstractions.dll`、`Brutal.Vulkan.Vma.dll`、`Brutal.Vulkan.dll`、`Brutal.ZLib.dll`

Steam（1）：`com.rlabrecque.steamworks.net.dll`

第三方托管库（30）：`AssetRipper.TextureDecoder.dll`、`AssetsTools.NET.dll`、`AssetsTools.NET.Texture.dll`、`CommunityToolkit.HighPerformance.dll`、`Fmod5Sharp.dll`、`ISharpZipLib.dll`、`IndexRange.dll`、`K4os.Compression.LZ4.dll`、`K4os.Compression.LZ4.Streams.dll`、`K4os.Hash.xxHash.dll`、`Microsoft.Bcl.Memory.dll`、`Microsoft.Extensions.Configuration.dll`、`Microsoft.Extensions.Configuration.Binder.dll`、`Microsoft.Extensions.DependencyInjection.dll`、`Microsoft.Extensions.Logging.dll`、`Microsoft.Extensions.Logging.Configuration.dll`、`Microsoft.Extensions.Logging.Console.dll`、`Microsoft.Extensions.ObjectPool.dll`、`Microsoft.Extensions.Options.ConfigurationExtensions.dll`、`NAudio.Core.dll`、`OggVorbisEncoder.dll`、`PsdSharp.dll`、`System.Configuration.ConfigurationManager.dll`、`System.Diagnostics.EventLog.dll`、`System.Diagnostics.EventLog.Messages.dll`、`System.IO.Hashing.dll`、`System.Security.Cryptography.ProtectedData.dll`、`System.Security.Permissions.dll`、`System.Windows.Extensions.dll`、`ZstdSharp.dll`

清单由 `scripts/copy-list.py <PC游戏目录> <官方 ios-arm64 运行时包 nupkg> [输出文件]` 生成
（规则 = 安卓 Setup.cs 的取舍：托管、不在运行时包里、非 .Native./DiaSymReader）。

## 传到 iPad

任选：
- **USB + Finder**（最稳，1 GB 量级）：iPad 连线 → Finder → 设备 → 文件 → Port17 →
  把 `game` 文件夹拖进 Port17 的 Documents；
- **文件 app**：game 文件夹经隔空投送/iCloud/网盘到 文件 app，再移动到
  「我的 iPhone/iPad → Port17 → Documents」下。

## 启动

开 Port17 → 点「**引擎**」。预期日志链：`==== 引擎启动` → `机器类型补写 N 个程序集`
（首两次会写 RimWorld.dll 和 com.rlabrecque.steamworks.net.dll，幂等）→ `TPA 共 N（运行时 … + 游戏 …）`
→ `初始化 CoreCLR` → `CoreCLR 就绪` → 引擎 stdout 滚动（Assets/Data 加载）→ 画面。

黑屏/闪退时：日志回放看 `Documents/port17-ui.log` 与 `Documents/coreclr-stderr.log`
（拷回给开发：整个 Documents 目录即可）。宿主对 Documents/game 的程序集做**原地**
AMD64→ARM64 机器类型补写（纯 IL 的 2 字节标记修正，安卓 Setup.MarkArm64 同款）——
介意的话重新拷一份覆盖回来即可复原。
