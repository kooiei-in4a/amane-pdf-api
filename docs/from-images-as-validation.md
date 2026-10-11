# PNG見積りと同時処理の検証（#59、案A）

## 採用方針と利用者への影響

2026-10-11、Kooの案A判断により、取込・overlay共通のAS（仮想アドレス空間）1 GiBを維持する。Adam7ではPNG見積りへ展開画像1枚分を追加し、上限320 MiB超過をimport前に422 `unsupported-image`で拒否する。取込専用AS・設定の分離・IsValid・起動経路の変更は行わない。

正規化PNG bytesをB、画素数をPとする。非interlaceの式は[型別の検証記録](from-images-followup-validation.md)を参照する。Adam7はその式へ`P × 展開サンプルbytes`を加える。パレット/透過なしグレーは1チャネル、その他は4チャネル、16 bitは1サンプル2 bytes、8 bit以下は1 byteとして見積もる。

- 必須成功対象は**非interlace**の12 MP RGBA8（4000×3000/3000×4000、低・高複雑度、両quality）。
- **Adam7**の12 MP RGBA8は低・高複雑度とも見積り超過で、変換を始める前に422となる。PNGのquality変更だけでは解消しない。
- Adam7の受付上限は、8 bitカラー/透過付きグレー約10.3 MP、16 bitカラー/透過付きグレーは低複雑度でも約4.8 MP、パレット/透過なしグレー8は低複雑度で約20.6 MP。実encoded bytesにより狭まり、任意入力の成功保証ではない。

ASは物理メモリ上限と異なる。Go soft limit 200MiBは超過しても強制停止する上限ではない。観測VmSizeとASの差（非interlaceの旧測定では約30 MiB）は**使用できるAS余裕ではない**。固定binary・固定ASの反復成功を根拠とし、pdfcpu/Go/runtime更新時は同じ受付境界で反復検証し直す。

## 案Aで完了した検証

[結果要約](from-images-adam7-estimate-results.json)にimage ID・driver/generator hash・入力寸法/bytes/hash・見積り・試行数・cgroup peakを保存した。製品コードは`3157adafbb22914bd9de1ebf18f1ddcf127bd6f7`、HTTP image IDは`sha256:ba797f1a79dd232eb9360f34510b221150d179e6f2111c5d666dab3e39b7f79e`。

