#!/usr/bin/env python3
"""Issue #82: 固定venvでAPI出力のToUnicode・抽出とpdfcpuの文字/座標挙動を検証する。"""
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


def unicode(api, directory):
    chars = [chr(c) for c in fill.TABLE if 0x4e00 <= c <= 0x9fff][:320]
    special = "%末尾%\n%%連続%%%\n%1%2%99\nか\u3099\\円￥①㈱※〒"
    cases = [(str(n), ["".join(chars[:n])]) for n in (99, 100, 101, 200)]
    cases.append(("multiple-pages", ["".join(chars[:160]), "".join(chars[160:]), special, "%a"*100]))
    results = []
    for name, texts in cases:
        source = fill.blank(directory, "unicode-"+name, width=1440, height=1000, pages=len(texts))
        fields = [fill.field(page=i+1, text=text, x=.01, y=.01, width=.98, height=.98, fontSize=6)
                  for i, text in enumerate(texts)]
        output, _ = fill.request(api, source, fields)
        path = directory / (name + "-api.pdf")
        path.write_bytes(output)
        extraction = matches(extracted(path), texts)
        assert all(extraction.values()), (name, "API extraction", extraction)
        drawn = set("".join(texts).replace("\n", ""))
        info = fill.glyphs.verify_tounicode(metadata(path), {ord(c): fill.TABLE[ord(c)] for c in drawn})
        results.append(dict(case=name, pages=len(texts), distinct_characters=len(drawn),
                            extraction=extraction, cmap=info))
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--results", type=Path)
    args = parser.parse_args()
    assert fill.importlib.metadata.version("PyMuPDF") == "1.26.3"
    with tempfile.TemporaryDirectory(prefix="amane-pdfcpu-update-") as temporary:
        directory = Path(temporary)
        with fill.smoke.running_container(args.image) as api:
            version = api.exec("/opt/amane-pdf/bin/pdfcpu", "-c", "/opt/amane-pdf/config", "--offline", "version").stdout.decode()
            assert "version: 0.16.2" in version.splitlines()
            root = api.exec("mktemp", "-d", "/tmp/pdfcpu-update-XXXXXXXX").stdout.decode().strip()
            assert re.fullmatch(r"/tmp/pdfcpu-update-[A-Za-z0-9]+", root)
            assert api.exec("stat", "-c", "%a", root).stdout.strip() == b"700"
            try:
                result = dict(version="0.16.2", text_behavior=text_behavior(api, root, directory, "0.16.2"),
                    coordinates=coordinates(api, root, directory), unicode=unicode(api, directory))
            finally:
                api.exec("rm", "-rf", root)
            api.assert_clean()
            assert api.request("GET", "/healthz")[0] == 200
    if args.results:
        args.results.write_text(json.dumps(result, indent=2)+"\n")
    print(json.dumps(result, indent=2), flush=True)
    print("pdfcpu update: substitutions / missing glyphs / baseline / API ToUnicode + MuPDF/Poppler: PASS", flush=True)


if __name__ == "__main__":
    main()
