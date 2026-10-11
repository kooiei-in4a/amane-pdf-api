# PNG取込のAS別反復検証（#59、2026-10-11 UTC）

## 採用した方針（案A、2026-10-11 Koo判断）

取込専用ASは採用せず、取込・overlay共通のAS 1 GiBを維持する。Adam7はpassごとの画像を持つため、PNG見積りに展開画像1枚分（画素数×展開サンプルbytes。パレット・透過なしグレーは1チャネル、その他は4チャネル）を加え、超過はimport前に422とする。設定の分離・`IsValid`・起動経路の変更は不要になった。

1136 MiB案を採らない理由は、Go初期化が全失敗する境界の16 MiB手前だったためである。同じimageでASを変えて`version`と12 MP非interlaceのimportを各3回試すと、1148 MiBまでは成功し、1152/1184/1216 MiBは`version`も失敗、1280/1344 MiBは`version`だけ成功、1536 MiB以上は両方成功した。境界の位置は固定Go runtimeの予約配置に依存し、pdfcpu・Go・kernelの更新で動き得る。下の候補表はこの判断の根拠として残す。

### 案Aの結果（AS 1024 MiB）

[結果](from-images-adam7-estimate-results.json)に入力のSHA-256・寸法・見積り・試行数・cgroup peakを保存した。

- 新しい見積りの境界直下にあるAdam7全26型×低/高複雑度の52入力と、非interlace 12 MP RGBA8の4入力を、製品と同じAS 1024 MiBで各30回、計1680回取り込んだ。失敗0、OOM/oom_kill 0。各入力は1行高い隣接入力が見積りを超えることも確認した。
- Adam7の受付は、8 bitのカラー・透過付きグレーが約10.3 MP、16 bitのカラー・透過付きグレーが低複雑度約4.8 MP、パレット・透過なしグレー8が約20.6 MP。12 MPのAdam7 RGBA8（低・高複雑度の縦横4入力）は見積り超過となる。
- 見積り変更後のimage（`3157ada`）でHTTPを確認した。8入力×両quality×単独/同時2件の32条件・48要求で200が36件、422 `unsupported-image`が12件。422はすべてpdfcpu起動0回、cgroup peak最大530.9 MiB、PDF検査・後片付け・health/rotateは正常だった。
- 「候補−64 MiB」の基準は案Aに適用しない。ASと成功率が単調でないため下側の余裕は意味のある指標にならず、非interlace 12 MP RGBA8は960 MiBで失敗する既存の状態でもある。根拠は固定binaryのAS 1024 MiBでの反復成功であり、pdfcpu更新時は同じ境界入力で再検証する。

## 測定前の合格基準

[初回計画](from-images-as-plan.json)を`fda21d8`で記録してから測定した。候補1152/1280 MiBが不合格となった後も基準を維持し、[追加候補の計画](from-images-as-next-plan.json)を各候補の測定前に記録した。

- 候補ASと候補−64 MiBの両方で、各入力30回連続・失敗0。
- 最悪のAdam7同士、Adam7とcompressを、両AS・両quality・compress両levelで各30組処理する。
- HTTP/PDF構造・配置・寸法・透過・ヘッダー・後片付けが正常。OOM/oom_kill、残存job/tool、health/後続rotateの失敗は0。
- cgroupのkernel peakは1536 MiBの90%未満。tmpfs 256 MiB未満、job 124 MiB以下、出力54 MiB以下。

ASの余裕の根拠は「64 MiB低いASでも各入力30回失敗0」である。必要最小ASを確定した結果でも、任意入力で64 MiBを使える保証でもない。20 msのVmSize/RSS観測は診断値だけとして残し、上限との差をAS余裕へ換算しない。

## 入力と測定条件

Adam7の26型それぞれ低・高複雑度、合計52入力を、作業量上限320 MiBの直下で生成した。幅4000 pxで実際のencoded bytesを使い、1行高い隣接入力が上限を超えることも確認し、両側の寸法・bytes・SHA-256・見積りを保存した。

- RGB8/tRNS、RGBA8、グレー8/tRNS、グレー+alpha8。
- グレー16/tRNS、RGB16/tRNS、グレー+alpha16、RGBA16。
- パレット8（透過あり/なし）。グレーとパレットの1/2/4 bit（各透過あり/なし）。

必須12 MP RGBA8は4000×3000と3000×4000、低・高複雑度、非interlace/Adam7の8入力を追加し、合計60入力とした。高複雑度の非interlace入力は前回の固定入力とhashが一致する。小さい画像では独立したPillowデコードで非interlaceとAdam7の画素一致を確認する。CIの共有ベクタは大画像の容量測定の代わりにしない。

