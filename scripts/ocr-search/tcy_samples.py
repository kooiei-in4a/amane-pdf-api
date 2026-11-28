"""縦中横の字面と検索語の正解位置を持つ合成試料（Issue #155）。"""
import math
import re

from PIL import Image, ImageDraw, ImageFont


SIZE = (2480, 3508)
CASES = {
    "tcy-basic": ([["青い鳥が12羽飛びました。", "星空通信第25号を読みます。", "観察日は10月三日です。"]], None, None),
    "tcy-three-digits": ([["記録した木の実は128個です。", "数を調べて表にまとめます。"]], None, None),
    "tcy-heading-ruby": ([["夕方には鳥が24羽戻ります。", "野原の観察記録を読みます。"]], "野原観察だより", "のはら"),
    "tcy-two-tiers": ([["上の池には鳥が16羽います。", "池の周りを歩きました。"],
                       ["下の池には鳥が38羽います。", "水辺には草が生えています。"]], None, None),
    "tcy-negative": ([["花が１輪咲きました。", "小鳥が三羽遊んでいます。"]], None, None),
}


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
