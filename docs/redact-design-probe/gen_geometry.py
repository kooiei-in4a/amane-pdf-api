"""Spike: write qpdf JSON v2 test documents (geometry + non-embedded CJK font)."""
import base64
import json
import sys

out = sys.argv[1]


def stream(data, extra=None):
    d = dict(extra or {})
    return {"stream": {"dict": d, "data": base64.b64encode(data.encode("latin-1")).decode()}}


def doc(pages):
    """pages: list of (page_dict_without_parent_contents, content_str, resources)"""
    objs = {}
    n = 3
    kids = []
    for page, content, resources in pages:
        pid, cid = n, n + 1
        n += 2
        p = {"/Type": "/Page", "/Parent": "2 0 R", "/Contents": f"{cid} 0 R", "/Resources": resources}
        p.update(page)
        objs[f"obj:{pid} 0 R"] = {"value": p}
        objs[f"obj:{cid} 0 R"] = stream(content)
        kids.append(f"{pid} 0 R")
    objs["obj:1 0 R"] = {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}}
    objs["obj:2 0 R"] = {"value": {"/Type": "/Pages", "/Kids": kids, "/Count": len(kids)}}
    objs["trailer"] = {"value": {"/Root": "1 0 R", "/Size": n}}
    return {"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objs]}


def corners(l, b, r, t, s=50):
    # BL red, BR green, TL blue, TR yellow; whole crop light gray.
    return (f"0.8 g {l} {b} {r - l} {t - b} re f "
            f"1 0 0 rg {l} {b} {s} {s} re f "
            f"0 1 0 rg {r - s} {b} {s} {s} re f "
            f"0 0 1 rg {l} {t - s} {s} {s} re f "
            f"1 1 0 rg {r - s} {t - s} {s} {s} re f")


pages = []
for rot in (0, 90, 180, 270):
    pages.append(({"/MediaBox": [0, 0, 600, 800], "/CropBox": [50, 100, 550, 700], "/Rotate": rot},
                  corners(50, 100, 550, 700), {}))
# UserUnit 2, rotate 90
pages.append(({"/MediaBox": [0, 0, 300, 400], "/UserUnit": 2, "/Rotate": 90}, corners(0, 0, 300, 400), {}))
# CropBox partly outside MediaBox -> intersection [0 0 300 400]
pages.append(({"/MediaBox": [0, 0, 600, 800], "/CropBox": [-50, -50, 300, 400]}, corners(0, 0, 300, 400), {}))
# Non-embedded Japanese CID font (MS-Mincho, UniJIS-UCS2-H): "日本語テスト"
font = {
    "/Type": "/Font", "/Subtype": "/Type0", "/BaseFont": "/MS-Mincho", "/Encoding": "/UniJIS-UCS2-H",
    "/DescendantFonts": [{
        "/Type": "/Font", "/Subtype": "/CIDFontType0", "/BaseFont": "/MS-Mincho",
        "/CIDSystemInfo": {"/Registry": "u:Adobe", "/Ordering": "u:Japan1", "/Supplement": 6},
        "/FontDescriptor": {"/Type": "/FontDescriptor", "/FontName": "/MS-Mincho", "/Flags": 6,
                            "/FontBBox": [0, -141, 1000, 859], "/ItalicAngle": 0, "/Ascent": 859,
                            "/Descent": -141, "/CapHeight": 700, "/StemV": 80},
        "/DW": 1000}]}
text = "65E5672C8A9E30C630B930C8"
pages.append(({"/MediaBox": [0, 0, 595, 842]},
              f"BT /F1 64 Tf 40 600 Td <{text}> Tj ET", {"/Font": {"/F1": font}}))
# Non-embedded Helvetica (standard 14)
pages.append(({"/MediaBox": [0, 0, 595, 842]}, "BT /F2 64 Tf 40 600 Td (Hello PDF) Tj ET",
              {"/Font": {"/F2": {"/Type": "/Font", "/Subtype": "/Type1", "/BaseFont": "/Helvetica"}}}))
json.dump(doc(pages), open(out, "w"))
