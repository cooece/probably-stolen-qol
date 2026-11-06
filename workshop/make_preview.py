from PIL import Image, ImageDraw, ImageFont, ImageFilter
import os

SRC = "2D_game_cover_background__grit_2026-11-06T20-52-35.png"
OUT = "preview.png"

F_PIX = "fonts/IPix.ttf"
F_UI = "C:/Windows/Fonts/msyhbd.ttc"

NAVY = (10, 22, 32)
CREAM = (242, 231, 207)
AMBER = (232, 176, 84)
TEAL = (127, 211, 224)
DIM = (176, 190, 200)


def render(width):
    height = round(width * 9 / 16)
    s = width / 1280.0

    def p(v):
        return max(1, round(v * s))

    def font(path, size, index=0):
        return ImageFont.truetype(path, p(size), index=index)

    # 底图：去掉右下角水印，裁成 16:9
    src = Image.open(SRC).convert("RGB")
    w0, h0 = src.size
    im = src.crop((0, 0, w0, round(w0 * 9 / 16))).resize((width, height), Image.LANCZOS)
    im = im.convert("RGBA")

    # 中央压暗，给标题腾出干净的底
    veil = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    vd = ImageDraw.Draw(veil)
    cx, cy = width // 2, p(300)
    for i in range(p(300), 0, -p(6)):
        a = int(150 * (1 - i / p(300)) ** 1.4)
        vd.ellipse([cx - p(560) * i / p(300), cy - p(300) * i / p(300) * 1.4,
                    cx + p(560) * i / p(300), cy + p(300) * i / p(300) * 1.4],
                   fill=(4, 10, 16, a))
    im = Image.alpha_composite(im, veil)

    draw = ImageDraw.Draw(im, "RGBA")

    # ---------- 顶部铭牌（深色金属 + 琥珀描边）----------
    y1, y2 = p(34), p(100)
    x1, x2 = p(330), p(950)
    notch = p(38)
    draw.polygon([(x1 - notch, (y1 + y2) // 2), (x1, y1), (x2, y1),
                  (x2 + notch, (y1 + y2) // 2), (x2, y2), (x1, y2)],
                 fill=(16, 28, 38, 240), outline=AMBER + (255,), width=p(3))

    tag = "深空当铺 · 模组"
    f_tag = font(F_UI, 34)
    tb = draw.textbbox((0, 0), tag, font=f_tag)
    draw.text(((width - (tb[2] - tb[0])) / 2, (y1 + y2 - (tb[3] - tb[1])) / 2 - p(6)),
              tag, font=f_tag, fill=AMBER)

    # ---------- 主标题（像素字）----------
    title = "便利整合"
    f_title = font(F_PIX, 132)
    tb = draw.textbbox((0, 0), title, font=f_title)
    tw, th = tb[2] - tb[0], tb[3] - tb[1]
    tx = (width - tw) / 2 - tb[0]
    ty = p(176) - tb[1]

    # 琥珀光晕
    glow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.text((tx, ty), title, font=f_title, fill=AMBER + (255,))
    glow = glow.filter(ImageFilter.GaussianBlur(p(9)))
    im = Image.alpha_composite(im, glow)
    draw = ImageDraw.Draw(im, "RGBA")

    # 阴影 + 描边 + 字面
    draw.text((tx + p(6), ty + p(8)), title, font=f_title, fill=(0, 0, 0, 150))
    draw.text((tx, ty), title, font=f_title, fill=CREAM,
              stroke_width=p(12), stroke_fill=NAVY)

    # ---------- 英文副标题 ----------
    sub = "QoL BUNDLE"
    f_sub = font(F_PIX, 66)
    tb = draw.textbbox((0, 0), sub, font=f_sub)
    sx = (width - (tb[2] - tb[0])) / 2 - tb[0]
    sy = p(354) - tb[1]
    draw.text((sx + p(4), sy + p(5)), sub, font=f_sub, fill=(0, 0, 0, 150))
    draw.text((sx, sy), sub, font=f_sub, fill=TEAL, stroke_width=p(5), stroke_fill=NAVY)

    # ---------- 数据牌 ----------
    cap = "21 项开关  ·  随时关掉"
    f_cap = font(F_UI, 28)
    tb = draw.textbbox((0, 0), cap, font=f_cap)
    cw = tb[2] - tb[0]
    cx1, cx2 = (width - cw) / 2 - p(30), (width + cw) / 2 + p(30)
    cy1, cy2 = p(462), p(522)
    draw.rounded_rectangle([cx1, cy1, cx2, cy2], radius=p(6),
                           fill=(14, 26, 36, 235), outline=TEAL + (170,), width=p(2))
    draw.text(((width - cw) / 2, cy1 + p(13)), cap, font=f_cap, fill=CREAM)

    # ---------- 底部信息条 ----------
    strip = p(646)
    draw.rectangle([0, strip, width, height], fill=(7, 14, 21, 225))
    draw.line([(0, strip), (width, strip)], fill=AMBER + (255,), width=p(2))
    tags = "自动换电   ·   真伪鉴定   ·   今日行情   ·   框选统计   ·   仓储整理"
    f_tags = font(F_UI, 25)
    tb = draw.textbbox((0, 0), tags, font=f_tags)
    draw.text(((width - (tb[2] - tb[0])) / 2, strip + p(20)), tags, font=f_tags, fill=DIM)

    im.convert("RGB").save(OUT, "PNG", optimize=True)
    return width, height, os.path.getsize(OUT)


for w in (1280, 1152, 1024):
    size = render(w)
    kb = size[2] / 1024
    print(size[0], "x", size[1], round(kb), "KB")
    if kb < 980:
        break
