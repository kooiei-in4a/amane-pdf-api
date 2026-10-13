# Runs inside amane-pdf-api image: /in = work dir (ro), /out = writable results dir.
set -u
cd /tmp && mkdir -p j/home
P="/usr/bin/env HOME=/tmp/j/home XDG_CONFIG_HOME=/tmp/j/home GOMEMLIMIT=200MiB GOGC=100 GOMAXPROCS=1 GOTRACEBACK=none prlimit --core=0:0 --as=1073741824:1073741824 --fsize=8388609:8388609 -- /opt/amane-pdf/bin/pdfcpu -c /opt/amane-pdf/config --offline"
ms() { date +%s%3N; }
while IFS="$(printf '\t')" read -r id geo img desc; do
  cd /tmp/j; rm -f blank.pdf target.pdf stamp.png layer-*.pdf out-*.pdf
  qpdf --json-input /in/json/blank-$geo.json blank.pdf
  qpdf --json-input /in/json/target-$geo.json target.pdf
  cp /in/png/$img.png stamp.png
  for m in stamp create; do
    t0=$(ms)
    if [ $m = stamp ]; then $P stamp add -m image -- /tmp/j/stamp.png "$desc" /tmp/j/blank.pdf /tmp/j/layer-$m.pdf >/dev/null 2>/tmp/j/err; rc=$?
    else $P create /in/json/create-$id.json /tmp/j/blank.pdf /tmp/j/layer-$m.pdf >/dev/null 2>/tmp/j/err; rc=$?; fi
    t1=$(ms); size=$(stat -c %s layer-$m.pdf 2>/dev/null || echo -1)
    chk=-; ov=-
    if [ $rc = 0 ]; then
      qpdf --check layer-$m.pdf >/dev/null 2>&1; chk=$?
      qpdf target.pdf --overlay layer-$m.pdf --to=1 --from=1 -- out-$m.pdf; ov=$?
      qpdf --check out-$m.pdf >/dev/null 2>&1; ov="$ov/$?"
      case $img in big*|adv*) ;; *) cp out-$m.pdf /out/$id-$m.pdf;; esac
    fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$id" $m $rc $((t1-t0)) $size $chk "$ov" "$(head -c 160 /tmp/j/err | tr '\n\t' '  ')" >> /out/results.tsv
  done
done < /in/plan.tsv
