#!/usr/bin/env bash
# 黒塗り（#35）設計の実測を再現する。設計調査用の試作で、実装ではない。
#
# usage: reproduce.sh [image] [workdir]
#   image   : qpdf 12.3.2・pdfcpu 0.16.1・libturbojpeg・.NET 10 runtime を含むAPI image
#             （既定 amane-pdf-api:issue72-5d8b298。main 43cb797 とは docs/roadmap.md だけが異なる）
#   workdir : 生成物の置き場（既定 ./redact-probe-work。数百MBを使う）
# 必要なもの: docker、host の dotnet SDK 10、python3、curl。
set -euo pipefail
IMAGE=${1:-amane-pdf-api:issue72-5d8b298}
WORK=$(mkdir -p "${2:-./redact-probe-work}" && cd "${2:-./redact-probe-work}" && pwd)
HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$WORK/out" "$WORK/fonts" "$WORK/work" "$WORK/dl"
W=$WORK/work

echo "## build spike renderer (PDFium chromium/8100 via NuGet, libturbojpeg from the image)"
dotnet publish "$HERE/renderer/Renderer.csproj" -c Release -o "$WORK/out" >/dev/null

echo "## BIZ UDPGothic (same artifact and checksums as third_party/pdfcpu.lock)"
if [ ! -f "$WORK/fonts/BIZUDPGothic-Regular.ttf" ]; then
  curl -sSL -o "$WORK/dl/BIZUDGothic.zip" https://github.com/googlefonts/morisawa-biz-ud-gothic/releases/download/v1.051/BIZUDGothic.zip
  echo "30692df621b92df13b88f1360aed1ab6ae50de441bce751a396c6439045cd759  $WORK/dl/BIZUDGothic.zip" | sha256sum -c -
  python3 -I -c 'import zipfile,sys; open(sys.argv[2],"wb").write(zipfile.ZipFile(sys.argv[1]).read("BIZUDPGothic-Regular.ttf"))' \
    "$WORK/dl/BIZUDGothic.zip" "$WORK/fonts/BIZUDPGothic-Regular.ttf"
  echo "595dfefa96dcb281c40f5d560841f3fc623b0d9d12064481a7daf78044462673  $WORK/fonts/BIZUDPGothic-Regular.ttf" | sha256sum -c -
fi

echo "## generate inputs"
python3 -I "$HERE/gen_geometry.py" "$W/geometry.json"
python3 -I "$HERE/gen_markers.py" "$W"
python3 -I "$HERE/gen_perf.py" "$W"
python3 -I "$HERE/gen_misc.py" "$W"

# Every container step runs a script file (no inline `sh -c`), as the host user, without network.
box() {  # box <script> [extra docker args...]
  local script=$1; shift
  docker run --rm --network none --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp \
    -v "$WORK":/w "$@" --entrypoint sh "$IMAGE" "/w/work/$script"
}
R="dotnet /w/out/Renderer.dll"

cat > "$W/c1.sh" <<EOF
set -e
cd /w/work
rm -f *.pdf
for j in geometry markers vector shared-res bomb needap blank2; do qpdf --json-input \$j.json \$j.pdf; done
cjpeg -quality 90 -outfile scan.jpg scan.ppm
cjpeg -quality 85 -outfile scan600.jpg scan600.ppm
cjpeg -quality 85 -progressive -outfile scan600p.jpg scan600.ppm
mkdir -p /tmp/h
HOME=/tmp/h /opt/amane-pdf/bin/pdfcpu -c /opt/amane-pdf/config --offline create shared-layer.json blank2.pdf shared.pdf >/dev/null
qpdf --json=2 --json-key=qpdf --json-stream-data=none markers.pdf > markers-in.json
echo "### PDFium page size (points, as PDFium reports it)"
$R info geometry.pdf
EOF
box c1.sh
for f in scan:595.276:841.89 scan600:595.276:841.89 scan600p:595.276:841.89; do
  IFS=: read -r name wpt hpt <<<"$f"
  python3 -I "$HERE/mkpage.py" "$W/$name.jpg" "$W/$name-page.json" "$wpt" "$hpt" "/w/work/$name.jpg" >/dev/null
done
python3 -I "$HERE/updates.py" strip "$W/markers-in.json" "$W/markers-strip.json"
cat > "$W/markers-flat-job.json" <<'EOF'
{"inputFile":"markers.pdf","outputFile":"markers-flat.pdf","updateFromJson":"markers-strip.json",
 "flattenAnnotations":"all","generateAppearances":"","removeAcroform":"","removeStructure":""}
EOF

