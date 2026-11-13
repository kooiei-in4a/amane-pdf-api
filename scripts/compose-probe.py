#!/usr/bin/env python3
"""D1 #444 V-S1〜V-S3。既存の overlay Docker ハーネスで合成と書体を検証する。"""

import argparse
import base64
from collections import Counter, defaultdict
import hashlib
import math
import io
import importlib.util
import json
from pathlib import Path
import random
import re
import shutil
import struct
import tempfile
from decimal import Decimal

import fitz
from fontTools.ttLib import TTFont
from PIL import Image, PngImagePlugin

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("overlay", ROOT / "scripts/overlay-validation.py")
overlay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(overlay)
pdf = overlay.load("compose_pdf", "compress-validation.py")
glyphs = overlay.load("compose_glyphs", "fill-template-glyphs-validation.py")
generator = overlay.load("compose_generator", "fill-template-glyphs.py")
MiB = 1048576
A4 = (595.28, 841.89)
FONT_SHA = "dbcea04578ac1e9d3484525e870ce491bd04361768f4d2ba4b827d96e20f891d"
POPPLER = "sha256:fecac17e9d2e442c4721dff3a895a7dc1b7c1f69911197e96dce4f08b9a6ff62"
BASE_SHA = "df3f8a8ff78770004e06d40aad0b1f662af6655c"
DESIGN_SHA = "38b1d07ed15588eaf72af5295d92beab39a23f61"
VECTORS_SHA = "1053588d5c3ab01181e641cca443548f0898ed5613a1f4cc589059719854ad33"
CASES = [
    ("V-S1", False, 20, 180),
    ("V-S2", True, 20, 0),
    ("V-S2-combined", True, 20, 180),
    ("V-S2-combined-12-images", True, 12, 188),
    ("V-S2-combined-10-images", True, 10, 190),
    ("V-S2-combined-8-images", True, 8, 192),
]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def asset(directory, index):
    # Mostly flat RGBA, with unique deterministic noise and a transparent corner.
    # The small tEXt chunk brings the upload to exactly 512 KiB.
    color = (30 + index * 5, 110, 170, 255)
    image = Image.new("RGBA", (2000, 2000), color)
    image.paste((0, 0, 0, 0), (0, 0, 500, 500))
    rng = random.Random(index)
    image.paste(Image.frombytes("RGB", (1000, 145), rng.randbytes(435000)).convert("RGBA"), (900, 1700))
    output = io.BytesIO()
    image.save(output, format="PNG")
    pad = 512 * 1024 - output.tell() - 12 - len(b"fixture\0")
    assert 0 <= pad <= 4096, pad
    info = PngImagePlugin.PngInfo()
    info.add_text("fixture", "x" * pad)
    path = directory / f"as_{index:08d}.png"
    image.save(path, pnginfo=info)
    assert path.stat().st_size == 512 * 1024


def background(path, large):
    # Same retained, independent random gray image streams as memory-fixtures.py's
    # near-50mib fixture. Use the existing PDF fixture writer, and draw each stream.
    width, height = A4
    pages = 30
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        f"<< /Type /Pages /Count {pages} /Kids [{' '.join(f'{4 + 2*i} 0 R' for i in range(pages))}] >>".encode(),
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    rng = random.Random(44)
    retained = [rng.randbytes(MiB) for _ in range(49)] + [b""] if large else []
    image_refs = " ".join(f"/Bg{i} {4 + 2*pages + i} 0 R" for i in range(len(retained)))
    tiles = "".join(f"q 5 0 0 5 {10+i*6} 24 cm /Bg{i} Do Q\n" for i in range(len(retained)))
    for i in range(pages):
        resources = "/Font << /F1 3 0 R >>"
        if i == 0 and retained:
            resources += f" /XObject << {image_refs} >>"
        objects.append(f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] "
                       f"/Resources << {resources} >> /Contents {5 + 2*i} 0 R >>".encode())
        content = f"BT /F1 10 Tf 20 24 Td (D1 Background {i+1:02}) Tj ET\n0 0 1 RG 20 12 200 1 re S\n"
        objects.append(pdf.stream("", (content + (tiles if i == 0 else "")).encode()))
    for data in retained:
        objects.append(pdf.stream("/Type /XObject /Subtype /Image /Width 1024 "
                                  f"/Height {len(data)//1024 or 1} /ColorSpace /DeviceGray /BitsPerComponent 8",
                                  data))
    if large:
        # Pad the last valid image stream with random pixels, then a PDF comment
        # before the xref, so the input is exactly 50 MiB.
        remainder = 50 * MiB - len(pdf.pdf(objects))
        height = remainder // 1024 - 1
        assert 1 <= height < 1024
        objects[-1] = pdf.stream(f"/Type /XObject /Subtype /Image /Width 1024 /Height {height} "
                                 "/ColorSpace /DeviceGray /BitsPerComponent 8", rng.randbytes(1024 * height))
        remainder = 50 * MiB - len(pdf.pdf(objects))
        assert remainder > 2
        objects[-1] += b"\n%" + b"x" * (remainder - 2)
    data = pdf.pdf(objects)
    assert not large or len(data) == 50 * MiB
    path.write_bytes(data)


