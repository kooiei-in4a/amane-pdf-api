# PDF画像圧縮の検証記録（2026-10-08）

Issue #22の2026-10-08更新本文に対する実装検証です。#42／PR #43のmerge commit `d80d710`から、新しいbranch・worktreeを作成しました。過去コメントの設計や500枚完了の見積りは実装仕様として使用していません。

## 条件と測定方法

- runtime: Ubuntu 26.04、.NET 10、qpdf 12.3.2、libjpeg-turbo-progs 2.1.5。
- Docker: memory 1.5 GiB、`--memory-swap 1.5g`（cgroupのswap上限0）、CPU 1、tmpfs `/tmp` 256 MiB、同時2件、non-root、read-only、network none、cap-drop ALL、no-new-privileges。
- API設定は標準値のまま。qpdf AS 544 MiB／JPEGMEM 600M、djpeg／cjpeg AS 64 MiB、job 124 MiB、PNM 24 MiB、soft 21秒／hard 30秒。
- コンテナのloopbackへ実HTTPでmultipartを送信し、200応答と出力のqpdf検証を確認。各測定行は同じPDFを同時2件送信した結果です。
- `scripts/docker-memory-check.py`を拡張して、HTTP時間、kernelの`memory.peak`／`memory.events`、tmpfs、ファイル割当量、外部コマンドのAS／fsizeを測定しました。PDF・画像・JSON・内部pathを本番ログへ追加していません。
- HTTP時間・kernelのpeakとeventsは実測値です。段階別時間、tmpfs／jobファイルのpeak、RSS、コマンド開始時刻はホストの`/proc`を約20 ms間隔で観測した推定値です。監視間に終了したコマンドは「未観測」とし、0秒とは扱いません。段階時間の和はHTTP時間と一致しません。
- 品質測定は、CC BY-SAの公開風景写真1点を一時利用して12／24／約48 MPへresampleし、quality 85でbaseline 4:2:0・baseline 4:4:4・progressive 4:2:0に保存したものです。48 MPの8064×6048は48,771,072画素で、元写真から一部拡大を含みます。文書スキャン相当は文字を描いた2480×3508グレー合成画像（quality 90）です。実写真の種類を増やした一般的な画質評価ではありません。

## 圧縮率と画質

表の順序は「長辺px／A4長辺297 mmに置いた場合のdpi／PDF全体の削減率%／PSNR dB」です。PSNRは指定M/8で縮小したPNMと新JPEGを比較し、JPEG再圧縮による差だけを示します。縮小による情報量の減少は長辺・dpiで示します。PDFのページサイズ自体は変更しません。

| 入力 | standard | strong | 置換数（各要求） |
| --- | --- | --- | ---: |
| 12 MP、4000×3000、baseline 4:2:0 | 2000／171.0／79.95／38.51 | 1500／128.3／90.95／36.40 | 1 |
| 12 MP、baseline 4:4:4 | 2000／171.0／83.98／38.29 | 1500／128.3／92.77／36.35 | 1 |
| 12 MP、progressive 4:2:0 | 2000／171.0／78.69／38.51 | 1500／128.3／90.39／36.40 | 1 |
| 24 MP、6000×4000、baseline 4:2:0 | 2250／192.4／88.39／38.38 | 1500／128.3／95.90／36.64 | 1 |
| 24 MP、baseline 4:4:4 | 2250／192.4／90.61／38.27 | 1500／128.3／96.69／36.70 | 1 |
| 24 MP、progressive 4:2:0 | 6000／513.1／−0.01／未変換 | 6000／513.1／−0.01／未変換 | 0 |
| 約48 MP、8064×6048、baseline 4:2:0 | 2016／172.4／94.91／38.51 | 2016／172.4／96.36／37.24 | 1 |
| 約48 MP、baseline 4:4:4 | 2016／172.4／95.83／38.57 | 2016／172.4／97.02／37.30 | 1 |
| 約48 MP、progressive 4:2:0 | 8064／689.6／約0／未変換 | 8064／689.6／約0／未変換 | 0 |
| 文書スキャン相当、2480×3508グレー | 1754／150.0／74.26／32.28 | 1316／112.5／86.74／28.19 | 1 |

