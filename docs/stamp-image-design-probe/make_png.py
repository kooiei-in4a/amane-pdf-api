"""Issue #29 design probe: generate synthetic PNG fixtures (no user data).

Usage: python3 -I make_png.py OUT_DIR
"""
import json
import struct
import sys
import zlib
from pathlib import Path

import numpy as np

SIG = b"\x89PNG\r\n\x1a\n"


def chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def pack_rows(arr: np.ndarray, depth: int) -> list[bytes]:
    """arr: (h, w, c) uint values. Returns raw scanlines without filter byte."""
    h, w, c = arr.shape
    rows = []
    for y in range(h):
        row = arr[y].reshape(-1)
        if depth == 16:
            rows.append(row.astype(">u2").tobytes())
        elif depth == 8:
            rows.append(row.astype(np.uint8).tobytes())
        else:
            per = 8 // depth
            bits = row.astype(np.uint8)
            pad = (-len(bits)) % per
            bits = np.concatenate([bits, np.zeros(pad, np.uint8)]).reshape(-1, per)
            out = np.zeros(len(bits), np.uint16)
            for i in range(per):
                out = (out << depth) | bits[:, i]
            rows.append(out.astype(np.uint8).tobytes())
    return rows


ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]


def encode(arr, color, depth, interlace=False, plte=None, trns=None, extra=b"", idat=None, filt=0):
    h, w, _ = arr.shape
    ihdr = struct.pack(">IIBBBBB", w, h, depth, color, 0, 0, 1 if interlace else 0)
    raw = b""
    if interlace:
        for x0, y0, dx, dy in ADAM7:
            sub = arr[y0::dy, x0::dx]
            if sub.size == 0:
                continue
            raw += b"".join(bytes([0]) + r for r in pack_rows(sub, depth))
    else:
        raw = b"".join(bytes([filt]) + r for r in pack_rows(arr, depth))
    data = zlib.compress(raw, 9) if idat is None else idat(raw)
    out = SIG + chunk(b"IHDR", ihdr)
    if plte is not None:
        out += chunk(b"PLTE", plte)
    if trns is not None:
        out += chunk(b"tRNS", trns)
    out += extra
    out += chunk(b"IDAT", data) + chunk(b"IEND", b"")
    return out


