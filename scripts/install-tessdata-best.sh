#!/bin/sh
set -eu

target=${1:?Usage: install-tessdata-best.sh DIRECTORY}
test ! -e "$target"
mkdir -p "$(dirname -- "$target")"
work=$(mktemp -d "${target}.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
source=https://github.com/tesseract-ocr/tessdata_best/raw/e12c65a915945e4c28e237a9b52bc4a8f39a0cec

for name in jpn.traineddata jpn_vert.traineddata; do
    curl --fail --location --silent --show-error --proto '=https' --proto-redir '=https' \
        --max-redirs 3 --connect-timeout 20 --max-time 120 --retry 2 \
        --output "$work/$name" "$source/$name"
done

(
    cd "$work"
    sha256sum --check <<'SUMS'
36bdf9ac823f5911e624c30d0553e890b8abc7c31a65b3ef14da943658c40b79  jpn.traineddata
1258be6eb2a9851f18043234ad18cca13ed32690bfff62b335c898bbea371548  jpn_vert.traineddata
SUMS
)
chmod 0755 "$work"
chmod 0644 "$work"/*.traineddata
mv -T -- "$work" "$target"
