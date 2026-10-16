"""Probe update JSON writers.

strip <input-inspect.json> <out-update.json>
    What PdfEmbeddedFileStripper does: drop /AF /EF /RF everywhere, FileAttachment annotations,
    and the catalog /Names /EmbeddedFiles tree (object numbers come from the inspected file).
final <pruned.json> <out-update.json> <page>=<jpeg-container-path>:<jpeg-host-path>[,...]
    Final single write: catalog and page-tree allowlist, new image-only pages replacing the
    given pages, /Annots removed from every remaining page.
"""
import base64
import json
import struct
import sys


def content(x):
    return x["value"] if "value" in x else x["stream"]["dict"]


def strip(src, dst):
    d = json.load(open(src))
    o = d["qpdf"][1]
    attachments = {k[4:] for k, x in o.items() if k != "trailer" and isinstance(content(x), dict)
                   and content(x).get("/Subtype") == "/FileAttachment"}

    def clean(v, annots=False):
        if isinstance(v, dict):
            return {k: clean(x, k == "/Annots") for k, x in v.items() if k not in ("/AF", "/EF", "/RF")}
        if isinstance(v, list):
            return [clean(i) for i in v if not (annots and isinstance(i, str) and i in attachments)]
        return v

    root = o["trailer"]["value"]["/Root"]
    upd = {}
    for k, x in o.items():
        if k == "trailer":
            continue
        if k[4:] in attachments:
            upd[k] = {"value": None}
        elif "value" in x:
            v = clean(x["value"])
            if k[4:] == root and isinstance(v.get("/Names"), dict):
                v["/Names"] = {n: t for n, t in v["/Names"].items() if n != "/EmbeddedFiles"}
            if v != x["value"]:
                upd[k] = {"value": v}
        else:
            dd = clean(x["stream"]["dict"])
            if dd != x["stream"]["dict"]:
                upd[k] = {"stream": {"dict": dd}}
    json.dump({"qpdf": [{"jsonversion": 2}, upd]}, open(dst, "w"))


def jpeg_size(path):
    d = open(path, "rb").read()
    i = 2
    while True:
        marker, length = d[i + 1], struct.unpack(">H", d[i + 2:i + 4])[0]
        if marker in (0xC0, 0xC1, 0xC2):
            h, w = struct.unpack(">HH", d[i + 5:i + 9])
            return w, h, d[i + 9]
        i += 2 + length


def final(src, dst, spec):
    d = json.load(open(src))
    header, o = d["qpdf"]
    root = o["trailer"]["value"]["/Root"]
    pages_ref = o["obj:" + root]["value"]["/Pages"]
    kids = [p["object"] for p in d["pages"]]
    upd = {"obj:" + root: {"value": {"/Type": "/Catalog", "/Pages": pages_ref}}}
    next_id = header["maxobjectid"] + 1
    for item in spec.split(","):
        page, paths = item.split("=")
        container_path, host_path = paths.split(":")
        index = int(page) - 1
        mb = content(o["obj:" + kids[index]])["/MediaBox"]   # probe pages: no rotate/crop
        wpt, hpt = mb[2] - mb[0], mb[3] - mb[1]
        w, h, comps = jpeg_size(host_path)
        p, c, im = next_id, next_id + 1, next_id + 2
        next_id += 3
        upd[f"obj:{p} 0 R"] = {"value": {"/Type": "/Page", "/Parent": pages_ref, "/MediaBox": [0, 0, wpt, hpt],
                                         "/Resources": {"/XObject": {"/Im0": f"{im} 0 R"}}, "/Contents": f"{c} 0 R"}}
        upd[f"obj:{c} 0 R"] = {"stream": {"dict": {}, "data": base64.b64encode(
            f"q {wpt} 0 0 {hpt} 0 0 cm /Im0 Do Q".encode()).decode()}}
        upd[f"obj:{im} 0 R"] = {"stream": {"dict": {
            "/Type": "/XObject", "/Subtype": "/Image", "/Width": w, "/Height": h,
            "/ColorSpace": "/DeviceRGB" if comps == 3 else "/DeviceGray", "/BitsPerComponent": 8,
            "/Filter": "/DCTDecode"}, "datafile": container_path}}
        kids[index] = f"{p} 0 R"
    upd["obj:" + pages_ref] = {"value": {"/Type": "/Pages", "/Kids": kids, "/Count": len(kids)}}
    for ref in kids:
        key = "obj:" + ref
        if key in o and "/Annots" in content(o[key]):
            upd[key] = {"value": {k: v for k, v in o[key]["value"].items() if k != "/Annots"}}
    json.dump({"qpdf": [{"jsonversion": 2}, upd]}, open(dst, "w"))


if sys.argv[1] == "strip":
    strip(sys.argv[2], sys.argv[3])
else:
    final(sys.argv[2], sys.argv[3], sys.argv[4])