- 新境界のAdam7全26型×低/高複雑度52入力と、非interlace 12 MP RGBA8の4入力を、AS 1024 MiBで各30回、計1680回直接取り込み、失敗0・OOMなしだった。直接試験は前回と同じ固定pdfcpu imageを使い、HTTPでは見積り変更済みimageを使った。境界の52入力は、1行高い隣接入力が見積りを超えることも確認した。
- HTTPは8入力×両quality×単独/同時2件の32条件・48要求。200が36件、422が12件。422はすべてpdfcpu起動0回、PDF構造・配置・透過・cleanup・health/rotate正常、cgroup最大530.9 MiBだった。この試験は各同時条件30回の反復・compress混在ではない。
- C#/Python共用159ベクタで標準320 MiB、パレット/透過なしグレー8の係数12、透過グレーの22P下限、Adam7の隣接境界と必須12 MPを固定する。小さなsanitizerテストはinterlaceだけを変えた入力が見積りを超えることを確かめる。大画像のデコードは手動測定と区別する。
- 案AのRelease buildは警告/エラー0、920テスト成功・失敗/skip 0。head `95b8cea`の[CI](https://github.com/kooiei-in4a/amane-pdf-api/actions/runs/38121405501)でもRelease build/test・Docker build/全smoke成功。追加測定で発見した監視の回帰を3件加え、Python回帰は8件成功した。

差分レビューでは、IHDRのinterlaceを0/1へ制限したうえで見積りへ渡すこと、tRNSの有無を含む追加項、`checked`による計算、全正規化検査の後・`ImportImageAsync`の前の拒否を確認した。[固定Go 1.27.1のPNGデコーダ](https://github.com/golang/go/blob/go1.27.1/src/image/png/reader.go)はAdam7の全寸法画像へpass画像を統合し、パレット/透過なしグレーとカラー/透過グレーで展開型が異なる。追加項の型分岐はこれに対応している。受付見積りの係数は実測に基づき、RSS/ASの上界を証明する式ではない。時間・入力/出力容量・同時2枠・kill/wait・cleanup・argvの経路はこの変更で変わらない。

## 同時処理の追加反復計画（測定前に記録）

2026-10-11、Kooの「進めてください」により、案AのASを維持した追加検証を行う。事前基準は次のとおり。製品imageと下記3入力のSHA-256を固定し、既存の約47.7 MiB高複雑度JPEG入りPDFをcompress入力に使う。

| 組み合わせ | 条件数 | 各条件の反復 | 予定要求数 |
| --- | ---: | ---: | ---: |
| Adam7高複雑度RGBA8境界直下 ×2（両quality） | 2 | 30組 | 120 |
| 非interlace高複雑度12 MP RGBA8縦横それぞれ ×2（両quality） | 4 | 30組 | 240 |
| Adam7高複雑度RGBA8境界直下 + compress（両quality×両level） | 4 | 30組 | 240 |

合計10条件・300組・600要求をすべて200とし、失敗0を要求する。API起動は条件ごとに新しいコンテナ。CPU 1、物理メモリ1536 MiB・追加swapなし、tmpfs 256 MiB、non-root/read-only/network none、cap-drop ALL/no-new-privileges、共通同時2件を維持する。pdfcpu AS 1024 MiB、GOMEMLIMIT=200MiB、GOGC=100、GOMAXPROCS=1、CORE=0、出力FSIZE=54 MiB+1は製品と同じにする。

HTTP/PDF構造・A4/余白・寸法/透過・成功ヘッダーが正常、各組のcleanup、各条件後のhealth/rotate 200、両jobの重なり、画像要求数とpdfcpu exec数の一致、compressのJPEG各1件再圧縮を確認する。OOM/oom_kill・残存job/tool・診断情報露出は0、cgroupのkernel peakは1536 MiBの90%未満、tmpfs 256 MiB未満、単一job 124 MiB以下、出力54 MiB以下を要求する。HTTP時間はアップロード・受信を含み、内部30秒期限との差を保証された余裕としない。

事前基準はcommit `051e972580a9f644aa63aad4814b504ea6a0c0c1`に記録した。tmpfs/job/toolは20 msの待機を挟む観測値、cgroup peakはkernel値である。新しいASを採用する測定ではないため、旧AS候補の「候補−64 MiB」条件を今回の合格条件にはしない。旧条件を満たしたと報告したり、固定ASの成功だけで必要最小ASやAS余裕を確定したりしない。

## 追加同時処理の結果

[要約・環境・固定入力](from-images-adam7-concurrency-results.json)と[300組の試行JSONL](from-images-adam7-concurrency-trials.jsonl)を保存した。全10条件で各30組連続成功、計600要求が200、失敗0だった。PDF構造・A4/余白・寸法/透過・成功ヘッダー・各組のcleanup・後続health/rotate正常、重なる2 jobを観測した。pdfcpu起動480回は画像要求480件と一致し、compressの120要求はそれぞれJPEG1件を再圧縮した。

| 組み合わせ | 条件×30組 | 要求数（全て200） | cgroup最大 MiB | tmpfs観測最大 MiB |
| --- | ---: | ---: | ---: | ---: |
| Adam7境界 ×2 | 2×30 | 120 | 577.676 | 157.582 |
| 非interlace 12 MP縦横 ×2 | 4×30 | 240 | 609.824 | 183.223 |
| Adam7境界 + compress | 4×30 | 240 | 526.961 | 187.867 |

全体のcgroup最大は609.824/1536 MiB、未使用926.176 MiB（60.30%）。物理メモリについて事前基準の10%以上を満たした。tmpfs観測最大187.867/256 MiB、単一job観測最大109.109/124 MiB、出力最大45.781/54 MiB。HTTP最大3.402秒はアップロード・受信を含む。OOM/oom_kill・監視エラー・残存job/toolは0だった。

初回の混在4条件では、ファイルを書かない`qpdf --check`のFSIZEが意図どおりunlimitedであることを監視が拒否し、空のAssertionErrorが空文字で保存されたため、driverが監視停止を見落とした。監視は例外型を含む非空文字を保存し、判定は`None`だけを正常とした。読み取り専用のqpdf検査を明示的に区別し、終了直後の空cmdlineはprocess消滅と同じ扱いにした。AS/COREと書出しtoolのFSIZEの確認は維持する。空エラー・読取検査/書出し・終了競合の3回帰を追加した。

不完全な初回4条件と原因確認用の中断2回は合格件数に含めず、修正版driver・新コンテナで混在全4条件を取り直した。除外理由は要約にも保存した。既存のAdam7同士/非interlace同士6条件は監視エラーが`None`で最後まで観測されていたため採用した。製品コードは変えていない。旧/new imageのpdfcpu SHA-256はともに`4ecc03cf86bab501ae1b8353ddc764177d85db015b616df1d539a3ae1f469f3d`で、同一binaryを使ったことを確認した。

## 再現手順

Pillow/NumPy/qpdf/Dockerが必要。hostのAppArmorがqpdfの拡張子なしraw出力を拒否する場合は、既存CIと同じqpdf配布バイナリの一時コピーをPATHに指定する。hostのAppArmor設定は変更しない。fixture/result dirは新しくする。

生成は`from-images-as-validation.py --generate-only`を使う。直接試験はmanifestの期待200の56入力だけを選び、旧AS探索用planを案Aの合格計画として使わない。新image ID・製品SHA・AS 1024 MiB・反復30・上記基準を含む案A用planを指定する。追加HTTP試験も同じplanを使う。保存結果からplanを取り出す例を示す。imageを再buildしてIDが変わる場合は新しい計画としてIDを更新し、その結果を今回の固定imageと混ぜない。

```bash
python3 - <<'PY'
import json
from pathlib import Path
result = json.loads(Path("docs/from-images-adam7-concurrency-results.json").read_text())
Path("/tmp/issue59-plan-a-plan.json").write_text(json.dumps(result["plan"], ensure_ascii=False, indent=2) + "\n")
PY
python3 scripts/from-images-as-validation.py --generate-only \
  --plan /tmp/issue59-plan-a-plan.json --fixtures /tmp/issue59-plan-a-fixtures
python3 scripts/from-images-followup.py --generate-only \
  --fixtures /tmp/issue59-review-final-fixtures
sudo python3 scripts/from-images-as-validation.py amane-pdf-api:pr61-adam7 \
  --plan /tmp/issue59-plan-a-plan.json --fixtures /tmp/issue59-plan-a-fixtures \
  --output /tmp/issue59-plan-a-pairs --address-spaces 1024 --http \
  --cases adam7-rgba8-high-boundary plain-rgba8-12mp-high-landscape plain-rgba8-12mp-high-portrait
sudo python3 scripts/from-images-as-validation.py amane-pdf-api:pr61-adam7 \
  --plan /tmp/issue59-plan-a-plan.json --fixtures /tmp/issue59-plan-a-fixtures \
  --output /tmp/issue59-plan-a-mixed --address-spaces 1024 --http \
  --cases adam7-rgba8-high-boundary --compress-fixture /tmp/issue59-review-final-fixtures/manifest.json
```

## 旧測定の履歴と配置前条件

[旧AS探索の文書（固定commit）](https://github.com/kooiei-in4a/amane-pdf-api/blob/ec5d4ab00e2e8a0d65e2b0871dc30c8864316b5d/docs/from-images-as-validation.md)、[集約データ](from-images-as-results.json)、[全試行](from-images-as-trials.jsonl)、計画JSON 2件は履歴として残す。旧モデル/別ASでの3600回取込・960要求を案Aの成功件数へ合算しない。

1136 MiB案は採らなかった。同じ固定Go binaryでは1152/1216 MiBでversionを含む初期化失敗があり、AS増加と成功率は単調ではなかった。1136 MiBで成功した事実は残すが、16 MiB先の失敗帯域からの距離も将来の安定性を保証しない。案AはASを維持して受付範囲を狭める選択である。

合成入力の成功を任意入力や別infra条件へ一般化しない。#46のcollector確認・infraのCORE=0/容量条件整合、Humanのmerge・配置承認は別途必要で、追加検証完了だけでは配置保留を解除しない。
