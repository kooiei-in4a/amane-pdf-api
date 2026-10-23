#!/bin/sh
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec python3 - "$script_dir/.." "$@" <<'PY'
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile

repo = Path(sys.argv[1]).resolve()
parser = argparse.ArgumentParser(description="Install locked pdfcpu and Japanese font without runtime downloads.")
parser.add_argument("--download-cache", type=Path, help="Read the locked Go SDK/font assets here; module downloads still use the Go proxy and checksum database.")
parser.add_argument("prefix", type=Path)
args = parser.parse_args(sys.argv[2:])
lock = json.loads((repo / "third_party/pdfcpu.lock").read_text())
if sys.platform != "linux" or platform.machine() not in ("x86_64", "amd64") or lock["platform"] != "linux-amd64":
    raise SystemExit("pdfcpu requires Linux x86_64.")
prefix = args.prefix.resolve()
if prefix.exists() and (not prefix.is_dir() or any(prefix.iterdir())):
    raise SystemExit("Install prefix must be absent or empty.")
prefix.parent.mkdir(parents=True, exist_ok=True)

def sha(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()

for line in (repo / "third_party/pdfcpu-licenses.sha256").read_text().splitlines():
    expected, relative = line.split("  ", 1)
    if sha(repo / "third_party" / relative) != expected:
        raise SystemExit("Bundled license checksum mismatch.")

with tempfile.TemporaryDirectory(prefix=".pdfcpu-install-", dir=prefix.parent) as temporary:
    work = Path(temporary)
    stage = work / "install"
    (stage / "bin").mkdir(parents=True)
    (stage / "config").mkdir()
    downloads = []
    for artifact in lock["artifacts"]:
        target = work / artifact["name"]
        if args.download_cache:
            shutil.copyfile(args.download_cache / artifact["name"], target)
        else:
            subprocess.run(["curl", "--fail", "--location", "--silent", "--show-error",
                "--proto", "=https", "--proto-redir", "=https", "--max-redirs", "3",
                "--connect-timeout", "20", "--max-time", "120", "--retry", "2",
                "--output", str(target), artifact["url"]], check=True, timeout=400)
        if sha(target) != artifact["sha256"]:
            raise SystemExit("Downloaded artifact checksum mismatch.")
        downloads.append(target)
    # Build the official module with a patched, checksum-pinned Go toolchain.
    # Upstream v0.16.3 binaries still embed Go 1.27.2 (CVE-2026-111943).
    toolchain = work / "toolchain"
    with tarfile.open(downloads[0]) as archive:
        archive.extractall(toolchain, filter="data")
    go = toolchain / "go/bin/go"
    build_env = dict(os.environ, GOROOT=str(toolchain / "go"), GOPATH=str(work / "gopath"),
        GOMODCACHE=str(work / "modules"), GOCACHE=str(work / "build-cache"), GOBIN=str(stage / "bin"),
        GO111MODULE="on", GOENV="off", GOTOOLCHAIN="local", GOWORK="off", GOFLAGS="", GOEXPERIMENT="",
        GOPROXY="https://proxy.golang.org", GOSUMDB="sum.golang.org", GOPRIVATE="", GONOPROXY="", GONOSUMDB="",
        CGO_ENABLED="0", GOOS="linux", GOARCH="amd64", GOAMD64="v1")
    def go_run(arguments, capture=False):
        return subprocess.run([str(go), *arguments], env=build_env, cwd=work, check=True,
            stdout=subprocess.PIPE if capture else None, timeout=600)
    if go_run(["env", "GOVERSION"], True).stdout.decode().strip() != lock["go_version"]:
        raise SystemExit("Go toolchain version mismatch.")
    source = lock["source"]
    module = source["module"] + "@" + source["version"]
    downloaded = json.loads(go_run(["mod", "download", "-json", module], True).stdout)
    if downloaded["Sum"] != source["sum"] or downloaded["GoModSum"] != source["go_mod_sum"]:
        raise SystemExit("pdfcpu source checksum mismatch.")
    if Path(downloaded["GoMod"]).read_bytes() != (repo / "third_party/pdfcpu/go.mod").read_bytes():
        raise SystemExit("pdfcpu go.mod mismatch.")
    flags = "-s -w -X main.version=" + lock["version"] + " -X github.com/pdfcpu/pdfcpu/pkg/pdfcpu/model.VersionStr=" + lock["version"]
    flags += " -X main.commit=" + source["commit"] + " -X main.date=" + source["date"]
    go_run(["install", "-trimpath", "-ldflags=" + flags, source["module"] + "/cmd/pdfcpu@" + source["version"]])
    binary = stage / "bin/pdfcpu"
    # Verify the resulting binary's runtime/module inventory and bundled licenses before execution.
    subprocess.run([sys.executable, str(repo / "scripts/verify-pdfcpu-modules.py"), str(binary)],
        env=build_env | {"PATH": str(go.parent) + os.pathsep + os.environ.get("PATH", "")}, check=True, timeout=60)
    font = work / "BIZUDPGothic-Regular.ttf"
    with zipfile.ZipFile(downloads[1]) as archive:
        with archive.open(lock["artifacts"][1]["member"]) as source, font.open("wb") as target:
            shutil.copyfileobj(source, target)
    if sha(font) != lock["font_sha256"] or sha(downloads[2]) != sha(repo / "third_party/pdfcpu/BIZ-UDGothic-OFL.txt"):
        raise SystemExit("Font or OFL checksum mismatch.")
    (stage / "fonts").mkdir()
    shutil.copyfile(font, stage / "fonts/BIZUDPGothic-Regular.ttf")
    home = work / "home"
    home.mkdir(mode=0o700)
    environment = dict(os.environ, HOME=str(home), XDG_CONFIG_HOME=str(home), GOMEMLIMIT="200MiB",
        GOGC="100", GODEBUG="", GOMAXPROCS="1", GOTRACEBACK="none")
    def run(arguments, capture=False):
        return subprocess.run([str(binary), "-c", str(stage / "config"), "--offline", *arguments],
            env=environment, cwd=work, stdout=subprocess.PIPE if capture else subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, check=True, timeout=60)
    if "version: " + lock["version"] not in run(["version"], True).stdout.decode().splitlines():
        raise SystemExit("pdfcpu version mismatch.")
    run(["fonts", "install", str(font)])
    config = stage / "config/pdfcpu/config.yml"
    text, count = re.subn(r"(?m)^offline: (?:true|false)$", "offline: true", config.read_text())
    if count != 1:
        raise SystemExit("pdfcpu offline configuration is missing.")
    config.write_text(text)
    if "BIZUDPGothic-Regular (" not in run(["fonts", "list"], True).stdout.decode():
        raise SystemExit("Japanese font is missing.")
    shutil.copytree(repo / "third_party/pdfcpu", stage / "licenses")
    for name in ("pdfcpu-modules.json", "pdfcpu-licenses.sha256", "pdfcpu.lock"):
        shutil.copyfile(repo / "third_party" / name, stage / "licenses" / name)
    shutil.copyfile(repo / "THIRD_PARTY_NOTICES.md", stage / "licenses/THIRD_PARTY_NOTICES.md")
    if prefix.exists():
        prefix.rmdir()
    stage.rename(prefix)
    for path in prefix.rglob("*"):
        path.chmod(0o555 if path.is_dir() else 0o444)
    (prefix / "bin/pdfcpu").chmod(0o555)
    prefix.chmod(0o555)
print("Locked pdfcpu and Japanese font installed.")
PY
