# 从 PC 游戏副本推 M2 收官的用户拷贝清单：
# 游戏目录里「是托管程序集、且不在 iOS 官方运行时包里」的 dll（Setup.cs 的取舍规则，
# 减去 .Native./DiaSymReader 这类 Windows 原生物），并报告每个的 PE 机器类型。
# 用法：python copy-list.py <PC游戏目录> <nupkg路径>
import os, sys, zipfile

game = sys.argv[1]
nupkg = sys.argv[2]

# 运行时包的框架程序集名（runtimes/ios-arm64/lib/net11.0/*.dll）
runtime_dlls = set()
with zipfile.ZipFile(nupkg) as z:
    for n in z.namelist():
        base = n.rsplit("/", 1)[-1]
        parts = n.split("/")
        if len(parts) == 5 and parts[0] == "runtimes" and parts[2] == "lib" and base.endswith(".dll"):
            runtime_dlls.add(base.lower())
print(f"runtime pack dlls: {len(runtime_dlls)}")

def pe_info(path):
    with open(path, "rb") as f:
        data = f.read(0x400)
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None
    pe = int.from_bytes(data[0x3c:0x40], "little")
    if pe + 6 > len(data) or data[pe:pe+4] != b"PE\0\0":
        return None
    machine = int.from_bytes(data[pe+4:pe+6], "little")
    return machine

def is_managed(path):
    # Setup.cs 的 IsManaged：PE + 有 CLI 头（数据目录 14 项非零）
    with open(path, "rb") as f:
        head = f.read(0x40)
        if len(head) < 0x40 or head[:2] != b"MZ":
            return False
        pe = int.from_bytes(head[0x3c:0x40], "little")
        f.seek(pe)
        sig = f.read(4)
        if sig != b"PE\0\0":
            return False
        f.seek(pe + 24)
        magic = int.from_bytes(f.read(2), "little")
        f.seek(pe + 24 + (112 if magic == 0x20b else 96) + 14 * 8 + 4)
        return int.from_bytes(f.read(4), "little") > 0

machines = {0x8664: "AMD64", 0xAA64: "ARM64", 0x14c: "I386", 0x1c0: "ARM"}
copy, skip_native, skip_runtime = [], [], []
total = 0
for name in sorted(os.listdir(game)):
    if not name.lower().endswith(".dll"):
        continue
    total += 1
    path = os.path.join(game, name)
    m = pe_info(path)
    if m is None or not is_managed(path):
        skip_native.append(name)
        continue
    if name.lower() in runtime_dlls:
        skip_runtime.append(name)
        continue
    if ".Native." in name or name.startswith("Microsoft.DiaSymReader"):
        skip_native.append(name)
        continue
    copy.append((name, machines.get(m, hex(m))))

print(f"\n== 要拷的游戏程序集（{len(copy)} 个）==")
for name, m in copy:
    print(f"  {m:>6}  {name}")
print(f"\n== 跳过：非托管/Windows 原生（{len(skip_native)} 个）==")
print("  " + ", ".join(skip_native))
print(f"\n== 跳过：框架副本（iOS 运行时包已带，{len(skip_runtime)} 个）==")
print("  " + ", ".join(skip_runtime))
amd64 = sum(1 for _, m in copy if m == "AMD64")
arm64 = sum(1 for _, m in copy if m == "ARM64")
print(f"\n机器类型：AMD64 {amd64} 个，ARM64 {arm64} 个，其它 {len(copy)-amd64-arm64} 个")

import sys
if len(sys.argv) > 3:
    with open(sys.argv[3], "w", encoding="utf-8") as f:
        f.write("\n".join(name for name, _ in copy) + "\n")
    print(f"清单已写 {sys.argv[3]}")
