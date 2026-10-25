#!/usr/bin/env python3
"""共通restoreの引数・終了判定・summaryが定期実行でも維持されることを確認。"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

script = Path(__file__).resolve().with_name("nuget-audit.sh")


class NuGetAuditTests(unittest.TestCase):
    def test_restore_verdict_and_summary(self):
        for code, output in ((0, ""), (0, "warning NU1902: moderate"),
                             (1, "error NU1903: high"), (1, "error NU1904: critical"),
                             (1, "error NU1900: fetch failed"), (1, "error NU1905: no audit source"),
                             (1, "error NU1301: restore failed")):
            with self.subTest(output=output), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                fake = root / "dotnet"
                fake.write_text('#!/bin/bash\nprintf "%s\\n" "$@" > args.txt\n'
                                'printf "%s\\n" "$MOCK_OUTPUT"\nexit "$MOCK_EXIT"\n')
                fake.chmod(0o700)
                env = os.environ | {"PATH": str(root) + ":" + os.environ["PATH"],
                    "MOCK_OUTPUT": output, "MOCK_EXIT": str(code), "GITHUB_STEP_SUMMARY": str(root / "summary")}
                result = subprocess.run(["bash", str(script)], cwd=root, env=env, capture_output=True)
                self.assertEqual(code, result.returncode, result.stderr)
                self.assertEqual(["restore", "-p:NuGetAudit=true", "-p:NuGetAuditMode=all",
                    "-p:WarningsAsErrors=NU1900%3BNU1903%3BNU1904%3BNU1905"],
                    (root / "args.txt").read_text().splitlines())
                summary = (root / "summary").read_text()
                if "NU19" in output:
                    self.assertIn(output, summary)
                if code:
                    self.assertIn("restore 失敗", summary)
                    self.assertNotIn("該当なし", summary)
                if not output:
                    self.assertIn("該当なし", summary)


if __name__ == "__main__":
    unittest.main()
