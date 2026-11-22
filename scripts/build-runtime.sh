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
BIN_MATCH="*ios*arm64*release*"
if [ "${1:-}" = "--sim" ]; then
    CLROS="iossimulator"   # 模拟器目标：二进制可直接在 macOS 宿主上原生跑（调试用）
    OUTDIR_NOTE="（simulator）"
    BIN_MATCH="*iossimulator*arm64*release*"
elif [ "${1:-}" = "--osx" ]; then
    # macOS 原生变体：同 pin 同补丁，产物在 runner 上原生可跑。CI 里配
    # RW17_SEED_ARENA=1 全链路验证竞技场后端（扫描/别名/探针/Commit 翻转），
    # 补 sim 变体测不了 JIT 的缺口。
    CLROS="osx"
    OUTDIR_NOTE="（osx 原生）"
    BIN_MATCH="*osx*arm64*release*"
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

# FEATURE_INTERPRETER=1（混合构建，2026-11-22 JIT 线复活）：JIT 静态链入 + 解释器作为
# 按需 dylib（libclrinterpreter.dylib，宿主用 DOTNET_InterpreterName 指绝对路径装载）。
# 两者的链接冲突（getJit/jitStartup 等 8 符号重复）由补丁里 mscoree/coreclr 的 CMake
# 门控解开：静态链入只在非 DCC 构建发生。注意别用默认值（不传）——那是 configure 期
# 未求值的生成器表达式字符串，if() 会把它当真值，恰好等于要修的 bug 形态。
./build.sh clr+libs -os "$CLROS" -arch arm64 -c Release \
    --dynamiccodecompiled true \
    -cmakeargs "-DFEATURE_INTERPRETER=1"

echo "build done ${OUTDIR_NOTE}: $(find artifacts/bin/coreclr -maxdepth 1 -type d -iname "$BIN_MATCH")"
