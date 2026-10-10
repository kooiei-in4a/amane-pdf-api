#!/usr/bin/env python3
"""画像→PDFの実HTTP・描画比較と、任意のDocker容量測定。合成画像だけを使用する。"""
import argparse
import concurrent.futures
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile
import threading
import time

import numpy as np
from PIL import Image, ImageDraw, ImageFile, ImageFilter, ImageOps

ROOT = Path(__file__).resolve().parent.parent
MIB = 1048576
spec = importlib.util.spec_from_file_location("smoke", ROOT / "scripts/docker-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, timeout=120)


def request(api, inputs, quality, expected=200):
    body, content_type = smoke.multipart(password=None, files=inputs)
    start = time.monotonic()
    status, headers, pdf = api.request("POST", "/api/pdf/from-images?quality=" + quality, body, content_type)
    seconds = time.monotonic() - start
    assert status == expected, (quality, expected, status, pdf[:300] if status != 200 else "PDF")
    if status == 200:
        assert headers["Content-Type"] == "application/pdf"
        assert "filename=images.pdf" in headers["Content-Disposition"]
        assert pdf.startswith(b"%PDF-") and len(pdf) <= 54 * MIB
        reason = None
    else:
        problem = json.loads(pdf)
        reason = problem.get("reason")
        assert headers["Content-Type"].startswith("application/problem+json")
        assert reason in ("output-too-large", "unsupported-image")
        assert not any(marker in pdf for marker in (b"private-image-marker", b"/tmp/", b"stack"))
    return {"status": status, "seconds": round(seconds, 3), "output_bytes": len(pdf) if status == 200 else 0,
            "reason": reason}, pdf


def small_jpeg(orientation):
    image = Image.new("RGB", (64, 48))
    draw = ImageDraw.Draw(image)
    for rectangle, color in (((0, 0, 31, 23), (220, 20, 20)), ((32, 0, 63, 23), (20, 180, 20)),
                             ((0, 24, 31, 47), (20, 20, 220)), ((32, 24, 63, 47), (220, 180, 20))):
        draw.rectangle(rectangle, fill=color)
    exif = Image.Exif(); exif[274] = orientation; exif[315] = "private-image-marker"
    output = io.BytesIO()
    image.save(output, "JPEG", quality=95, subsampling=2, dpi=(300, 300), exif=exif)
    # An APP1 after more than 64 KiB of other markers must still be read.
    data = output.getvalue()
    return data[:2] + b"\xff\xe2" + struct.pack(">H", 65532) + b"P" * 65530 + data[2:] + b"private-image-marker"


def inspect_pdf(path):
    command("qpdf", "--check", str(path))
    result = command("qpdf", "--json", str(path))
    return json.loads(result.stdout)


def verify_layout(path, metadata):
    objects = metadata["qpdf"][1]
    for page in metadata["pages"]:
        dictionary = objects["obj:" + page["object"]]["value"]
        image = page["images"][0]
        w, h = image["width"], image["height"]
        p, q = ((297, 210) if w >= h else (210, 297))
        p *= 72 / 25.4; q *= 72 / 25.4; margin = 10 * 72 / 25.4
        assert np.max(np.abs(np.array(dictionary["/MediaBox"]) - [0, 0, p, q])) < .01
        scale = min((p - 2 * margin) / w, (q - 2 * margin) / h)
        expected = [w * scale, 0, 0, h * scale, (p - w * scale) / 2, (q - h * scale) / 2]
        content = command("qpdf", "--show-object=" + page["contents"][0].split()[0],
                          "--filtered-stream-data", str(path)).stdout.decode()
        matrix = re.search(r"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) cm", content)
        assert matrix and np.max(np.abs(np.array([float(v) for v in matrix.groups()]) - expected)) < .01
        assert "/Rotate" not in dictionary or dictionary["/Rotate"] == 0


def render(image):
    for tool in ("qpdf", "pdftoppm"):
        assert shutil.which(tool), tool + " is required on the validation host."
    smoke.assert_startup_failure(image, ("Pdf__JpegtranPath=/missing/jpegtran",),
                                 "画像からPDFへの変換の自己テストに失敗しました。")
    smoke.assert_startup_failure(image, ("Pdf__ImageJpegtranAddressSpaceLimitBytes=335544320",
                                 "Pdf__ImageJpegtranMaxMemoryMegabytes=269"), "PDF設定値が不正です。")
    with tempfile.TemporaryDirectory(prefix="amane-images-render-") as temporary:
        directory = Path(temporary)
        with smoke.running_container(image, isolated=True) as api:
            for quality in ("standard", "original"):
                inputs = [small_jpeg(orientation) for orientation in range(1, 9)]
                _, pdf = request(api, inputs, quality)
                api.assert_clean()
                path = directory / (quality + ".pdf"); path.write_bytes(pdf)
                metadata = inspect_pdf(path)
                assert len(metadata["pages"]) == 8
                verify_layout(path, metadata)
                assert b"private-image-marker" not in pdf
                command("pdftoppm", "-r", "72", "-png", str(path), str(directory / quality))
                for i, source in enumerate(inputs, 1):
                    expected = np.array(ImageOps.exif_transpose(Image.open(io.BytesIO(source))).convert("RGB"))
                    actual = np.array(Image.open(directory / f"{quality}-{i}.png").convert("RGB"))
                    # Compare four interior colored regions after page centering, independently of orientation code.
                    page = metadata["pages"][i-1]
                    w, h = page["images"][0]["width"], page["images"][0]["height"]
                    p, q = actual.shape[1], actual.shape[0]; margin = 10 * 72 / 25.4
                    k = min((p - 2 * margin) / w, (q - 2 * margin) / h)
                    for fx, fy in ((.25, .25), (.75, .25), (.25, .75), (.75, .75)):
                        x = round((p - w * k) / 2 + fx * w * k); y = round((q - h * k) / 2 + fy * h * k)
                        reference = expected[round(fy * expected.shape[0]), round(fx * expected.shape[1])].astype(int)
                        # The second JPEG quantization and decoder color conversion may change an interior channel by several levels.
                        assert np.max(np.abs(actual[y, x].astype(int) - reference)) <= 8, (quality, i, fx, fy, actual[y, x].tolist(), reference.tolist())
                    stream_id = page["images"][0]["object"].split()[0]
                    jpeg = command("qpdf", "--show-object=" + stream_id, "--raw-stream-data", str(path)).stdout
                    assert jpeg[2:4] == b"\xff\xe0" and jpeg[6:11] == b"JFIF\0"
                    assert jpeg[13:18] == b"\x00\x00\x01\x00\x01", "JFIF density must be normalized"
            # PNG alpha and its declared dimensions survive both qualities.
            png = io.BytesIO(); Image.new("RGBA", (64, 48), (200, 0, 0, 128)).save(png, "PNG")
            for quality in ("standard", "original"):
                _, pdf = request(api, [png.getvalue()], quality)
                path = directory / (quality + "-alpha.pdf"); path.write_bytes(pdf)
                data = inspect_pdf(path); verify_layout(path, data)
                dictionary = data["qpdf"][1]["obj:" + data["pages"][0]["images"][0]["object"]]["stream"]["dict"]
                assert "/SMask" in dictionary
                command("pdftoppm", "-r", "72", "-singlefile", "-png", str(path), str(directory / quality))
                pixel = np.array(Image.open(directory / (quality + ".png")).convert("RGB"))[298, 421]
                assert np.max(np.abs(pixel.astype(int) - [227, 127, 127])) <= 2
                api.assert_clean()
            logs = api.exec("sh", "-c", "find /tmp/amane-pdf-api -mindepth 1 -print").stdout
            assert not logs
        print("from-images: A4/10mm/order/all orientations/JFIF/privacy/alpha/rendering PASS", flush=True)


def make_fixtures(directory):
    directory.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(26)
    ImageFile.MAXBLOCK = 192 * MIB
    cases = []
    def add(name, inputs, qualities=("standard", "original"), expected=200):
        for quality in qualities:
            cases.append({"Name": name, "Inputs": [str(directory / path) for path in inputs],
                          "Quality": quality, "Expected": expected})
    for w, h in ((6000, 4000), (8064, 6048)):
        for progressive in (False, True):
            name = f"jpeg-{w}x{h}-" + ("progressive" if progressive else "baseline")
            path = directory / (name + ".jpg")
            if not path.exists():
                Image.new("RGB", (w, h), (70, 100, 200)).save(path, quality=90, subsampling=2, progressive=progressive)
            add(name, [path.name])
    for w, h in ((4000, 3000), (3000, 4000)):
        for high in (False, True):
            name = f"png-{w}x{h}-rgba-" + ("high" if high else "low")
            path = directory / (name + ".png")
            if not path.exists():
                source = Image.fromarray(rng.integers(0, 256, (h, w, 4), dtype=np.uint8)) if high else Image.new("RGBA", (w, h), (70, 100, 200, 128))
                source.save(path, compress_level=6)
            add(name, [path.name])
    path = directory / "jpeg-near-50mib.jpg"
    if not path.exists():
        source = Image.fromarray(rng.integers(0, 256, (4000, 4500, 3), dtype=np.uint8))
        source.save(path, quality=100, subsampling=0, optimize=True)
        if path.stat().st_size > 50 * MIB:
            source.crop((0, 0, 4500, int(4000 * 49 * MIB / path.stat().st_size))).save(path, quality=100, subsampling=0, optimize=True)
    assert 47 * MIB < path.stat().st_size <= 50 * MIB
    add("jpeg-near-50mib", [path.name], qualities=("original",))
    # A rotation needs destination coefficient arrays as well as the source arrays.
    for name, source_name, qualities in (
        ("jpeg-48mp-progressive-rotate90", "jpeg-8064x6048-progressive.jpg", ("standard", "original")),
        ("jpeg-near-50mib-rotate90", "jpeg-near-50mib.jpg", ("original",))):
        target = directory / (name + ".jpg")
        if not target.exists():
            original = (directory / source_name).read_bytes()
            exif = Image.Exif(); exif[274] = 6; payload = exif.tobytes()
            target.write_bytes(original[:2] + b"\xff\xe1" + struct.pack(">H", len(payload)+2) + payload + original[2:])
        add(name, [target.name], qualities=qualities)
    for portrait in (False, True):
        name = "photo-portrait.png" if portrait else "photo.png"
        path = directory / name
        if not path.exists():
            noise = np.random.default_rng(2603).integers(0, 256, (3000, 4000, 3), dtype=np.uint8)
            source = Image.fromarray(noise).filter(ImageFilter.GaussianBlur(2))
            if portrait: source = source.transpose(Image.Transpose.TRANSPOSE)
            source.save(path, compress_level=6)
    gradient = directory / "gradient.png"
    if not gradient.exists():
        y, x = np.indices((3000, 4000), dtype=np.int32)
        source = Image.fromarray(np.stack((x*255//3999, y*255//2999, (x+y)*255//6998), axis=2).astype(np.uint8))
        source.save(gradient, compress_level=6)
    add("gradient", [gradient.name])
    add("photo-pair", ["photo.png", "photo-portrait.png"])
    add("photo-mixed", ["photo.png", "photo-portrait.png", "gradient.png", "jpeg-6000x4000-baseline.jpg"])
    add("photo-over-output", ["photo.png", "photo-portrait.png", "photo.png"], expected=422)
    (directory / "manifest.json").write_text(json.dumps(cases, indent=2))
    return cases


class Monitor:
    def __init__(self, api):
        self.pid = int(smoke.docker("inspect", "--format", "{{.State.Pid}}", api.name).stdout)
        self.root = Path(f"/proc/{self.pid}/root")
        self.cg = self.root / "sys/fs/cgroup"
        assert (self.cg / "memory.swap.max").read_text().strip() == "0"
        self.stop = threading.Event(); self.peak = {}; self.processes = {}; self.samples = 0; self.error = None
    def loop(self):
        while not self.stop.wait(.02):
            try:
                st = os.statvfs(self.root / "tmp")
                values = {"tmpfs_bytes": (st.f_blocks - st.f_bfree) * st.f_frsize}
                jobs = self.root / "tmp/amane-pdf-api"
                values["job_bytes_single"] = max((sum(p.stat().st_blocks*512 for p in job.rglob("*") if p.is_file())
                    for job in jobs.glob("*") if job.is_dir()), default=0)
                for pid in (self.cg / "cgroup.procs").read_text().split():
                    proc = Path("/proc") / pid; name = (proc / "comm").read_text().strip()
                    if name not in ("djpeg", "cjpeg", "jpegtran", "pdfcpu", "qpdf"): continue
                    status = (proc / "status").read_text()
                    match = re.search(r"^VmHWM:\s+(\d+) kB", status, re.M)
                    if match: values[name + "_rss_bytes"] = int(match[1])*1024
                    record = self.processes.setdefault(pid, {"tool": name, "first": time.monotonic(), "last": 0})
                    record["last"] = time.monotonic()
                    for key, prefix in (("as", "Max address space"), ("fsize", "Max file size"), ("core", "Max core file size")):
                        line = next(line for line in (proc / "limits").read_text().splitlines() if line.startswith(prefix))
                        value = line[len(prefix):].split()[0]
                        assert value != "unlimited" and (key != "core" or value == "0")
                        values[name + "_" + key] = int(value)
                for key, value in values.items(): self.peak[key] = max(value, self.peak.get(key, 0))
                self.samples += 1
            except (FileNotFoundError, ProcessLookupError): pass
            except Exception as error: self.error = error; break
    def finish(self):
        self.stop.set(); self.thread.join()
        if self.error: raise self.error
        self.peak.update(memory_peak=int((self.cg / "memory.peak").read_text()), samples=self.samples,
            memory_events={k: int(v) for k, v in (line.split() for line in (self.cg / "memory.events").read_text().splitlines())},
            tool_seconds_sampled={name: round(sum(p["last"] - p["first"] for p in self.processes.values() if p["tool"] == name), 3)
                                  for name in ("djpeg", "cjpeg", "jpegtran", "pdfcpu", "qpdf")})
        assert self.samples and self.peak["memory_events"]["oom"] == self.peak["memory_events"]["oom_kill"] == 0
        assert self.peak.get("job_bytes_single", 0) <= 124 * MIB
        return self.peak


def capacity(args):
    assert os.geteuid() == 0, "--capacity needs host root to read container /proc; use sudo."
    cases = select_cases(make_fixtures(args.fixtures), args)
    args.output.mkdir(parents=True, exist_ok=True)
    results_path = args.output / "http-resources.json"
    results = json.loads(results_path.read_text()) if results_path.exists() else []
    for case in cases:
        inputs = [Path(path).read_bytes() for path in case["Inputs"]]
        assert sum(map(len, inputs)) <= 50 * MIB
        for concurrent in (1, 2):
            with smoke.running_container(args.image, isolated=True) as api:
                monitor = Monitor(api); monitor.thread = threading.Thread(target=monitor.loop); monitor.thread.start()
                try:
                    with concurrent_futures(concurrent) as pool:
                        values = list(pool.map(lambda _: request(api, inputs, case["Quality"], case["Expected"]), range(concurrent)))
                finally: metrics = monitor.finish()
                api.assert_clean()
                assert api.request("GET", "/healthz")[0] == 200
                assert not any(x in api.exec("ps", "-eo", "comm").stdout.decode().splitlines()
                               for x in ("qpdf", "pdfcpu", "prlimit", "djpeg", "cjpeg", "jpegtran"))
                for evidence, pdf in values:
                    if evidence["status"] == 200:
                        path = args.output / "validation.pdf"; path.write_bytes(pdf)
                        assert len(inspect_pdf(path)["pages"]) == len(inputs)
                        path.unlink()
                    assert evidence["seconds"] < 30
                row = dict(name=case["Name"], quality=case["Quality"], concurrent=concurrent,
                           input_bytes=sum(map(len, inputs)), requests=[value[0] for value in values], **metrics)
                results = [r for r in results if (r["name"], r["quality"], r["concurrent"]) !=
                           (row["name"], row["quality"], row["concurrent"])] + [row]
                results_path.write_text(json.dumps(results, indent=2))
                print(json.dumps(row), flush=True)


def concurrent_futures(workers):
    return concurrent.futures.ThreadPoolExecutor(max_workers=workers)


def select_cases(cases, args):
    selected = [row for row in cases if (not args.cases or row["Name"] in args.cases) and
                (not args.qualities or row["Quality"] in args.qualities)]
    assert selected, "No capacity cases matched."
    return selected


def reservations(args):
    # Reuse the established test harness only in a validation container. It is not part of the product image.
    spec = importlib.util.spec_from_file_location("overlay", ROOT / "scripts/overlay-validation.py")
    overlay = importlib.util.module_from_spec(spec); spec.loader.exec_module(overlay)
    sdk = Path(command("dotnet", "--list-sdks").stdout.decode().splitlines()[-1].split("[")[1].rstrip("]")) / command("dotnet", "--version").stdout.decode().strip()
    manifest = select_cases(json.loads((args.fixtures / "manifest.json").read_text()), args)
    for row in manifest: row["Inputs"] = ["/fixture/" + Path(path).name for path in row["Inputs"]]
    (args.fixtures / "container-manifest.json").write_text(json.dumps(manifest))
    args.fixtures.chmod(0o755)
    for path in args.fixtures.iterdir(): path.chmod(0o644)
    container = overlay.Container(args.image, sdk, [(args.fixtures.resolve(), "/fixture")],
        {"FROM_IMAGES_CAPACITY_MANIFEST": "/fixture/container-manifest.json",
         "FROM_IMAGES_CAPACITY_RESULTS": "/tmp/results/reservations.json", "DOTNET_CLI_HOME": "/tmp/cli"})
    try:
        print(container.tests("FullyQualifiedName~MeasureActualReservations_WithSyntheticInputs").stdout.decode(), flush=True)
        args.output.mkdir(parents=True, exist_ok=True)
        result_path = args.output / "reservations.json"
        prior = json.loads(result_path.read_text()) if result_path.exists() else []
        measured = json.loads(container.exec("cat", "/tmp/results/reservations.json").stdout)
        keys = {(row["Name"], row["Quality"]) for row in measured}
        result_path.write_text(json.dumps([row for row in prior if (row["Name"], row["Quality"]) not in keys] + measured, indent=2))
        container.stopped()
    finally: container.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--capacity", action="store_true")
    parser.add_argument("--reservations", action="store_true")
    parser.add_argument("--fixtures", type=Path, default=Path("/tmp/from-images-fixtures"))
    parser.add_argument("--output", type=Path, default=Path("/tmp/from-images-results"))
    parser.add_argument("--cases", nargs="+")
    parser.add_argument("--qualities", choices=("standard", "original"), nargs="+")
    args = parser.parse_args()
    if args.capacity: capacity(args)
    elif args.reservations: reservations(args)
    else: render(args.image)
