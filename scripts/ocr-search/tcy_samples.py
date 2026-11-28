"""縦中横の字面と検索語の正解位置を持つ合成試料（Issue #155）。"""
import math
import random
import re

from PIL import Image, ImageDraw, ImageFilter, ImageFont


SIZE = (2480, 3508)
CASES = {
    "tcy-basic": ([["青い鳥が12羽飛びました。", "星空通信第25号を読みます。", "観察日は10月三日です。"]], None, None),
    "tcy-three-digits": ([["記録した木の実は128個です。", "数を調べて表にまとめます。"]], None, None),
    "tcy-heading-ruby": ([["夕方には鳥が24羽戻ります。", "野原の観察記録を読みます。"]], "野原観察だより", "のはら"),
    "tcy-two-tiers": ([["上の池には鳥が16羽います。", "池の周りを歩きました。"],
                       ["下の池には鳥が38羽います。", "水辺には草が生えています。"]], None, None),
    "tcy-negative": ([["花が１輪咲きました。", "小鳥が三羽遊んでいます。"]], None, None),
}


BAND_GLYPHS = "いりけにハル川八小州什"
BAND_COLUMNS = (
    "小さな川のそばに八つの灯りがともり、けむりが流れた。",
    "古い小屋に入り、八枚の札からハとルの印を見つけた。",
    "州境の川べりに什器を運び、岩の陰に小さな旗を立てた。",
    "広い川には小舟があり、八本の柱に細い糸をかけていた。",
    "明け方に小雨が降り、川沿いの什器に白い布をかけた。",
)


def band_controls(font_path):
    # Each entry in glyphs identifies the source glyph and its cell on the scan.
    font = ImageFont.truetype(str(font_path), 76)
    clean = Image.new("L", SIZE, 255)
    draw = ImageDraw.Draw(clean)
    glyphs = []
    for column, text in enumerate(BAND_COLUMNS):
        x = 2020 - column * 125
        for row, glyph in enumerate(text):
            y = 480 + row * 92
            draw.text((x, y), glyph, fill=0, font=font, anchor="lt")
            if glyph in BAND_GLYPHS:
                glyphs.append((glyph, x, y))
    yield "tcy-bands-clean", clean, glyphs

    # Thin portions of strokes can disappear in a scan while leaving parallel strokes.
    for streaked in (False, True):
        rng = random.Random(158 if streaked else 156)
        blurred = clean.filter(ImageFilter.MinFilter(5)).filter(ImageFilter.GaussianBlur(2.25 if streaked else 1.25))
        draw = ImageDraw.Draw(blurred)
        for _, x, y in glyphs:
            cut = rng.randrange(12, 47)
            left = x + rng.randrange(2, 36)
            if streaked:
                draw.rectangle((left, y + 10, left + 14, y + 59), fill=224)
            else:
                draw.rectangle((left, y + cut, left + 27, y + cut + 9), fill=248)
        for _ in range(400 if streaked else 200):
            x, y = rng.randrange(1470, 2130), rng.randrange(470, 3060)
            draw.ellipse((x, y, x + 2, y + 2), fill=rng.randrange(100, 230))
        yield ("tcy-bands-streaked" if streaked else "tcy-bands-faded"), blurred, glyphs


def samples(api, font_path, image_pdf):
    font = ImageFont.truetype(str(font_path), 76)
    heading_font = ImageFont.truetype(str(font_path), 82)
    ruby_font = ImageFont.truetype(str(font_path), 29)
    for name, (tiers, heading, ruby) in CASES.items():
        image = Image.new("L", SIZE, 255)
        draw = ImageDraw.Draw(image)
        numbers, phrases = {}, []
        if heading:
            draw.text((290, 215), heading, fill=0, font=heading_font)
            phrases.append(heading)
        for tier, columns in enumerate(tiers):
            for column, text in enumerate(columns):
                x = 2020 - column * 125 - tier * 400
                y = 480 + tier * 1450
                for row, glyph in enumerate(re.findall(r"[0-9]+|.", text)):
                    if glyph[0].isascii() and glyph[0].isdigit():
                        digit_font = font.font_variant(size=76 if len(glyph) == 2 else 54)
                        left, top, right, bottom = digit_font.getbbox(glyph)
                        cell = Image.new("L", (right - left, bottom - top), 255)
                        ImageDraw.Draw(cell).text((-left, -top), glyph, fill=0, font=digit_font)
                        px = x + (76 - cell.width) // 2
                        py = y + (92 - cell.height) // 2
                        image.paste(cell, (px, py))
                        ink = cell.point(lambda value: 255 if value < 128 else 0).getbbox()
                        numbers[glyph] = (px + ink[0], py + ink[1], px + ink[2], py + ink[3])
                    else:
                        draw.text((x, y), glyph, fill=0, font=font, anchor="lt")
                    if ruby and column == 0 and row in (3, 4, 5):
                        draw.text((x + 83, y + 15), ruby[row - 3], fill=0, font=ruby_font, anchor="lt")
                    y += 92
                # Verify the complete numeral in context and its position in extraction order.
                phrases.extend(re.findall(r".{2}[0-9]{2,3}.", text))
        if name == "tcy-negative":
            phrases = ["花が1輪", "小鳥が三羽"]
        yield name, image_pdf(api, image, 300, 95), numbers, phrases
    for name, image, _ in band_controls(font_path):
        yield name, image_pdf(api, image, 300, 95), {}, []


def expected_boxes(mask, numbers):
    # /from-images centers the image inside 10 mm A4 margins. Use that
    # placement to find each digit run, then measure the rendered ink.
    page_width, page_height, margin = (mm * 300 / 25.4 for mm in (210, 297, 10))
    scale = min((page_width - 2 * margin) / SIZE[0], (page_height - 2 * margin) / SIZE[1])
    offset_x, offset_y = (page_width - SIZE[0] * scale) / 2, (page_height - SIZE[1] * scale) / 2
    result = {}
    for number, (left, top, right, bottom) in numbers.items():
        x0, x1 = math.floor(offset_x + left * scale) - 2, math.ceil(offset_x + right * scale) + 2
        y0, y1 = math.floor(offset_y + top * scale) - 2, math.ceil(offset_y + bottom * scale) + 2
        ys, xs = mask[y0:y1, x0:x1].nonzero()
        result[number] = [x0 + int(xs.min()), y0 + int(ys.min()), x0 + int(xs.max()) + 1, y0 + int(ys.max()) + 1]
    return result
