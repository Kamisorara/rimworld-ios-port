#!/usr/bin/env bash
# 取 Slang 2026.19（PC 版游戏自带的着色器编译器版本，着色器缓存键与它的 build tag 绑定）
# 到 build-slang/src/：shader-slang/slang @ v2026.19（钉 commit 0fb6b758），含子模块（浅克隆）。
# 与安卓仓库 host/fetch-deps.sh 同钉。已就位则跳过（.fetched 标记）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/build-slang"
SRC="$OUT/src"
MARK="$OUT/.fetched"

[ -f "$MARK" ] && { echo "slang: 源码已就位（$(cat "$MARK")）"; exit 0; }

rm -rf "$SRC"
mkdir -p "$OUT"

echo "slang: 克隆 v2026.19（含子模块，浅克隆，约 1–2 GB）"
git -c advice.detachedHead=false clone -q --depth 1 --branch v2026.19 \
    --recurse-submodules --shallow-submodules \
    https://github.com/shader-slang/slang.git "$SRC"

GOT="$(git -C "$SRC" rev-parse HEAD)"
if [ "$GOT" != "0fb6b7587a94fa33d88141ec9930766eedc6f59a" ]; then
    echo "::error::v2026.19 不再是 0fb6b758（现在 $GOT）——tag 被官方动了，需人工核对"
    exit 1
fi

echo "slang-2026.19-0fb6b758" > "$MARK"
du -sh "$SRC" | cut -f1
