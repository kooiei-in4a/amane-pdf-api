#!/usr/bin/env python3
"""Linux Docker上の実HTTP負荷とリソースを測定する。監視処理はコンテナ外で動かす。"""

import argparse
from collections import Counter
import concurrent.futures
import csv

# docker-smoke.py has a hyphen in its filename.
import importlib.util
import json
import math
import os
import re
import signal
import subprocess
import tempfile
import threading
import time
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "smoke", Path(__file__).with_name("docker-smoke.py")
)
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("image")
parser.add_argument("fixtures", type=Path)
parser.add_argument("results", type=Path)
parser.add_argument("--as-mib", type=int, help="Override the API default AS limit")
parser.add_argument("--jpeg-memory", help="Override the API default JPEGMEM")
parser.add_argument("--memory-mib", type=int, default=1536)
parser.add_argument(
    "--counters", type=Path, help="Optional host dotnet-counters executable"
)
parser.add_argument("--cases", nargs="+")
parser.add_argument("--compress", choices=("standard", "strong"), help="圧縮APIの時間・品質・容量を測定")
args = parser.parse_args()
if os.geteuid() != 0:
    parser.error(
        "Host root access is required to read the container /proc and diagnostic socket."
    )
if args.memory_mib <= 0 or (args.as_mib is not None and args.as_mib <= 0):
    parser.error("memory and AS limits must be positive.")
root = args.fixtures
outroot = args.results
outroot.mkdir(mode=0o700, parents=True, exist_ok=True)
IMAGE = args.image
# Reporting label for the current default; do not override the API configuration.
limit = args.as_mib if args.as_mib is not None else 544


def status(pid):
    return {
        k: int(v.split()[0]) * 1024
        for k, v in (
            line.split(":", 1)
            for line in Path(f"/proc/{pid}/status").read_text().splitlines()
            if ":" in line
        )
        if k in ("VmRSS", "VmHWM", "RssAnon", "RssFile", "RssShmem", "VmSize")
    }