24／48 MPのprogressiveは、固定の`-maxmemory 64M`でdjpegがbacking storeを必要として非0終了しました。ASを192 MiBへ上げた補助probeでも同じため、ASだけの問題とは判断していません。Issue本文の画像単位失敗として元JPEGを保持し、生データ一致・正常200・ヘッダー0を確認しました。−0.01%は構造書き出しによる僅かな増加です。採用成功やファイル全体の削減を保証するように仕様を変更していません。

上の写真・文書20条件のHTTP時間は最大2.520秒でした。長辺が目標以下なら拡大しないこと、整数の切り上げ、8064×6048が両levelとも2016になることは別途実JPEGのAPIテストで確認しています。

## 時間・メモリ・tmpfs

MiBは1,048,576 bytesです。HTTPは同時2件の遅い方、置換数は2要求の値、tmpfsとjob割当は観測最大です。500画像・大小混在・最悪PNMは最終版イメージで再測定しました。

| 入力 | level | HTTP秒 | 置換数 | cgroup memory.peak bytes | tmpfs bytes | 1 job割当bytes |
| --- | --- | ---: | --- | ---: | ---: | ---: |
| 上限付近・対象JPEGなし、51,389,831 bytes | standard | 3.383 | 0／0 | 248,963,072 | 205,602,816 | 102,801,408 |
| 同上 | strong | 3.607 | 0／0 | 255,537,152 | 205,602,816 | 102,801,408 |
| 上限付近・JPEGあり、52,416,263 bytes（49.99 MiB） | standard | 6.313 | 5／5 | 575,197,184 | 221,319,168 | 110,735,360 |
| 同上 | strong | 6.344 | 5／5 | 558,940,160 | 221,503,488 | 110,768,128 |
| 500固有画像、30,390,056 bytes | standard | 27.950 | 257／257 | 196,943,872 | 119,914,496 | 59,957,248 |
| 同上 | strong | 25.712 | 403／406 | 173,629,440 | 92,315,648 | 46,211,072 |
| 大小混在112画像、32,503,220 bytes | standard | 12.736 | 112／112 | 253,091,840 | 140,992,512 | 71,192,576 |
| 同上 | strong | 9.306 | 112／112 | 253,124,608 | 130,678,784 | 65,691,648 |
| 7014×7014 RGB、実写真 | standard | 1.326 | 1／1 | 404,611,072 | 60,153,856 | 30,126,080 |
| 7014×7014 RGB、合成画像 | standard | 1.584 | 1／1 | 423,653,376 | 81,461,248 | 40,837,120 |
| 同上 | strong | 1.384 | 1／1 | 437,604,352 | 57,765,888 | 29,089,792 |
| 約48 MP progressive、合成画像 | standard | 3.124 | 0／0 | 783,777,792 | 34,177,024 | 17,088,512 |
| A4・600 dpiカラーFlate、44,023,268 bytes | standard | 2.655 | 0／0 | 539,910,144 | 176,095,232 | 88,047,616 |
| 同上 | strong | 2.664 | 0／0 | 552,300,544 | 176,095,232 | 88,047,616 |

測定全体で`memory.events`の`max`／`oom`／`oom_kill`はすべて0でした。最大memory.peakは783,777,792 bytes（約747.5 MiB）、最大観測tmpfsは221,503,488 bytes（約211.2 MiB）でした。HTTP 504やコンテナOOMは発生しませんでした。上限付近のJPEGありfixtureは、8064×6048カラーbaseline 4:2:0 JPEGを5個（各9,827,577 bytes）と、3,276,800 bytesの非圧縮グレー画像を含みます。両levelとも全5個を採用し、入力検証・抽出・変換・最終処理を測定しました。

500画像は2048×2048グレー、生JPEG各60,571 bytesです。21秒で画像処理を打ち切るため、500個全部の置換を保証しません。大小混在は24 MPの大きな画像12 objectと小さな画像100 objectです。新しい方式で112個を両levelとも処理しました。元画像のraw抽出とn×S予約は約20 ms監視とテストで確認し、容量不足後の継続・stdout超過から一枚ずつのfallbackはAPIテストで確認しました。

### 段階別時間（サンプリング推定、同時2件の範囲）

