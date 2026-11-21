#!/usr/bin/env bash
# 下载官方 .NET 11 ios-arm64 运行时包（解释器配置，微软支持形态），摊平到
# runtime-out-official/：lib/net11.0 与 native 合一目录（bundle 拍平布局）。
# 无补丁、无缓存键——runtime 升级 = 改下面一个版本号。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VER="${1:-11.0.0}"
PKG="microsoft.netcore.app.runtime.ios-arm64"
OUT="$ROOT/runtime-out-official"

rm -rf "$OUT"
mkdir -p "$OUT" "$ROOT/build"

SRC=""
for base in "https://api.nuget.org/v3-flatcontainer" "https://nuget.azure.cn/v3-flatcontainer"; do
    if curl -sfL -o "$ROOT/build/official-pack.nupkg" \
        "$base/$PKG/$VER/$PKG.$VER.nupkg"; then
        SRC="$base"
        break
    fi
done
[ -n "$SRC" ] || { echo "::error::两个 NuGet 源都取不到 $PKG $VER"; exit 1; }
echo "pack source: ${SRC} (${VER})"

unzip -q "$ROOT/build/official-pack.nupkg" -d "$ROOT/build/official-pack"
cp "$ROOT"/build/official-pack/runtimes/ios-arm64/lib/net11.0/*.dll "$OUT"/
cp "$ROOT"/build/official-pack/runtimes/ios-arm64/lib/net11.0/*.dylib "$OUT"/ 2>/dev/null || true
cp "$ROOT"/build/official-pack/runtimes/ios-arm64/native/*.dylib "$OUT"/

echo "---- runtime-out-official ----"
ls "$OUT" | head -30
echo "dll: $(ls "$OUT"/*.dll 2>/dev/null | wc -l | tr -d ' ')  dylib: $(ls "$OUT"/*.dylib 2>/dev/null | wc -l | tr -d ' ')"
[ -e "$OUT/libcoreclr.dylib" ] || { echo "::error::没有 libcoreclr.dylib"; exit 1; }
[ -e "$OUT/System.Private.CoreLib.dll" ] || { echo "::error::没有 System.Private.CoreLib.dll"; exit 1; }
