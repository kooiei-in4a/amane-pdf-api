#!/usr/bin/env bash
set -euo pipefail

prefix="$(realpath "${1:?usage: install-compose-mincho.sh PDFCPU_PREFIX}")"
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
font="$work/BIZUDPMincho-Regular.ttf"
curl --fail --location --silent --show-error --proto '=https' --proto-redir '=https' \
    --connect-timeout 20 --max-time 120 --retry 2 \
    https://github.com/googlefonts/morisawa-biz-ud-mincho/releases/download/v1.06/BIZUDPMincho-Regular.ttf --output "$font"
echo "dbcea04578ac1e9d3484525e870ce491bd04361768f4d2ba4b827d96e20f891d  $font" | sha256sum --check
echo "ef0ed524b3bd012f37015e986809eed6f67905456ebfc1ebd7fcc2765282ee01  $root/third_party/pdfcpu/BIZ-UDMincho-OFL.txt" | sha256sum --check
font_dir="$prefix/config/pdfcpu/fonts"
chmod u+w "$font_dir" "$prefix/fonts" "$prefix/licenses"
trap 'chmod a-w "$font_dir" "$prefix/fonts" "$prefix/licenses"; rm -rf -- "$work"' EXIT
install -m 0444 "$font" "$prefix/fonts/BIZUDPMincho-Regular.ttf"
install -m 0444 "$root/third_party/pdfcpu/BIZ-UDMincho-OFL.txt" "$prefix/licenses/BIZ-UDMincho-OFL.txt"
mkdir -m 0700 "$work/home"
HOME="$work/home" XDG_CONFIG_HOME="$work/home" "$prefix/bin/pdfcpu" -c "$prefix/config" --offline fonts install "$font"
test -s "$font_dir/BIZUDPMincho-Regular.gob"
chmod 0444 "$font_dir/BIZUDPMincho-Regular.gob"
