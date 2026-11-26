# 本地构建：PortHooks.dll（完整启动钩）、PortMobile.dll（触控层）与 PortWorkshop.dll（创意工坊），
# 安卓 hooks/build.sh 与 mobile/build.sh 的 iOS 版。产物进 prebuilt/（入仓，CI 的 build-ipa.sh 拷进 .app）。
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
#   WORKSHOP_PKG  工坊 NuGet 编译引用的存放目录（默认 build/workshop-pkg；缺的按 build-ipa.sh
#                 同一份版本与 nupkg 哈希从 nuget.org 取）
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
    "Waylith.Windowing", "Brutal.Vulkan", "Brutal.Vulkan.Abstractions", "Brutal.Vulkan.Vma", "Brutal.Core.Numerics",
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

# 程序集版本钉 1.0.0.0：setup/Setup.cs 写进游戏程序集的 PortHooks 引用就是这个版本，
# CoreCLR 的 TPA 绑定要求版本精确匹配（m4-touch-6：0.0.0.0 的 dll 一装载即
# FileNotFoundException → Injection cctor → Environment.FailFast）。Roslyn csc 没有
# 程序集版本的命令行开关（-version 是"打印编译器版本并退出"），只能走特性。
attrs = os.path.join(out, "Version.cs")
with open(attrs, "w", newline="\n") as f:
    f.write('[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]\n')
    f.write('[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]\n')

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
    w(f'"{attrs}"\n')
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
        ref = HARMONY if name == "0Harmony" else (
            os.path.join(GAME, name + ".dll") if name == "Waylith.Windowing"
            else os.path.join(GAME_STAGE, name + ".dll"))
        w(f'-r:"{ref}"\n')
    w(f'-r:"{os.path.join(out, "PortHooks.dll")}"\n')
    for s in mobile_srcs + [builtfor, attrs]:
        w(f'"{s}"\n')
print(f"PortMobile: {len(mobile_srcs) + 1} 个源文件（含 BuiltFor {version}）")
csc(rsp)

for f in ("PortHooks.dll", "PortMobile.dll"):
    p = os.path.join(out, f)
    print(f, os.path.getsize(p), "bytes")

# ---- PortWorkshop.dll：创意工坊下载器（workshop/ 五个源文件，0.4.38 起）----
# 编译引用要游戏本体（Assembly-CSharp / UnityStubs：工坊窗口是游戏的 Window 子类）和刚编出的
# PortMobile，所以排在它之后。NuGet 依赖与 host/build-ipa.sh 4.991 钉同一份版本和 nupkg 哈希，
# 从 nuget.org 平面容器取进 build/workshop-pkg/（WORKSHOP_PKG 可覆盖），只抽编译要用的那一个 dll；
# 运行期进包的另几个（ZstdSharp 等）仍由 build-ipa.sh 取。此前 prebuilt/workshop.rsp 是手写的一次性
# 文件，指向带时间戳的临时目录，又随 .gitignore 的 prebuilt/*.rsp 一起不入库——换机器重编不了
# PortWorkshop.dll（0.4.43 开放风险）。源文件顺序照旧手写 rsp 的（不是排序）：顺序决定 TypeDef
# 行序，换序就不是逐字节同一个二进制；workshop/ 增删文件时下面的一致性检查会拦下来。
WORKSHOP_PKG = os.environ.get("WORKSHOP_PKG", os.path.join(IOS, "build", "workshop-pkg"))
WORKSHOP_REFS_RT = ["System.Runtime", "System.Console", "System.Collections", "System.Linq", "System.Threading",
    "System.Private.CoreLib", "System.Net.Http", "System.Net.Primitives", "System.IO.Compression",
    "System.IO.Compression.ZipFile", "System.Security.Cryptography", "System.Text.RegularExpressions",
    "System.Runtime.InteropServices", "System.Memory", "System.Private.Uri"]
WORKSHOP_GAME_REFS = ["Assembly-CSharp", "UnityStubs"]
WORKSHOP_SRCS = ["Downloads.cs", "Session.cs", "Updates.cs", "WorkshopWindow.cs", "WorkshopLog.cs"]
# (包 id, 版本, nupkg SHA256, nupkg 内路径)——与 build-ipa.sh fetch_workshop_dll 的同一份
WORKSHOP_NUGET = [
    ("steamkit2", "3.4.0", "cd90b5ac507c4eba3ee3439b62b8a5dab17d687dc51dfc81202b69f6c8643153", "lib/net10.0/SteamKit2.dll"),
    ("protobuf-net", "3.2.56", "2a3c111f21b07e54038d56ae753f8d8d19df1a11dbe02a427e7f611d58b123e1", "lib/net8.0/protobuf-net.dll"),
    ("protobuf-net.core", "3.2.56", "355bcbade0afbef9d2fecd2cc8be7f2f7991a577acc0fec4ef50ba58ff5fd837", "lib/net8.0/protobuf-net.Core.dll"),
    ("qrcoder", "1.8.1", "d3fe5c80c742b6a2ef8a31734c06600e2d674806a97f36f4af97b0d2871de309", "lib/net6.0/QRCoder.dll"),
]


