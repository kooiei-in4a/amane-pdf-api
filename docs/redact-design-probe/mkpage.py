"""Write a qpdf JSON v2 one-page PDF whose only content is a full-page DCT image."""
import json, struct, sys
jpg, out, wpt, hpt = sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
d = open(jpg, "rb").read()
i = 2
while True:  # find SOF0/SOF2 for dimensions
    assert d[i] == 0xFF
    m = d[i+1]; L = struct.unpack(">H", d[i+2:i+4])[0]
    if m in (0xC0, 0xC1, 0xC2):
        h, w = struct.unpack(">HH", d[i+5:i+9]); comps = d[i+9]; break
    i += 2 + L
content = f"q {wpt:.4f} 0 0 {hpt:.4f} 0 0 cm /Im0 Do Q".encode()
import base64
doc = {"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, {
  "obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
  "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": 1, "/Kids": ["3 0 R"]}},
  "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, wpt, hpt],
                          "/Resources": {"/XObject": {"/Im0": "5 0 R"}}, "/Contents": "4 0 R"}},
  "obj:4 0 R": {"stream": {"dict": {}, "data": base64.b64encode(content).decode()}},
  "obj:5 0 R": {"stream": {"dict": {"/Type": "/XObject", "/Subtype": "/Image", "/Width": w, "/Height": h,
                                    "/ColorSpace": "/DeviceRGB" if comps == 3 else "/DeviceGray",
                                    "/BitsPerComponent": 8, "/Filter": "/DCTDecode"},
                           "datafile": sys.argv[5]}},
  "trailer": {"value": {"/Root": "1 0 R", "/Size": 6}}}]}
json.dump(doc, open(out, "w"))
print(w, h, comps)