製品コードは`c3b4c9d3d0c77a3c1a0ba53e0fdfe2bf2a040259`、Docker image IDは`sha256:51e71b30fc8d893429cc542572df7a76fdf48ed173e766de0c33c905503e9b29`。固定imageの実出力は.NET 10.0.12、qpdf 12.3.2、pdfcpu 0.16.1（Go 1.27.1）、libjpeg-turbo 2.1.5である。生成側はPython 3.14.4、NumPy 2.3.5、zlib 1.3.1。完全な版と各driver/hashは生データを参照する。

各条件は新しいnon-root/read-only/network noneコンテナ、CPU 1、memory/MemorySwap合計1536 MiB（追加swapなし）、tmpfs 256 MiB、cap-drop ALL、no-new-privilegesで測る。pdfcpuはGOMEMLIMIT=200MiB、GOGC=100、GOMAXPROCS=1、GODEBUG空、GOTRACEBACK=none、private HOME、CORE=0、FSIZE=54 MiB+1を使う。成功PDFはAS 544 MiBのqpdfでも構造検査する。

直接取込は毎回新しいpdfcpu processを起動する。HTTP測定では既存製品の共用AS設定を一時的に候補値へ変え、read-only wrapperがimport以外を1 GiBへ戻す。overlay・version/font確認へ候補ASを広げない。固定1 byteのexec記録を起動時検証後に消し、画像要求数とpdfcpu起動数の一致をassertする。このwrapperは測定コンテナだけにbind mountし、製品へ追加していない。

## AS別の失敗率

| AS MiB | 入力範囲 | 失敗 / 実行 | 判定 |
| ---: | --- | ---: | --- |
| 960 | Adam7 RGBA8境界・Adam7/非interlace 12 MP高複雑度横 | 75 / 90 | 不合格、各入力30回 |
| 1024 | 同上 | 8 / 90 | Adam7の2入力は各4/30、非interlaceは0/30 |
| 1056 | Adam7 RGBA8境界・12 MP高複雑度縦横 | 18 / 90 | 各6/30、1120候補の−64 MiB条件が不合格 |
| 1072 | 全60入力 | 0 / 1800 | 1136候補の−64 MiB、全入力30回連続成功 |
| 1088 | 全60入力 | 0 / 1800 | 1152候補の−64 MiBだけは成功 |
| 1120 | Adam7 RGBA8境界・12 MP高複雑度縦横 | 0 / 90 | 全型検証へ進めず。1056で不合格 |
| 1136 | 全60入力 | 0 / 1800 | 全入力30回連続成功 |
| 1152 | 全60入力 | 1800 / 1800 | Go初期化で失敗。HTTP起動時検証も失敗 |
| 1216 | 全60入力 | 1800 / 1800 | Go初期化で失敗。1280候補の−64 MiB条件が不合格 |
| 1280 | 保存済み44入力（未完了3条件を含む） | 1281 / 1281 | 不合格が判明したため中断。全60入力の完了とは扱わない |

ASと成功率は単調ではなかった。画像なしのversionも1152/1216 MiBでは各3回とも初期化に失敗し、1024/1088/1280 MiBでは成功した。versionの3回試験は原因を探る補助試験で、採用判定へ合算しない。固定Go runtimeの予約・割当てが関係することは示唆されるが、詳しい因果を確定したとは扱わない。1536 MiB候補の全型測定は、1136候補が基準を満たしたため実施していない。

## 最悪入力の同時2件

1072/1136 MiBの両方で次を各30組測定した。qualityはstandard/original、compressはstandard/strongの全組み合わせを含む。圧縮の入力は前回の固定seed高複雑度4:4:4 JPEGを埋め込んだ約47.7 MiB PDFで、SHA-256一致を確認した。

| 組み合わせ | 条件数 | 要求数（すべて200） | 最大HTTP秒 | cgroup peak MiB | tmpfs観測peak MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Adam7 12 MP高複雑度RGBA8横 ×2 | 4 | 240 | 4.745 | 625.836 | 183.332 |
| Adam7 高複雑度RGBA8境界直下 ×2 | 4 | 240 | 5.298 | 636.160 | 186.316 |
| Adam7 12 MP高複雑度RGBA8横 + compress | 8 | 480 | 4.460 | 538.770 | 139.320 |

全960要求、pdfcpu起動720回、compress240回のJPEG各1件再圧縮が成功した。両jobの重なり、PDF構造・A4/余白・PNG寸法/SMask・成功ヘッダー、各組終了後のcleanup、各条件終了後のhealth/rotate 200を確認した。500/503/504、部分PDF・構造不正・診断情報露出、OOM/oom_kill、残存job/toolは0。

最大単一job観測93.133/124 MiB、出力46.529/54 MiB。cgroupピークは1072 MiB条件で630.055 MiB、1136 MiB条件で636.160 MiBだった。最大値の条件は列ごとに異なる。HTTP時間はアップロード・応答受信を含み、内部30秒deadlineとの差を保証された余裕にしない。tmpfs/job/toolは20 msの観測値で、cgroup peakはkernel値である。

