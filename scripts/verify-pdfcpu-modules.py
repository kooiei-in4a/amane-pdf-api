#!/usr/bin/env python3
"""取得binaryとnoticeのmodule一覧をgo version -mで照合する（CI/development専用）。"""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
inventory = json.loads((root / "third_party/pdfcpu-modules.json").read_text())
required = dict(re.findall(r"^\s*(\S+) (v\S+)", (root / "third_party/pdfcpu/go.mod").read_text(), re.M))
assert len(required) == 11
assert required == {m["path"]: m["version"] for m in inventory["modules"]}
output = subprocess.run(["go", "version", "-m", sys.argv[1]], stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL, check=True, text=True, timeout=30).stdout
assert output.splitlines()[0].endswith(": " + inventory["go_version"])
assert "\tmod\tgithub.com/pdfcpu/pdfcpu\t" + inventory["pdfcpu_version"] + "\t" in output
actual = {}
for line in output.splitlines()[1:]:
    parts = line.split()
    if parts and parts[0] == "dep":
        assert len(parts) == 4 and parts[1] not in actual
        actual[parts[1]] = (parts[2], parts[3])
    assert not line.strip().startswith("=>")
expected = {m["path"]: (m["version"], m["sum"]) for m in inventory["modules"] if m["in_linux_binary"]}
assert actual == expected
for module in inventory["modules"]:
    assert module["license_files"]
    for name in module["license_files"]:
        assert (root / "third_party/pdfcpu" / name).is_file()
for line in (root / "third_party/pdfcpu-licenses.sha256").read_text().splitlines():
    expected_hash, name = line.split("  ", 1)
    with (root / "third_party" / name).open("rb") as file:
        assert hashlib.file_digest(file, "sha256").hexdigest() == expected_hash
print("go.mod: 11 modules; Linux binary: 9 modules; licenses: PASS")
