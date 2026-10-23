#!/usr/bin/env python3
"""成功・重大な検出・検査漏れを合成レポートで検証する。"""
from copy import deepcopy
import importlib.util
import json
from pathlib import Path
import unittest

root = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("audit", root / "scripts/native-audit.py")
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)
inventory = json.loads((root / "third_party/pdfcpu-modules.json").read_text())


class VerdictTests(unittest.TestCase):
    def setUp(self):
        self.report = {"Metadata": {"OS": {"Family": "ubuntu", "Name": "26.04"}}, "Results": [
            {"Target": "ubuntu", "Class": "os-pkgs", "Packages": [
                {"Name": n} for n in ("qpdf", "libqpdf30", "libjpeg-turbo-progs", "libjpeg-turbo8")]},
            {"Target": "opt/amane-pdf/bin/pdfcpu", "Type": "gobinary", "Packages": [
                {"Name": m["path"], "Version": m["version"]} for m in inventory["modules"]
                if m["in_linux_binary"]] + [
                    {"Name": "stdlib", "Version": inventory["go_version"][2:]},
                    {"Name": "github.com/pdfcpu/pdfcpu", "Version": inventory["pdfcpu_version"]}]}]}

    def test_complete_without_known_high_is_pass_and_discloses_pdfium_gap(self):
        failed, summary = audit.evaluate(self.report, inventory)
        self.assertFalse(failed)
        self.assertIn("CVE自動照合の対象外", summary)

    def test_all_severities_and_unfixed_high(self):
        for severity in ("LOW", "MEDIUM", "HIGH", "CRITICAL"):
            with self.subTest(severity=severity):
                report = deepcopy(self.report)
                report["Results"][0]["Vulnerabilities"] = [{"Severity": severity,
                    "VulnerabilityID": "TEST-1", "PkgName": "qpdf", "InstalledVersion": "1"}]
                failed, summary = audit.evaluate(report, inventory)
                self.assertEqual(severity in ("HIGH", "CRITICAL"), failed)
                if failed:
                    self.assertIn("修正版未提供", summary)

    def test_missing_os_and_eol_fail(self):
        for os_info in ({}, {"Family": "alpine"}, {"Family": "ubuntu", "EOSL": True}):
            self.report["Metadata"]["OS"] = os_info
            self.assertTrue(audit.evaluate(self.report, inventory)[0])

    def test_missing_os_package_fails(self):
        self.report["Results"][0]["Packages"].pop()
        self.assertTrue(audit.evaluate(self.report, inventory)[0])

    def test_missing_go_or_any_module_fails(self):
        for index in range(len(self.report["Results"][1]["Packages"])):
            report = deepcopy(self.report)
            report["Results"][1]["Packages"].pop(index)
            self.assertTrue(audit.evaluate(report, inventory)[0])
        self.report["Results"].pop()
        self.assertTrue(audit.evaluate(self.report, inventory)[0])

    def test_wrong_version_fails(self):
        self.report["Results"][1]["Packages"][0]["Version"] = "0.0.0"
        self.assertTrue(audit.evaluate(self.report, inventory)[0])

    def test_empty_result_fails(self):
        self.assertTrue(audit.evaluate({}, inventory)[0])


if __name__ == "__main__":
    unittest.main()
