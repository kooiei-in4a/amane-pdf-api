#!/usr/bin/env python3
"""CI専用PopplerでDocker内の日本語描画の文字抽出とfont埋込みを検証する。"""
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

for name in ("pdftotext", "pdffonts"):
    if shutil.which(name) is None:
        raise SystemExit("Poppler is required for explicit pdfcpu rendering validation.")
spec = importlib.util.spec_from_file_location("smoke", Path(__file__).with_name("docker-smoke.py"))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
with smoke.running_container(sys.argv[1], isolated=True) as api:
    pdf = smoke.pdfcpu_smoke(api)
with tempfile.TemporaryDirectory(prefix="amane-pdfcpu-render-") as temporary:
    path = Path(temporary) / "layer.pdf"
    path.write_bytes(pdf)
    text = subprocess.run(["pdftotext", str(path), "-"], stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, check=True, timeout=30).stdout.decode()
    assert "日本語の起動確認" in text
    fonts = subprocess.run(["pdffonts", str(path)], stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, check=True, timeout=30).stdout.decode().splitlines()
    assert any("+BIZUDPGothic-Regular" in row and "yes yes yes" in row for row in fonts)
print("Japanese extraction and embedded/subset/Unicode font: PASS (text position pixel comparison remains PR B)")
