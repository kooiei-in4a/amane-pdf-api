#!/usr/bin/env python3
"""透かしの実HTTP・描画・透過・抽出と固定字形表をCI専用ツールで検証する。"""
import argparse
import base64
import importlib.util
import itertools
import json
import math
from pathlib import Path
import re
import subprocess
import tempfile
import time

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("smoke", ROOT / "scripts/docker-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
RANGES = [(0x20, 0x7E), (0x3000, 0x30FF), (0x4E00, 0x9FFF), (0xFF01, 0xFF5E), (0xFF61, 0xFF9F)]


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, timeout=45)


def blank_json(cases):
    objects = {"obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
               "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": len(cases),
                                      "/Kids": [f"{i+3} 0 R" for i in range(len(cases))]}}}
    for i, (_, media, crop, rotation, unit) in enumerate(cases):
        page = {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": media,
                "/Rotate": rotation, "/UserUnit": unit, "/Resources": {}}
        if crop:
            page["/CropBox"] = crop
        objects[f"obj:{i+3} 0 R"] = {"value": page}
    objects["trailer"] = {"value": {"/Root": "1 0 R", "/Size": len(cases)+3}}
    return {"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objects]}


def fixture(directory):
    cases = [("black-image", (0, 0, 595.28, 841.89), None, 0, 1),
             ("A4-landscape", (0, 0, 841.89, 595.28), None, 0, 1)]
    cases += [(f"rotate-{rotation}", (0, 0, 595.28, 841.89), None, rotation, 1) for rotation in (90, 180, 270)]
    cases += [("crop-offset", (-20, -30, 630, 850), (100, 200, 500, 700), 0, 1),
              ("crop-270", (-20, -30, 630, 850), (100, 200, 500, 700), 270, 1),
              ("unit-2", (0, 0, 595, 842), (30, 40, 330, 490), 0, 2),
              ("B5", (0, 0, 515.91, 728.50), None, 0, 1),
              ("postcard", (0, 0, 283.46, 419.53), None, 0, 1),
              ("small", (0, 0, 100, 100), None, 0, 1),
              ("tall", (0, 0, 200, 2000), None, 0, 1)]
    data = blank_json(cases)
    objects = data["qpdf"][1]
    image, content = len(cases)+3, len(cases)+4
    objects["obj:3 0 R"]["value"].update({"/Resources": {"/XObject": {"/Black": f"{image} 0 R"}},
                                          "/Contents": f"{content} 0 R"})
    objects[f"obj:{image} 0 R"] = {"stream": {"dict": {"/Type": "/XObject", "/Subtype": "/Image",
        "/Width": 1, "/Height": 1, "/BitsPerComponent": 8, "/ColorSpace": "/DeviceRGB"}, "data": "AAAA"}}
    objects[f"obj:{content} 0 R"] = {"stream": {"dict": {}, "data": base64.b64encode(
        b"q 595.28 0 0 841.89 0 0 cm /Black Do Q\n").decode()}}
    objects["trailer"]["value"]["/Size"] = content+1
    source = directory / "source.json"
    source.write_text(json.dumps(data))
    path = directory / "source.pdf"
    command("qpdf", "--json-input", str(source), str(path))
    return path, cases


def pixels(path, directory, prefix):
    command("pdftoppm", "-cropbox", "-r", "144", "-png", str(path), str(directory / prefix))
    paths = sorted(directory.glob(prefix + "-*.png"), key=lambda p: int(p.stem.rsplit("-", 1)[1]))
    return [np.array(Image.open(p).convert("RGB")) for p in paths]


def table():
    source = (ROOT / "src/Amane.Pdf.Api/WatermarkGlyphs.g.cs").read_text()
    encoded = "".join(re.findall(r'"([A-Za-z0-9+/=]+)"', source.split("Convert.FromBase64String(", 1)[1].split(");", 1)[0]))
    bits = base64.b64decode(encoded)
    advances = list(map(int, re.findall(r"\d+", source.split("AsciiAdvances =>", 1)[1].split("];", 1)[0])))
    scalars = [c for start, end in RANGES for c in range(start, end+1)]
    drawable = {c for i, c in enumerate(scalars) if bits[i // 8] & (1 << (i % 8))}
    return scalars, drawable, advances


def em_width(text, advances):
    return sum(advances[ord(c)-0x20]/2048 if 0x20 <= ord(c) <= 0x7E else
               0.5 if 0xFF61 <= ord(c) <= 0xFF9F else 1 for c in text)


def stream(objects, reference):
    return base64.b64decode(objects["obj:" + reference]["stream"]["data"]).decode("latin1")


def literal_bytes(value):
    # Decode PDF literal-string escapes before reading Identity-H glyph IDs.
    output = bytearray()
    i = 0
    while i < len(value):
        c = value[i]
        i += 1
        if c != "\\":
            output.append(ord(c))
            continue
        c = value[i]
        i += 1
        if c in "01234567":
            digits = c
            while i < len(value) and len(digits) < 3 and value[i] in "01234567":
                digits += value[i]
                i += 1
            output.append(int(digits, 8) % 256)
        elif c in "\r\n":
            if c == "\r" and i < len(value) and value[i] == "\n":
                i += 1
        else:
            output.append(ord({"n": "\n", "r": "\r", "t": "\t", "b": "\b", "f": "\f"}.get(c, c)))
    return output


def text_frame_center(objects, form, content):
    font_name, font_size = re.search(r"(/\S+) ([\d.]+) Tf", content).groups()
    size = float(font_size)
    font = objects["obj:"+form["dict"]["/Resources"]["/Font"][font_name]]["value"]
    cid = objects["obj:"+font["/DescendantFonts"][0]]["value"]
    widths = {}
    data = cid.get("/W", [])
    if isinstance(data, str):
        data = objects["obj:"+data]["value"]
    i = 0
    while i < len(data):
        first = data[i]
        i += 1
        if isinstance(data[i], list):
            widths.update({first+j: width for j, width in enumerate(data[i])})
            i += 1
        else:
            last, width = data[i:i+2]
            widths.update({glyph: width for glyph in range(first, last+1)})
            i += 2
    value = re.search(r"\(((?:\\[\s\S]|[^\\)])*)\)\s+Tj", content)[1]
    glyphs = literal_bytes(value)
    assert len(glyphs) % 2 == 0
    width = sum(widths.get(int.from_bytes(glyphs[j:j+2], "big"), cid.get("/DW", 1000)) for j in range(0, len(glyphs), 2))*size/1000
    matrix = list(map(float, re.search(r"([-+\d.eE]+) ([-+\d.eE]+) ([-+\d.eE]+) ([-+\d.eE]+) ([-+\d.eE]+) ([-+\d.eE]+) cm", content).groups()))
    a, b, c, d, x, y = matrix
    # Locked TTF bbox (-246..1802)/2048 is exactly 1 em high.
    return x+a*width/2+c*size/2, y+b*width/2+d*size/2


def verify_structure(path, count, diagonal):
    command("qpdf", "--check", str(path))
    metadata = json.loads(command("qpdf", "--json=2", "--json-key=pages", "--json-key=qpdf",
                                  "--json-stream-data=inline", "--decode-level=all", str(path)).stdout)
    assert len(metadata["pages"]) == count
    objects = metadata["qpdf"][1]
    centers = []
    for i, page in enumerate(metadata["pages"]):
        content = "".join(stream(objects, reference) for reference in page["contents"])
        draws = re.findall(r"(/\S+)\s+Do", content)
        assert len(draws) == 2, (i, content)
        resources = objects["obj:"+page["object"]]["value"]["/Resources"]["/XObject"]
        form = objects["obj:"+resources[draws[-1]]]["stream"]
        text = base64.b64decode(form["data"]).decode("latin1")
        assert len(re.findall(r"\bTj\b", text)) == 1
        if diagonal:
            assert text.lstrip().startswith("/AmaneAlpha gs")
            alpha = form["dict"]["/Resources"]["/ExtGState"]["/AmaneAlpha"]
            assert alpha["/ca"] == alpha["/CA"] == 0.25
            centers.append(text_frame_center(objects, form, text))
        else:
            assert "/ExtGState" not in form["dict"]["/Resources"]
        if i == 0:
            original = objects["obj:"+resources[draws[0]]]["stream"]["dict"]
            image_refs = original["/Resources"]["/XObject"].values()
            assert any(objects["obj:"+ref]["stream"]["dict"]["/Subtype"] == "/Image" for ref in image_refs)
    return centers


def verify(path, cases, before, after, text, style, color, advances):
    centers = verify_structure(path, len(cases), style == "diagonal")
    fonts = command("pdffonts", str(path)).stdout.decode()
    assert re.search(r"\+BIZUDPGothic-Regular\s+CID TrueType\s+Identity-H\s+yes\s+yes\s+yes", fonts), fonts
    extracted = command("pdftotext", "-raw", "-cropbox", str(path), "-").stdout.decode().split("\f")
    assert len(extracted) >= len(cases)
    for page in extracted[:len(cases)]:
        assert "".join(text.split()) in "".join(page.split()), (text, page)
    em = em_width(text, advances)
    assert len(before) == len(after) == len(cases)
    for i, (original, final, (name, media, crop, rotation, unit)) in enumerate(zip(before, after, cases)):
        assert original.shape == final.shape
        difference = np.max(np.abs(original.astype(int)-final.astype(int)), axis=2)
        mask = difference > 6
        # qpdf/Poppler may change the antialiasing of a fractional page-edge column.
        mask[:, -2:] = False
        ys, xs = np.nonzero(mask)
        assert len(xs) > 5, (name, text, style, "visible watermark required")
        box = crop or media
        width, height = (box[2]-box[0])*unit, (box[3]-box[1])*unit
        if rotation in (90, 270):
            width, height = height, width
        pixel_unit = unit/2  # Poppler ignores UserUnit; 144 dpi gives two pixels per raw pt.
        xmin, xmax = xs.min()*pixel_unit, (xs.max()+1)*pixel_unit
        ymin, ymax = ys.min()*pixel_unit, (ys.max()+1)*pixel_unit
        assert xmin >= 0 and ymin >= 0 and xmax <= width+unit and ymax <= height+unit
        if style == "diagonal":
            angle = math.atan2(height, width)
            size = max(1, math.floor(min(0.8*width/(em*math.cos(angle)+math.sin(angle)),
                       0.8*height/(em*math.sin(angle)+math.cos(angle)), 0.95*width/em, 0.95*height)*100)/100)
            cx, cy = centers[i]
            assert abs(cx-width/2) <= max(1, 0.01*size), (name, text, "frame center x", cx, width)
            assert abs(cy-height/2) <= max(1, 0.01*size), (name, text, "frame center y", cy, height)
            # Ink bounds are asymmetric around the text frame for punctuation
            # and mixed scripts; check their shape separately from the anchor.
            tolerance = max(3, 0.25*size)
            assert abs((xmin+xmax)/2-width/2) <= tolerance, (name, text, "center x", xmin, xmax, width, size)
            assert abs((ymin+ymax)/2-height/2) <= tolerance, (name, text, "center y", ymin, ymax, height, size)
            # PCA on ink pixels independently measures the rendered text's axis.
            covariance = np.cov(np.array([xs, -ys]))
            vector = np.linalg.eigh(covariance)[1][:, -1]
            measured = math.degrees(math.atan2(vector[1], vector[0])) % 180
            expected = math.degrees(angle)
            error = abs((measured-expected+90) % 180-90)
            assert error <= 4, (name, text, measured, expected)
        else:
            size = max(1, min(14, math.floor((width-56)/em*100)/100))
            assert abs(width-xmax-28) <= max(1, unit), (name, text, "right margin", width-xmax)
            assert abs(ymin-28) <= max(2, unit), (name, text, "top margin", ymin)
        rgb = np.array((128, 128, 128) if color == "gray" else (192, 0, 0))
        background = np.array((0, 0, 0) if name == "black-image" else (255, 255, 255))
        alpha = 0.25 if style == "diagonal" else 1
        expected_color = np.rint(background*(1-alpha)+rgb*alpha)
        # Small glyphs may contain only antialiased pixels. Measure the exact
        # color on sufficiently large ink; every request has both A4 backgrounds.
        if size/unit >= 12:
            matching = np.max(np.abs(final[mask].astype(int)-expected_color), axis=1) <= 3
            assert matching.sum() >= 3, (name, text, color, style, expected_color, matching.sum())


def upload(api, root, name, data):
    smoke.docker("exec", "-i", api.name, "sh", "-c", f"umask 077; cat > {root}/{name}", data=data)


def verify_glyphs(api, directory, scalars, drawable, advances):
    # The production binary draws every allowed scalar independently. Empty Tj
    # identifies missing glyphs; anchor translation measures actual text width.
    root = "/tmp/watermark-glyph-validation"
    api.exec("mkdir", "-m", "700", root)
    try:
        scalars = [c for c in scalars if c not in (0x25, 0x5C)]
        assert len(scalars) == 21498 and len(drawable-{0x25, 0x5C}) == 10051
        batches = [scalars[i:i+256] for i in range(0, len(scalars), 256)]
        cases = [("glyph", (0, 0, 10000, 10000), None, 0, 1)] * len(batches)
        upload(api, root, "blank.json", json.dumps(blank_json(cases)).encode())
        api.exec("qpdf", "--json-input", root+"/blank.json", root+"/blank.pdf")
        pages = {str(i+1): {"crop": "[0 0 10000 10000]", "content": {"text": [{"value": chr(c), "anchor": "c", "font":
                 {"name": "BIZUDPGothic-Regular", "size": 1000}} for c in batch]}} for i, batch in enumerate(batches)}
        upload(api, root, "layer.json", json.dumps({"origin": "LowerLeft", "pages": pages}, ensure_ascii=False).encode())
        api.exec("/opt/amane-pdf/bin/pdfcpu", "-c", "/opt/amane-pdf/config", "--offline", "create",
                 root+"/layer.json", root+"/blank.pdf", root+"/layer.pdf")
        metadata = json.loads(api.exec("qpdf", "--json=2", "--json-key=qpdf", "--json-key=pages",
                             "--json-stream-data=inline", "--decode-level=all", root+"/layer.pdf").stdout)
        objects = metadata["qpdf"][1]
        drawn = set()
        for page, batch in zip(metadata["pages"], batches):
            content = "".join(stream(objects, reference) for reference in page["contents"])
            runs = re.findall(r"q\s+(?:BT.*?ET\s+)?([\d.\-]+) ([\d.\-]+) ([\d.\-]+) ([\d.\-]+) ([\d.\-]+) ([\d.\-]+) cm\s+BT(.*?)ET\s+Q", content, re.S)
            assert len(runs) == len(batch), (len(runs), len(batch), content[:500])
            for c, run in zip(batch, runs):
                value = re.search(r"\((.*?)\) Tj", run[-1], re.S)
                assert value is not None
                if not value[1]:
                    continue
                drawn.add(c)
                actual = (10000-2*float(run[4]))/1000
                estimated = em_width(chr(c), advances)
                assert actual <= estimated+0.00002, (hex(c), actual, estimated)
                if 0x20 <= c <= 0x7E or 0xFF61 <= c <= 0xFF9F:
                    assert abs(actual-estimated) <= 0.0011, (hex(c), actual, estimated)
        assert drawn == drawable-{0x25, 0x5C}, (len(drawn), len(drawable), list(drawn ^ (drawable-{0x25, 0x5C}))[:20])
    finally:
        api.exec("rm", "-rf", root)
    print("Glyphs: 21,498 scalars / 10,051 drawn / no underestimated widths: PASS")


def capacity(api, directory):
    pages = [("capacity", (0, 0, 14400, 14400), None, 0, 1)]*1000
    source = directory / "capacity.json"
    source.write_text(json.dumps(blank_json(pages)))
    path = directory / "capacity.pdf"
    command("qpdf", "--json-input", str(source), str(path))
    # 60 runes at the exact 120-byte bound, plus the maximum diagonal size (one glyph).
    measurements = []
    for text in ("A\u3099"*30, "秘"):
        body, content_type = smoke.multipart(path.read_bytes(), password=None, text=text)
        started = time.monotonic()
        status, _, output = api.request("POST", "/api/pdf/watermark?style=diagonal&color=gray", body, content_type)
        seconds = time.monotonic()-started
        assert status == 200, (status, output[:300])
        path_out = directory / "capacity-output.pdf"
        path_out.write_bytes(output)
        assert command("qpdf", "--show-npages", str(path_out)).stdout.strip() == b"1000"
        command("qpdf", "--check", str(path_out))
        api.assert_clean()
        measurements.append({"runes": len(text), "seconds": round(seconds, 3), "output_bytes": len(output),
                             "layer_bytes": int(api.exec("cat", "/tmp/watermark-layer-bytes").stdout)})
    pages.append(pages[0])
    source.write_text(json.dumps(blank_json(pages)))
    command("qpdf", "--json-input", str(source), str(path))
    body, content_type = smoke.multipart(path.read_bytes(), password=None, text="社外秘")
    status, _, problem = api.request("POST", "/api/pdf/watermark?style=diagonal&color=gray", body, content_type)
    assert status == 422 and json.loads(problem)["reason"] == "too-complex"
    api.assert_clean()
    print("Capacity: " + json.dumps(measurements) + "; 1,001 pages rejected: PASS")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--artifacts", type=Path, help="代表出力のPDF・PNGを保存（合成fixtureのみ）")
    args = parser.parse_args()
    scalars, drawable, advances = table()
    with tempfile.TemporaryDirectory(prefix="amane-watermark-") as temporary:
        directory = Path(temporary)
        source, cases = fixture(directory)
        before = pixels(source, directory, "original")
        with smoke.running_container(args.image) as api:
            for text, style, color in itertools.product(("社外秘", "取扱注意", "見本", "DRAFT", "-DRAFT, 社外秘", "W"*30),
                                                        ("diagonal", "corner"), ("gray", "red")):
                body, content_type = smoke.multipart(source.read_bytes(), password=None, text=text)
                status, headers, output = api.request("POST", f"/api/pdf/watermark?style={style}&color={color}", body, content_type)
                assert status == 200 and headers["Content-Type"] == "application/pdf", (status, output[:300])
                assert "filename=watermarked.pdf" in headers["Content-Disposition"]
                path = directory / "watermarked.pdf"
                path.write_bytes(output)
                after = pixels(path, directory, "watermarked")
                verify(path, cases, before, after, text, style, color, advances)
                if args.artifacts and text == "社外秘":
                    args.artifacts.mkdir(parents=True, exist_ok=True)
                    (args.artifacts / f"{style}-{color}.pdf").write_bytes(output)
                    for page, name in ((1, "black"), (2, "landscape"), (4, "portrait")):
                        Image.fromarray(after[page-1]).save(args.artifacts / f"{style}-{color}-{name}.png")
                api.assert_clean()
            verify_glyphs(api, directory, scalars, drawable, advances)
            assert api.request("GET", "/healthz")[0] == 200
            events = dict(line.split() for line in api.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())
            assert events["oom"] == events["oom_kill"] == "0", events
            print("Memory peak (drawing/glyphs): " + api.exec("cat", "/sys/fs/cgroup/memory.peak").stdout.decode().strip())
        # Instrument only capacity measurements. Placement and glyph checks
        # above use the production executable directly, without a wrapper.
        wrapper = directory / "measure-pdfcpu.sh"
        wrapper.write_text("""#!/bin/sh
umask 077
/opt/amane-pdf/bin/pdfcpu "$@"
result=$?
if [ "$result" = 0 ]; then
  for argument do last=$argument; done
  case "$last" in */overlay-layer.pdf) stat -c %s "$last" > /tmp/watermark-layer-bytes;; esac
fi
exit "$result"
""")
        wrapper.chmod(0o755)
        with smoke.running_container(args.image, settings=("Pdf__PdfcpuPath=/watermark-measure.sh",),
                                     mounts=((wrapper, "/watermark-measure.sh"),)) as api:
            capacity(api, directory)
            assert api.request("GET", "/healthz")[0] == 200
            events = dict(line.split() for line in api.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())
            assert events["oom"] == events["oom_kill"] == "0", events
            print("Memory peak (capacity): " + api.exec("cat", "/sys/fs/cgroup/memory.peak").stdout.decode().strip())
        print("Watermark: 24 HTTP requests / 288 pages; geometry/opacity/color/front/extraction/subset/cleanup/health: PASS")


if __name__ == "__main__":
    main()
