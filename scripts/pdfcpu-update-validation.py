#!/usr/bin/env python3
"""Issue #82: 固定venvでpdfcpuの直接出力とToUnicode修正後のAPI出力を比較する。"""
import argparse
import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import tempfile

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("fill_update", ROOT / "scripts/fill-template-validation.py")
fill = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fill)
INVERSE = {gid: chr(c) for c, (gid, _) in fill.TABLE.items()}


def metadata(path):
    return json.loads(fill.command("qpdf", "--json=2", "--json-key=pages", "--json-key=qpdf",
        "--json-stream-data=inline", "--decode-level=all", str(path)).stdout)


def cmap(path):
    objects = metadata(path)["qpdf"][1]
    fonts = [obj["value"] for obj in objects.values() if isinstance(obj.get("value"), dict) and obj["value"].get("/Subtype") == "/Type0"]
    assert len(fonts) == 1, ("Type0 count", len(fonts))
    font = fonts[0]
    assert font["/Encoding"] == "/Identity-H"
    stream = objects["obj:" + font["/ToUnicode"]]["stream"]
    text = base64.b64decode(stream["data"]).decode("ascii")
    blocks = re.findall(r"(\d+) beginbfchar\s+(.*?)endbfchar", text, re.S)
    entries = [re.findall(r"<([0-9A-Fa-f]+)>\s+<([0-9A-Fa-f]+)>", body) for _, body in blocks]
    return dict(type0_fonts=len(fonts), blocks=[int(n) for n, _ in blocks],
        actual_block_entries=[len(block) for block in entries], mappings=sum(map(len, entries)),
        mapping_sha256=hashlib.sha256(json.dumps(sorted(pair for block in entries for pair in block)).encode()).hexdigest())


def extracted(path):
    poppler = fill.command("pdftotext", "-raw", str(path), "-").stdout.decode().split("\f")[:-1]
    with fill.fitz.open(path) as document:
        mupdf = [page.get_text(sort=False) for page in document]
    return {"poppler": [text.strip() for text in poppler], "mupdf": [text.strip() for text in mupdf]}


def matches(result, texts):
    # No Unicode normalization or whitespace removal inside a page.
    return {tool: pages == texts for tool, pages in result.items()}


def upload_bytes(api, root, name, data):
    fill.smoke.docker("exec", "-i", api.name, "sh", "-c", 'umask 077; cat > "$1"',
        "sh", root + "/" + name, data=data)


def direct(api, root, directory, name, pages, size=12, width=600):
    source = fill.blank(directory, name + "-blank", width=width, height=1000, pages=len(pages))
    upload_bytes(api, root, "blank.pdf", source)
    model = {"origin": "LowerLeft", "pages": {str(i+1): {"crop": f"[0 0 {width} 1000]",
        "content": {"text": [{"value": value, "pos": [x, y], "dy": dy,
            "font": {"name": "BIZUDPGothic-Regular", "size": size}}
            for value, x, y, dy in texts]}} for i, texts in enumerate(pages)}}
    fill.glyphs.upload(api, root, "layer.json", model)
    api.exec("rm", "-f", root + "/raw.pdf")
    api.exec("/opt/amane-pdf/bin/pdfcpu", "-c", "/opt/amane-pdf/config", "--offline", "create",
        root + "/layer.json", root + "/blank.pdf", root + "/raw.pdf")
    path = directory / (name + "-raw.pdf")
    path.write_bytes(api.exec("cat", root + "/raw.pdf").stdout)
    fill.command("qpdf", "--check", str(path))
    return path, source


def runs(path):
    document = metadata(path)
    result = []
    for page in document["pages"]:
        content = "".join(base64.b64decode(document["qpdf"][1]["obj:"+ref]["stream"]["data"]).decode("latin1")
            for ref in page["contents"])
        values = re.findall(r"\(((?:\\[\s\S]|[^\\)])*)\)\s+Tj", content)
        texts = []
        for value in values:
            data = fill.glyphs.literal_bytes(value)
            assert len(data) % 2 == 0
            texts.append("".join(INVERSE[int.from_bytes(data[i:i+2], "big")] for i in range(0, len(data), 2)))
        result.append((texts, content))
    return result


def text_behavior(api, root, directory, version):
    values = ["日%p本", "日%P本", "日%t本", "日%v本", "日\\n本", "%", "日😀\u0378本"]
    values += ["%"*(n+1) for n in (1, 2, 3, 10, 199)]
    path, _ = direct(api, root, directory, "substitutions", [[(v, 20, 600, 0)] for v in values], size=1)
    decoded = [texts for texts, _ in runs(path)]
    assert decoded[0] == ["日1本"] and decoded[1] == [f"日{len(values)}本"]
    assert re.fullmatch(r"日\d{4}-\d{2}-\d{2} \d{2}:\d{2}本", decoded[2][0])
    assert decoded[3] == ["日" + version + "本"]
    assert decoded[4] == ["日", "本"] and decoded[5] == [] and decoded[6] == ["日本"]
    assert decoded[7:] == [["%"*n] for n in (1, 2, 3, 10, 199)]
    # Independent content-stream check: the glyph table cannot hide missing ToUnicode entries.
    return dict(placeholders="PASS", literal_backslash_n="two lines", single_percent="element omitted",
        missing_glyphs="U+1F600 and U+0378 omitted", percent_runs=[1, 2, 3, 10, 199])


