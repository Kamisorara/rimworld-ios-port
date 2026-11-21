#!/usr/bin/env bash
# 取音频与内存分配器的上游源码，摊到 build-native-deps/。版本与 SHA-256 全部沿用安卓仓库
# host/fetch-deps.sh 的钉（位级同源——音频解码与纹理路径两端行为一致的前提）：
#   miniaudio/   miniaudio 0.11.22 的单头 + 其 extras 自带的 stb_vorbis.c（waylithaudio 用）
#   stb_image.h / stb_image_write.h   nothings/stb @013ac3be（libstb 用）
#   VulkanMemoryAllocator-3.3.0/     VMA 3.3.0（libvma 用）
# 已就位则跳过（.fetched 标记），配合 CI 的 actions/cache。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build-native-deps"
MARK="$OUT/.fetched"
RAW=https://raw.githubusercontent.com

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

echo "native-deps: 取 VMA 3.3.0"
curl -fsSL --retry 3 -o "$OUT/vma.zip" \
    "https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator/archive/refs/tags/v3.3.0.zip"
GOT="$(sha256sum < "$OUT/vma.zip" | cut -d' ' -f1)"
if [ "$GOT" != "81755d8fcb411b97292c6682e828501315db319374c7c34ba6e1226452c6c392" ]; then
    echo "::error::VMA 3.3.0 SHA-256 $GOT，期望 81755d8f…"
    exit 1
fi
unzip -q "$OUT/vma.zip" -d "$OUT"
rm "$OUT/vma.zip"

echo "native-deps-v1" > "$MARK"
ls "$OUT" "$OUT/miniaudio"