| 入力／level | 入力検証秒 | metadata秒 | 抽出秒 | 変換秒 | 最終書き出し秒 | 出力検証秒 |
| --- | --- | --- | --- | --- | --- | --- |
| 上限付近・対象JPEGなし／standard | 0.107–0.170 | 未観測 | 対象なし | 対象なし | 2.057–2.099 | 0.214–0.215 |
| 上限付近・対象JPEGなし／strong | 0.107–0.128 | 0.021（1件） | 対象なし | 対象なし | 2.071–2.093 | 0.194–0.260 |
| 上限付近・JPEGあり／standard | 3.592 | 未観測 | 0.175 | 1.331–1.417 | 0.108–0.130 | 0.196–0.202 |
| 上限付近・JPEGあり／strong | 3.531–3.594 | 未観測 | 0.131 | 1.309–1.417 | 0.109–0.174 | 0.152–0.196 |
| 500画像／standard | 6.686–6.728 | 0.064–0.112 | 0.205–0.316 | 10.659–11.178 | 0.081 | 6.384–6.492 |
| 500画像／strong | 6.602–6.773 | 0.064–0.131 | 0.426–0.533 | 9.529–10.102 | 0.060–0.094 | 4.111–4.218 |
| 大小混在／standard | 3.700–3.785 | 0.021（1件） | 0.173–0.253 | 6.151–6.205 | 0.025–0.081 | 0.915–0.960 |
| 大小混在／strong | 3.729–3.879 | 0.021–0.044 | 0.125–0.175 | 3.578 | 0.025（1件） | 0.411–0.412 |
| 最悪PNM・実写真／standard | 0.586–0.608 | 0.022 | 0.022 | 0.281 | 0.022–0.065 | 0.087–0.108 |

500画像の最終書き出し開始は、最初の入力検証の観測からstandardで21.021–21.043秒、strongで21.033秒でした。入力検証からのsoft 21秒を含み、hard 30秒内で最終処理まで完了した実測です。開始時刻と短い処理の正確な時間はこの監視では測れません。入力検証を遅らせるfake command、実行中画像のsoft中断、画像2秒の共有予算、hard 504、request cancel、アプリ停止は別途テストしています。

### PNM・fsize・容量台帳

- 最悪寸法: 7014×7014 RGB、standardのM=3、2631×2631×3＋header=**20,766,500 bytes**。実ファイルと一致しました。
- djpeg fsizeは**20,767,012 bytes**（512 bytesの余裕）、ASは**67,108,864 bytes**。実写真でcjpegの観測RSSは23,465,984 bytes（約22.4 MiB）でした。
- 500画像のraw batchは最大50個、S=65,536 bytes、n×S=**3,276,800 bytes**。メタデータfsizeは16,777,216 bytes。std PNM fsizeは3,211,793、strongは1,638,929、cjpeg fsizeは54,513 bytesでした。
- 大小混在の最大観測raw予約はstandard 86,102,016、strong 90,517,504 bytes。大きな画像を含むbatchのSは2,207,744 bytesでした。max nとmax Sは別のbatchの観測値なので単純に掛けません。
- 上限付近・JPEGありはS=9,834,496 bytes、5×S=49,172,480 bytes。PNM fsize=9,145,105、cjpeg fsize=8,844,819、最終fsize=standard 7,749,377／strong 7,617,912 bytesを確認しました。最大1 job割当は110,768,128 bytes（約105.6 MiB）で124 MiB以内です。
- 容量台帳は予約時に4 KiB割当で上限を判定します。50画像のS最大値に基づく予約、実PNMサイズ計算、採用後のI+A+J+F、上限を下げた場合の不採用と復帰をテストしました。各測定のファイル割当はjob 124 MiB以内です。
- SIGXFSZでfsizeを越える出力を打ち切ること、画像段階の153は不採用、最終段階の153は500となることをsmoke／テストで確認しました。

## 構造・表示・失敗処理

CI専用の`compress-validation.py --render`は26種類×両levelを比較します。DeviceRGB／Gray、ICCBased（合成sRGB profile）、CalRGB／Gray、SMask、Mask、Matte、Decode、color key、Form、inline、Flate、RunLength、LZW、ASCIIHex、ASCII85、複合filter、JPX、CCITT、JBIG2、CMYK、Lab、Indexed、Separation、DeviceNが対象です。

