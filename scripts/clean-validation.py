#!/usr/bin/env python3
"""cleanの容量・object数上限に近い合成PDFを測定する。通常CI smokeとは別に実行。"""
import argparse
import concurrent.futures
import hashlib
import importlib.util
import json
from pathlib import Path
import random
import subprocess
import tempfile
import threading
import time


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


smoke = load("clean_smoke", "docker-smoke.py")
fixtures = load("clean_fixtures", "compress-validation.py")
MiB = 1048576
OBJECT_LIMIT = 50000
JSON_TARGET = 32 * MiB - 128 * 1024
SUCCESS_SECONDS = 24


class Monitor:
    # Read through docker exec; /proc/<pid>/root is not accessible on every host.
    def __init__(self, api):
        self.api = api
        self.stop = threading.Event()
        self.peak_tmpfs = self.peak_jobs = self.samples = 0
        self.failure = None
        assert api.exec("cat", "/sys/fs/cgroup/memory.swap.max").stdout.strip() == b"0"
        self.thread = threading.Thread(target=self.loop)

    def loop(self):
        while not self.stop.wait(0.1):
            try:
                blocks, free, size = map(int, self.api.exec("stat", "-f", "-c", "%b %f %S", "/tmp").stdout.split())
                self.peak_tmpfs = max(self.peak_tmpfs, (blocks - free) * size)
                jobs = self.api.exec("sh", "-c", "if [ -d /tmp/amane-pdf-api ]; then find /tmp/amane-pdf-api -mindepth 1 -maxdepth 1 -type d; fi").stdout.splitlines()
                self.peak_jobs = max(self.peak_jobs, len(jobs))
                self.samples += 1
            except Exception as error:
                self.failure = error
                self.stop.set()

    def finish(self):
        self.stop.set()
        self.thread.join()
        if self.failure is not None:
            raise RuntimeError("Resource sampling failed.") from self.failure
        events = {k: int(v) for k, v in (line.split() for line in self.api.exec("cat", "/sys/fs/cgroup/memory.events").stdout.decode().splitlines())}
        assert events["oom"] == events["oom_kill"] == 0
        assert self.samples > 0
        return {"tmpfs_peak_sampled": self.peak_tmpfs, "job_count_peak_sampled": self.peak_jobs,
                "memory_peak": int(self.api.exec("cat", "/sys/fs/cgroup/memory.peak").stdout), "memory_events": events, "samples": self.samples}

    def assert_stopped(self):
        commands = self.api.exec("ps", "-eo", "comm").stdout.decode().splitlines()
        assert not set(commands) & {"qpdf", "djpeg", "cjpeg"}


def near_limits():
    padding = 32 * MiB - 128 * 1024
    raw = random.Random(24).randbytes(50 * MiB - padding - 4096)
    # Retained private data is deliberately not in the small catalog update.
    # Both JSON and PDF approach their upload/inspection limits independently.
    return fixtures.pdf([
        b"<< /Type /Catalog /Pages 2 0 R /PieceInfo << /Text 4 0 R /Bytes 5 0 R >> /AF [7 0 R] >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 6 0 R >>",
        b"(" + b"P" * padding + b")",
        fixtures.stream("", raw),
        fixtures.stream("", b"q 0 0 20 20 re S Q\n"),
        b"<< /Type /Filespec /F (ATTACHMENT_FILENAME_MARKER) /EF << /F 8 0 R >> >>",
        fixtures.stream("/Type /EmbeddedFile", b"ATTACHMENT_PAYLOAD_MARKER"),
    ])


def many_objects(padding, object_count=OBJECT_LIMIT):
    pages, extra = divmod(object_count - 7, 3)
    # Keep the remainder reachable so qpdf retains exactly the requested count.
    boundary = " ".join(f"{8 + pages * 3 + index} 0 R" for index in range(extra))
    objects = [
        (f"<< /Type /Catalog /Pages 2 0 R /PieceInfo << /Text 4 0 R /Bytes 5 0 R "
         f"/Boundary [{boundary}] >> /AF [6 0 R] >>").encode(),
        f"<< /Type /Pages /Count {pages} /Kids [{' '.join(str(8 + index * 3)+' 0 R' for index in range(pages))}] >>".encode(),
        fixtures.stream("", b"q 0 0 20 20 re S Q\n"),
        b"(" + b"P" * padding + b")",
        fixtures.stream("", random.Random(24).randbytes(MiB)),
        b"<< /Type /Filespec /F (ATTACHMENT_FILENAME_MARKER) /EF << /F 7 0 R >> >>",
        fixtures.stream("/Type /EmbeddedFile", b"ATTACHMENT_PAYLOAD_MARKER"),
    ]
    for index in range(pages):
        page = 8 + index * 3
        objects.extend([
            (f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
             f"/Contents 3 0 R /Annots {page+2} 0 R /AF [6 0 R] >>").encode(),
            (f"<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI "
             f"/URI (https://example.test/page/{index+1}) >> >>").encode(),
            f"[{page+1} 0 R]".encode(),
        ])
    objects.extend([b"<< /Boundary (retained) >>"] * extra)
    assert len(objects) == object_count
    return fixtures.pdf(objects)


