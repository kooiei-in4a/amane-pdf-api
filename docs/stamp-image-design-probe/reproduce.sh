#!/bin/sh
# Issue #29 design probe (2026-10-13). Not part of CI; see docs/stamp-image-design.md.
# Usage: sh docs/stamp-image-design-probe/reproduce.sh IMAGE WORK_DIR
#   IMAGE    = image built from main 74ff1ea (pdfcpu 0.16.1, qpdf 12.3.2), e.g. amane-pdf-api:issue28
#   WORK_DIR = new empty directory for fixtures and results
# Host needs python3 + numpy + Pillow, qpdf, pdftoppm (Poppler).
set -eu
here=$(cd "$(dirname "$0")" && pwd)
image=$1; work=$(mkdir -p "$2" && cd "$2" && pwd)
python3 -I "$here/make_png.py" "$work/png" >/dev/null
python3 -I "$here/make_cases.py" "$work"
cp "$here/docker-matrix.sh" "$work/run.sh"
mkdir -p "$work/out" "$work/host" && chmod 755 "$work" && chmod 777 "$work/out"
docker run --rm --network none --read-only --cpus 1 --memory 1536m --memory-swap 1536m \
  --tmpfs /tmp:size=256m --cap-drop ALL --security-opt no-new-privileges \
  -v "$work:/in:ro" -v "$work/out:/out" --entrypoint sh "$image" /in/run.sh
# Host qpdf may refuse files owned by the container uid: copy them first.
cp "$work"/out/*.pdf "$work/host/"
column -t -s "$(printf '\t')" "$work/out/results.tsv"
python3 -I "$here/analyze.py" "$work"
