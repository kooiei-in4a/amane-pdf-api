#!/usr/bin/env python3
"""差し込みAPIの実配置・文字抽出・実署名・上限負荷を本番Dockerで検証する。"""
import argparse
import base64
import datetime
import importlib.metadata
import importlib.util
import json
import math
from pathlib import Path
import re
import subprocess
import tempfile
import threading
import time

import fitz
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
MiB = 1048576


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


smoke = load("fill_smoke", "docker-smoke.py")
glyphs = load("fill_glyphs", "fill-template-glyphs-validation.py")
numbered = load("fill_geometry", "page-numbers-validation.py")
pdf = load("fill_pdf", "compress-validation.py")
TABLE = glyphs.table()


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, timeout=60)


def field(page=1, text="日本語", **values):
    return dict(page=page, x=.1, y=.1, width=.8, height=.8, fontSize=12, align="left", text=text) | values


def request(api, source, fields, expected=200, reason=None):
    data = json.dumps(fields, ensure_ascii=False, separators=(",", ":"))
    assert len(data.encode()) <= 65536
    body, content_type = smoke.multipart(source, password=None, restriction={"fields": data})
    started = time.monotonic()
    status, headers, output = api.request("POST", "/api/pdf/fill-template", body, content_type)
    seconds = time.monotonic() - started
    assert status == expected, (status, output[:300])
    if expected == 200:
        assert headers["Content-Type"] == "application/pdf"
        assert "filename=filled.pdf" in headers["Content-Disposition"]
    else:
        assert json.loads(output).get("reason") == reason, output
    api.assert_clean()
    return output, seconds


def blank(directory, name, width=14400, height=14400, pages=1):
    objects = {"obj:1 0 R": {"value": {"/Type": "/Catalog", "/Pages": "2 0 R"}},
               "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": pages,
                   "/Kids": [f"{i+3} 0 R" for i in range(pages)]}}}
    for i in range(pages):
        objects[f"obj:{i+3} 0 R"] = {"value": {"/Type": "/Page", "/Parent": "2 0 R",
            "/MediaBox": [0, 0, width, height], "/Resources": {}}}
    objects["trailer"] = {"value": {"/Root": "1 0 R", "/Size": pages + 3}}
    return from_json(directory, name, objects)


