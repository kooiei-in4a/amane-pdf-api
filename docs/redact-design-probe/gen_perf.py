"""Spike: scan-like page PPM (300 dpi A4) and a vector-heavy page qpdf JSON."""
import base64, json, random, sys
w = sys.argv[1]
random.seed(7)
W, H = 2480, 3508
rows = bytearray()
for y in range(H):
    base = 235 + (y * 13 % 17)
    row = bytearray([base]) * (W * 3)
    if (y // 40) % 3 == 0 and y % 40 < 24:   # text-like dark bands with noise
        for x in range(200, W - 200):
            if random.random() < 0.35:
                v = random.randint(0, 90)
                row[x*3:x*3+3] = bytes((v, v, v))
    else:
        for x in range(0, W, 7):
            v = base - random.randint(0, 25)
            row[x*3:x*3+3] = bytes((v, v - 2 if v > 2 else 0, v))
    rows += row
open(f"{w}/scan.ppm", "wb").write(f"P6\n{W} {H}\n255\n".encode() + bytes(rows))
# vector-heavy: 40k small filled rects + 20k line segments
ops = ["0.2 g"]
for i in range(40000):
    x, y = random.uniform(20, 570), random.uniform(20, 820)
    ops.append(f"{x:.2f} {y:.2f} 1.5 1.5 re f")
ops.append("0 0 1 RG 0.3 w")
for i in range(20000):
    x, y = random.uniform(20, 570), random.uniform(20, 820)
    ops.append(f"{x:.2f} {y:.2f} m {x+random.uniform(-20,20):.2f} {y+random.uniform(-20,20):.2f} l S")
content = "\n".join(ops).encode()
doc = {"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, {
  "obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
  "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": 2, "/Kids": ["3 0 R", "5 0 R"]}},
  "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842], "/Resources": {}, "/Contents": "4 0 R"}},
  "obj:4 0 R": {"stream": {"dict": {}, "data": base64.b64encode(content).decode()}},
  # A2 page (1191 x 1684 pt) with same content scaled: ~15.5 Mpx at 200 dpi
  "obj:5 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 1191, 1684], "/Resources": {}, "/Contents": "6 0 R"}},
  "obj:6 0 R": {"stream": {"dict": {}, "data": base64.b64encode(b"q 2 0 0 2 0 0 cm\n" + content + b"\nQ").decode()}},
  "trailer": {"value": {"/Root": "1 0 R", "/Size": 7}}}]}
json.dump(doc, open(f"{w}/vector.json", "w"))