cat > "$W/c2.sh" <<EOF
cd /w/work
for j in scan-page scan600-page scan600p-page; do qpdf --json-input \$j.json \$j.pdf; done
echo "### rotate/crop/UserUnit renders with region (0.4,0.3,0.2,0.1)"
for spec in "0 1000 1200" "1 1200 1000" "2 1000 1200" "3 1200 1000" "4 1600 1200" "5 600 800"; do
  set -- \$spec
  $R render geometry.pdf \$1 \$2 \$3 "0.4,0.3,0.2,0.1" g\$1.jpg - >/dev/null && djpeg -pnm -outfile g\$1.ppm g\$1.jpg
done
echo "### non-embedded fonts"
$R render geometry.pdf 6 1190 1684 - jp-nofont.jpg - >/dev/null
$R render geometry.pdf 6 1190 1684 - jp-font.jpg /w/fonts >/dev/null
$R render geometry.pdf 7 1190 1684 - helv.jpg - >/dev/null
for f in jp-nofont jp-font helv; do djpeg -pnm -grayscale -outfile \$f.pgm \$f.jpg; done
echo "### unreferenced-resource removal (exit code; SECRETFORM / F1 marker counts in output)"
for pages in 2 3 4; do
  rm -f sr-out.pdf sr-outq.pdf
  qpdf --empty --remove-unreferenced-resources=yes --pages shared-res.pdf \$pages -- sr-out.pdf 2>/dev/null; rc=\$?
  qpdf --qdf --object-streams=disable sr-out.pdf sr-outq.pdf 2>/dev/null
  echo "keep page \$pages: exit=\$rc SECRETFORM=\$(grep -c SECRETFORM sr-outq.pdf) MarkerF1=\$(grep -c MarkerF1secret sr-outq.pdf)"
done
echo "### form value without appearance (NeedAppearances true)"
for g in 0 1; do rm -f needap-flat-\$g.pdf; qpdf --job-json-file=needap-job-\$g.json 2>/dev/null; echo "generateAppearances=\$g exit=\$?"
  $R render needap-flat-\$g.pdf 0 595 842 - needap-\$g.jpg /w/fonts >/dev/null; djpeg -pnm -grayscale -outfile needap-\$g.pgm needap-\$g.jpg; done
echo "### shared-font document: prune, inspect, render page 1"
qpdf --empty --remove-unreferenced-resources=yes --pages shared.pdf 1-z -- shared-pruned.pdf
qpdf --json=2 --json-key=pages --json-key=qpdf --json-stream-data=none shared-pruned.pdf > shared-pruned.json
$R render shared-pruned.pdf 0 1654 2339 "0.05,0.1,0.6,0.05" shared-p1.jpg /w/fonts >/dev/null
echo "### marker document: strip+flatten (one job), prune, inspect, render page 1"
qpdf --job-json-file=markers-flat-job.json; echo "flatten job exit=\$?"
qpdf --empty --remove-unreferenced-resources=yes --pages markers-flat.pdf 1-z -- markers-pruned.pdf; echo "prune exit=\$?"
qpdf --json=2 --json-key=pages --json-key=qpdf --json-stream-data=none markers-pruned.pdf > markers-pruned.json
qpdf --qdf --object-streams=disable markers-pruned.pdf markers-pruned-q.pdf
$R render markers-pruned.pdf 0 1654 2339 "0.08,0.08,0.4,0.05" markers-p1.jpg /w/fonts >/dev/null
EOF
box c2.sh
python3 -I "$HERE/check_geom.py" "$W"
echo "dark pixels: no CJK font=$(python3 -I "$HERE/checks.py" dark "$W/jp-nofont.pgm") BIZ UDPGothic=$(python3 -I "$HERE/checks.py" dark "$W/jp-font.pgm") Helvetica=$(python3 -I "$HERE/checks.py" dark "$W/helv.pgm")"
for g in 0 1; do echo "needap generateAppearances=$g: dark px in field=$(python3 -I "$HERE/checks.py" dark "$W/needap-$g.pgm" 50 102 400 142)"; done
echo "font sharing (shared-font doc, target 1):"; python3 -I "$HERE/fontshare.py" "$W/shared-pruned.json" 1
echo "font sharing (marker doc, target 1):"; python3 -I "$HERE/fontshare.py" "$W/markers-pruned.json" 1
echo "marker doc after prune:"; python3 -I "$HERE/checks.py" markers "$W/markers-pruned-q.pdf"
python3 -I "$HERE/updates.py" final "$W/shared-pruned.json" "$W/shared-final.json" "1=/w/work/shared-p1.jpg:$W/shared-p1.jpg"
python3 -I "$HERE/updates.py" final "$W/markers-pruned.json" "$W/markers-final.json" "1=/w/work/markers-p1.jpg:$W/markers-p1.jpg"

