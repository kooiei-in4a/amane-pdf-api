#!/usr/bin/env python3
"""ページ番号APIの実HTTP・本番フォント・表示上の位置をCI用Popplerで検証する。"""
import base64
import importlib.util
import itertools
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("smoke", ROOT / "scripts/docker-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, timeout=45)


def fixture(directory):
    # 合成白紙と表紙。元PDFの属性に基づく独立した表示寸法で位置を比較する。
    cases = [("cover", (0, 0, 595.28, 841.89), None, 0, 1)]
    cases += [("rotate-" + str(rotation), (0, 0, 595.28, 841.89), None, rotation, 1)
              for rotation in (0, 90, 180, 270)]
    cases += [("crop-offset", (-20, -30, 630, 850), (100, 200, 500, 700), 0, 1),
              ("crop-270", (-20, -30, 630, 850), (100, 200, 500, 700), 270, 1),
              ("unit-2", (0, 0, 595, 842), (30, 40, 330, 490), 0, 2),
              ("unit-half-90", (0, 0, 595, 842), (30, 40, 530, 740), 90, 0.5),
              ("A3", (0, 0, 841.89, 1190.55), None, 0, 1),
              ("B5", (0, 0, 515.91, 728.50), None, 0, 1),
              ("postcard", (0, 0, 283.46, 419.53), None, 0, 1)]
    objects = {"obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
               "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": len(cases),
                                      "/Kids": [f"{i+3} 0 R" for i in range(len(cases))]}}}
    for i, (_, media, crop, rotation, unit) in enumerate(cases):
        page = {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": media,
                "/Rotate": rotation, "/UserUnit": unit, "/Resources": {}}
        if crop:
            page["/CropBox"] = crop
        objects[f"obj:{i+3} 0 R"] = {"value": page}
    # A cover marker verifies that the non-target page survives the real HTTP path.
    font, content = len(cases) + 3, len(cases) + 4
    objects["obj:3 0 R"]["value"].update({"/Contents": f"{content} 0 R", "/Resources": {"/Font": {"/F1": f"{font} 0 R"}}})
    objects[f"obj:{font} 0 R"] = {"value": {"/Type": "/Font", "/Subtype": "/Type1", "/BaseFont": "/Helvetica"}}
    objects[f"obj:{content} 0 R"] = {"stream": {"dict": {}, "data": base64.b64encode(b"BT /F1 12 Tf 40 400 Td (COVER) Tj ET").decode()}}
    objects["trailer"] = {"value": {"/Root": "1 0 R", "/Size": content + 1}}
    source = directory / "source.json"
    source.write_text(json.dumps({"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objects]}))
    path = directory / "source.pdf"
    command("qpdf", "--json-input", str(source), str(path))
    return path.read_bytes(), cases


def expected_text(format, number, total):
    if format == "number":
        return str(number)
    if format == "number-total":
        return f"{number} / {total}"
    return f"- {number} -"


def verify(path, cases, position, format, size):
    command("qpdf", "--check", str(path))
    assert command("qpdf", "--show-npages", str(path)).stdout.strip() == str(len(cases)).encode()
    fonts = command("pdffonts", str(path)).stdout.decode()
    assert re.search(r"\+BIZUDPGothic-Regular\s+CID TrueType\s+Identity-H\s+yes\s+yes\s+yes", fonts), fonts
    # Poppler's bbox ignores UserUnit but reflects Rotate and the explicit CropBox.
    # Assert this premise, then convert bbox coordinates back to physical points.
    document = ET.fromstring(command("pdftotext", "-cropbox", "-bbox", str(path), "-").stdout)
    pages = document.findall(".//{*}page")
    assert len(pages) == len(cases)
    font_size = {"small": 9, "medium": 10.5, "large": 12}[size]
    for i, (page, (name, media, crop, rotation, unit)) in enumerate(zip(pages, cases)):
        words = page.findall(".//{*}word")
        text = " ".join(word.text or "" for word in words)
        if i == 0:
            assert text == "COVER", text
            continue
        expected = expected_text(format, 10 + i - 1, 10 + len(cases) - 2)
        assert text == expected, (name, position, format, size, expected, text)
        box = crop or media
        raw_width, raw_height = box[2] - box[0], box[3] - box[1]
        assert abs(float(page.attrib["width"]) - raw_width) < 0.01, "Poppler UserUnit/bbox width premise changed"
        assert abs(float(page.attrib["height"]) - raw_height) < 0.01, "Poppler UserUnit/bbox height premise changed"
        width, height = raw_width * unit, raw_height * unit
        if rotation in (90, 270):
            width, height = height, width
        xmin = min(float(word.attrib["xMin"]) for word in words) * unit
        xmax = max(float(word.attrib["xMax"]) for word in words) * unit
        ymin = min(float(word.attrib["yMin"]) for word in words) * unit
        ymax = max(float(word.attrib["yMax"]) for word in words) * unit
        assert 0 <= xmin < xmax <= width and 0 <= ymin < ymax <= height, (name, xmin, xmax, ymin, ymax)
        assert abs((ymax - ymin) - font_size) < 1, ("Horizontal orientation and requested font size", name, ymin, ymax)
        if position == "bottom-center":
            assert abs((xmin + xmax) / 2 - width / 2) <= 1, (name, position, xmin, xmax, width)
        elif position == "bottom-left":
            assert abs(xmin - 28) <= 1, (name, position, xmin)
        else:
            assert abs(width - xmax - 28) <= 1, (name, position, width, xmax)
        # Anchor offsets use the baseline; the glyph bbox bottom differs by a few points.
        margin = ymin if position == "top-right" else height - ymax
        assert abs(margin - 28) <= 6, (name, position, margin)


def main(image):
    with tempfile.TemporaryDirectory(prefix="amane-page-numbers-") as temporary:
        directory = Path(temporary)
        source, cases = fixture(directory)
        checks = 0
        with smoke.running_container(image) as api:
            for position, format, size in itertools.product(
                    ("bottom-center", "bottom-right", "bottom-left", "top-right"),
                    ("number", "number-total", "dash"), ("small", "medium", "large")):
                body, content_type = smoke.multipart(source, password=None)
                query = f"position={position}&format={format}&size={size}&from=2&start=10"
                status, headers, output = api.request("POST", "/api/pdf/page-numbers?" + query, body, content_type)
                assert status == 200, (query, status, output[:300])
                assert headers["Content-Type"] == "application/pdf"
                assert "filename=numbered.pdf" in headers["Content-Disposition"]
                path = directory / "numbered.pdf"
                path.write_bytes(output)
                verify(path, cases, position, format, size)
                api.assert_clean()
                checks += len(cases) - 1
            assert api.request("GET", "/healthz")[0] == 200
            events = dict(line.split() for line in api.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())
            assert events["oom"] == events["oom_kill"] == "0", events
        print(f"Page numbers: 36 real HTTP requests / {checks} numbered-page checks, formats/positions/sizes/Rotate/CropBox/UserUnit/A3/B5/postcard/cover/font/cleanup/health: PASS")


if __name__ == "__main__":
    main(sys.argv[1])
