#!/usr/bin/env python3
"""Trivyの検査範囲を確認し、重大な脆弱性・検査欠落を失敗にする。"""
import json
from pathlib import Path
import sys


def evaluate(report, inventory):
    errors = []
    results = report.get("Results", [])
    os_info = report.get("Metadata", {}).get("OS", {})
    if os_info.get("Family") != "ubuntu" or not os_info.get("Name") or os_info.get("EOSL", False) is not False:
        errors.append("OSの識別・サポート状態を確認できません。")
    os_packages = {p["Name"] for r in results if r.get("Class") == "os-pkgs"
                   for p in r.get("Packages", [])}
    for package in ("qpdf", "libqpdf30", "libjpeg-turbo-progs", "libjpeg-turbo8"):
        if package not in os_packages:
            errors.append(f"OS検査対象が欠落: {package}")
    binaries = [r for r in results if r.get("Type") == "gobinary"
                and r.get("Target", "").lstrip("/") == "opt/amane-pdf/bin/pdfcpu"]
    if len(binaries) != 1:
        errors.append("pdfcpuのGo binary検査結果が欠落・重複しています。")
    else:
        packages = {p["Name"]: p["Version"] for p in binaries[0].get("Packages", [])}
        expected = {m["path"]: m["version"] for m in inventory["modules"] if m["in_linux_binary"]}
        expected.update({"stdlib": inventory["go_version"].removeprefix("go"),
                         "github.com/pdfcpu/pdfcpu": inventory["pdfcpu_version"]})
        for name, version in expected.items():
            # Trivy normalizes some Go versions by removing the v prefix.
            if packages.get(name, "").removeprefix("v") != version.removeprefix("v"):
                errors.append(f"Go検査対象・版が不一致: {name} {version}")
    findings = [(r.get("Target"), v) for r in results for v in r.get("Vulnerabilities", [])]
    high = [(target, v) for target, v in findings if v.get("Severity") in ("HIGH", "CRITICAL")]
    lines = ["### 外部依存監査", "", f"検出件数: 全重大度 {len(findings)} / High・Critical {len(high)}",
             "修正版の有無にかかわらずHigh/Criticalで失敗します。", ""]
    for target, v in high:
        lines.append(f"- {v['Severity']}: {v['VulnerabilityID']} / {v['PkgName']} "
                     f"{v['InstalledVersion']} → {v.get('FixedVersion') or '修正版未提供'} ({target})")
    lines.extend(f"- 検査失敗: {error}" for error in errors)
    lines += ["", "PDFiumおよび内包ライブラリはCVE自動照合の対象外です。脆弱性なしを意味しません。",
              "採用版・更新情報と手動確認先はpdfium-release.jsonおよびdocs/dependency-audit.mdを参照してください。"]
    return bool(errors or high), "\n".join(lines) + "\n"


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent
    failed, summary = evaluate(json.loads(Path(sys.argv[1]).read_text()),
                               json.loads((root / "third_party/pdfcpu-modules.json").read_text()))
    Path(sys.argv[2]).write_text(summary)
    print(summary)
    sys.exit(int(failed))
