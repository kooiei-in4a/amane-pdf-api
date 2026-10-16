#!/usr/bin/env python3
"""固定PDFiumのDocker検証。Pillow/NumPyは検証側だけで使用する。"""
import argparse
import hashlib
import io
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid

import numpy as np
from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
TOOLS = "/opt/amane-pdf"
RENDERER = ["dotnet", TOOLS + "/renderer/Amane.Pdf.Renderer.dll"]
LIBRARY = ["--pdfium", TOOLS + "/lib/libpdfium.so", "--font-dir", TOOLS + "/fonts"]
LIMITED = ["/usr/bin/env", "--ignore-signal=XFSZ", "--", "prlimit", "--core=0:0",
           "--data=402653184:402653184", "--fsize=31457281:31457281", "--"]
HARDENED = ["--network", "none", "--read-only", "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=256m",
            "--cpus", "1", "--memory", "1536m", "--memory-swap", "1536m",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true", "-e", "DOTNET_EnableDiagnostics=0"]


def run(*args, check=True):
    return subprocess.run(args, capture_output=True, check=check, timeout=60)


def docker(*args, check=True):
    return run("docker", *args, check=check)


def render_args(source, page, width, height, expected, output, budget=30 * 1048576, fill=None):
    args = [*LIMITED, *RENDERER, "render-jpeg", *LIBRARY, "--input", source, "--page", str(page),
            "--expect-size", expected, "--width", str(width), "--height", str(height),
            "--quality", "85", "--max-bytes", str(budget), "--output", output]
    if fill:
        args += ["--fill", ",".join(map(str, fill))]
    return args


def check_image(data, width, height, rotation, fill):
    image = Image.open(io.BytesIO(data)).convert("RGB")
    assert image.size == (width, height)
    pixels = np.asarray(image).astype(int)
    colors = np.array([[0, 0, 255], [255, 255, 0], [255, 0, 0], [0, 255, 0]])
    order = {0: [0, 1, 2, 3], 90: [2, 0, 3, 1], 180: [3, 2, 1, 0], 270: [1, 3, 0, 2]}[rotation]
    actual = pixels[[15, 15, height - 16, height - 16], [15, width - 16, 15, width - 16]]
    assert np.max(np.abs(actual - colors[order])) <= 8
    left, top, right, bottom = fill
    assert np.max(pixels[top + 2:bottom - 2, left + 2:right - 2]) <= 3
    cx, cy = (left + right) // 2, (top + bottom) // 2
    outer = pixels[[cy, cy, top - 3, bottom + 2], [left - 3, right + 2, cx, cx]]
    assert np.max(np.abs(outer - 204)) <= 5
    return image


def validate(image, work, artifacts):
    name = "amane-renderer-" + uuid.uuid4().hex
    docker("run", "--detach", "--name", name, *HARDENED, "-v", f"{work}:/inputs:ro",
           "--entrypoint", "sleep", image, "120")
    try:
        def execute(*args, check=True):
            return docker("exec", name, *args, check=check)

        result = execute(*LIMITED, *RENDERER, "self-test", *LIBRARY)
        assert result.stdout == result.stderr == b""
        execute("qpdf", "--json-input", "/inputs/geometry.json", "/tmp/geometry.pdf")
        sizes = [(500, 600, 0), (600, 500, 90), (500, 600, 180), (600, 500, 270),
                 (400, 300, 90), (300, 400, 0)]
        montage = Image.new("RGB", (900, 720), "white")
        draw = ImageDraw.Draw(montage)
        for index, (wpt, hpt, rotation) in enumerate(sizes):
            width, height = wpt * 2, hpt * 2
            fill = [math.floor(width * .4), math.floor(height * .3), math.ceil(width * .6), math.ceil(height * .4)]
            output = f"/tmp/geometry-{index}.jpg"
            result = execute(*render_args("/tmp/geometry.pdf", index + 1, width, height, f"{wpt},{hpt}", output, fill=fill))
            assert result.stdout == result.stderr == b""
            assert execute("stat", "-c", "%a", output).stdout.strip() == b"600"
            picture = check_image(execute("cat", output).stdout, width, height, rotation, fill)
            picture.thumbnail((270, 300))
            x, y = (index % 3) * 300, (index // 3) * 360
            montage.paste(picture, (x + 15, y + 30))
            label = f"Rotate {rotation} / shifted CropBox" if index < 4 else ("UserUnit 2 / Rotate 90" if index == 4 else "CropBox outside MediaBox")
            draw.text((x + 10, y + 8), label, fill="black")
        if artifacts:
            artifacts.mkdir(parents=True, exist_ok=True)
            montage.save(artifacts / "renderer-geometry.png")

        for args, code in [(render_args("/tmp/geometry.pdf", 1, 1000, 1200, "501,600", "/tmp/mismatch.jpg"), 12),
                           (render_args("/tmp/geometry.pdf", 1, 1000, 1200, "500,600", "/tmp/overflow.jpg", budget=1), 10)]:
            result = execute(*args, check=False)
            assert result.returncode == code and result.stdout == result.stderr == b""
        assert execute("test", "!", "-e", "/tmp/overflow.jpg", check=False).returncode == 0
        assert execute("test", "!", "-e", "/tmp/mismatch.jpg", check=False).returncode == 0

        execute(*render_args("/tmp/geometry.pdf", 7, 1190, 1684, "595,842", "/tmp/japanese.jpg"))
        japanese = np.asarray(Image.open(io.BytesIO(execute("cat", "/tmp/japanese.jpg").stdout)).convert("RGB"))
        assert np.count_nonzero(np.max(japanese[300:600, 60:900], axis=2) < 128) > 5000
        lock = json.loads((REPO / "third_party/pdfium.lock").read_text())
        assert execute("stat", "-c", "%a", TOOLS + "/lib/libpdfium.so").stdout.strip() == b"444"
        for member in lock["license_members"]:
            assert execute("test", "-s", TOOLS + "/licenses/pdfium/" + member).returncode == 0
        font = execute("cat", TOOLS + "/fonts/BIZUDPGothic-Regular.ttf").stdout
        assert hashlib.sha256(font).hexdigest() == json.loads((REPO / "third_party/pdfcpu.lock").read_text())["font_sha256"]
        execute("mkdir", "/tmp/empty-fonts")
        result = execute(*LIMITED, *RENDERER, "self-test", "--pdfium", TOOLS + "/lib/libpdfium.so", "--font-dir", "/tmp/empty-fonts", check=False)
        assert result.returncode == 20 and result.stdout == result.stderr == b""
        print("Docker renderer: geometry/fill/Japanese/size/overflow/0600/locked font/licenses PASS", flush=True)
    finally:
        docker("rm", "-f", name)

    result = docker("run", "--rm", *HARDENED, "-e", "Pdf__RenderFontDir=/tmp", image, check=False)
    assert result.returncode == 1
    logs = (result.stdout + result.stderr).decode()
    assert "描画プログラム（PDFium）の自己テストに失敗しました。" in logs
    assert "Now listening" not in logs and "/tmp" not in logs and "Exception" not in logs
    print("Docker renderer: missing Japanese font prevents API startup with exit 1 PASS", flush=True)


def measure(image, work):
    # New cgroup per sample; renderer, shell, page cache and tmpfs are all counted.
    script = work / "measure.sh"
    script.write_text("""#!/bin/sh
set -eu
qpdf --json-input /inputs/scan-page.json /tmp/scan.pdf
s=$(date +%s%N)
/usr/bin/env --ignore-signal=XFSZ -- prlimit --core=0:0 --data=402653184:402653184 --fsize=31457281:31457281 -- dotnet /opt/amane-pdf/renderer/Amane.Pdf.Renderer.dll render-jpeg --pdfium /opt/amane-pdf/lib/libpdfium.so --font-dir /opt/amane-pdf/fonts --input /tmp/scan.pdf --page 1 --expect-size 595.276,841.89 --width "$1" --height "$2" --quality 85 --max-bytes 31457280 --output /tmp/rendered.jpg
e=$(date +%s%N)
printf '%s %s %s\\n' "$(( (e-s)/1000000 ))" "$(cat /sys/fs/cgroup/memory.peak)" "$(stat -c %s /tmp/rendered.jpg)"
""")
    samples = []
    for width, height in [(1654, 2339), (4000, 4000)]:
        for iteration in range(3):
            result = docker("run", "--rm", *HARDENED, "-v", f"{work}:/inputs:ro", "--entrypoint", "sh", image, "/inputs/measure.sh", str(width), str(height))
            assert result.stderr == b""
            milliseconds, peak, size = map(int, result.stdout.split())
            samples.append(dict(width=width, height=height, iteration=iteration + 1,
                                milliseconds=milliseconds, memory_peak_bytes=peak, jpeg_bytes=size))
    print(json.dumps(samples, indent=2), flush=True)
    return samples


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--artifacts", type=Path)
    parser.add_argument("--measure", action="store_true")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="renderer-validation-") as temporary:
        work = Path(temporary)
        work.chmod(0o755)
        run(sys.executable, "-I", str(REPO / "docs/redact-design-probe/gen_geometry.py"), str(work / "geometry.json"))
        validate(args.image, work, args.artifacts)
        if args.measure:
            run(sys.executable, "-I", str(REPO / "docs/redact-design-probe/gen_perf.py"), str(work))
            docker("run", "--rm", "--network", "none", "-v", f"{work}:/inputs", "--user", f"{os.getuid()}:{os.getgid()}",
                   "--entrypoint", "cjpeg", args.image, "-quality", "90", "-outfile", "/inputs/scan.jpg", "/inputs/scan.ppm")
            run(sys.executable, "-I", str(REPO / "docs/redact-design-probe/mkpage.py"), str(work / "scan.jpg"),
                str(work / "scan-page.json"), "595.276", "841.89", "/inputs/scan.jpg")
            samples = measure(args.image, work)
            if args.artifacts:
                (args.artifacts / "renderer-measurements.json").write_text(json.dumps(samples, indent=2) + "\n")


if __name__ == "__main__":
    main()
