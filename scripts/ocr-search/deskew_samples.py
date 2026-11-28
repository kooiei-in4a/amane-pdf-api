"""Issue #154 用の架空の横書きと罫線付き帳票。座標は元画像の300 dpi。"""
from dataclasses import dataclass
import io
import math

from PIL import Image, ImageDraw, ImageFilter, ImageFont


WIDTH, HEIGHT = 2480, 3508
DOCUMENT = (
    "展示室の利用案内",
    "荷物は入口の棚に置いてから入室してください。",
    "実習で使う道具は係員から受け取ってください。",
    "終了時刻は午後四時です。机を元の位置に戻します。",
)
HEADER = "倉庫備品の購入明細"
NUMBER = "RK-2764"
ITEMS = (("収納箱", "8,430"), ("仕切板", "2,160"), ("保護材", "14,250"), ("移動台", "31,500"))
TOTAL = "56,340"

# Pillowの出力座標から入力座標への射影。H2と同程度の台形歪み。
SHEAR, OFFSET, PERSPECTIVE = -0.045, 24, -0.00002
MATRIX_CASES = (("clean", 0, False, False), ("tilt-0.25", 0.25, False, False),
                ("tilt-0.5", 0.5, False, False), ("tilt-1", 1, False, False),
                ("tilt-2", 2, False, False), ("tilt-3", 3, False, False),
                ("tilt-5", 5, False, False), ("tilt-7", 7, False, False),
                ("tilt-minus-2", -2, False, False), ("tilt-minus-5", -5, False, False),
                ("perspective", 0, True, False), ("blur", 0, False, True),
                ("combined", 5, True, True))


@dataclass
class Sample:
    image: bytes
    truth: str
    boxes: dict
    fields: tuple
    row_pairs: tuple
    terms: tuple
    absent: tuple
    angle: float


def original(kind, font_path):
    image = Image.new("L", (WIDTH, HEIGHT), 255)
    draw = ImageDraw.Draw(image)
    title = ImageFont.truetype(str(font_path), 86)
    font = ImageFont.truetype(str(font_path), 62)
    boxes = {}

    def text(value, x, y, face, terms):
        draw.text((x, y), value, font=face, fill=0)
        for term in terms:
            start = value.index(term)
            advance = face.getlength(value[:start])
            glyphs, (dx, dy) = face.getmask2(term)
            l, t, r, b = glyphs.getbbox()
            boxes[term] = (x + advance + dx + l, y + dy + t, x + advance + dx + r, y + dy + b)

    if kind == "text":
        terms = ("展示室の利用案内", "荷物は入口の棚", "実習で使う道具", "終了時刻は")
        for i, (line, term) in enumerate(zip(DOCUMENT, terms)):
            text(line, 235, 360 + 290*i, title if i == 0 else font, (term,))
        return image, "\n".join(DOCUMENT), boxes, (), (), terms, ()

    text(HEADER, 235, 320, title, ("購入明細",))
    text("管理番号", 235, 585, font, ("管理番号",))
    text(NUMBER, 690, 585, font, (NUMBER,))
    rows = (("品目", "金額"), *ITEMS, ("合計", TOTAL))
    xs = (224, 1185, 2160)
    top, pitch = 905, 174
    bottom = top + pitch * len(rows)
    for x in xs:
        draw.line((x, top, x, bottom), fill=50, width=4)
    for i in range(len(rows) + 1):
        y = top + i * pitch
        draw.line((xs[0], y, xs[-1], y), fill=50, width=4)
    for i, row in enumerate(rows):
        for x, value in zip(xs, row):
            text(value, x + 48, top + i*pitch + 41, font, (value,))

    fields = (NUMBER, *(value for _, value in ITEMS), TOTAL)
    terms = ("購入明細", "管理番号", NUMBER, "品目", "金額", "収納箱", "移動台", "合計", *fields[1:])
    absent = ("品目金額", "管理番号" + NUMBER, "収納箱8,430")
    truth = "\n".join((HEADER, "管理番号 " + NUMBER, *(" ".join(row) for row in rows)))
    return image, truth, boxes, fields, ITEMS, terms, absent


def transformed_box(box, angle, perspective):
    radians = math.radians(angle)
    c, s = math.cos(radians), math.sin(radians)
    cx, cy = WIDTH / 2, HEIGHT / 2
    points = []
    for edge_x in (box[0], box[2]):
        for edge_y in (box[1], box[3]):
            x, y = edge_x - cx, edge_y - cy
            x, y = cx + c*x - s*y, cy + s*x + c*y
            if perspective:
                denominator = 1 - PERSPECTIVE*y
                x, y = (x - SHEAR*y) / denominator - OFFSET, y / denominator
            points.append((x, y))
    # /from-images は画像の縦横比を維持し、A4の10 mmの余白内に配置する。
    page_width, page_height, margin = (mm * 300 / 25.4 for mm in (210, 297, 10))
    scale = min((page_width - 2 * margin) / WIDTH, (page_height - 2 * margin) / HEIGHT)
    left, top = (page_width - WIDTH * scale) / 2, (page_height - HEIGHT * scale) / 2
    xs, ys = zip(*points)
    return [left + min(xs) * scale, top + min(ys) * scale,
            left + max(xs) * scale, top + max(ys) * scale]


def generate(font_path, matrix=False):
    selected = {"tilt-2", "tilt-minus-5", "perspective"}
    for kind in ("text", "form"):
        original_page, truth, boxes, fields, row_pairs, terms, absent = original(kind, font_path)
        for variant, angle, perspective, blur in MATRIX_CASES:
            if not matrix and variant not in selected:
                continue
            page = original_page
            if angle:
                page = page.rotate(-angle, Image.Resampling.BICUBIC, fillcolor=255)
            if perspective:
                page = page.transform(page.size, Image.Transform.PERSPECTIVE,
                                      (1, SHEAR, OFFSET, 0, 1, 0, 0, PERSPECTIVE),
                                      Image.Resampling.BICUBIC, fillcolor=255)
            if blur:
                jpeg = io.BytesIO()
                page.filter(ImageFilter.GaussianBlur(1.8)).save(jpeg, format="JPEG", quality=14)
                page = Image.open(jpeg)
            png = io.BytesIO()
            page.save(png, format="PNG", dpi=(300, 300))
            expected = {term: transformed_box(box, angle, perspective) for term, box in boxes.items()}
            yield f"deskew-{kind}-{variant}", Sample(png.getvalue(), truth, expected, fields, row_pairs, terms, absent, angle)
