#!/usr/bin/env python3
"""メモリ検証用の合成PDFを一時領域へ生成する。PillowとNumPyは検証専用。"""

import argparse
import io
import json
import zlib
from pathlib import Path

import numpy as np
from PIL import Image

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("destination", type=Path)
parser.add_argument("--compress", action="store_true", help="圧縮用の全写真形式、PNM最悪値、500画像も生成")
parser.add_argument("--photo", type=Path, help="一時的な実写真を使用。入力画像はRepositoryへ含めない")
parser.add_argument(
    "--include-giant",
    action="store_true",
    help="JPEGMEM比較用の225 MP progressiveカラーも生成",
)
args = parser.parse_args()
if args.photo and not args.compress:
    parser.error("--photo requires --compress.")
root = args.destination
root.mkdir(mode=0o700, parents=True, exist_ok=True)


def pdf(data, w, h, gray=False, filter="DCTDecode"):
    page_width, page_height = (
        ("595.28", "841.89") if filter == "FlateDecode" else ("100", "100")
    )
    contents = f"q {page_width} 0 0 {page_height} 0 0 cm /Im0 Do Q\n".encode()
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page_width} {page_height}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>".encode(),
        f"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /Device{'Gray' if gray else 'RGB'} /BitsPerComponent 8 /Filter /{filter} /Length {len(data)} >>\nstream\n".encode()
        + data
        + b"\nendstream",
        f"<< /Length {len(contents)} >>\nstream\n".encode() + contents + b"endstream",
    ]
    out = io.BytesIO()
    out.write(b"%PDF-1.4\n")
    offsets = [0]
    for n, obj in enumerate(objects, 1):
        offsets.append(out.tell())
        out.write(f"{n} 0 obj\n".encode() + obj + b"\nendobj\n")
    xref = out.tell()
    out.write(b"xref\n0 6\n0000000000 65535 f \n")
    for pos in offsets[1:]:
        out.write(f"{pos:010} 00000 n \n".encode())
    out.write(
        f"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    )
    return out.getvalue()


meta = {}
dimensions = [
    (4000, 3000, False),
    (6000, 4000, False),
    (8064, 6048, False),
    (10000, 10000, True),
    (10000, 10000, False),
]
if args.include_giant:
    dimensions.append((15000, 15000, False))
if args.compress:
    dimensions = dimensions[:3] + [(7014, 7014, False)]
for w, h, gray in dimensions:
    rng = np.random.default_rng(42)
    arr = np.empty((h, w) if gray else (h, w, 3), dtype=np.uint8)
    x = np.arange(w, dtype=np.float32)[None, :] / w
    for y in range(0, h, 128):
        n = min(128, h - y)
        yy = np.arange(y, y + n, dtype=np.float32)[:, None] / h
        # Smooth colour fields with fine deterministic sensor-like texture.
        if gray:
            arr[y : y + n] = np.clip(
                70 + 60 * np.sin(x * 19) + 60 * yy + rng.normal(0, 9, (n, w)), 0, 255
            ).astype("uint8")
        else:
            for c in range(3):
                arr[y : y + n, :, c] = np.clip(
                    90
                    + 65 * np.sin(x * (5 + c * 2) + yy * 4 + c)
                    + 65 * yy
                    + rng.normal(0, 9, (n, w)),
                    0,
                    255,
                ).astype("uint8")
    variants = [(False, 2, "baseline")]
    if args.photo:
        arr = np.asarray(Image.open(args.photo).convert("RGB").resize((w, h), Image.Resampling.LANCZOS))
    if gray:
        variants.append((True, 2, "progressive"))
    elif w in (6000, 8064) or args.compress and w == 4000:
        variants.extend([(False, 0, "baseline-444"), (True, 2, "progressive-420")])
    elif w == 15000:
        variants = [(True, 2, "progressive-420")]
    for progressive, subsampling, variant in variants:
        encoded = io.BytesIO()
        Image.fromarray(arr).save(
            encoded,
            format="JPEG",
            quality=85,
            progressive=progressive,
            subsampling=subsampling,
        )
        name = "100mp-color" if w == 10000 and not gray else f"{w}x{h}-{variant}"
        out = pdf(encoded.getvalue(), w, h, gray)
        (root / (name + ".pdf")).write_bytes(out)
        meta[name] = len(out)
        print(name, len(out), flush=True)
# A4 600 dpi: colour scan-like stream, 44 MB compressed. Repeated RGB texture plus noise.
w, h = 4961, 7016
rng = np.random.default_rng(43)
compressor = zlib.compressobj(6)
parts = []
for y in range(h):
    values = rng.integers(0, 132, w, dtype=np.uint8)
    row = np.stack((values * 2, values * 3, 255 - values), axis=1)
    parts.append(compressor.compress(row.tobytes()))
parts.append(compressor.flush())
out = pdf(b"".join(parts), w, h, filter="FlateDecode")
name = "a4-600dpi-flate"
(root / (name + ".pdf")).write_bytes(out)
meta[name] = len(out)
print(name, len(out), flush=True)

if args.compress:
    # Temporary scan-like JPEG with text, fine lines and a grayscale ramp.
    from PIL import ImageDraw
    scan = Image.new("L", (2480, 3508), 248)
    draw = ImageDraw.Draw(scan)
    for y in range(60, 3400, 50):
        draw.text((80, y), "Synthetic document scan - 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ " * 3, fill=25, font_size=26)
    for x in range(100, 2300, 27): draw.line((x, 3200, x, 3360), fill=x % 200, width=2)
    encoded = io.BytesIO(); scan.save(encoded, format="JPEG", quality=90)
    (root / "document-scan.pdf").write_bytes(pdf(encoded.getvalue(), 2480, 3508, gray=True))

    # 500 distinct objects, each containing the same deterministic small JPEG.
    x = np.arange(2048)[None, :]; y = np.arange(2048)[:, None]
    gray = ((x // 16 + y // 16) % 200 + 25).astype("uint8")
    encoded = io.BytesIO(); Image.fromarray(gray).save(encoded, format="JPEG", quality=85)
    raw = encoded.getvalue()
    assert len(raw) >= 32768
    objects = [b"<< /Type /Catalog /Pages 2 0 R >>", b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>", b""]
    refs = []
    for n in range(500):
        refs.append(f"/Im{n} {n + 4} 0 R")
        objects.append(f"<< /Type /XObject /Subtype /Image /Width 2048 /Height 2048 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode /Length {len(raw)} >>\nstream\n".encode() + raw + b"\nendstream")
    objects[2] = f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << {' '.join(refs)} >> >> >>".encode()
    out = io.BytesIO(); out.write(b"%PDF-1.4\n"); offsets = []
    for n, obj in enumerate(objects, 1):
        offsets.append(out.tell()); out.write(f"{n} 0 obj\n".encode() + obj + b"\nendobj\n")
    pos = out.tell(); out.write(f"xref\n0 {len(objects)+1}\n0000000000 65535 f \n".encode())
    for offset in offsets: out.write(f"{offset:010} 00000 n \n".encode())
    out.write(f"trailer\n<< /Size {len(objects)+1} /Root 1 0 R >>\nstartxref\n{pos}\n%%EOF\n".encode())
    (root / "500-images.pdf").write_bytes(out.getvalue())
    print("500-images", len(out.getvalue()), "JPEG", len(raw), flush=True)
    # Mixed stream lengths exercise n×S, not just the sum of actual lengths.
    small = raw
    large_pdf = root / "6000x4000-baseline.pdf"
    import subprocess
    source_pages = json.loads(subprocess.check_output(["qpdf", "--json=2", "--json-key=pages", str(large_pdf)]))["pages"]
    reference = source_pages[0]["images"][0]["object"].split()
    large = subprocess.check_output(["qpdf", "--show-object=" + ",".join(reference[:2]), "--raw-stream-data", str(large_pdf)])
    mixed_objects = objects[:3].copy(); mixed_refs = []
    for n in range(112):
        data, width, height, cs = (large,6000,4000,"RGB") if n < 12 else (small,2048,2048,"Gray")
        mixed_refs.append(f"/Im{n} {n+4} 0 R")
        mixed_objects.append(f"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /Device{cs} /BitsPerComponent 8 /Filter /DCTDecode /Length {len(data)} >>\nstream\n".encode()+data+b"\nendstream")
    mixed_objects[2] = f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << {' '.join(mixed_refs)} >> >> >>".encode()
    out=io.BytesIO(); out.write(b"%PDF-1.4\n"); offsets=[]
    for n,obj in enumerate(mixed_objects,1):
        offsets.append(out.tell()); out.write(f"{n} 0 obj\n".encode()+obj+b"\nendobj\n")
    pos=out.tell(); out.write(f"xref\n0 {len(mixed_objects)+1}\n0000000000 65535 f \n".encode())
    for offset in offsets: out.write(f"{offset:010} 00000 n \n".encode())
    out.write(f"trailer\n<< /Size {len(mixed_objects)+1} /Root 1 0 R >>\nstartxref\n{pos}\n%%EOF\n".encode())
    (root / "mixed-images.pdf").write_bytes(out.getvalue())
    print("mixed-images",len(out.getvalue()),flush=True)


# Near 50 MiB, many unique 1 MiB random content streams. qpdf writes/optimizes these.
def bulk():
    rng = np.random.default_rng(44)
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
    ]
    # Unreferenced streams would be dropped; image resources retain all streams.
    refs = []
    for n in range(49):
        data = rng.bytes(1024 * 1024)
        refs.append(f"/Im{n} {n + 4} 0 R")
        objs.append(
            f"<< /Type /XObject /Subtype /Image /Width 1024 /Height 1024 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length {len(data)} >>\nstream\n".encode()
            + data
            + b"\nendstream"
        )
    objs[2] = (
        f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << {' '.join(refs)} >> >> >>".encode()
    )
    output = io.BytesIO()
    output.write(b"%PDF-1.4\n")
    offsets = []
    for n, obj in enumerate(objs, 1):
        offsets.append(output.tell())
        output.write(f"{n} 0 obj\n".encode() + obj + b"\nendobj\n")
    xref = output.tell()
    output.write(f"xref\n0 {len(objs) + 1}\n0000000000 65535 f \n".encode())
    for pos in offsets:
        output.write(f"{pos:010} 00000 n \n".encode())
    output.write(
        f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    )
    return output.getvalue()


out = bulk()
name = "near-50mib"
(root / (name + ".pdf")).write_bytes(out)
meta[name] = len(out)
print(name, len(out), flush=True)
(root / "sizes.json").write_text(json.dumps(meta, indent=2))