ページ数・MediaBoxと画像データを比較し、profile／Maskのデコード後データも一致させます。対象外の単独DCT／Flate／RunLength／JPX／JBIG2／CCITTは生データ一致、LZW／ASCII系／複合filterはデコード後一致です。Popplerで描画し、文字・ベクター領域はpixel一致、置換0個はページ全体のpixel一致、置換ありは描画PSNR >22 dBを確認します。.NETテストでは文字content・フォント辞書、共有参照と固有ヘッダー数も比較します。rendererの警告があるfixtureは成功扱いしません。描画ツール・Pillow・NumPyをruntimeへ入れていません。

その他のAPI／parserテストは、不正query／multipart、413／共通422／503、辞書とJPEGの寸法・成分数不一致、ICCBased Nの非整数・不一致、未解決／循環参照、SOF0／1／2、DNL、複数SOF、破損marker、対象外ColorSpace、全不採用・出力増大、最終2／3／153の500、126／127と通常画像失敗の区別、成功ヘッダーなし、情報非露出、0700／0600、設定変更を確認します。timeout／cancel／アプリ停止には既存Runnerによる親子processの終了とjob削除・処理枠の再利用を確認します。循環PDFは共通入力検査で422になる場合も含みます。

Docker smokeは実JPEGの両level圧縮、実AS／fsize、SIGXFSZ、djpeg／cjpeg起動失敗、固定ログ、3つのJPEG copyrightファイルを検査します。Linuxの自己テストに省略設定はなく、fakeはPdfTestContextで起動後に差し替えます。既存#42のテストと外部Runnerは変更していません。

## 再測定

ホストのCI専用依存はqpdf・libjpeg-turbo-progs・poppler-utils・python3-pil・python3-numpyです。Ubuntuホストの`/usr/bin/qpdf`のAppArmorが拡張子なしrawを拒否するため、CIでは同じAPTバイナリを一時コピーしてPATHへ置きます。Docker runtimeの起動・Runnerには特例を入れません。

```bash
python3 scripts/compress-validation.py --tools
python3 scripts/memory-fixtures.py /tmp/compress-fixtures --compress
# 実写真を使う場合は --photo /tmp/licensed-photo.jpg を追加
docker build -t amane-pdf-api:verify .
python3 scripts/docker-smoke.py amane-pdf-api:verify
python3 scripts/compress-validation.py --render amane-pdf-api:verify
sudo python3 scripts/docker-memory-check.py amane-pdf-api:verify \
  /tmp/compress-fixtures /tmp/compress-results-standard --compress standard
sudo python3 scripts/docker-memory-check.py amane-pdf-api:verify \
  /tmp/compress-fixtures /tmp/compress-results-strong --compress strong
# 大小混在を含める場合は --cases mixed-images 500-images を追加
```

## 確認範囲とセルフレビュー

確認済みの事実は、上のfixtureのHTTP結果・実サイズ・kernel memory情報と、Release build（警告・エラー0）／test（424件PASS）、Docker build／smoke、CI専用描画比較です。段階別時間・tmpfs／RSS peakはサンプリング推定です。任意のPDF、写真の一般的な削減率、500枚すべての置換、他のCPU環境での30秒内成功、非Linuxでのruntime試験、サイト#38の動作は未検証です。出力見積りFは数学的な上界ではなく、fsize超過は仕様どおり500です。

[GitHub CI run 37760739723](https://github.com/kooiei-in4a/amane-pdf-api/actions/runs/37760739723)でも、Release build／424 tests／Docker build／smoke／描画比較がすべてPASSしました。上限付近・JPEGありの追加は実測文書だけの変更で、検証したAPIソースは同じです。

セルフレビューでは、JSON深さ設定が一部のserializerに反映されていない点、update JSON上限の判定漏れ、採用前に中断したupdate断片の即時削除、非0世代objectのraw名の検査を修正し、関連テストを追加しました。重大な問題は残っていないと判断しています。サイト#38はlevelの下限目安、ヘッダー0、部分成功、PDF全体の増大、Linux提供と413／422／503／504を扱い、proxyでヘッダーを伝達する必要があります。サイトの変更・merge・tag・Release・deploy・イメージ公開は行いません。

fixtureと実写真はRepositoryに含めず一時領域へ生成しました。検証に使った一時画像・fixture・測定出力・コンテナ・検証イメージを削除し、残っていないことを確認しました。PRレビュー用のworktree・branchとソースの生成スクリプトは保持します。
