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
#    install_name 同样钉住只为整洁）。libvulkan.1.dylib 是同一前向的第二副本：引擎的
#    MacOS 家族路径（OS 探测补丁后 iOS 走它）在 EngineApplication 里 NativeLibrary.
#    Load("vulkan.1")，Darwin 解析序是 vulkan.1.dylib / libvulkan.1.dylib。
xcrun -sdk "$SDK" clang \
    -target "$TARGET" \
    -shared -fvisibility=default \
    -Wl,-install_name,@rpath/libvulkan.dylib \
    -I "$INC" \
    -o "$APP/libvulkan.dylib" \
    "$ROOT/host/vulkan/vulkan_ios.c"
xcrun -sdk "$SDK" clang \
    -target "$TARGET" \
    -shared -fvisibility=default \
    -Wl,-install_name,@rpath/libvulkan.1.dylib \
    -I "$INC" \
    -o "$APP/libvulkan.1.dylib" \
    "$ROOT/host/vulkan/vulkan_ios.c"

# 4) MoltenVK 官方 framework（原样放 .app 根；install name 是 @rpath/…，宿主带 @executable_path）
cp -R "$MVK/framework/MoltenVK.framework" "$APP/"

# 4.5) 引擎原生库（M2 后半）：音频（waylithaudio/CoreAudio）、VMA、stb。
#      依赖源码由 host/fetch-native-deps.sh 备好（版本与 sha 沿用安卓仓库的钉）。
NDEPS="$ROOT/build-native-deps"
[ -f "$NDEPS/.fetched" ] || { echo "::error::原生库依赖未就位，先跑 host/fetch-native-deps.sh"; exit 1; }

# waylithaudio：引擎的 miniaudio 壳（host/audio，安卓版等义移植），CoreAudio 后端。
# -x objective-c：miniaudio 0.11.22 的 iOS 路径引 AVFoundation 头（AVAudioSession），
# 纯 C 编译见到 ObjC 头就炸——按 ObjC 编译（源码本身是 C，两种模式都吃）。
xcrun -sdk "$SDK" clang \
    -target "$TARGET" \
    -x objective-c \
    -shared -O2 -fvisibility=default -Wno-unused-function -Wno-unused-variable \
    -Wl,-install_name,@rpath/libwaylithaudio.dylib \
    -I "$NDEPS/miniaudio" \
    -framework CoreAudio -framework AudioToolbox -framework AVFoundation -framework Foundation \
    -o "$APP/libwaylithaudio.dylib" \
    "$ROOT/host/audio/waylithaudio.c" -lm

# vma：Vulkan Memory Allocator 3.3.0 的 C API（host/native/vma.cpp，填充 libvulkan 的函数指针）
xcrun -sdk "$SDK" clang++ \
    -target "$TARGET" \
    -std=c++17 -O2 -shared -fvisibility=default \
    -Wl,-install_name,@rpath/libvma.dylib \
    -I "$NDEPS/VulkanMemoryAllocator-3.3.0/include" -I "$INC" \
    -o "$APP/libvma.dylib" \
    "$ROOT/host/native/vma.cpp"

# stb：stb_image + stb_image_write 的导出面（host/native/stb.c）
xcrun -sdk "$SDK" clang \
    -target "$TARGET" \
    -O2 -shared -fvisibility=default \
    -Wl,-install_name,@rpath/libstb.dylib \
    -I "$NDEPS" \
    -o "$APP/libstb.dylib" \
    "$ROOT/host/native/stb.c" -lm

# gli：DDS/KTX/KMG 纹理库的 C 面（header-only + glm，单编译单元）
xcrun -sdk "$SDK" clang++ \
    -target "$TARGET" \
    -std=c++17 -O2 -shared -fvisibility=default -Wno-unused-parameter \
    -Wl,-install_name,@rpath/libgli.dylib \
    -I "$NDEPS/gli-master" -I "$NDEPS/glm-1.0.1" \
    -DGLM_ENABLE_EXPERIMENTAL -DGLM_FORCE_SILENT_WARNINGS \
    -o "$APP/libgli.dylib" \
    "$ROOT/host/native/gli_c.cpp"

