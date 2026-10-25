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
        self.presence = {"usr/bin/pebble": True}
        self.report = {"Metadata": {"OS": {"Family": "ubuntu", "Name": "26.04"}}, "Results": [
            {"Target": "ubuntu", "Class": "os-pkgs", "Packages": [
                {"Name": n, "Version": "12.3.2-1"} for n in ("qpdf", "libqpdf30", "libjpeg-turbo-progs", "libjpeg-turbo8")]},
            {"Target": "opt/amane-pdf/bin/pdfcpu", "Type": "gobinary", "Packages": [
                {"Name": m["path"], "Version": m["version"]} for m in inventory["modules"]
                if m["in_linux_binary"]] + [
                    {"Name": "stdlib", "Version": inventory["go_version"][2:]},
                    {"Name": "github.com/pdfcpu/pdfcpu", "Version": inventory["pdfcpu_version"]}]},
            {"Target": "usr/bin/pebble", "Type": "gobinary", "Packages": [
                {"Name": "stdlib", "Version": "v1.27.1"}]}]}

    def test_complete_without_known_high_is_pass_and_discloses_pdfium_gap(self):
        failed, summary = audit.evaluate(self.report, inventory, self.presence)
        self.assertFalse(failed)
        self.assertIn("CVE自動照合の対象外", summary)

    def test_all_severities_and_unfixed_high(self):
        for severity in ("LOW", "MEDIUM", "HIGH", "CRITICAL"):
            with self.subTest(severity=severity):
                report = deepcopy(self.report)
                report["Results"][0]["Vulnerabilities"] = [{"Severity": severity,
                    "VulnerabilityID": "TEST-1", "PkgName": "qpdf", "InstalledVersion": "1"}]
                failed, summary = audit.evaluate(report, inventory, self.presence)
                self.assertEqual(severity in ("HIGH", "CRITICAL"), failed)
                if failed:
                    self.assertIn("修正版未提供", summary)

    def test_missing_os_and_eol_fail(self):
        for os_info in ({}, {"Family": "alpine"}, {"Family": "ubuntu", "EOSL": True}):
            self.report["Metadata"]["OS"] = os_info
            self.assertTrue(audit.evaluate(self.report, inventory, self.presence)[0])

    def test_missing_os_package_fails(self):
        self.report["Results"][0]["Packages"].pop()
        self.assertTrue(audit.evaluate(self.report, inventory, self.presence)[0])

    def test_missing_go_or_any_module_fails(self):
        for index in range(len(self.report["Results"][1]["Packages"])):
            report = deepcopy(self.report)
            report["Results"][1]["Packages"].pop(index)
            self.assertTrue(audit.evaluate(report, inventory, self.presence)[0])
        self.report["Results"].pop(1)
        self.assertTrue(audit.evaluate(self.report, inventory, self.presence)[0])

    def test_os_version_and_support_metadata_fail_closed(self):
        for name in (None, "", " ", "unknown", 26.04):
            with self.subTest(name=name):
                report = deepcopy(self.report)
                report["Metadata"]["OS"]["Name"] = name
                self.assertTrue(audit.evaluate(report, inventory, self.presence)[0])
        for state in (True, None, "false", 0):
            with self.subTest(eosl=state):
                report = deepcopy(self.report)
                report["Metadata"]["OS"]["EOSL"] = state
                self.assertTrue(audit.evaluate(report, inventory, self.presence)[0])
        self.report["Metadata"]["OS"]["EOSL"] = False
        self.assertFalse(audit.evaluate(self.report, inventory, self.presence)[0])

    def test_every_os_package_requires_a_nonempty_version(self):
        for index in range(4):
            for value in (None, "", " ", 123):
                with self.subTest(index=index, value=value):
                    report = deepcopy(self.report)
                    report["Results"][0]["Packages"][index]["Version"] = value
                    failed, summary = audit.evaluate(report, inventory, self.presence)
                    self.assertTrue(failed)
                    self.assertIn("OS検査対象の版が欠落・不正", summary)
            report = deepcopy(self.report)
            del report["Results"][0]["Packages"][index]["Version"]
            self.assertTrue(audit.evaluate(report, inventory, self.presence)[0])

    def test_present_pebble_requires_exactly_one_go_result_with_runtime_version(self):
        for result in (None, {"Target": "usr/bin/pebble", "Type": "gobinary"},
                       {"Target": "usr/bin/pebble", "Type": "gobinary", "Packages": [
                           {"Name": "stdlib", "Version": ""}]}):
            with self.subTest(result=result):
                report = deepcopy(self.report)
                report["Results"].pop()
                if result is not None:
                    report["Results"].append(result)
                self.assertTrue(audit.evaluate(report, inventory, self.presence)[0])
        self.report["Results"].append(deepcopy(self.report["Results"][-1]))
        self.assertTrue(audit.evaluate(self.report, inventory, self.presence)[0])

    def test_actually_removed_pebble_is_not_required(self):
        self.report["Results"].pop()
        self.presence["usr/bin/pebble"] = False
        self.assertFalse(audit.evaluate(self.report, inventory, self.presence)[0])

    def test_missing_or_invalid_presence_fails(self):
        for presence in ({}, {"usr/bin/pebble": "false"}, {"usr/bin/pebble": None}):
            self.assertTrue(audit.evaluate(self.report, inventory, presence)[0])

    def test_inconsistent_presence_and_scan_fails(self):
        self.presence["usr/bin/pebble"] = False
        self.assertTrue(audit.evaluate(self.report, inventory, self.presence)[0])

    def test_wrong_version_fails(self):
        self.report["Results"][1]["Packages"][0]["Version"] = "0.0.0"
        self.assertTrue(audit.evaluate(self.report, inventory, self.presence)[0])

    def test_empty_result_fails(self):
        self.assertTrue(audit.evaluate({}, inventory, self.presence)[0])


if __name__ == "__main__":
    unittest.main()
