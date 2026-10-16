#!/usr/bin/env python3
"""合成画像でPDFiumの境界を実測する。Pillow/encoderは検証hostだけに置く。"""
import argparse
import base64
import json
from pathlib import Path
import subprocess

from PIL import Image, ImageDraw

Image.MAX_IMAGE_PIXELS = None


def run(args, **kwargs):
    return subprocess.run(args, check=True, capture_output=True, timeout=300, **kwargs)


def page(path, data, width, height, filter_name, components, alpha=False):
    objects = {
        "obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
        "obj:2 0 R": {"value": {"/Type": "/Pages", "/Kids": ["3 0 R"], "/Count": 1}},
        "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 1440, 1440],
                               "/Resources": {"/XObject": {"/Im0": "5 0 R"}}, "/Contents": "4 0 R"}},
        "obj:4 0 R": {"stream": {"dict": {}, "data": base64.b64encode(b"0 0 1 rg 0 0 1440 1440 re f q 1440 0 0 1440 0 0 cm /Im0 Do Q").decode()}},
        "obj:5 0 R": {"stream": {"dict": {"/Type": "/XObject", "/Subtype": "/Image", "/Width": width, "/Height": height,
                                           "/ColorSpace": "/DeviceRGB" if components == 3 else "/DeviceCMYK" if components == 4 else "/DeviceGray",
                                           "/BitsPerComponent": 1 if filter_name == "/JBIG2Decode" else 8, "/Filter": filter_name},
                                    "datafile": "/inputs/" + data.name}},
        "trailer": {"value": {"/Root": "1 0 R", "/Size": 6}}}
    if components == 4:
        objects["obj:5 0 R"]["stream"]["dict"]["/Decode"] = [1, 0] * 4
    if alpha:
        objects["obj:5 0 R"]["stream"]["dict"]["/SMaskInData"] = 1
    path.write_text(json.dumps({"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objects]}))


def generate(work, name, width, height, mode, filter_name, progressive=False, encoder=None):
    suffix = {"JPEG": ".jpg", "JPEG2000": ".jp2", "JBIG2": ".jb2"}[filter_name]
    encoded = work / (name + suffix)
    components = 3 if mode in ("RGB", "RGBA") else 4 if mode == "CMYK" else 1
    picture = Image.new(mode, (width, height), (128, 128, 128, 255) if mode == "RGBA" else (0, 0, 0, 127) if mode == "CMYK" else (128, 128, 128) if mode == "RGB" else 128 if mode == "L" else 1)
    # The center is dark, and the outside is gray/white. An omitted image exposes blue.
    draw = ImageDraw.Draw(picture)
    draw.rectangle((width // 3, height // 3, width * 2 // 3, height * 2 // 3),
                   fill=(32, 32, 32, 255) if mode == "RGBA" else (0, 0, 0, 223) if mode == "CMYK" else (32, 32, 32) if mode == "RGB" else 32 if mode == "L" else 0)
    if filter_name == "JPEG":
        picture.save(encoded, "JPEG", quality=85, progressive=progressive, subsampling=0)
        native_filter = "/DCTDecode"
    elif filter_name == "JPEG2000":
        picture.save(encoded, "JPEG2000", irreversible=False)
        native_filter = "/JPXDecode"
    else:
        if not encoder:
            raise ValueError("--jbig2-encoder is required")
        pbm = work / (name + ".pbm")
        picture.save(pbm)
        result = run([encoder, "-p", "-d", str(pbm)])
        encoded.write_bytes(result.stdout)
        pbm.unlink()
        native_filter = "/JBIG2Decode"
    picture.close()
    page(work / (name + ".json"), encoded, width, height, native_filter, components, mode == "RGBA")
    return dict(name=name, mode=mode, width=width, height=height, samples=width * height * components,
                filter=native_filter, progressive=progressive, encoded_bytes=encoded.stat().st_size)


def measure(image, work, spec, data_limit=384 * 1048576):
    script = work / "measure.sh"
    script.write_text("""#!/bin/sh
set -eu
qpdf --json-input "/inputs/$1.json" /tmp/input.pdf
s=$(date +%s%N)
set +e
env DOTNET_EnableDiagnostics=0 /usr/bin/env --ignore-signal=XFSZ -- prlimit --core=0:0 --data=402653184:402653184 --fsize=31457281:31457281 -- dotnet /opt/amane-pdf/renderer/Amane.Pdf.Renderer.dll render-jpeg --pdfium /opt/amane-pdf/lib/libpdfium.so --font-dir /opt/amane-pdf/fonts --input /tmp/input.pdf --page 1 --expect-size 1440,1440 --width 4000 --height 4000 --quality 85 --max-bytes 31457280 --fill 100,100,300,200 --output /tmp/rendered.jpg
rc=$?
set -e
e=$(date +%s%N)
if [ "$rc" -eq 0 ]; then
  rgb=$(djpeg -pnm -rgb -scale 1/8 /tmp/rendered.jpg | dd iflag=skip_bytes,count_bytes skip=375765 count=3 status=none | od -An -tu1)
  outside=$(djpeg -pnm -rgb -scale 1/8 /tmp/rendered.jpg | dd iflag=skip_bytes,count_bytes skip=187890 count=3 status=none | od -An -tu1)
  size=$(stat -c %s /tmp/rendered.jpg)
else
  rgb="0 0 0"
  outside="0 0 0"
  size=0
fi
printf '%s %s %s %s %s\\n' "$rc" "$(( (e-s)/1000000 ))" "$(cat /sys/fs/cgroup/memory.peak)" "$size" "$rgb $outside"
""")
    script.write_text(script.read_text().replace("402653184:402653184", f"{data_limit}:{data_limit}"))
    result = run(["docker", "run", "--rm", "--network", "none", "--read-only",
                  "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=256m", "--memory", "1536m", "--memory-swap", "1536m",
                  "--cpus", "1", "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true",
                  "-v", f"{work}:/inputs:ro", "--entrypoint", "sh", image, "/inputs/measure.sh", spec["name"]])
    values = list(map(int, result.stdout.split()))
    assert len(values) == 10, (result.stdout, result.stderr)
    code, milliseconds, peak, size = values[:4]
    rgb, outside = values[4:7], values[7:10]
    expected = ((0, 0, 0), (255, 255, 255)) if spec["filter"] == "/JBIG2Decode" else ((69, 70, 72), (148, 149, 153)) if spec["mode"] == "CMYK" else ((32, 32, 32), (128, 128, 128))
    return dict(**spec, data_limit_bytes=data_limit, exit_code=code, milliseconds=milliseconds, memory_peak_bytes=peak, jpeg_bytes=size, center_rgb=rgb,
                outside_rgb=outside, drawn=code == 0 and max(abs(c - e) for c, e in zip(rgb, expected[0])) <= 5 and max(abs(c - e) for c, e in zip(outside, expected[1])) <= 5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--jbig2-encoder")
    parser.add_argument("--reference", action="store_true", help="Compare pixels against the same renderer at DATA 768 MiB.")
    parser.add_argument("--cases", default="dct-baseline,dct-progressive,jpx-rgb60,jpx-gray60,jbig2-1000")
    args = parser.parse_args()
    args.work.mkdir(parents=True, exist_ok=True); args.work.chmod(0o755)
    cases = {
        "dct-baseline": (4000, 10000, "RGB", "JPEG", False),
        "dct-progressive": (4000, 10000, "RGB", "JPEG", True),
        "dct-progressive90": (4000, 7500, "RGB", "JPEG", True),
        "dct-baseline90": (4000, 7500, "RGB", "JPEG", False),
        "dct-gray90": (9000, 10000, "L", "JPEG", True),
        "dct-cmyk90": (3000, 7500, "CMYK", "JPEG", True),
        "jpx-rgba30": (2000, 5000, "RGBA", "JPEG2000", False),
        "dct-progressive60": (4000, 5000, "RGB", "JPEG", True),
        "jpx-rgb60": (4000, 5000, "RGB", "JPEG2000", False),
        "jpx-gray60": (6000, 10000, "L", "JPEG2000", False),
        "jpx-rgb30": (2000, 5000, "RGB", "JPEG2000", False),
        "jpx-gray30": (5000, 6000, "L", "JPEG2000", False),
        "jpx-rgb15": (2000, 2500, "RGB", "JPEG2000", False),
        "jpx-gray15": (3000, 5000, "L", "JPEG2000", False),
        "jbig2-1000": (40000, 25000, "1", "JBIG2", False),
        "jbig2-500": (25000, 20000, "1", "JBIG2", False),
    }
    results = []
    for name in args.cases.split(","):
        spec = generate(args.work, name, *cases[name], encoder=args.jbig2_encoder)
        result = measure(args.image, args.work, spec)
        if args.reference:
            reference = measure(args.image, args.work, spec, 768 * 1048576)
            result["reference_768_mib"] = reference
            result["drawn"] = result["exit_code"] == 0 and reference["exit_code"] == 0 and result["center_rgb"] == reference["center_rgb"] and result["outside_rgb"] == reference["outside_rgb"] and result["center_rgb"] != result["outside_rgb"]
        results.append(result)
        print(json.dumps(result), flush=True)
        (args.work / "measurements.json").write_text(json.dumps(results, indent=2) + "\n")


if __name__ == "__main__":
    main()
