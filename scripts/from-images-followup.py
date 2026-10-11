#!/usr/bin/env python3
"""#59: 合成PNG/JPEGと既存PDF処理の配置前容量測定（通常CIとは別）。"""
import argparse
import concurrent.futures
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import struct
import threading
import time
import zlib

import numpy as np
import PIL
from PIL import Image, ImageFile

ROOT = Path(__file__).resolve().parent.parent
MIB = 1048576
TOOLS = ("qpdf", "pdfcpu", "prlimit", "djpeg", "cjpeg", "jpegtran")


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


images = load("images", "from-images-validation.py")
smoke = images.smoke
compress = load("compress", "compress-validation.py")


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def save(path, value):
    # Interrupted runs keep every completed measurement. A new run gets a new directory.
    temporary = path.with_suffix(path.suffix + ".new")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def png_rows(width, height, depth, color, alpha, high, seed):
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    rng = np.random.default_rng(seed)
    maximum = (1 << depth) - 1
    for row in range(height):
        if high:
            pixels = rng.integers(0, maximum + 1, (width, channels), dtype=np.uint16 if depth == 16 else np.uint8)
            if alpha and color in (0, 2):
                pixels[0] = maximum // 4
        else:
            values = ([70, 100, 200, 128] if color == 6 else [100, 128] if color == 4
                      else [70, 100, 200] if color == 2 else [0] if color == 3 else [100])
            pixels = np.tile(np.array(values, dtype=np.uint16 if depth == 16 else np.uint8), (width, 1))
            if depth == 16:
                pixels *= 257
            if alpha and color in (0, 2) and row < height // 2:
                pixels[:] = maximum // 4
        yield b"\0" + pixels.astype(">u2" if depth == 16 else "u1").tobytes()