途中で測定driverがexit 137となった1136 MiB・12 MP×2・originalの1条件は20組までの記録を保存し、合格件数から除外した。APIコンテナはOOMKilled=false、memory.eventsのOOM/oom_kill 0、health 200、残存jobなしだった。driver終了の原因は未確定である。driverが応答bodyを次の試行まで保持しないようにし、新しいコンテナで同じ条件を30組取り直して成功した。上表は取り直した完了条件だけを含む。

## 生データと再現

[集約記録](from-images-as-results.json)にenvironment、plan、入力manifest、各条件のsummaryとmetricsを保存し、[試行記録](from-images-as-trials.jsonl)に1行1試行で保存した。HTTPの1試行は同時2要求である。各条件の`trial_line_start`/`trial_lines`は1-basedで、JSONLの完全なSHA-256も集約記録へ保存した。直接取込の失敗・候補中断・旧HTTP測定・driver中断は別phaseへ保存し、採用候補の成功件数へ混ぜない。PDF/PNG本体・stderr本文・秘密情報はcommitしない。

初回生成は測定機能追加前の試作版を使ったため、その完全なsourceを集約記録の`original_generation_source`へchecksum付きで保存した（SHA-256 `ad68e9dfd57ce50a70ee1a0cbae86cd269a7de096b55f934218077c9705f26f7`）。生成の由来だけに使い、測定実装の証拠とは扱わない。commit `fda21d8`のdriver（SHA-256 `9dd0be50ad5a9836ac89253efc4e4dcda59c74b28aa6fb0fbe7e9997e4f3cd2c`）で全60入力を生成し直し、隣接境界を含む全case metadataが一致した。直接/HTTP測定は各environmentのdriver hashに対応し、途中のstartup失敗記録と最終1条件のdriver修正も区別できる。

固定SHAの製品imageを使う。新しく構築したimage IDが変わった場合は、保存済みplanのIDだけを書き換えて同一結果と扱わず、新しい環境・planとして記録する。現行driverで再現する例は次のとおり（Pillow/NumPy/qpdf/Dockerが必要）。fixture/result dirは毎回新しくする。

```bash
python3 scripts/from-images-followup.py --generate-only --fixtures /tmp/issue59-plain-reproduce
python3 scripts/from-images-as-validation.py --generate-only --fixtures /tmp/issue59-adam7-reproduce
sudo python3 scripts/from-images-as-validation.py --plan docs/from-images-as-next-plan.json \
  --fixtures /tmp/issue59-adam7-reproduce --output /tmp/issue59-as-direct-reproduce \
  --address-spaces 1072 1136
sudo python3 scripts/from-images-as-validation.py --plan docs/from-images-as-next-plan.json \
  --fixtures /tmp/issue59-adam7-reproduce --output /tmp/issue59-as-pairs-reproduce \
  --address-spaces 1072 1136 --http \
  --cases adam7-rgba8-12mp-high-landscape adam7-rgba8-high-boundary
sudo python3 scripts/from-images-as-validation.py --plan docs/from-images-as-next-plan.json \
  --fixtures /tmp/issue59-adam7-reproduce --output /tmp/issue59-as-mixed-reproduce \
  --address-spaces 1072 1136 --http --cases adam7-rgba8-12mp-high-landscape \
  --compress-fixture /tmp/issue59-plain-reproduce/manifest.json
```

## CIに固定した項目

C#とPythonが同じ固定159ベクタを読み、正確な作業量bytesと標準320 MiBの受付判定を検査する。12 MPの縦横・低高複雑度、パレット/透過なしグレー8の係数12、透過付きグレー8と1/2/4 bitの22P下限、Adam7全型の隣接境界（案Aの見積り）を含む。小さいencoded値でも係数12/22Pを識別するベクタがある。sanitizerのテストで、同じ画素のPNGがinterlaceだけで見積りを超えることも確認する。

案A（`3157ada`）でRelease build警告/エラー0、全920 .NETテスト成功、失敗/skip 0（3分24秒）、driver回帰5件も成功した。それ以前の記録として、GitHub CIはhead `5b3a7ac`の[実行38117119157](https://github.com/kooiei-in4a/amane-pdf-api/actions/runs/38117119157)でRelease build/test・Docker build/全smoke成功を確認した。TRX `/tmp/issue59-as-test-results/issue59-as-vectors-all.trx`はローカル記録であり、CI成果物と区別する。以後のheadのCI状態はPRで確認する。

現在の製品AS 1 GiBでも非interlaceの前回測定は成功したが、Adam7の非決定的失敗が残る。今回の成功は固定binary・合成入力・測定環境で宣言した反復基準を満たす根拠であり、未採用の設定が配置済みになったことを意味しない。
