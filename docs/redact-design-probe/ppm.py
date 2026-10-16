import sys
def read(path):
    d = open(path, "rb").read()
    parts = []; i = 0
    while len(parts) < 4:
        while d[i:i+1].isspace(): i += 1
        if d[i:i+1] == b"#":
            while d[i:i+1] != b"\n": i += 1
            continue
        j = i
        while not d[j:j+1].isspace(): j += 1
        parts.append(d[i:j]); i = j
    i += 1
    w, h = int(parts[1]), int(parts[2]); ch = 3 if parts[0] == b"P6" else 1
    return w, h, ch, d[i:]
def px(img, x, y):
    w, h, ch, data = img; o = (y * w + x) * ch; return tuple(data[o:o+ch])
