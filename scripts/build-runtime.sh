#!/usr/bin/env bash
# 构建 iOS arm64 带动态代码（JIT）的 CoreCLR + BCL。
# 在 macOS 上运行（Apple 目标需要 Xcode）。产物收集到 runtime-out/：
#   runtime-out/*.dylib        libcoreclr.dylib（如构建产出分离的 libclrjit.dylib 一并带上）
#   runtime-out/managed/*.dll  BCL 程序集
# 只在 runtime-pin.txt 变化后需要重跑（CI 按 pin 哈希缓存）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="$(head -n1 "$ROOT/runtime-pin.txt" | tr -d '[:space:]')"
SRC="$ROOT/runtime-src"
OUT="$ROOT/runtime-out"

echo "runtime pin: $PIN"

if [ ! -d "$SRC" ]; then
    git clone https://github.com/dotnet/runtime.git "$SRC"
fi
cd "$SRC"
git checkout --detach "$PIN"

# -DFEATURE_INTERPRETER=0：Apple mobile 强制静态链接，而 FEATURE_INTERPRETER 的默认值是个
# configure 期未求值的生成器表达式字符串（$<IF:$<CONFIG:Debug,Checked>,1,0>），链接清单处的
# if(FEATURE_INTERPRETER) 会把它当真值 → 解释器与 JIT 同时静态链入 libcoreclr.dylib，
# getJit/jitStartup 等 8 个符号重复。显式定义为 0（JIT 变体不需要解释器）绕过该组合
# （静态链接 + 动态代码，微软 CI 不构建的矩阵格）。
./build.sh clr+libs -os ios -arch arm64 -c Release \
    --dynamiccodecompiled true \
    -cmakeargs "-DFEATURE_INTERPRETER=0"

# ---- 收集产物 ----
rm -rf "$OUT"
mkdir -p "$OUT/managed"

CORECLR_BIN="$(find artifacts/bin/coreclr -maxdepth 1 -type d -iname '*ios*arm64*release*' | head -n1)"
if [ -z "$CORECLR_BIN" ]; then
    echo "找不到 coreclr 的 iOS arm64 Release 输出目录" >&2
    exit 1
fi
cp "$CORECLR_BIN"/*.dylib "$OUT"/

# BCL 分散在各项目的输出目录里，按文件名去重收集（排除 ref 程序集）
find artifacts/bin -name '*.dll' -ipath '*ios*release*' \
    -not -ipath '*/ref/*' -not -ipath '*coreclr*' -print0 |
while IFS= read -r -d '' dll; do
    name="$(basename "$dll")"
    if [ ! -e "$OUT/managed/$name" ]; then
        cp "$dll" "$OUT/managed/$name"
    fi
done

echo "---- runtime-out ----"
ls -la "$OUT" "$OUT/managed" | head -40
echo "managed dll 数量：$(ls "$OUT/managed" | wc -l | tr -d ' ')"
if [ ! -e "$OUT/managed/System.Private.CoreLib.dll" ]; then
    echo "managed 里没有 System.Private.CoreLib.dll，收集有误" >&2
    exit 1
fi
