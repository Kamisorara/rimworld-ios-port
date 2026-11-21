#!/usr/bin/env bash
# 组装 M2 宿主 IPA（Port17Host）：CAMetalLayer + 触摸采集 + 软键盘桥的 UIKit 宿主，
# GLFW 垫片（libglfw.dylib）与 Vulkan 前向层（libvulkan.dylib → MoltenVK.framework），
# Vulkan 三角形验证（glslang 现编 SPIR-V），外加 M1 的 .NET 探针诊断件（官方运行时包）。
# 前置：build-moltenvk/（host/fetch-moltenvk.sh）、runtime-out-official/（scripts/fetch-official-runtime.sh）、
# glslangValidator（brew install glslang）。产物 build-m2/port17host-m2.ipa。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/build-m2/Port17Host.app"
MVK="$ROOT/build-moltenvk"
SDK="iphoneos"
TARGET="arm64-apple-ios15.0"
INC="$MVK/include"

rm -rf "$ROOT/build-m2"
mkdir -p "$APP"

[ -f "$MVK/.fetched" ] || { echo "::error::MoltenVK 未就位，先跑 host/fetch-moltenvk.sh"; exit 1; }
command -v glslangValidator >/dev/null || { echo "::error::glslangValidator 不在（brew install glslang）"; exit 1; }
[ -e "$ROOT/runtime-out-official/libcoreclr.dylib" ] || { echo "::error::官方运行时包未就位（scripts/fetch-official-runtime.sh）"; exit 1; }

# 1) 着色器：GLSL -> SPIR-V（目标环境 vulkan1.1——引擎的地板，MoltenVK 1.4 之上照跑）
for s in vert frag; do
    glslangValidator -V --target-env vulkan1.1 -o "$APP/triangle.$s.spv" \
        "$ROOT/host/app/shaders/triangle.$s"
done
ls -la "$APP"/*.spv

# 2) GLFW 垫片（UIKit/CAMetalLayer 后端；非 ARC——垫片持有的 view/layer 由宿主保活）。
#    install_name 必须钉 @rpath：默认值是构建机的绝对路径，宿主链接后记下的是那个路径，
#    设备上 dyld 找不到就直接拒载（CI 上链接却全绿，这类错只有 install_name 能防）。
xcrun -sdk "$SDK" clang \
    -target "$TARGET" \
    -shared -fvisibility=default \
    -Wl,-install_name,@rpath/libglfw.dylib \
    -I "$INC" \
    -framework UIKit -framework Foundation -framework QuartzCore \
    -o "$APP/libglfw.dylib" \
    "$ROOT/host/glfw/glfw_ios.m"

# 3) Vulkan 前向层（libvulkan.dylib → 同目录 MoltenVK.framework；宿主按路径 dlopen，
#    install_name 同样钉住只为整洁）
xcrun -sdk "$SDK" clang \
    -target "$TARGET" \
    -shared -fvisibility=default \
    -Wl,-install_name,@rpath/libvulkan.dylib \
    -I "$INC" \
    -o "$APP/libvulkan.dylib" \
    "$ROOT/host/vulkan/vulkan_ios.c"

# 4) MoltenVK 官方 framework（原样放 .app 根；install name 是 @rpath/…，宿主带 @executable_path）
cp -R "$MVK/framework/MoltenVK.framework" "$APP/"

# 5) 宿主可执行（M2 骨架 + 三角形 + .NET 探针诊断件）
xcrun -sdk "$SDK" clang \
    -target "$TARGET" \
    -fobjc-arc -DPORT17_INTERP=1 \
    -I "$ROOT/host/app" -I "$INC" \
    -Wl,-rpath,@executable_path \
    -L "$APP" -lglfw -lvulkan \
    -framework UIKit -framework Foundation -framework QuartzCore -framework CoreGraphics -framework Metal \
    -o "$APP/Port17Host" \
    "$ROOT/host/app/main.m" "$ROOT/host/app/host.c" "$ROOT/host/app/triangle.c"
cp "$ROOT/host/app/Info.plist" "$APP/"

# 6) .NET 探针诊断件（M1 全套：官方运行时 + Hello，平铺 .app 根——CoreLib 同目录原则）
dotnet build "$ROOT/probe/hello/hello.csproj" -c Release -o "$ROOT/build-m2/hello"
cp "$ROOT"/runtime-out-official/*.dylib "$APP/"
cp "$ROOT"/runtime-out-official/*.dll "$APP/"
cp "$ROOT/build-m2/hello/Hello.dll" "$APP/"

# 6.5) 出包自检：install_name 必须是 @rpath（默认值是构建机绝对路径，设备上 dyld 拒载而 CI 全绿）；
#      宿主的依赖链也打印出来留档。任何一条不符合直接失败，不让坏包出门。
for lib in libglfw.dylib libvulkan.dylib; do
    NAME="$(otool -D "$APP/$lib" | tail -1)"
    echo "otool -D $lib: ${NAME}"
    case "$NAME" in
        @rpath/*) ;;
        *) echo "::error::$lib 的 install_name 是 ${NAME}（应为 @rpath/…）"; exit 1 ;;
    esac
done
echo "---- otool -L Port17Host ----"
otool -L "$APP/Port17Host"
case "$(otool -L "$APP/Port17Host")" in
    *runner*) echo "::error::宿主依赖里有构建机绝对路径"; exit 1 ;;
esac

# 7) ad-hoc 签名：先内嵌文件与 framework，后 bundle（顺序不能反）
ENT="$ROOT/host/app/entitlements.plist"
for f in "$APP"/*.dylib "$APP/MoltenVK.framework/MoltenVK" "$APP/Port17Host"; do
    codesign --force --sign - --timestamp=none --entitlements "$ENT" "$f"
done
codesign --force --sign - --timestamp=none --entitlements "$ENT" "$APP/MoltenVK.framework"
codesign --force --sign - --timestamp=none --entitlements "$ENT" "$APP"

# 8) IPA
cd "$ROOT/build-m2"
mkdir Payload
cp -R Port17Host.app Payload/
zip -qry port17host-m2.ipa Payload
echo "OK: $ROOT/build-m2/port17host-m2.ipa"
