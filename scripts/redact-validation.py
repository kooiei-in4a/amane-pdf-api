#!/usr/bin/env python3
"""黒塗りAPIのPoppler比較と、30 MiB/30ページ・結合同時負荷の測定。"""
import argparse
import base64
import concurrent.futures
import copy
import importlib.util
import json
import os
from pathlib import Path
import random
import subprocess
import tempfile
import time
import zlib

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("smoke", ROOT / "scripts/docker-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, timeout=120)


def form(pdf, regions):
    body, content_type = smoke.multipart(pdf, password=None)
    boundary = content_type.split("boundary=")[1]
    tail = f"--{boundary}--\r\n".encode()
    field = (f'--{boundary}\r\nContent-Disposition: form-data; name="regions"\r\n\r\n'.encode()
             + json.dumps(regions, separators=(",", ":")).encode() + b"\r\n")
    return body[:-len(tail)] + field + tail, content_type


def region(page):
    return dict(page=page, x=.4, y=.3, width=.2, height=.1)


def no_renderer(api):
    script = """for path in /proc/[0-9]*/cmdline; do
{ IFS= read -r -d '' exe; IFS= read -r -d '' dll; IFS= read -r -d '' cmd; } < "$path" 2>/dev/null || continue
if [[ "$dll" == */Amane.Pdf.Renderer.dll && "$cmd" == render-jpeg ]]; then exit 1; fi
done
exit 0"""
    api.exec("bash", "-c", script)


def geometry(image, work, artifacts):
    source = work / "geometry.json"
    command("python3", "-I", str(ROOT / "docs/redact-design-probe/gen_geometry.py"), str(source))
    pdf = work / "geometry.pdf"
    command("qpdf", "--json-input", str(source), str(pdf))
    with smoke.running_container(image, isolated=True) as api:
        body, content_type = form(pdf.read_bytes(), [region(n) for n in [6, 4, 2, 1, 5, 3]])
        status, headers, result = api.request("POST", "/api/pdf/redact", body, content_type)
        assert status == 200, (status, result[:500])
        assert headers["X-Pdf-Redacted-Pages"] == headers["X-Pdf-Rasterized-Pages"] == "1,2,3,4,5,6"
        assert "filename=redacted.pdf" in headers["Content-Disposition"]
        api.assert_clean(); no_renderer(api)
    output = work / "redacted.pdf"; output.write_bytes(result)
    command("qpdf", "--check", str(output))
    montage = Image.new("RGB", (900, 720), "white"); draw = ImageDraw.Draw(montage)
    colors = np.array([[0, 0, 255], [255, 255, 0], [255, 0, 0], [0, 255, 0]])
    cases = [(500, 600, 0), (600, 500, 90), (500, 600, 180), (600, 500, 270), (800, 600, 90), (300, 400, 0)]
    for index, (width, height, rotation) in enumerate(cases):
        prefix = work / f"page-{index + 1}"
        command("pdftoppm", "-r", "72", "-f", str(index + 1), "-l", str(index + 1), "-singlefile", "-png", str(output), str(prefix))
        picture = Image.open(str(prefix) + ".png").convert("RGB")
        assert picture.size == (width, height)
        pixels = np.asarray(picture).astype(int)
        order = {0: [0, 1, 2, 3], 90: [2, 0, 3, 1], 180: [3, 2, 1, 0], 270: [1, 3, 0, 2]}[rotation]
        actual = pixels[[15, 15, height - 16, height - 16], [15, width - 16, 15, width - 16]]
        assert np.max(np.abs(actual - colors[order])) <= 8
        left, top, right, bottom = int(width * .4), int(height * .3), int(width * .6), int(height * .4)
        assert np.max(pixels[top + 2:bottom - 2, left + 2:right - 2]) <= 5
        cx, cy = (left + right) // 2, (top + bottom) // 2
        outer = pixels[[cy, cy, top - 3, bottom + 2], [left - 3, right + 2, cx, cx]]
        assert np.max(np.abs(outer - 204)) <= 8
        picture.thumbnail((270, 300))
        x, y = index % 3 * 300, index // 3 * 360
        montage.paste(picture, (x + 15, y + 30))
        label = f"API / Rotate {rotation} / CropBox" if index < 4 else ("API / UserUnit 2" if index == 4 else "API / CropBox outside MediaBox")
        draw.text((x + 10, y + 8), label, fill="black")
    if artifacts:
        artifacts.mkdir(parents=True, exist_ok=True)
        montage.save(artifacts / "redact-geometry.png")
    print("Docker redact: HTTP/headers/Rotate/CropBox/UserUnit/Poppler pixels/cleanup PASS", flush=True)


def generate_capacity(image, work):
    work.chmod(0o755)
    # Thirty pages share one embedded Japanese font and one 300dpi scan.
    blank = json.loads((ROOT / "tests/Amane.Pdf.Api.Tests/Fixtures/redact-blank2.json").read_text())
    objects = blank["qpdf"][1]
    for key in list(objects):
        if key not in ("obj:1 0 R", "obj:2 0 R", "trailer"): del objects[key]
    objects["obj:2 0 R"]["value"].update({"/Count": 30, "/Kids": [f"{n+3} 0 R" for n in range(30)]})
    for n in range(30):
        objects[f"obj:{n+3} 0 R"] = {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842], "/Resources": {}}}
    objects["trailer"]["value"]["/Size"] = 33
    (work / "blank.json").write_text(json.dumps(blank))
    layer = {"origin": "LowerLeft", "pages": {str(n): {"content": {"text": [{"value": f"鈴木一郎 個人番号 9876 {n}",
                 "pos": [50, 760], "font": {"name": "BIZUDPGothic-Regular", "size": 24}}]}} for n in range(1, 31)}}
    (work / "layer.json").write_text(json.dumps(layer, ensure_ascii=False))
    script = work / "prepare.sh"
    script.write_text("""set -eu
mkdir -p /tmp/home
qpdf --json-input /w/blank.json /w/blank.pdf
env HOME=/tmp/home GOMEMLIMIT=200MiB GOMAXPROCS=1 /opt/amane-pdf/bin/pdfcpu -c /opt/amane-pdf/config --offline create /w/layer.json /w/blank.pdf /w/shared.pdf >/dev/null 2>&1
qpdf --json=2 --json-key=qpdf --json-key=pages --json-stream-data=inline --decode-level=none /w/shared.pdf /w/shared.json
""")
    def tools(*args):
        return command("docker", "run", "--rm", "--network", "none", "--user", f"{os.getuid()}:{os.getgid()}",
                       "-v", f"{work}:/w", "--entrypoint", *args)
    tools("sh", image, "/w/prepare.sh")
    command("python3", "-I", str(ROOT / "docs/redact-design-probe/gen_perf.py"), str(work))
    tools("cjpeg", image, "-quality", "90", "-outfile", "/w/scan.jpg", "/w/scan.ppm")
    doc = json.loads((work / "shared.json").read_text()); objects = doc["qpdf"][1]
    maximum = doc["qpdf"][0]["maxobjectid"]; image_id, content_id, padding_id = maximum + 1, maximum + 2, maximum + 3
    def value(raw):
        while isinstance(raw, str) and raw.endswith(" R"): raw = objects["obj:" + raw]["value"]
        return raw
    for entry in doc["pages"]:
        page = objects["obj:" + entry["object"]]["value"]
        resources = copy.deepcopy(value(page.get("/Resources", {})))
        resources.setdefault("/XObject", {})["/ImScan"] = f"{image_id} 0 R"
        page["/Resources"] = resources
        current = page.get("/Contents", [])
        page["/Contents"] = [f"{content_id} 0 R", *(current if isinstance(current, list) else [current])]
    objects[f"obj:{image_id} 0 R"] = {"stream": {"dict": {"/Type": "/XObject", "/Subtype": "/Image", "/Width": 2480, "/Height": 3508,
        "/ColorSpace": "/DeviceRGB", "/BitsPerComponent": 8, "/Filter": "/DCTDecode"}, "datafile": "/w/scan.jpg"}}
    objects[f"obj:{content_id} 0 R"] = {"stream": {"dict": {}, "data": base64.b64encode(b"q 595 0 0 842 0 0 cm /ImScan Do Q").decode()}}
    catalog = value(objects["trailer"]["value"]["/Root"])
    catalog["/SyntheticPadding"] = f"{padding_id} 0 R"
    objects[f"obj:{padding_id} 0 R"] = {"stream": {"dict": {"/Filter": "/FlateDecode"}, "datafile": "/w/padding.zlib"}}
    objects["trailer"]["value"]["/Size"] = padding_id + 1
    doc["qpdf"][0]["maxobjectid"] = padding_id
    (work / "capacity.json").write_text(json.dumps(doc))
    noise = random.Random(35).randbytes(31 * 1048576)
    def exact(json_name, pdf_name, target):
        amount = target - 2 * 1048576 if json_name == "capacity.json" else target - 4096
        for _ in range(6):
            (work / "padding.zlib").write_bytes(zlib.compress(noise[:amount], 0))
            tools("qpdf", image, "--json-input", "/w/" + json_name, "/w/" + pdf_name)
            actual = (work / pdf_name).stat().st_size
            if actual == target: return
            amount += target - actual
        raise AssertionError((target, actual))
    exact("capacity.json", "capacity30.pdf", 30 * 1048576)
    merge = {"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, {
        "obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
        "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": 1, "/Kids": ["3 0 R"]}},
        "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842],
                               "/Resources": {}, "/PieceInfo": {"/Synthetic": {"/Private": "4 0 R"}}}},
        "obj:4 0 R": {"stream": {"dict": {"/Filter": "/FlateDecode"}, "datafile": "/w/padding.zlib"}},
        "trailer": {"value": {"/Root": "1 0 R", "/Size": 5}}}]}
    (work / "merge.json").write_text(json.dumps(merge))
    exact("merge.json", "merge25.pdf", 25 * 1048576)
    return (work / "capacity30.pdf").read_bytes(), (work / "merge25.pdf").read_bytes()


