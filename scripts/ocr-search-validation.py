#!/usr/bin/env python3
"""OCR済みPDFをPDF.jsの検索で確かめる（Issue #116、#126）。

fill-templateとPillowで作った日本語の画像PDFを、実コンテナの /api/pdf/ocr に標準と高精度で送る。
PDF.js 6.3.289のPDFFindController（ビューアと同じ検索処理）で、語句が分断されずに見つかること、
欧文の単語間の空白が保たれること、離れた欄の語が連結しないことを確かめる。縦書きの横見出し、
罫線付きの表、影、上下逆さの試料も検索する。--benchmark で上限ページ数の処理時間を測る。
事前に scripts/ocr-search で `npm ci --ignore-scripts --omit=optional --omit=dev` を実行しておく。

--highlight DIR を付けると、横書き・回転+CropBox・縦書きと横見出しの試料について、OCR出力PDFと、元画像から
求めた語ごとの期待矩形（300 dpiの表示座標）を DIR に保存する。強調表示の位置は scripts/ocr-search/highlight.mjs
で実測する（CIでは実行しない）。期待矩形は、行の文字の画素の範囲を元画像から求め、行内の
位置をfill-templateが描いたBIZ UDPゴシックの送り幅で按分する。縦書きの語は、列の中で字ごとに分けた画素の範囲を使う。
"""
import argparse
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import time
import uuid
import zipfile

ROOT = Path(__file__).resolve().parent.parent
SEARCH = ROOT / "scripts/ocr-search"
spec = importlib.util.spec_from_file_location("smoke", ROOT / "scripts/docker-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)

BLANK_A4 = {"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, {
    "obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
    "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": 1, "/Kids": ["3 0 R"]}},
    "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842], "/Resources": {},
                            "/Contents": "4 0 R"}},
    "obj:4 0 R": {"stream": {"dict": {}, "data": ""}},
    "trailer": {"value": {"/Root": "1 0 R", "/Size": 5}}}]}
LINES = ["中央図書館の開館時間は午前九時から午後七時までです。", "返却ポストは正面入口の東側にあります。",
         "本製品は Windows 11 と macOS に対応しています。"]
COLUMNS = ["中央図書館の開館時間は午前九時からです。", "返却ポストは正面入口の東側にあります。",
           "休館日は毎月第三月曜日です。", "本人確認書類の画像が必要です。", "市民課までお問い合わせください。"]
HORIZONTAL_TERMS = ["中央図書館", "開館時間", "午前九時", "午後七時", "返却ポスト", "正面入口", "Windows 11"]
VERTICAL_TERMS = ["中央図書館だより", "第3号", "開館時間", "午前九時", "返却ポスト", "正面入口", "休館日",
                  "本人確認書類", "市民課"]
