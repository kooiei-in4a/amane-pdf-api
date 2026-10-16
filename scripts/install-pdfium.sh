#!/bin/sh
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec python3 - "$script_dir/.." "$@" <<'PY'
import argparse
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tarfile
import tempfile

repo = Path(sys.argv[1]).resolve()
parser = argparse.ArgumentParser(description="Install locked PDFium and licenses without runtime downloads.")
parser.add_argument("--download-cache", type=Path, help="Read the locked archive here; still verify its checksum.")
parser.add_argument("prefix", type=Path)
args = parser.parse_args(sys.argv[2:])
lock = json.loads((repo / "third_party/pdfium.lock").read_text())
if sys.platform != "linux" or platform.machine() not in ("x86_64", "amd64") or lock["platform"] != "linux-x64":
    raise SystemExit("PDFium requires Linux x86_64.")
prefix = args.prefix.resolve()
if prefix.exists() and (not prefix.is_dir() or any(prefix.iterdir())):
    raise SystemExit("Install prefix must be absent or empty.")
prefix.parent.mkdir(parents=True, exist_ok=True)

with tempfile.TemporaryDirectory(prefix=".pdfium-install-", dir=prefix.parent) as temporary:
    work = Path(temporary)
    archive_path = work / lock["name"]
    if args.download_cache:
        shutil.copyfile(args.download_cache / lock["name"], archive_path)
    else:
        subprocess.run(["curl", "--fail", "--location", "--silent", "--show-error",
            "--proto", "=https", "--proto-redir", "=https", "--max-redirs", "3",
            "--connect-timeout", "20", "--max-time", "120", "--retry", "2",
            "--output", str(archive_path), lock["url"]], check=True, timeout=400)
    with archive_path.open("rb") as source:
        if hashlib.file_digest(source, "sha256").hexdigest() != lock["sha256"]:
            raise SystemExit("Downloaded artifact checksum mismatch.")
    stage = work / "install"
    with tarfile.open(archive_path) as archive:
        # No extractall: neither symlinks nor any other archive members are installed.
        members = [(lock["library_member"], "lib/libpdfium.so")]
        members += [(name, "licenses/pdfium/" + name) for name in lock["license_members"]]
        for name, relative in members:
            member = archive.getmember(name)
            if not member.isfile() or Path(relative).is_absolute() or ".." in Path(relative).parts:
                raise SystemExit("Invalid PDFium archive member.")
            target = stage / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(member) as source, target.open("xb") as output:
                shutil.copyfileobj(source, output)
    shutil.copyfile(repo / "third_party/pdfium.lock", stage / "licenses/pdfium/pdfium.lock")
    if prefix.exists():
        prefix.rmdir()
    stage.rename(prefix)
    for path in prefix.rglob("*"):
        path.chmod(0o555 if path.is_dir() else 0o444)
    prefix.chmod(0o555)
print("Locked PDFium and licenses installed.")
PY
