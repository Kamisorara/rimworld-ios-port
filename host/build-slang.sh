#!/usr/bin/env bash
# 编 Slang 2026.19 的 iOS arm64 三件库（引擎装载的只有这三个，照安卓 build-official.cmd 的目标清单）：
#   libslang-compiler.dylib          Slang 本体（引擎经 libslang.dylib 代理转到这里）
#   libslang-glslang-2026.19.dylib   Slang 的 glslang/SPIRV-Tools 部分（版本尾巴是装载名的一部分）
#   libslang-glsl-module-2026.19.dylib
# 两段式（Slang 的规矩）：先在宿主（macOS）编 generators（构建期代码生成器），再以 iOS 工具链编目标库。
# iOS 无现成 preset（安卓有 android-arm64），目标段手动喂 CMAKE_SYSTEM_NAME=iOS。
# 产物摊到 build-slang/out/；已建则跳过（.built 标记；改本脚本要删标记或升 vN——CI 缓存键同源）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build-slang"
SRC="$OUT/src"
BUILT="$OUT/.built-v1"

[ -f "$BUILT" ] && { echo "slang: 已建（$(cat "$BUILT")）"; exit 0; }
[ -f "$OUT/.fetched" ] || { echo "::error::Slang 源码未就位，先跑 host/fetch-slang.sh"; exit 1; }

command -v ninja >/dev/null || brew install ninja

# ---- 阶段 1：宿主 generators（macOS 原生）----
if [ ! -x "$SRC/generators/bin/slang-bootstrap" ] && [ ! -x "$SRC/generators/bin/slang-bootstrap.exe" ]; then
    echo "== slang: 宿主 generators（macOS）"
    (cd "$SRC" && cmake --preset generators -DSLANG_IGNORE_ABORT_MSG=ON)
    (cd "$SRC" && cmake --build --preset generators)
    (cd "$SRC" && cmake --install build --config Release --prefix generators --component generators)
fi

# ---- 阶段 2：iOS arm64 目标库 ----
# 与安卓 preset（android-base）同款关停清单（GFX/RHI/SLANGD/REPLAYER/LLVM/EXAMPLES/XLIB/CUDA/OPTIX/
# NVAPI/AFTERMATH——不关的话 tools/ 的 gfx 块引用被关掉的 slangc 目标，configure 直接死），
# 再叠加安卓 build-official.cmd 的同参（不带 slangc/slangi/slangrt/proxy；IGNORE_ABORT_MSG 容忍
# -DNDEBUG 下的 abort 提示），工具链换成 iOS：CMAKE_SYSTEM_NAME=iOS + iphoneos SDK + 15.0 部署目标。
echo "== slang: iOS arm64 目标库"
BUILD="$SRC/build-ios-arm64"
cmake -S "$SRC" -B "$BUILD" -G Ninja \
    -DCMAKE_SYSTEM_NAME=iOS \
    -DCMAKE_MACOSX_BUNDLE=OFF \
    -DCMAKE_OSX_SYSROOT=iphoneos \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
    -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_BUILD_TYPE=Release \
    -DSLANG_GENERATORS_PATH="$SRC/generators/bin" \
    -DSLANG_ENABLE_SLANGC=OFF \
    -DSLANG_ENABLE_SLANGI=OFF \
    -DSLANG_ENABLE_SLANGRT=OFF \
    -DSLANG_ENABLE_SLANG_PROXY=OFF \
    -DSLANG_ENABLE_GFX=OFF \
    -DSLANG_ENABLE_SLANG_RHI=OFF \
    -DSLANG_ENABLE_TESTS=OFF \
    -DSLANG_ENABLE_SLANGD=OFF \
    -DSLANG_ENABLE_REPLAYER=OFF \
    -DSLANG_SLANG_LLVM_FLAVOR=DISABLE \
    -DSLANG_ENABLE_EXAMPLES=OFF \
    -DSLANG_ENABLE_XLIB=OFF \
    -DSLANG_ENABLE_CUDA=OFF \
    -DSLANG_ENABLE_OPTIX=OFF \
    -DSLANG_ENABLE_NVAPI=OFF \
    -DSLANG_ENABLE_AFTERMATH=OFF \
    -DSLANG_ENABLE_SPLIT_DEBUG_INFO=OFF \
    -DSLANG_IGNORE_ABORT_MSG=ON

cmake --build "$BUILD" --target slang slang-glslang slang-glsl-module

# ---- 收集产物 ----
# 本体带版本尾巴（libslang-compiler.0.2026.19.dylib），收成净名；伴生模块在 Apple 上也叫
# .so（Slang 的模块命名，运行时按这个名字装载，保持原样）。
mkdir -p "$OUT/out"
COMPILER="$(find "$BUILD" -name 'libslang-compiler*.dylib' ! -name '*without*' | head -1)"
[ -n "$COMPILER" ] || { echo "::error::libslang-compiler 没编出来"; exit 1; }
cp "$COMPILER" "$OUT/out/libslang-compiler.dylib"
for f in libslang-glslang-2026.19.so libslang-glsl-module-2026.19.so; do
    P="$(find "$BUILD" -name "$f" | head -1)"
    [ -n "$P" ] || { echo "::error::$f 没编出来"; exit 1; }
    cp "$P" "$OUT/out/$f"
done
echo "slang: 三件就位"; ls -la "$OUT/out"

echo "ios-arm64-2026.19" > "$BUILT"
