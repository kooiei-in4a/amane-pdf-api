"""Issue #29 design probe: write qpdf JSON inputs and a shell plan for Docker.

Mimics PdfOverlayBuilder for placement purposes: the target page already has
MediaBox = CropBox = TrimBox = crop and normalized Rotate (the Builder's
pre-overlay adjustment), the blank page is unrotated with MediaBox
[0 0 W H] in pt (W/H = display size incl. UserUnit), pdfcpu stamps the
blank, qpdf --overlay merges it.

Usage: python3 -I make_cases.py WORK_DIR
"""
import json
import sys
from decimal import Decimal
from pathlib import Path


def fmt(value: float) -> str:
    # Fixed point, no exponent: what the C# design proposes ("0.######").
    text = f"{value:.6f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text


def blank_json(w, h):
    return {"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, {
        "obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
        "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": 1, "/Kids": ["3 0 R"]}},
        "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, w, h], "/Resources": {}}},
        "trailer": {"value": {"/Root": "1 0 R", "/Size": 4}}}]}


def target_json(crop, rotate, unit, pages=2):
    objs = {"obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
            "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": pages, "/Kids": [f"{3 + 2 * i} 0 R" for i in range(pages)]}}}
    x0, y0, x1, y1 = crop
    for i in range(pages):
        page = {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, x1 + 40, y1 + 40], "/CropBox": list(crop),
                "/TrimBox": list(crop), "/Rotate": rotate, "/Resources": {}, "/Contents": f"{4 + 2 * i} 0 R"}
        if unit != 1:
            page["/UserUnit"] = unit
        objs[f"obj:{3 + 2 * i} 0 R"] = {"value": page}
        # Light grey frame inside the crop so the page is not empty.
        data = f"0.9 g {x0 + 5} {y0 + 5} {x1 - x0 - 10} {y1 - y0 - 10} re f\n".encode()
        import base64
        objs[f"obj:{4 + 2 * i} 0 R"] = {"stream": {"dict": {}, "data": base64.b64encode(data).decode()}}
    objs["trailer"] = {"value": {"/Root": "1 0 R", "/Size": 3 + 2 * pages}}
    return {"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objs]}


def placement(W, H, px_w, px_h, x, y, width):
    """The design's pure function with decimal inputs, double geometry."""
    w = float(Decimal(width)) * W
    h = w * px_h / px_w
    left = float(Decimal(x)) * W
    top = float(Decimal(y)) * H
    bottom = H - top - h
    return dict(w=w, h=h, left=left, top=top, bottom=bottom, scale=w / px_w)


def main(work: Path):
    (work / "json").mkdir(parents=True, exist_ok=True)
    cases = []
    crop = (50, 80, 650, 880)  # 600 x 800 user units, offset origin
    geometries = []
    for r in (0, 90, 180, 270):
        geometries.append((f"r{r}-u1", crop, r, 1))
    geometries += [("r90-u2", crop, 90, 2), ("r270-u0.5", crop, 270, 0.5), ("r0-u1-mediaonly", (0, 0, 595, 842), 0, 1)]
    stamps = [("ex", "0.72", "0.08", "0.12"), ("tl", "0", "0", "0.5"), ("br-edge", None, None, "0.2")]
    for name, crop_box, r, u in geometries:
        cw, ch = crop_box[2] - crop_box[0], crop_box[3] - crop_box[1]
        W = (ch if r in (90, 270) else cw) * u
        H = (cw if r in (90, 270) else ch) * u
        (work / "json" / f"target-{name}.json").write_text(json.dumps(target_json(crop_box, r, u)))
        (work / "json" / f"blank-{name}.json").write_text(json.dumps(blank_json(W, H)))
        for sname, x, y, width in stamps:
            px_w, px_h = 300, 200
            if x is None:  # exactly touching the bottom-right corner
                wv = float(Decimal(width)) * W
                hv = wv * px_h / px_w
                x = str((Decimal(1) - Decimal(width)).quantize(Decimal("0.0001")))
                y = str(((Decimal(H) - Decimal(hv)) / Decimal(H)).quantize(Decimal("0.0001"), rounding="ROUND_FLOOR"))
            p = placement(W, H, px_w, px_h, x, y, width)
            desc = f"pos:bl, off:{fmt(p['left'])} {fmt(p['bottom'])}, scale:{fmt(p['scale'])} abs, rot:0"
            cases.append(dict(id=f"{name}-{sname}", geometry=name, crop=crop_box, rotate=r, unit=u, W=W, H=H,
                              x=x, y=y, width=width, desc=desc, **p, image="rgba8"))
    # Format matrix: every fixture on one plain page.
    for img in ["rgba8", "rgba8-adam7", "rgba16", "rgba16-adam7", "rgb8", "rgb8-trns", "rgb16", "gray8", "gray8-trns",
                "gray16", "ga8", "ga16", "gray1", "pal8-trns", "pal8", "pal4-trns", "pal8-adam7-trns",
                "bad-zlib", "bad-adler", "short-data", "long-data", "bad-filter", "big-rgba8", "adv-rgba8",
                "big-rgba16-adam7", "adv-rgba16"]:
        W, H = 400.0, 600.0
        p = placement(W, H, 300, 200, "0.1", "0.1", "0.5")
        if img.startswith(("big", "adv")):
            p = placement(W, H, 2000, 2000, "0.1", "0.1", "0.5")
        desc = f"pos:bl, off:{fmt(p['left'])} {fmt(p['bottom'])}, scale:{fmt(p['scale'])} abs, rot:0"
        cases.append(dict(id=f"fmt-{img}", geometry="fmt", crop=(0, 0, 400, 600), rotate=0, unit=1, W=W, H=H,
                          x="0.1", y="0.1", width="0.5", desc=desc, **p, image=img))
    (work / "json" / "target-fmt.json").write_text(json.dumps(target_json((0, 0, 400, 600), 0, 1)))
    (work / "json" / "blank-fmt.json").write_text(json.dumps(blank_json(400.0, 600.0)))
    (work / "cases.json").write_text(json.dumps(cases, indent=1))
    for c in cases:
        layer = {"origin": "LowerLeft", "pages": {"1": {"crop": f"[0 0 {c['W']!r} {c['H']!r}]", "content": {"image": [
            {"src": "/tmp/j/stamp.png", "pos": [c["left"], c["bottom"]], "width": c["w"], "height": c["h"], "rot": 0}]}}}}
        (work / "json" / f"create-{c['id']}.json").write_text(json.dumps(layer))
    with open(work / "plan.tsv", "w") as f:
        for c in cases:
            f.write("\t".join([c["id"], c["geometry"], c["image"], c["desc"]]) + "\n")
    print(len(cases), "cases")


if __name__ == "__main__":
    main(Path(sys.argv[1]))
