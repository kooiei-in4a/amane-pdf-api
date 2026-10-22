# 共通helper・fixture・実行設定の監査

本文のテストだけでなく、何を実際に動かし、何を期待値にしているかを決める補助部分も確認した。全ファイルの同定用hashは[files.csv](files.csv)。ここはテスト件数に加算する一覧ではない。

## C#補助6ファイル

| ファイル | 役割と確認した境界 | 重複・本番との差 |
| --- | --- | --- |
| [PdfTestContext.cs](../../tests/Amane.Pdf.Api.Tests/PdfTestContext.cs) | 実ProgramのWebApplicationFactory、合成PDF作成、multipart、qpdf確認、log収集、残留job確認。 | 本文未読probeは有効multipartのHTTP要求と別にTestServerでread回数0と400を確認する。実Kestrel socketではない。Qpdf/Djpeg/Cjpeg/timeoutは起動後に差し替える故障注入。QpdfBytesAsyncでstdoutをraw byte回収し、QpdfAsyncはtext用ラッパーに限定。binary比較はbyte版へ移行済み（LV11）。AssertCleanは呼出時点の一時rootだけを観測する。 |
| [BlockingQpdf.cs](../../tests/Amane.Pdf.Api.Tests/BlockingQpdf.cs) | 特定stageで停止するshell、親子PID記録、停止確認、先頭引数の呼出履歴。 | 通常時は実qpdfへ渡す。第1引数の履歴は全argvの証拠には使わず、Cleanは独立wrapperの全argv/private jobとbudget検証へ統合済み（LV09）。sleepを使うのは段階ごとの取消を決定的に観測するため。 |
| [CompressFixtures.cs](../../tests/Amane.Pdf.Api.Tests/CompressFixtures.cs) | PDF object/stream、JPEG、色空間、共有画像・多頁等のfixture生成。 | qpdf/実JPEGで成立させるfixtureと候補判定用の人工構造を区別。有効ICCの真値はPython側で補う。 |
| [FromImagesFixtures.cs](../../tests/Amane.Pdf.Api.Tests/FromImagesFixtures.cs) | JPEG EXIF/scan、PNG chunk、metadata等の画像入力生成。 | ChunkのCRCが本番PngSanitizer.UpdateCrcに依存。構造解析用PNGの全ケースを有効なpacked/Adam7 pixel列として扱わない。独立zlib/Pillowと実pdfcpuで補完。 |
| [OverlayFixtures.cs](../../tests/Amane.Pdf.Api.Tests/OverlayFixtures.cs) | OverlayJob、実qpdf/pdfcpu/Builder、geometry/継承fixture、故障shell。 | HTTPを通らない直接builder試験。Metadataは本番PdfPageBoxesで読むため同じparserを両側に使う比較には相関がある。Job.Disposeはテスト所有者の後始末。 |
| [StampFixtures.cs](../../tests/Amane.Pdf.Api.Tests/StampFixtures.cs) | depth/color/palette/tRNS/alpha、packed sub-byteとAdam7のPNG、分割IDAT/末尾metadataを生成。 | 入力decoder用の画素fixture。CRCはFromImagesFixtures.Chunkを共有するため独立CRC oracleではない。Python/実合成後の画素も必要。 |

## 固定fixture18点

fixtureの有無だけでテストを網羅とみなさず、主な消費側と目的を対応させた。hash一致は内容の同定であり妥当性や安全性の証明ではない。

| fixture | 主な消費側・守る対象 | 用途上の注意 |
| --- | --- | --- |
| [sample.pdf](../../tests/Amane.Pdf.Api.Tests/Fixtures/sample.pdf) | PdfTestContext等の最小正常PDF。HTTP正常/異常条件を切り分ける共通入力。 | 実qpdfで妥当性を確認。これ一つで複雑PDFの保持を保証しない。 |
| [clean-source.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/clean-source.json) | PdfCleanTests、OverlayBuilderの複雑fixture、Docker検証。attachment/annotation/metadataの除去と通常要素保持。 | qpdf JSONから実PDF化する。known markerの消失を検知する目的。 |
| [fill-template-form-object-stream.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/fill-template-form-object-stream.json) | FillTemplateStructureTestsのobject stream内のform/signature検出。 | 実署名の成立はPython pyHanko fixtureが補完。 |
| [pdfcpu-blank.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/pdfcpu-blank.json) | docker-smoke.pdfcpu_smokeの実pdfcpu入力canvas。 | 本番起動自己テストが生成する同名の一時ファイルと固定fixtureを混同しない。 |
| [pdfcpu-layer.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/pdfcpu-layer.json) | docker-smoke.pdfcpu_smokeの日本語layer/実font描画。 | 直接CLIのfixture。HTTPで受け取るJSONではない。 |
| [png-working-vectors.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/png-working-vectors.json) | PngSanitizerTestsとPython driverのworking byte/admission共通境界。 | 両実装が同じ固定期待値と一致することを確認。実decode資源の較正は別測定。 |
| [redact-blank2.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-blank2.json) | PdfRedactTests、redact-validation.pyの共有layer/日本語font等の準備。 | 2page基礎fixtureと描画layerを組み合わせる。 |
| [redact-shared-layer.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-shared-layer.json) | PdfRedactTestsの共有embedded日本語font。 | 対象外pageへのrasterize拡張と元glyph data削除を確認する。 |
| [redact-markers.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-markers.json) | page/annotation/attachment/appearance等の既知秘密marker除去。 | ページを黒く描くだけでhidden contentが残る退行を防ぐ。 |
| [redact-needap.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-needap.json) | NeedAppearancesと非対象pageのフォーム表示生成。 | 対象外pageの見た目保持も必要な契約。 |
| [redact-prune-warning.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-prune-warning.json) | pruning警告をunsupportedとして返しPDFを送らない。 | 壊れた入力をわざと用いる拒否側fixture。 |
| [redact-neutral-profile.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-neutral-profile.json) | RedactNeutralObjectsTestsの限定neutral profile共有。 | neutral親を理由に子の本文まで許容しないことを周辺testで確認。 |
| [redact-dct-baseline90.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-dct-baseline90.json) | PdfRedactTests.RealEncodedImagesのbaseline DCT境界と実pixel描画。 | 寸法だけの偽imageでなく実encoded imageでdecoder到達を確認。 |
| [redact-dct-progressive90.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-dct-progressive90.json) | 同progressive DCTの許容閾値。 | baseline JPEGとdecoderの資源特性が異なるため一方だけに統合しない。 |
| [redact-jbig2-1000.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-jbig2-1000.json) | 同JBIG2の実decodeと閾値。 | 画像filter別の安全境界。 |
| [redact-jpx-gray30.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-jpx-gray30.json) | 同gray JPXの実decodeと閾値。 | 多channel JPXの枝と区別。 |
| [redact-jpx-rgba30.json](../../tests/Amane.Pdf.Api.Tests/Fixtures/redact-jpx-rgba30.json) | 同RGBA JPXの実decodeと閾値。 | alpha/色channelのworking差がある。 |
| [stamp-near-limit.png](../../tests/Amane.Pdf.Api.Tests/Fixtures/stamp-near-limit.png) | 手動stamp容量の実PNG負荷。既存stamp-image-validation.mdの再現資材。 | test csprojはPNGを通常出力へコピーしない。`stamp-image-validation.py --capacity --capacity-image ...`で明示指定する。通常CIでこのfixtureの最大負荷が走るわけではない。 |

