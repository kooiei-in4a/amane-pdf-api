#!/usr/bin/env python3
"""実Builderの描画比較・手動容量測定。runner/Popplerは製品imageへ入れない。"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import random
import shutil
import struct
import subprocess
import tempfile
import threading
import uuid

ROOT = Path(__file__).resolve().parent.parent
MiB = 1048576


def command(*args, timeout=120):
    result = subprocess.run(args, capture_output=True, timeout=timeout)
    if result.returncode:
        print(result.stderr.decode(errors="replace"))
        result.check_returncode()
    return result


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def fixture(directory):
    pdf = load("overlay_pdf", "compress-validation.py")
    # 1,000 pages plus 48,996 reachable objects, with independent retained
    # string/stream padding. JSON expansion, font subset and PDF bytes combine.
    pages = 1000
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R /Padding 3 0 R /Payload 4 0 R /Reachable [" +
        b" ".join(f"{i} 0 R".encode() for i in range(1005, 50001)) + b"] >>",
        b"<< /Type /Pages /Count 1000 /Kids [" +
        b" ".join(f"{i} 0 R".encode() for i in range(5, 1005)) + b"] >>",
        b"(P)", pdf.stream("", b"x"),
    ]
    for i in range(pages):
        unit = (0.5, 1, 2)[i % 3]
        width, height = ((14400 / unit, 14400 / unit) if i % 2 else (24 / unit, 32 / unit))
        objects.append(f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << >> /Rotate {(0,90,180,270,-90,630)[i%6]} /UserUnit {unit} >>".encode())
    objects.extend([b"<< /Retained true >>"] * (50000 - len(objects)))
    assert len(objects) == 50000
    path = directory / "capacity.pdf"

    def metadata():
        path.write_bytes(pdf.pdf(objects))
        return command("qpdf", "--json=2", "--json-key=pages", "--json-key=qpdf",
                       "--json-key=encrypt",
                       "--json-stream-data=none", "--decode-level=none", str(path)).stdout

    initial = metadata()
    padding = 32 * MiB - 512 * 1024 - len(initial)
    assert padding > 0
    objects[2] = b"(" + b"P" * padding + b")"
    objects[3] = pdf.stream("", random.Random(25).randbytes(50 * MiB - len(pdf.pdf(objects)) - 128 * 1024))
    data = metadata()
    assert 49 * MiB < path.stat().st_size <= 50 * MiB
    assert 31 * MiB < len(data) < 32 * MiB
    parsed = json.loads(data)
    count = sum(key.startswith("obj:") for key in parsed["qpdf"][1])
    assert count == 50000 and len(parsed["pages"]) == 1000
    return {"input_bytes": path.stat().st_size, "metadata_bytes": len(data), "objects": count, "pages": pages}


def font_characters(font, supplementary=False):
    # Read locked TrueType cmap format 4 using only Python's standard library.
    # Include each BMP glyph once, excluding control and pdfcpu substitutions.
    data = Path(font).read_bytes()
    lock = json.loads((ROOT / "third_party/pdfcpu.lock").read_text())
    assert hashlib.sha256(data).hexdigest() == lock["font_sha256"], "Capacity input must use the locked Regular font"
    u16 = lambda offset: struct.unpack_from(">H", data, offset)[0]
    u32 = lambda offset: struct.unpack_from(">I", data, offset)[0]
    cmap = None
    for i in range(u16(4)):
        entry = 12 + i * 16
        if data[entry:entry+4] == b"cmap":
            cmap = u32(entry + 8)
    assert cmap is not None
    glyphs = {}
    for i in range(u16(cmap+2)):
        start = cmap + u32(cmap + 4 + i*8 + 4)
        if supplementary and u16(start) == 12:
            for group in range(u32(start+12)):
                first, last, glyph = struct.unpack_from(">III", data, start+16+group*12)
                for c in range(first, last+1):
                    if c < 32 or c == 37 or 127 <= c < 160 or 0xd800 <= c <= 0xdfff or c > 0x10ffff:
                        continue
                    index = glyph+c-first
                    if index and index not in glyphs:
                        glyphs[index] = chr(c)
            continue
        if u16(start) != 4:
            continue
        segments = u16(start + 6) // 2
        ends, starts = start + 14, start + 16 + segments * 2
        deltas, ranges = starts + segments*2, starts + segments*4
        for s in range(segments):
            for c in range(u16(starts+s*2), u16(ends+s*2)+1):
                if c < 32 or c in (37, 0xffff) or 127 <= c < 160 or 0xd800 <= c <= 0xdfff:
                    continue
                delta, distance = u16(deltas+s*2), u16(ranges+s*2)
                glyph = u16(ranges+s*2+distance+2*(c-u16(starts+s*2))) if distance else c
                if glyph:
                    glyph = (glyph+delta) % 65536
                if glyph and glyph not in glyphs:
                    glyphs[glyph] = chr(c)
    assert len(glyphs) > 11400
    return "".join(glyphs.values())


class Container:
    def __init__(self, image, sdk, mounts, env):
        self.name = "amane-overlay-" + uuid.uuid4().hex
        arguments = ["docker", "run", "-d", "--name", self.name, "--read-only",
                     "--network", "none", "--cpus", "1", "--memory", "1536m", "--memory-swap", "1536m",
                     "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=256m", "--cap-drop", "ALL",
                     "--security-opt", "no-new-privileges=true", "--entrypoint", "sleep"]
        for source, target in [(ROOT, "/workspace"), (sdk, "/runner"), *mounts]:
            arguments += ["--mount", f"type=bind,source={source},target={target},readonly"]
        for key, value in env.items():
            arguments += ["-e", key + "=" + str(value)]
        command(*arguments, image, "600")
        assert self.exec("id", "-u").stdout.strip() != b"0"
        assert self.exec("cat", "/sys/fs/cgroup/memory.swap.max").stdout.strip() == b"0"

    def exec(self, *args):
        return command("docker", "exec", self.name, *args)

    def tests(self, filter):
        self.exec("mkdir", "-p", "/tmp/results", "/tmp/cli")
        return self.exec("dotnet", "/runner/vstest.console.dll",
                         "/workspace/tests/Amane.Pdf.Api.Tests/bin/Release/net10.0/Amane.Pdf.Api.Tests.dll",
                         "/TestCaseFilter:" + filter, "/ResultsDirectory:/tmp/results",
                         "/Logger:trx;LogFileName=overlay.trx", "/Logger:console;verbosity=normal")

    def stopped(self):
        processes = self.exec("ps", "-eo", "comm").stdout.decode().splitlines()
        assert not set(processes) & {"qpdf", "pdfcpu", "prlimit", "djpeg", "cjpeg", "testhost", "dotnet"}
        jobs = self.exec("sh", "-c", "find /tmp -maxdepth 1 -type d -name 'overlay-test-*'").stdout
        assert not jobs
        return {"remaining_jobs": 0, "remaining_processes": 0}

    def close(self):
        command("docker", "rm", "-f", self.name)


def capacity(args, sdk):
    with tempfile.TemporaryDirectory(prefix="amane-overlay-fixture-") as temporary:
        directory = Path(temporary)
        directory.chmod(0o755)
        evidence = fixture(directory)
        characters = list(font_characters(args.font, args.supplementary))
        random.Random(25).shuffle(characters)
        glyphs = "".join(characters)
        (directory / "glyphs.txt").write_text(glyphs)
        evidence["unique_mapped_glyphs"] = len(glyphs)
        evidence["supplementary_scalars"] = sum(ord(c) > 0xffff for c in glyphs)
        evidence["text_layout"] = args.text_layout
        for concurrent in (1, 2):
            run_capacity_case(args, sdk, directory, dict(evidence, concurrent=concurrent), concurrent)


def run_capacity_case(args, sdk, directory, evidence, concurrent):
    container = Container(args.image, sdk, [(directory, "/fixture")],
                          {"OVERLAY_CAPACITY_INPUT": "/fixture/capacity.pdf",
                           "OVERLAY_CAPACITY_GLYPHS": "/fixture/glyphs.txt",
                           "OVERLAY_CAPACITY_LAYOUT": args.text_layout,
                           "OVERLAY_CAPACITY_RESULTS": "/tmp/results", "DOTNET_CLI_HOME": "/tmp/cli"})
    stop = threading.Event()
    sampled = []
    failures = []
    def monitor():
        try:
            while not stop.wait(0.1):
                blocks, free, size = map(int, container.exec("stat", "-f", "-c", "%b %f %S", "/tmp").stdout.split())
                sampled.append((blocks-free)*size)
        except Exception as error:
            failures.append(str(error))
    thread = threading.Thread(target=monitor)
    thread.start()
    try:
        try:
            result = container.tests("FullyQualifiedName~MeasureRealBuilder_" + ("Single" if concurrent == 1 else "Concurrent"))
            print(result.stdout.decode())
        except subprocess.CalledProcessError as error:
            print(error.stdout.decode())
            raise
        finally:
            stop.set(); thread.join()
        assert not failures and sampled
        events = {key: int(value) for key, value in
                  (line.split() for line in container.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())}
        assert events["oom"] == events["oom_kill"] == 0
        evidence.update(memory_peak=int(container.exec("cat", "/sys/fs/cgroup/memory.peak").stdout),
                        tmpfs_peak_sampled=max(sampled), memory_events=events, samples=len(sampled),
                        driver_memory_included=True, **container.stopped())
        assert max(sampled) <= 256 * MiB
        args.output.mkdir(parents=True, exist_ok=True)
        for name in (f"capacity-{concurrent}.json", "overlay.trx"):
            (args.output / (f"capacity-{concurrent}.trx" if name == "overlay.trx" else name)).write_bytes(container.exec("cat", "/tmp/results/" + name).stdout)
        (args.output / f"resources-{concurrent}.json").write_text(json.dumps(evidence, indent=2))
        print(json.dumps(evidence, indent=2))
    finally:
        container.close()


def render(args, sdk):
    with tempfile.TemporaryDirectory(prefix="amane-overlay-render-") as temporary:
        directory = Path(temporary)
        container = Container(args.image, sdk, [], {"OVERLAY_RENDER_DIR": "/tmp/render", "DOTNET_CLI_HOME": "/tmp/cli"})
        try:
            print(container.tests("FullyQualifiedName~Geometry_RealText|FullyQualifiedName~ComplexFixture_Preserves").stdout.decode())
            container.stopped()
            for name in ("original.pdf", "layer.pdf", "final.pdf", "complex-original.pdf", "complex-layer.pdf", "complex-final.pdf"):
                (directory / name).write_bytes(container.exec("cat", "/tmp/render/" + name).stdout)
            for label in ("original", "final"):
                metadata = json.loads(container.exec("qpdf", "--json=2", "--json-key=pages", "--json-key=qpdf",
                                                     "--json-stream-data=none", "/tmp/render/" + label + ".pdf").stdout)
                patch = {}
                for page in metadata["pages"]:
                    dictionary = metadata["qpdf"][1]["obj:"+page["object"]]["value"]
                    dictionary["/CropBox"] = dictionary["/MediaBox"]
                    patch["obj:"+page["object"]] = {"value": dictionary}
                update = json.dumps({"qpdf": [{"jsonversion": 2}, patch]})
                subprocess.run(["docker", "exec", "-i", container.name, "sh", "-c", "cat > /tmp/expand.json"],
                               input=update.encode(), check=True, capture_output=True)
                container.exec("qpdf", "/tmp/render/"+label+".pdf", "--update-from-json=/tmp/expand.json", "/tmp/expanded.pdf")
                (directory / ("expanded-"+label+".pdf")).write_bytes(container.exec("cat", "/tmp/expanded.pdf").stdout)
        finally:
            container.close()
        args.output.mkdir(parents=True, exist_ok=True)
        for path in directory.glob("*.pdf"):
            shutil.copyfile(path, args.output / path.name)
        if args.render_image:
            directory.chmod(0o755)
            for path in directory.glob("*.pdf"):
                path.chmod(0o644)
            result = command("docker", "run", "--rm", "--read-only", "--network", "none",
                             "--user", "1000:1000", "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true",
                             "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=256m",
                             "--mount", f"type=bind,source={directory},target=/render,readonly",
                             "--mount", f"type=bind,source={Path(__file__).resolve()},target=/compare.py,readonly",
                             "--entrypoint", "/usr/bin/python3", args.render_image, "/compare.py", "unused", "--render-files", "/render")
            print(result.stdout.decode())
        else:
            compare_files(directory)


def compare_files(source):
    for tool in ("pdftoppm", "pdftotext", "pdffonts"):
        if shutil.which(tool) is None:
            raise SystemExit("Poppler is required for the explicit rendering comparison.")
    import numpy as np
    from PIL import Image
    with tempfile.TemporaryDirectory(prefix="amane-overlay-pixels-") as temporary:
        directory = Path(temporary)
        for path in source.glob("*.pdf"):
            shutil.copyfile(path, directory / path.name)
        def pixels(label, crop=True):
            prefix = directory / label
            command("pdftoppm", "-r", "72", "-png", *(["-cropbox"] if crop else []), str(prefix)+".pdf", str(prefix))
            return [np.array(Image.open(path).convert("RGB")) for path in sorted(directory.glob(label+"-*.png"))]
        original, final = pixels("original"), pixels("final")
        # Poppler does not apply UserUnit to its pixel dimensions. Render the
        # independent physical layer at reciprocal-unit DPI for this comparison.
        layer = []
        for i, unit in enumerate((0.5, 2, 1, 0.5, 2, 1), 1):
            prefix = directory / f"layer-reference-{i}"
            command("pdftoppm", "-f", str(i), "-l", str(i), "-r", str(72 / unit),
                    "-png", "-cropbox", str(directory / "layer.pdf"), str(prefix))
            path = next(directory.glob(f"layer-reference-{i}-*.png"))
            layer.append(np.array(Image.open(path).convert("RGB")))
        assert len(original) == len(final) == 7 and len(layer) == 6
        for i in range(6):
            assert original[i].shape == final[i].shape == layer[i].shape, (i, original[i].shape, layer[i].shape)
            mask = np.any(layer[i] < 250, axis=2)
            assert mask.sum() > 50, ("A real visible text layer is required", i + 1, int(mask.sum()))
            # Allow one-pixel antialiasing boundary around the independently rendered text.
            expanded = mask.copy()
            for dy in (-1,0,1):
                for dx in (-1,0,1):
                    expanded |= np.roll(np.roll(mask, dy, axis=0), dx, axis=1)
            assert np.array_equal(original[i][~expanded], final[i][~expanded]), f"Original content moved on page {i+1}"
            expected = np.minimum(original[i], layer[i])
            difference = np.abs(expected.astype(int)-final[i].astype(int))
            assert difference.max() <= 2, (i+1, int(difference.max()), int(np.count_nonzero(difference)))
        assert np.array_equal(original[6], final[6]), "Non-target page changed"
        complex_original, complex_final, complex_layer = (pixels("complex-" + name)[0] for name in ("original", "final", "layer"))
        assert complex_original.shape == complex_final.shape == complex_layer.shape
        mask = np.any(complex_layer < 250, axis=2)
        assert mask.sum() > 50
        expanded = mask.copy()
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                expanded |= np.roll(np.roll(mask, dy, axis=0), dx, axis=1)
        assert np.array_equal(complex_original[~expanded], complex_final[~expanded]), "Image/link/form display changed"
        alpha = (255-complex_layer[:, :, 1].astype(float)) / 255
        expected = np.rint(complex_original.astype(float)*(1-alpha[:, :, None]) + np.array([255,0,0])*alpha[:, :, None])
        difference = np.abs(expected-complex_final.astype(int))
        assert difference.max() <= 2, (int(difference.max()), int(np.count_nonzero(difference > 2)))
        before, after = pixels("expanded-original", False)[0], pixels("expanded-final", False)[0]
        assert before.shape == after.shape
        assert np.count_nonzero(np.all(before < 20, axis=2) & np.all(after > 240, axis=2)) >= 300
        text = command("pdftotext", "-raw", str(directory/"final.pdf"), "-").stdout.decode()
        assert "OUTSIDE" in text and "日本語" in text
        fonts = command("pdffonts", str(directory/"final.pdf")).stdout.decode()
        assert "+BIZUDPGothic-Regular" in fonts and "yes yes yes" in fonts
        print("Real Builder: text position/orientation, original/image/link/form pixels, non-target page, CropBox limitation, extraction/font: PASS")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("image")
    parser.add_argument("--render", action="store_true")
    parser.add_argument("--render-image")
    parser.add_argument("--render-files", type=Path)
    parser.add_argument("--capacity", action="store_true")
    parser.add_argument("--sdk", type=Path)
    parser.add_argument("--font", type=Path)
    parser.add_argument("--supplementary", action="store_true")
    parser.add_argument("--text-layout", choices=("dense", "lines-tabs"), default="dense")
    parser.add_argument("--output", type=Path, default=Path("/tmp/overlay-capacity-results"))
    args = parser.parse_args()
    if args.render_files:
        compare_files(args.render_files)
        return
    sdk = args.sdk or Path(command("dotnet", "--list-sdks").stdout.decode().splitlines()[-1].split("[")[1].rstrip("]")) / command("dotnet", "--version").stdout.decode().strip()
    assert (sdk / "vstest.console.dll").exists()
    if args.capacity:
        assert args.font is not None, "--font must point to the locked Regular TTF"
        capacity(args, sdk)
    if args.render:
        render(args, sdk)


if __name__ == "__main__":
    main()