# ktx：Khronos libktx 4.4.0 的读+转码子集 + Brutal 的入口名（<函数>1），文件清单与宏照安卓
KTX="$NDEPS/KTX-Software-4.4.0"
KDEFS="-DLIBKTX -DKTX_FEATURE_KTX1 -DKTX_FEATURE_KTX2 -DKTX_OMIT_VULKAN=1 -DSUPPORT_SOFTWARE_ETC_UNPACK=1 -DBASISD_SUPPORT_FXT1=0"
KINC="-I $KTX/include -I $KTX/lib -I $KTX/utils -I $KTX/other_include -I $KTX/external -I $KTX/external/basisu/transcoder -I $KTX/external/basisu/zstd -I $KTX/external/dfdutils -I $INC"
KOBJ="$ROOT/build-m2/ktx"
rm -rf "$KOBJ"
mkdir -p "$KOBJ"
for f in lib/checkheader.c lib/filestream.c lib/hashlist.c lib/info.c lib/memstream.c lib/strings.c lib/swap.c lib/texture.c lib/texture1.c lib/texture2.c lib/vkformat_check.c lib/vkformat_check_variant.c lib/vkformat_str.c lib/vkformat_typesize.c \
         external/dfdutils/createdfd.c external/dfdutils/colourspaces.c external/dfdutils/interpretdfd.c external/dfdutils/printdfd.c external/dfdutils/queries.c external/dfdutils/vk2dfd.c external/basisu/zstd/zstd.c; do
    xcrun -sdk "$SDK" clang -target "$TARGET" -std=c11 -O2 -fPIC -w $KDEFS $KINC \
        -c "$KTX/$f" -o "$KOBJ/$(echo "$f" | tr '/' '_').o"
done
for f in lib/basis_transcode.cpp lib/miniz_wrapper.cpp external/basisu/transcoder/basisu_transcoder.cpp lib/etcunpack.cxx external/etcdec/etcdec.cxx; do
    xcrun -sdk "$SDK" clang++ -target "$TARGET" -std=c++17 -O2 -fPIC -w $KDEFS $KINC \
        -c "$KTX/$f" -o "$KOBJ/$(echo "$f" | tr '/' '_').o"
done
xcrun -sdk "$SDK" clang -target "$TARGET" -std=c11 -O2 -fPIC -w $KDEFS $KINC \
    -c "$ROOT/host/native/ktx_c.c" -o "$KOBJ/ktx_c.o"
