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

次の20条件はレビュー対応前の実測です。追加した12 MP progressive 4:4:4と300ページの測定は末尾の「PR #45レビュー対応後の検証」に記録します。

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

progressiveは24／48 MPだけが失敗するわけではありません。固定の`-maxmemory 64M`・AS 64 MiBで、12 MP（4000×3000）の4:4:4でもdjpegが`Backing store not supported`となることを追加検証しました。係数の保存量からの目安は4:2:0で約21 MP以上、4:4:4で約11 MP以上です。この境界は推定であり、任意の画像の成功・失敗を保証しません。設定はHuman判断により変更していません。24／48 MPの4:2:0については、ASを192 MiBへ上げた過去の補助probeでも同じ失敗があり、ASだけの問題とは判断していません。Issue本文の画像単位失敗として元JPEGを保持し、生データ一致・正常200・ヘッダー0を確認しました。−0.01%は構造書き出しによる僅かな増加です。採用成功やファイル全体の削減を保証するように仕様を変更していません。

上の写真・文書20条件のHTTP時間は最大2.520秒でした。長辺が目標以下なら拡大しないこと、整数の切り上げ、8064×6048が両levelとも2016になることは別途実JPEGのAPIテストで確認しています。

## 時間・メモリ・tmpfs

MiBは1,048,576 bytesです。HTTPは同時2件の遅い方、置換数は2要求の値、tmpfsとjob割当は観測最大です。次の表はレビュー対応前の実装での記録です。500画像・大小混在・最悪PNMはその時点の最終版イメージで再測定しました。

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

