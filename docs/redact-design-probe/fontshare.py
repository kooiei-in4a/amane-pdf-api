"""Spike prototype: which non-target pages share font data with target pages (qpdf JSON v2, pruned doc)."""
import json
import sys

FONT_SUBTYPES = {"/Type0", "/Type1", "/MMType1", "/Type3", "/TrueType", "/CIDFontType0", "/CIDFontType2"}
d = json.load(open(sys.argv[1]))
targets = {int(x) for x in sys.argv[2].split(",")}
objs = d["qpdf"][1]


def content(wrapper):
    return wrapper["value"] if "value" in wrapper else wrapper["stream"]["dict"]


def resolve(v):
    seen = 0
    while isinstance(v, str) and v.endswith(" R") and ("obj:" + v) in objs:
        v = content(objs["obj:" + v]); seen += 1
        assert seen < 32
    return v


def is_font(v):
    return isinstance(v, dict) and (v.get("/Type") == "/Font" or (v.get("/Subtype") in FONT_SUBTYPES and "/BaseFont" in v))


def font_keys(font_value, font_ref, keys):
    """Every indirect object reachable from a font (descriptor, programs, ToUnicode, CharProcs...),
    plus the font itself (ref, or canonical text for a direct dict)."""
    keys.add(font_ref if font_ref else "direct:" + json.dumps(font_value, sort_keys=True))
    stack = [font_value]
    while stack:
        v = stack.pop()
        if isinstance(v, str) and v.endswith(" R") and ("obj:" + v) in objs:
            if v in keys: continue
            keys.add(v); stack.append(content(objs["obj:" + v])); continue
        if isinstance(v, dict):
            stack.extend(x for k, x in v.items() if k != "/Parent")
        elif isinstance(v, list):
            stack.extend(v)


def page_fonts(page_ref):
    keys, seen_res = set(), set()
    stack = [content(objs["obj:" + page_ref]).get("/Resources")]
    while stack:
        res = stack.pop()
        rkey = res if isinstance(res, str) else None
        if rkey:
            if rkey in seen_res: continue
            seen_res.add(rkey)
        res = resolve(res)
        if not isinstance(res, dict): continue
        fonts = resolve(res.get("/Font"))
        if isinstance(fonts, dict):
            for raw in fonts.values():
                f = resolve(raw)
                if is_font(f):
                    font_keys(f, raw if isinstance(raw, str) else None, keys)
                    if f.get("/Subtype") == "/Type3" and "/Resources" in f: stack.append(f["/Resources"])
        for cat in ("/XObject", "/Pattern"):
            group = resolve(res.get(cat))
            if isinstance(group, dict):
                for raw in group.values():
                    x = resolve(raw)
                    if isinstance(x, dict) and "/Resources" in x: stack.append(x["/Resources"])
    return keys


pages = [p["object"] for p in d["pages"]]
fk = {i + 1: page_fonts(ref) for i, ref in enumerate(pages)}
target_keys = set().union(*(fk[t] for t in targets))
shared = sorted(p for p in fk if p not in targets and fk[p] & target_keys)
print("font keys per page:", {p: len(k) for p, k in fk.items()})
print("targets", sorted(targets), "-> also rasterize", shared)
