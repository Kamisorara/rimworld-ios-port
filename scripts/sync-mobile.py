# M4 托管层共享（安卓 mobile/ 的 iOS 对齐，PLAN.md M4 第②步）：从安卓姊妹仓库复制
# hooks/*.cs（完整 PortHooks）与 mobile/*.cs（PortMobile 触控层）到本仓库。
#
# 规则：
# - hooks/WaylithClr.cs 不覆盖——iOS 版经过真机验证（m2-engine-20 起游戏每次启动都在
#   跑它）；两边是同一替身家族的格式化副本。
# - hooks/ModPrep.cs 放到 hooks/ 下（安卓在 patch/，iOS 的 csproj glob 简化为单目录）。
# - 复制是幂等的（重复运行结果相同）；本脚本只拷贝不改写——iOS 化改写直接落在
#   iOS 仓库的文件上（同步会覆盖它们，所以改写尽量少，见 hooks/ 与 mobile/ 内的
#   "iOS:" 注释）。
# 用法：python scripts/sync-mobile.py [安卓仓库路径]
import os
import shutil
import sys

IOS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ANDROID = sys.argv[1] if len(sys.argv) > 1 else r"D:\Data\Code\reverse\rimworld-android-port"

HOOKS_SKIP = {"WaylithClr.cs"}  # iOS 版真机验证过，不覆盖

copied, skipped = [], []


def copy_dir(src_dir, dst_dir, skip=frozenset(), dst_rename=None):
    for name in sorted(os.listdir(src_dir)):
        if not name.endswith(".cs") or name in skip:
            continue
        src = os.path.join(src_dir, name)
        out = dst_rename.get(name, name) if dst_rename else name
        dst = os.path.join(dst_dir, out)
        with open(src, "rb") as f:
            data = f.read()
        if os.path.exists(dst):
            with open(dst, "rb") as f:
                if f.read() == data:
                    skipped.append(out)
                    continue
        with open(dst, "wb") as f:
            f.write(data)
        copied.append(out)


os.makedirs(os.path.join(IOS_ROOT, "mobile"), exist_ok=True)
copy_dir(os.path.join(ANDROID, "hooks"), os.path.join(IOS_ROOT, "hooks"), skip=HOOKS_SKIP)
copy_dir(os.path.join(ANDROID, "mobile"), os.path.join(IOS_ROOT, "mobile"))
copy_dir(os.path.join(ANDROID, "patch"), os.path.join(IOS_ROOT, "hooks"),
         skip={"Patch.cs"},  # 安卓 patch/Patch.cs 的两件功能（UIRoot 钩插入 + Harmony 改写）由 iOS setup 承接
         dst_rename={"ModPrep.cs": "ModPrep.cs"})

print(f"复制 {len(copied)} 个，内容未变 {len(skipped)} 个：")
for name in copied:
    print("  +", name)
for name in skipped:
    print("  =", name)
