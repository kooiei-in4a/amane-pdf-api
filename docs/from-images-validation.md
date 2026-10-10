# 画像からPDFの実装・検証記録

2026-10-10 UTC。[Issue #26](https://github.com/kooiei-in4a/amane-pdf-api/issues/26)の設計に基づく実装。API・README・テストが対象で、サイトの実装とdeployは別作業です。

## 実装と通常CI

Linuxの`POST /api/pdf/from-images?quality=standard|original`を追加しました。JPEGは両qualityともEXIFの8方向を係数変換で補正し、standardだけ縮小・quality 80の再圧縮を行います。PNGは画素寸法を維持し、許可chunkへの書き直しとpdfcpu importを行います。A4の向き・10 mm以上の余白は補正後の寸法から決定し、入力順に1画像1ページを生成します。

画像用djpeg・jpegtranのAS 384 MiB / maxmemory 320M、PNGの作業量見積り320 MiBを、下記の同時2件測定で確認しました。compressのAS 64 MiBは変更しません。1 job 124 MiB、生成ページ合計・最終PDFそれぞれ54 MiB、PNM 24 MiB、入力合計50 MiBの予算を実装しています。

.NETテストは、縮小率・端数・gray MCU・Exifのbyte order/境界/重複・PNGのCRC/構造/透過/palette/16 bit/interlace・qualityとfield・入力順・metadata・固定Problem Details・sentinel・部分出力の削除・異常終了・upload削除失敗・timeout/cancel/stopping・共通実行枠を確認します。容量ハーネスの通常ケースも小さな合成PNGで同じProcessorを実行します。

Dockerの通常CIには`scripts/from-images-validation.py IMAGE`を追加しました。Popplerは検証hostだけで使い、全8方向のJPEG両qualityを色付き領域の位置で照合し、qpdfのMediaBox・描画matrixを0.01 pt以内で照合します。JFIF density 1×1、元EXIF/末尾データの非残存、PNG SMaskと白地へのalpha合成も確認します。JPEG再圧縮・decoder色変換の差として、色付き領域の各channelは8/255以内を許可します。runtimeの追加パッケージはありません。

ローカルではRelease build（警告0）、既存APIを含む全745件と、その後追加したケースを含む関連31件のテストが成功しました。既存Docker E2Eも成功しています。PRのCI結果はGitHub Actionsで別途確認します。

## 手動Docker測定

固定版pdfcpu 0.16.1、qpdf 12.3.2、libjpeg-turbo 2.1.5、.NET runtime 10.0.12、Linux x86_64・4 KiB割当で実施しました。CPU 1、memory 1.5 GiB、swapなし、tmpfs /tmp 256 MiB、non-root、read-only、network none、cap-drop ALL、no-new-privilegesです。画像APIの設定は上書きしていません。

各条件に新しいコンテナを使用し、healthが起動してから、単独1要求と同時2要求の実HTTPを測りました。28組の入力/qualityについて56回のコンテナ測定、計84要求です。HTTP時間にはアップロードとPDF受信も含み、起動自己テスト・fixture生成・事後qpdf検査は含みません。時間は各条件1回の測定で、平均や任意の入力の保証ではありません。

| 入力 | quality | 同時2件の最大HTTP秒 | PDF MiB | cgroup peak MiB | tmpfs観測peak MiB |
| --- | --- | ---: | ---: | ---: | ---: |
| 24 MP baseline / progressive 4:2:0 | 両方 | 1.067 | standard 0.020 / original 0.135 | 192.14 | 20.03 |
| 48 MP baseline / progressive 4:2:0 | 両方 | 1.872 | standard 0.018 / original 0.274 | 418.17 | 18.93 |
| 48 MP progressive + Orientation 6 | 両方 | 2.096 | standard 0.018 / original 0.274 | 595.46 | 18.27 |
| 12 MP RGBA、4000×3000 / 3000×4000、低entropy | 両方 | 1.482 | 0.091 | 388.32 | 0.38 |
| 同、高entropy（入力45.796 MiB） | 両方 | 3.098 | 45.781 | 545.89 | 183.16 |
| optimized JPEG、入力49.053 MiB | original | 5.751 | 49.054 | 532.96 | 196.22 |
| 同、Orientation 6 | original | 5.573 | 48.955 | 599.82 | 196.02 |
| 12 MP RGBグラデーション | 両方 | 1.756 | 2.397 | 292.07 | 9.59 |
| 写真風PNG 2枚、入力27.674 MiB | 両方 | 8.696 | 47.090 | 506.17 | 188.38 |
| 写真風PNG 2枚 + グラデーション + JPEG、入力28.382 MiB | 両方 | 11.027 | 49.507 / 49.622 | 510.86 | 198.52 |
| 写真風PNG 3枚、入力41.519 MiB | 両方 | 10.925 | 422、部分PDFなし | 545.64 | 149.55 |

各行の時間・peakは同じ行の複数条件からの最大値です。成功ケースはすべて200・30秒以内、容量を超える組み合わせは422でした。全ケースでOOM/oom_killは0、残存jobと処理toolは0、処理後のhealthは200でした。FSIZE・CORE=0・ASの継承もプロセスから確認しています。jpegtranの最大観測RSSは48 MP回転時の約281.44 MiBで、384 MiBのAS内です。

24/48 MP JPEGは単色の合成画像です。約49 MiBのJPEGはNumPy seed 26の高entropy RGBをquality 100・4:4:4・optimizeで保存し、50 MiB以内へ高さを調整したものです。サイズ・係数保持の条件を確認するfixtureで、全JPEGの処理時間の上界を示すものではありません。

PNGの写真風fixtureはseed 2603のRGBノイズをGaussianBlur(2)でぼかしたもので、横/縦の12 MP画像です。単独の正規化PNGは13.845 / 13.830 MiB、ページPDFは23.496 / 23.594 MiBでした。PDFの画像はpredictorなしFlateとなり、最終PDFは入力合計の約1.7倍です。今回のグラデーションは0.350→2.397 MiB（約6.9倍）でした。Issueの前段probeとはfixtureが異なるため、9.1倍の値と直接比較しません。

写真風PNG 3枚の3ページ目では、残り約6.9 MiBのFSIZE予算でpdfcpuが通常エラー終了し、部分出力を削除しました。実サイズで容量超過を証明できないため、設計どおり`unsupported-image`で返しました。stderrから`output-too-large`へ推測しません。

## 容量台帳の実測

HTTP測定と別に、検証専用の.NETハーネスから同じProcessorをDocker内で単独実行して予約peakを取得しました。製品imageにハーネスは追加しません。callbackは内部methodの測定用で、本番endpointは指定せず、ログにも出しません。upload・正規化画像・各ページ・最終PDFの実bytesと、4 KiB丸め・FSIZE sentinel・補助1 MiBを含む予約peakを[JSON記録](from-images-capacity-results.json)に保存しています。

| ケース | 台帳予約peak MiB |
| --- | ---: |
| 12 MP RGBA高entropy | 100.801 |
| 49.053 MiB JPEG original | 104.059 |
| 写真風PNG 2枚 | 102.102 |
| PNG・JPEG混在 standard | 114.516 |
| PNG・JPEG混在 original | 104.859 |
| 写真風PNG 3枚（422） | 96.531 |

最大114.516 MiBは上限124 MiB以内です。最終結合前に画像中間物がなく、ページと最終出力の予算を確保できることも確認しました。予約peakは実際のファイル占有量とは異なり、まだ書いていない外部出力の最大予約を含みます。HTTP全測定の物理tmpfs観測peakは198.516 MiB、cgroup peakは599.824 MiBでした。

`tool_seconds_sampled`はhostから20 ms間隔で見えた各toolの稼働時間の和です。同時2件では重なった時間も加算し、CPU時間ではありません。短いプロセスの0は測定間隔で捕捉できなかった値であり、実処理0秒を示しません。cgroup peakはkernelの値、tmpfs・RSSはサンプリング値です。

## 再現手順

```bash
dotnet build --configuration Release
docker build -t amane-pdf-api:images .
python3 scripts/from-images-validation.py amane-pdf-api:images
sudo python3 scripts/from-images-validation.py amane-pdf-api:images --capacity \
  --fixtures /tmp/images-fixtures --output /tmp/images-results
sudo python3 scripts/from-images-validation.py amane-pdf-api:images --reservations \
  --fixtures /tmp/images-fixtures --output /tmp/images-results
```

hostにPillow・NumPy・qpdf・Popplerと.NET SDKが必要です。大fixtureと生の測定記録は`/tmp`にだけ置きます。`--cases` / `--qualities`で一部を選択でき、同じ結果dirへの再実行は同じキーだけ更新します。容量測定は通常CIには含めません。

## サイトと運用への引き継ぎ

[amane-tools-site #41](https://github.com/kooiei-in4a/amane-tools-site/issues/41)では、JPEG/PNG・1〜30枚・順序・qualityを案内し、50 MPがPNGすべての成功保証ではないことを明記してください。JPEGは両qualityで端が切り落とされること、ICC除去で色が変わり得ること、PNGはPDF化で大きくなるためJPEG化・縮小・枚数削減が有効なことも対象です。GIF/WebP/HEIC/APNGをサーバーで変換しません。

PNG/JPEG/EXIF・元ファイル名・path・tool出力はログ/Problem Detailsに出しません。日次枠の返却は既存方針に沿って400と確認済み422 `output-too-large`だけを対象とし、空画像や`unsupported-image`へ広げません。API deploy後にサイトを公開する順序を維持します。[#46](https://github.com/kooiei-in4a/amane-pdf-api/issues/46)のcollector確認とinfra容量条件の整合は別作業で、この実装・測定ではdeploy保留を解除しません。
