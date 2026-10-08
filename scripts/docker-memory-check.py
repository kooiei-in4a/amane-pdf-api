#!/usr/bin/env python3
"""Linux Docker上の実HTTP負荷とリソースを測定する。監視処理はコンテナ外で動かす。"""

import argparse
import concurrent.futures
import csv

# docker-smoke.py has a hyphen in its filename.
import importlib.util
import json
import os
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
parser.add_argument("--as-mib", type=int, default=324)
parser.add_argument("--memory-mib", type=int, default=1024)
parser.add_argument(
    "--counters", type=Path, help="Optional host dotnet-counters executable"
)
parser.add_argument("--cases", nargs="+")
args = parser.parse_args()
if os.geteuid() != 0:
    parser.error(
        "Host root access is required to read the container /proc and diagnostic socket."
    )
root = args.fixtures
outroot = args.results
outroot.mkdir(mode=0o700, parents=True, exist_ok=True)
IMAGE = args.image
limit = args.as_mib


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
        for k, v in vals.items():
            self.peak[k] = max(v, self.peak.get(k, 0))
        self.samples += 1

    def loop(self):
        while not self.stop.wait(0.02):
            try:
                self.sample()
            except (FileNotFoundError, ProcessLookupError):
                pass

    def finish(self):
        self.stop.set()
        self.thread.join()
        self.peak["cgroup_peak"] = int((self.cg / "memory.peak").read_text())
        self.peak["events"] = {
            k: int(v)
            for k, v in (
                line.split()
                for line in (self.cg / "memory.events").read_text().splitlines()
            )
        }
        self.peak["samples"] = self.samples
        return self.peak


def request(api, item):
    path, inputs, _expected = item
    body, ct = smoke.multipart(password=None, files=inputs)
    start = time.monotonic()
    code, _headers, result = api.request("POST", path, body, ct)
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
    else:
        problem = json.loads(result)
        assert (
            problem["title"]
            == "このPDFは処理できません。PDFの破損・パスワード設定や、画像が大きすぎないか確認してください。"
        )
        assert "reason" not in problem
    return {
        "status": code,
        "seconds": round(elapsed, 3),
        "output_bytes": len(result),
    }


def run(name, items, fill_mib=0):
    api = smoke.ApiContainer(
        IMAGE,
        [f"Pdf__QpdfAddressSpaceLimitBytes={limit * 1024 * 1024}"],
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
                    "00:01:15" if name == "sustained" else "00:00:35",
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
            for iteration in range(30 if name == "sustained" else 1):
                requests.extend(pool.map(worker, items))
        time.sleep(1.2)
        measure = mon.finish()
        mon = None
        if counters is not None:
            counters.wait(timeout=80)
            counters = None
        gc = {}
        if args.counters and cp.exists():
            for row in csv.DictReader(cp.open()):
                key = row["Counter Name"]
                gc[key] = max(float(row["Mean/Increment"]), gc.get(key, 0))
        measure["runtime_counters"] = gc
        if args.counters:
            assert gc, "No runtime counters were recorded"
        api.assert_clean()
        assert api.request("GET", "/healthz")[0] == 200
        assert measure["events"]["oom"] == measure["events"]["oom_kill"] == 0
        assert measure["qpdf_count"] == 2, (
            "Two simultaneous qpdf processes were not observed"
        )
        assert not any(
            Path(f"/proc/{pid}/comm").read_text().strip() == "qpdf"
            for pid in (mon_cgroup(api) / "cgroup.procs").read_text().split()
            if Path(f"/proc/{pid}/comm").exists()
        ), "qpdf process remains"

        result = {
            "case": name,
            "memory_mib": args.memory_mib,
            "limit_mib": limit,
            "fill_mib": fill_mib,
            "requests": requests,
            "measure": measure,
        }
        print(json.dumps(result), flush=True)
        (outroot / f"{limit}-{name}.json").write_text(json.dumps(result, indent=2))
        assert [r["status"] for r in requests] == [i[2] for i in items] * (
            30 if name == "sustained" else 1
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
    names = args.cases or [
        "4000x3000-baseline",
        "6000x4000-baseline",
        "8064x6048-baseline",
        "a4-600dpi-flate",
        "near-optimize",
        "near-merge",
        "10000x10000-baseline",
        "10000x10000-progressive",
        "100mp-color",
        "mixed",
        "mixed-as",
        "tmpfs-pressure",
        "sustained",
    ]
    for name in names:
        if name == "near-merge":
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
                ("/api/pdf/optimize", [load("8064x6048-baseline")], 200),
                ("/api/pdf/optimize", [load("10000x10000-progressive")], 422),
            ]
        elif name == "mixed-as":
            items = [
                ("/api/pdf/optimize", [load("8064x6048-baseline")], 200),
                ("/api/pdf/optimize", [load("100mp-color")], 422),
            ]
        elif name == "sustained" or name == "tmpfs-pressure":
            items = [("/api/pdf/optimize", [load("8064x6048-baseline")], 200)] * 2
        else:
            items = [
                (
                    "/api/pdf/optimize",
                    [load(name)],
                    422 if "progressive" in name else 200,
                )
            ] * 2
        run(name, items, 210 if name in ("tmpfs-pressure", "sustained") else 0)