def nuget_dll(pkg_id, version, want, path):
    """nupkg 不在 WORKSHOP_PKG 就下载；哈希对上才抽出 path，返回抽出的 dll 路径。"""
    import hashlib
    import urllib.request
    import zipfile
    os.makedirs(WORKSHOP_PKG, exist_ok=True)
    pkg = os.path.join(WORKSHOP_PKG, f"{pkg_id}.{version}.nupkg")
    if not os.path.isfile(pkg):
        url = f"https://api.nuget.org/v3-flatcontainer/{pkg_id}/{version}/{pkg_id}.{version}.nupkg"
        print(f"取 {url}")
        with urllib.request.urlopen(url, timeout=120) as r, open(pkg + ".part", "wb") as f:
            f.write(r.read())
        os.replace(pkg + ".part", pkg)
    with open(pkg, "rb") as f:
        if hashlib.sha256(f.read()).hexdigest() != want:
            sys.exit(f"{pkg} 的 SHA256 与 build-ipa.sh 钉的不一致，删掉重取")
    dll = os.path.join(WORKSHOP_PKG, os.path.basename(path))
    with zipfile.ZipFile(pkg) as z, open(dll, "wb") as f:
        f.write(z.read(path))
    return dll


if set(sources("workshop")) != {os.path.join(IOS, "workshop", s) for s in WORKSHOP_SRCS}:
    sys.exit(f"workshop/ 的源文件变了：WORKSHOP_SRCS {WORKSHOP_SRCS} 要同步改（顺序决定二进制）")
rsp = os.path.join(out, "workshop.rsp")
with open(rsp, "w", newline="\n") as f:
    w = f.write
    w("-nologo -target:library -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig "
      "-deterministic+\n")
    w(f'-out:"{os.path.join(out, "PortWorkshop.dll")}"\n')
    for name in WORKSHOP_REFS_RT:
        w(f'-r:"{os.path.join(RT, name + ".dll")}"\n')
    for name in WORKSHOP_GAME_REFS:
        w(f'-r:"{os.path.join(GAME, name + ".dll")}"\n')
    w(f'-r:"{os.path.join(out, "PortMobile.dll")}"\n')
    for pkg_id, version, want, path in WORKSHOP_NUGET:
        w(f'-r:"{nuget_dll(pkg_id, version, want, path)}"\n')
    for s in WORKSHOP_SRCS:
        w(f'"{os.path.join(IOS, "workshop", s)}"\n')
print(f"PortWorkshop: {len(WORKSHOP_SRCS)} 个源文件")
csc(rsp)
print("PortWorkshop.dll", os.path.getsize(os.path.join(out, "PortWorkshop.dll")), "bytes")

# ---- System.IO.FileSystem.Watcher.dll：iOS 运行时里的这个存根每个成员都抛 ----
# PlatformNotSupportedException，CameraPlus 这类 mod 在根 Start() 里 new 一个 FileSystemWatcher
# 就把开局拦下（0.4.41/0.4.42 真机）。scripts/unstub-watcher 用 Cecil 把存根的 61 个方法体
# 改写成“从不报告变化”的实现，输出进 prebuilt/，build-ipa.sh 6.06 盖进包里。不走 Harmony：
# 补丁是原生 detour，mod 里被 JIT 内联的存根单行 throw 会绕开它。
stub = os.path.join(RT, "System.IO.FileSystem.Watcher.dll")
watcher = os.path.join(out, "System.IO.FileSystem.Watcher.dll")
tool = os.path.join(IOS, "scripts", "unstub-watcher")
if os.path.isfile(stub):
    for args in (["unstub", stub, watcher], ["smoke", watcher]):
        rc = subprocess.run(["dotnet", "run", "-c", "Release", "--verbosity", "quiet", "--project", tool, "--"] + args).returncode
        if rc != 0:
            sys.exit(f"unstub-watcher {args[0]} 失败")
    print("System.IO.FileSystem.Watcher.dll", os.path.getsize(watcher), "bytes")
else:
    print("System.IO.FileSystem.Watcher.dll: 运行时目录里没有这个存根，跳过（手机上它仍全抛）")
