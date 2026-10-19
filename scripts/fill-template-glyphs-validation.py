#!/usr/bin/env python3
"""本番imageのpdfcpuで差し込み文字表のGID・幅とBMP全域の網羅性を照合する。"""
import argparse
import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import struct
import time
import unicodedata

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("smoke", ROOT / "scripts/docker-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def table():
    source = (ROOT / "src/Amane.Pdf.Api/FillTemplateGlyphs.g.cs").read_text()
    expression = re.search(r"Glyphs = Convert\.FromBase64String\(([\s\S]*?)\);", source)[1]
    encoded = "".join(re.findall(r'"([A-Za-z0-9+/=]+)"', expression))
    data = base64.b64decode(encoded, validate=True)
    assert hashlib.sha256(data).hexdigest() == re.search(r"data_sha256: ([0-9a-f]{64})", source)[1]
    assert re.search(r"font_sha256: ([0-9a-f]{64})", source)[1] == json.loads(
        (ROOT / "third_party/pdfcpu.lock").read_text())["font_sha256"]
    entries = list(struct.iter_unpack("<HHH", data))
    assert len(entries) == int(re.search(r"entries: (\d+)", source)[1]) == 11425
    assert all(a[0] < b[0] for a, b in zip(entries, entries[1:]))
    return {c: (gid, advance) for c, gid, advance in entries}


def upload(api, root, name, data):
    smoke.docker("exec", "-i", api.name, "sh", "-c", 'umask 077; cat > "$1"',
                 "sh", root + "/" + name, data=json.dumps(data, ensure_ascii=False).encode())


def literal_bytes(value):
    # Identity-H strings are PDF literals: escapes can include octal and parentheses.
    output = bytearray()
    i = 0
    while i < len(value):
        c = value[i]
        i += 1
        if c != "\\":
            output.append(ord(c))
            continue
        c = value[i]
        i += 1
        if c in "01234567":
            digits = c
            while i < len(value) and len(digits) < 3 and value[i] in "01234567":
                digits += value[i]
                i += 1
            output.append(int(digits, 8) % 256)
        elif c in "\r\n":
            if c == "\r" and i < len(value) and value[i] == "\n":
                i += 1
        else:
            output.append(ord({"n": "\n", "r": "\r", "t": "\t", "b": "\b", "f": "\f"}.get(c, c)))
    return bytes(output)


def resolve(objects, value):
    return objects["obj:" + value]["value"] if isinstance(value, str) else value


def cid_widths(objects, page, name):
    resources = resolve(objects, resolve(objects, page["object"])["/Resources"])
    fonts = resolve(objects, resources["/Font"])
    font = resolve(objects, fonts[name])
    assert font["/Subtype"] == "/Type0" and font["/Encoding"] == "/Identity-H"
    cid = resolve(objects, font["/DescendantFonts"][0])
    assert cid.get("/CIDToGIDMap", "/Identity") == "/Identity"
    data = resolve(objects, cid.get("/W", []))
    widths = {}
    i = 0
    while i < len(data):
        first = data[i]
        i += 1
        if isinstance(data[i], list):
            widths.update({first + j: width for j, width in enumerate(data[i])})
            i += 1
        else:
            last, width = data[i:i+2]
            widths.update({gid: width for gid in range(first, last + 1)})
            i += 2
    return widths, cid.get("/DW", 1000)


def verify_tounicode(metadata, expected):
    """Check the upstream CMap against independently known original characters/GIDs."""
    objects = metadata["qpdf"][1]
    fonts = [obj["value"] for obj in objects.values()
             if isinstance(obj.get("value"), dict) and obj["value"].get("/Subtype") == "/Type0"]
    assert len(fonts) == 1, ("Type0 count", len(fonts))
    font = fonts[0]
    assert font["/Encoding"] == "/Identity-H"
    text = base64.b64decode(objects["obj:" + font["/ToUnicode"]]["stream"]["data"]).decode("ascii")
    blocks = re.findall(r"(\d+) beginbfchar\s+(.*?)endbfchar", text, re.S)
    assert blocks and text.count("beginbfchar") == text.count("endbfchar") == len(blocks)
    assert "beginbfrange" not in text
    mappings, counts = {}, []
    for count, body in blocks:
        pairs = re.findall(r"<([0-9A-Fa-f]{4})>\s+<([0-9A-Fa-f]{4})>", body)
        assert 1 <= int(count) <= 100 and int(count) == len(pairs), (count, len(pairs))
        assert not re.sub(r"<([0-9A-Fa-f]{4})>\s+<([0-9A-Fa-f]{4})>", "", body).strip()
        counts.append(int(count))
        for gid, scalar in pairs:
            gid, scalar = int(gid, 16), int(scalar, 16)
            assert gid not in mappings, ("duplicate GID", gid)
            mappings[gid] = scalar
    inverse = {gid: c for c, (gid, _) in expected.items()}
    assert len(inverse) == len(expected), "Expected characters must have unique GIDs"
    assert mappings == inverse, ("ToUnicode mismatch", len(mappings), len(inverse),
        [(gid, c, mappings.get(gid)) for gid, c in inverse.items() if mappings.get(gid) != c][:20],
        sorted(set(mappings) - set(inverse))[:20])
    return dict(type0_fonts=len(fonts), blocks=counts, mappings=len(mappings), mismatches=0)


def draw(api, root, scalars, escape_percent=True, expected_unicode=None):
    batches = [scalars[i:i+256] for i in range(0, len(scalars), 256)]
    objects = {"obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
               "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": len(batches),
                                      "/Kids": [f"{i+3} 0 R" for i in range(len(batches))]}}}
    for i in range(len(batches)):
        objects[f"obj:{i+3} 0 R"] = {"value": {"/Type": "/Page", "/Parent": "2 0 R",
            "/MediaBox": [0, 0, 10000, 10000], "/Resources": {}}}
    objects["trailer"] = {"value": {"/Root": "1 0 R", "/Size": len(batches)+3}}
    upload(api, root, "blank.json", {"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objects]})
    api.exec("qpdf", "--json-input", root+"/blank.json", root+"/blank.pdf")
    pages = {str(i+1): {"crop": "[0 0 10000 10000]", "content": {"text": [
        {"value": "%%" if c == 0x25 and escape_percent else chr(c), "anchor": "c",
         "font": {"name": "BIZUDPGothic-Regular", "size": 1000}} for c in batch]}}
        for i, batch in enumerate(batches)}
    upload(api, root, "layer.json", {"origin": "LowerLeft", "pages": pages})
    api.exec("rm", "-f", root+"/layer.pdf")
    api.exec("/opt/amane-pdf/bin/pdfcpu", "-c", "/opt/amane-pdf/config", "--offline", "create",
             root+"/layer.json", root+"/blank.pdf", root+"/layer.pdf")
    metadata = json.loads(api.exec("qpdf", "--json=2", "--json-key=qpdf", "--json-key=pages",
                                  "--json-stream-data=inline", "--decode-level=all", root+"/layer.pdf").stdout)
    if expected_unicode is not None:
        info = verify_tounicode(metadata, expected_unicode)
        print("ToUnicode: " + json.dumps(info) + " PASS", flush=True)
    objects = metadata["qpdf"][1]
    assert len(metadata["pages"]) == len(batches)
    result = {}
    for page, batch in zip(metadata["pages"], batches):
        content = "".join(base64.b64decode(objects["obj:" + ref]["stream"]["data"]).decode("latin1")
                          for ref in page["contents"])
        runs = re.findall(r"q\s+(?:BT.*?ET\s+)?[\d.\-]+ [\d.\-]+ [\d.\-]+ [\d.\-]+ [\d.\-]+ [\d.\-]+ cm\s+BT.*?ET\s+Q",
                          content, re.S)
        if not escape_percent:
            assert batch == [0x25] and not runs and not re.search(r"\bTj\b", content)
            continue
        assert len(runs) == len(batch), (len(runs), len(batch))
        fonts = {}
        for c, run in zip(batch, runs):
            values = re.findall(r"\(((?:\\[\s\S]|[^\\)])*)\)\s+Tj", run)
            assert len(values) == 1, f"U+{c:04X}: expected one Tj"
            glyph = literal_bytes(values[0])
            if not glyph:
                continue
            assert len(glyph) == 2, f"U+{c:04X}: expected one Identity-H GID"
            gid = int.from_bytes(glyph, "big")
            assert gid != 0, f"U+{c:04X}: .notdef"
            name = re.search(r"(/\S+) [\d.]+ Tf", run)[1]
            if name not in fonts:
                fonts[name] = cid_widths(objects, page, name)
            widths, default = fonts[name]
            result[c] = (gid, widths.get(gid, default))
    return result


def verify(api, root, expected):
    started = time.monotonic()
    actual = draw(api, root, list(expected), expected_unicode=expected)
    assert actual == expected, (len(actual), len(expected), [hex(c) for c in expected if actual.get(c) != expected[c]][:20])
    print(f"Table: {len(expected)} drawn / missing 0 / GID mismatches 0 / width mismatches 0: PASS ({time.monotonic()-started:.2f}s)", flush=True)
    for c in (0x00AD, 0x0020, 0x00A0, 0x3000, 0x3099, 0x25, 0x5C):
        print(f"U+{c:04X}: GID {actual[c][0]}, advance {actual[c][1]}", flush=True)
    marks = [c for c in expected if unicodedata.category(chr(c)) == "Mn"]
    assert len(marks) == 33 and all(c in actual for c in marks)
    print("Combining marks (Mn): 33/33 drawn: PASS", flush=True)
    started = time.monotonic()
    scalars = [c for c in range(0x10000) if not 0xD800 <= c <= 0xDFFF and unicodedata.category(chr(c)) != "Cc"]
    assert len(scalars) == 63423
    drawn = {}
    # Bound per-process work and JSON size; each PDF page has at most 256 elements.
    for i in range(0, len(scalars), 4096):
        drawn.update(draw(api, root, scalars[i:i+4096]))
    assert set(drawn) == set(expected) | {0x02DC, 0x0303}, (len(drawn), sorted(set(drawn) ^ (set(expected) | {0x02DC, 0x0303}))[:20])
    assert all(drawn[c] == value for c, value in expected.items())
    assert drawn[0x02DC][0] == drawn[0x0303][0] == 1343
    print(f"BMP: {len(scalars)} tested / {len(drawn)} drawn / table + U+02DC,U+0303 exactly: PASS ({time.monotonic()-started:.2f}s)", flush=True)
    assert draw(api, root, [0x25], escape_percent=False) == {}
    print("Percent: single % drops, %% draws one GID; standalone backslash draws: PASS", flush=True)
    return dict(glyphs=len(expected), tounicode_mismatches=0, missing=0, gid_mismatches=0, width_mismatches=0,
                bmp_tested=len(scalars), bmp_drawn=len(drawn))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--results", type=Path)
    args = parser.parse_args()
    expected = table()
    started = time.monotonic()
    with smoke.running_container(args.image) as api:
        version = api.exec("/opt/amane-pdf/bin/pdfcpu", "-c", "/opt/amane-pdf/config", "--offline", "version").stdout.decode()
        assert "version: 0.16.2" in version.splitlines()
        root = api.exec("mktemp", "-d", "/tmp/fill-template-glyphs-XXXXXXXX").stdout.decode().strip()
        try:
            assert re.fullmatch(r"/tmp/fill-template-glyphs-[A-Za-z0-9]+", root)
            assert api.exec("stat", "-c", "%a", root).stdout.strip() == b"700"
            result = verify(api, root, expected)
        finally:
            api.exec("rm", "-rf", root)
        assert api.exec("test", "!", "-e", root).returncode == 0
        api.assert_clean()
    if args.results:
        args.results.write_text(json.dumps(result, indent=2) + "\n")
    print(f"pdfcpu 0.16.2 / total {time.monotonic()-started:.2f}s", flush=True)


if __name__ == "__main__":
    main()
