"""Probe result checks (host side, reads files written by the container steps).

dark <pgm> [x0 y0 x1 y1]      count pixels < 128 (optionally inside a box)
center <pgm>                  value of the centre pixel
tounicode <qdf.pdf> <chars>   which chars appear in any ToUnicode CMap
markers <qdf.pdf>             MK_* and PUBLIC_* strings present
"""
import re
import sys

sys.path.insert(0, __import__("os").path.dirname(__file__))
from ppm import px, read  # noqa: E402

cmd = sys.argv[1]
if cmd == "dark":
    w, h, ch, d = read(sys.argv[2])
    if len(sys.argv) > 3:
        x0, y0, x1, y1 = map(int, sys.argv[3:7])
        print(sum(1 for y in range(y0, y1) for x in range(x0, x1) if d[y * w + x] < 128))
    else:
        print(sum(1 for v in d if v < 128))
elif cmd == "center":
    img = read(sys.argv[2])
    print(px(img, img[0] // 2, img[1] // 2)[0])
elif cmd == "tounicode":
    data = open(sys.argv[2], "rb").read()
    found = set()
    for block in re.findall(rb"beginbf(?:char|range)(.*?)endbf(?:char|range)", data, re.S):
        for m in re.finditer(rb"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>(?:\s*<([0-9A-Fa-f]+)>)?", block):
            try:
                found.add(bytes.fromhex((m.group(3) or m.group(2)).decode()).decode("utf-16-be"))
            except (ValueError, UnicodeDecodeError):
                pass
    print(" ".join(f"{c}:{'present' if c in found else 'absent'}" for c in sys.argv[3]))
elif cmd == "markers":
    data = open(sys.argv[2], "rb").read()
    print("MK:", sorted({m.decode() for m in re.findall(rb"MK_[A-Z0-9_]+", data)}),
          "PUBLIC:", sorted({m.decode() for m in re.findall(rb"PUBLIC_?[A-Z0-9]+", data)}))