def capacity(image, work, artifacts):
    source, merge_source = generate_capacity(image, work)
    regions = [dict(page=n, x=0, y=.07, width=1, height=.08) for n in range(1, 21)]
    redact_body, redact_type = form(source, regions)
    merge_body, merge_type = smoke.multipart(files=[merge_source, merge_source], password=None)
    monitor_binary = work / "measure-tmpfs"
    command("gcc", "-std=c17", "-O2", "-Wall", "-Wextra", "-Werror", str(ROOT / "scripts/measure-tmpfs.c"), "-o", str(monitor_binary))
    results = []
    for with_merge in (False, True):
        with smoke.running_container(image, isolated=True, mounts=((monitor_binary, "/validation/measure-tmpfs"),)) as api:
            smoke.docker("exec", "--detach", api.name, "/validation/measure-tmpfs", "/tmp/measurement.stop", "/tmp/measurement.result")
            def request(path, body, content_type):
                started = time.monotonic()
                status, headers, output = api.request("POST", path, body, content_type)
                return dict(status=status, headers=headers, output_bytes=len(output), milliseconds=round((time.monotonic() - started) * 1000))
            try:
                with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
                    redact = pool.submit(request, "/api/pdf/redact", redact_body, redact_type)
                    merge = None
                    if with_merge:
                        # Start the 50 MiB merge while the thirty-page render loop is active.
                        for _ in range(200):
                            probe = smoke.docker("exec", api.name, "sh", "-c", 'set -- /tmp/amane-pdf-api/*/redact-page-*.jpg; test -f "$1"', check=False)
                            if probe.returncode == 0: break
                            if redact.done(): raise AssertionError("redact completed before overlap")
                            time.sleep(.025)
                        else: raise AssertionError("render phase was not observed")
                        merge = pool.submit(request, "/api/pdf/merge", merge_body, merge_type)
                    redacted = redact.result()
                    merged = merge.result() if merge else None
                assert redacted["status"] == 200, redacted
                assert redacted["headers"]["X-Pdf-Redacted-Pages"] == ",".join(map(str, range(1, 21)))
                assert redacted["headers"]["X-Pdf-Rasterized-Pages"] == ",".join(map(str, range(1, 31)))
                if merged: assert merged["status"] == 200 and merged["output_bytes"] > 49 * 1048576, merged
            finally:
                api.exec("touch", "/tmp/measurement.stop")
                for _ in range(100):
                    if smoke.docker("exec", api.name, "test", "-s", "/tmp/measurement.result", check=False).returncode == 0: break
                    time.sleep(.01)
                peak, sample_count = map(int, api.exec("cat", "/tmp/measurement.result").stdout.split())
                api.exec("rm", "/tmp/measurement.stop", "/tmp/measurement.result")
            api.assert_clean(); no_renderer(api)
            events = dict(line.split() for line in api.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())
            assert int(events["oom"]) == int(events["oom_kill"]) == 0
            result = dict(concurrent_merge=with_merge, redact_input_bytes=len(source), merge_input_bytes=len(merge_source) * 2,
                          redact=redacted, merge=merged, memory_peak_bytes=int(api.exec("cat", "/sys/fs/cgroup/memory.peak").stdout),
                          tmpfs_peak_sampled_bytes=peak, sample_interval_ms=1, samples=sample_count, memory_events=events)
            results.append(result); print(json.dumps(result), flush=True)
    if artifacts:
        artifacts.mkdir(parents=True, exist_ok=True)
        (artifacts / "redact-capacity.json").write_text(json.dumps(results, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--artifacts", type=Path)
    parser.add_argument("--capacity", action="store_true")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="redact-validation-") as directory:
        work = Path(directory)
        geometry(args.image, work, args.artifacts)
        if args.capacity: capacity(args.image, work, args.artifacts)


if __name__ == "__main__":
    main()
