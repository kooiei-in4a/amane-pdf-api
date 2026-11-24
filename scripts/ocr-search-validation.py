#!/usr/bin/env python3
"""OCR済みPDFをPDF.jsの検索で確かめる（Issue #116、CI専用ツール）。

fill-templateで描いた日本語の文書を200 dpiの画像だけのPDFにし、実コンテナの /api/pdf/ocr に送る。
出力をPDF.js 6.3.289のPDFFindController（ビューアと同じ検索処理）で検索し、語句が空白で分断されずに
見つかること、欧文の単語間の空白が保たれること、縦書きのページの横書きの見出しが読めることを確かめる。
事前に scripts/ocr-search で `npm ci --ignore-scripts --omit=optional --omit=dev` を実行しておく。
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
VERTICAL_TERMS = ["中央図書館だより", "開館時間", "返却ポスト", "本人確認書類", "市民課"]


def form(parts):
    boundary = "ocr-search-" + uuid.uuid4().hex
    body = b""
    for name, filename, content_type, data in parts:
        disposition = f'form-data; name="{name}"' + (f'; filename="{filename}"' if filename else "")
        body += f"--{boundary}\r\nContent-Disposition: {disposition}\r\n".encode()
        body += (f"Content-Type: {content_type}\r\n" if content_type else "").encode() + b"\r\n" + data + b"\r\n"
    return body + f"--{boundary}--\r\n".encode(), "multipart/form-data; boundary=" + boundary


def post(api, path, parts, expected_type):
    body, content_type = form(parts)
    status, headers, output = api.request("POST", path, body, content_type)
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


def ocr(api, direction, pdf):
    start = time.monotonic()
    output = post(api, f"/api/pdf/ocr?direction={direction}", [("file", "scan.pdf", "application/pdf", pdf)],
                  "application/pdf")
    return output, time.monotonic() - start


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--artifacts", type=Path, help="OCRの入力と出力のPDFを保存する")
    args = parser.parse_args()
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
             "text": "中央図書館だより　第3号"}])
        cases = [("horizontal", "horizontal", horizontal, HORIZONTAL_TERMS, ["Windows11"]),
                 ("horizontal-rotated-cropped", "horizontal", rotate_with_crop(directory, horizontal),
                  HORIZONTAL_TERMS, ["Windows11"]),
                 ("vertical-with-heading", "vertical", vertical, VERTICAL_TERMS, [])]
        for name, direction, source, terms, absent in cases:
            output, seconds = ocr(api, direction, source)
            found = find(directory, name, output, terms + absent)
            missing = [term for term in terms if found[term] == 0]
            joined = [term for term in absent if found[term] > 0]
            assert not missing and not joined, (name, found)
            print(f"{name}: {len(terms)}/{len(terms)} terms found by PDF.js, {seconds:.2f} s")
            if args.artifacts:
                args.artifacts.mkdir(parents=True, exist_ok=True)
                (args.artifacts / f"{name}-scan.pdf").write_bytes(source)
                (args.artifacts / f"{name}-ocr.pdf").write_bytes(output)
            api.assert_clean()
        events = dict(line.split() for line in api.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())
        assert events["oom"] == events["oom_kill"] == "0", events
        print("OCR search (PDF.js 6.3.289): PASS")


if __name__ == "__main__":
    main()
