#!/usr/bin/env python3
"""実 compose の回転・配置・抽出と D1-r5 の境界を検証する。"""
import argparse
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import random
import re
import shutil
import subprocess
import tempfile
import time

import fitz
from PIL import Image, PngImagePlugin

ROOT = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("pdf_fixture", ROOT / "compress-validation.py")
pdf_fixture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(pdf_fixture)
MIB = 1024 * 1024
FONTS = [
    {"id": "gothic", "sha256": "595dfefa96dcb281c40f5d560841f3fc623b0d9d12064481a7daf78044462673"},
    {"id": "mincho", "sha256": "dbcea04578ac1e9d3484525e870ce491bd04361768f4d2ba4b827d96e20f891d"},
]
POPPLER = "sha256:fecac17e9d2e442c4721dff3a895a7dc1b7c1f69911197e96dce4f08b9a6ff62"


def run(*args, input=None):
    return subprocess.run(args, input=input, capture_output=True, check=True, timeout=120).stdout


def pdf_source(path, dimensions, megabytes=0):
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        f"<< /Type /Pages /Count {len(dimensions)} /Kids [{' '.join(f'{4 + 2*i} 0 R' for i in range(len(dimensions)))}] >>".encode(),
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    rng = random.Random(106)
    retained = [rng.randbytes(MIB) for _ in range(megabytes - 1)] + [b""] if megabytes else []
    first_image = 4 + 2 * len(dimensions)
    image_refs = " ".join(f"/B{i} {first_image+i} 0 R" for i in range(len(retained)))
    tiles = "".join(f"q 4 0 0 4 {8+i*6} 12 cm /B{i} Do Q\n" for i in range(len(retained)))
    for i, (media, crop, rotation, unit) in enumerate(dimensions):
        media, crop = (" ".join(map(str, box)) for box in (media, crop))
        resources = "/Font << /F1 3 0 R >>"
        if i == 0 and retained:
            resources += f" /XObject << {image_refs} >>"
        objects.append(f"<< /Type /Page /Parent 2 0 R /MediaBox [{media}] /CropBox [{crop}] "
                       f"/Rotate {rotation} /UserUnit {unit} /Resources << {resources} >> /Contents {5+2*i} 0 R >>".encode())
        content = f"BT /F1 12 Tf 80 140 Td (SOURCE {i+1:02}) Tj ET\n0 0 1 RG 80 110 180 1 re S\n"
        objects.append(pdf_fixture.stream("", (content + (tiles if i == 0 else "")).encode()))
    for data in retained:
        objects.append(pdf_fixture.stream(f"/Type /XObject /Subtype /Image /Width 1024 /Height {max(1,len(data)//1024)} "
                                         "/ColorSpace /DeviceGray /BitsPerComponent 8", data))
    if retained:
        remaining = megabytes * MIB - len(pdf_fixture.pdf(objects))
        height = remaining // 1024 - 1
        objects[-1] = pdf_fixture.stream(f"/Type /XObject /Subtype /Image /Width 1024 /Height {height} "
                                         "/ColorSpace /DeviceGray /BitsPerComponent 8", rng.randbytes(height * 1024))
        remaining = megabytes * MIB - len(pdf_fixture.pdf(objects))
        objects[-1] += b"\n%" + b"x" * (remaining - 2)
    data = pdf_fixture.pdf(objects)
    if megabytes:
        assert len(data) == megabytes * MIB
    path.write_bytes(data)


