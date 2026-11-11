#!/usr/bin/env bash
# 从 runtime-src 的构建输出里收集 iOS 运行时到 runtime-out/（尽力而为，只警告不失败——
# 保证编译产物总能被后续的缓存/Release/Artifact 步骤保存下来）。
# 输出布局（2026-11-08 由 CI 实测日志确认）：
#   libcoreclr.dylib 等        artifacts/bin/coreclr/ios.arm64.Release/
#   System.Private.CoreLib.dll artifacts/bin/coreclr/ios.arm64.Release/IL/
#   其余 BCL                   artifacts/bin/<程序集>/Release/net11.0[-ios]/（ios 变体优先）
# 排除：/ref/（引用程序集）、/stub/（桩）
set -uo pipefail   # 不用 -e：单段失败不应毁掉整个收集

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/runtime-src"
OUT="$ROOT/runtime-out"
BIN_MATCH="*ios*arm64*release*"
if [ "${1:-}" = "--sim" ]; then
    OUT="$ROOT/runtime-out-sim"
    BIN_MATCH="*iossimulator*arm64*release*"
elif [ "${1:-}" = "--osx" ]; then
    OUT="$ROOT/runtime-out-osx"
    BIN_MATCH="*osx*arm64*release*"
fi

rm -rf "$OUT"
mkdir -p "$OUT/managed"

CORECLR_BIN="$(find "$SRC/artifacts/bin/coreclr" -maxdepth 1 -type d -iname "$BIN_MATCH" | head -n1)"
if [ -z "$CORECLR_BIN" ]; then
    echo "::warning::找不到 coreclr 的目标 arm64 Release 输出目录（$BIN_MATCH）"
else
    cp "$CORECLR_BIN"/*.dylib "$OUT"/ || echo "::warning::dylib 复制不完整"
    # CoreLib 的 IL 输出在 coreclr 输出目录的 IL/ 子目录里
    cp "$CORECLR_BIN"/IL/*.dll "$OUT/managed/" 2>/dev/null \
        || echo "::warning::CoreLib IL 目录复制失败"
fi

# 托管类库的原生部分（libSystem.Native 等）：coreclr 输出目录里没有，
# 但官方 ios 运行时包的 native/ 里有——缺了它们 CoreLib 引导期的第一个
# P/Invoke 就会 dlopen 失败（表现为 0x80070002）。按名字在全部构建输出里找。
find "$SRC/artifacts/bin" -name 'libSystem.*.dylib' -print0 |
while IFS= read -r -d '' d; do
    cp -n "$d" "$OUT/" 2>/dev/null || true
done

# 其余 BCL：先 ios 专属变体（cp -n 之后不再覆盖），再 RID 无关版本补齐
find "$SRC/artifacts/bin" -type d -path '*/Release/net11.0-ios' -print0 |
while IFS= read -r -d '' d; do
    cp -n "$d"/*.dll "$OUT/managed/" 2>/dev/null || true
done
find "$SRC/artifacts/bin" -type d -path '*/Release/net11.0' \
    -not -path '*/stub/*' -not -path '*/ref/*' -print0 |
while IFS= read -r -d '' d; do
    cp -n "$d"/*.dll "$OUT/managed/" 2>/dev/null || true
done

echo "---- runtime-out ----"
ls -la "$OUT" | head -25
echo "managed dll 数量：$(ls "$OUT/managed" 2>/dev/null | wc -l | tr -d ' ')"
if [ ! -e "$OUT/libcoreclr.dylib" ]; then
    echo "::warning::runtime-out 里没有 libcoreclr.dylib，产物不完整"
fi
if [ ! -e "$OUT/libSystem.Native.dylib" ]; then
    echo "::warning::runtime-out 里没有 libSystem.Native.dylib（类库原生部分），运行时会 0x80070002"
fi
if [ ! -e "$OUT/managed/System.Private.CoreLib.dll" ]; then
    echo "::warning::managed 里没有 System.Private.CoreLib.dll，产物不完整"
fi
echo "收集完成（警告不阻塞：产物按原样保存，可下载排查）"
