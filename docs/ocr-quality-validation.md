# OCRの標準・高精度の検証（2026-11-26）

対象は [Issue #126](https://github.com/kooiei-in4a/amane-pdf-api/issues/126) の実装です。起点は `e3fd370` です。APIの指定と上限は[READMEのOCR節](../README.md#スキャンpdfの日本語ocrlinux)に記載しています。

## 再現方法

Ubuntu 26.04、AMD Ryzen 5 5600G、Tesseract 5.5.0、qpdf 12.3.2 で実測しました。Dockerは `--cpus 1 --memory 1536m --memory-swap 1536m --read-only --tmpfs /tmp:rw,nosuid,nodev,noexec,size=256m` で起動しました。アプリは非rootで、同時実行上限2件、qpdf上限30秒の既定設定を使いました。Tesseractは1スレッド、1プロセスあたりの仮想メモリ上限384 MiBです。

```bash
docker build -t amane-pdf-api:ocr-quality .
npm ci --prefix scripts/ocr-search --ignore-scripts --omit=optional --omit=dev
python3 scripts/ocr-search-validation.py amane-pdf-api:ocr-quality --benchmark
```

検証ホストにPython 3、Pillow、Node.js、qpdfが必要です。`--benchmark` で密な文書4件の応答時間を測ります。`--quality standard` または `--quality high` で方式を指定できます。`--artifacts DIR` で検索試験の入出力PDFを確認できます。

## PDF.js検索

PDF.js 6.3.289の `PDFFindController` で、出力PDFから1回以上見つかった語を数えました。試料はスクリプトで生成しました。請求書と表は罫線付きの明細7行を200 dpi・JPEG品質75で描画しました。影はページの片側を約35%暗くしました。

| 試料 | 標準 | 高精度 |
| --- | ---: | ---: |
| 横書き | 7/7 | 7/7 |
| 離れた欄 | 4/4 | 4/4 |
| 行をまたぐ段落 | 3/3 | 3/3 |
| 横書き、PDFの回転とCropBoxあり | 7/7 | 7/7 |
| 縦書き、横書き見出しあり | 7/9 | 9/9 |
| 罫線付き請求書 | 31/34 | 34/34 |
| 罫線付きの表だけのページ | 8/9 | 9/9 |
| 影のあるページ | 12/12 | 12/12 |
| 合計 | 79/85 | 85/85 |

両方式とも、離れた欄の3つの誤結合候補と `Windows11` の検索結果は0件でした。罫線付き請求書は両方式で「品名」「金額」「合計金額」「返却ポスト」「12,800」が見つかりました。標準で見つからなかったのは請求書の「請求書」「19,730」「217,030」、表だけのページの「数量」、縦書きの横書き見出し2語です。

スキャン画像自体が上下逆さのページは対応範囲外です。画像を180度回転して行った初回計測では、両方式ともPDF.jsで12/12語、固定版PDFiumで0/12語でした。横向きなどで回転した行の文字層と、既に文字があるページの扱いは #124 に残っています。[初回の試験コード](https://github.com/kooiei-in4a/amane-pdf-api/blob/3076bd984ecb5c0d47800fd6b1cfc322068ad8a5/scripts/ocr-search-validation.py)・[Issue #124](https://github.com/kooiei-in4a/amane-pdf-api/issues/124)

試作の罫線付き請求書は標準32/34語・高精度31/34語、表だけのページは9/9語・8/9語、影は10/12語・12/12語、縦書きの横書き見出しは7/9語・9/9語でした。試作全体は622語を対象にしています。[試作結果（Issue #126）](https://github.com/kooiei-in4a/amane-pdf-api/issues/126)

## 短い1行の標準OCRで文字層が空になった回帰

**旧試料：標準0/8字・高精度8/8字。** レビューでは、PRの `d1d658d` のイメージを上記の本番相当のコンテナ制約で動かし、12 ptの「日本語の起動確認」だけを描いた旧スモーク試料で確認しました。両方式ともHTTP 200でした。標準では文字層が空になり、高精度では全文を認識しました。ページ番号だけ、署名欄だけ、短い1行だけの文書も、処理が成功したように見えたまま検索できない可能性があります。発生頻度は未計測です。[PR #127 のレビュー](https://github.com/kooiei-in4a/amane-pdf-api/pull/127#issuecomment-7179161029)

1. `scripts/docker-smoke.py` の `pdfcpu_smoke` と同じ手順で、`tests/Amane.Pdf.Api.Tests/Fixtures/pdfcpu-blank.json` を `qpdf --json-input` でPDFにし、`pdfcpu-layer.json` を `pdfcpu create` で重ねます。ページ下端中央に旧試料の1行が入ります。
2. `POST /api/pdf/to-images?format=png&dpi=200` のZIPからPNGを取り出し、`POST /api/pdf/from-images?quality=original` に渡して画像だけのPDFを作ります。
3. そのPDFを `POST /api/pdf/ocr?direction=horizontal&quality=standard` と `quality=high` に送り、HTTPステータスと出力PDFの文字層を比較します。
4. 同じ画像PDFからAPIと同じPDFiumレンダラーの `render-png` で300 dpiのPNGを得ます。標準の `jpn` で同じPNGをTesseractに渡し、`--psm 1`・`3`・`6` と `thresholding_method=0`・`1`・`2` を組み合わせてTSVを比較します。描画経路を比べる場合は、同じPDFを `pdftoppm -r 300 -png -singlefile` で描画し、同じ認識条件で比較します。

PDFiumのPNGでは、どのPSMでもSauvola（`thresholding_method=2`）のときだけ空になり、従来の二値化（`0`）と適応型Otsu（`1`）では全文を認識しました。文字をページ中央や上端に移しても同じ結果でした。比較した二値化画像の黒画素数は7,012と7,015で、文字の画素が残っていても認識段階で結果が空になりました。同じPDFを `pdftoppm` で300 dpiに描いたPNGでは、画素がほぼ同じでもSauvolaで全文を認識しました。[PR #127 のレビュー](https://github.com/kooiei-in4a/amane-pdf-api/pull/127#issuecomment-7179161029)

`thresholding_window_size=1.0` と `thresholding_kfactor=0.2` の組み合わせで1行の試料は回復しましたが、罫線付き請求書は29/34語で既存の合格基準を下回りました。[Issue #128](https://github.com/kooiei-in4a/amane-pdf-api/issues/128) では、標準の横書きに `thresholding_window_size=0.15` と `thresholding_kfactor=0.2` を適用しました。1ページにつき1回の認識で短い1行を抽出でき、複数行の文書も含めてDockerスモークで確認します。

## 処理時間

密な文書は、300 dpiのA4に11 pt相当のBIZ UDP明朝で約1,680字を描き、横書き35行または縦書き28列に配置しました。同じ画像ページを標準は10ページ、高精度は5ページ連ねました。応答時間は、1リクエストずつ送信を開始してからPDFを受信し終えるまでです。他の処理が並行する条件と、本番ホストのCPUでの余裕は未計測です。

| 方式・試料 | 実測 | 試作 | 差 |
| --- | ---: | ---: | ---: |
| 標準・密な縦書き10ページ | 54.50秒 | 50.0秒 | +4.50秒 |
| 標準・密な横書き10ページ | 39.20秒 | 35.1秒 | +4.10秒 |
| 高精度・密な縦書き5ページ | 100.36秒 | 約90秒 | 約+10秒 |
| 高精度・密な横書き5ページ | 94.55秒 | 約35秒 | 約+60秒 |

全件がHTTP 200で、OCRの上限内に収まりました。全リクエストの終了後に一時ファイルの削除を確認し、コンテナの `memory.events` の `oom` と `oom_kill` はともに0でした。

差が大きかった高精度の横書きについて、同じ1ページを300 dpiのPNGにして、同じCPU・メモリ制約とTesseractの384 MiB制限でPDF・TSV出力まで測りました。

| Tesseractの条件 | 1ページ |
| --- | ---: |
| 標準モデル・明朝・`psm 1` | 3.57秒 |
| 高精度モデル・明朝・`psm 1` | 16.19秒 |
| 高精度モデル・明朝・`psm 3` | 15.80秒 |
| 高精度モデル・明朝・`psm 1`・出力設定を設定ファイルで指定 | 16.36秒 |
| 高精度モデル・ゴシック・`psm 1`（同じ本文と配置） | 17.19秒 |

今回の試料では、高精度モデルのTesseract実行だけで5ページ約81秒を占めます。向き判定の有無と出力指定方法による差は各0.4秒以内でした。試作の入力そのものは使えなかったため、試作との差を生んだ入力条件は特定できていません。

レビューでは、上限の1,600万画素を使った密な縦書き1ページが、高精度のTesseract単体で34.1秒かかりました（1 CPU）。5ページ分の認識だけで約170秒と見積もられ、描画やPDF生成も含む180秒の上限に近づきます。また、密な横書き1ページの高精度認識は、Sauvolaありで13.99秒、なしで13.98秒でした。この0.01秒の差では、高精度の横書きが試作より遅かったことを説明できません。[PR #127 のレビュー](https://github.com/kooiei-in4a/amane-pdf-api/pull/127#issuecomment-7179161029)
