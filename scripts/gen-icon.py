# 生成 Port17 的占位图标（纯stdlib PNG 写出器，无 PIL 依赖）：深蓝底 + 亮边框的纯色块。
# 输出到 host/app/icons/，build-ipa.sh 拷进 .app。要换正式图标：替换这里的颜色/画法
# 或直接放同名 PNG 进 host/app/icons/ 再重跑出包。
import os, struct, zlib

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "host", "app", "icons")
SIZES = [120, 152, 167, 180, 1024]
BASE = (40, 53, 147)     # 靛蓝
BORDER = (121, 134, 203) # 亮一档的边框


def png(path, w, h):
    rows = bytearray()
    bw = max(4, w // 14)  # 边框宽
    for y in range(h):
        rows.append(0)  # filter: none
        for x in range(w):
            r, g, b = BORDER if (x < bw or y < bw or x >= w - bw or y >= h - bw) else BASE
            rows += bytes((r, g, b))

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)  # 8bit RGB
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) +
                chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b""))


os.makedirs(OUT, exist_ok=True)
for s in SIZES:
    png(os.path.join(OUT, f"icon-{s}.png"), s, s)
    print("icon", s)
