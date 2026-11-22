# M4 本地构建：PortHooks.dll（完整启动钩）与 PortMobile.dll（触控层），安卓 hooks/build.sh
# 与 mobile/build.sh 的 iOS 版。产物进 prebuilt/（入仓，CI 的 build-ipa.sh 拷进 .app）。
#
# 为什么本地构建：两者都要引用游戏程序集（编译期符号，运行期不用），CI 无游戏文件（红线）。
# 为什么不用 SDK csproj：安卓版用 csc rsp 的逐字节确定性（-deterministic+），同版本游戏
# 构建产物一致；SDK 会注入自己的引用与属性，打破对齐。
#
# 材料路径（都能用环境变量覆盖）：
#   RIMWORLD_DIR  Steam 原件（1.7，含 UnityStubs.dll）
#   HARMONY_DLL   创意工坊 brrainz.harmony 的 1.7 0Harmony.dll
#   RT_DIR        自建混合运行时（runtime-out-hybrid，fetch-hybrid-runtime.sh 或本地解包）
#   CECIL_DLL     Mono.Cecil（nuget 缓存 0.11.5）
# 用法：python scripts/build-portlayers.py
import os
import subprocess
import sys

IOS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = os.environ.get("RIMWORLD_DIR", r"D:\SteamLibrary\steamapps\common\RimWorld")
# PortMobile 的编译引用要用 relax 过的游戏目录（static readonly 字段摘除 initonly——
# 安卓对 staged 游戏做同一件事；scripts/relax-initonly 对 game-stage-relaxed/ 做）。
GAME_STAGE = os.environ.get("GAME_STAGE", os.path.join(IOS, "game-stage-relaxed"))
HARMONY = os.environ.get(
    "HARMONY_DLL",
    r"D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll")
RT = os.environ.get("RT_DIR", os.path.join(IOS, "runtime-out-hybrid"))
CECIL = os.environ.get(
    "CECIL_DLL",
    os.path.expanduser(r"~\.nuget\packages\mono.cecil\0.11.5\lib\netstandard2.0\Mono.Cecil.dll"))

CSC = None
for root in [r"C:\Program Files\dotnet\sdk"]:
    base = root
    if os.path.isdir(base):
        sdk = sorted(os.listdir(base))[-1]
        cand = os.path.join(base, sdk, "Roslyn", "bincore", "csc.dll")
        if os.path.isfile(cand):
            CSC = cand
            break
if CSC is None:
    sys.exit("找不到 Roslyn csc.dll（装 dotnet SDK 10）")

HOOK_REFS_RT = ["System.Runtime", "System.Runtime.Loader", "System.Console", "System.Collections",
    "System.Collections.Concurrent", "System.Runtime.InteropServices", "System.Linq", "System.Threading",
    "System.Threading.Thread", "System.Security.Cryptography", "System.Reflection.Primitives",
    "System.Reflection.Emit.ILGeneration", "System.Reflection.Emit.Lightweight", "System.Reflection.Emit",
    "System.Threading.Tasks.Parallel", "System.Memory", "System.Net.Primitives", "System.Net.Http",
    "System.Private.Uri", "System.Private.CoreLib", "netstandard"]
MOBILE_REFS_RT = ["System.Runtime", "System.Runtime.Loader", "System.Console", "System.Collections",
    "System.Linq", "System.Threading", "System.Threading.Thread", "System.Private.CoreLib",
    "System.Runtime.InteropServices", "System.Numerics.Vectors", "System.Private.Xml",
    "System.Text.RegularExpressions", "System.Reflection.Primitives", "System.Reflection.Emit.ILGeneration",
    "System.Reflection.Emit", "System.Net.Http", "System.Private.Uri", "System.Net.Sockets",
    "System.Net.Primitives", "System.Text.Json", "System.Collections.Concurrent", "System.Memory",
    "System.Text.Encodings.Web", "System.Threading.Tasks.Parallel", "System.Collections.NonGeneric",
    "System.IO.Compression", "System.Diagnostics.Process", "System.ComponentModel.Primitives"]

GAME_HOOK_REFS = ["Waylith", "Brutal.Vulkan", "Brutal.Core.Common", "System.IO.Hashing"]
GAME_MOBILE_REFS = ["Assembly-CSharp", "UnityStubs", "0Harmony", "Waylith", "Waylith.UnityCompat",
    "Brutal.Vulkan", "Brutal.Vulkan.Abstractions", "Brutal.Vulkan.Vma", "Brutal.Core.Numerics",
    "System.IO.Hashing"]

