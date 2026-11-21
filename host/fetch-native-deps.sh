#!/usr/bin/env bash
# 取引擎原生库的上游源码，摊到 build-native-deps/。版本与 SHA-256 全部沿用安卓仓库
# host/fetch-deps.sh 的钉（位级同源——音频解码与纹理路径两端行为一致的前提）：
#   miniaudio/   miniaudio 0.11.22 的单头 + 其 extras 自带的 stb_vorbis.c（waylithaudio 用）
#   stb_image.h / stb_image_write.h   nothings/stb @013ac3be（libstb 用）
#   VulkanMemoryAllocator-3.3.0/     VMA 3.3.0（libvma 用）
#   gli-master/ + glm-1.0.1/         gli @3542f883 + glm 1.0.1（libgli 用，header-only）
#   astc-encoder-5.7.0/              ARM astcenc 5.7.0（libportastc 用）
#   KTX-Software-4.4.0/              Khronos libktx 4.4.0 读+转码子集（libKtx 用）
#   imgui-1.92.2-docking/            Dear ImGui 1.92.2 docking（libimgui 用；平铺 C 面在仓内
#                                    host/native/imgui_c.cpp，从玩家 PC 副本生成，不在此拉）
#   freetype-VER-2-13-3/             FreeType 2.13.3（libimgui_freetype 用）
# 已就位则跳过（.fetched 标记），配合 CI 的 actions/cache；依赖集合变化时升标记版本。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build-native-deps"
MARK="$OUT/.fetched"
RAW=https://raw.githubusercontent.com
GH=https://github.com

[ -f "$MARK" ] && { echo "native-deps: 已就位（$(cat "$MARK")）"; exit 0; }

rm -rf "$OUT"
mkdir -p "$OUT/miniaudio"

fetch() {  # <url> <sha256> <dest>
    if [ -f "$3" ] && [ "$(sha256sum < "$3" | cut -d' ' -f1)" = "$2" ]; then return; fi
    echo "native-deps: 取 $1"
    curl -fsSL --retry 3 -o "$3.part" "$1"
    local got
    got="$(sha256sum < "$3.part" | cut -d' ' -f1)"
    if [ "$got" != "$2" ]; then
        echo "::error::$1 SHA-256 $got，期望 $2"
        exit 1
    fi
    mv "$3.part" "$3"
}

fetch_zip() {  # <url> <sha256> <解出的目录名>（zip 解出后统一改名为它）
    echo "native-deps: 取 $1"
    curl -fsSL --retry 3 -o "$OUT/dl.zip" "$1"
    local got
    got="$(sha256sum < "$OUT/dl.zip" | cut -d' ' -f1)"
    if [ "$got" != "$2" ]; then
        echo "::error::$1 SHA-256 $got，期望 $2"
        exit 1
    fi
    rm -rf "$OUT/.unzip_tmp"
    unzip -q "$OUT/dl.zip" -d "$OUT/.unzip_tmp"
    rm "$OUT/dl.zip"
    local inner
    inner="$(find "$OUT/.unzip_tmp" -maxdepth 1 -mindepth 1 -type d | head -1)"
    mv "$inner" "$OUT/$3"
    rm -rf "$OUT/.unzip_tmp"
}

fetch "$RAW/mackron/miniaudio/0.11.22/miniaudio.h" \
      9019743287e443c55e5737a7297f38e5e358561701d6db2d905afb114390c410 \
      "$OUT/miniaudio/miniaudio.h"
fetch "$RAW/mackron/miniaudio/0.11.22/extras/stb_vorbis.c" \
      4c7cb2ff1f7011e9d67950446b7eb9ca044f2e464d76bfbb0b84dd2e23e65636 \
      "$OUT/miniaudio/stb_vorbis.c"
fetch "$RAW/nothings/stb/013ac3beddff3dbffafd5177e7972067cd2b5083/stb_image.h" \
      594c2fe35d49488b4382dbfaec8f98366defca819d916ac95becf3e75f4200b3 \
      "$OUT/stb_image.h"
fetch "$RAW/nothings/stb/013ac3beddff3dbffafd5177e7972067cd2b5083/stb_image_write.h" \
      cbd5f0ad7a9cf4468affb36354a1d2338034f2c12473cf1a8e32053cb6914a05 \
      "$OUT/stb_image_write.h"

fetch_zip "$GH/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator/archive/refs/tags/v3.3.0.zip" \
          81755d8fcb411b97292c6682e828501315db319374c7c34ba6e1226452c6c392 \
          VulkanMemoryAllocator-3.3.0
fetch_zip "$GH/g-truc/gli/archive/3542f8830178061e0661f5df1e89d36ba7d7b0ab.zip" \
          482302cff80aeed356a57ecf5b8ef04c0ef2334648e15d48aa7bdc43df88f38d \
          gli-master
fetch_zip "$GH/g-truc/glm/archive/refs/tags/1.0.1.zip" \
          09c5716296787e1f7fcb87b1cbdbf26814ec1288ed6259ccd30d5d9795809fa5 \
          glm-1.0.1
fetch_zip "$GH/ocornut/imgui/archive/refs/tags/v1.92.2-docking.zip" \
          dbd869305d7cd40cc7fbc285f201ec921950a5e296bf4e8abf54d5b28b638254 \
          imgui-1.92.2-docking

echo "native-deps: 取 freetype 2.13.3"
curl -fsSL --retry 3 -o "$OUT/freetype.tar.gz" \
    "$GH/freetype/freetype/archive/refs/tags/VER-2-13-3.tar.gz"
GOT="$(sha256sum < "$OUT/freetype.tar.gz" | cut -d' ' -f1)"
[ "$GOT" = "bc5c898e4756d373e0d991bab053036c5eb2aa7c0d5c67e8662ddc6da40c4103" ] || {
    echo "::error::freetype 2.13.3 SHA-256 $GOT"; exit 1; }
tar -xzf "$OUT/freetype.tar.gz" -C "$OUT"
rm "$OUT/freetype.tar.gz"

echo "native-deps: 取 astc-encoder 5.7.0"
curl -fsSL --retry 3 -o "$OUT/astc.tar.gz" \
    "$GH/ARM-software/astc-encoder/archive/refs/tags/5.7.0.tar.gz"
GOT="$(sha256sum < "$OUT/astc.tar.gz" | cut -d' ' -f1)"
[ "$GOT" = "7c1b28ece59c9c2737e297123a9910c1c548676c7aed87a09d5734e2fa7bdaf0" ] || {
    echo "::error::astc-encoder 5.7.0 SHA-256 $GOT"; exit 1; }
tar -xzf "$OUT/astc.tar.gz" -C "$OUT"
rm "$OUT/astc.tar.gz"

echo "native-deps: 取 KTX-Software 4.4.0"
curl -fsSL --retry 3 -o "$OUT/ktx.tar.gz" \
    "$GH/KhronosGroup/KTX-Software/archive/refs/tags/v4.4.0.tar.gz"
GOT="$(sha256sum < "$OUT/ktx.tar.gz" | cut -d' ' -f1)"
[ "$GOT" = "3585d76edcdcbe3a671479686f8c81c1c10339f419e4b02a9a6f19cc6e4e0612" ] || {
    echo "::error::KTX-Software 4.4.0 SHA-256 $GOT"; exit 1; }
tar -xzf "$OUT/ktx.tar.gz" -C "$OUT"
rm "$OUT/ktx.tar.gz"

echo "native-deps-v3" > "$MARK"
ls "$OUT"