cat > "$W/c3.sh" <<'EOF'
cd /w/work
for d in shared markers; do
  rm -f $d-out.pdf $d-out-q.pdf
  qpdf $d-pruned.pdf --update-from-json=$d-final.json $d-out.pdf; echo "$d final write exit=$?"
  qpdf --check $d-out.pdf | tail -1
  qpdf --qdf --object-streams=disable $d-out.pdf $d-out-q.pdf
done
EOF
box c3.sh
echo "shared-font doc: page-1-only characters in output ToUnicode (page 1 replaced, font shared with page 2):"
python3 -I "$HERE/checks.py" tounicode "$W/shared-out-q.pdf" "鈴木一郎個人番号9876"
echo "marker doc after final write:"; python3 -I "$HERE/checks.py" markers "$W/markers-out-q.pdf"

echo "## time and memory (1 CPU, no swap; memory.peak of the whole container)"
cat > "$W/m.sh" <<EOF
cd /w/work
s=\$(date +%s%N); out=\$($R render "\$@" 2>&1 | tail -1); e=\$(date +%s%N)
echo "\$1 page \$2 \${3}x\$4: wall=\$(( (e - s) / 1000000 ))ms peak=\$(( \$(cat /sys/fs/cgroup/memory.peak) / 1048576 ))MiB :: \$out"
EOF
lim=(--cpus 1 --memory 512m --memory-swap 512m)
for args in "geometry.pdf 7 1654 2339" "scan-page.pdf 0 1654 2339" "vector.pdf 0 1654 2339" "vector.pdf 1 3308 4678" "scan-page.pdf 0 4000 4000"; do
  set -- $args
  docker run --rm --network none "${lim[@]}" --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp \
    -v "$WORK":/w --entrypoint sh "$IMAGE" /w/work/m.sh "$1" "$2" "$3" "$4" - o.jpg /w/fonts
done

echo "## process limits: RLIMIT_AS vs RLIMIT_DATA (16 Mpx render of the 300 dpi scan page; full image => jpeg=3746065)"
cat > "$W/limits.sh" <<EOF
cd /w/work
for as in 1073741824 2147483648 4294967296; do
  echo "AS \$((as/1048576))MiB: \$(/usr/bin/prlimit --core=0:0 --as=\$as:\$as -- $R render scan-page.pdf 0 4000 4000 - /tmp/o.jpg /w/fonts 2>&1 | tail -1 | cut -c1-90)"
done
echo "AS 1024MiB + GCHeapHardLimit 192MiB + GCRegionRange 256MiB: \$(DOTNET_GCHeapHardLimit=0xC000000 DOTNET_GCRegionRange=0x10000000 /usr/bin/prlimit --as=1073741824:1073741824 -- $R render scan-page.pdf 0 4000 4000 - /tmp/o.jpg /w/fonts 2>&1 | tail -1 | cut -c1-90)"
for data in 134217728 201326592 268435456 402653184; do
  echo "DATA \$((data/1048576))MiB 16Mpx scan: \$(/usr/bin/prlimit --core=0:0 --data=\$data:\$data -- $R render scan-page.pdf 0 4000 4000 - /tmp/o.jpg /w/fonts 2>&1 | tail -1 | cut -c1-90)"
  echo "DATA \$((data/1048576))MiB A2 vector: \$(/usr/bin/prlimit --core=0:0 --data=\$data:\$data -- $R render vector.pdf 1 3308 4678 - /tmp/o.jpg /w/fonts 2>&1 | tail -1 | cut -c1-90)"
done
echo "### 600 dpi A4 scans rendered at 200 dpi (full image => jpeg=1083960)"
for f in scan600-page scan600p-page; do for data in 201326592 268435456 402653184; do
  echo "\$f DATA \$((data/1048576))MiB: \$(/usr/bin/prlimit --data=\$data:\$data -- $R render \$f.pdf 0 1654 2339 - /tmp/o.jpg /w/fonts 2>&1 | tail -1 | cut -c1-90)"
done; done
echo "### Flate bombs (20000^2, 40000^2 gray) at DATA 384MiB"
for p in 0 1; do echo "bomb page \$p: \$(/usr/bin/prlimit --data=402653184:402653184 -- $R render bomb.pdf \$p 1654 2339 - bomb\$p.jpg /w/fonts 2>&1 | tail -1)"; done
djpeg -pnm -grayscale -outfile bomb0.pgm bomb0.jpg
echo "peak=\$(( \$(cat /sys/fs/cgroup/memory.peak) / 1048576 ))MiB"
EOF
box limits.sh --cpus 1 --memory 1536m --memory-swap 1536m
echo "bomb page 0 centre gray value (128 = image drawn): $(python3 -I "$HERE/checks.py" center "$W/bomb0.pgm")"