class Monitor:
    def __init__(self, api):
        self.pid = int(
            smoke.docker("inspect", "--format", "{{.State.Pid}}", api.name).stdout
        )
        self.root = Path(f"/proc/{self.pid}/root")
        self.cg = self.root / "sys/fs/cgroup"
        assert (self.cg / "memory.swap.max").read_text().strip() == "0"
        self.stop = threading.Event()
        self.peak = {}
        self.samples = 0
        self.processes = {}
        self.jobs = {}
        self.error = None

    def sample(self):
        vals = {"cgroup_current": int((self.cg / "memory.current").read_text())}
        st = os.statvfs(self.root / "tmp")
        vals["tmpfs_used"] = (st.f_blocks - st.f_bfree) * st.f_frsize
        vals.update({"api_" + k: v for k, v in status(self.pid).items()})
        q = [
            status(int(p))
            for p in (self.cg / "cgroup.procs").read_text().split()
            if Path(f"/proc/{p}/comm").exists()
            and Path(f"/proc/{p}/comm").read_text().strip() == "qpdf"
        ]
        vals["qpdf_count"] = len(q)
        vals["qpdf_RSS_sum"] = sum(p.get("VmRSS", 0) for p in q)
        vals["qpdf_RSS_single"] = max((p.get("VmHWM", 0) for p in q), default=0)
        vals["qpdf_AS_single"] = max((p.get("VmSize", 0) for p in q), default=0)
        if args.compress:
            now = time.monotonic()
            live = set()
            for text_pid in (self.cg / "cgroup.procs").read_text().split():
                pid = int(text_pid)
                comm = Path(f"/proc/{pid}/comm").read_text().strip()
                if comm not in ("qpdf", "djpeg", "cjpeg"): continue
                vals[comm + "_RSS_single"] = status(pid).get("VmHWM", 0)
                argv = Path(f"/proc/{pid}/cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace")
                match = re.search(r"/amane-pdf-api/([A-F0-9]{32})/", argv)
                if not match: continue
                job = match[1]
                self.jobs.setdefault(job, len(self.jobs) + 1)
                if comm != "qpdf": phase = "conversion"
                elif "--json-stream-data=file" in argv: phase = "extraction"
                elif "--json" in argv: phase = "metadata"
                elif "--check" in argv or "--is-encrypted" in argv:
                    phase = "output_validation" if "/output.pdf" in argv else "input_validation"
                else: phase = "final_write"
                item = self.processes.setdefault(pid, {"job": self.jobs[job], "phase": phase, "first": now, "last": now})
                item["last"] = now
                live.add(pid)
                limits = Path(f"/proc/{pid}/limits").read_text().splitlines()
                for key, prefix in (("AS", "Max address space"), ("fsize", "Max file size")):
                    line = next(line for line in limits if line.startswith(prefix))
                    value = line[len(prefix):].split()[0]
                    if value != "unlimited":
                        vals[phase + "_" + comm + "_" + key] = int(value)
                        if phase == "extraction" and key == "fsize":
                            count = argv.count("--json-object=")
                            vals["raw_batch_images"] = count
                            vals["raw_batch_reserved_bytes"] = count * int(value)
            for pid, item in self.processes.items():
                if pid not in live and "end" not in item: item["end"] = now
            vals["pnm_bytes"] = max((p.stat().st_size for p in (self.root / "tmp/amane-pdf-api").glob("*/image.pnm")), default=0)
            vals["job_allocated_bytes"] = max((sum(p.stat().st_blocks * 512 for p in job.rglob("*") if p.is_file())
                for job in (self.root / "tmp/amane-pdf-api").glob("*") if job.is_dir()), default=0)
        for k, v in vals.items():
            self.peak[k] = max(v, self.peak.get(k, 0))
        self.samples += 1

    def loop(self):
        while not self.stop.wait(0.02):
            try:
                self.sample()
            except (FileNotFoundError, ProcessLookupError):
                pass
            except Exception as error:
                self.error = error
                self.stop.set()

    def finish(self):
        self.stop.set()
        self.thread.join()
        if self.error is not None:
            raise RuntimeError("Resource monitor failed; do not treat measurements as valid") from self.error
        self.peak["cgroup_peak"] = int((self.cg / "memory.peak").read_text())
        self.peak["events"] = {
            k: int(v)
            for k, v in (
                line.split()
                for line in (self.cg / "memory.events").read_text().splitlines()
            )
        }
        self.peak["samples"] = self.samples
        if args.compress:
            phases = {}
            for item in self.processes.values():
                key = str(item["job"])
                row = phases.setdefault(key, {})
                row[item["phase"]] = row.get(item["phase"], 0) + item.get("end", item["last"]) - item["first"]
            self.peak["phase_seconds_sampled"] = {job: {k: round(v, 3) for k, v in row.items()} for job, row in phases.items()}
            first = {job: min(p["first"] for p in self.processes.values() if p["job"] == job) for job in self.jobs.values()}
            self.peak["final_start_seconds_sampled"] = {str(job): round(min(p["first"] for p in self.processes.values()
                if p["job"] == job and p["phase"] == "final_write") - first[job], 3) for job in first
                if any(p["job"] == job and p["phase"] == "final_write" for p in self.processes.values())}
        return self.peak


def request(api, item):
    path, inputs, _expected = item
    body, ct = smoke.multipart(password=None, files=inputs)
    start = time.monotonic()
    code, headers, result = api.request("POST", path, body, ct)
    elapsed = time.monotonic() - start
    # Compare expected status after preserving measurements.
    if code == 200:
        assert result.startswith(b"%PDF-")
        with tempfile.TemporaryDirectory(prefix="issue42-output-") as tmp:
            p = Path(tmp) / "output.pdf"
            p.write_bytes(result)
            assert (
                subprocess.run(
                    ["qpdf", "--check", str(p)],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                ).returncode
                == 0
            )
    elif code == 422:
        problem = json.loads(result)
        assert (
            problem["title"]
            == "このPDFは処理できません。PDFの破損・パスワード設定や、画像が大きすぎないか確認してください。"
        )
        assert "reason" not in problem
    measurement = {
        "status": code,
        "seconds": round(elapsed, 3),
        "output_bytes": len(result),
    }
    if args.compress and code == 200:
        measurement["recompressed"] = int(headers["X-Pdf-Images-Recompressed"])
        if current_case not in ("500-images", "mixed-images", "near-50mib", "a4-600dpi-flate"):
            measurement["quality"] = quality(inputs[0], result, args.compress, measurement["recompressed"])
    return measurement


