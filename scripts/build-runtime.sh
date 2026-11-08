#!/usr/bin/env bash
# 构建 iOS arm64 带动态代码（JIT）的 CoreCLR + BCL（只编译，不收集）。
# 在 macOS 上运行（Apple 目标需要 Xcode）。
# 产物收集由 collect-runtime.sh 单独做：编译成功后即使收集出错，
# 编译产物也已经可以保存复用，不必重新编译。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="$(head -n1 "$ROOT/runtime-pin.txt" | tr -d '[:space:]')"
SRC="$ROOT/runtime-src"
CLROS="ios"
OUTDIR_NOTE=""
if [ "${1:-}" = "--sim" ]; then
    CLROS="iossimulator"   # 模拟器目标：二进制可直接在 macOS 宿主上原生跑（调试用）
    OUTDIR_NOTE="（simulator）"
fi

echo "runtime pin: $PIN"

if [ ! -d "$SRC" ]; then
    git clone https://github.com/dotnet/runtime.git "$SRC"
fi
cd "$SRC"
git checkout --detach "$PIN"

# 应用本仓库的 runtime 补丁（patches/*.patch，对 pin 生成；已应用则跳过）
for p in "$ROOT"/patches/*.patch; do
    [ -e "$p" ] || continue
    if git apply --check "$p" 2>/dev/null; then
        git apply "$p"
        echo "已应用补丁 $(basename "$p")"
    else
        if git apply --reverse --check "$p" 2>/dev/null; then
            echo "补丁 $(basename "$p") 已在（跳过）"
        else
            echo "::error::补丁 $(basename "$p") 无法应用（pin 源码不匹配？）"
            exit 1
        fi
    fi
done

# -DFEATURE_INTERPRETER=0：Apple mobile 强制静态链接，而 FEATURE_INTERPRETER 的默认值是个
# configure 期未求值的生成器表达式字符串（$<IF:$<CONFIG:Debug,Checked>,1,0>），链接清单处的
# if(FEATURE_INTERPRETER) 会把它当真值 → 解释器与 JIT 同时静态链入 libcoreclr.dylib，
# getJit/jitStartup 等 8 个符号重复。显式定义为 0（JIT 变体不需要解释器）绕过该组合
# （静态链接 + 动态代码，微软 CI 不构建的矩阵格）。
./build.sh clr+libs -os "$CLROS" -arch arm64 -c Release \
    --dynamiccodecompiled true \
    -cmakeargs "-DFEATURE_INTERPRETER=0"

echo "build done ${OUTDIR_NOTE}: $(find artifacts/bin/coreclr -maxdepth 1 -type d -iname '*ios*arm64*release*')"