def from_json(directory, name, objects):
    source = directory / (name + ".json")
    source.write_text(json.dumps({"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objects]}))
    path = directory / (name + ".pdf")
    command("qpdf", "--json-input", str(source), str(path))
    return path.read_bytes()


def width(text, size):
    return sum(TABLE[ord(c)][1] for c in text) * size / 1000


def positions(f, w, h):
    s = f["fontSize"]
    lines = f["text"].split("\n")
    top = h*(1-f["y"]) - (h*f["height"] - s - 1.2*s*(len(lines)-1))/2
    result = []
    for i, line in enumerate(lines):
        advance = width(line, s)
        x = w*f["x"] + {"left": .05*s, "center": (w*f["width"]-advance)/2,
                         "right": w*f["width"]-.05*s-advance}[f["align"]]
        baseline = top - 1.2*s*i - 1802/2048*s
        result.append((x, baseline, advance, line))
    return result


def geometry(api, directory):
    source, cases = numbered.fixture(directory)
    fields = [field(page=i+1, text="日本A\n差込B", y=y, height=.2, align=align)
              for i in range(1, len(cases)) for y, align in zip((.1, .4, .7), ("left", "center", "right"))]
    result, _ = request(api, source, fields)
    path = directory / "geometry-filled.pdf"
    path.write_bytes(result)
    metadata = json.loads(command("qpdf", "--json=2", "--json-key=pages", "--json-key=qpdf",
        "--json-stream-data=inline", "--decode-level=all", str(path)).stdout)
    objects = metadata["qpdf"][1]
    command("pdftoppm", "-cropbox", "-r", "144", "-png", str(path), str(directory / "geometry"))
    command("pdftoppm", "-f", "1", "-l", "1", "-singlefile", "-cropbox", "-r", "144", "-png",
            str(directory / "source.pdf"), str(directory / "original-cover"))
    cover = next(directory.glob("geometry-0*1.png"))
    assert np.array_equal(np.array(Image.open(cover)), np.array(Image.open(directory / "original-cover.png")))
    for i, (name, media, crop, rotation, unit) in enumerate(cases):
        if i == 0:
            continue
        box = crop or media
        w, h = (box[2]-box[0])*unit, (box[3]-box[1])*unit
        if rotation in (90, 270):
            w, h = h, w
        page = metadata["pages"][i]
        page_dict = objects["obj:" + page["object"]]["value"]
        content = "".join(base64.b64decode(objects["obj:"+ref]["stream"]["data"]).decode("latin1") for ref in page["contents"])
        draw = re.findall(r"([-+\d.eE]+) ([-+\d.eE]+) ([-+\d.eE]+) ([-+\d.eE]+) ([-+\d.eE]+) ([-+\d.eE]+) cm\s+(/\S+) Do", content)[-1]
        expected_matrix = {0: (1/unit, 0, 0, 1/unit, box[0], box[1]),
                           90: (0, 1/unit, -1/unit, 0, box[2], box[1]),
                           180: (-1/unit, 0, 0, -1/unit, box[2], box[3]),
                           270: (0, -1/unit, 1/unit, 0, box[0], box[3])}[rotation]
        assert np.allclose(list(map(float, draw[:6])), expected_matrix, atol=.00002), (name, draw[:6], expected_matrix)
        form_ref = page_dict["/Resources"]["/XObject"][draw[-1]]
        stream = base64.b64decode(objects["obj:"+form_ref]["stream"]["data"]).decode("latin1")
        runs = re.findall(r"q\s+BT /\S+ [\d.]+ Tf ET ([\d.\-]+) ([\d.\-]+) ([\d.\-]+) ([\d.\-]+) ([\d.\-]+) ([\d.\-]+) cm\s+BT(.*?)ET Q", stream, re.S)
        assert len(runs) == 6, (name, len(runs))
        expected = [position for f in fields if f["page"] == i+1 for position in positions(f, w, h)]
        image_path = next(directory.glob(f"geometry-{i+1:0{len(str(len(cases)))}d}.png"))
        ink = np.array(Image.open(image_path).convert("L")) < 220
        for run, (x, baseline, advance, text) in zip(runs, expected):
            a, b, c, d, actual_x, actual_y = map(float, run[:6])
            assert (a, b, c, d) == (1, 0, 0, 1)
            tx, ty = map(float, re.search(r"([-\d.]+) ([-\d.]+) Td", run[-1]).groups())
            assert abs(actual_x+tx-x) < .00002, (name, "x", actual_x+tx, x)
            assert abs(actual_y+ty-baseline) < .00002, (name, "baseline", actual_y+ty, baseline)
            values = re.findall(r"\(((?:\\[\s\S]|[^\\)])*)\)\s+Tj", run[-1])
            gids = glyphs.literal_bytes(values[0])
            assert gids == b"".join(TABLE[ord(c)][0].to_bytes(2, "big") for c in text)
            # Independently locate ink inside the physical glyph frame. Poppler ignores
            # UserUnit; 144 dpi produces two pixels per raw PDF point.
            scale = 2/unit
            left, right = math.floor((x-.5)*scale), math.ceil((x+advance+.5)*scale)
            top = math.floor((h-baseline-1802/2048*12-.5)*scale)
            bottom = math.ceil((h-baseline+246/2048*12+.5)*scale)
            ys, xs = np.nonzero(ink[max(0, top):bottom, max(0, left):right])
            assert len(xs) > 4, (name, "rendered glyph frame")
            # All three alignments must start within 2.5 pt (the glyph has a left side bearing) of the expected left edge;
            # the second line also proves the fixed baseline interval.
            assert abs((xs.min()+max(0, left))/scale-x) <= 2.5, (name, "ink x", x, (xs.min()+max(0, left))/scale, baseline)
        # No ink may occur outside the expected frames (allow antialiasing at edges).
        permitted = np.zeros(ink.shape, dtype=bool)
        for x, baseline, advance, _ in expected:
            scale = 2/unit
            permitted[max(0, math.floor((h-baseline-12)*scale)):math.ceil((h-baseline+3)*scale),
                      max(0, math.floor((x-2)*scale)):math.ceil((x+advance+2)*scale)] = True
        assert not np.any(ink & ~permitted), (name, "unexpected ink outside glyph frames")
    print(f"Geometry: {len(cases)-1} mixed pages / 66 baselines / 3 alignments / Poppler ink: PASS", flush=True)


def extraction(api, directory):
    source = blank(directory, "extraction", pages=4)
    chars = [chr(c) for c in TABLE if 0x4e00 <= c <= 0x9fff][:320]
    texts = ["".join(chars[:160]), "".join(chars[160:]), "%末尾%\n%%連続%%%\n%1%2%99\nか\u3099\\円￥①㈱※〒", "%a"*100]
    output, _ = request(api, source, [field(page=i+1, text=text) for i, text in enumerate(texts)])
    path = directory / "extract-filled.pdf"; path.write_bytes(output)
    poppler = command("pdftotext", "-raw", str(path), "-").stdout.decode().split("\f")
    with fitz.open(path) as doc:
        mupdf = [page.get_text(sort=False) for page in doc]
    for expected, p, m in zip(texts, poppler, mupdf):
        assert p.strip() == m.strip() == expected, (expected[:30], p[:30], m[:30])
    print("Extraction: 320+ glyphs / multiple pages / percent end,runs,digits / combining marks / MuPDF + Poppler: PASS", flush=True)


def boundaries(api, directory):
    # Full page dimensions are exact decimals, so 13.2 pt includes a 12 pt glyph + 1.2 pt padding.
    for w, h, expected in ((13.2, 12, 200), (13.1, 12, 422), (13.2, 11.9, 422)):
        source = blank(directory, "boundary", width=w, height=h)
        output, _ = request(api, source, [field(text="日", x=0, y=0, width=1, height=1)], expected, "field-overflow" if expected == 422 else None)
        if expected == 200:
            verify_bottom_baseline(directory, output, 246/2048*12)
    for size in (6, 6.1, 12, 72):
        source = blank(directory, "page-bottom", width=600, height=1000)
        for align in ("left", "center", "right"):
            output, _ = request(api, source, [field(text="日", x=0, y=round(1-size/1000, 4),
                width=1, height=round(size/1000, 4), fontSize=size, align=align)])
            verify_bottom_baseline(directory, output, 246/2048*size)
    print("Overflow: exact fit / width +0.1 pt / height +0.1 pt; page-bottom baselines at 6/6.1/12/72 pt x3 alignments: PASS", flush=True)


def verify_bottom_baseline(directory, output, expected):
    path = directory / "bottom-filled.pdf"
    path.write_bytes(output)
    metadata = json.loads(command("qpdf", "--json=2", "--json-key=qpdf", "--json-stream-data=inline",
                                  "--decode-level=all", str(path)).stdout)
    found = []
    for obj in metadata["qpdf"][1].values():
        stream = obj.get("stream", {})
        if stream.get("dict", {}).get("/Subtype") != "/Form":
            continue
        content = base64.b64decode(stream["data"]).decode("latin1")
        if " Tj" not in content:
            continue
        matrix = re.search(r"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) cm", content)
        offset = re.search(r"([-\d.]+) ([-\d.]+) Td", content)
        found.append(float(matrix[6]) + float(offset[2]))
    assert len(found) == 1 and abs(found[0] - expected) < .00002, ("page-bottom baseline", found, expected)


def signed_fixtures(directory, source):
    # All keys/certs live in TemporaryDirectory; no secret fixture is retained.
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID
    from pyhanko.pdf_utils.incremental_writer import IncrementalPdfFileWriter
    from pyhanko.pdf_utils.reader import PdfFileReader
    from pyhanko.sign import signers, timestamps
    from pyhanko.sign.fields import MDPPerm
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Ephemeral fill-template fixture")])
    now = datetime.datetime.now(datetime.timezone.utc)
    cert = x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key()).serial_number(
        x509.random_serial_number()).not_valid_before(now-datetime.timedelta(days=1)).not_valid_after(
        now+datetime.timedelta(days=1)).add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True).add_extension(
        x509.ExtendedKeyUsage([ExtendedKeyUsageOID.TIME_STAMPING]), critical=True).sign(key, hashes.SHA256())
    key_path, cert_path = directory / "ephemeral-key.pem", directory / "ephemeral-cert.pem"
    key_path.touch(mode=0o600)
    key_path.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    signer = signers.SimpleSigner.load(str(key_path), str(cert_path))
    assert signer is not None
    fixtures = {}
    for kind in ("approval", "docmdp", "timestamp"):
        path = directory / (kind + ".pdf")
        import io
        writer = IncrementalPdfFileWriter(io.BytesIO(source))
        with path.open("wb") as target:
            if kind == "timestamp":
                tsa = timestamps.DummyTimeStamper(signer.signing_cert, signer.signing_key, fixed_dt=now)
                signers.PdfTimeStamper(tsa).timestamp_pdf(writer, md_algorithm="sha256", output=target)
            else:
                signers.sign_pdf(writer, signers.PdfSignatureMetadata(field_name="FixtureSignature", certify=kind == "docmdp",
                    docmdp_permissions=MDPPerm.NO_CHANGES), signer=signer, output=target)
        with path.open("rb") as stream:
            reader = PdfFileReader(stream)
            assert len(reader.embedded_signatures) == 1
            signature = reader.embedded_signatures[0]
            assert signature.sig_object["/ByteRange"][2] > 0 and len(signature.sig_object["/Contents"]) > 100
        fixtures[kind] = path.read_bytes()
    key_path.unlink(); cert_path.unlink()
    return fixtures


def forms(api, directory):
    source = blank(directory, "forms-source", width=595, height=842)
    fixtures = signed_fixtures(directory, source)
    base = json.loads(command("qpdf", "--json=2", "--json-key=qpdf", "--json-stream-data=none", "--decode-level=none",
                              str(directory / "forms-source.pdf")).stdout)["qpdf"][1]
    for kind in ("ur3", "fields-only", "xfa", "empty-acroform", "direct-widget"):
        objects = json.loads(json.dumps(base))
        catalog = objects["obj:1 0 R"]["value"]
        if kind == "ur3":
            catalog["/Perms"] = {"/UR3": {"/Type": "/Sig"}}
        elif kind == "direct-widget":
            objects["obj:3 0 R"]["value"]["/Annots"] = [{"/Type": "/Annot", "/Sub#74ype": "/Wid#67et", "/Rect": [0, 0, 10, 10]}]
        else:
            catalog["/AcroForm"] = {} if kind == "empty-acroform" else {"/XFA": "u:<xfa/>"} if kind == "xfa" else {
                "/Fields": ["4 0 R"]}
            if kind == "fields-only":
                objects["obj:4 0 R"] = {"value": {"/Type": "/Annot", "/Subtype": "/Widget",
                    "/FT": "/Tx", "/T": "u:Fixture", "/Rect": [0, 0, 10, 10], "/P": "3 0 R"}}
                objects["obj:3 0 R"]["value"]["/Annots"] = ["4 0 R"]
                objects["trailer"]["value"]["/Size"] = 5
        fixtures[kind] = from_json(directory, kind, objects)
    for kind, source in fixtures.items():
        print("Form fixture: " + kind, flush=True)
        request(api, source, [field()], 422, "form-fields")
        # Object stream generation must not hide directly embedded markers or real signatures.
        # Rewriting signatures invalidates them; original signed fixtures above are sent intact.
        path = directory / "form-raw.pdf"; packed = directory / "form-packed.pdf"
        path.write_bytes(source)
        command("qpdf", "--object-streams=generate", str(path), str(packed))
        request(api, packed.read_bytes(), [field()], 422, "form-fields")
    assert len(fixtures) == 8
    print("Forms: real approval/DocMDP/offline TSA + UR3/fields/XFA/empty AcroForm/direct Widget; packed variants: 16/16 PASS", flush=True)


def capacity_fixture():
    pages = 1000
    objects = [f"<< /Type /Catalog /Pages 2 0 R /Payload {pages+3} 0 R >>".encode(),
               b"<< /Type /Pages /Count 1000 /Kids [" + b" ".join(f"{i+3} 0 R".encode() for i in range(pages)) + b"] >>"]
    objects += [b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 14400 14400] /Resources << >> >>"] * pages
    objects.append(pdf.stream("", b""))
    length = 50*MiB-len(pdf.pdf(objects))
    for _ in range(3):
        objects[-1] = pdf.stream("", b"P"*length)
        source = pdf.pdf(objects)
        length += 50*MiB-len(source)
    assert len(source) == 50*MiB
    scalars = "".join(chr(c) for c in TABLE)
    fields = []
    offset = 0
    for i in range(200):
        # Every one of the 11,425 allowed glyphs, then short repeated lines. This
        # maximizes distinct glyphs while respecting the independent 64 KiB JSON limit.
        chunk = scalars[offset:offset+196] or "日本語文字"
        offset += 196
        lengths = (40, 39, 39, 39, 39) if len(chunk) == 196 else None
        if lengths:
            lines, p = [], 0
            for n in lengths:
                lines.append(chunk[p:p+n]); p += n
        else:
            lines = [chunk[j::5] or "日" for j in range(5)]
        fields.append(field(page=i//20+1 if i < 180 else 1000, text="\n".join(lines), fontSize=72))
    assert set(scalars) <= set("".join(f["text"] for f in fields))
    assert sum(len(f["text"].split("\n")) for f in fields) == 1000
    encoded = json.dumps(fields, ensure_ascii=False, separators=(",", ":")).encode()
    assert len(encoded) <= 65536, len(encoded)
    return source, fields, len(encoded)


def capacity(api, directory):
    source, fields, fields_bytes = capacity_fixture()
    peak_job = [0]
    failures = []
    finished = threading.Event()
    def sample():
        try:
            while not finished.is_set():
                result = api.exec("sh", "-c", 'du -sk /tmp/amane-pdf-api/* 2>/dev/null || true').stdout.decode()
                for line in result.splitlines():
                    peak_job[0] = max(peak_job[0], int(line.split()[0])*1024)
                finished.wait(.05)
        except Exception as error:
            failures.append(str(error))
    sampler = threading.Thread(target=sample); sampler.start()
    try:
        output, seconds = request(api, source, fields)
    finally:
        finished.set(); sampler.join()
    assert not failures, failures
    assert seconds < 30
    assert 0 < peak_job[0] < 124*MiB
    memory = int(api.exec("cat", "/sys/fs/cgroup/memory.peak").stdout)
    assert memory <= 1536*MiB
    path = directory / "capacity-filled.pdf"; path.write_bytes(output)
    assert command("qpdf", "--show-npages", str(path)).stdout.strip() == b"1000"
    metrics = dict(input_bytes=len(source), fields_bytes=fields_bytes, elements=1000, fields=200, glyphs=len(TABLE),
        font_size=72, drawn_pages=10, span=1000, seconds=round(seconds, 3), output_bytes=len(output),
        sampled_job_peak_bytes=peak_job[0], container_memory_peak_bytes=memory)
    print("Capacity: " + json.dumps(metrics) + " PASS", flush=True)
    return metrics


def builder_capacity(image, directory):
    overlay = load("fill_overlay", "overlay-validation.py")
    sdk = Path(command("dotnet", "--list-sdks").stdout.decode().splitlines()[-1].split("[")[1].rstrip("]")) / command("dotnet", "--version").stdout.decode().strip()
    source, fields, _ = capacity_fixture()
    fixture = directory / "builder-capacity"
    fixture.mkdir(mode=0o755)
    # Synthetic data only. No signing keys or user uploads enter this read-only mount.
    (fixture / "capacity.pdf").write_bytes(source)
    (fixture / "fields.json").write_text(json.dumps(fields, ensure_ascii=False, separators=(",", ":")))
    directory.chmod(0o755)
    container = overlay.Container(image, sdk, [(fixture, "/fixture")], {
        "FILL_TEMPLATE_CAPACITY_INPUT": "/fixture/capacity.pdf",
        "FILL_TEMPLATE_CAPACITY_FIELDS": "/fixture/fields.json",
        "FILL_TEMPLATE_CAPACITY_RESULTS": "/tmp/results", "DOTNET_CLI_HOME": "/tmp/cli"})
    try:
        container.tests("FullyQualifiedName~FillTemplateCapacityTests")
        results = [json.loads(container.exec("cat", f"/tmp/results/fill-template-budget-{n}.json").stdout) for n in (1, 2)]
        peak = int(container.exec("cat", "/sys/fs/cgroup/memory.peak").stdout)
        events = dict(line.split() for line in container.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())
        assert events["oom"] == events["oom_kill"] == "0", events
        container.stopped()
        print("Builder capacity: " + json.dumps({"runs": results, "container_memory_peak_bytes": peak}) + " PASS", flush=True)
        return dict(runs=results, container_memory_peak_bytes=peak)
    finally:
        container.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--results", type=Path)
    args = parser.parse_args()
    assert importlib.metadata.version("pyHanko") == "0.29.1"
    assert importlib.metadata.version("PyMuPDF") == "1.26.3"
    with tempfile.TemporaryDirectory(prefix="amane-fill-template-") as temporary:
        directory = Path(temporary)
        with smoke.running_container(args.image) as api:
            geometry(api, directory)
            extraction(api, directory)
            boundaries(api, directory)
            forms(api, directory)
            metrics = capacity(api, directory)
            assert api.request("GET", "/healthz")[0] == 200
            events = dict(line.split() for line in api.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())
            assert events["oom"] == events["oom_kill"] == "0", events
            api.assert_clean()
        metrics["builder"] = builder_capacity(args.image, directory)
        if args.results:
            args.results.write_text(json.dumps(metrics, indent=2) + "\n")
    print("Fill-template: geometry/extraction/percent/boundaries/8 forms/capacity/cleanup/health: PASS", flush=True)


if __name__ == "__main__":
    main()