## shell・C・設計probe・lock

| ファイル | 役割と評価 |
| --- | --- |
| [install-pdfcpu.sh](../../scripts/install-pdfcpu.sh) | 固定pdfcpu/font/licenseを取得しhash等を確認。CI/Docker依存の準備でありHTTP回帰テストではない。今回このinstaller自体の再取得は行わず既存imageの依存を利用。 |
| [install-pdfium.sh](../../scripts/install-pdfium.sh) | 固定PDFiumと必要ライブラリを準備し検証。依存の配置を守る処理。今回の再取得は未実施。 |
| [measure-tmpfs.c](../../scripts/measure-tmpfs.c) | ホストから対象tmpfsを高頻度で計測しpeakを記録するC補助。assertを持つAPIテストではなく、瞬間peakを取り逃しにくくする計測器。 |
| [redact reproduce.sh](../../docs/redact-design-probe/reproduce.sh) | 初期黒塗り設計の作成/更新/描画/観測を再現する履歴。現行production validatorを全て通さない。 |
| [redact renderer/Program.cs](../../docs/redact-design-probe/renderer/Program.cs) / [csproj](../../docs/redact-design-probe/renderer/Renderer.csproj) | PDFium P/Invokeの初期spike。現行renderer CLIの引数・native limit・silent failure契約と同じではない。MSTestに数えない。 |
| [stamp reproduce.sh](../../docs/stamp-image-design-probe/reproduce.sh) | 初期PNG/配置/合成probeの再現。現在の採用結果は別validationへ継承。 |
| [stamp docker-matrix.sh](../../docs/stamp-image-design-probe/docker-matrix.sh) | 過去pdfcpu版を含むmatrix比較。現行versionのCI実行とは区別。 |
| [IdatCheck.cs](../../docs/stamp-image-design-probe/IdatCheck.cs) | PNG IDAT decode可否を表示するprototype。表示結果の全てをassertする回帰テストではなく、現行PngDataValidator/StampImageInputTestsで補完済み。 |
| [Python requirements.in](../../scripts/fill-template-validation-requirements.in) / [requirements.txt](../../scripts/fill-template-validation-requirements.txt) | fontTools/MuPDF/署名等の検証用依存を固定。CIは--require-hashes/--only-binaryで導入。productionの依存ではない。 |
| [redact結果履歴](../../docs/redact-design-probe/results-2026-10-16.txt) | 当時の実験出力。現行の成功状態や今回の結果として流用しない。 |

Pythonの13 probeは[scripts.md](scripts.md)に個別記載した。特にgen_geometry.pyは現在のCIも使う現役fixture生成器なので、古いprobe群と一括して削除しない。

## 実行設定

| ファイル | 確認内容 |
| --- | --- |
| [test csproj](../../tests/Amane.Pdf.Api.Tests/Amane.Pdf.Api.Tests.csproj) | net10.0、MSTest adapter/framework、実API参照、rendererの先行build、PDF/JSON fixtureコピー、runsettingsの指定。 |
| [ci.runsettings](../../tests/ci.runsettings) | 2 workers・class単位並列。DoNotParallelizeの計測/プロセス状態変更を尊重。dotnetの-m:1はMSBuild数でありMSTest worker数を1にするものではない。 |
| [ci.yml](../../.github/workflows/ci.yml) | host build/testとDocker suiteの分離、JPEG実変換、依存/版、Python driver、独立描画・文字・署名・容量の呼出条件を確認。初回監査時はCI結果を照会せず、その後の成功記録は[ci.json](evidence/review-fixes/ci.json)に保存。再レビュー後は変異判定テスト9件もbuild-testで実行する。 |

独立した失敗要因を一つのfixtureに混ぜると、別の拒否で本来狙う検証を隠す。対応済みLV01/LV02の強化では「狙った一点だけ不正、他は正常」を入力fixtureにも適用する。