xcrun -sdk "$SDK" clang++ -target "$TARGET" -shared -fvisibility=default \
    -Wl,-install_name,@rpath/libKtx.dylib \
    -o "$APP/libKtx.dylib" "$KOBJ"/*.o -lm

# astc：ARM astcenc 5.7.0，NEON（arm64 全系），隐藏可见性只露 shim 的入口
xcrun -sdk "$SDK" clang++ \
    -target "$TARGET" \
    -std=c++17 -O3 -shared -fvisibility=hidden \
    -DASTCENC_NEON=1 -DASTCENC_SSE=0 -DASTCENC_AVX=0 -DASTCENC_SVE=0 -DASTCENC_POPCNT=0 -DASTCENC_F16C=0 -DASTCENC_NO_INVARIANCE=1 \
    -Wl,-install_name,@rpath/libportastc.dylib \
    -I "$NDEPS/astc-encoder-5.7.0/Source" \
    -o "$APP/libportastc.dylib" \
    "$ROOT/host/native/astc_shim.cpp" "$NDEPS/astc-encoder-5.7.0/Source"/astcenc_*.cpp -lm

# 4.8) Slang 2026.19（PC 版游戏的着色器编译器版本，着色器缓存键与 build tag 绑定）：三件库 + 代理。
#      产物由 host/fetch-slang.sh + host/build-slang.sh 备好（两段式：宿主 generators → iOS arm64）。
SLANG="$ROOT/build-slang/out"
[ -f "$ROOT/build-slang/.built-v1" ] || { echo "::error::Slang 未就位（host/fetch-slang.sh + host/build-slang.sh）"; exit 1; }
for f in libslang-compiler.dylib libslang-glslang-2026.19.so libslang-glsl-module-2026.19.so; do
    [ -f "$SLANG/$f" ] || { echo "::error::build-slang/out 缺 $f"; exit 1; }
    cp "$SLANG/$f" "$APP/"
done
# Slang 的 cmake 给的 install_name 可能是构建机绝对路径：统一钉成 @rpath（代理链接与设备装载都靠它）。
for f in libslang-compiler.dylib libslang-glslang-2026.19.so libslang-glsl-module-2026.19.so; do
    install_name_tool -id "@rpath/$f" "$APP/$f"
done
# 代理（host/native/slang_proxy.c）：锚点函数取 Slang 入口的地址，链接器为解析这个未定义符号挂上
# libslang-compiler.dylib——dlsym 顺着依赖搜索，引擎 P/Invoke "slang" 即落进 Slang。
xcrun -sdk "$SDK" clang \
    -target "$TARGET" \
    -shared \
    -Wl,-install_name,@rpath/libslang.dylib \
    -L "$APP" -lslang-compiler \
    -o "$APP/libslang.dylib" \
    "$ROOT/host/native/slang_proxy.c"

# 4.9) ImGui（Dear ImGui 1.92.2 docking + 平铺 C 面 imgui_c.cpp）与其 FreeType 加载器。
#      安卓版的两个补丁（上游 1.92.3 的字体图矩槽复用修复、FreeType 字形加载的线程锁）打在副本上。
IMGUI="$NDEPS/imgui-1.92.2-docking"
IMGUI_BUILD="$ROOT/build-m2/imgui-src"
rm -rf "$IMGUI_BUILD"
cp -R "$IMGUI" "$IMGUI_BUILD"
for p in "$ROOT/host/native/rect-generation.patch" "$ROOT/host/native/thread-lock.patch"; do
    patch -s -p1 -d "$IMGUI_BUILD" < "$p"
done
# -mms-bitfields 不是 x86 专用：它强制 MSVC 的位域布局规则，托管侧（按 PC 的 imgui.dll 布局编译的
# Brutal.ImGui）的结构体尺寸才能对上——iOS 默认的 Itanium 规则下 ImGuiContext/ImGuiStackLevelInfo
# 等位域结构差 8 字节（CI 实测 + 桩头本地双跑对照坐实；安卓版一直带着这面旗，删它是我上一轮的错判）。
IMGUI_DEFS="-DIMGUI_DISABLE_OBSOLETE_FUNCTIONS -DIMGUI_USE_WCHAR32 -DNDEBUG -mms-bitfields"
# imgui_draw.cpp 的字体装载换成 port_fonts.cpp 的映射版（ImFileLoadToMemory → PortFontFileLoad）
xcrun -sdk "$SDK" clang++ -target "$TARGET" -std=c++17 -O2 -fPIC $IMGUI_DEFS \
    -DImFileLoadToMemory=PortFontFileLoad -I "$IMGUI_BUILD" \
    -c -o "$ROOT/build-m2/imgui_draw.o" "$IMGUI_BUILD/imgui_draw.cpp"
# darwin 符号变换：生成件的 1146 个导出别名是 __asm__("名")——Mach-O 上原样落符号（不带下划线），
# 而 dlsym/NativeLibrary 查找时自动补 _，两边对不上（安卓的 ELF 两边都是裸名所以没事）。
# 编译用副本给别名补下划线，生成件本身保持与安卓同源。
sed 's/__asm__("/__asm__("_/' "$ROOT/host/native/imgui_c.cpp" > "$ROOT/build-m2/imgui_c_darwin.cpp"
xcrun -sdk "$SDK" clang++ -target "$TARGET" -std=c++17 -O2 -shared -fvisibility=default \
    -Wl,-install_name,@rpath/libimgui.dylib \
    $IMGUI_DEFS -I "$IMGUI_BUILD" \
    -o "$APP/libimgui.dylib" \
    "$IMGUI_BUILD/imgui.cpp" "$ROOT/build-m2/imgui_draw.o" \
    "$IMGUI_BUILD/imgui_tables.cpp" "$IMGUI_BUILD/imgui_widgets.cpp" "$IMGUI_BUILD/imgui_demo.cpp" \
    "$ROOT/host/native/port_fonts.cpp" "$ROOT/build-m2/imgui_c_darwin.cpp"
# 别名落位验证：引擎按这些名字解析，缺一个就是白编。注意不用 grep -q——它命中即退出会让 nm
# 吃 SIGPIPE，在 set -o pipefail 下把"找到了"误判成失败（真凶复盘：本地 llvm-nm 确认符号全在，
# CI 的报错是 grep -q 提前退出 + pipefail 的组合误报）。grep 读完整个输入，nm 自然结束。
if ! nm -gU "$APP/libimgui.dylib" | grep ' _AcceptDragDropPayload$' > /dev/null; then
    echo "::error::libimgui 里没有 _AcceptDragDropPayload（asm 别名的下划线变换没生效）"; exit 1
fi
echo "imgui 别名抽查：_AcceptDragDropPayload 在位"

# FreeType 2.13.3 静态库 + imgui_freetype（引擎的 FreeType 字形加载器）
FT="$NDEPS/freetype-VER-2-13-3"
FTOBJ="$ROOT/build-m2/freetype"
rm -rf "$FTOBJ"
mkdir -p "$FTOBJ"
FT_SOURCES="src/base/ftsystem.c src/base/ftinit.c src/base/ftdebug.c src/base/ftbase.c src/base/ftbbox.c src/base/ftglyph.c src/base/ftbdf.c src/base/ftbitmap.c src/base/ftcid.c src/base/ftfstype.c src/base/ftgasp.c src/base/ftgxval.c src/base/ftmm.c src/base/ftotval.c src/base/ftpatent.c src/base/ftpfr.c src/base/ftstroke.c src/base/ftsynth.c src/base/fttype1.c src/base/ftwinfnt.c src/truetype/truetype.c src/cff/cff.c src/sfnt/sfnt.c src/psaux/psaux.c src/psnames/psnames.c src/pshinter/pshinter.c src/autofit/autofit.c src/smooth/smooth.c src/raster/raster.c src/sdf/sdf.c src/type1/type1.c src/cid/type1cid.c src/type42/type42.c src/pfr/pfr.c src/winfonts/winfnt.c src/pcf/pcf.c src/bdf/bdf.c src/gzip/ftgzip.c src/lzw/ftlzw.c src/svg/svg.c"
for f in $FT_SOURCES; do
    xcrun -sdk "$SDK" clang -target "$TARGET" -O2 -fPIC -DFT2_BUILD_LIBRARY \
        -I "$FT/include" -w -c "$FT/$f" -o "$FTOBJ/$(basename "$f" .c).o"
done
xcrun -sdk "$SDK" ar rcs "$FTOBJ/libfreetype.a" "$FTOBJ"/*.o
xcrun -sdk "$SDK" clang++ -target "$TARGET" -std=c++17 -O2 -shared -fvisibility=default \
    -Wl,-install_name,@rpath/libimgui_freetype.dylib \
    $IMGUI_DEFS \
    -I "$IMGUI_BUILD" -I "$FT/include" \
    -o "$APP/libimgui_freetype.dylib" \
    "$IMGUI_BUILD/misc/freetype/imgui_freetype.cpp" "$ROOT/host/native/imgui_freetype_c.cpp" \
    "$FTOBJ/libfreetype.a" -L "$APP" -limgui

# 5) 宿主可执行（M2 骨架 + 三角形 + 音频/原生库自检 + .NET 探针诊断件 + 引擎装载件）
xcrun -sdk "$SDK" clang \
    -target "$TARGET" \
    -fobjc-arc -DPORT17_INTERP=1 \
    -I "$ROOT/host/app" -I "$INC" \
    -Wl,-rpath,@executable_path \
    -L "$APP" -lglfw -lvulkan -lwaylithaudio \
    -framework UIKit -framework Foundation -framework QuartzCore -framework CoreGraphics -framework Metal \
    -o "$APP/Port17Host" \
    "$ROOT/host/app/main.m" "$ROOT/host/app/host.c" "$ROOT/host/app/engine.c" "$ROOT/host/app/triangle.c" "$ROOT/host/app/audio_test.c" "$ROOT/host/app/libs_check.c"
cp "$ROOT/host/app/Info.plist" "$APP/"

# 6) .NET 探针诊断件（M1 全套：官方运行时 + Hello，平铺 .app 根——CoreLib 同目录原则）
dotnet build "$ROOT/probe/hello/hello.csproj" -c Release -o "$ROOT/build-m2/hello"
cp "$ROOT"/runtime-out-official/*.dylib "$APP/"
cp "$ROOT"/runtime-out-official/*.dll "$APP/"
cp "$ROOT/build-m2/hello/Hello.dll" "$APP/"

# 6.5) 出包自检：install_name 必须是 @rpath（默认值是构建机绝对路径，设备上 dyld 拒载而 CI 全绿）；
#      所有自建 dylib 的依赖行里出现构建机路径的，自动 -change 成 @rpath/<basename>（同目录即能解析），
#      改不动的直接失败，不让坏包出门。
fixup_dylib() {  # <dylib 路径>
    local lib="$1" deps dep base
    deps="$(otool -L "$lib" | tail -n +2 | grep -oE '/Users/[^ ]*\.dylib' || true)"
    for dep in $deps; do
        base="$(basename "$dep")"
        if [ -e "$APP/$base" ]; then
            install_name_tool -change "$dep" "@rpath/$base" "$lib"
            echo "install_name_tool -change $lib: $base -> @rpath"
        else
            echo "::error::$lib 依赖 $dep（构建机路径，且 bundle 里没有 $base）"
            exit 1
        fi
    done
}
for lib in "$APP"/*.dylib "$APP"/libslang-glslang-*.so "$APP"/libslang-glsl-module-*.so; do
    fixup_dylib "$lib"
done
for lib in libglfw.dylib libvulkan.dylib libslang.dylib libslang-compiler.dylib; do
    NAME="$(otool -D "$APP/$lib" | tail -1)"
    echo "otool -D $lib: ${NAME}"
    case "$NAME" in
        @rpath/*) ;;
        *) echo "::error::$lib 的 install_name 是 ${NAME}（应为 @rpath/…）"; exit 1 ;;
    esac
done
echo "---- otool -L Port17Host ----"
otool -L "$APP/Port17Host"
# 首行是输入文件自己的完整路径（构建机路径），依赖从第二行起——只查依赖行。
if otool -L "$APP/Port17Host" | tail -n +2 | grep -q "runner/work"; then
    echo "::error::宿主依赖里有构建机绝对路径"; exit 1
fi

# 7) ad-hoc 签名：先内嵌文件与 framework，后 bundle（顺序不能反）
ENT="$ROOT/host/app/entitlements.plist"
for f in "$APP"/*.dylib "$APP"/libslang-glslang-*.so "$APP"/libslang-glsl-module-*.so "$APP/MoltenVK.framework/MoltenVK" "$APP/Port17Host"; do
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
