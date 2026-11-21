#!/usr/bin/env bash
# 下载官方 KhronosGroup/MoltenVK（Vulkan 1.4 over Metal）的 iOS 包，摊到 build-moltenvk/：
#   framework/  动态 MoltenVK.framework（ios-arm64 真机，放 .app 根）
#   include/    MoltenVK 自带的完整 Vulkan 头（编译垫片/前向层/三角形共用）
# 版本与 sha256 都钉死在这里（docs/ENGINE-NATIVE-CONTRACT.md 第 5 节有核验日期）；
# 升级 = 改下面两个值。已就位则跳过（.fetched 标记），配合 CI 的 actions/cache 用。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="v1.4.2"
SHA256="b5d947b1660e6e9fed40b9cd2387e160aaab9e80b775c0cef7e14059405178c1"
OUT="$ROOT/build-moltenvk"
MARK="$OUT/.fetched"

[ -f "$MARK" ] && { echo "moltenvk: 已就位（$(cat "$MARK")）"; exit 0; }

rm -rf "$OUT"
mkdir -p "$OUT"
echo "moltenvk: 取 MoltenVK-ios.tar ${VERSION}"
curl -fsSL --retry 3 -o "$OUT/MoltenVK-ios.tar" \
    "https://github.com/KhronosGroup/MoltenVK/releases/download/${VERSION}/MoltenVK-ios.tar"

GOT="$(sha256sum < "$OUT/MoltenVK-ios.tar" | cut -d' ' -f1)"
if [ "$GOT" != "$SHA256" ]; then
    echo "::error::MoltenVK-ios.tar SHA-256 $GOT，期望 $SHA256（版本被官方动了？钉死校验和的意义在此）"
    exit 1
fi

# GitHub 的 asset 名叫 .tar，本体是未压缩 tar（2026-11 核验）。
tar -xf "$OUT/MoltenVK-ios.tar" -C "$OUT" MoltenVK/MoltenVK/dynamic/MoltenVK.xcframework \
    MoltenVK/MoltenVK/include
mkdir -p "$OUT/framework" "$OUT"
cp -R "$OUT/MoltenVK/MoltenVK/dynamic/MoltenVK.xcframework/ios-arm64/MoltenVK.framework" "$OUT/framework/"
mv "$OUT/MoltenVK/MoltenVK/include" "$OUT/include"
rm -rf "$OUT/MoltenVK" "$OUT/MoltenVK-ios.tar"

echo "$VERSION" > "$MARK"
echo "moltenvk: framework 与 Vulkan 头就位"
ls "$OUT/framework/MoltenVK.framework"
ls "$OUT/include/vulkan/vulkan_core.h"
