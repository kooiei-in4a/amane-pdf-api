"""容量測定driverの応答証拠と境界探索の回帰テスト。"""
import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import unittest
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("followup", Path(__file__).with_name("from-images-followup.py"))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class DriverTests(unittest.TestCase):
    def test_monitor_handles_qpdf_inspection_exit_and_rejects_unlimited_writer(self):
        for command, expected_error, expected_samples in (
                (b"", False, 0),
                (b"qpdf\0--check\0input.pdf\0", False, 1),
                (b"qpdf\0input.pdf\0output.pdf\0", True, 0)):
            with self.subTest(command=command), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / "tmp/amane-pdf-api").mkdir(parents=True)
                (root / "cgroup.procs").write_text("7\n")
                proc = root / "proc/7"
                proc.mkdir(parents=True)
                (proc / "comm").write_text("qpdf\n")
                (proc / "status").write_text("")
                (proc / "limits").write_text("Max address space 570425344 570425344 bytes\n"
                    "Max file size unlimited unlimited bytes\nMax core file size 0 0 bytes\n")
                (proc / "cmdline").write_bytes(command)
                monitor = m.Monitor.__new__(m.Monitor)
                monitor.root = monitor.cg = root
                waits = iter((False, True))
                monitor.stop = SimpleNamespace(wait=lambda _: next(waits))
                monitor.peak, monitor.processes = {}, {}
                monitor.samples, monitor.error = 0, None
                with patch.object(m, "Path", return_value=root / "proc"):
                    monitor.loop()
                self.assertEqual(expected_error, monitor.error is not None)
                self.assertEqual(expected_samples, monitor.samples)

    def test_only_read_only_qpdf_commands_allow_unlimited_fsize(self):
        for flag in (b"--check", b"--is-encrypted", b"--requires-password", b"--show-npages"):
            self.assertTrue(m.stdout_only_qpdf("qpdf", [b"/usr/bin/qpdf", flag, b"input.pdf"]))
        self.assertFalse(m.stdout_only_qpdf("qpdf", [b"/usr/bin/qpdf", b"input.pdf", b"output.pdf"]))
        self.assertFalse(m.stdout_only_qpdf("qpdf", [b"/usr/bin/qpdf", b"--check", b"input.pdf", b"output.pdf"]))
        self.assertFalse(m.stdout_only_qpdf("cjpeg", [b"/usr/bin/cjpeg", b"--check", b"input.pdf"]))

    def test_monitor_preserves_an_empty_exception_as_a_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            cg = Path(directory)
            (cg / "memory.peak").write_text("123\n")
            (cg / "memory.events").write_text("oom 0\noom_kill 0\n")
            monitor = m.Monitor.__new__(m.Monitor)
            monitor.cg = cg
            monitor.stop = threading.Event()
            monitor.thread = threading.Thread(target=lambda: None)
            monitor.thread.start()
            monitor.peak = {}
            monitor.processes = {}
            monitor.samples = 0
            monitor.error = AssertionError()
            metrics = monitor.finish()
        self.assertTrue(metrics["monitor_error"])
        self.assertIn("AssertionError", metrics["monitor_error"])

    def test_startup_failure_keeps_non_http_evidence(self):
        spec = importlib.util.spec_from_file_location("as_validation", Path(__file__).with_name("from-images-as-validation.py"))
        driver = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(driver)
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(output=Path(directory))
            case = {"name": "synthetic"}
            with patch.object(driver, "http_batch", side_effect=AssertionError("Docker health check failed.")):
                with self.assertRaisesRegex(AssertionError, "Docker health check failed"):
                    driver.checked_http_batch(args, [], [(case, "from-images", "original")], 1152)
            row = json.loads(next(args.output.glob("startup-*.json")).read_text())
        self.assertTrue(row["startup_failed"])
        self.assertEqual(0, row["http_requests_sent"])
        self.assertFalse(row["qualified"])
        self.assertNotIn("status", row)

    def test_working_estimate_matches_shared_vectors_and_default_admission(self):
        source = m.ROOT / "tests/Amane.Pdf.Api.Tests/Fixtures/png-working-vectors.json"
        vectors = json.loads(source.read_text())
        self.assertEqual(320 * m.MIB, vectors["working_limit_bytes"])
        for row in vectors["cases"]:
            with self.subTest(name=row["name"]):
                estimate = m.working_bytes(row["width"], row["height"], row["depth"], row["color"],
                    row["transparency"], row["encoded_bytes"], row["interlace"] == 1)
                self.assertEqual(row["working_bytes"], estimate)
                self.assertEqual(row["accepted"], estimate <= vectors["working_limit_bytes"])

    def test_adam7_generator_preserves_decoded_pixels(self):
        spec = importlib.util.spec_from_file_location("as_validation", Path(__file__).with_name("from-images-as-validation.py"))
        driver = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(driver)
        with tempfile.TemporaryDirectory() as directory:
            for depth, color, alpha in ((1, 0, False), (2, 0, True), (4, 3, True), (8, 0, True),
                    (8, 2, True), (8, 4, True), (8, 6, True), (16, 0, False), (16, 2, False), (16, 6, True)):
                with self.subTest(depth=depth, color=color):
                    data = driver.pixels(20, 17, depth, color, alpha, True, 59)
                    decoded = []
                    for interlace in (False, True):
                        path = Path(directory) / "fixture.png"
                        driver.write_png(path, data, depth, color, alpha, 59, interlace)
                        with m.Image.open(path) as image:
                            self.assertEqual((20, 17), image.size)
                            decoded.append(m.np.array(image.convert("RGBA") if depth != 16 or color != 0 else image))
                    m.np.testing.assert_array_equal(decoded[0], decoded[1])

    def test_non_json_error_keeps_http_evidence(self):
        class Api:
            def request(self, *args):
                return 503, {"Content-Type": "text/html"}, b"service unavailable"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "input.png").write_bytes(b"synthetic")
            evidence, body = m.send(Api(), root, {"file": "input.png", "sha256": "fixture-hash"},
                                    "from-images", "original", threading.Barrier(1))
        self.assertEqual(503, evidence["status"])
        self.assertEqual("text/html", evidence["media_type"])
        self.assertEqual(len(body), evidence["response_bytes"])
        self.assertEqual(["Invalid JSON problem response"], evidence["response_validation_errors"])
        self.assertNotIn("transport_error", evidence)

    def test_prefix_boundary_matches_complete_png_sizes(self):
        with tempfile.TemporaryDirectory() as directory:
            for depth, color, alpha, high in ((16, 0, False, True), (16, 6, True, False),
                                             (8, 3, True, True), (8, 2, True, True)):
                with self.subTest(depth=depth, color=color):
                    limit = 64 * m.MIB + 25000
                    height = m.boundary_height(64, depth, color, alpha, high, 61, limit)
                    for h, accepted in ((height, True), (height + 1, False)):
                        path = Path(directory) / "fixture.png"
                        m.png(path, 64, h, depth, color, alpha, high, 61)
                        estimate = m.working_bytes(64, h, depth, color, alpha, path.stat().st_size)
                        self.assertEqual(accepted, estimate <= limit)


if __name__ == "__main__":
    unittest.main()
