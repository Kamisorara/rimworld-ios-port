#!/usr/bin/env bash
# 组装解释器路线 IPA：官方 .NET 11 ios 运行时包（解释器配置，微软支持形态），
# 宿主编译带 -DPORT17_INTERP（跳过 JIT26 竞技场握手），无 StikDebug 依赖——
# 装完直接打开。产物 port17probe-interp.ipa。
# 前置：runtime-out-official/ 已由 scripts/fetch-official-runtime.sh 备好。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/build-interp/Port17Probe.app"

rm -rf "$ROOT/build-interp"
mkdir -p "$APP"

# 1) 托管探针（IL 即可，TPA 宿主加载；解释器全程解释执行）
dotnet build "$ROOT/probe/hello/hello.csproj" -c Release -o "$ROOT/build-interp/hello"

# 2) 原生 app（PORT17_INTERP：跳过 jit26 竞技场，提示文案切到解释器）
xcrun -sdk iphoneos clang \
    -target arm64-apple-ios15.0 \
    -fobjc-arc -DPORT17_INTERP=1 \
    -Wl,-rpath,@executable_path \
    "$ROOT/probe/app/main.m" "$ROOT/probe/app/host.c" \
    -framework UIKit -framework Foundation -framework CoreGraphics \
    -o "$APP/Port17Probe"
cp "$ROOT/probe/app/Info.plist" "$APP/"

# 3) 运行时 + 探针程序集：全部平铺在 .app 根（CoreLib 同目录原则不变）
cp "$ROOT"/runtime-out-official/*.dylib "$APP/"
cp "$ROOT"/runtime-out-official/*.dll "$APP/"
cp "$ROOT/build-interp/hello/Hello.dll" "$APP/"

# 4) ad-hoc 签名：先内嵌文件，后 bundle（顺序不能反）
ENT="$ROOT/probe/app/entitlements.plist"
for f in "$APP"/*.dylib "$APP/Port17Probe"; do
    codesign --force --sign - --timestamp=none --entitlements "$ENT" "$f"
done
codesign --force --sign - --timestamp=none --entitlements "$ENT" "$APP"

# 5) IPA
cd "$ROOT/build-interp"
mkdir Payload
cp -R Port17Probe.app Payload/
zip -qry port17probe-interp.ipa Payload
echo "OK: $ROOT/build-interp/port17probe-interp.ipa"
