#!/usr/bin/env bash
# 下载自建混合运行时（m1-probe 流水线发布的 Release 资产 runtime-<pin>），
# 摊平到 runtime-out-hybrid/：资产内部是 collect-runtime.sh 的布局（dylib 在根、
# BCL 在 managed/ 子目录），build-ipa.sh 要的是全平（lib 与 dll 同目录）。
# 资产按 pin 命名、同 tag clobber 上传——上游补丁/flag 变更并重跑 m1-probe 后，
# 这里取到的就是最新构建（无版本混淆：缓存键变了必须重编，否则 m1-probe 红）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PIN="$(head -n1 "$ROOT/runtime-pin.txt" | tr -d '[:space:]')"
OUT="$ROOT/runtime-out-hybrid"

rm -rf "$OUT"
mkdir -p "$OUT"
gh release download "runtime-$PIN" -p "runtime-ios-arm64-$PIN.zip" --clobber -O "$OUT/runtime.zip" -R Kamisorara/rimworld-ios-port
unzip -q "$OUT/runtime.zip" -d "$OUT/unpacked"
# 摊平：dylib 已在根；managed/*.dll 提到根（cp -n 与 collect 语义一致）
cp "$OUT"/unpacked/*.dylib "$OUT"/ 2>/dev/null || true
cp -n "$OUT"/unpacked/managed/*.dll "$OUT"/ 2>/dev/null || true
rm -rf "$OUT/unpacked" "$OUT/runtime.zip"

for f in libcoreclr.dylib libclrinterpreter.dylib libSystem.Native.dylib managed/System.Private.CoreLib.dll System.Private.CoreLib.dll; do
    [ -e "$OUT/$f" ] || echo "::warning::runtime-out-hybrid 里没有 $f"
done
echo "混合运行时就位：$(ls "$OUT" | wc -l | tr -d ' ') 项（含 $(ls "$OUT"/*.dll 2>/dev/null | wc -l | tr -d ' ') 个托管 dll）"
