#!/usr/bin/env python3
"""実qpdf単体でAS/JPEGMEMを比較する。APIの同時HTTP負荷はdocker-memory-check.pyで検証。"""

import argparse
import importlib.util
import json
import os
import subprocess
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
parser.add_argument("--cases", nargs="+", required=True)
parser.add_argument(
    "--as-mib",
    type=int,
    nargs="+",
    default=[544],
    help="0 means no AS limit (single process only)",
)
parser.add_argument("--jpeg-memory", nargs="+", default=["unset", "600M"])
parser.add_argument("--repeat", type=int, default=3)
args = parser.parse_args()
if os.geteuid() != 0:
    parser.error("Host root access is required to read container /proc.")
if args.repeat < 1 or any(limit < 0 for limit in args.as_mib):
    parser.error("repeat must be positive; AS must be nonnegative.")
args.results.parent.mkdir(mode=0o700, parents=True, exist_ok=True)


def check(api, cgroup, name, limit, jpeg_memory):
    command = ["docker", "exec", api.name, "env", "-u", "JPEGMEM"]
    if jpeg_memory != "unset":
        command.append("JPEGMEM=" + jpeg_memory)
    if limit:
        command.extend(["prlimit", f"--as={limit * 1048576}:{limit * 1048576}", "--"])
    command.extend(["qpdf", "--check", "/tmp/qpdf-input.pdf"])
    peak_rss = peak_vm = 0
    start = time.monotonic()
    with subprocess.Popen(
        command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
    ) as process:
        while process.poll() is None:
            if time.monotonic() - start > 40:
                process.kill()
                raise TimeoutError(
                    "qpdf calibration timed out; container will be removed"
                )
            for pid in (cgroup / "cgroup.procs").read_text().split():
                try:
                    if Path(f"/proc/{pid}/comm").read_text().strip() != "qpdf":
                        continue
                    fields = dict(
                        line.split(":", 1)
                        for line in Path(f"/proc/{pid}/status").read_text().splitlines()
                        if ":" in line
                    )
                    peak_rss = max(
                        peak_rss, int(fields.get("VmHWM", "0").split()[0]) * 1024
                    )
                    peak_vm = max(
                        peak_vm, int(fields.get("VmSize", "0").split()[0]) * 1024
                    )
                except (FileNotFoundError, ProcessLookupError):
                    pass
            time.sleep(0.005)
        return {
            "case": name,
            "as_mib": limit,
            "jpeg_memory": jpeg_memory,
            "exit": process.returncode,
            "rss_bytes": peak_rss,
            "vm_bytes": peak_vm,
            "seconds": round(time.monotonic() - start, 3),
        }


def main():
    api = smoke.ApiContainer(args.image, isolated=True)
    records = []
    try:
        api.start()
        pid = int(
            smoke.docker("inspect", "--format", "{{.State.Pid}}", api.name).stdout
        )
        cgroup = Path(f"/proc/{pid}/root/sys/fs/cgroup")
        assert (cgroup / "memory.swap.max").read_text().strip() == "0"
        for name in args.cases:
            fixture = (args.fixtures / (name + ".pdf")).read_bytes()
            smoke.docker(
                "exec",
                "--interactive",
                api.name,
                "dd",
                "of=/tmp/qpdf-input.pdf",
                "status=none",
                data=fixture,
            )
            for limit in args.as_mib:
                for jpeg_memory in args.jpeg_memory:
                    for _ in range(args.repeat):
                        record = check(api, cgroup, name, limit, jpeg_memory)
                        records.append(record)
                        print(json.dumps(record), flush=True)
                        args.results.write_text(json.dumps(records, indent=2))
        events = dict(
            line.split() for line in (cgroup / "memory.events").read_text().splitlines()
        )
        assert events["oom"] == events["oom_kill"] == "0"
        assert api.request("GET", "/healthz")[0] == 200
    finally:
        api.close()


if __name__ == "__main__":
    main()
