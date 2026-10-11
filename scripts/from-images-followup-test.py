"""容量測定driverの応答証拠と境界探索の回帰テスト。"""
import importlib.util
from pathlib import Path
import tempfile
import threading
import unittest

spec = importlib.util.spec_from_file_location("followup", Path(__file__).with_name("from-images-followup.py"))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class DriverTests(unittest.TestCase):
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
