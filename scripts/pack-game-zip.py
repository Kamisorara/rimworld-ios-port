# 把 game 目录打成一个 port17-game.zip：设备侧一个文件拖进 Documents，宿主开 app 自动
# 解压安装成 Documents/game。zip 的根就是 game 的内容（RimWorld.dll 在 zip 顶层——宿主
# 也兼容套了一层 game/ 目录的压缩包）。
#
# --raw：Steam 原件直接打包（不打任何补丁）——设备内 setup（新宿主）会自己改写；
#        默认模式：打包前自动跑两道 PC 侧补丁（调试新补丁/无 setup 的旧包用）。
# 已压缩的东西（Assets.zip 本体、贴图 png、音效 ogg……）按 stored 存：打包快、
# 设备端解压也快（没有第二遍 deflate）；其余（dll/txt/xml）deflate。
#
# 用法：python pack-game-zip.py [--raw] [game目录] [输出zip]
#   默认 game 目录 = 仓库里的 game-staging，输出 = 仓库根的 port17-game.zip（gitignored）。
import os, subprocess, sys, zipfile

STORED = {".zip", ".png", ".jpg", ".jpeg", ".ogg", ".wav", ".mp3", ".mp4", ".webp",
          ".astc", ".ktx", ".ktx2", ".basis", ".bnk", ".bank", ".dds", ".tga"}

def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    pos = [a for a in sys.argv[1:] if not a.startswith("--")]
    raw = "--raw" in sys.argv
    game = pos[0] if len(pos) > 0 else os.path.join(root, "game-staging")
    out = pos[1] if len(pos) > 1 else os.path.join(root, "port17-game.zip")
    if not os.path.isfile(os.path.join(game, "RimWorld.dll")):
        print(f"::error::{game} 里没有 RimWorld.dll（先把游戏文件放进来）")
        sys.exit(1)
    if raw:
        print("== --raw：Steam 原件模式，跳过全部补丁（设备内 setup 负责改写）")
        stamp = True  # 不检查戳
    else:
        # 打包前自动跑两道补丁（都幂等）：OS 探测字节补丁 + Cecil 平台改写（安卓 patch 管线的 iOS 雏形）
        print("== 补丁 1/2：OS 探测（Brutal.Vulkan 装载链 / Brutal.Core.Common Platform 家族）")
        subprocess.check_call([sys.executable, "-I",
                               os.path.join(root, "scripts", "patch-os-detect.py"), game])
        print("== 补丁 2/2：平台改写（Cecil：iOS 硬墙逐条）")
        subprocess.check_call(["dotnet", "run", "--project",
                               os.path.join(root, "scripts", "patch-platform"), "--", game])
        stamp = os.path.isfile(os.path.join(game, "os-detect.port17")) and \
                os.path.isfile(os.path.join(game, "platform.port17"))
    count, stored, total = 0, 0, 0
    with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_STORED, allowZip64=True) as z:
        for dirpath, dirnames, filenames in os.walk(game):
            dirnames.sort()
            for name in sorted(filenames):
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, game).replace(os.sep, "/")
                ext = os.path.splitext(name)[1].lower()
                if ext in STORED:
                    z.write(full, rel, compress_type=zipfile.ZIP_STORED)
                    stored += 1
                else:
                    z.write(full, rel, compress_type=zipfile.ZIP_DEFLATED, compresslevel=6)
                count += 1
                total += os.path.getsize(full)
    size = os.path.getsize(out)
    print(f"OK: {out}")
    print(f"  {count} 个文件（{stored} 个 stored），原始 {total/1073741824:.2f} GB → zip {size/1073741824:.2f} GB")
    print(f"  OS 探测补丁戳：{'在' if stamp else '不在！先跑 scripts/patch-os-detect.py ' + game}")
    if not stamp:
        sys.exit(1)

if __name__ == "__main__":
    main()
