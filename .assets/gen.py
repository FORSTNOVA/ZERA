# -*- coding: utf-8 -*-
"""
ZERA 品牌素材生成器（程序化绘制，零外部依赖）
产出：
  starfield-dark.png   512x512 可无缝平铺的深空星图贴图
  starfield-light.png  512x512 可无缝平铺的晨曦天穹贴图
  app-logo.png         512x512 应用 Logo（圆角方形 + 星空 + 发光 Z）
  app.ico              多尺寸图标（PNG 条目，Win10/11 支持）
设计目标：与 Colors.Dark / Colors.Light 的令牌值严格对齐，保证背景与贴图无缝衔接。
"""
import math
import os
import random
import struct

from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.dirname(os.path.abspath(__file__))
TILE = 512

# ---- 与主题令牌严格对齐 ----
DARK_BG = (0x0A, 0x0E, 0x1C)      # Colors.Dark AppBgColor
LIGHT_BG = (0xED, 0xF1, 0xFB)     # Colors.Light AppBgColor
DEEP_TOP = (0x0B, 0x10, 0x26)     # Logo 渐变起点
DEEP_BOT = (0x2A, 0x2F, 0x6B)     # Logo 渐变终点
STAR_W = (0xF2, 0xF6, 0xFF)


def draw_star(layer, x, y, r, color, alpha):
    """画一颗圆点星，带极淡的光晕。"""
    d = ImageDraw.Draw(layer, "RGBA")
    if r >= 1.0:
        glow = max(2, int(r * 3))
        d.ellipse([x - glow, y - glow, x + glow, y + glow],
                  fill=color + (int(alpha * 0.16),))
    d.ellipse([x - r, y - r, x + r, y + r], fill=color + (alpha,))


def draw_flare(layer, x, y, length, color, alpha):
    """四角星芒（十字光芒）—— 星空的'点睛'元素。"""
    d = ImageDraw.Draw(layer, "RGBA")
    w = 1
    for (dx, dy) in ((1, 0), (0, 1)):
        if dx:
            d.line([x - length, y, x + length, y], fill=color + (alpha,), width=w)
        else:
            d.line([x, y - length, x, y + length], fill=color + (alpha,), width=w)
    d.ellipse([x - 2, y - 2, x + 2, y + 2], fill=color + (alpha,))


def wrapped_positions(x, y):
    """无缝平铺：把同一个星在 3x3 个偏移位置都画一遍。"""
    for ox in (-TILE, 0, TILE):
        for oy in (-TILE, 0, TILE):
            yield x + ox, y + oy


def make_tile(bg, star_count, rmin, rmax, amin, amax, warm_ratio, flare_count,
              nebula, seed):
    rnd = random.Random(seed)
    img = Image.new("RGB", (TILE, TILE), bg)
    layer = Image.new("RGBA", (TILE, TILE), (0, 0, 0, 0))

    # 星云：极低不透明度的大半径柔斑（先画，压在星星下面）
    if nebula:
        neb = Image.new("RGBA", (TILE, TILE), (0, 0, 0, 0))
        nd = ImageDraw.Draw(neb)
        for _ in range(nebula):
            cx, cy = rnd.uniform(0, TILE), rnd.uniform(0, TILE)
            rr = rnd.uniform(90, 200)
            tint = rnd.choice([(0x5B, 0x6B, 0xE0), (0x8B, 0x7C, 0xF6), (0x3E, 0x5C, 0xC8)])
            nd.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=tint + (14,))
        neb = neb.filter(ImageFilter.GaussianBlur(48))
        layer = Image.alpha_composite(layer, neb)
        layer = Image.alpha_composite(layer, layer)  # no-op 保持类型

    # 普通星
    for _ in range(star_count):
        x, y = rnd.uniform(0, TILE), rnd.uniform(0, TILE)
        r = rnd.uniform(rmin, rmax)
        a = int(rnd.uniform(amin, amax))
        warm = rnd.random() < warm_ratio
        col = (0xFF, 0xE3, 0xB8) if warm else STAR_W
        for px, py in wrapped_positions(x, y):
            draw_star(layer, px, py, r, col, a)

    # 亮星 + 星芒
    for _ in range(flare_count):
        x, y = rnd.uniform(0, TILE), rnd.uniform(0, TILE)
        a = int(rnd.uniform(170, 235))
        ln = rnd.uniform(4, 9)
        for px, py in wrapped_positions(x, y):
            draw_flare(layer, px, py, ln, STAR_W, a)
            draw_star(layer, px, py, rnd.uniform(0.9, 1.5), STAR_W, 255)

    img = Image.alpha_composite(img.convert("RGBA"), layer).convert("RGB")
    return img


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def make_logo(size=512):
    # 1) 圆角方形 + 对角深空渐变
    grad = Image.new("RGB", (size, size))
    gd = ImageDraw.Draw(grad)
    for y in range(size):
        for_x = 0  # 渐变按对角走：把纵向插值 + 横向偏移叠加
        t = y / (size - 1)
        c = tuple(int(DEEP_TOP[i] + (DEEP_BOT[i] - DEEP_TOP[i]) * t) for i in range(3))
        gd.line([(0, y), (size, y)], fill=c)
    # 横向再叠一层从左到右的提亮，形成对角光感
    veil = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    vd = ImageDraw.Draw(veil)
    for x in range(size):
        t = x / (size - 1)
        vd.line([(x, 0), (x, size)], fill=(0xFF, 0xFF, 0xFF, int(26 * (1 - t))))
    grad = Image.alpha_composite(grad.convert("RGBA"), veil)

    # 2) 星空（裁剪进圆角方形内）
    sky = make_tile(DEEP_BOT, 90, 0.5, 1.5, 90, 220, 0.18, 6, None, seed=7)
    mask = rounded_mask(size, int(size * 0.225))
    grad.paste(sky, (0, 0), mask)

    # 3) 中心发光 Z（粗几何 Z + 圆角端）
    zl = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    zd = ImageDraw.Draw(zl)
    cx0, cx1 = size * 0.30, size * 0.70
    ytop, ymid, ybot = size * 0.29, size * 0.50, size * 0.71
    th = size * 0.115          # 笔画粗细
    beam = STAR_W + (255,)
    # 上横
    zd.rounded_rectangle([cx0, ytop - th / 2, cx1, ytop + th / 2],
                         radius=th / 2, fill=beam)
    # 下横
    zd.rounded_rectangle([cx0, ybot - th / 2, cx1, ybot + th / 2],
                         radius=th / 2, fill=beam)
    # 斜笔：四个顶点全部收在两根横笔的**内部**（纵向不越过横笔中线 ±0.62*th），
    # 这样斜笔看起来是从横笔里"长出来"的，两端不会戳出尖角破坏横笔的圆角收头。
    slant = [
        (cx1, ytop + th * 0.62),
        (cx1 - th * 1.05, ytop - th * 0.30),
        (cx0, ybot - th * 0.62),
        (cx0 + th * 1.05, ybot + th * 0.30),
    ]
    zd.polygon(slant, fill=beam)

    glow = zl.filter(ImageFilter.GaussianBlur(size * 0.028))
    grad = Image.alpha_composite(grad, glow)
    grad = Image.alpha_composite(grad, zl)

    # 4) 右上角一颗四角星芒（品牌点睛）
    sp = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw_flare(sp, size * 0.795, size * 0.215, size * 0.052, STAR_W, 235)
    draw_flare(sp, size * 0.20, size * 0.80, size * 0.030, STAR_W, 150)
    grad = Image.alpha_composite(grad, sp)

    # 5) 裁成圆角 + 外描边（让浅色背景下也有清晰轮廓）
    grad.putalpha(rounded_mask(size, int(size * 0.225)))
    stroke = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(stroke).rounded_rectangle(
        [1, 1, size - 2, size - 2], radius=int(size * 0.225) - 1,
        outline=(0xFF, 0xFF, 0xFF, 34), width=2)
    grad = Image.alpha_composite(grad, stroke)
    return grad


