#!/usr/bin/env python3
"""電子印鑑の実HTTP・向き・透過と最大入力同時2件を検証する。描画ツールはCI専用。"""
import argparse
import base64
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
from pathlib import Path
import re
import struct
import subprocess
import tempfile
import time
import zlib

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


smoke = load("stamp_smoke", ROOT / "scripts/docker-smoke.py")


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, timeout=60)


def chunk(kind, payload):
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))


def png_metadata(data):
    if data[:16] != b"\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDR" or len(data) < 33:
        raise ValueError("Expected PNG with IHDR")
    width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", data[16:29])
    position = 8
    while position + 12 <= len(data):
        length = struct.unpack_from(">I", data, position)[0]
        end = position + 12 + length
        if end > len(data):
            raise ValueError("Truncated PNG chunk")
        kind = data[position+4:position+8]
        if kind == b"IEND":
            if length != 0:
                raise ValueError("Invalid IEND")
            return {"image_bytes": len(data), "image_sides": [width, height], "depth": depth,
                    "color_type": color, "adam7": interlace == 1,
                    "image_png_bytes": end, "image_trailing_bytes": len(data)-end}
        position = end
    raise ValueError("Missing IEND")


def png(width=120, height=80, depth=8, adam7=False):
    pixels = np.zeros((height, width, 4), dtype=np.uint16 if depth == 16 else np.uint8)
    colors = np.array([(255, 0, 0, 255), (0, 255, 0, 255), (0, 0, 255, 255), (255, 255, 0, 255)], dtype=pixels.dtype)
    if depth == 16:
        colors *= 257
    for i, (ys, xs) in enumerate(((slice(0, height//2), slice(0, width//2)),
                                 (slice(0, height//2), slice(width//2, None)),
                                 (slice(height//2, None), slice(0, width//2)),
                                 (slice(height//2, None), slice(width//2, None)))):
        pixels[ys, xs] = colors[i]
    # An opaque gray page shows these regions independently of alpha's stored RGB.
    pixels[height//4:height//2, width//4:width//2] = 0
    pixels[height//2:3*height//4, width//2:3*width//4] = (0, 0, 0, 128 * (257 if depth == 16 else 1))
    passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)] if adam7 else [(0, 0, 1, 1)]
    encoder = zlib.compressobj(9)
    pieces = []
    for x, y, dx, dy in passes:
        part = pixels[y::dy, x::dx]
        if part.shape[0] == 0 or part.shape[1] == 0:
            continue
        if depth == 16:
            part = part.astype(">u2")
        for row in part:
            pieces.append(encoder.compress(b"\0" + row.tobytes()))
    pieces.append(encoder.flush())
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, depth, 6, 0, 0, int(adam7))) + chunk(b"IDAT", b"".join(pieces)) + chunk(b"IEND", b"")


def render(image, output):
    cases = [(r, u) for r in (0, 90, 180, 270) for u in (0.5, 1, 2)]
    with tempfile.TemporaryDirectory(prefix="stamp-render-") as temp:
        directory = Path(temp)
        objects = {"obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
                   "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": len(cases), "/Kids": [f"{i+3} 0 R" for i in range(len(cases))]}}}
        stream_ref = len(cases) + 3
        # Gray background extends past CropBox, also exercising original-content preservation.
        objects[f"obj:{stream_ref} 0 R"] = {"stream": {"dict": {}, "data": base64.b64encode(b"0.8 g -20 -30 650 900 re f").decode()}}
        for i, (rotation, unit) in enumerate(cases):
            objects[f"obj:{i+3} 0 R"] = {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [-20, -30, 630, 850],
                "/CropBox": [100, 200, 500, 700], "/Rotate": rotation, "/UserUnit": unit, "/Resources": {}, "/Contents": f"{stream_ref} 0 R"}}
        objects["trailer"] = {"value": {"/Root": "1 0 R", "/Size": stream_ref + 1}}
        (directory / "source.json").write_text(json.dumps({"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objects]}))
        command("qpdf", "--json-input", str(directory / "source.json"), str(directory / "source.pdf"))
        evidence = []
        with smoke.running_container(image, isolated=True) as api:
            for ocg_off in (False, True):
                if ocg_off:
                    # A visible red background would expose accidental enabling of the input's OFF layer.
                    ocg_ref = stream_ref + 1
                    objects[f"obj:{ocg_ref} 0 R"] = {"value": {"/Type": "/OCG", "/Name": "u:Input OFF layer"}}
                    objects["obj:1 0 R"]["value"]["/OCProperties"] = {"/OCGs": [f"{ocg_ref} 0 R"],
                        "/D": {"/BaseState": "/OFF", "/Order": [f"{ocg_ref} 0 R"]}}
                    for page in range(len(cases)):
                        objects[f"obj:{page+3} 0 R"]["value"]["/Resources"] = {"/Properties": {"/InputOff": f"{ocg_ref} 0 R"}}
                    objects[f"obj:{stream_ref} 0 R"]["stream"]["data"] = base64.b64encode(
                        b"0.8 g -20 -30 650 900 re f\n/OC /InputOff BDC 1 0 0 rg -20 -30 650 900 re f EMC").decode()
                    objects["trailer"]["value"]["/Size"] = ocg_ref + 1
                    (directory / "source.json").write_text(json.dumps({"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objects]}))
                    command("qpdf", "--json-input", str(directory / "source.json"), str(directory / "source.pdf"))
                source = (directory / "source.pdf").read_bytes()
                for depth, adam7 in ((8, False), (16, True)):
                    for page, (rotation, unit) in enumerate(cases, 1):
                        body, content_type = smoke.multipart(source, password=None, image=png(depth=depth, adam7=adam7))
                        status, _, result = api.request("POST", f"/api/pdf/stamp-image?page={page}&x=0.2&y=0.2&width=0.3", body, content_type)
                        assert status == 200, result[:500]
                        path = directory / "stamped.pdf"
                        path.write_bytes(result)
                        command("qpdf", "--check", str(path))
                        if ocg_off:
                            document = json.loads(command("qpdf", "--json=2", "--json-key=qpdf", "--json-stream-data=none", str(path)).stdout)
                            out_objects = document["qpdf"][1]
                            def resolve(value):
                                while isinstance(value, str) and re.fullmatch(r"[0-9]+ [0-9]+ R", value):
                                    value = out_objects["obj:" + value]["value"]
                                return value
                            catalog = resolve(out_objects["trailer"]["value"]["/Root"])
                            properties = resolve(catalog["/OCProperties"])
                            assert resolve(properties["/D"])["/BaseState"] == "/OFF"
                            assert len(properties["/OCGs"]) == 1
                        command("pdftoppm", "-f", str(page), "-l", str(page), "-singlefile", "-cropbox", "-r", "144", "-png", str(path), str(directory / "render"))
                        pixels = np.array(Image.open(directory / "render.png").convert("RGB"))
                        h, w = pixels.shape[:2]
                        left, top = 0.2 * w, 0.2 * h
                        sw, sh = 0.3 * w, 0.3 * w * 80 / 120
                        for x, y, expected in ((0.1, 0.1, (255,0,0)), (0.9,0.1,(0,255,0)),
                                                (0.1,0.9,(0,0,255)), (0.9,0.9,(255,255,0)),
                                                (0.375,0.375,(204,204,204)), (0.625,0.625,(102,102,102))):
                            actual = pixels[int(top+y*sh), int(left+x*sw)]
                            assert np.max(np.abs(actual.astype(int) - expected)) <= 3, (ocg_off, rotation, unit, depth, x, y, actual)
                        assert np.max(np.abs(pixels[0, 0].astype(int) - 204)) <= 1
                        # Non-target pages retain gray content; the OFF red layer stays hidden.
                        other = 2 if page == 1 else 1
                        command("pdftoppm", "-f", str(other), "-l", str(other), "-singlefile", "-cropbox", "-r", "12", "-png", str(path), str(directory / "other"))
                        assert np.max(np.abs(np.array(Image.open(directory / "other.png").convert("RGB")).astype(int) - 204)) <= 1
                        api.assert_clean()
                        record = {"rotation": rotation, "user_unit": unit, "depth": depth, "adam7": adam7,
                                  "input_ocg_base_state": "OFF" if ocg_off else None}
                        evidence.append(record)
                        if page in (1, 4, 7, 10) and depth == 8 and not ocg_off:
                            Image.open(directory / "render.png").save(output / f"stamp-rotate-{rotation}.png")
        (output / "render-results.json").write_text(json.dumps(evidence, indent=2))
        print(f"Stamp image: {len(evidence)} render/alpha/orientation/non-target cases PASS", flush=True)


def pad_pdf(data, size):
    match = re.search(rb"startxref\s+([0-9]+)", data)
    offset = int(match[1])
    suffix = original = data[offset:]
    for _ in range(3):
        suffix = re.sub(rb"(startxref\s+)[0-9]+", lambda m: m[1] + str(size-len(suffix)).encode(), original)
    return data[:offset] + b" " * (size - len(suffix) - offset) + suffix


def capacity(image, output, image_path=None):
    overlay = load("stamp_capacity_fixture", ROOT / "scripts/overlay-validation.py")
    with tempfile.TemporaryDirectory(prefix="stamp-capacity-") as temp:
        directory = Path(temp)
        evidence = overlay.fixture(directory)
        pdf = pad_pdf((directory / "capacity.pdf").read_bytes(), 50 * 1048576)
        if image_path is None:
            stamp = png(2000, 2000, 16, True)
            stamp += bytes(1048576-len(stamp))
        else:
            stamp = image_path.read_bytes()
        image_info = png_metadata(stamp)
        body, content_type = smoke.multipart(pdf, password=None, image=stamp)
        evidence.update(input_bytes=len(pdf), **image_info)
        with smoke.running_container(image, isolated=True) as api:
            def send(_):
                start = time.monotonic()
                status, _, result = api.request("POST", "/api/pdf/stamp-image?page=1000&x=0.2&y=0.2&width=0.5", body, content_type)
                elapsed = time.monotonic()-start
                assert status == 200, (status, result[:500])
                assert elapsed <= 24, elapsed
                return {"seconds_including_upload_and_download": elapsed, "output_bytes": len(result)}
            with ThreadPoolExecutor(max_workers=2) as pool:
                evidence["requests"] = list(pool.map(send, range(2)))
            api.assert_clean()
            events = {k: int(v) for k, v in (line.split() for line in api.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())}
            assert events["oom"] == events["oom_kill"] == 0
            evidence.update(memory_peak=int(api.exec("cat", "/sys/fs/cgroup/memory.peak").stdout), memory_events=events,
                            cpu=1, memory_limit_mib=1536, swap=False, tmpfs_limit_mib=256,
                            concurrent=2, jobs_remaining=0, driver_memory_included=False)
            processes = api.exec("ps", "-eo", "comm").stdout.decode().splitlines()
            assert not set(processes) & {"qpdf", "pdfcpu", "prlimit"}
        (output / "capacity-results.json").write_text(json.dumps(evidence, indent=2))
        print(json.dumps(evidence, indent=2), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--capacity", action="store_true")
    parser.add_argument("--capacity-image", type=Path, help="容量測定に使うPNG。省略時は16 bit Adam7をIEND後に0埋めして1 MiBにする")
    parser.add_argument("--output", type=Path, default=Path("/tmp/stamp-image-validation"))
    args = parser.parse_args()
    if args.capacity_image is not None and not args.capacity:
        parser.error("--capacity-image requires --capacity")
    args.output.mkdir(parents=True, exist_ok=True)
    if args.capacity:
        capacity(args.image, args.output, args.capacity_image)
    else:
        render(args.image, args.output)


if __name__ == "__main__":
    main()