Docker smokeは実JPEGの両level圧縮、実AS／fsize、SIGXFSZ、djpeg／cjpeg起動失敗、固定ログ、3つのJPEG copyrightファイルを検査します。Linuxの自己テストに省略設定はなく、fakeはPdfTestContextで起動後に差し替えます。既存#42のテストケースと外部Runnerは変更していません。PdfTestContextは短いrequest予算も起動後に差し替え、実コマンド・有効な設定による自己テストを維持します。

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
# 多ページだけ測る場合は --cases 1400-pages-shared 1400-pages-shared-inherited 300-pages-direct 300-pages-indirect 300-pages-inherited を追加
# 大小混在だけを測る場合は --cases mixed-images 500-images を追加
```

## 確認範囲とセルフレビュー

レビュー対応前に確認した事実は、上のfixtureのHTTP結果・実サイズ・kernel memory情報と、Release build（警告・エラー0）／test（424件PASS）、Docker build／smoke、CI専用描画比較です。段階別時間・tmpfs／RSS peakはサンプリング推定です。任意のPDF、写真の一般的な削減率、500枚すべての置換、他のCPU環境での30秒内成功、非Linuxでのruntime試験、サイト#38の動作は未検証です。出力見積りFは数学的な上界ではなく、fsize超過は仕様どおり500です。

[GitHub CI run 37760739723](https://github.com/kooiei-in4a/amane-pdf-api/actions/runs/37760739723)でも、Release build／424 tests／Docker build／smoke／描画比較がすべてPASSしました。上限付近・JPEGありの追加は実測文書だけの変更で、検証したAPIソースは同じです。

セルフレビューでは、JSON深さ設定が一部のserializerに反映されていない点、update JSON上限の判定漏れ、採用前に中断したupdate断片の即時削除、非0世代objectのraw名の検査を修正し、関連テストを追加しました。重大な問題は残っていないと判断しています。サイト#38はlevelの下限目安、ヘッダー0、部分成功、PDF全体の増大、Linux提供と413／422／503／504を扱い、proxyでヘッダーを伝達する必要があります。サイトの変更・merge・tag・Release・deploy・イメージ公開は行いません。

初回実装時のfixtureと実写真はRepositoryに含めず一時領域へ生成しました。その検証に使った一時画像・fixture・測定出力・コンテナ・検証イメージを削除し、残っていないことを確認しました。PRレビュー用のworktree・branchとソースの生成スクリプトは保持します。

## PR #45レビュー対応後の検証

本節は初回レビュー対応commit `a327d8e` の記録です。再レビューのページspool修正は次節に記録します。

### 変更とテスト

- ページ→Parent→Resources→XObject→画像辞書→SMask／Mask／ICCの参照を階層ごとに取得します。辞書・値の別名参照も階層単位で先読みし、循環と深さ上限を維持します。既存の500件chunk、JSON 16 MiB・深さ64、spool 16 MiB、fsizeを変更せず、全辞書のDOMは保持しません。
- 起動回数を数えるAPIテストは、10ページ・直接Resourcesで最大3回、10／300ページ・間接Resources／XObjectでともに最大5回、300ページ・間接かつParent継承で最大6回、10ページ・各画像にSMask／Mask／ICCと間接Nがある場合で最大9回です。共有画像は1個、依存参照付きの10固有画像は10個を置換し、ページ数も検証します。fake qpdfは実コマンドでの起動後に差し替えます。
- 最初のEOI以降のデータを無視するHuman承認をIssue本文へ追記しました。parserの後続データ許容、APIの両levelでヘッダー1と実寸・precision・成分数一致、EOIより前の異常拒否をテストします。cjpeg出力にも同じparserを使い、後続データがあっても本体のDNL・寸法違い・成分数違いを採用しないテストを追加しました。実機のMPF／Ultra HDR表示の保持は未検証で、後続gain mapは置換JPEGへコピーしません。
- entry群とupdate.jsonの同時存在について、各entryの4 KiB割当を合計して採用前に判定します。結合時と最終出力時の二つのpeakを確認する容量境界テストを追加しました。低いjob上限で不採用、上限を戻すと採用になる既存APIテストも維持します。旧版が標準job上限でこの理由による500になる実PDFは再現していません。
- `CompressSoftTimeoutSeconds < QpdfTimeoutSeconds`を起動時の設定検証に追加し、21／30は有効、30／30・31／30は無効となるテストを追加しました。短いrequest予算はテスト側だけで起動後に設定し、本番の自己テストを省略しません。

### 300ページ・間接参照（確認済み）

`memory-fixtures.py --compress`に直接記述・間接参照・Parent継承の3種類を追加しました。各PDFはA4の300ページに2048×2048グレーbaseline JPEGを1固有objectずつ置き、画像内容は同じです。生JPEGは各59,478 bytes（quality 80）。入力は直接17,950,339、間接17,977,749、継承18,007,530 bytesです。レビュー時の15 MB fixtureとは異なる合成fixtureであり、レビューの11.3／22.9秒との絶対時間の比較はしません。

標準Docker条件・API設定は上記と同じです。同時2件の実HTTPで、全12要求が200・各300枚置換でした。表のHTTPは2件の遅い方、kernel memory.peakは実測、tmpfs／job割当は約20 msサンプリングの観測最大です。

| Resourcesの形式 | level | HTTP秒 | 置換数 | memory.peak bytes | tmpfs bytes | 1 job割当bytes |
| --- | --- | ---: | --- | ---: | ---: | ---: |
| 直接 | standard | 24.341 | 300／300 | 140,169,216 | 70,459,392 | 35,229,696 |
| 間接（XObjectも間接） | standard | 24.910 | 300／300 | 143,659,008 | 70,533,120 | 35,266,560 |
| 間接＋Parent継承 | standard | 25.001 | 300／300 | 144,642,048 | 70,598,656 | 35,299,328 |
| 直接 | strong | 16.679 | 300／300 | 114,946,048 | 55,894,016 | 27,992,064 |
| 間接（XObjectも間接） | strong | 16.981 | 300／300 | 118,513,664 | 55,726,080 | 27,975,680 |
| 間接＋Parent継承 | strong | 17.718 | 300／300 | 124,866,560 | 55,832,576 | 27,959,296 |

`memory.events`のmax／oom／oom_killは全ケース0、hard 30秒の504もありませんでした。standardで最終書き出しを観測した開始時刻は、直接20.496秒、間接20.700–20.801秒、継承20.957秒です。今回のfixtureはsoft 21秒までに300枚を処理できましたが、任意の300ページPDFの全画像処理を保証しません。

以下は段階別のサンプリング推定です。短い外部プロセスの未観測分や.NET内での処理を含まず、各段階の和はHTTPと一致しません。

| Resources／level | 入力検証秒 | metadata秒 | 抽出秒 | 変換秒 | 最終書き出し秒 | 出力検証秒 |
| --- | --- | --- | --- | --- | --- | --- |
| 直接／standard | 3.978–4.086 | 0.117–0.144 | 0.156–0.254 | 13.693–13.893 | 0.086 | 3.404–3.482 |
| 間接／standard | 4.076–4.119 | 0.205–0.287 | 0.334–0.418 | 14.036–14.044 | 0.102–0.111 | 3.693–3.846 |
| 継承／standard | 4.056–4.143 | 0.358–0.396 | 0.431–0.486 | 13.321–13.790 | 0.115 | 3.559–3.663 |
| 直接／strong | 3.943–3.986 | 0.065–0.145 | 0.334–0.430 | 6.503–7.291 | 0.055 | 2.130–2.211 |
| 間接／strong | 3.984–4.027 | 0.221–0.232 | 0.377–0.412 | 7.206–7.544 | 0.028–0.059 | 1.989–1.993 |
| 継承／strong | 4.095–4.117 | 0.262–0.399 | 0.367–0.426 | 7.582–7.592 | 0.086 | 2.183 |

standardのmetadataプロセスは直接3／3回、間接5／5回、継承6／6回を観測しました。これはサンプリングで見えた起動数の下限です。短い起動を見逃し得るため、正確な回数上限の根拠には上記wrapperによるテストを使います。rawは最大50個、S=65,536 bytes、n×S=3,276,800 bytes、metadata fsize=16,777,216 bytes、djpeg／cjpeg AS=67,108,864 bytesを確認しました。PNM fsizeはstandard 3,211,793／strong 1,638,929、cjpeg fsizeは53,530 bytesでした。

### 12 MP progressive 4:4:4（確認済み）

公開画像は新たに取得せず、4000×3000カラーの合成画像をquality 85のprogressive 4:4:4にしました（入力PDF 3,421,884 bytes）。標準Docker条件・同時2件で、standard 1.293／1.294秒、strong 1.299／1.301秒、全4要求が200・ヘッダー0でした。出力は3,422,035 bytes、元JPEGの生データ一致、長辺4000 px／A4長辺換算342.1 dpi、削減率−0.0044%です。未変換なので再圧縮PSNRは評価対象外です。

同じruntimeのnon-root・read-only・network none・memory 1.5 GiB・swapなし・CPU 1・tmpfs 256 MiBの補助コンテナでも、AS 64 MiB・`-maxmemory 64M -maxscans 100 -strict -scale 4/8`／`3/8`でdjpegがexit 1・`Backing store not supported`となることを確認しました。製品ログへのstderr追加はしていません。API測定のmemory.peakはstandard 300,339,200／strong 296,243,200 bytes、観測tmpfs最大13,701,120 bytes、max／oom／oom_killは0でした。

約21 MP（4:2:0）／約11 MP（4:4:4）は係数保持量からの目安であり推定です。12 MP 4:4:4と既存の24／48 MP 4:2:0の失敗は実測ですが、境界前後の全画像を網羅した値ではありません。失敗した画像を元のまま保持する仕様を受け入れ、ASとmaxmemoryは変更していません。

### 検証とセルフレビューの範囲

a327d8eのRelease build（警告・エラー0）、全Release test（439件PASS・skip 0）、Docker build／smoke、26種類×両levelの構造・データ・描画比較はすべてPASSしました。最新commitのGitHub CI結果はPR #45へ記録します。起動停止のテストではTestServerのAbortが一時領域削除の完了より先に観測される競合があり、既存unlock／mergeテストと同様に削除を最大5秒待ってから検証するよう修正しました。新JPEGのDNL拒否テストが寸法不一致でも失敗する点は、期待する寸法の出力に変更して本体の検査だけを確認するよう直しました。外部Runner、QpdfProcessor、DI、kill／waitの本番処理には変更がありません。

確認済み／推定／未検証の区別は上記のとおりです。実機MPF／HDR表示、他のCPU環境、任意のPDFの30秒内成功、標準job上限で旧JSON結合問題が500になる実PDF、サイト#38の動作は未検証です。API契約はHuman承認のEOI後続データ許容以外を維持し、サイト#38にはprogressiveの失敗目安と元画像保持、置換時のgain map非継承を説明する必要があります。

今回のレビュー対応の一時fixture・測定結果・補助probe・コンテナ・検証イメージも削除済みです。公開画像の新規取得はなく、生成コードとPRのworktree／branchを保持します。merge・tag・Release・deploy・イメージ公開は行っていません。


## PR #45再レビュー：ページ側spoolの解放

### 修正と既存上限

ページ側は最大500ページずつ、階層単位の取得→画像参照収集→削除を行います。低いspool設定では `min(500, max(1, floor(spool上限 / (4×4096))))` ページとし、4 MiBでは256ページにします。4は通常のpage・Parent・Resources・XObjectの4段階の目安で、深い継承・別名、大きな辞書が必ず収まるという保証ではありません。実際の各ファイル割当を既存spool上限とjob台帳で検査し、上限は緩めません。

別名の連鎖を含め、その単位で取得したページ側metadataをfinallyで削除します。spoolBytesと容量台帳の割当を解放し、未解決参照のcache entryも削除します。画像参照だけをHashSetに集め、ページ処理が完了してから画像／ICC／Mask／SMask辞書を取得して判定するため、必要な画像辞書を途中で削除しません。JSONサイズ・深さ、spool、500参照chunk、500画像、job上限と外部Runner等の仕組みは変更していません。

qpdfは1参照・1ページずつ起動せず、ページ単位内の階層と既存500参照chunkごとに起動します。ページ数が500を超えると単位数に応じて起動が増えますが、ページ数と同じ数の起動にはなりません。共有1画像の1,400ページはpages JSON 1回＋3単位×3階層＋画像1回で最大11回、Parent継承では最大14回です。300ページの既存上限（直接3、間接5、継承6、依存参照付き10ページ9）は維持します。

### spool上限後の判定済み候補について

判定済み候補を上限到達後に新たに変換する案は採用していません。Issue本文のエラー表は「既知の画像処理用metadata・時間・容量上限 → 画像処理を打ち切り、採用済み結果で最終処理」と定めています。辞書で候補と判定した段階では実JPEGのheader検査・変換・10%削減の採用判定は未完了で、採用済みJPEGではありません。今回も本文の処理順（候補判定→Length降順・最大500→変換）と打ち切り契約を維持します。したがって単位内の巨大辞書・深い参照や画像側metadata自体で上限に達し、まだ採用済みJPEGがなければ0になる場合は残ります。ページ数に比例する不要な辞書の累積は今回解消しています。

### テストとセルフレビュー

- 4 MiB・700ページ（間接Resources、Parent継承も含む）で共有画像を1回置換します。修正前のページ側3×700×4 KiBは約8.20 MiBで4 MiBを超えます。今回は256／256／188ページの単位で解放するため、抽出開始時に残る辞書は画像objectだけです。
- 標準設定・1,400ページの間接Resourcesで両levelヘッダー1、Parent継承でもヘッダー1を確認します。入力50 MiB以内、出力検証・ページ数を確認し、job残留を検査します。起動回数をwrapperで数えて11／14回以内を確認します。
- 容量台帳の入力割当＋4 KiBから、辞書削除で入力割当だけへ戻り、次の辞書を同じ4 KiB spoolで取得できることを直接検証します。別名の3段連鎖も削除・再取得できることを起動後のfake metadataで確認します。PDFの間接objectそのものを別名にした実fixtureは共通qpdf入力検証で422になるため、defensive metadata処理の単体テストへ分けました。
- 既存4 KiB spoolの不採用テストは、ページ側の解放後にも画像辞書2個が収まらないfixtureへ変更しました。上限値と期待する200・ヘッダー0は同じで、不要なページ辞書の累積を上限テストの根拠にしません。既存の階層・継承・ICC／Mask／SMaskテストも維持しています。

### 1,400ページ・共有画像の実測（確認済み／推定を区別）

生成コードに `1400-pages-shared.pdf` と `1400-pages-shared-inherited.pdf` を追加しました。2048×2048グレーbaseline JPEG（quality 80、59,478 bytes）1 objectをA4の全1,400ページで共有します。Resources・XObject辞書はページごとの間接objectで、後者はページごとのParentからResourcesを継承します。入力は416,224／555,950 bytes（50 MiB以内）です。公開画像は新たに取得せず、fixture本体はRepositoryへ含めません。

修正前 `a327d8e` をgit archiveから別の一時build contextへ展開して比較しました。両imageとも標準Docker条件（memory 1.5 GiB、`--memory-swap 1.5g`＝swapなし、CPU 1、tmpfs 256 MiB、同時2、non-root、read-only、network none）・標準API設定の実HTTPです。以下のHTTP／置換数とkernel memory.peak／memory.eventsは確認済みの実測値です。HTTPは2要求の遅い方です。

| Resources | level | 修正前HTTP秒 | 修正前置換数 | 修正後HTTP秒（2要求） | 修正後置換数 |
| --- | --- | ---: | --- | --- | --- |
| 間接 | standard | 2.591 | 0／0 | 3.492／3.462 | 1／1 |
| 間接＋Parent継承 | standard | 3.198 | 0／0 | 4.844／4.947 | 1／1 |
| 間接 | strong | 2.682 | 0／0 | 3.335／3.314 | 1／1 |
| 間接＋Parent継承 | strong | 3.267 | 0／0 | 4.736／4.700 | 1／1 |

標準16 MiBでも旧版の置換0を実測で再現しました。旧版は途中でmetadata処理を打ち切るため、HTTPが短いことを処理速度の優位とは評価しません。修正後の全8要求は200・ヘッダー1、qpdf出力検証・job削除・health・子プロセス残留なしを確認しました。全ケースでmemory.eventsのmax／oom／oom_killは0、504なしです。

metadata秒は約20 msサンプリングで観測したmetadata qpdfの累積実行時間で、.NET側の解析・削除や未観測区間を含むmetadata段階全体の正確な時間ではありません。tmpfs／job割当は同じサンプリングの観測最大であり、短いpeakを見逃し得る推定です。

| Resources／level | metadata秒（推定） | memory.peak bytes（確認済み） | tmpfs bytes（観測最大） | 1 job割当bytes（観測最大） |
| --- | --- | ---: | ---: | ---: |
| 間接／standard | 1.402–1.435 | 61,427,712 | 11,120,640 | 6,561,792 |
| 継承／standard | 2.459–2.550 | 70,062,080 | 15,450,112 | 8,749,056 |
| 間接／strong | 1.397–1.648 | 66,404,352 | 11,075,584 | 6,561,792 |
| 継承／strong | 2.279–2.400 | 69,857,280 | 15,495,168 | 8,749,056 |

修正前のmemory.peakは間接standard 91,377,664／strong 87,629,824、継承standard 97,263,616／strong 96,645,120 bytesでした。修正前の観測tmpfs最大は順に33,177,600／27,869,184／32,243,712／32,243,712 bytesです。ページ側を解放することで修正後はこれらの観測値が下がりました。修正後にmetadata起動11／14回を両要求で観測し、wrapperテストの上限と一致しました。

| Resources／level | 入力検証秒 | 抽出秒 | 変換秒 | 最終書き出し秒 | 出力検証秒 |
| --- | --- | --- | --- | --- | --- |
| 間接／standard | 0.172 | 0.108–0.171 | 0.022 | 0.130–0.173 | 0.042–0.086 |
| 継承／standard | 0.195–0.216 | 0.194–0.208 | 0.022–0.044 | 0.129–0.195 | 0.084–0.086 |
| 間接／strong | 0.150 | 0.151–0.173 | 0.022（1件、他は未観測） | 0.108–0.173 | 0.022–0.084 |
| 継承／strong | 0.194 | 0.151–0.173 | 0.022–0.066 | 0.194–0.195 | 0.042–0.086 |

段階時間はすべてサンプリング推定です。最終書き出し開始の観測は3.142–3.186／4.432–4.626／2.955–3.043／4.300–4.408秒で、soft 21秒より前でした。PNM実サイズはstandard 3,211,281／strong 1,638,417 bytes、fsizeは各サイズ＋512 bytesです。rawは1 object・予約65,536 bytes、metadata fsize 16,777,216、cjpeg fsize 53,530、djpeg／cjpeg AS 67,108,864 bytesを確認しました。

### 300ページの比較（確認済み）

前節と同じ300固有画像fixtureを同一host・標準Docker条件で修正前後各1組（同時2件）測定しました。HTTPは遅い方、metadata秒はサンプリング推定です。

| Resources／level | 修正前HTTP秒 | 修正前置換数 | 修正後HTTP秒 | 修正後置換数 | 修正前metadata秒 | 修正後metadata秒 |
| --- | ---: | --- | ---: | --- | --- | --- |
| 直接／standard | 24.560 | 300／300 | 24.507 | 300／300 | 0.043–0.102 | 0.114–0.128 |
| 間接／standard | 24.853 | 300／300 | 24.659 | 300／300 | 0.245–0.340 | 0.180–0.243 |
| 継承／standard | 25.010 | 291／289 | 25.124 | 300／300 | 0.488–0.538 | 0.218–0.275 |
| 直接／strong | 16.672 | 300／300 | 16.555 | 300／300 | 0.073–0.139 | 0.067 |
| 間接／strong | 16.913 | 300／300 | 16.753 | 300／300 | 0.219–0.250 | 0.205–0.255 |
| 継承／strong | 17.857 | 300／300 | 17.047 | 300／300 | 0.365–0.448 | 0.303–0.306 |

修正前の継承standardは今回soft deadlineで291／289枚になり、最終書き出し開始を21.000–21.023秒に観測しました。修正後は全300枚で20.899秒でした。HTTP差は＋0.114秒（約0.46%）で、他5条件は短縮しました。各1組の測定で統計的な性能差は保証しませんが、大きな悪化は観測せず、全要求200・30秒以内でした。前節の旧測定と今回の置換数の違いも、21秒付近での時間変動を示します。

修正後300ページのmemory.peak最大148,537,344 bytes、観測tmpfs最大70,598,656 bytes、1 job最大35,299,328 bytes、memory.events max／oom／oom_killは全ケース0でした。metadata起動は直接3、間接4–5、継承6回を観測し、短い起動を見逃すため正確な上限は既存wrapperテスト3／5／6回で確認します。各要求後のjob／子プロセス残留なしを確認しました。

### 再レビュー対応の検証・残る確認範囲

Release build（警告・エラー0）、全Release test（446件PASS、skip 0）、Docker build、Docker smoke、26種類×両levelの構造・データ・描画比較がすべてPASSしました。push後の最新commitのGitHub CI結果はPR #45に記録します。既存の多ページ・継承・ICC／Mask／SMask・timeout／cancel／容量テストもPASSしています。

確認済みの事実、サンプリングによる推定、未検証事項は上記のとおりです。深い別名・継承や大きい画像側metadataが上限に達するすべてのPDF、任意のPDFの30秒以内完了、他CPU環境での性能、サイト#38の動作は未検証です。ページ単位の4 object見積りや1回の比較から、任意の多ページPDFの置換数を保証しません。API契約・既存の上限・依存・ExternalProcessRunner／QpdfProcessor／DI／kill／waitは変更せず、サイト側の追加変更はありません。

今回の一時fixture・測定出力・baseline build context・qpdfコピー・Python cache・コンテナ・2つの検証イメージを削除し、残留なしを確認しました。公開画像の新規取得はなく、生成コードとPR用worktree／branchを保持します。merge・tag・Release・deploy・イメージ公開は行っていません。