def layout_fixture(directory, vector, images, texts):
    directory.mkdir()
    elements = []
    text_index = 0
    gothic_sha = json.loads((ROOT / "third_party/pdfcpu.lock").read_text())["font_sha256"]
    width, height = A4
    front_texts = min(texts - 29, 60 - images) if texts else 0
    assert not texts or front_texts > images
    remaining = texts - front_texts
    for page in range(1, 31):
        count = front_texts if page == 1 else remaining // 29 + (page - 2 < remaining % 29)
        for i in range(count):
            text_index += 1
            elements.append(dict(id=f"el_{text_index:08d}", page=page, kind="text",
                                 rect=[2000 + (i % 3) * 18000, 2000 + (i // 3) * 3000,
                                       vector["boxWidth"], vector["boxHeight"]],
                                 text="\n".join(vector["expected"]["lines"]), font="gothic",
                                 size=vector["size"], color="#000000", align="left",
                                 valign="middle", lineHeight=vector["lineHeight"]))
            if page == 1 and i < images:
                elements.append(dict(id=f"el_img{i+1:05d}", page=page, kind="image",
                                     rect=[3500 + (i % 5) * 10400, 49000 + (i // 5) * 7300, 6400, 6400],
                                     image=f"as_{i+1:08d}"))
        if not texts and page <= images:
            elements.append(dict(id=f"el_img{page:05d}", page=page, kind="image",
                                 rect=[3500, 49000, 6400, 6400], image=f"as_{page:08d}"))
    assert text_index == texts
    layout = dict(layout="ds-layout/1", fonts=[dict(id="gothic", sha256=gothic_sha)], blank=False,
                  pages=[dict(index=i, width=round(width*100), height=round(height*100)) for i in range(1, 31)],
                  elements=elements)
    (directory / "layout.json").write_text(json.dumps(layout, ensure_ascii=False), encoding="utf-8")
    return layout


def tool_wrapper(directory):
    # Count before prlimit sets FSIZE=1 for the stamp stdout writer.
    path = directory / "count-prlimit.sh"
    path.write_text("""#!/bin/sh
kind=
for argument do
    case "$argument" in
        qpdf) kind=qpdf ;;
        */pdfcpu) kind=pdfcpu ;;
        --overlay) kind=overlay ;;
    esac
done
test -n "$kind" || exit 125
printf '%s\\n' "$kind" >> "$COMPOSE_RESULTS/calls.txt" || exit 125
exec /usr/bin/prlimit "$@"
""")
    path.chmod(0o755)


def output_checks(path, layout, large):
    text = defaultdict(Counter)
    boxes = defaultdict(list)
    for element in layout["elements"]:
        page = element["page"]
        if element["kind"] == "text":
            text[page].update(element["text"].splitlines())
        else:
            x, y, w, h = [value / 100 for value in element["rect"]]
            boxes[page].append((x, y, x + w, y + h))

    with fitz.open(path) as doc:
        assert len(doc) == len(layout["pages"])
        for i, page in enumerate(doc, 1):
            assert abs(page.rect.width - A4[0]) < .001 and abs(page.rect.height - A4[1]) < .001
            assert Counter(page.get_text().splitlines()) == text[i] + Counter([f"D1 Background {i:02}"])
            images = page.get_image_info()
            if large and i == 1:
                assert sum(image["width"] == 1024 for image in images) == 50
            placed = [image["bbox"] for image in images if image["width"] == 2000 and image["height"] == 2000]
            assert len(placed) == len(boxes[i])
            for expected in boxes[i]:
                assert any(max(abs(a-b) for a, b in zip(expected, actual)) < .1 for actual in placed)
    return dict(structure=True, page_attributes=True, source_markers=30,
                text_lines=sum(sum(lines.values()) for lines in text.values()),
                image_placements=sum(map(len, boxes.values())))


def measure(args, directory, case, layout, large):
    env = {"COMPOSE_FIXTURE": f"/fixture/{case}", "COMPOSE_RESULTS": "/tmp/results", "DOTNET_CLI_HOME": "/tmp/cli"}
    container = overlay.Container(args.image, args.sdk, [(directory, "/fixture")], env)
    try:
        container.tests("FullyQualifiedName~ComposeProbeTests.Measure")
        result = json.loads(container.exec("cat", "/tmp/results/metrics.json").stdout)
        container.stopped()
        result["cgroup_memory_peak_bytes"] = int(container.exec("cat", "/sys/fs/cgroup/memory.peak").stdout)
        result["output_checks"] = None
        if result["failure"] is None:
            output = directory / "output.pdf"
            output.write_bytes(container.exec("cat", "/tmp/results/output.pdf").stdout)
            try:
                result["output_checks"] = output_checks(output, layout, large)
            except (AssertionError, RuntimeError) as error:
                result["output_check_error"] = type(error).__name__
            finally:
                output.unlink()
        result["pass"] = (result["output_checks"] is not None
                          and result["qpdfOverlayCalls"] == 1
                          and result["pdfcpuCalls"] == result["runCount"]
                          and result["peakLedgerBytes"] <= 124 * MiB
                          and (large or result["seconds"] <= 30)
                          and (not large or result["outputBytes"] <= 80 * MiB))
        return result
    finally:
        container.close()


def mincho_table(path):
    data = path.read_bytes()
    assert sha(data) == FONT_SHA
    with TTFont(path) as font:
        units = font["head"].unitsPerEm
        assert units == 2048
        cmap = font.getBestCmap()
        gids = {code: font.getGlyphID(name) for code, name in cmap.items()}
        multiplicity = Counter(gid for gid in gids.values() if gid)
        table = {code: (gid, font["hmtx"][cmap[code]][0] * 1000 // units)
                 for code, gid in sorted(gids.items()) if gid and multiplicity[gid] == 1 and generator.eligible(code)}
        head = font["head"]
        hhea = font["hhea"]
        os2 = font["OS/2"]
        assert (hhea.ascent, hhea.descent) == (os2.sTypoAscender, os2.sTypoDescender)
        assert hhea.lineGap == os2.sTypoLineGap == 0
        metrics = dict(units_per_em=units, layout_upper_units=hhea.ascent, layout_lower_units=-hhea.descent,
                       layout_upper_em=hhea.ascent / units, layout_lower_em=-hhea.descent / units,
                       ink_bbox_y_units=[head.yMin, head.yMax])
    return table, metrics


def direct(container, name, pages, size):
    blank = [b"<< /Type /Catalog /Pages 2 0 R >>",
             f"<< /Type /Pages /Count {len(pages)} /Kids [{' '.join(f'{3+i} 0 R' for i in range(len(pages)))}] >>".encode()]
    blank.extend([b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 6000 6000] /Resources << >> >>"] * len(pages))
    model = dict(origin="LowerLeft", pages={str(i): dict(crop="[0 0 6000 6000]",
        content=dict(text=[dict(item, font=dict(name="BIZUDPMincho-Regular", size=size))]))
        for i, item in enumerate(pages, 1)})
    for suffix, data in [("pdf", pdf.pdf(blank)), ("json", json.dumps(model, ensure_ascii=False).encode())]:
        glyphs.smoke.docker("exec", "-i", container.name, "sh", "-c", 'cat > "$1"',
                            "sh", f"/tmp/{name}.{suffix}", data=data)
    path = f"/tmp/{name}-drawn.pdf"
    container.exec("/opt/amane-pdf/bin/pdfcpu", "-c", "/tmp/mincho-config", "--offline",
                   "create", f"/tmp/{name}.json", f"/tmp/{name}.pdf", path)
    container.exec("qpdf", "--check", path)
    return path


def metadata(container, path):
    return json.loads(container.exec("qpdf", "--json=2", "--json-key=pages", "--json-key=qpdf",
        "--json-stream-data=inline", "--decode-level=all", path).stdout)


def actual_baselines(container, path):
    parsed = metadata(container, path)
    for page in parsed["pages"]:
        contents = "".join(base64.b64decode(parsed["qpdf"][1]["obj:" + ref]["stream"]["data"]).decode("latin1")
                           for ref in page["contents"])
        cm = re.search(r"([-.\d]+) ([-.\d]+) ([-.\d]+) ([-.\d]+) ([-.\d]+) ([-.\d]+) cm", contents)
        td = re.search(r"([-.\d]+) ([-.\d]+) Td", contents)
        yield float(cm[6]) + float(td[2])


def font_probe(args, directory):
    table, metrics = mincho_table(args.font)
    common = table.keys() & glyphs.table().keys()
    binary = b"".join(struct.pack("<HHH", code, *record) for code, record in table.items())
    args.results.with_name("compose-probe-mincho-widths.bin").write_bytes(binary)
    chars = [chr(code) for code in sorted(common) if 0x4e00 <= code <= 0x9fff][:320]
    expected = ["".join(chars[:160]), "".join(chars[160:])]
    shutil.copyfile(args.font, directory / "BIZUDPMincho-Regular.ttf")

    container = overlay.Container(args.image, args.sdk, [(directory, "/fixture")], {})
    try:
        container.exec("cp", "-R", "/opt/amane-pdf/config", "/tmp/mincho-config")
        container.exec("chmod", "u+w", "/tmp/mincho-config/pdfcpu/fonts")
        container.exec("/opt/amane-pdf/bin/pdfcpu", "-c", "/tmp/mincho-config", "--offline", "fonts", "install",
                       "/fixture/BIZUDPMincho-Regular.ttf")
        unicode = direct(container, "unicode", [dict(value=line, pos=[20, 4000]) for line in expected], 12)
        blob = container.exec("cat", unicode).stdout
        with fitz.open(stream=blob, filetype="pdf") as doc:
            mupdf = [page.get_text().strip("\n") for page in doc]
        extracted = glyphs.smoke.docker("run", "--rm", "-i", "--pull=never", "--network=none",
                                       "--entrypoint", "pdftotext", POPPLER, "-raw", "-", "-", data=blob)
        poppler = [page.strip("\n") for page in extracted.stdout.decode().split("\f")[:-1]]
        cmap = glyphs.verify_tounicode(metadata(container, unicode), {ord(c): table[ord(c)] for c in chars})

        pdfcpu_bbox = [abs(value) * 1000 // metrics["units_per_em"]
                       for value in reversed(metrics["ink_bbox_y_units"])]
        measures = []
        for size in (1000, 2048):
            baseline = direct(container, f"baseline-{size}", [dict(value="日", pos=[20, 2000]),
                              dict(value="日", anchor="tl")], size)
            from_pos, from_top = actual_baselines(container, baseline)
            measures.append(dict(size_pt=size, upper_pt=6000 - from_top, lower_pt=from_pos - 2000,
                                 upper_em=(6000 - from_top)/size, lower_em=(from_pos - 2000)/size))
        offsets = []
        for size in (6, 6.1, 12, 55, 72, 144):
            expected_y = metrics["layout_lower_em"] * size
            compensation = math.ceil(Decimal(pdfcpu_bbox[1]) * Decimal(str(size)) / 1000)
            baseline = direct(container, f"bottom-{size}", [
                dict(value="日", pos=[20, 0], dy=expected_y - compensation)], size)
            actual_y, = actual_baselines(container, baseline)
            offsets.append(dict(size_pt=size, compensation_pt=compensation, target_pt=expected_y,
                                actual_pt=actual_y, absolute_error_pt=abs(expected_y - actual_y)))
        metrics.update(pdfcpu_bbox_milli_em=pdfcpu_bbox, pdfcpu_edge_measurements=measures,
                       page_bottom_checks=offsets)
        geometry = all(item["upper_pt"] == pdfcpu_bbox[0]*item["size_pt"]//1000
                       and item["lower_pt"] == math.ceil(Decimal(pdfcpu_bbox[1])*item["size_pt"]/1000)
                       for item in measures)
        geometry &= all(item["absolute_error_pt"] < .00002 for item in offsets)
        pdfcpu = container.exec("/opt/amane-pdf/bin/pdfcpu", "version").stdout.decode().strip().splitlines()
        qpdf = container.exec("qpdf", "--version").stdout.decode().splitlines()[0]
        poppler_version = glyphs.smoke.docker("run", "--rm", "--pull=never", "--network=none",
                                              "--entrypoint", "pdftotext", POPPLER, "-v").stderr.decode().splitlines()[0]
        return dict(entries=len(table), gothic_entries=len(glyphs.table()), common_entries=len(common),
                    tool_versions=dict(pdfcpu=[line.strip() for line in pdfcpu], qpdf=qpdf, poppler=poppler_version),
                    font_sha256=FONT_SHA, widths_sha256=sha(binary), metrics=metrics,
                    extraction=dict(pages=2, distinct_glyphs=len(chars), page_lengths=list(map(len, expected)),
                                    mupdf_exact=mupdf == expected, poppler_exact=poppler == expected,
                                    mupdf_page_sha256=[sha(page.encode()) for page in mupdf],
                                    poppler_page_sha256=[sha(page.encode()) for page in poppler]),
                    cmap=cmap, **{"pass": mupdf == poppler == expected and geometry})
    finally:
        container.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--font", type=Path, required=True)
    parser.add_argument("--vectors", type=Path, required=True)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--case", action="append", choices=[case[0] for case in CASES] + ["V-S3"],
                        help="ケース名を指定して再測定する")
    parser.add_argument("--supplemental-repeats", type=int, default=1)
    args = parser.parse_args()
    sdk_base = overlay.command("dotnet", "--list-sdks").stdout.decode().splitlines()[-1].split("[")[1].rstrip("]")
    args.sdk = Path(sdk_base) / overlay.command("dotnet", "--version").stdout.decode().strip()
    assert args.repeats >= 1 and args.supplemental_repeats >= 1
    assert (args.sdk / "vstest.console.dll").exists()
    image = json.loads(overlay.command("docker", "image", "inspect", args.image).stdout)[0]
    assert image["Config"]["Labels"]["org.opencontainers.image.revision"] == BASE_SHA
    image_name = args.image
    args.image = image["Id"]
    vectors_data = args.vectors.read_bytes()
    assert sha(vectors_data) == VECTORS_SHA
    vectors = json.loads(vectors_data)
    assert vectors["layout"] == "ds-layout/1"
    vector = next(vector for vector in vectors["vectors"] if vector["name"] == "exact-fit-minus-1cpt")
    probe_sha = overlay.command("git", "-C", str(ROOT), "rev-parse", "HEAD").stdout.decode().strip()
    evidence = dict(base_sha=BASE_SHA, probe_sha=probe_sha, design_sha=DESIGN_SHA, vectors_sha256=VECTORS_SHA,
                    image=image_name, image_id=image["Id"], architecture=image["Architecture"], dotnet_sdk=args.sdk.name,
                    ledger_limit_bytes=124*MiB, existing_overlay_pdf_limit_bytes=62*MiB,
                    existing_generated_layer_limit_bytes=8*MiB, deadline_seconds=30,
                    cpu=1, memory_mib=1536, memory_swap_bytes=0, tmpfs_mib=256,
                    mupdf=fitz.VersionFitz, pymupdf=fitz.VersionBind, poppler_image_id=POPPLER,
                    mincho_release="v1.06", mincho_release_sha="c30a6221b1f3d09afae9137ffe73c7cbec649947",
                    mincho_license="OFL-1.1", repetitions=args.repeats,
                    supplemental_repetitions=args.supplemental_repeats)
    args.results.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="amane-compose-probe-") as temporary:
        directory = Path(temporary)
        directory.chmod(0o755)
        tool_wrapper(directory)
        for i in range(1, 21):
            asset(directory, i)
        normal_pdf = directory / "normal.pdf"
        large_pdf = directory / "large.pdf"
        background(normal_pdf, False)
        background(large_pdf, True)
        for case, large, images, texts in CASES:
            if args.case and case not in args.case:
                continue
            fixture = directory / case
            layout = layout_fixture(fixture, vector, images, texts)
            (fixture / "background.pdf").hardlink_to(large_pdf if large else normal_pdf)
            attempts = []
            repeats = args.supplemental_repeats if case.startswith("V-S2-combined") else args.repeats
            for _ in range(repeats):
                attempt = measure(args, directory, case, layout, large)
                attempts.append(attempt)
                print(case, {key: attempt[key] for key in ("pass", "seconds", "peakLedgerBytes", "failure")}, flush=True)
            evidence[case] = attempts
            args.results.write_text(json.dumps(evidence, indent=2) + "\n")
        if not args.case or "V-S3" in args.case:
            evidence["V-S3"] = font_probe(args, directory)
            print("V-S3", evidence["V-S3"]["pass"], flush=True)
        args.results.write_text(json.dumps(evidence, indent=2) + "\n")


if __name__ == "__main__":
    main()
