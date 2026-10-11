#!/usr/bin/env python3
"""#59: Adam7全型の境界とAS別の反復測定。製品コードは変更しない。"""
import argparse
import concurrent.futures
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import struct
import threading
import time
import zlib

import numpy as np

spec = importlib.util.spec_from_file_location("followup", Path(__file__).with_name("from-images-followup.py"))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
MIB = m.MIB
PASSES = ((0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4),
          (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2))
PLAN = m.ROOT / "docs/from-images-as-plan.json"


def pixels(width, height, depth, color, alpha, high, seed):
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    if depth >= 8:
        dtype = ">u2" if depth == 16 else "u1"
        return np.frombuffer(b"".join(row[1:] for row in m.png_rows(
            width, height, depth, color, alpha, high, seed)), dtype=dtype).reshape(height, width, channels)
    maximum = (1 << depth) - 1
    if high:
        data = np.random.default_rng(seed).integers(0, maximum + 1, (height, width, 1), dtype=np.uint8)
    else:
        data = np.full((height, width, 1), maximum // 2, dtype=np.uint8)
    if alpha and color == 0:
        data[:, 0] = maximum // 4
    return data


def packed_row(row, depth):
    if depth >= 8:
        return row.tobytes()
    flat = row.reshape(-1)
    bits = ((flat[:, None] >> np.arange(depth - 1, -1, -1, dtype=np.uint8)) & 1).reshape(-1)
    return np.packbits(bits).tobytes()


def write_png(path, data, depth, color, alpha, seed, interlace):
    height, width, channels = data.shape
    encoder = zlib.compressobj(6)
    with path.open("wb") as output:
        def chunk(kind, payload):
            output.write(struct.pack(">I", len(payload)) + kind + payload)
            output.write(struct.pack(">I", zlib.crc32(kind + payload)))
        output.write(b"\x89PNG\r\n\x1a\n")
        chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, depth, color, 0, 0, int(interlace)))
        if color == 3:
            count = 1 << depth
            palette = np.random.default_rng(seed + 1).integers(0, 256, (count, 3), dtype=np.uint8)
            palette[0] = [70, 100, 200]
            chunk(b"PLTE", palette.tobytes())
            if alpha:
                transparency = np.arange(count, dtype=np.uint8)
                transparency[0] = 128
                chunk(b"tRNS", transparency.tobytes())
        elif alpha and color in (0, 2):
            chunk(b"tRNS", struct.pack(">" + "H" * channels, *([((1 << depth) - 1) // 4] * channels)))
        for x, y, dx, dy in PASSES if interlace else ((0, 0, 1, 1),):
            if x >= width or y >= height:
                continue
            for row in data[y::dy, x::dx]:
                compressed = encoder.compress(b"\0" + packed_row(row, depth))
                if compressed:
                    chunk(b"IDAT", compressed)
        chunk(b"IDAT", encoder.flush())
        chunk(b"IEND", b"")


def generate(args):
    assert not args.fixtures.exists() or not any(args.fixtures.iterdir()), "Use a fresh fixture directory."
    args.fixtures.mkdir(parents=True, exist_ok=True)
    cases = []
    def add(name, width, height, depth, color, alpha, high, seed, interlace, boundary=False):
        path = args.fixtures / (name + ".png")
        data = pixels(width, height, depth, color, alpha, high, seed)
        write_png(path, data, depth, color, alpha, seed, interlace)
        estimate = m.working_bytes(width, height, depth, color, alpha, path.stat().st_size)
        if boundary:
            # Use actual Adam7 size. Correct the analytical starting height and
            # retain an adjacent rejected input as evidence of the exact boundary.
            for _ in range(20):
                if estimate > 320 * MIB:
                    step = max(1, math.ceil((estimate - 320 * MIB) / max(1, (estimate - 64 * MIB) / height)))
                    height -= step
                else:
                    height += 1
                data = pixels(width, height, depth, color, alpha, high, seed)
                write_png(path, data, depth, color, alpha, seed, interlace)
                following = m.working_bytes(width, height, depth, color, alpha, path.stat().st_size)
                if estimate <= 320 * MIB < following:
                    above = dict(height=height, bytes=path.stat().st_size, sha256=m.digest(path), png_working_bytes=following)
                    height -= 1
                    data = pixels(width, height, depth, color, alpha, high, seed)
                    write_png(path, data, depth, color, alpha, seed, interlace)
                    estimate = m.working_bytes(width, height, depth, color, alpha, path.stat().st_size)
                    assert estimate <= 320 * MIB
                    break
                estimate = following
            else:
                raise AssertionError("Boundary search did not converge: " + name)
        else:
            above = None
        assert path.stat().st_size <= 50 * MIB and estimate <= 320 * MIB
        case = dict(name=name, file=path.name, format="png", width=width, height=height, depth=depth,
            color=color, alpha=alpha, entropy="high" if high else "low", seed=seed, interlace=int(interlace),
            bytes=path.stat().st_size, sha256=m.digest(path), pixel_sha256=hashlib.sha256(data.tobytes()).hexdigest(),
            png_working_bytes=estimate, expected=200, expected_reason=None, adjacent_above=above)
        cases.append(case)
        print(name, height, case["bytes"], round(estimate / MIB, 3), flush=True)
    formats = [("rgb8", 8, 2, False), ("rgb8-trns", 8, 2, True), ("rgba8", 8, 6, True),
               ("gray8", 8, 0, False), ("gray8-trns", 8, 0, True), ("grayalpha8", 8, 4, True),
               ("gray16", 16, 0, False), ("gray16-trns", 16, 0, True), ("rgb16", 16, 2, False),
               ("rgb16-trns", 16, 2, True), ("grayalpha16", 16, 4, True), ("rgba16", 16, 6, True),
               ("palette8", 8, 3, False), ("palette8-trns", 8, 3, True)]
    for depth in (1, 2, 4):
        formats.extend([(f"gray{depth}", depth, 0, False), (f"gray{depth}-trns", depth, 0, True),
                        (f"palette{depth}", depth, 3, False), (f"palette{depth}-trns", depth, 3, True)])
    for index, (label, depth, color, alpha) in enumerate(formats):
        for high in (False, True):
            channels = 1 if color == 0 and not alpha else 4
            coefficient = 6 * channels * 2 if depth == 16 else 14 if color != 3 and channels == 4 else 12
            bpp = depth * {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color] / 8 if high else 0
            per_pixel = max(coefficient + 2 * bpp, 22 if depth != 16 and color != 3 and channels == 4 else 0)
            height = int(256 * MIB / per_pixel / 4000) - 2
            add(f"adam7-{label}-{'high' if high else 'low'}-boundary", 4000, height,
                depth, color, alpha, high, 6200 + index, True, boundary=True)
    for high in (False, True):
        for portrait in (False, True):
            for interlace in (False, True):
                width, height = (3000, 4000) if portrait else (4000, 3000)
                add(f"{'adam7' if interlace else 'plain'}-rgba8-12mp-{'high' if high else 'low'}-{'portrait' if portrait else 'landscape'}",
                    width, height, 8, 6, True, high, 5980, interlace)
    manifest = dict(generator_sha256=m.digest(Path(__file__)), plan_sha256=m.digest(args.plan),
        versions=dict(python=platform.python_version(), numpy=np.__version__, zlib=zlib.ZLIB_RUNTIME_VERSION), cases=cases)
    m.save(args.fixtures / "manifest.json", manifest)


def environment(args, manifest):
    plan = json.loads(args.plan.read_text())
    info = json.loads(m.smoke.docker("image", "inspect", args.image).stdout)[0]
    assert info["Id"] == plan["image_id"]
    for case in manifest["cases"]:
        assert m.digest(args.fixtures / case["file"]) == case["sha256"]
    m.save(args.output / "environment.json", dict(measured_at_utc=datetime.now(timezone.utc).isoformat(),
        plan=plan, plan_sha256=m.digest(args.plan), image_id=info["Id"], fixture_manifest=manifest,
        driver_sha256=m.digest(Path(__file__)), followup_sha256=m.digest(Path(m.__file__)), host_kernel=platform.release()))
    return plan


def summary(trials):
    failures = sum(not t["success"] for t in trials)
    consecutive = 0
    for trial in reversed(trials):
        if not trial["success"]:
            break
        consecutive += 1
    return dict(trials=len(trials), failures=failures, failure_fraction=failures / len(trials),
                trailing_consecutive_successes=consecutive)


def layout(case):
    w, h = case["width"], case["height"]
    page_w, page_h = ((297, 210) if w >= h else (210, 297))
    page_w, page_h, margin = page_w * 72 / 25.4, page_h * 72 / 25.4, 10 * 72 / 25.4
    fit = min((page_w - 2 * margin) / w, (page_h - 2 * margin) / h)
    full = min(page_w / w, page_h / h)
    return f"dim:{page_w:.17g} {page_h:.17g}, pos:c, sc:{fit/full:.17g} rel"


def direct_batch(args, case, as_mib, repetitions):
    label = f"direct-{case['name']}-as{as_mib}"
    trials = []
    with m.smoke.running_container(args.image, isolated=True, mounts=[(str(args.fixtures.resolve()), "/fixture")]) as api:
        api.exec("mkdir", "-m", "700", "/tmp/as-probe")
        monitor = m.Monitor(api)
        monitor.thread = threading.Thread(target=monitor.loop); monitor.thread.start()
        try:
            for repetition in range(repetitions):
                start = time.monotonic()
                result = m.smoke.docker("exec", "--workdir", "/tmp/as-probe", api.name, "env",
                    "HOME=/tmp/as-probe", "XDG_CONFIG_HOME=/tmp/as-probe", "GOMEMLIMIT=200MiB", "GOGC=100", "GODEBUG=",
                    "GOMAXPROCS=1", "GOTRACEBACK=none", "/usr/bin/env", "--ignore-signal=XFSZ", "--", "prlimit",
                    "--core=0:0", f"--as={as_mib*MIB}:{as_mib*MIB}", "--fsize=56623105:56623105", "--",
                    "/opt/amane-pdf/bin/pdfcpu", "-c", "/opt/amane-pdf/config", "--offline", "import", "--",
                    layout(case), "/tmp/as-probe/result.pdf",
                    "/fixture/" + case["file"], check=False)
                size_result = api.exec("sh", "-c", "if test -f /tmp/as-probe/result.pdf; then stat -c %s /tmp/as-probe/result.pdf; else printf 0; fi")
                size = int(size_result.stdout)
                checked = m.smoke.docker("exec", api.name, *m.smoke.LIMITED_EXEC,
                    "--as=570425344:570425344", "--fsize=56623105:56623105", "--", "qpdf", "--check",
                    "/tmp/as-probe/result.pdf", check=False) if result.returncode == 0 else None
                trial = dict(repetition=repetition + 1, exit=result.returncode, seconds=round(time.monotonic()-start, 3),
                    output_bytes=size, qpdf_check_exit=None if checked is None else checked.returncode,
                    success=result.returncode == 0 and checked is not None and checked.returncode == 0 and 0 < size <= 54*MIB,
                    allocation_failure=b"out of memory" in result.stderr,
                    diagnostic_sha256=hashlib.sha256(result.stderr).hexdigest())
                trials.append(trial)
                api.exec("rm", "-f", "/tmp/as-probe/result.pdf")
                m.save(args.output / (label + ".json"), dict(name=case["name"], address_space_mib=as_mib,
                    input_sha256=case["sha256"], trials=trials, summary=summary(trials), completed=False))
        finally:
            metrics = monitor.finish()
        remaining = [name for name in api.exec("ps", "-eo", "comm").stdout.decode().splitlines() if name in m.TOOLS]
        health = api.request("GET", "/healthz")[0]
        assert not remaining and health == 200
        assert not metrics["memory_events"]["oom"] and not metrics["memory_events"]["oom_kill"] and not metrics["monitor_error"]
    # VmSize remains a sampled diagnostic; it is not an AS reserve measurement.
    metrics.pop("tool_address_space_headroom", None)
    row = dict(name=case["name"], address_space_mib=as_mib, input_sha256=case["sha256"], trials=trials,
        summary=summary(trials), metrics=metrics, remaining_tools=remaining, health_after=health, completed=True)
    m.save(args.output / (label + ".json"), row)
    print(label, row["summary"], "cgroup", round(metrics["memory_peak"]/MIB, 2), flush=True)
    return row


def http_batch(args, cases, specs, as_mib):
    label = "http-" + "-".join(c["name"] + "-" + operation + "-" + setting
        for c, operation, setting in specs) + f"-as{as_mib}"
    trials = []
    # Existing product settings are shared. In this measurement-only container
    # the wrapper restores 1 GiB for non-import commands (including startup),
    # so the candidate AS affects image import alone. Adoption needs a separate
    # product option after Koo's decision, followed by verification of that build.
    settings = ("Pdf__PdfcpuPath=/as-tools/pdfcpu", f"Pdf__PdfcpuAddressSpaceLimitBytes={as_mib*MIB}")
    with m.smoke.running_container(args.image, settings=settings, isolated=True,
            mounts=[(str(args.audit_wrapper.resolve()), "/as-tools/pdfcpu")]) as api:
        inspected = json.loads(m.smoke.docker("inspect", api.name).stdout)[0]
        api.exec("truncate", "-s", "0", "/tmp/capacity-pdfcpu-invocations")
        monitor = m.Monitor(api)
        monitor.thread = threading.Thread(target=monitor.loop); monitor.thread.start()
        try:
            for repetition in range(args.repetitions):
                barrier = threading.Barrier(len(specs))
                with concurrent.futures.ThreadPoolExecutor(max_workers=len(specs)) as pool:
                    futures = [pool.submit(m.send, api, args.fixtures, case, operation, setting, barrier)
                        for case, operation, setting in specs]
                    values = [future.result() for future in futures]
                errors = []
                for (case, operation, setting), (evidence, output) in zip(specs, values):
                    evidence["case"] = case["name"]
                    if evidence["status"] != 200 or evidence["response_validation_errors"]:
                        errors.append("Unexpected HTTP response")
                    elif evidence["media_type"] != "application/pdf" or not output.startswith(b"%PDF-") or len(output) > 54*MIB:
                        errors.append("Invalid PDF response")
                    else:
                        evidence["pdf_validation"] = m.validate_pdf(output, case, operation, setting, args.output)
                    if operation == "compress" and evidence.get("recompressed") != 1:
                        errors.append("Expected one JPEG recompression")
                api.assert_clean()
                trials.append(dict(repetition=repetition + 1, requests=[v[0] for v in values],
                    success=not errors, validation_errors=errors))
                m.save(args.output / (label + ".json"), dict(name=label, address_space_mib=as_mib,
                    trials=trials, summary=summary(trials), completed=False))
                # Large response bodies belong to this trial only. Retaining
                # them until the next two uploads/replies raises driver RSS.
                del values, futures, output
        finally:
            metrics = monitor.finish()
        invocations = len(api.exec("cat", "/tmp/capacity-pdfcpu-invocations").stdout)
        expected_invocations = args.repetitions * sum(op == "from-images" for _, op, _ in specs)
        remaining = [name for name in api.exec("ps", "-eo", "comm").stdout.decode().splitlines() if name in m.TOOLS]
        health = api.request("GET", "/healthz")[0]
        api.rotate(m.smoke.FIXTURE)
        errors = []
        if invocations != expected_invocations or remaining or health != 200:
            errors.append("Invocation/cleanup/health failure")
        if metrics["memory_events"]["oom"] or metrics["memory_events"]["oom_kill"] or metrics["monitor_error"]:
            errors.append("OOM or monitor failure")
        if metrics["memory_peak"] >= .9 * 1536 * MIB:
            errors.append("cgroup reserve below the predeclared 10%")
        if metrics.get("tmpfs_bytes", 0) >= 256*MIB or metrics.get("job_bytes_single", 0) > 124*MIB:
            errors.append("Temporary storage budget exceeded")
        if metrics.get("jobs", 0) < 2:
            errors.append("No two overlapping jobs observed")
    metrics.pop("tool_address_space_headroom", None)
    row = dict(name=label, address_space_mib=as_mib, trials=trials, summary=summary(trials), metrics=metrics,
        pdfcpu_invocations=invocations, expected_pdfcpu_invocations=expected_invocations,
        remaining_jobs=[], remaining_tools=remaining, health_after=health, rotate_after=200,
        validation_errors=errors, container=dict(image_id=inspected["Image"], host_config=inspected["HostConfig"]), completed=True)
    m.save(args.output / (label + ".json"), row)
    print(label, row["summary"], "cgroup", round(metrics["memory_peak"]/MIB, 2), errors, flush=True)
    return row


def checked_http_batch(args, cases, specs, as_mib):
    try:
        return http_batch(args, cases, specs, as_mib)
    except AssertionError as error:
        if str(error) != "Docker health check failed.":
            raise
        # The container context already removes failed startup containers.
        # Keep this separate from an HTTP result: no request was sent.
        label = "startup-" + "-".join(case["name"] + "-" + setting for case, _, setting in specs) + f"-as{as_mib}"
        m.save(args.output / (label + ".json"), dict(name=label, address_space_mib=as_mib,
            startup_failed=True, http_requests_sent=0, qualified=False, completed=False,
            note="コンテナの起動検証に失敗し、HTTP要求は送っていない。候補は不合格。"))
        raise


def measure(args):
    assert os.geteuid() == 0, "Use sudo for validation-container /proc/cgroup."
    assert not args.output.exists() or not any(args.output.iterdir()), "Use a fresh result directory."
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((args.fixtures / "manifest.json").read_text())
    plan = environment(args, manifest)
    cases = [c for c in manifest["cases"] if not args.cases or c["name"] in args.cases]
    assert cases and args.repetitions >= plan["repetitions"]
    if args.http:
        args.audit_wrapper = args.output / "pdfcpu-import-as.sh"
        args.audit_wrapper.write_text('#!/bin/sh\nprintf x >> /tmp/capacity-pdfcpu-invocations\n'
            'if test "$4" = import; then exec /opt/amane-pdf/bin/pdfcpu "$@"; fi\n'
            'exec prlimit --as=1073741824:1073741824 -- /opt/amane-pdf/bin/pdfcpu "$@"\n')
        args.audit_wrapper.chmod(0o755)
        if args.compress_fixture:
            old = json.loads(args.compress_fixture.read_text())
            source = next(c for c in old["cases"] if c["name"] == "compress-near50")
            destination = args.fixtures / source["file"]
            if not destination.exists():
                destination.write_bytes((args.compress_fixture.parent / source["file"]).read_bytes())
            assert m.digest(destination) == source["sha256"]
        for as_mib in args.address_spaces:
            for case in cases:
                for quality in args.qualities:
                    if args.compress_fixture:
                        for level in ("standard", "strong"):
                            checked_http_batch(args, cases, [(case, "from-images", quality), (source, "compress", level)], as_mib)
                    else:
                        checked_http_batch(args, cases, [(case, "from-images", quality)] * 2, as_mib)
        return
    for as_mib in args.address_spaces:
        for case in cases:
            direct_batch(args, case, as_mib, args.repetitions)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", nargs="?", default="amane-pdf-api:issue59-review")
    parser.add_argument("--generate-only", action="store_true")
    parser.add_argument("--fixtures", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--cases", nargs="+")
    parser.add_argument("--address-spaces", nargs="+", type=int, default=[1088, 1152])
    parser.add_argument("--repetitions", type=int, default=30)
    parser.add_argument("--http", action="store_true")
    parser.add_argument("--compress-fixture", type=Path)
    parser.add_argument("--plan", type=Path, default=PLAN)
    parser.add_argument("--qualities", nargs="+", choices=("standard", "original"), default=["standard", "original"])
    args = parser.parse_args()
    if args.generate_only:
        generate(args)
    else:
        assert args.output is not None
        measure(args)