def request(api, source, barrier, directory, number, pages, expected_status):
    body, ct = smoke.multipart(source, password=None)
    barrier.wait(timeout=30)
    started = time.monotonic()
    code, headers, output = api.request("POST", "/api/pdf/clean", body, ct)
    seconds = time.monotonic() - started
    row = {"status": code, "seconds": seconds, "input_bytes": len(source), "output_bytes": len(output)}
    assert code == expected_status, row
    if expected_status == 422:
        assert headers["Content-Type"].split(";")[0] == "application/problem+json"
        problem = json.loads(output)
        assert problem["status"] == 422 and problem["reason"] == "too-complex", problem
        row["reason"] = problem["reason"]
        return row
    assert headers["Content-Type"].split(";")[0] == "application/pdf"
    assert "filename=cleaned.pdf" in headers["Content-Disposition"]
    path = directory / f"output-{number}.pdf"
    path.write_bytes(output)
    fixtures.command("qpdf", "--check", str(path))
    metadata = json.loads(fixtures.command("qpdf", "--json=2", "--json-key=qpdf", "--json-key=attachments",
                                            "--json-stream-data=none", "--decode-level=none", str(path)))
    assert metadata["attachments"] == {}
    objects = metadata["qpdf"][1]
    for item in objects.values():
        dictionary = item.get("value", item.get("stream", {}).get("dict"))
        if isinstance(dictionary, dict):
            assert all(key not in dictionary for key in ("/AF", "/EF", "/RF"))
            assert dictionary.get("/Type") != "/EmbeddedFile"
    # The decoded private binary data must be preserved despite final compression.
    catalog = objects["obj:" + objects["trailer"]["value"]["/Root"]]["value"]
    tree = objects["obj:" + catalog["/Pages"]]["value"]
    assert tree["/Count"] == pages
    assert len(tree["/Kids"]) == pages
    row["pages"] = pages
    row["output_objects"] = len(objects) - 1
    if pages > 1:
        links = sum(item.get("value", {}).get("/Subtype") == "/Link"
                    for item in objects.values() if isinstance(item.get("value"), dict))
        assert links == pages
        row["retained_links"] = links
    binary_ref = catalog["/PieceInfo"]["/Bytes"].split()[0]
    binary = fixtures.command("qpdf", "--show-object=" + binary_ref, "--filtered-stream-data", str(path))
    row["private_stream_sha256"] = hashlib.sha256(binary).hexdigest()
    return row


def case(image, concurrency, label):
    with tempfile.TemporaryDirectory(prefix="clean-validation-") as tmp:
        directory = Path(tmp)
        path = directory / "input.pdf"
        object_target = OBJECT_LIMIT + (label == "over-object-limit")
        pages = (object_target - 7) // 3 if label != "bytes" else 1
        expected_status = 422 if label == "over-object-limit" else 200
        source = many_objects(1, object_target) if pages > 1 else near_limits()
        path.write_bytes(source)
        if pages > 1:
            baseline = fixtures.command("qpdf", "--json=2", "--json-key=qpdf", "--json-stream-data=none", "--decode-level=none", str(path))
            padding = JSON_TARGET - len(baseline) + 1
            assert padding > 0
            source = many_objects(padding, object_target)
            path.write_bytes(source)
        fixtures.command("qpdf", "--check", str(path))
        objects = fixtures.command("qpdf", "--json=2", "--json-key=qpdf", "--json-stream-data=none", "--decode-level=none", str(path))
        assert len(source) < 50 * MiB and 31 * MiB < len(objects) < 32 * MiB
        object_count = len(json.loads(objects)["qpdf"][1]) - 1
        if pages > 1:
            assert object_count == object_target
            assert len(objects) == JSON_TARGET
        expected_hash = hashlib.sha256(fixtures.command("qpdf", "--show-object=5", "--filtered-stream-data", str(path))).hexdigest()
        with smoke.running_container(image, isolated=True) as api:
            monitor = Monitor(api)
            monitor.thread.start()
            try:
                with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as pool:
                    barrier = threading.Barrier(concurrency)
                    futures = [pool.submit(request, api, source, barrier, directory, number, pages, expected_status)
                               for number in range(1, concurrency + 1)]
                    rows = [future.result() for future in futures]
                if expected_status == 200:
                    assert all(row["private_stream_sha256"] == expected_hash for row in rows)
                api.assert_clean()
                monitor.assert_stopped()
                assert api.request("GET", "/healthz")[0] == 200
            finally:
                resources = monitor.finish()
            print(json.dumps({"case": label, "input_json_bytes": len(objects), "input_objects": object_count,
                              "requests": rows, "resources": resources}, ensure_ascii=False), flush=True)
            assert resources["job_count_peak_sampled"] == concurrency
            limit = SUCCESS_SECONDS if expected_status == 200 else 30
            assert all(row["seconds"] <= limit for row in rows), rows


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("image", nargs="?", default="amane-pdf-api:ci")
    parser.add_argument("--concurrency", type=int, choices=(1, 2), default=2)
    parser.add_argument("--case", choices=("all", "bytes", "many-objects", "over-object-limit"), default="all")
    args = parser.parse_args()
    for label in ("bytes", "many-objects", "over-object-limit") if args.case == "all" else (args.case,):
        case(args.image, args.concurrency, label)