def write_ico(png_paths, ico_path):
    """把多张 PNG 打包成 .ico（PNG 条目，Windows Vista+ / .NET 均支持）。"""
    entries = []
    for p in png_paths:
        with open(p, "rb") as f:
            data = f.read()
        im = Image.open(p)
        w, h = im.size
        entries.append((w, h, data))
    # ICONDIR: 保留字0, 类型1(icon), 数量
    out = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    dir_entries = b""
    blob = b""
    for w, h, data in entries:
        dir_entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(data), offset)
        blob += data
        offset += len(data)
    with open(ico_path, "wb") as f:
        f.write(out + dir_entries + blob)


def main():
    # --- 星空贴图 ---
    # AppBg：内容区背景（深空底）
    dark = make_tile(DARK_BG, 150, 0.4, 1.5, 70, 225, 0.16, 7, 3, seed=11)
    dark.save(os.path.join(OUT, "starfield-dark.png"), optimize=True)

    # Surface：侧栏背景（比 AppBg 亮一档的星空，星点更稀，避免和内容区抢注意力）
    surf_dark = make_tile((0x12, 0x18, 0x29), 55, 0.4, 1.2, 55, 165, 0.14, 3, 1, seed=31)
    surf_dark.save(os.path.join(OUT, "starfield-surface-dark.png"), optimize=True)

    # 亮色：晨曦天穹，星点极淡（只是"有天光"的暗示，不能影响文字可读性）
    light = make_tile(LIGHT_BG, 40, 0.4, 1.0, 55, 130, 0.10, 3, 0, seed=23)
    light.save(os.path.join(OUT, "starfield-light.png"), optimize=True)

    # --- Logo ---
    logo = make_logo(512)
    logo.save(os.path.join(OUT, "app-logo.png"), optimize=True)

    # --- 多尺寸 PNG（供 ico 使用）---
    tmpdir = os.path.join(OUT, ".icotmp")
    os.makedirs(tmpdir, exist_ok=True)
    sizes = [16, 24, 32, 48, 64, 128, 256]
    paths = []
    for s in sizes:
        p = os.path.join(tmpdir, "logo_%d.png" % s)
        logo.resize((s, s), Image.LANCZOS).save(p, optimize=True)
        paths.append(p)
    write_ico(paths, os.path.join(OUT, "app.ico"))

    print("OK")
    for n in ("starfield-dark.png", "starfield-surface-dark.png",
              "starfield-light.png", "app-logo.png", "app.ico"):
        fp = os.path.join(OUT, n)
        print("%-28s %8d bytes" % (n, os.path.getsize(fp)))


if __name__ == "__main__":
    main()