# 同じページの離れた位置にある独立した語。PDF.jsはCJK直後の改行を検索時に除くため、境界がないと
# 「時間返却」が一致してしまう。
SEPARATE_FIELDS = [(0.08, 0.10, "開館時間"), (0.55, 0.45, "返却ポスト"), (0.20, 0.70, "Dark Mode を選択")]
SEPARATE_TERMS = ["開館時間", "返却ポスト", "Dark Mode", "を選択"]
SEPARATE_ABSENT = ["時間返却", "ポストDark", "DarkMode"]
# 同じ段落で行をまたぐ語は、境界を置かずにつながったまま検索できること。
WRAPPED_LINES = ["市民会館の大ホールは改修工", "事のため来月から休館します。"]
WRAPPED_TERMS = ["大ホール", "改修工事", "休館"]
HEADING = "中央図書館だより　第3号"
HIGHLIGHT_TERMS = {
    "horizontal": HORIZONTAL_TERMS,
    "horizontal-rotated-cropped": HORIZONTAL_TERMS,
    "vertical-with-heading": VERTICAL_TERMS,
}
FONT = "/opt/amane-pdf/fonts/BIZUDPGothic-Regular.ttf"
MINCHO = "/opt/amane-pdf/fonts/BIZUDPMincho-Regular.ttf"
INVOICE_ROWS = [
    ("品名", "数量", "単価", "金額"),
    ("返却ポスト", "2", "6,400", "12,800"),
    ("案内看板", "3", "4,500", "13,500"),
    ("展示棚", "4", "9,200", "36,800"),
    ("受付台", "1", "35,000", "35,000"),
    ("会議机", "5", "8,800", "44,000"),
    ("事務椅子", "6", "7,200", "43,200"),
    ("書類ケース", "8", "1,500", "12,000"),
]
TABLE_TERMS = ["品名", "数量", "単価", "金額", "返却ポスト", "案内看板", "展示棚", "12,800", "36,800"]
INVOICE_TERMS = [
    "請求書", "株式会社", "中央事務用品", "請求番号", "お支払期限", "納品先", "中央図書館", "担当者",
    "備品購入", "小計", "消費税", "合計金額", "197,300", "19,730", "217,030",
    *TABLE_TERMS, "受付台", "会議机", "事務椅子", "書類ケース", "6,400", "4,500", "9,200", "35,000",
    "44,000", "43,200",
]
DOCUMENT_LINES = [
    "中央図書館の開館時間は午前九時から午後七時までです。",
    "返却ポストは正面入口の東側にあります。",
    "休館日は毎月第三月曜日です。",
    "本人確認書類を持って受付窓口までお越しください。",
    "利用登録には住所と氏名の記入が必要です。",
    "図書資料は一度に十冊まで借りることができます。",
    "返却期限は貸出日から二週間です。",
    "予約資料の取り置き期間は一週間です。",
    "詳しくは市民課までお問い合わせください。",
]
DOCUMENT_TERMS = ["中央図書館", "開館時間", "午前九時", "午後七時", "返却ポスト", "正面入口", "休館日",
                  "本人確認書類", "受付窓口", "利用登録", "返却期限", "市民課"]


def form(parts):
    boundary = "ocr-search-" + uuid.uuid4().hex
    body = b""
    for name, filename, content_type, data in parts:
        disposition = f'form-data; name="{name}"' + (f'; filename="{filename}"' if filename else "")
        body += f"--{boundary}\r\nContent-Disposition: {disposition}\r\n".encode()
        body += (f"Content-Type: {content_type}\r\n" if content_type else "").encode() + b"\r\n" + data + b"\r\n"
    return body + f"--{boundary}--\r\n".encode(), "multipart/form-data; boundary=" + boundary


def post(api, path, parts, expected_type, timeout=40):
    body, content_type = form(parts)
    status, headers, output = api.request("POST", path, body, content_type, timeout=timeout)
    assert status == 200 and headers["Content-Type"].startswith(expected_type), (path, status, output[:300])
    return output


def scan(api, directory, name, fields):
    blank = directory / f"{name}-blank.pdf"
    source = directory / f"{name}-blank.json"
    source.write_text(json.dumps(BLANK_A4))
    subprocess.run(["qpdf", "--json-input", str(source), str(blank)], check=True, timeout=60)
    filled = post(api, "/api/pdf/fill-template", [("file", "template.pdf", "application/pdf", blank.read_bytes()),
                  ("fields", None, None, json.dumps(fields, ensure_ascii=False).encode())], "application/pdf")
    images = post(api, "/api/pdf/to-images?format=png&dpi=200",
                  [("file", "filled.pdf", "application/pdf", filled)], "application/zip")
    with zipfile.ZipFile(io.BytesIO(images)) as archive:
        png = archive.read(archive.namelist()[0])
    return post(api, "/api/pdf/from-images?quality=original", [("file", "scan.png", "image/png", png)], "application/pdf")


