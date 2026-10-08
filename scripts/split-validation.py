#!/usr/bin/env python3
"""分割の容量・同時実行・時間を一度測定する。通常のsmoke/CIとは別に実行する。"""
import argparse
import concurrent.futures
import importlib.util
import io
import json
import os
from pathlib import Path
import random
import subprocess
import tempfile
import threading
import time
import zipfile


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


smoke = load("split_smoke", "docker-smoke.py")
fixtures = load("split_fixtures", "compress-validation.py")
MiB = 1048576


def image_pages(sizes, shared=False):
    """固定seedの非圧縮RGB。3 MiB単位、同じresourceの複製も作る。"""
    objects = [b"", b""]
    refs = []
    images = {}
    for size in sizes:
        if not shared or size not in images:
            # 1024 pixel RGB rows make each 3 MiB exactly 1024 rows.
            height = size * MiB // (1024 * 3)
            data = random.Random(23 + size).randbytes(size * MiB)
            objects.append(fixtures.stream(
                f"/Type /XObject /Subtype /Image /Width 1024 /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8", data))
            images[size] = len(objects)
        image = images[size]
        objects.append(fixtures.stream("", b"q 100 0 0 100 0 0 cm /Im0 Do Q\n"))
        content = len(objects)
        objects.append(f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im0 {image} 0 R >> >> /Contents {content} 0 R >>".encode())
        refs.append(len(objects))
    objects[0] = b"<< /Type /Catalog /Pages 2 0 R >>"
    objects[1] = f"<< /Type /Pages /Kids [{' '.join(str(ref)+' 0 R' for ref in refs)}] /Count {len(refs)} >>".encode()
    return fixtures.pdf(objects)


def hundred_pages():
    objects = [b"<< /Type /Catalog /Pages 2 0 R >>", b""]
    for index in range(100):
        objects.append(b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>")
    objects[1] = f"<< /Type /Pages /Kids [{' '.join(str(index)+' 0 R' for index in range(3,103))}] /Count 100 >>".encode()
    return fixtures.pdf(objects)


def check_pdf(data, directory):
    path = directory / "check.pdf"
    path.write_bytes(data)
    result = subprocess.run(["qpdf", "--check", str(path)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
    assert result.returncode == 0, "Invalid synthetic fixture/output."


class Monitor:
    def __init__(self, api):
        pid = int(smoke.docker("inspect", "--format", "{{.State.Pid}}", api.name).stdout)
        self.root = Path(f"/proc/{pid}/root")
        self.cg = self.root / "sys/fs/cgroup"
        self.stop = threading.Event()
        self.peak_tmpfs = 0
        self.peak_jobs = 0
        self.samples = 0
        self.failure = None
        assert (self.cg / "memory.swap.max").read_text().strip() == "0"
        self.thread = threading.Thread(target=self.loop)

    def loop(self):
        while not self.stop.wait(0.02):
            try:
                st = os.statvfs(self.root / "tmp")
                self.peak_tmpfs = max(self.peak_tmpfs, (st.f_blocks - st.f_bfree) * st.f_frsize)
                jobs = list((self.root / "tmp/amane-pdf-api").glob("*"))
                self.peak_jobs = max(self.peak_jobs, len(jobs))
                self.samples += 1
            except (FileNotFoundError, ProcessLookupError):
                pass
            except Exception as error:
                self.failure = error
                self.stop.set()

    def finish(self):
        self.stop.set()
        self.thread.join()
        if self.failure is not None:
            raise RuntimeError("Resource sampling failed.") from self.failure
        events = {k: int(v) for k, v in (line.split() for line in (self.cg / "memory.events").read_text().splitlines())}
        assert events["oom"] == events["oom_kill"] == 0
        assert self.samples > 0
        return {"tmpfs_peak_sampled": self.peak_tmpfs, "job_count_peak_sampled": self.peak_jobs,
                "memory_peak": int((self.cg / "memory.peak").read_text()), "memory_events": events, "samples": self.samples}

    def assert_stopped(self):
        for pid in (self.cg / "cgroup.procs").read_text().split():
            try:
                assert Path(f"/proc/{pid}/comm").read_text().strip() not in ("qpdf", "djpeg", "cjpeg"), "Process remains."
            except FileNotFoundError:
                pass


def request(api, item, barrier):
    path, inputs, password = item
    body, ct = smoke.multipart(files=inputs, password=password)
    barrier.wait(timeout=30)
    started = time.monotonic()
    code, headers, data = api.request("POST", path, body, ct)
    seconds = time.monotonic() - started
    row = {"operation": path.split("?")[0].rsplit("/", 1)[-1], "status": code,
           "seconds": round(seconds, 3), "input_bytes": sum(map(len, inputs)), "output_bytes": len(data)}
    assert code in (200, 422, 504), row
    if code == 200:
        with tempfile.TemporaryDirectory(prefix="split-output-") as tmp:
            directory = Path(tmp)
            if row["operation"] == "split":
                assert headers["Content-Type"].split(";")[0] == "application/zip"
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    for entry in archive.infolist():
                        assert entry.compress_type == zipfile.ZIP_STORED and entry.date_time == (1980, 1, 1, 0, 0, 0)
                        check_pdf(archive.read(entry), directory)
                    row["parts"] = len(archive.infolist())
                    row["pdf_total_bytes"] = sum(entry.file_size for entry in archive.infolist())
            else:
                check_pdf(data, directory)
    else:
        problem = json.loads(data)
        if code == 422 and row["operation"] == "split":
            assert problem.get("reason") == "output-too-large", row
    return row


def case(image, label, items):
    with smoke.running_container(image, isolated=True) as api:
        monitor = Monitor(api)
        monitor.thread.start()
        try:
            with concurrent.futures.ThreadPoolExecutor(max_workers=len(items)) as pool:
                barrier = threading.Barrier(len(items))
                responses = list(pool.map(lambda item: request(api, item, barrier), items))
        finally:
            measurements = monitor.finish()
        api.assert_clean()
        monitor.assert_stopped()
        assert api.request("GET", "/healthz")[0] == 200
        row = {"case": label, "responses": responses, **measurements}
        print(json.dumps(row), flush=True)
        return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("results", type=Path)
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Host root access is required to sample container /proc.")
    args.results.parent.mkdir(parents=True, exist_ok=True)
    report = {"image": args.image, "image_id": smoke.docker("image", "inspect", "--format", "{{.Id}}", args.image).stdout.decode().strip(),
              "architecture": os.uname().machine, "page_size": os.sysconf("SC_PAGE_SIZE"),
              "cpu": 1, "memory_bytes": 1536 * MiB, "tmpfs_bytes": 256 * MiB, "cases": []}
    def run(label, items):
        report["cases"].append(case(args.image, label, items))
        args.results.write_text(json.dumps(report, indent=2) + "\n")
    source = image_pages([30, 18])
    shared = image_pages([27, 27], shared=True)
    hundred = hundred_pages()
    with tempfile.TemporaryDirectory(prefix="split-input-") as tmp:
        directory = Path(tmp)
        for fixture in (source, shared, hundred):
            assert len(fixture) < 50 * MiB
            check_pdf(fixture, directory)
        path = directory / "source.pdf"
        path.write_bytes(source)
        output = directory / "encrypted.pdf"
        job = directory / "job.json"
        job.write_text(json.dumps({"inputFile": str(path), "outputFile": str(output), "encrypt": {
            "userPassword": smoke.PASSWORD, "ownerPassword": "synthetic-owner", "256bit": {}}}))
        subprocess.run(["qpdf", "--job-json-file=" + str(job)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True, timeout=30)
        encrypted = output.read_bytes()
    split = ("/api/pdf/split?every=1", [source], None)
    run("near-limit-two-splits", [split, split])
    run("reverse-large-part", [("/api/pdf/split?ranges=2,1", [source], None)])
    run("shared-resource-expansion", [("/api/pdf/split?every=1", [shared], None)])
    run("hundred-parts", [("/api/pdf/split?every=1", [hundred], None)])
    run("split-compress", [split, ("/api/pdf/compress?level=standard", [source], None)])
    run("split-merge", [split, ("/api/pdf/merge", [source, smoke.FIXTURE], None)])
    run("split-unlock", [split, ("/api/pdf/unlock", [encrypted], smoke.PASSWORD)])
    print("Split capacity/concurrency/time validation PASS", flush=True)


if __name__ == "__main__":
    main()
