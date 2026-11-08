#!/usr/bin/env bash
# 组装 M1 探针 IPA：编译 app（clang + UIKit）、编译 Hello.dll（dotnet）、
# 塞入 runtime-out 的运行时，ad-hoc 签名后打成未签名 IPA。
# 前置：runtime-out/ 已由 scripts/build-runtime.sh（或 CI 缓存）备好。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/build/Port17Probe.app"

rm -rf "$ROOT/build"
mkdir -p "$APP"

# 1) 托管探针（IL 即可，TPA 宿主加载）
dotnet build "$ROOT/probe/hello/hello.csproj" -c Release -o "$ROOT/build/hello"

# 2) 原生 app
xcrun -sdk iphoneos clang \
    -target arm64-apple-ios15.0 \
    -fobjc-arc \
    -Wl,-rpath,@executable_path \
    "$ROOT/probe/app/main.m" "$ROOT/probe/app/host.c" \
    -framework UIKit -framework Foundation -framework CoreGraphics \
    -o "$APP/Port17Probe"
cp "$ROOT/probe/app/Info.plist" "$APP/"

# 3) 运行时 + 探针程序集：全部平铺在 .app 根——CoreCLR 按 libcoreclr.dylib 所在
#    目录找 System.Private.CoreLib.dll，与托管 dll 分目录会让初始化 0x80070002
cp "$ROOT"/runtime-out/*.dylib "$APP/"
cp "$ROOT"/runtime-out/managed/*.dll "$APP/"
cp "$ROOT/build/hello/Hello.dll" "$APP/"

# 4) ad-hoc 签名：先内嵌文件，后 bundle（顺序不能反）
ENT="$ROOT/probe/app/entitlements.plist"
for f in "$APP"/*.dylib "$APP/Port17Probe"; do
    codesign --force --sign - --timestamp=none --entitlements "$ENT" "$f"
done
codesign --force --sign - --timestamp=none --entitlements "$ENT" "$APP"

# 5) IPA
cd "$ROOT/build"
mkdir Payload
cp -R Port17Probe.app Payload/
zip -qry port17probe.ipa Payload
echo "OK: $ROOT/build/port17probe.ipa"
