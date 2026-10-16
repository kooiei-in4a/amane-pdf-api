"""Probe inputs that are not covered by the other generators (qpdf JSON v2 unless noted).

usage: gen_misc.py <workdir>
"""
import base64
import json
import random
import sys
import zlib

w = sys.argv[1]
b = lambda s: base64.b64encode(s.encode()).decode()


def save(name, objs, version="1.7"):
    json.dump({"qpdf": [{"jsonversion": 2, "pdfversion": version}, objs]}, open(f"{w}/{name}", "w"), ensure_ascii=False)


# 1. Two blank pages + pdfcpu layer: both pages use one embedded BIZ UDPGothic subset font.
save("blank2.json", {
    "obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
    "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": 2, "/Kids": ["3 0 R", "5 0 R"]}},
    "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842], "/Resources": {}, "/Contents": "4 0 R"}},
    "obj:4 0 R": {"stream": {"dict": {}, "data": ""}},
    "obj:5 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842], "/Resources": {}, "/Contents": "6 0 R"}},
    "obj:6 0 R": {"stream": {"dict": {}, "data": ""}},
    "trailer": {"value": {"/Root": "1 0 R", "/Size": 7}}})
json.dump({"origin": "LowerLeft", "pages": {
    "1": {"content": {"text": [{"value": "鈴木一郎 個人番号 9876", "pos": [50, 700], "font": {"name": "BIZUDPGothic-Regular", "size": 24}}]}},
    "2": {"content": {"text": [{"value": "テスト PUBLIC", "pos": [50, 700], "font": {"name": "BIZUDPGothic-Regular", "size": 24}}]}}}},
    open(f"{w}/shared-layer.json", "w"), ensure_ascii=False)

# 2. Shared /Resources dict (pages 1-2), unused refs on pages 3-4, page 3 content has a lexical error.
save("shared-res.json", {
    "obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
    "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": 4, "/Kids": ["3 0 R", "5 0 R", "7 0 R", "12 0 R"]}},
    "obj:9 0 R": {"value": {"/Font": {"/F1": "10 0 R", "/F2": "11 0 R"}, "/XObject": {"/X1": "13 0 R"}}},
    "obj:10 0 R": {"value": {"/Type": "/Font", "/Subtype": "/Type1", "/BaseFont": "/Helvetica", "/MarkerF1secret": True}},
    "obj:11 0 R": {"value": {"/Type": "/Font", "/Subtype": "/Type1", "/BaseFont": "/Helvetica", "/MarkerF2public": True}},
    "obj:13 0 R": {"stream": {"dict": {"/Type": "/XObject", "/Subtype": "/Form", "/BBox": [0, 0, 300, 50],
                                       "/Resources": {"/Font": {"/F1": "10 0 R"}}},
                              "data": b("BT /F1 12 Tf 0 10 Td (SECRETFORM) Tj ET")}},
    "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842], "/Resources": "9 0 R", "/Contents": "4 0 R"}},
    "obj:4 0 R": {"stream": {"dict": {}, "data": b("BT /F1 12 Tf 50 700 Td (SECRETBODY) Tj ET q 1 0 0 1 50 600 cm /X1 Do Q")}},
    "obj:5 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842], "/Resources": "9 0 R", "/Contents": "6 0 R"}},
    "obj:6 0 R": {"stream": {"dict": {}, "data": b("BT /F2 12 Tf 50 700 Td (PUBLIC2) Tj ET")}},
    "obj:7 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842],
                            "/Resources": {"/Font": {"/F1": "10 0 R", "/F2": "11 0 R"}, "/XObject": {"/X1": "13 0 R"}}, "/Contents": "8 0 R"}},
    "obj:8 0 R": {"stream": {"dict": {}, "data": b("BT /F2 12 Tf 50 700 Td (PUBLIC3) Tj ET\n<< /A [ 1 2 \n( unterminated string")}},
    "obj:12 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842],
                             "/Resources": {"/Font": {"/F1": "10 0 R", "/F2": "11 0 R"}, "/XObject": {"/X1": "13 0 R"}}, "/Contents": "14 0 R"}},
    "obj:14 0 R": {"stream": {"dict": {}, "data": b("BT /F2 12 Tf 50 700 Td (PUBLIC4) Tj ET")}},
    "trailer": {"value": {"/Root": "1 0 R", "/Size": 15}}})