def rotate_with_crop(directory, source):
    """PdfP3EndpointsTests.RotateWithCropAsync と同じ /Rotate 90 + CropBox/TrimBox のページにする。"""
    paths = [directory / f"rotated-{i}.pdf" for i in range(4)]
    paths[0].write_bytes(source)
    subprocess.run(["qpdf", str(paths[0]), "--rotate=+270", str(paths[1])], check=True, timeout=60)
    subprocess.run(["qpdf", str(paths[1]), "--flatten-rotation", str(paths[2])], check=True, timeout=60)
    subprocess.run(["qpdf", str(paths[2]), "--rotate=+90", str(paths[3])], check=True, timeout=60)
    meta = json.loads(subprocess.run(["qpdf", "--json", "--json-key=pages", "--json-key=qpdf", str(paths[3])],
                                     check=True, capture_output=True, timeout=60).stdout)
    reference = "obj:" + meta["pages"][0]["object"]
    page = meta["qpdf"][1][reference]
    width, height = page["value"]["/MediaBox"][2], page["value"]["/MediaBox"][3]
    page["value"]["/CropBox"] = [10, 14, width - 6, height - 21]
    page["value"]["/TrimBox"] = [15, 18, width - 9, height - 26]
    update = directory / "rotated.json"
    update.write_text(json.dumps({"qpdf": [{"jsonversion": 2}, {reference: page}]}))
    output = directory / "rotated-cropped.pdf"
    subprocess.run(["qpdf", str(paths[3]), "--update-from-json=" + str(update), str(output)], check=True, timeout=60)
    return output.read_bytes()


def find(directory, name, pdf, terms):
    path, query = directory / f"{name}.pdf", directory / f"{name}-terms.json"
    path.write_bytes(pdf)
    query.write_text(json.dumps(terms, ensure_ascii=False))
    result = subprocess.run(["node", "find.mjs", str(path), str(query)], cwd=SEARCH, check=True,
                            capture_output=True, timeout=300)
    return json.loads(result.stdout)


def ocr(api, quality, direction, pdf):
    start = time.monotonic()
    output = post(api, f"/api/pdf/ocr?direction={direction}&quality={quality}",
                  [("file", "../../untrusted.pdf", "application/pdf", pdf)], "application/pdf", timeout=200)
    return output, time.monotonic() - start


def image_pdf(api, image, dpi, quality):
    jpeg = io.BytesIO()
    image.save(jpeg, format="JPEG", quality=quality, dpi=(dpi, dpi))
    return post(api, "/api/pdf/from-images?quality=original",
                [("file", "scan.jpg", "image/jpeg", jpeg.getvalue())], "application/pdf")


def invoice_scans(api, font_path):
    from PIL import Image, ImageDraw, ImageFont
    table = Image.new("L", (1654, 2339), 255)
    draw = ImageDraw.Draw(table)
    font = ImageFont.truetype(str(font_path), 36)
    columns = (130, 670, 840, 1130, 1524)
    top, row_height = 575, 96
    bottom = top + len(INVOICE_ROWS) * row_height
    for x in columns:
        draw.line((x, top, x, bottom), fill=0, width=3)
    for index in range(len(INVOICE_ROWS) + 1):
        y = top + index * row_height
        draw.line((columns[0], y, columns[-1], y), fill=0, width=3)
    for index, row in enumerate(INVOICE_ROWS):
        for x, text in zip(columns, row):
            draw.text((x + 25, top + index * row_height + 26), text, fill=0, font=font)

    invoice = table.copy()
    draw = ImageDraw.Draw(invoice)
    draw.text((130, 120), "請求書", fill=0, font=ImageFont.truetype(str(font_path), 62))
    for index, text in enumerate(("株式会社 中央事務用品", "請求番号 2026-11-26", "お支払期限 2026年12月末日",
                                  "納品先 中央図書館", "担当者 備品購入係")):
        draw.text((130, 225 + 56 * index), text, fill=0, font=font)
    for index, (label, amount) in enumerate((("小計", "197,300"), ("消費税", "19,730"), ("合計金額", "217,030"))):
        y = bottom + 45 + 62 * index
        draw.text((840, y), label, fill=0, font=font)
        draw.text((1155, y), amount, fill=0, font=font)
    return image_pdf(api, invoice, 200, 75), image_pdf(api, table, 200, 75)