def corner_rgba(w, h):
    """Asymmetric: TL red, TR green, BL blue, BR yellow (opaque 1/4 squares),
    centre transparent, a half-alpha black vertical band in the middle."""
    a = np.zeros((h, w, 4), np.uint8)
    qw, qh = w // 4, h // 4
    a[:qh, :qw] = [255, 0, 0, 255]
    a[:qh, -qw:] = [0, 160, 0, 255]
    a[-qh:, :qw] = [0, 0, 255, 255]
    a[-qh:, -qw:] = [255, 200, 0, 255]
    a[qh:-qh, w // 2 - qw // 4:w // 2 + qw // 4] = [0, 0, 0, 128]
    return a


def main(out: Path):
    out.mkdir(parents=True, exist_ok=True)
    files = {}
    w, h = 300, 200
    rgba = corner_rgba(w, h)
    files["rgba8"] = encode(rgba, 6, 8)
    files["rgba8-adam7"] = encode(rgba, 6, 8, interlace=True)
    files["rgba16"] = encode(rgba.astype(np.uint16) * 257, 6, 16)
    files["rgba16-adam7"] = encode(rgba.astype(np.uint16) * 257, 6, 16, interlace=True)
    rgb = rgba[:, :, :3].copy(); rgb[rgba[:, :, 3] == 0] = [255, 0, 255]
    files["rgb8"] = encode(rgb, 2, 8)
    files["rgb8-trns"] = encode(rgb, 2, 8, trns=struct.pack(">HHH", 255, 0, 255))
    files["rgb16"] = encode(rgb.astype(np.uint16) * 257, 2, 16)
    gray = rgba[:, :, :1].copy(); gray[rgba[:, :, 3] == 0] = 77
    la = np.concatenate([gray, rgba[:, :, 3:]], axis=2)
    files["gray8"] = encode(gray, 0, 8)
    files["gray8-trns"] = encode(gray, 0, 8, trns=struct.pack(">H", 77))
    files["gray16"] = encode(gray.astype(np.uint16) * 257, 0, 16)
    files["ga8"] = encode(la, 4, 8)
    files["ga16"] = encode(la.astype(np.uint16) * 257, 4, 16)
    g1 = (gray > 100).astype(np.uint8)
    files["gray1"] = encode(g1, 0, 1)
    # Palette: 0 transparent magenta, 1 red, 2 green, 3 blue, 4 yellow, 5 half black.
    pal = [(255, 0, 255), (255, 0, 0), (0, 160, 0), (0, 0, 255), (255, 200, 0), (0, 0, 0)]
    idx = np.zeros((h, w, 1), np.uint8)
    for i, c in enumerate([(255, 0, 0, 255), (0, 160, 0, 255), (0, 0, 255, 255), (255, 200, 0, 255), (0, 0, 0, 128)], 1):
        idx[(rgba == c).all(axis=2)] = i
    plte = b"".join(bytes(p) for p in pal)
    files["pal8-trns"] = encode(idx, 3, 8, plte=plte, trns=bytes([0, 255, 255, 255, 255, 128]))
    files["pal8"] = encode(idx, 3, 8, plte=plte)
    files["pal4-trns"] = encode(idx, 3, 4, plte=plte, trns=bytes([0, 255, 255, 255, 255, 128]))
    files["pal8-adam7-trns"] = encode(idx, 3, 8, interlace=True, plte=plte, trns=bytes([0, 255, 255, 255, 255, 128]))
    # Corruption cases (CRCs valid; the zlib payload is wrong).
    files["bad-zlib"] = encode(rgba, 6, 8, idat=lambda raw: b"\x78\x9c" + bytes(64) + b"\x00" * 4)
    files["bad-adler"] = encode(rgba, 6, 8, idat=lambda raw: zlib.compress(raw, 9)[:-4] + b"\x00\x00\x00\x00")
    files["short-data"] = encode(rgba, 6, 8, idat=lambda raw: zlib.compress(raw[: len(raw) // 2], 9))
    files["long-data"] = encode(rgba, 6, 8, idat=lambda raw: zlib.compress(raw + bytes(5000), 9))
    files["bad-filter"] = encode(rgba, 6, 8, filt=7)
    # 2000x2000 fixtures.
    big = np.zeros((2000, 2000, 4), np.uint8)
    big[:, :, :] = corner_rgba(2000, 2000)
    files["big-rgba8"] = encode(big, 6, 8)
    # Adversarial: Up filter makes every row a constant delta, but raw rows have no
    # LZ77 matches within the 32 KiB window -> tiny PNG, large unpredicted Flate.
    rng = np.random.default_rng(29)
    base = rng.integers(0, 256, (1, 2000, 4), dtype=np.uint16)
    adv = ((base + np.arange(2000, dtype=np.uint16).reshape(-1, 1, 1) * 1) % 256).astype(np.uint8)
    adv[:, :, 3] = 255
    def up_filtered(raw_rows_arr):
        rows = pack_rows(raw_rows_arr, 8)
        prev = bytes(len(rows[0]))
        out = b""
        for r in rows:
            out += b"\x02" + bytes((a - b) & 255 for a, b in zip(r, prev))
            prev = r
        return out
    adv_idat = zlib.compress(up_filtered(adv), 9)
    files["adv-rgba8"] = SIG + chunk(b"IHDR", struct.pack(">IIBBBBB", 2000, 2000, 8, 6, 0, 0, 0)) + chunk(b"IDAT", adv_idat) + chunk(b"IEND", b"")
    # 16-bit variant: high bytes repeat per column, low bytes +1 per row (Up filter -> tiny PNG).
    hi = rng.integers(0, 256, (2000 * 4,), dtype=np.uint8)
    lo0 = rng.integers(0, 256, (2000 * 4,), dtype=np.uint8)
    co = zlib.compressobj(9); data = b""; prev = bytes(2000 * 8)
    for i in range(2000):
        row = np.empty(2000 * 8, np.uint8); row[0::2] = hi; row[1::2] = ((lo0.astype(np.uint16) + i) % 256).astype(np.uint8)
        rb = row.tobytes()
        data += co.compress((b"\x02" + bytes((a - b) & 255 for a, b in zip(rb, prev))) if i else (b"\x00" + rb)); prev = rb
    data += co.flush()
    files["adv-rgba16"] = SIG + chunk(b"IHDR", struct.pack(">IIBBBBB", 2000, 2000, 16, 6, 0, 0, 0)) + chunk(b"IDAT", data) + chunk(b"IEND", b"")
    files["big-rgba16-adam7"] = encode(big.astype(np.uint16) * 257, 6, 16, interlace=True)
    meta = {}
    for name, data in files.items():
        (out / f"{name}.png").write_bytes(data)
        meta[name] = len(data)
    (out / "sizes.json").write_text(json.dumps(meta, indent=1))
    print(json.dumps(meta, indent=1))


if __name__ == "__main__":
    main(Path(sys.argv[1]))