def png(path, width, height, depth, color, alpha, high, seed):
    """PNGを直接書き、Pillowによる16 bit RGB/alphaの8 bit化を避ける。"""
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    maximum = (1 << depth) - 1
    encoder = zlib.compressobj(6)
    with path.open("wb") as output:
        def chunk(kind, data):
            output.write(struct.pack(">I", len(data)) + kind + data)
            output.write(struct.pack(">I", zlib.crc32(kind + data)))
        output.write(b"\x89PNG\r\n\x1a\n")
        chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, depth, color, 0, 0, 0))
        if color == 3:
            palette = np.random.default_rng(seed + 1).integers(0, 256, (256, 3), dtype=np.uint8)
            palette[0] = [70, 100, 200]
            chunk(b"PLTE", palette.tobytes())
            if alpha:
                transparency = np.arange(256, dtype=np.uint8)
                transparency[0] = 128
                chunk(b"tRNS", transparency.tobytes())
        elif alpha and color in (0, 2):
            chunk(b"tRNS", struct.pack(">" + "H" * channels, *([maximum // 4] * channels)))
        for raw in png_rows(width, height, depth, color, alpha, high, seed):
            encoded = encoder.compress(raw)
            if encoded:
                chunk(b"IDAT", encoded)
        encoded = encoder.flush()
        if encoded:
            chunk(b"IDAT", encoded)
        chunk(b"IEND", b"")


def working_bytes(width, height, depth, color, alpha, encoded, interlace=False):
    # Gray+tRNS and gray+alpha expand to four decoder channels too.
    channels = 1 if color == 0 and not alpha else 4
    coefficient = 6 * channels * 2 if depth == 16 else (14 if color != 3 and channels == 4 else 12)
    estimate = 2 * encoded + width * height * coefficient
    if depth != 16 and color != 3 and channels == 4:
        estimate = max(estimate, width * height * 22)
    if interlace:
        # Adam7 pass images: one more decoded image (palette decodes to one channel).
        estimate += width * height * (2 if depth == 16 else 1) * (1 if color == 3 else channels)
    return estimate + 64 * MIB


def boundary_height(width, depth, color, alpha, high, seed, limit=320 * MIB):
    # Probe successive prefixes of one compressor, rather than rebuilding PNGs.
    # Low-entropy tRNS regions depend on the final height, so use high entropy there.
    assert high or not (alpha and color in (0, 2))
    size = 8 + 25 + 12  # signature, IHDR, IEND
    if color == 3:
        size += 780 + (268 if alpha else 0)
    elif alpha and color in (0, 2):
        size += 12 + (2 if color == 0 else 6)
    encoder = zlib.compressobj(6)
    for height, raw in enumerate(png_rows(width, 50_000_000 // width, depth, color, alpha, high, seed), 1):
        encoded = encoder.compress(raw)
        if encoded:
            size += len(encoded) + 12
        tail = encoder.copy().flush()
        complete_size = size + (len(tail) + 12 if tail else 0)
        if working_bytes(width, height, depth, color, alpha, complete_size) > limit:
            assert height > 1
            return height - 1
    raise AssertionError("No working-limit boundary below pixel limit")


def generate(directory):
    directory.mkdir(parents=True, exist_ok=True)
    cases = []
    def add(name, path, **details):
        record = dict(name=name, file=path.name, bytes=path.stat().st_size, sha256=digest(path), **details)
        if details.get("format") == "png":
            record["png_working_bytes"] = working_bytes(details["width"], details["height"], details["depth"],
                details["color"], details["alpha"], record["bytes"])
            record["expected"] = 200 if record["png_working_bytes"] <= 320 * MIB else 422
            record["expected_reason"] = None if record["expected"] == 200 else "unsupported-image"
        else:
            record.update(expected=200, expected_reason=None)
        assert record["bytes"] <= 50 * MIB
        cases.append(record)
        print(name, record["bytes"], record.get("png_working_bytes"), flush=True)
    # Near the 320 MiB estimate: low entropy is limited by pixel count; high
    # entropy also counts compressed bytes. RGBA16 high is near the upload cap.
    formats = [("gray16", 16, 0, False, 1285), ("gray16-trns", 16, 0, True, 1285),
               ("rgb16", 16, 2, False, 1115), ("rgb16-trns", 16, 2, True, 1115),
               ("grayalpha16", 16, 4, True, 1195), ("rgba16", 16, 6, True, 1045),
               ("palette8", 8, 3, False, 4775), ("palette8-trns", 8, 3, True, 4775)]
    for index, (label, depth, color, alpha, high_height) in enumerate(formats):
        for high in (False, True):
            width, height = 4000, high_height if high else (1394 if depth == 16 else 5570)
            name = label + ("-high" if high else "-low")
            path = directory / (name + ".png")
            seed = 5900 + index
            png(path, width, height, depth, color, alpha, high, seed)
            add(name, path, format="png", width=width, height=height, depth=depth, color=color,
                alpha=alpha, entropy="high" if high else "low", seed=seed)
    # Bracket the working limit with adjacent heights using the actual PNG size.
    for label, depth, color, alpha, high, seed in (
            ("rgba16-boundary", 16, 6, True, False, 5990),
            ("palette8-trns-boundary", 8, 3, True, True, 5991),
            ("gray16-low-boundary", 16, 0, False, False, 6102),
            ("gray16-high-boundary", 16, 0, False, True, 6103),
            ("rgb8-high-boundary", 8, 2, False, True, 6104),
            ("rgb8-trns-high-boundary", 8, 2, True, True, 6105),
            ("rgba8-high-boundary", 8, 6, True, True, 6106),
            ("rgba8-low-boundary", 8, 6, True, False, 6107)):
        height = boundary_height(4000, depth, color, alpha, high, seed)
        for suffix, h in (("below", height), ("above", height + 1)):
            path = directory / (label + "-" + suffix + ".png")
            png(path, 4000, h, depth, color, alpha, high, seed)
            add(label + "-" + suffix, path, format="png", width=4000, height=h,
                depth=depth, color=color, alpha=alpha, entropy="high" if high else "low", seed=seed)
        assert cases[-2]["expected"] == 200 and cases[-1]["expected"] == 422
    # The old high-entropy RGB/RGBA failures must now be rejected before import.
    for label, color, height, seed in (("rgb16", 2, 1860, 5902), ("rgba16", 6, 1600, 5905)):
        name = "oversized-" + label + "-high"
        path = directory / (name + ".png")
        png(path, 4000, height, 16, color, color == 6, True, seed)
        add(name, path, format="png", width=4000, height=height, depth=16, color=color,
            alpha=color == 6, entropy="high", seed=seed)
    for name, color, alpha, height in (("oversized-rgb8-high", 2, False, 3700),
                                      ("oversized-rgb8-trns-high", 2, True, 3700),
                                      ("oversized-rgba8-high", 6, True, 3250)):
        path = directory / (name + ".png")
        png(path, 4000, height, 8, color, alpha, True, 6100)
        add(name, path, format="png", width=4000, height=height, depth=8, color=color,
            alpha=alpha, entropy="high", seed=6100)
    # Recheck the design's mandatory 12 MP RGBA acceptance cases with new inputs.
    for portrait in (False, True):
        w, h = (3000, 4000) if portrait else (4000, 3000)
        name = "rgba8-12mp-high-" + ("portrait" if portrait else "landscape")
        path = directory / (name + ".png")
        png(path, w, h, 8, 6, True, True, 5980)
        add(name, path, format="png", width=w, height=h, depth=8, color=6, alpha=True, entropy="high", seed=5980)
    ImageFile.MAXBLOCK = 192 * MIB
    # Flat color isolates coefficient memory (not an all-photo time bound).
    for progressive in (False, True):
        for orientation in (1, 6):
            name = "jpeg48-444-" + ("progressive" if progressive else "baseline") + f"-o{orientation}"
            path = directory / (name + ".jpg")
            exif = Image.Exif(); exif[274] = orientation
            Image.new("RGB", (8064, 6048), (90, 140, 200)).save(
                path, quality=90, subsampling=0, progressive=progressive, exif=exif)
            add(name, path, format="jpeg", width=8064, height=6048, progressive=progressive,
                orientation=orientation, subsampling="4:4:4", entropy="flat", qualities=["standard"])
    path = directory / "jpeg-near50.jpg"
    source = Image.fromarray(np.random.default_rng(5960).integers(0, 256, (3800, 4500, 3), dtype=np.uint8))
    source.save(path, quality=100, subsampling=0, optimize=True)
    assert 47 * MIB < path.stat().st_size < 50 * MIB
    add("jpeg-near50", path, format="jpeg", width=4500, height=3800, entropy="high", seed=5960,
        subsampling="4:4:4", qualities=["standard", "original"])
    pdf_path = directory / "compress-near50.pdf"
    pdf_path.write_bytes(compress.image_pdf(path.read_bytes(), 4500, 3800))
    images.command("qpdf", "--check", str(pdf_path))
    add("compress-near50", pdf_path, format="pdf", width=4500, height=3800, entropy="high", seed=5960)
    save(directory / "manifest.json", dict(generator_sha256=digest(Path(__file__)),
        versions=dict(python=platform.python_version(), pillow=PIL.__version__, numpy=np.__version__, zlib=zlib.ZLIB_RUNTIME_VERSION),
        cases=cases))


def stdout_only_qpdf(tool, arguments):
    # These inspection commands write no output file. The product discards or
    # bounds stdout, and intentionally sets AS/CORE without setting FSIZE.
    return tool == "qpdf" and len(arguments) == 3 and arguments[1] in (
        b"--check", b"--is-encrypted", b"--requires-password", b"--show-npages")


class Monitor(images.Monitor):
    def loop(self):
        while not self.stop.wait(.02):
            try:
                stats = os.statvfs(self.root / "tmp")
                jobs = [job for job in (self.root / "tmp/amane-pdf-api").glob("*") if job.is_dir()]
                values = dict(tmpfs_bytes=(stats.f_blocks - stats.f_bfree) * stats.f_frsize, jobs=len(jobs))
                values["job_bytes_single"] = max((sum(p.stat().st_blocks * 512 for p in job.rglob("*") if p.is_file())
                                                   for job in jobs), default=0)
                active = 0
                for pid in (self.cg / "cgroup.procs").read_text().split():
                    proc = Path("/proc") / pid
                    name = (proc / "comm").read_text().strip()
                    if name not in TOOLS:
                        continue
                    active += 1
                    status = (proc / "status").read_text()
                    for prefix, label in (("VmHWM:", "rss"), ("VmSize:", "vms")):
                        line = next((line for line in status.splitlines() if line.startswith(prefix)), None)
                        if line:
                            key = name + "_" + label + "_bytes"
                            values[key] = max(values.get(key, 0), int(line.split()[1]) * 1024)
                    record = self.processes.setdefault(pid, dict(tool=name, first=time.monotonic(), last=0))
                    record["last"] = time.monotonic()
                    for key, prefix in (("as", "Max address space"), ("fsize", "Max file size"), ("core", "Max core file size")):
                        line = next(line for line in (proc / "limits").read_text().splitlines() if line.startswith(prefix))
                        limit = line[len(prefix):].split()[0]
                        # prlimit can be sampled before it has applied limits; record tools only.
                        if name != "prlimit":
                            if key == "fsize" and limit == "unlimited":
                                arguments = (proc / "cmdline").read_bytes().rstrip(b"\0").split(b"\0")
                                assert stdout_only_qpdf(name, arguments), f"pid={pid} tool={name} fsize=unlimited"
                                values["qpdf_inspection_unlimited_fsize_observed"] = 1
                                continue
                            assert limit != "unlimited" and (key != "core" or limit == "0"), \
                                f"pid={pid} tool={name} {key}={limit}"
                            values[name + "_" + key] = int(limit)
                values["active_tools"] = active
                for key, value in values.items():
                    self.peak[key] = max(value, self.peak.get(key, 0))
                self.samples += 1
            except (FileNotFoundError, ProcessLookupError):
                pass
            except Exception as error:
                self.error = error
                break

    def finish(self):
        self.stop.set(); self.thread.join()
        self.peak.update(memory_peak=int((self.cg / "memory.peak").read_text()), samples=self.samples,
            memory_events={k: int(v) for k, v in (line.split() for line in (self.cg / "memory.events").read_text().splitlines())},
            monitor_error=None if self.error is None else f"{type(self.error).__name__}: {self.error}",
            tool_seconds_sampled={name: round(sum(p["last"] - p["first"] for p in self.processes.values() if p["tool"] == name), 3)
                                  for name in TOOLS})
        # VmSize is sampled and includes runtime reservations. Its distance
        # from RLIMIT_AS is not the usable AS reserve; compare repeated failure
        # rates at explicit AS values instead (from-images-as-validation.py).
        return self.peak


def send(api, directory, case, operation, setting, barrier):
    data = (directory / case["file"]).read_bytes()
    body, content_type = smoke.multipart(password=None, files=[data])
    endpoint = "/api/pdf/from-images?quality=" + setting if operation == "from-images" else "/api/pdf/compress?level=" + setting
    barrier.wait(timeout=30)
    start = time.monotonic()
    status, headers, result = api.request("POST", endpoint, body, content_type)
    evidence = dict(operation=operation, setting=setting, input_bytes=len(data), input_sha256=case["sha256"],
                    status=status, reason=None, output_bytes=0, seconds=round(time.monotonic() - start, 3),
                    media_type=headers.get("Content-Type", ""), disposition=headers.get("Content-Disposition"),
                    response_bytes=len(result), response_sha256=hashlib.sha256(result).hexdigest(),
                    response_validation_errors=[])
    if status == 200:
        evidence.update(output_bytes=len(result), output_sha256=hashlib.sha256(result).hexdigest())
        if operation == "compress":
            try:
                evidence["recompressed"] = int(headers["X-Pdf-Images-Recompressed"])
            except (KeyError, ValueError):
                evidence["response_validation_errors"].append("Missing/invalid recompression count")
    else:
        try:
            problem = json.loads(result)
            if not isinstance(problem, dict):
                raise ValueError("Problem response is not an object")
            evidence["reason"] = problem.get("reason")
        except (ValueError, UnicodeDecodeError):
            evidence["response_validation_errors"].append("Invalid JSON problem response")
        if any(word in result for word in (b"/tmp/", b"stack", b"qpdf", b"jpegtran", b"pdfcpu")):
            evidence["response_validation_errors"].append("Problem response leaked tool/path diagnostics")
    return evidence, result


def validate_pdf(output, case, operation, setting, directory):
    path = directory / "validation.pdf"
    path.write_bytes(output)
    try:
        document = images.inspect_pdf(path)
        assert len(document["pages"]) == 1
        image = document["pages"][0]["images"][0]
        if operation == "from-images":
            images.verify_layout(path, document)
            if case["format"] == "png":
                assert (image["width"], image["height"]) == (case["width"], case["height"])
                dictionary = document["qpdf"][1]["obj:" + image["object"]]["stream"]["dict"]
                assert ("/SMask" in dictionary) == case["alpha"]
            elif case.get("orientation"):
                width, height = case["width"], case["height"]
                numerator = max(1, min(8, 2339 * 8 // max(width, height)))
                width = (width * numerator + 7) // 8
                height = (height * numerator + 7) // 8
                if case["orientation"] == 6:
                    width, height = height // 16 * 16, width
                assert (image["width"], image["height"]) == (width, height)
        return dict(qpdf_check=True, pages=1, image_width=image["width"], image_height=image["height"])
    finally:
        path.unlink(missing_ok=True)


def run_case(args, specs, label, settings=()):
    row = dict(name=label, concurrent=len(specs), settings=list(settings), requests=[], validation_errors=[])
    with smoke.running_container(args.image, settings=("Pdf__PdfcpuPath=/capacity-tools/pdfcpu", *settings),
            isolated=True, mounts=[(str(args.audit_wrapper.resolve()), "/capacity-tools/pdfcpu")]) as api:
        inspected = json.loads(smoke.docker("inspect", api.name).stdout)[0]
        config = inspected["HostConfig"]
        row["container"] = dict(image_id=inspected["Image"], non_root=True, readonly=config["ReadonlyRootfs"],
            network=config["NetworkMode"], memory=config["Memory"], memory_swap=config["MemorySwap"],
            nano_cpus=config["NanoCpus"], tmpfs=config["Tmpfs"], cap_drop=config["CapDrop"], security_opt=config["SecurityOpt"])
        # Exclude startup self-tests. One fixed byte is appended before each exec;
        # unlike /proc sampling this also detects short-lived tool invocations.
        api.exec("truncate", "-s", "0", "/tmp/capacity-pdfcpu-invocations")
        monitor = Monitor(api)
        monitor.thread = threading.Thread(target=monitor.loop); monitor.thread.start()
        values = []
        try:
            barrier = threading.Barrier(len(specs))
            with concurrent.futures.ThreadPoolExecutor(max_workers=len(specs)) as pool:
                futures = [pool.submit(send, api, args.fixtures, case, operation, setting, barrier)
                           for case, operation, setting, expected in specs]
                for future in futures:
                    try:
                        values.append(future.result())
                    except Exception as error:
                        values.append((dict(transport_error=str(error)), b""))
        finally:
            row.update(monitor.finish())
        row["requests"] = [evidence for evidence, _ in values]
        row["pdfcpu_invocations"] = len(api.exec("cat", "/tmp/capacity-pdfcpu-invocations").stdout)
        if not any(spec[3] == 504 for spec in specs):
            row["expected_pdfcpu_invocations"] = sum(op == "from-images" and expected == 200 for _, op, _, expected in specs)
            if row["pdfcpu_invocations"] != row["expected_pdfcpu_invocations"]:
                row["validation_errors"].append("Unexpected pdfcpu invocation count (admission rejection must precede import)")
        for (case, operation, setting, expected), (evidence, output) in zip(specs, values):
            evidence["expected_status"] = expected
            evidence["expected_reason"] = case["expected_reason"] if expected == 422 else None
            try:
                assert not evidence.get("response_validation_errors"), evidence.get("response_validation_errors")
                assert evidence.get("status") == expected, (case["name"], expected, evidence)
                assert evidence["reason"] == evidence["expected_reason"]
                if expected == 200:
                    assert evidence["media_type"] == "application/pdf" and output.startswith(b"%PDF-")
                    assert len(output) <= 54 * MIB
                    filename = "images.pdf" if operation == "from-images" else "compressed.pdf"
                    assert "filename=" + filename in evidence["disposition"]
                    evidence["pdf_validation"] = validate_pdf(output, case, operation, setting, args.output)
                else:
                    assert evidence["media_type"].startswith("application/problem+json")
                    assert evidence["disposition"] is None and not output.startswith(b"%PDF-")
            except Exception as error:
                row["validation_errors"].append(str(error) or type(error).__name__)
        try:
            api.assert_clean()
            row["remaining_jobs"] = []
        except Exception as error:
            row["remaining_jobs"] = api.exec("find", "/tmp/amane-pdf-api", "-mindepth", "1").stdout.decode().splitlines()
            row["validation_errors"].append(str(error) or "cleanup failed")
        row["remaining_tools"] = [name for name in api.exec("ps", "-eo", "comm").stdout.decode().splitlines() if name in TOOLS]
        row["health_after"] = api.request("GET", "/healthz")[0]
        # Existing PDF behavior remains usable after large conversions/rejections/deadlines.
        try:
            api.rotate(smoke.FIXTURE)
            row["rotate_after"] = 200
        except Exception as error:
            row["validation_errors"].append(str(error) or "rotate failed")
        if row["memory_events"]["oom"] or row["memory_events"]["oom_kill"] or row["monitor_error"] is not None:
            row["validation_errors"].append("OOM or monitor failure")
        if not row["samples"] or row["remaining_tools"] or row["health_after"] != 200:
            row["validation_errors"].append("measurement/cleanup/health failure")
        if row.get("job_bytes_single", 0) > 124 * MIB or row.get("tmpfs_bytes", 0) >= 256 * MIB:
            row["validation_errors"].append("temporary storage budget exceeded")
        if len(specs) == 2 and all(spec[3] == 200 for spec in specs) and row.get("jobs", 0) < 2:
            row["validation_errors"].append("two overlapping jobs were not observed")
    save(args.output / (label + ".json"), row)
    print(label, [r.get("status") for r in row["requests"]], round(row["memory_peak"] / MIB, 2),
          round(row.get("tmpfs_bytes", 0) / MIB, 2), row["validation_errors"], flush=True)
    return row


def capacity(args):
    assert os.geteuid() == 0, "Use sudo to read the validation containers' /proc/cgroup."
    assert not args.output.exists() or not any(args.output.iterdir()), "Use a fresh output directory for each run."
    manifest = json.loads((args.fixtures / "manifest.json").read_text())
    cases = {case["name"]: case for case in manifest["cases"]}
    for case in cases.values():
        assert digest(args.fixtures / case["file"]) == case["sha256"], case["name"]
    args.output.mkdir(parents=True, exist_ok=True)
    args.audit_wrapper = args.output / "pdfcpu-audit.sh"
    args.audit_wrapper.write_text("#!/bin/sh\nprintf x >> /tmp/capacity-pdfcpu-invocations\nexec /opt/amane-pdf/bin/pdfcpu \"$@\"\n")
    args.audit_wrapper.chmod(0o755)
    image_info = json.loads(smoke.docker("image", "inspect", args.image).stdout)[0]
    with smoke.running_container(args.image, isolated=True) as api:
        versions = {name: api.exec(*command).stdout.decode().strip() for name, command in
                    (("dotnet", ("dotnet", "--list-runtimes")), ("qpdf", ("qpdf", "--version")),
                     ("pdfcpu", ("/opt/amane-pdf/bin/pdfcpu", "version")), ("os", ("cat", "/etc/os-release")),
                     ("packages", ("dpkg-query", "-W", "libjpeg-turbo-progs", "util-linux", "coreutils")))}
    save(args.output / "environment.json", dict(measured_at_utc=datetime.now(timezone.utc).isoformat(),
        target_sha=args.target_sha, image_id=image_info["Id"], image_digests=image_info["RepoDigests"],
        scripts={name: digest(ROOT / "scripts" / name) for name in
                 ("from-images-followup.py", "from-images-validation.py", "docker-smoke.py", "compress-validation.py")},
        fixture_manifest=manifest, host_kernel=platform.release(), tool_versions=versions,
        driver=dict(python=platform.python_version(), pillow=PIL.__version__, numpy=np.__version__),
        sampling_seconds=.02, http_seconds_include_upload_and_download=True,
        default_processing_deadline_seconds=30, pdfcpu_audit_wrapper_sha256=digest(args.audit_wrapper),
        pdfcpu_audit_note="Read-only measurement wrapper appends one byte per exec after startup; product binaries and limits are unchanged."))
    rows = []
    selected = set(args.cases or cases)
    for case in cases.values():
        if case["name"] not in selected or case["format"] == "pdf":
            continue
        for quality in args.qualities:
            if quality not in case.get("qualities", args.qualities):
                continue
            for count in args.concurrencies:
                specs = [(case, "from-images", quality, case["expected"])] * count
                rows.append(run_case(args, specs, f"{case['name']}-{quality}-{count}"))
    if args.mixed:
        for name in ("rgba8-12mp-high-landscape", "rgba16-high", "palette8-trns-high", "jpeg-near50", "oversized-rgba8-high"):
            for quality in args.qualities:
                for level in ("standard", "strong"):
                    specs = [(cases[name], "from-images", quality, cases[name]["expected"]),
                             (cases["compress-near50"], "compress", level, cases["compress-near50"]["expected"])]
                    rows.append(run_case(args, specs, f"mixed-{name}-{quality}-{level}"))
    if args.timeout_check:
        # A one-second deadline is incompatible with the positive compress soft
        # deadline. Use two/one seconds and the deliberately slow lossless input.
        rows.append(run_case(args, [(cases["jpeg-near50"], "from-images", "original", 504)],
                             "deadline-original", settings=("Pdf__QpdfTimeoutSeconds=2",
                                                            "Pdf__CompressSoftTimeoutSeconds=1")))
    assert rows, "No cases selected."
    failures = [row["name"] for row in rows if row["validation_errors"]]
    print("Completed", len(rows), "measurements; failures:", failures, flush=True)
    assert not failures, failures


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", nargs="?")
    parser.add_argument("--generate-only", action="store_true")
    parser.add_argument("--fixtures", type=Path, default=Path("/tmp/issue59-fixtures"))
    parser.add_argument("--output", type=Path, default=Path("/tmp/issue59-results"))
    parser.add_argument("--target-sha")
    parser.add_argument("--cases", nargs="+")
    parser.add_argument("--qualities", nargs="+", choices=("standard", "original"), default=["standard", "original"])
    parser.add_argument("--concurrencies", nargs="+", type=int, choices=(1, 2), default=[1, 2])
    parser.add_argument("--mixed", action="store_true")
    parser.add_argument("--timeout-check", action="store_true")
    args = parser.parse_args()
    if args.generate_only:
        generate(args.fixtures)
    else:
        if not args.image or not args.target_sha:
            parser.error("image and --target-sha are required for measurements")
        capacity(args)