# 3. Flate "bomb" images (20000^2 and 40000^2 gray) drawn over a blue square.
def bomb(side):
    c = zlib.compressobj(9)
    row = bytes([0x80]) * side
    return b"".join([c.compress(row) for _ in range(side)] + [c.flush()])


objs, kids, n = {"obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}}}, [], 3
for side in (20000, 40000):
    objs[f"obj:{n} 0 R"] = {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842],
                                      "/Resources": {"/XObject": {"/Im0": f"{n + 2} 0 R"}}, "/Contents": f"{n + 1} 0 R"}}
    objs[f"obj:{n + 1} 0 R"] = {"stream": {"dict": {}, "data": b("0 0 1 rg 50 50 100 100 re f q 495 0 0 742 50 50 cm /Im0 Do Q")}}
    objs[f"obj:{n + 2} 0 R"] = {"stream": {"dict": {"/Type": "/XObject", "/Subtype": "/Image", "/Width": side, "/Height": side,
                                                    "/ColorSpace": "/DeviceGray", "/BitsPerComponent": 8, "/Filter": "/FlateDecode"},
                                           "data": base64.b64encode(bomb(side)).decode()}}
    kids.append(f"{n} 0 R")
    n += 3
objs["obj:2 0 R"] = {"value": {"/Type": "/Pages", "/Count": len(kids), "/Kids": kids}}
objs["trailer"] = {"value": {"/Root": "1 0 R", "/Size": n}}
save("bomb.json", objs)

# 4. Text field with /V but no appearance, /NeedAppearances true.
save("needap.json", {
    "obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R", "/AcroForm": {
        "/Fields": ["5 0 R"], "/NeedAppearances": True, "/DA": "u:/Helv 0 Tf 0 g", "/DR": {"/Font": {"/Helv": "6 0 R"}}}}},
    "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": 1, "/Kids": ["3 0 R"]}},
    "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842], "/Resources": {},
                            "/Contents": "4 0 R", "/Annots": ["5 0 R"]}},
    "obj:4 0 R": {"stream": {"dict": {}, "data": ""}},
    "obj:5 0 R": {"value": {"/Type": "/Annot", "/Subtype": "/Widget", "/FT": "/Tx", "/T": "u:name", "/V": "u:FIELD VALUE 12345",
                            "/Rect": [50, 700, 400, 740], "/P": "3 0 R", "/DA": "u:/Helv 24 Tf 0 g", "/F": 4}},
    "obj:6 0 R": {"value": {"/Type": "/Font", "/Subtype": "/Type1", "/BaseFont": "/Helvetica", "/Encoding": "/WinAnsiEncoding"}},
    "trailer": {"value": {"/Root": "1 0 R", "/Size": 7}}})
for ga in (0, 1):
    job = {"inputFile": "needap.pdf", "outputFile": f"needap-flat-{ga}.pdf",
           "flattenAnnotations": "all", "removeAcroform": "", "removeStructure": ""}
    if ga:
        job["generateAppearances"] = ""
    json.dump(job, open(f"{w}/needap-job-{ga}.json", "w"))

# 5. 600 dpi A4 scan-like PPM (4960 x 7016), encoded later as baseline and progressive JPEG.
random.seed(3)
W, H = 4960, 7016
pats = []
for _ in range(64):
    row = bytearray([240]) * (W * 3)
    for x in range(300, W - 300, 3):
        if random.random() < 0.3:
            v = random.randint(0, 120)
            row[x * 3:x * 3 + 9] = bytes((v, v, v)) * 3
    pats.append(bytes(row))
blank = bytes([245]) * (W * 3)
with open(f"{w}/scan600.ppm", "wb") as f:
    f.write(f"P6\n{W} {H}\n255\n".encode())
    for y in range(H):
        f.write(pats[(y * 7) % 64] if (y // 80) % 2 == 0 else blank)
