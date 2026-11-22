# OS 探测补丁（M2 收官的装载前提，游戏文件的准备步骤——在 PC 上对拷贝跑，不动 Steam 原件）
#
# 5014 引擎的原生绑定只认 Windows/Linux/macOS 三个家族，iOS 上两个静态构造会翻车：
#   Brutal.Vulkan.dll   的 Vulkan 静态构造：只按 IsWindows(vulkan-1)/IsMacOS(VulkanEx)/
#                       IsLinux(vulkan) 选装载库，iOS 全假 → Dll=0 → NullReferenceException
#                       ("Failed to load Vulkan library")。补丁：那唯一一处 IsLinux() 调用
#                       → ldc.i4.1（true），装载名 "vulkan" 落到宿主的 libvulkan.dylib。
#   Brutal.Core.Common.dll 的 Platform 静态构造：三族全不中 → PlatformNotSupportedException
#                       ("Unsupported platform")。补丁：那唯一一处 IsMacOS() 调用 → true，
#                       Platform 家族 = MacOS（Darwin 真相：kqueue 文件监视、引擎自己的
#                       MVK_CONFIG_USE_METAL_ARGUMENT_BUFFERS=0 等路径全走对；Linux 族会在
#                       Darwin 上碰不存在的 inotify）。Mac 族随之多一次 NativeLibrary.Load
#                       ("vulkan.1")——宿主为此附带 libvulkan.1.dylib（前向层的第二副本）。
#
# 两个 dll 各只有这一个目标调用点（ilspycmd 反编译核实：两文件的 OperatingSystem.Is* 全部
# 位于目标静态构造内），所以"全文件搜 token、唯一命中即补"是安全的。补丁为 5 字节原地改写：
#   28 TT TT TT TT (call OperatingSystem::IsXxx)  →  17 00 00 00 00 (ldc.i4.1 + 4x nop)
# 幂等：命中点已是补丁形态即报"已补"。产物写 <game>/os-detect.port17 戳文件，宿主据此提示。
#
# 用法：python patch-os-detect.py <游戏目录（含两个 dll 的那份）> [--check]
# 依赖：pip install dnfile（元数据表解析，找 MemberRef 的 token；不写 dll 的元数据结构）
import sys, os

try:
    import dnfile
except ImportError:
    print("缺 dnfile：pip install dnfile"); sys.exit(2)

# (文件, 方法名) —— 补丁把它们在目标位置的调用改为恒真
TARGETS = [
    ("Brutal.Vulkan.dll", "IsLinux"),
    ("Brutal.Core.Common.dll", "IsMacOS"),
]

def memberref_token(path, method):
    """dll 里 System.OperatingSystem.<method>() 的 MemberRef token（0x0A 行号 | 0x0A000000）。
    行号从 1 数；确认父 TypeRef 的名字与命名空间，杜绝同名误中。
    解析完把 dnfile 的对象丢掉再回收——它可能持有文件句柄，Windows 上会挡住后面的写。"""
    import gc
    pe = dnfile.dnPE(path)
    # TypeRef 行号 → (namespace, name)
    typerefs = {}
    t = pe.net.mdtables.TypeRef
    for i, row in enumerate(t.rows, 1):
        typerefs[i] = (row.TypeNamespace, row.TypeName)
    hits = []
    for i, row in enumerate(pe.net.mdtables.MemberRef.rows, 1):
        if row.Name != method:
            continue
        table = row.Class.table
        if table is not None and table.name == "TypeRef":
            idx = row.Class.row_index
            ns, name = typerefs.get(idx, ("", ""))
            if ns == "System" and name == "OperatingSystem":
                hits.append(0x0A000000 | i)
    del pe, t, typerefs
    gc.collect()
    return hits

def patch_one(gamedir, dll, method):
    path = os.path.join(gamedir, dll)
    if not os.path.isfile(path):
        return f"{dll}: 不在（这一目录里没有它）", False
    tokens = memberref_token(path, method)
    if len(tokens) != 1:
        return f"{dll}: System.OperatingSystem::{method}() 的 MemberRef 有 {len(tokens)} 个（期望 1）——版本变了？不补", False
    token = tokens[0]
    with open(path, "rb") as f:
        data = bytearray(f.read())
    needle = bytes([0x28, token & 0xFF, (token >> 8) & 0xFF, (token >> 16) & 0xFF, (token >> 24) & 0xFF])
    patched = bytes([0x17, 0, 0, 0, 0])
    at = []
    start = 0
    while True:
        i = data.find(needle, start)
        if i < 0:
            break
        at.append(i)
        start = i + 1
    if len(at) == 0:
        # 幂等检查：补丁形态 + 上一条/下一条指令无从对齐，退而求其次看形态数量
        if data.find(patched) >= 0:
            return f"{dll}: OperatingSystem::{method}() 的调用已不在（已补过）", True
        return f"{dll}: 找不到 call OperatingSystem::{method}()（token 0x{token:08x}）——没补也没找到", False
    if len(at) > 1:
        return f"{dll}: call 出现 {len(at)} 处（期望 1）——反编译核实过只有一处，版本变了？不补", False
    i = at[0]
    data[i:i+5] = patched
    tmp = path + ".port17tmp"
    with open(tmp, "wb") as f:
        f.write(bytes(data))
    os.replace(tmp, path)
    return f"{dll}: OperatingSystem::{method}() → true（文件偏移 0x{i:x}）", True

def main():
    if len(sys.argv) < 2:
        print(__doc__); sys.exit(2)
    gamedir = sys.argv[1]
    check = "--check" in sys.argv[2:]
    ok = True
    lines = []
    for dll, method in TARGETS:
        if check:
            path = os.path.join(gamedir, dll)
            # 检查态：报告 token 与命中数，不写；call 已不在而补丁形态在 = 已补过
            if not os.path.isfile(path):
                lines.append(f"{dll}: 不在"); ok = False; continue
            tokens = memberref_token(path, method)
            if len(tokens) != 1:
                lines.append(f"{dll}: MemberRef {len(tokens)} 个（期望 1）"); ok = False; continue
            token = tokens[0]
            with open(path, "rb") as f:
                data = f.read()
            needle = bytes([0x28, token & 0xFF, (token >> 8) & 0xFF, (token >> 16) & 0xFF, (token >> 24) & 0xFF])
            n = data.count(needle)
            if n == 1:
                lines.append(f"{dll}: token 0x{token:08x}，call 命中 1 处（可补）")
            elif n == 0 and data.count(bytes([0x17, 0, 0, 0, 0])) > 0:
                lines.append(f"{dll}: 已补（call 不在，ldc.i4.1 形态在）")
            else:
                lines.append(f"{dll}: token 0x{token:08x}，call 命中 {n} 处（异常）")
                ok = False
        else:
            msg, done = patch_one(gamedir, dll, method)
            lines.append(msg)
            ok = ok and done
    if not check and ok:
        with open(os.path.join(gamedir, "os-detect.port17"), "w", encoding="utf-8") as f:
            f.write("Brutal.Vulkan.dll=IsLinux:true\nBrutal.Core.Common.dll=IsMacOS:true\n")
        lines.append("戳文件 os-detect.port17 已写（宿主读它确认补丁在）")
    print("\n".join(lines))
    sys.exit(0 if ok else 1)

if __name__ == "__main__":
    main()
