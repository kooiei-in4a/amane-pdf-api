"""Issue #29 design probe: compose content-stream matrices of the overlaid
output and convert the image's unit square into display coordinates.

Usage: python3 -I analyze.py WORK_DIR
Requires host qpdf (files are *.pdf / *.json only).
"""
import base64
import json
import re
import subprocess
import sys
from pathlib import Path

TOKEN = re.compile(rb"/[^\s/\[\]()<>{}%]+|<<|>>|\[|\]|\((?:\\.|[^\\)])*\)|[+-]?(?:\d+\.?\d*|\.\d+)|[A-Za-z'\"*]+|\S")


def mul(a, b):  # a then b (row-vector convention, PDF: [a b c d e f])
    return [a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3], a[2] * b[0] + a[3] * b[2],
            a[2] * b[1] + a[3] * b[3], a[4] * b[0] + a[5] * b[2] + b[4], a[4] * b[1] + a[5] * b[3] + b[5]]


def apply(m, x, y):
    return m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]


def load(pdf: Path):
    out = subprocess.run(["qpdf", "--json=2", "--json-stream-data=inline", "--decode-level=generalized", str(pdf), "-"],
                         capture_output=True, check=True).stdout
    doc = json.loads(out)
    objs = doc["qpdf"][1]
    pages = [p["object"] for p in doc["pages"]]
    return objs, pages


def resolve(objs, v):
    while isinstance(v, str) and re.fullmatch(r"\d+ \d+ R", v):
        o = objs[f"obj:{v}"]
        v = o.get("value", o.get("stream"))
    return v


def stream_dict(objs, ref):
    o = objs[f"obj:{ref}"]
    return o["stream"]["dict"], base64.b64decode(o["stream"].get("data", ""))


def walk(objs, data, resources, ctm, found, depth=0):
    stack, ops = [], []
    gs = [ctm]
    res = resolve(objs, resources) or {}
    xobjs = resolve(objs, res.get("/XObject", {})) or {}
    for tok in TOKEN.findall(data):
        if re.fullmatch(rb"[+-]?(?:\d+\.?\d*|\.\d+)", tok):
            stack.append(float(tok)); continue
        if tok.startswith(b"/"):
            stack.append(tok.decode()); continue
        op = tok.decode("latin1")
        if op == "q": gs.append(gs[-1])
        elif op == "Q": gs.pop()
        elif op == "cm":
            m = stack[-6:]; gs[-1] = mul(m, gs[-1])
        elif op == "Do":
            name = stack[-1]; ref = xobjs[name]
            d, body = stream_dict(objs, ref)
            if d.get("/Subtype") == "/Image":
                found.append(dict(ctm=gs[-1], ref=ref, dict=d))
            else:
                m = d.get("/Matrix", [1, 0, 0, 1, 0, 0])
                walk(objs, body, d.get("/Resources", {}), mul(m, gs[-1]), found, depth + 1)
        if op.isalpha() or op in ("'", '"'):
            stack = []
    return found


def display(pt, crop, rotate, unit):
    x0, y0, x1, y1 = crop
    X, Y = (pt[0] - x0) * unit, (pt[1] - y0) * unit
    Wc, Hc = (x1 - x0) * unit, (y1 - y0) * unit
    r = rotate % 360
    if r == 0: return X, Hc - Y
    if r == 90: return Y, X
    if r == 180: return Wc - X, Y
    return Hc - Y, Wc - X


def main(work: Path):
    cases = {c["id"]: c for c in json.loads((work / "cases.json").read_text())}
    rows = []
    worst = {"stamp": 0.0, "create": 0.0}
    for c in cases.values():
        if c["id"].startswith("fmt-"): continue
        for m in ("stamp", "create"):
            pdf = work / "host" / f"{c['id']}-{m}.pdf"
            objs, pages = load(pdf)
            page = resolve(objs, pages[0])
            contents = page["/Contents"]
            refs = contents if isinstance(contents, list) else [contents]
            data = b"\n".join(stream_dict(objs, r)[1] for r in refs)
            found = walk(objs, data, page.get("/Resources", {}), [1, 0, 0, 1, 0, 0], [])
            assert len(found) == 1, (c["id"], m, len(found))
            ctm = found[0]["ctm"]
            tl = display(apply(ctm, 0, 1), c["crop"], c["rotate"], c["unit"])
            tr = display(apply(ctm, 1, 1), c["crop"], c["rotate"], c["unit"])
            bl = display(apply(ctm, 0, 0), c["crop"], c["rotate"], c["unit"])
            exp_tl = (c["left"], c["top"])
            exp_tr = (c["left"] + c["w"], c["top"])
            exp_bl = (c["left"], c["top"] + c["h"])
            err = max(abs(a - b) for p, q in ((tl, exp_tl), (tr, exp_tr), (bl, exp_bl)) for a, b in zip(p, q))
            worst[m] = max(worst[m], err)
            # page 2 must not reference any image
            p2 = resolve(objs, pages[1])
            rows.append((c["id"], m, f"{err:.6f}", f"tl=({tl[0]:.3f},{tl[1]:.3f}) exp=({exp_tl[0]:.3f},{exp_tl[1]:.3f})",
                         f"w={c['w']:.3f} h={c['h']:.3f}", "p2-xobj" if "/XObject" in json.dumps(p2.get("/Resources", {})) else "p2-clean"))
    for r in rows: print("\t".join(r))
    print("worst display error pt per method:", worst)


if __name__ == "__main__":
    main(Path(sys.argv[1]))