def quality(source, result, level, recompressed):
    from PIL import Image
    import numpy as np
    with tempfile.TemporaryDirectory(prefix="issue22-quality-") as tmp:
        directory = Path(tmp)
        def raw(pdf, label):
            path = directory / (label + ".pdf"); path.write_bytes(pdf)
            pages = json.loads(subprocess.check_output(["qpdf", "--json=2", "--json-key=pages", str(path)]))["pages"]
            image = pages[0]["images"][0]; ref = image["object"].split()
            jpeg = directory / (label + ".jpg")
            jpeg.write_bytes(subprocess.check_output(["qpdf", "--show-object=" + ref[0] + "," + ref[1], "--raw-stream-data", str(path)]))
            return jpeg, image
        original, image = raw(source, "input"); rewritten, output = raw(result, "output")
        if recompressed == 0:
            assert original.read_bytes() == rewritten.read_bytes()
            edge = max(output["width"], output["height"])
            return {"long_edge": edge, "a4_dpi": round(edge * 25.4 / 297, 1), "pdf_reduction_percent": round(100 * (1-len(result)/len(source)), 2), "psnr_db": "unchanged (raw identical)"}
        edge = max(image["width"], image["height"]); target = 1754 if level == "standard" else 1169
        scale = min(8, max(1, (target * 8 + edge - 1) // edge)) if edge > target else 8
        pnm = directory / "scaled.pnm"
        subprocess.run(["prlimit", "--as=67108864:67108864", "--fsize=25165824:25165824", "--", "djpeg", "-scale", f"{scale}/8", "-maxmemory", "64M", "-maxscans", "100", "-strict", "-outfile", str(pnm), str(original)], check=True, capture_output=True)
        a = np.asarray(Image.open(pnm), dtype=np.float32); b = np.asarray(Image.open(rewritten), dtype=np.float32)
        assert a.shape == b.shape
        mse = float(np.mean((a-b)**2)); psnr = 10 * math.log10(255**2/mse) if mse else None
        edge = max(output["width"], output["height"])
        return {"long_edge": edge, "a4_dpi": round(edge * 25.4 / 297, 1), "pdf_reduction_percent": round(100 * (1-len(result)/len(source)), 2), "psnr_db": round(psnr, 2) if psnr else None, "pnm_bytes": pnm.stat().st_size}


def run(name, items, fill_mib=0):
    settings = []
    if args.as_mib is not None:
        settings.append(f"Pdf__QpdfAddressSpaceLimitBytes={args.as_mib * 1024 * 1024}")
    if args.jpeg_memory is not None:
        settings.append(f"Pdf__QpdfJpegMemory={args.jpeg_memory}")
    sustained = name.startswith("sustained-")
    api = smoke.ApiContainer(
        IMAGE,
        settings,
        isolated=True,
        memory_mib=args.memory_mib,
    )
    counters = None
    mon = None
    try:
        api.start()
        mon = Monitor(api)
        if fill_mib:
            api.exec(
                "dd",
                "if=/dev/zero",
                "of=/tmp/pressure",
                "bs=1M",
                f"count={fill_mib}",
                "status=none",
            )
        cp = outroot / f"{limit}-{name}.csv"
        if args.counters:
            socket = next((mon.root / "tmp").glob("dotnet-diagnostic-*-socket"))
            counters = subprocess.Popen(
                [
                    str(args.counters),
                    "collect",
                    "--diagnostic-port",
                    str(socket) + ",connect",
                    "--counters",
                    "EventCounters\\System.Runtime[gc-heap-size,gc-committed,working-set]",
                    "--duration",
                    "00:03:00" if sustained else "00:00:35",
                    "--format",
                    "csv",
                    "--output",
                    str(cp),
                ],
                stdout=(outroot / f"{limit}-{name}-diagnostics.log").open("w"),
                stderr=subprocess.STDOUT,
            )
            time.sleep(5)
        mon.thread = threading.Thread(target=mon.loop)
        mon.thread.start()
        barrier = threading.Barrier(2)

        def worker(item):
            barrier.wait()
            return request(api, item)

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            requests = []
            for iteration in range(30 if sustained else 1):
                requests.extend(pool.map(worker, items))
        time.sleep(1.2)
        measure = mon.finish()
        mon = None
        if counters is not None:
            counters.wait(timeout=190)
            counters = None
        gc = {}
        if args.counters and cp.exists():
            for row in csv.DictReader(cp.open()):
                key = row["Counter Name"]
                gc[key] = max(float(row["Mean/Increment"]), gc.get(key, 0))
        measure["runtime_counters"] = gc
        measure["runtime_counter_units"] = "MB (1,000,000 bytes)"
        if args.counters:
            assert gc, "No runtime counters were recorded"
        api.assert_clean()
        assert api.request("GET", "/healthz")[0] == 200
        assert measure["events"]["oom"] == measure["events"]["oom_kill"] == 0
        if not args.compress:
            assert measure["qpdf_count"] == 2, "Two simultaneous qpdf processes were not observed"
        assert not any(
            Path(f"/proc/{pid}/comm").read_text().strip() in ("qpdf", "djpeg", "cjpeg")
            for pid in (mon_cgroup(api) / "cgroup.procs").read_text().split()
            if Path(f"/proc/{pid}/comm").exists()
        ), "PDF tool process remains"

        result = {
            "case": name,
            "memory_mib": args.memory_mib,
            "limit_mib": limit,
            "jpeg_memory_override": args.jpeg_memory,
            "api_settings_overridden": bool(settings),
            "fill_mib": fill_mib,
            "requests": requests,
            "measure": measure,
        }
        summary = {
            **result,
            "requests": {
                "count": len(requests),
                "statuses": dict(Counter(r["status"] for r in requests)),
                "max_seconds": max(r["seconds"] for r in requests),
            },
        }
        print(json.dumps(summary), flush=True)
        (outroot / f"{limit}-{name}.json").write_text(json.dumps(result, indent=2))
        assert [r["status"] for r in requests] == [i[2] for i in items] * (
            30 if sustained else 1
        ), "Unexpected HTTP status; measurement saved"
    finally:
        try:
            if mon is not None and hasattr(mon, "thread"):
                mon.finish()
        finally:
            try:
                if counters is not None:
                    counters.send_signal(signal.SIGINT)
                    try:
                        counters.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        counters.kill()
                        counters.wait()
            finally:
                api.close()


def mon_cgroup(api):
    pid = int(smoke.docker("inspect", "--format", "{{.State.Pid}}", api.name).stdout)
    return Path(f"/proc/{pid}/root/sys/fs/cgroup")


def load(name):
    return (root / (name + ".pdf")).read_bytes()


if __name__ == "__main__":
    names = args.cases or (["4000x3000-baseline", "4000x3000-baseline-444", "4000x3000-progressive-420", "6000x4000-baseline", "6000x4000-baseline-444", "6000x4000-progressive-420", "8064x6048-baseline", "8064x6048-baseline-444", "8064x6048-progressive-420", "7014x7014-baseline", "document-scan", "near-50mib", "500-images", "a4-600dpi-flate"] if args.compress else [
        "4000x3000-baseline",
        "6000x4000-baseline",
        "6000x4000-baseline-444",
        "6000x4000-progressive-420",
        "8064x6048-baseline",
        "8064x6048-baseline-444",
        "8064x6048-progressive-420",
        "a4-600dpi-flate",
        "near-optimize",
        "near-merge",
        "10000x10000-baseline",
        "10000x10000-progressive",
        "100mp-color",
        "mixed",
        "mixed-as",
        "sustained-baseline",
        "sustained-baseline-444",
        "sustained-progressive-420",
    ])
    if "mixed" in names and not (root / "15000x15000-progressive-420.pdf").exists():
        parser.error("The mixed case requires memory-fixtures.py --include-giant.")
    for name in names:
        current_case = name
        if args.compress:
            items = [("/api/pdf/compress?level=" + args.compress, [load(name)], 200)] * 2
        elif name == "near-merge":
            file = load("near-50mib")
            items = [("/api/pdf/merge", [file, smoke.FIXTURE], 200)] * 2
        elif name == "near-optimize":
            items = [("/api/pdf/optimize", [load("near-50mib")], 200)] * 2
        elif name == "100mp-color":
            items = [
                ("/api/pdf/optimize", [load(name)], 200 if limit >= 768 else 422)
            ] * 2
        elif name == "mixed":
            items = [
                ("/api/pdf/optimize", [load("8064x6048-progressive-420")], 200),
                ("/api/pdf/optimize", [load("15000x15000-progressive-420")], 422),
            ]
        elif name == "mixed-as":
            items = [
                ("/api/pdf/optimize", [load("8064x6048-baseline-444")], 200),
                ("/api/pdf/optimize", [load("100mp-color")], 422),
            ]
        elif name.startswith("sustained-"):
            fixture = load("8064x6048-" + name.removeprefix("sustained-"))
            items = [("/api/pdf/optimize", [fixture], 200)] * 2
        else:
            items = [
                (
                    "/api/pdf/optimize",
                    [load(name)],
                    200,
                )
            ] * 2
        # Leave room for two inputs and two outputs, with a few MiB of tmpfs slack.
        fill = (
            max(0, 248 - math.ceil(4 * len(fixture) / (1024 * 1024)))
            if name.startswith("sustained-")
            else 0
        )
        run((args.compress + "-" if args.compress else "") + name, items, fill)
