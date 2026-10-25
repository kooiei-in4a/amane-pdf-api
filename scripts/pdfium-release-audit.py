#!/usr/bin/env python3
"""固定PDFiumと配布元の最新releaseを記録。更新通知はCVE判定ではない。"""
import json
import os
from pathlib import Path
import sys
from urllib.request import Request, urlopen

root = Path(__file__).resolve().parent.parent
lock = json.loads((root / "third_party/pdfium.lock").read_text())
headers = {"Accept": "application/vnd.github+json", "User-Agent": "amane-pdf-dependency-audit"}
if os.environ.get("GH_TOKEN"):
    headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]
request = Request("https://api.github.com/repos/bblanchon/pdfium-binaries/releases/latest", headers=headers)
with urlopen(request, timeout=60) as response:
    release = json.load(response)
tag = release["tag_name"]
if not isinstance(tag, str) or not tag.startswith("chromium/") or release["draft"] or release["prerelease"]:
    raise ValueError("PDFium release metadata is invalid")
result = {"adopted": lock["version"], "pdfium_version": lock["pdfium_version"],
          "artifact": lock["url"], "sha256": lock["sha256"], "latest": tag,
          "published_at": release["published_at"], "release_url": release["html_url"],
          "update_available": tag != lock["version"], "cve_coverage": "not-scanned"}
Path(sys.argv[1]).write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
print(f"PDFium: adopted={lock['version']}, latest={tag}; CVE自動照合の対象外")
