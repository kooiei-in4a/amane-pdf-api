"""PR #158 の角度の境界・密度を測るための架空の原稿。"""
from pathlib import Path
import argparse
import json

from PIL import Image, ImageDraw, ImageFont

from deskew_samples import WIDTH, HEIGHT


PROSE = (
    "備品点検のお知らせ",
    "作業の前に用具の名前と個数を確認してください。",
    "返却するときは乾いた布で表面の汚れを拭き取ります。",
    "破損した部品は青い容器に入れ、担当者へお知らせください。",
)
ROWS = (
    ("折り畳み棚", "3,260"), ("紙製仕切り", "8,940"),
    ("角形収納具", "17,520"), ("作業用敷板", "26,300"),
)
DENSE_PROSE = (
    "作業を始める前に窓を開け、室内の照明と換気の状態を確認してください。",
    "記録用紙には実施した時刻と作業の内容を順番に書き、最後に見直してください。",
    "使用した備品の個数を確認し、傷や変形があれば図の該当箇所に印を付けます。",
    "調整の途中で気付いた点は余白に書き、終わった後に記録用紙へ転記します。",
    "乾燥が終わった資料は仕切りのある箱に収め、番号の順に棚へ並べてください。",
    "保管場所の変更がある場合は、古い番号と新しい番号を一覧表に追記します。",
    "用紙の右下に当日の点検印を押し、完了した記録をまとめて提出してください。",
    "備品の持ち出しがあるときは返却予定を記録し、連絡票を机の上に置きます。",
)
DENSE_TERMS = ("室内の照明", "作業の内容", "該当箇所", "記録用紙", "乾燥が終わった", "一覧表", "当日の点検印", "返却予定")


def make_prose(font_path):
    image = Image.new("L", (WIDTH, HEIGHT), 255)
    draw = ImageDraw.Draw(image)
    fonts = [ImageFont.truetype(str(font_path), size) for size in (78, 59)]
    for i, text in enumerate(PROSE):
        draw.text((220, 340 + 285 * i), text, fill=0, font=fonts[min(i, 1)])
    return image, PROSE, ("備品点検", "用具の名前", "表面の汚れ", "担当者"), ()


def make_form(font_path, dense=False):
    image = Image.new("L", (WIDTH, HEIGHT), 255)
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype(str(font_path), 46 if dense else 61)
    title, code = "工房用品の発注控え", "KT-4827"
    top, pitch = (390, 78) if dense else (900, 169)
    data = ROWS
    if dense:
        data = tuple((f"{item}交換部品", f"{4700 + i * 790:,}") for i, (item, _) in enumerate(ROWS * 8))
    total = f"{sum(int(amount.replace(',', '')) for _, amount in data):,}"
    rows = (("品名", "発注額"), *data, ("合計額", total))
    x0, x1, x2 = (205, 1455, 2275) if dense else (245, 1305, 2180)
    bottom = top + len(rows) * pitch
    draw.text((x0, 190 if dense else 320), title, fill=0, font=font)
    draw.text((x0, 280 if dense else 585), "発注番号", fill=0, font=font)
    draw.text((x1 + 42, 280 if dense else 585), code, fill=0, font=font)
    for x in (x0, x1, x2):
        draw.line((x, top, x, bottom), fill=45, width=4)
    for i in range(len(rows) + 1):
        y = top + i * pitch
        draw.line((x0, y, x2, y), fill=45, width=4)
    for i, pair in enumerate(rows):
        for x, value in zip((x0, x1), pair):
            draw.text((x + 42, top + i * pitch + round(pitch / 5)), value, fill=0, font=font)
    truth = (title, "発注番号 " + code, *(" ".join(pair) for pair in rows))
    fields = (code, *(amount for _, amount in data), total)
    terms = ("発注控え", "発注番号", "合計額", code, data[0][1], data[-1][1], total)
    return image, truth, terms, fields


def make_dense_prose(font_path, points):
    image = Image.new("L", (WIDTH, HEIGHT), 255)
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype(str(font_path), round(points * 300 / 72))
    pitch = 59 if points == 10.5 else 68
    lines = []
    body = "".join(DENSE_PROSE)
    # Keep a margin around the ink so the ±5° rotation can fit the raster.
    while len(lines) < 1 + (HEIGHT - 440) // pitch:
        line = ""
        for character in body:
            if font.getlength(line + character) > WIDTH - 390:
                break
            line += character
        body = body[len(line):]
        if len(body) < 120:
            body += "".join(DENSE_PROSE)
        lines.append(line)
    for i, line in enumerate(lines):
        draw.text((195, 215 + i * pitch), line, fill=0, font=font)
    return image, lines, DENSE_TERMS, ()


def cases(font_path):
    tight_angles = (1.5, 2, 2.5, 3, -1.5, -2, -2.5, -3)
    dense_angles = (0, 3, 5, -3, -5)
    for name, document, angles in (
        ("review-text", make_prose(font_path), tight_angles),
        ("review-form", make_form(font_path), tight_angles),
        ("dense-text-10.5pt", make_dense_prose(font_path, 10.5), dense_angles),
        ("dense-text-12pt", make_dense_prose(font_path, 12), dense_angles),
        ("dense-form", make_form(font_path, dense=True), dense_angles),
    ):
        image, truth, terms, fields = document
        for angle in angles:
            page = image.rotate(-angle, Image.Resampling.BICUBIC, fillcolor=255) if angle else image
            yield f"{name}-{angle:+g}", page, {
                "angle": angle, "truth": "\n".join(truth), "terms": terms, "fields": fields,
            }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("font", type=Path)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    args.directory.mkdir(parents=True, exist_ok=True)
    manifest = {}
    for name, page, entry in cases(args.font):
        page.save(args.directory / f"{name}.png", dpi=(300, 300))
        manifest[name] = entry
    (args.directory / "truth.json").write_text(json.dumps(manifest, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