def document_scans(api, font_path):
    from PIL import Image, ImageChops, ImageDraw, ImageFont
    image = Image.new("L", (1654, 2339), 255)
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype(str(font_path), 39)
    for index, text in enumerate(DOCUMENT_LINES):
        draw.text((165, 275 + index * 112), text, fill=0, font=font)
    # 約35%の照度差を左右に付ける。JPEGにして撮影・スキャン時の劣化を加える。
    gradient = Image.frombytes("L", (image.width, 1),
                               bytes(round(255 - 89 * x / (image.width - 1)) for x in range(image.width)))
    shadow = ImageChops.multiply(image, gradient.resize(image.size))
    return (image_pdf(api, shadow, 200, 75),
            image_pdf(api, image.transpose(Image.Transpose.ROTATE_180), 200, 75))


def dense_scan(api, font_path, direction):
    from PIL import Image, ImageDraw, ImageFont
    image = Image.new("L", (2480, 3508), 255)
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype(str(font_path), round(11 * 300 / 72))
    text = "".join(DOCUMENT_LINES)
    text = (text * (1680 // len(text) + 1))[:1680]
    if direction == "vertical":
        for index, character in enumerate(text):
            column, row = divmod(index, 60)
            draw.text((2300 - 79 * column, 195 + 52 * row), character, fill=0, font=font)
    else:
        for row in range(35):
            draw.text((135, 210 + 86 * row), text[row * 48:(row + 1) * 48], fill=0, font=font)
    return image_pdf(api, image, 300, 85)


def benchmark(api, directory, qualities):
    font = directory / "BIZUDPMincho-Regular.ttf"
    font.write_bytes(api.exec("cat", MINCHO).stdout)
    # 同じ1ページから各方式の上限ページ数のPDFを作り、1リクエストずつ送る。
    for direction in ("vertical", "horizontal"):
        page = directory / f"dense-{direction}.pdf"
        page.write_bytes(dense_scan(api, font, direction))
        for quality in qualities:
            count = 5 if quality == "high" else 10
            source = directory / f"dense-{direction}-{count}.pdf"
            subprocess.run(["qpdf", "--empty", "--pages", *([str(page), "1"] * count), "--", str(source)],
                           check=True, timeout=60)
            _, seconds = ocr(api, quality, direction, source.read_bytes())
            print(f"{quality} dense-{direction}-{count}: HTTP 200, {seconds:.2f} s", flush=True)
            api.assert_clean()


def ink(directory, name, pdf):
    """PDFの1ページ目を、CropBoxと回転を反映した300 dpiの表示座標で描き、文字の画素をTrueにして返す。"""
    import numpy as np
    from PIL import Image
    source, prefix = directory / f"{name}-ink.pdf", directory / f"{name}-ink"
    source.write_bytes(pdf)
    subprocess.run(["pdftoppm", "-cropbox", "-r", "300", "-gray", "-png", "-singlefile", str(source), str(prefix)],
                   check=True, timeout=120)
    return np.array(Image.open(f"{prefix}.png").convert("L")) < 128


def runs(mask, join=10):
    """Trueが続く区間 [start, end) を返す。join画素未満の切れ目はつなぐ。"""
    import numpy as np
    index = np.flatnonzero(mask)
    result = []
    for value in index:
        if result and value - result[-1][1] < join:
            result[-1][1] = int(value) + 1
        else:
            result.append([int(value), int(value) + 1])
    return result


def line_boxes(mask, top, bottom, text, terms, font):
    """横書きの1行: 文字の画素の左右端を元画像から求め、語の位置を字形の送り幅で按分する。"""
    columns = runs(mask[top:bottom].any(axis=0))
    left, right = columns[0][0], columns[-1][1]
    glyphs = font.getbbox(text)
    scale = (right - left) / (glyphs[2] - glyphs[0])
    boxes = {}
    for term in terms:
        start = text.find(term)
        if start >= 0:
            x0, x1 = font.getlength(text[:start]), font.getlength(text[:start + len(term)])
            boxes[term] = [left + (x0 - glyphs[0]) * scale, top, left + (x1 - glyphs[0]) * scale, bottom]
    return boxes


def expected_boxes(directory, name, pdf, font_path):
    from PIL import ImageFont
    font = ImageFont.truetype(str(font_path), 100, layout_engine=ImageFont.Layout.BASIC)
    mask = ink(directory, name, pdf)
    terms = HIGHLIGHT_TERMS[name]
    boxes = {}
    rows = runs(mask.any(axis=1))
    if name != "vertical-with-heading":
        assert len(rows) == len(LINES), (name, rows)
        for (top, bottom), text in zip(rows, LINES):
            boxes |= line_boxes(mask, top, bottom, text, terms, font)
        return {term: boxes[term] for term in terms}
    # 先頭の行が横書きの見出し。その下の本文は右から左へ並ぶ縦書きの列。
    top, bottom = rows[0]
    boxes |= line_boxes(mask, top, bottom, HEADING, terms, font)
    body = bottom + 20
    columns = sorted(runs(mask[body:].any(axis=0)), reverse=True)
    assert len(columns) == len(COLUMNS), columns
    for (left, right), text in zip(columns, COLUMNS):
        # 字の上下端: 字の中の切れ目（「三」など）はつなぎ、字数と同じ数に分かれる最小のつなぎ幅を使う。
        cells = next((cells for join in range(4, 60, 2)
                      if len(cells := runs(mask[body:, left:right].any(axis=1), join)) == len(text)), None)
        assert cells is not None, text
        for term in terms:
            start = text.find(term)
            if start >= 0:
                boxes[term] = [left, body + cells[start][0], right, body + cells[start + len(term) - 1][1]]
    return {term: boxes[term] for term in terms}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--quality", choices=("both", "standard", "high"), default="both")
    parser.add_argument("--benchmark", action="store_true", help="上限ページ数の密な文書で応答時間を測る")
    parser.add_argument("--artifacts", type=Path, help="OCRの入力と出力のPDFを保存する")
    parser.add_argument("--highlight", type=Path, help="強調表示の実測用に、OCR出力PDFと元画像から求めた期待矩形を保存する")
    args = parser.parse_args()
    qualities = ("standard", "high") if args.quality == "both" else (args.quality,)
    assert (SEARCH / "node_modules/pdfjs-dist").is_dir(), "Run npm ci in scripts/ocr-search first."
    with tempfile.TemporaryDirectory(prefix="amane-ocr-search-") as temporary, smoke.running_container(args.image) as api:
        directory = Path(temporary)
        horizontal = scan(api, directory, "horizontal", [
            {"page": 1, "x": 0.06, "y": round(0.08 + 0.05 * i, 2), "width": 0.9, "height": 0.04, "fontSize": 18,
             "align": "left", "text": text} for i, text in enumerate(LINES)])
        vertical = scan(api, directory, "vertical", [
            {"page": 1, "x": round(0.80 - 0.07 * c, 2), "y": round(0.12 + 0.03 * i, 2), "width": 0.05, "height": 0.03,
             "fontSize": 18, "align": "center", "text": character}
            for c, column in enumerate(COLUMNS) for i, character in enumerate(column)] + [
            {"page": 1, "x": 0.08, "y": 0.04, "width": 0.6, "height": 0.04, "fontSize": 20, "align": "left",
             "text": HEADING}])
        separate = scan(api, directory, "separate", [
            {"page": 1, "x": x, "y": y, "width": 0.42, "height": 0.05, "fontSize": 22, "align": "left", "text": text}
            for x, y, text in SEPARATE_FIELDS])
        wrapped = scan(api, directory, "wrapped", [
            {"page": 1, "x": 0.08, "y": round(0.10 + 0.035 * i, 3), "width": 0.84, "height": 0.03, "fontSize": 20,
             "align": "left", "text": text} for i, text in enumerate(WRAPPED_LINES)])
        font_path = directory / "BIZUDPGothic-Regular.ttf"
        font_path.write_bytes(api.exec("cat", FONT).stdout)
        invoice, table = invoice_scans(api, font_path)
        shadow, upside_down = document_scans(api, font_path)
        cases = [("horizontal", "horizontal", horizontal, HORIZONTAL_TERMS, ["Windows11"]),
                 ("separate-fields", "horizontal", separate, SEPARATE_TERMS, SEPARATE_ABSENT),
                 ("wrapped-paragraph", "horizontal", wrapped, WRAPPED_TERMS, []),
                 ("horizontal-rotated-cropped", "horizontal", rotate_with_crop(directory, horizontal),
                  HORIZONTAL_TERMS, ["Windows11"]),
                 ("vertical-with-heading", "vertical", vertical, VERTICAL_TERMS, []),
                 ("ruled-invoice", "horizontal", invoice, INVOICE_TERMS, []),
                 ("ruled-table", "horizontal", table, TABLE_TERMS, []),
                 ("shadow", "horizontal", shadow, DOCUMENT_TERMS, []),
                 ("upside-down", "horizontal", upside_down, DOCUMENT_TERMS, [])]
        failures = []
        for quality in qualities:
            artifacts = args.artifacts
            if artifacts and len(qualities) > 1:
                artifacts /= quality
            for name, direction, source, terms, absent in cases:
                output, seconds = ocr(api, quality, direction, source)
                found = find(directory, name, output, terms + absent)
                if artifacts:
                    artifacts.mkdir(parents=True, exist_ok=True)
                    (artifacts / f"{name}-scan.pdf").write_bytes(source)
                    (artifacts / f"{name}-ocr.pdf").write_bytes(output)
                if args.highlight and name in HIGHLIGHT_TERMS and (quality == "high" or len(qualities) == 1):
                    args.highlight.mkdir(parents=True, exist_ok=True)
                    (args.highlight / f"{name}-ocr.pdf").write_bytes(output)
                    boxes = expected_boxes(directory, name, source, font_path)
                    (args.highlight / f"{name}-expected.json").write_text(json.dumps(
                        {term: [round(v, 1) for v in box] for term, box in boxes.items()}, ensure_ascii=False, indent=1))
                required = terms
                minimum = len(terms)
                if name == "vertical-with-heading" and quality == "standard":
                    required = terms[2:]
                    minimum = len(required)
                elif name == "ruled-invoice":
                    required = ["品名", "金額", "合計金額", "返却ポスト", "12,800"]
                    minimum = 30
                elif name == "ruled-table":
                    required = ["品名", "金額", "返却ポスト"]
                    minimum = 8
                elif name == "shadow":
                    required = ["開館時間", "返却ポスト", "本人確認書類"]
                    minimum = 10
                hits = sum(found[term] > 0 for term in terms)
                joined = [term for term in absent if found[term] > 0]
                print(f"{quality} {name}: {hits}/{len(terms)} terms found by PDF.js, "
                      f"{len(absent) - len(joined)}/{len(absent)} unwanted joins absent, {seconds:.2f} s", flush=True)
                if hits < minimum or any(found[term] == 0 for term in required) or joined:
                    failures.append((quality, name, found))
                api.assert_clean()
        assert not failures, failures
        if args.benchmark:
            benchmark(api, directory, qualities)
        logs = smoke.docker("logs", api.name)
        assert all(term.encode() not in logs.stdout + logs.stderr for term in ("中央図書館", "返却ポスト"))
        events = dict(line.split() for line in api.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())
        assert events["oom"] == events["oom_kill"] == "0", events
        print("OCR search (PDF.js 6.3.289): PASS")


if __name__ == "__main__":
    main()