missing = []
for f in GAME_HOOK_REFS + [f for f in GAME_MOBILE_REFS if f != "0Harmony"]:
    if not os.path.isfile(os.path.join(GAME, f + ".dll")):
        missing.append(f + ".dll")
for p in (HARMONY, CECIL, os.path.join(GAME, "Version.txt")):
    if not os.path.isfile(p):
        missing.append(p)
if missing:
    sys.exit("缺材料：\n  " + "\n  ".join(missing))


def sources(dirname):
    return [os.path.join(IOS, dirname, f) for f in sorted(os.listdir(os.path.join(IOS, dirname)))
            if f.endswith(".cs")]


def csc(rsp_path):
    subprocess.run([sys.executable, "-c",
        "import subprocess,sys;rc=subprocess.run(['dotnet',sys.argv[1],'@'+sys.argv[2]]).returncode;"
        "sys.exit(rc)", "exec", CSC, rsp_path], check=False) if False else None
    rc = subprocess.run(["dotnet", CSC, "@" + rsp_path]).returncode
    if rc != 0:
        sys.exit(f"csc 失败（{rsp_path}）")


out = os.path.join(IOS, "prebuilt")
os.makedirs(out, exist_ok=True)

# ---- PortHooks.dll：24 个同步文件 + iOS 验证版 WaylithClr.cs + hooks/ModPrep.cs ----
hook_srcs = [f for f in sources("hooks") if os.path.basename(f) != "BuiltFor.cs"]
rsp = os.path.join(out, "hooks.rsp")
with open(rsp, "w", newline="\n") as f:
    w = f.write
    w("-nologo -target:library -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+\n")
    w(f'-out:"{os.path.join(out, "PortHooks.dll")}"\n')
    for name in HOOK_REFS_RT:
        w(f'-r:"{os.path.join(RT, name + ".dll")}"\n')
    w(f'-r:"{CECIL}"\n')
    w(f'-r:"{HARMONY}"\n')
    for name in GAME_HOOK_REFS:
        w(f'-r:"{os.path.join(GAME, name + ".dll")}"\n')
    for s in hook_srcs:
        w(f'"{s}"\n')
print(f"PortHooks: {len(hook_srcs)} 个源文件")
csc(rsp)

# ---- PortMobile.dll：65 个文件 + BuiltFor.cs（游戏版本备忘，安卓 build.sh 同款） ----
version = open(os.path.join(GAME, "Version.txt"), encoding="utf-8").readline().split()[0]
builtfor = os.path.join(out, "BuiltFor.cs")
with open(builtfor, "w", newline="\n") as f:
    f.write(f'namespace PortMobile {{ internal static class BuiltFor '
            f'{{ public const string Version = "{version}"; }} }}\n')
mobile_srcs = sources("mobile")
rsp = os.path.join(out, "mobile.rsp")
with open(rsp, "w", newline="\n") as f:
    w = f.write
    w("-nologo -target:library -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig "
      "-deterministic+ -unsafe+\n")
    w(f'-out:"{os.path.join(out, "PortMobile.dll")}"\n')
    for name in MOBILE_REFS_RT:
        w(f'-r:"{os.path.join(RT, name + ".dll")}"\n')
    for name in GAME_MOBILE_REFS:
        # 0Harmony 不在 Steam 目录（创意工坊 mod 带的）：编译引用走 HARMONY_DLL（安卓
        # stage.sh 把它拷进 build/game 后同理）；其余走 relaxed staging（ PortMobile 写
        # 游戏 static readonly 字段，必须引用摘过 initonly 的副本——安卓对 staged 游戏
        # 同一件事，game-stage-relaxed/ 由 scripts/relax-initonly 产出）。
        w(f'-r:"{HARMONY if name == "0Harmony" else os.path.join(GAME_STAGE, name + ".dll")}"\n')
    w(f'-r:"{os.path.join(out, "PortHooks.dll")}"\n')
    for s in mobile_srcs + [builtfor]:
        w(f'"{s}"\n')
print(f"PortMobile: {len(mobile_srcs) + 1} 个源文件（含 BuiltFor {version}）")
csc(rsp)

for f in ("PortHooks.dll", "PortMobile.dll"):
    p = os.path.join(out, f)
    print(f, os.path.getsize(p), "bytes")