def png(path, width, height, color, size=0, seed=0):
    image = Image.new("RGBA", (width, height), color)
    image.paste((0, 0, 0, 0), (0, 0, width // 4, height // 4))
    if size:
        rows = 145 if size == MIB // 2 else 595
        noise = random.Random(seed).randbytes(3 * 1000 * rows)
        image.paste(Image.frombytes("RGB", (1000, rows), noise).convert("RGBA"), (900, 2000 - rows))
        data = io.BytesIO()
        image.save(data, format="PNG")
        padding = size - data.tell() - 12 - len(b"test\0")
        assert padding >= 0, padding
        info = PngImagePlugin.PngInfo()
        info.add_text("test", "x" * padding)
        image.save(path, pnginfo=info)
        assert path.stat().st_size == size
    else:
        image.save(path)


def photo_jpeg(path, seed):
    side = 2000
    rows = 1100
    rng = random.Random(seed)
    # 滑らかな領域と細かな領域を持つ合成写真。JPEG のコメントで 2 MiB に揃える。
    photo = Image.frombytes("RGB", (125, 125), rng.randbytes(125 * 125 * 3))
    photo = photo.resize((side, side), Image.Resampling.BICUBIC)
    detail = Image.frombytes("RGB", (side, rows), rng.randbytes(side * rows * 3))
    photo.paste(Image.blend(photo.crop((0, 0, side, rows)), detail, 0.55), (0, 0))
    encoded = io.BytesIO()
    photo.save(encoded, format="JPEG", quality=92, optimize=True)
    padding = 2 * MIB - encoded.tell() - 4
    assert 1 <= padding <= 65533, padding
    photo.save(path, format="JPEG", quality=92, optimize=True, comment=b"x" * padding)
    assert path.stat().st_size == 2 * MIB


def layout(pages, blank=False, images=None, elements=None):
    return dict(layout="ds-layout/1", fonts=FONTS, blank=blank,
                pages=[dict(index=i, width=w, height=h) for i, (w, h) in enumerate(pages, 1)],
                images=[dict(id=id, sha256=hashlib.sha256(path.read_bytes()).hexdigest()) for id, path in (images or {}).items()],
                elements=elements or [])


def text(i, page, value, rect, font="gothic", size=1000):
    return dict(id=f"el_text{i:04}", kind="text", page=page, rect=rect, text=value, font=font,
                size=size, color="#000000", align="left", valign="top", lineHeight=120)


def image(i, page, id, rect):
    return dict(id=f"el_image{i:04}", kind="image", page=page, rect=rect, image=id)


class Api:
    def __init__(self, image_name):
        self.name = run("docker", "run", "-d", "--rm", "--pull=never", "--cpus", "1",
                        "--memory", "1536m", "--memory-swap", "1536m", "--read-only",
                        "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=256m",
                        "-p", "127.0.0.1::8080", image_name).decode().strip()
        port = run("docker", "port", self.name, "8080").decode().strip().split(":")[-1]
        self.url = "http://127.0.0.1:" + port
        try:
            for _ in range(100):
                status = subprocess.run(["curl", "-fsS", self.url + "/healthz"], capture_output=True)
                if status.returncode == 0:
                    return
                time.sleep(0.1)
            raise RuntimeError("API did not start")
        except BaseException:
            self.close()
            raise

    def close(self):
        run("docker", "stop", self.name)

    def compose(self, folder, spec, source=None, images=None, expected=200):
        path = folder / "layout.json"
        path.write_text(json.dumps(spec, ensure_ascii=False, separators=(",", ":")), encoding="utf8")
        output = folder / "result.pdf"
        args = ["curl", "-sS", "--max-time", "60", "-o", str(output), "-w", "%{http_code}",
                "-F", f"layout=<{path};type=application/json"]
        if source:
            args += ["-F", f"file=@{source};type=application/pdf"]
        for id, path in (images or {}).items():
            args += ["-F", f"image=@{path};filename={id}"]
        started = time.monotonic()
        status = int(run(*args, self.url + "/api/pdf/compose"))
        seconds = time.monotonic() - started
        result = output.read_bytes()
        if expected is not None:
            assert status == expected, (status, result[:400])
        logs = subprocess.run(["docker", "logs", self.name], capture_output=True, check=True)
        peaks = re.findall(r"PDF合成の台帳ピーク ([0-9]+) bytes", (logs.stdout + logs.stderr).decode())
        assert run("docker", "exec", self.name, "find", "/tmp/amane-pdf-api", "-mindepth", "1").strip() == b""
        return result, dict(status=status, seconds=round(seconds, 3), peak_bytes=int(peaks[-1]) if peaks else None,
                            output_bytes=len(result) if status == 200 else 0,
                            reason=None if status == 200 else json.loads(result).get("reason"))


def poppler(data):
    if shutil.which("pdftotext"):
        return run("pdftotext", "-raw", "-", "-", input=data).decode()
    return run("docker", "run", "--rm", "-i", "--pull=never", "--network=none", "--entrypoint",
               "pdftotext", POPPLER, "-raw", "-", "-", input=data).decode()


def checks(api, folder):
    source = folder / "source.pdf"
    dims = [([0, 0, 600, 800], [0, 0, 600, 800], 0, 1),
            ([0, 0, 700, 1000], [50, 40, 650, 960], 90, 1),
            ([0, 0, 640, 480], [30, 20, 610, 460], 180, 1),
            ([0, 0, 420, 500], [20, 40, 400, 450], 270, 2),
            ([0, 0, 500, 700], [0, 0, 500, 700], 0, 1)]
    pages = [(60000, 80000), (92000, 60000), (58000, 44000), (82000, 76000), (50000, 70000)]
    pdf_source(source, dims)
    blue, red, photo = (folder / name for name in ("blue.png", "red.png", "photo.jpg"))
    png(blue, 32, 24, (0, 40, 255, 255))
    png(red, 24, 32, (255, 10, 0, 255))
    Image.new("RGB", (40, 20), (50, 220, 80)).save(photo, progressive=True)
    assets = {"as_blue": blue, "as_red": red, "as_photo": photo}
    elements = []
    for page in range(1, 5):
        elements += [image(page * 4, page, "as_blue", [5000, 7000, 12000, 12000]),
                     text(page, page, "架空 太郎\n書類の検証", [8000, 9000, 22000, 5000], "mincho" if page % 2 else "gothic", 1600),
                     image(page * 4 + 1, page, "as_red", [11500, 12500, 8000, 8000]),
                     image(page * 4 + 2, page, "as_photo", [24000, 7000, 10000, 10000])]
    spec = layout(pages, images=assets, elements=elements)
    data, measurement = api.compose(folder, spec, source, assets)
    extracted = poppler(data)
    matrix_max = 0.0
    rendered_max = 0.0
    with fitz.open(source) as old, fitz.open(stream=data) as new:
        assert len(old) == len(new) == 5
        for index, (before, after) in enumerate(zip(old, new)):
            assert (before.mediabox, before.cropbox, before.rotation, before.rect) == (
                after.mediabox, after.cropbox, after.rotation, after.rect)
            assert f"SOURCE {index+1:02}" in before.get_text() and f"SOURCE {index+1:02}" in after.get_text()
            assert f"SOURCE{index+1:02}" in re.sub(r"\s+", "", extracted), repr(extracted)
            if index == 4:
                assert before.get_pixmap().samples == after.get_pixmap().samples
                continue
            assert "架空 太郎" in after.get_text() and "書類の検証" in after.get_text()
            transform = after.rotation_matrix
            # MuPDF's rotation matrix is in user units; the extracted image bbox
            # is in physical points. Scale the translation for UserUnit.
            unit = dims[index][3]
            transform.e *= unit
            transform.f *= unit
            expected = fitz.Rect(50, 85, 170, 175)
            boxes = [fitz.Rect(info["bbox"]) * transform for info in after.get_image_info()]
            errors = [max(abs(a - b) for a, b in zip(box, expected)) for box in boxes]
            matrix_max = max(matrix_max, min(errors))
            pixels = after.get_pixmap(matrix=fitz.Matrix(2, 2), clip=fitz.Rect(42, 77, 178, 181))
            rgb = Image.frombytes("RGB", (pixels.width, pixels.height), pixels.samples)
            old_pixels = before.get_pixmap(matrix=fitz.Matrix(2, 2), clip=fitz.Rect(42, 77, 178, 181))
            original_rgb = Image.frombytes("RGB", (old_pixels.width, old_pixels.height), old_pixels.samples)
            xs, ys = [], []
            for y in range(rgb.height):
                for x in range(rgb.width):
                    r, g, b = rgb.getpixel((x, y))
                    if b > 230 and g < 65 and r < 25 and (r, g, b) != original_rgb.getpixel((x, y)):
                        xs.append(x)
                        ys.append(y)
            assert xs and ys
            bounds = (42 + min(xs)/2, 77 + min(ys)/2, 42 + (max(xs)+1)/2, 77 + (max(ys)+1)/2)
            rendered_max = max(rendered_max, max(abs(a-b) for a,b in zip(bounds, expected)))
            assert rgb.getpixel(((155-42)*2, (150-77)*2))[0] > 230
            assert rgb.getpixel(((55-42)*2, (90-77)*2)) == (255, 255, 255)
    assert extracted.count("架空 太郎") == extracted.count("書類の検証") == 4, extracted
    assert matrix_max <= 0.6 and rendered_max <= 0.6, (matrix_max, rendered_max)

    spec["pages"][0]["width"] += 1
    assert api.compose(folder, spec, source, assets, 400)[1]["reason"] == "page-mismatch"
    for orientation in range(2, 9):
        exif = Image.Exif()
        exif[274] = orientation
        Image.new("RGB", (40, 20)).save(photo, exif=exif)
        spec = layout([(60000, 80000)], True, {"as_photo": photo},
                      [image(1, 1, "as_photo", [5000, 5000, 10000, 10000])])
        assert api.compose(folder, spec, images={"as_photo": photo}, expected=400)[1]["reason"] == "image-orientation"

    exact = text(1, 1, "あいうえおかきくけこ", [0, 0, 9566, 1000])
    spec = layout([(60000, 80000)], True, elements=[exact])
    assert "あいうえおかきくけこ" in poppler(api.compose(folder, spec)[0])
    exact["rect"][2] -= 1
    assert api.compose(folder, spec, expected=422)[1]["reason"] == "text-overflow"
    exact["text"] = "𠮷"
    assert api.compose(folder, spec, expected=400)[1]["reason"] == "unsupported-text"
    return {**measurement, "matrix_error_pt": matrix_max, "rendered_error_pt": rendered_max,
            "orientation_cases": 7, "original_pages": 5}


def boundary(api, folder, megabytes, image_count, image_bytes, *, page_count=30, jpeg=False):
    source = folder / "background.pdf"
    pages = [(59528, 84189)] * page_count
    dimension = ([0, 0, 595.28, 841.89], [0, 0, 595.28, 841.89], 0, 1)
    pdf_source(source, [dimension] * len(pages), megabytes)
    assets = {}
    for i in range(image_count):
        id = f"as_{i:08}"
        assets[id] = folder / (id + (".jpg" if jpeg else ".png"))
        if jpeg:
            photo_jpeg(assets[id], i)
        else:
            png(assets[id], 2000, 2000, (30 + i * 5, 110, 170, 255), image_bytes, i)
    elements = []
    front = 60 - image_count
    remaining = 200 - image_count - front
    per_page, extra_pages = divmod(remaining, page_count - 1)
    for page in range(1, page_count + 1):
        count = front if page == 1 else per_page + (page - 2 < extra_pages)
        for i in range(count):
            elements.append(text(len(elements), page, "架空 太郎\n書類の検証",
                                 [2000+(i%3)*17000, 2000+(i//3)*2500, 12000, 2400],
                                 "mincho" if i % 2 else "gothic", 1000))
            if page == 1 and i < image_count:
                id = f"as_{i:08}"
                elements.append(image(i, page, id, [3500+(i%5)*10400, 49000+(i//5)*7300, 6400, 6400]))
    assert len(elements) == 200
    data, result = api.compose(folder, layout(pages, images=assets, elements=elements), source, assets, expected=None)
    if result["status"] == 200:
        with fitz.open(stream=data) as doc:
            assert len(doc) == page_count and all(f"SOURCE {i+1:02}" in page.get_text() for i, page in enumerate(doc))
    background_bytes = source.stat().st_size
    actual_image_bytes = sum(p.stat().st_size for p in assets.values())
    assert actual_image_bytes == image_count * image_bytes
    assert result["peak_bytes"] is not None
    return dict(**result, background_bytes=background_bytes, image_bytes=actual_image_bytes,
                combined_bytes=background_bytes + actual_image_bytes, image_count=image_count,
                image_format=("jpeg" if jpeg else "png") if image_count else None,
                pages=page_count, elements=len(elements))


def draw_lines(api, folder):
    source = folder / "background.pdf"
    pages = [(59528, 84189)] * 10
    dimension = ([0, 0, 595.28, 841.89], [0, 0, 595.28, 841.89], 0, 1)
    pdf_source(source, [dimension] * len(pages))
    elements = [text(i, i // 20 + 1, "\n".join(["合成"] * 20),
                     [1000+(i%10)*5600, 1500+((i//10)%2)*30000, 5300, 14500],
                     "mincho" if i % 2 else "gothic", 600) for i in range(200)]
    spec = layout(pages, elements=elements)
    data, result = api.compose(folder, spec, source, expected=None)
    if result["status"] == 200:
        with fitz.open(stream=data) as doc:
            assert sum(page.get_text().count("合成") for page in doc) == 4000
        assert poppler(data).count("合成") == 4000
    elements[0]["text"] += "\n合成"
    elements[0]["rect"][3] += 1000
    assert api.compose(folder, spec, source, expected=422)[1]["reason"] == "limit-exceeded"
    return dict(**result, draw_lines=4000, elements=len(elements))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--boundaries", action="store_true")
    parser.add_argument("--only-boundaries", action="store_true")
    parser.add_argument("--combined-proposal", action="store_true")
    parser.add_argument("--results", type=Path)
    args = parser.parse_args()
    success = (200, None)
    too_complex = (422, "image-too-complex")
    cases = [] if args.only_boundaries else [("geometry_and_extraction", checks, success)]
    if args.boundaries or args.only_boundaries:
        cases += [
            ("34_plus_10", lambda api, folder: boundary(api, folder, 34, 20, MIB // 2), success),
            ("40_plus_4", lambda api, folder: boundary(api, folder, 40, 2, 2 * MIB), success),
            ("44_plus_10", lambda api, folder: boundary(api, folder, 44, 20, MIB // 2), too_complex),
            ("50_plus_4", lambda api, folder: boundary(api, folder, 50, 2, 2 * MIB), too_complex),
            ("trial_background", lambda api, folder: boundary(api, folder, 20, 0, 0, page_count=10), success),
            ("trial_jpegs", lambda api, folder: boundary(api, folder, 10, 5, 2 * MIB, page_count=10, jpeg=True), success),
            ("4000_lines", draw_lines, success),
        ]
    if args.combined_proposal:
        cases.append(("40_plus_10", lambda api, folder: boundary(api, folder, 40, 20, MIB // 2), success))
    results = {}
    unexpected = []
    for name, case, expected in cases:
        api = Api(args.image)
        try:
            with tempfile.TemporaryDirectory(prefix="compose-verification-") as temp:
                results[name] = case(api, Path(temp))
                print(name, json.dumps(results[name]), flush=True)
                actual = (results[name]["status"], results[name]["reason"])
                if actual != expected:
                    unexpected.append(f"{name}: expected {expected}, got {actual}")
        finally:
            api.close()
    if args.results:
        args.results.write_text(json.dumps(results, indent=2) + "\n")
    if unexpected:
        raise SystemExit("\n".join(unexpected))


if __name__ == "__main__":
    main()