def coordinates(api, root, directory):
    results = []
    for size in (6, 6.1, 12, 72):
        expected = 246/2048*size
        y = expected - fill.math.ceil(expected)
        path, _ = direct(api, root, directory, "coordinates-"+str(size),
            [[("日", 20, y, 0)], [("日", 20, 0, y)]], size=size)
        baselines = []
        for _, content in runs(path):
            matrix = re.search(r"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) cm", content)
            offset = re.search(r"([-\d.]+) ([-\d.]+) Td", content)
            baselines.append(float(matrix[6]) + float(offset[2]))
        assert abs(baselines[1] - expected) < .00002, (size, baselines, expected)
        assert abs(baselines[0] - expected) > 400, ("negative y centers", size, baselines)
        results.append(dict(font_size=size, pos_y=y, expected_baseline=expected,
            negative_pos_baseline=baselines[0], split_dy_baseline=baselines[1]))
    return results


def unicode(api, root, directory, version):
    chars = [chr(c) for c in fill.TABLE if 0x4e00 <= c <= 0x9fff][:320]
    special = "%末尾%\n%%連続%%%\n%1%2%99\nか\u3099\\円￥①㈱※〒"
    cases = [(str(n), ["".join(chars[:n])]) for n in (99, 100, 101, 200)]
    cases.append(("multiple-pages", ["".join(chars[:160]), "".join(chars[160:]), special, "%a"*100]))
    results = []
    for name, texts in cases:
        fields = [fill.field(page=i+1, text=text, x=.01, y=.01, width=.98, height=.98, fontSize=6)
            for i, text in enumerate(texts)]
        pages = []
        for field in fields:
            lines = []
            for x, baseline, _, line in fill.positions(field, 1440, 1000):
                # Same JSON boundary as the API: n consecutive percent signs become n+1.
                value = re.sub(r"%+", lambda m: m[0]+"%", line)
                y = baseline - fill.math.ceil(246/2048*6)
                lines.append((value, x, max(0, y), min(0, y)))
            pages.append(lines)
        raw, source = direct(api, root, directory, "unicode-"+name, pages, size=6, width=1440)
        output, _ = fill.request(api, source, fields)
        repaired = directory / (name + "-repaired.pdf"); repaired.write_bytes(output)
        raw_matches = matches(extracted(raw), texts)
        fixed_matches = matches(extracted(repaired), texts)
        assert all(fixed_matches.values()), (name, "API extraction", fixed_matches)
        if version == "0.16.2" or name in ("99", "100"):
            assert all(raw_matches.values()), (name, "upstream extraction", raw_matches)
        else:
            assert not all(raw_matches.values()), (name, "baseline bug must reproduce")
        unique = len(set("".join(texts).replace("\n", "")))
        raw_map, fixed_map = cmap(raw), cmap(repaired)
        for info in ([raw_map, fixed_map] if version == "0.16.2" else [fixed_map]):
            assert info["blocks"] == info["actual_block_entries"]
            assert all(1 <= n <= 100 for n in info["blocks"]) and info["mappings"] == unique
        # CMap replacement must not change visible glyphs on any page.
        with fill.fitz.open(raw) as a, fill.fitz.open(repaired) as b:
            pixels = [(pa.get_pixmap().samples, pb.get_pixmap().samples) for pa, pb in zip(a, b)]
            render_matches = [pa == pb for pa, pb in pixels]
            render_hashes = [hashlib.sha256(pb).hexdigest() for _, pb in pixels]
        assert len(render_matches) == len(texts) and all(render_matches)
        results.append(dict(case=name, pages=len(texts), distinct_characters=unique, raw_extraction=raw_matches,
            repaired_extraction=fixed_matches, raw_cmap=raw_map, repaired_cmap=fixed_map, same_rendering=render_matches,
            repaired_render_sha256=render_hashes))
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--expected-version", choices=("0.16.1", "0.16.2"), default="0.16.2")
    parser.add_argument("--results", type=Path)
    args = parser.parse_args()
    assert fill.importlib.metadata.version("PyMuPDF") == "1.26.3"
    with tempfile.TemporaryDirectory(prefix="amane-pdfcpu-update-") as temporary:
        directory = Path(temporary)
        with fill.smoke.running_container(args.image) as api:
            version = api.exec("/opt/amane-pdf/bin/pdfcpu", "-c", "/opt/amane-pdf/config", "--offline", "version").stdout.decode()
            assert "version: " + args.expected_version in version.splitlines()
            root = api.exec("mktemp", "-d", "/tmp/pdfcpu-update-XXXXXXXX").stdout.decode().strip()
            assert re.fullmatch(r"/tmp/pdfcpu-update-[A-Za-z0-9]+", root)
            assert api.exec("stat", "-c", "%a", root).stdout.strip() == b"700"
            try:
                result = dict(version=args.expected_version, text_behavior=text_behavior(api, root, directory, args.expected_version),
                    coordinates=coordinates(api, root, directory), unicode=unicode(api, root, directory, args.expected_version))
            finally:
                api.exec("rm", "-rf", root)
            api.assert_clean()
            assert api.request("GET", "/healthz")[0] == 200
    if args.results:
        args.results.write_text(json.dumps(result, indent=2)+"\n")
    print(json.dumps(result, indent=2), flush=True)
    print("pdfcpu update: substitutions / missing glyphs / baseline / ToUnicode raw + repaired: PASS", flush=True)


if __name__ == "__main__":
    main()
