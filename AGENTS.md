# 仓库协作说明（AGENTS）

## 当前状态

- 这是 RimWorld 1.7 的 iOS 移植仓库。日常在 Windows 上改代码；Apple 目标由 GitHub Actions 的 macOS runner 编译。
- `docs/PLAN.md` 是决策与状态的权威记录。动手机层、Harmony/MonoMod、JIT 或触控输入前，先读它的 M4 / 0.4.x 段落。
- 当前已出包线是 `0.4.16`。最新真机测试包是 `test_work/m4-touch-20/port17host-m2.ipa`。
- `m4-touch-19` 已作废：其 IOS 触控桥在 review 中发现首个触摸必然回退的问题，`0.4.16` 已替换。

## 按任务阅读

| 任务 | 必读 |
|---|---|
| 里程碑、CI 出包、真机裁决 | `docs/PLAN.md` |
| Runtime / CoreCLR 行为、源码补丁、版本升级 | `runtime-pin.txt`、`docs/NET11-COMPARISON.md`、`patches/dualjit-arena.patch` |
| JIT 竞技场、调试器握手、W^X 限制 | `docs/PLAN-JIT26.md`、`patches/dualjit-arena.patch` |
| 宿主原生 ABI、GLFW shim、Vulkan/MoltenVK、动态库名 | `docs/ENGINE-NATIVE-CONTRACT.md` |
| 游戏导入、资源布局、设备内 setup | `docs/GAME-IMPORT.md` |
| 安卓同源的托管手机层 | `mobile/`、`hooks/`，以及 `docs/PLAN.md` 中的安卓对照说明 |

改了契约或裁决，必须在同一次修改中更新对应文档；不要只改代码。

## 本地 Runtime 参考

本机钉住的 runtime 源码路径：

```text
D:\Data\Code\OpenSourceProjects\runtime-110
```

- 可移植的版本真源是 `runtime-pin.txt`，当前为 `v11.0.0`。
- 2026-11-24 核对：该目录 origin 是 `https://github.com/dotnet/runtime.git`，HEAD 在 detached `v11.0.0`，commit `deae2e32bf0`。
- 该目录不是干净上游：五个 CoreCLR 源文件已带有竞技场 / dual-JIT 修改。读源码时按“已打补丁的参考树”理解，不要当成未修改上游。
- CI 与 `scripts/build-runtime.sh` 会另克隆 `runtime-src/`，checkout `runtime-pin.txt` 后应用 `patches/*.patch`。本地 `runtime-110` 是源码检索和证据链参考，不是规范构建输入。
- 构建或下载出的运行时工件在 `runtime-out-hybrid/`；`scripts/build-portlayers.py` 默认通过 `RT_DIR` 使用它。

沙箱 PowerShell 中查询外部仓库时使用：

```powershell
git -c safe.directory='D:/Data/Code/OpenSourceProjects/runtime-110' -C 'D:\Data\Code\OpenSourceProjects\runtime-110' ...
```

## 构建与验证

- Windows 本机使用 PowerShell；不要直接用 `bash`。`scripts/*.sh` 属于 macOS / CI 路径，除非任务明确说明。
- 托管层用 `python scripts/build-portlayers.py` 构建。它依赖本机 Steam 游戏、relaxed staging、Harmony 程序集、混合 runtime 和 Cecil；换机器用文档化环境变量覆盖，不要为单机临时改脚本。
- 改动后做对应验证：Harmony / MonoMod 用 `scripts/patch-harmony` 的 `smoke`；托管产物要反编译核对关键路径，并把 `prebuilt/*.dll` 哈希与解包 IPA 内容比对。
- iOS IPA 由 GitHub Actions 的 `m2-host.yml` 生成；修改 `host/**` 后 push 会自动触发出包。

## 真机与包纪律

- 每轮只使用明确指定的 `test_work/m4-touch-*` 目录，不要混入旧 run 的日志或 IPA。
- 离线编译、smoke、反编译通过不等于修复完成；触控 / runtime 修复必须等真机日志裁决。
- 包落位记录必须写入 `docs/PLAN.md`：CI run、IPA 哈希、解包版本、关键托管 DLL 哈希、smoke 结果、下次日志看什么。
- 保留用户或证据目录（例如 `.zcode/`、`raw10/`、`raw11/`、NET11 对照的 raw/reports），除非用户明确要求清理。
