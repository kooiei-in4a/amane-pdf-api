# 処理制限の設定ガイド

このAPIはアップロード、PDF処理、一時ファイル、子プロセスを個別に制限します。ここでは利用者が変更できる範囲と、変更しても残る保護上限をまとめます。環境変数は.NET configurationの `Pdf` セクションに対応する `Pdf__...` 形式です。設定を省略した場合の値、API応答、エラー形式は従来どおりです。

## Issue #146 で広げた設定

| 設定 | 既定値 | 設定可能範囲 | 関連する保護・注意 |
| --- | ---: | --- | --- |
| `Pdf__MaxMergeFiles` | 10 | 2〜100 | 100件で反復検証・結合回数を有限に保つ。各ファイルは `MaxFileBytes` 以下、合計は既定50 MiBの `MaxMergeInputBytes` 以下。qpdfの処理期限と全体同時実行枠も適用。 |
| `Pdf__MaxImageInputBytes` | 50 MiB | 1〜100 MiB | 既存の画像job上限124 MiBとvalidatorにより、入力100 MiB、ファイル予約、補助領域を収容する。各画像には `MaxFileBytes`（既定50 MiB）も適用。入力上限と生成PDF上限は別で、`original` は生成ページPDF合計・最終PDF各54 MiB（`ImageOutputLimitBytes` の最大値）にも制限される。 |
| `Pdf__MaxOcrPages` | 10 | 1〜100 | 標準OCRの対象ページ数。1ページずつ処理し、描画枠・1ページ画素上限・総処理期限も維持。 |
| `Pdf__MaxOcrHighPages` | 5 | 1〜100 | 高精度OCRの対象ページ数。同じ共有枠・job容量・総処理期限を維持。 |
| `Pdf__OcrTimeoutSeconds` | 90秒 | 1〜`int.MaxValue / 1000` | アップロード後の標準OCR全体の期限。qpdfの個別コマンド期限とは異なる。 |
| `Pdf__OcrHighTimeoutSeconds` | 180秒 | 1〜`int.MaxValue / 1000` | アップロード後の高精度OCR全体の期限。 |
| `Pdf__MaxToImagesPages` | 50 | 1〜1,000 | overlayの既存対象ページ上限と揃える。ページ上限は30秒の既定期限内に完了する保証ではない。個別期限はなく、`QpdfTimeoutSeconds` を共有する。延長中は唯一のPDFium描画枠を占有し、黒塗り・OCR・比較などが503になる場合がある。最大1,600万画素/ページも維持。 |
| `Pdf__MaxToImagesZipBytes` | 64 MiB | 1〜120 MiB | 124 MiBの既存job容量内に置いたZIP固有の上限。ZIPと描画中のページ画像を含む実効容量は `RedactJobLimitBytes`（既定・最大124 MiB）でさらに制限する。 |
| `Pdf__OverlayOutputLimitBytes` | 62 MiB | 1〜512 MiB | overlaid PDFと最終PDFそれぞれの上限。512 MiBは1 fileの設定ガード上限で、4 KiB単位job台帳とqpdfのFSIZE上限も維持する。 |
| `Pdf__OverlayJobLimitBytes` | 124 MiB | 1〜512 MiB | 全入力・中間物・出力・FSIZE番兵を含む1要求の容量ガード。4 KiB割当で計上し、`MaxFileBytes` と印影画像用の余裕を満たす必要がある。 |

OCRのページ数を増やす場合は、処理に必要な時間に合わせてtimeoutも確認してください。OCRは1ページずつ処理しますが、既定の124 MiB job予算、ページあたり1,600万画素、Tesseractのメモリ上限、共有描画枠を維持します。

PDF→画像には専用timeoutがなく、処理全体に `QpdfTimeoutSeconds`（既定30秒）を使います。1,000ページの上限を設定しても、既定期限内に終わらなければ504になります。実測ではSite相当のCPU 1・RAM 1,536 MiB・tmpfs 256 MiB環境で、単純なページを72 dpiで描画した場合に400ページは16秒、1,000ページは30秒を超えました。処理時間はPDFと実行環境で変わります。timeoutを延ばすとPDF→画像が共有描画枠を長く占有し、黒塗り・OCR・比較などの要求がその間503になる場合があります。また `QpdfTimeoutSeconds` はPDF→画像以外のqpdf処理でも使う共有設定です。

from-imagesの入力上限を100 MiBにしても、生成ページPDF合計・最終PDFそれぞれの `ImageOutputLimitBytes` は最大54 MiBです。特に `quality=original` では入力画像を縮小しないため、入力合計が100 MiBに達する前に生成PDF上限へ達することがあります。その場合は422 `output-too-large` になります。画像の形式や破損を示す `unsupported-image` とは区別されます。

PDF→画像のZIP上限を上げても、入力PDF、qpdf metadata、ページ画像、ZIPが同時に使う領域をjob台帳で管理します。利用可能な容量は入力PDFの大きさにも依存します。ページ数とZIP容量の最大値を同時に利用できることは保証しません。

`OverlayJobLimitBytes` を増やすと、overlay・composeなどの1要求が占有できる一時領域も増えます。 `MaxFileBytes` を既定値より大きくする場合は、最低でも `OverlayJobLimitBytes >= round4KiB(MaxFileBytes)` が必要です。印影APIも起動時検証に含まれるため、既定の印影画像容量を保つ場合はさらに約2 MiBの余裕が必要です。生成metadata・描画JSON等の個別上限（最大8/32/40 MiB）も維持されます。

overlayの512 MiB上限は処理成功を実測・保証した値ではありません。1要求が予約できる一時領域の設定上限を有限にし、実サイズの書き込みは既存台帳とqpdfのファイルサイズ制限で止めます。既定の同時実行数2で最大設定を使うと最大1 GiB相当のjob予約が可能になり、同時実行数を増やせばその分も増えます。tmpfs、CPU、memoryと処理期限を配備先の値に合わせ、上限より十分小さい負荷で確認してから設定してください。

## 既存設定で調整できる制限

| 設定 | 既定値 | 現在の検証範囲・依存 |
| --- | ---: | --- |
| `Pdf__MaxFileBytes` | 50 MiB | 環境変数で変更可能。全体に共通する一律のバイト上限は置かないが、機能別job容量、設定間の整数overflow検証、フロント側のrequest上限が適用される。overlayで大きな入力を使うには `OverlayJobLimitBytes` など関連容量も必要。 |
| `Pdf__MaxMergeInputBytes` | 50 MiB | 正の値で設定可能。単一PDF上限とは別の合計上限。request上限はこの値+64 KiBで算出し、overflowする設定は起動時に拒否。 |
| `Pdf__MaxConcurrentProcesses` | 2 | 1以上。待ち行列は0で、上限超過時は503。設定を増やすとディスク・CPU・メモリの同時負荷も増える。 |
| `Pdf__QpdfTimeoutSeconds` | 30秒 | 1〜`int.MaxValue / 1000`。composeおよびPDF→画像を含む多くのqpdf処理の共有処理期限。アップロード時間は含めない。 |
| `Pdf__CleanJobLimitBytes` | 124 MiB | 正の値で設定可能。4 KiB単位の単一PDF入力割当以上。出力・JSON個別上限も別に適用。 |
| `Pdf__MaxSplitJobBytes` | 124 MiB | 正の値で設定可能。入力、ZIP、現在のパート、番兵を含む必要容量以上。ZIP32上限は別に残る。 |

値の詳細な検証条件は[READMEの設定一覧](../README.md#リソース制限と設定)と各capacity validatorを参照してください。

## 設定化しなかった保護上限

- 全PDF処理枠の待ち行列は0です。PDFium描画・OCR・比較が共有する描画枠は待ちなしの1枠に固定しています。描画子プロセスは最大384 MiBの `RLIMIT_DATA` と個別のFSIZEを使い、枠を増やすとメモリと同時一時領域が同時に増えるためです。
- 黒塗り・OCR・PDF→画像・比較が共有する `RedactJobLimitBytes` は124 MiB以下です。複数の出力・画像コピー・metadataが重なる実測済みジョブ予算であり、本Issueでは上限を広げていません。
- 画像からPDFへの生成は最大100枚、画像1枚50 MP、画像job124 MiB、生成PDF合計と最終PDF各54 MiBです。PNG見積り、pdfcpuのAS/soft limitおよび起動時自己テストも維持します。
- OCRは日本語モデル `jpn` / `jpn_vert` と `osd` に限ります。各ページの描画画素、TesseractのAS/FSIZE、語数・TSV制限も維持します。
- overlayの生成JSON・白紙PDF・描画レイヤー個別8 MiB、入力JSON 32 MiB、生成JSON 40 MiB、最大1,000ページ、フォーム・署名・添付の各構造制約は変更していません。
- mergeの総入力容量は既定50 MiBのままです。必要なら `MaxMergeInputBytes` と単一PDF上限を別々に設定します。qpdfには任意PDF出力全体を横断する共通FSIZE上限がないため、ディスク使用量は実行環境の `/tmp` / tmpfs とcontainer limitで管理してください。
- DPI、query値、ページ選択文字数、テンプレート項目、透かし・印影・差し込みの書式、compose protocol、OCR言語、OS/architectureは本Issueでは広げていません。

描画同時実行枠（1）と待ち行列0は固定しました。PDFium描画は最大384 MiBの `RLIMIT_DATA` とjob容量制限を使い、同時枠を増やすと同じ一時領域・メモリ負荷が重なるためです。一般qpdf処理へ共通の出力上限を追加する案も、endpoint別の契約・容量台帳をまたぐ大きな変更になるため見送っています。

## 配備時の保護

API単体は認証、利用者別quota、IP別rate limitを提供しません。外部へ直接公開する場合は、認証・rate limit・HTTP全体timeout・request/response上限をgateway側に設定してください。APIの `QpdfTimeoutSeconds` などの処理期限はアップロード後に始まり、低速な送信や受信の時間を制限しません。

CPU、container memory、swap、`TempRoot` の容量はDocker/Composeや実行環境で制限してください。job容量や同時実行数を上げる際は一時領域の上限も見直してください。例えば2件の同時要求がそれぞれ256 MiBのjobを予約するなら、tmpfsは最低512 MiBに加えて起動用ファイル等の余裕が必要です。容量台帳はAPIが作成・追跡するファイルを制限しますが、ホスト全体のmemory/RSS、一般qpdf処理のすべての生成物、処理成功を保証するものではありません。

以下は選択的な設定例です。どれも既定値を変えず、明示したAPI設定だけを変更します。

```yaml
environment:
  Pdf__MaxMergeFiles: "25"
  Pdf__MaxImageInputBytes: "104857600"
  Pdf__MaxOcrPages: "20"
  Pdf__OcrTimeoutSeconds: "180"
  Pdf__MaxToImagesPages: "100"
  Pdf__MaxToImagesZipBytes: "104857600"
```

この例も認証やcontainer resource limitsの代わりにはなりません。選択した値に合わせてgateway timeout、tmpfs、CPU、memory、外側の受け付け上限を設定します。

## amane-tools-siteとの責任分界と互換性

APIの既定値は変更していないため、現行Siteの既存設定を変えずに起動・利用できます。SiteはAPIとは独立してrequest容量、全体timeout、同時実行を適用します。Siteの現在の既定値は単一request約50 MiB、全体timeout600秒、全体同時2件・1 subjectあたり1件です。API側だけを拡張しても、Site経由の受付上限は増えません。サイトUI・中継・Compose、APIの各上限を揃える必要がある場合は、Site側の別変更が必要です。本IssueではSiteのコード、Compose、設定を変更しません。

比較APIには既存の境界差があります。PDF APIの比較子processは出力ファイルを8 MiBで制限します。一方、Siteの比較relayは成功JSONを2 MiBまでbufferし、それを超えると502を返す実装で、Site側には2 MiB + 1 byteを拒否する回帰テストがあります。APIと同じTextExtractorへWinAnsiの500,000文字差分を渡すと、`truncated=false` の正常なJSONが6,000,203 bytesになりました。API側にはこのfixtureをHTTP経由で確認するテストも追加しましたが、この作業環境では起動用PDF処理バイナリがなく未実行です。したがって、同じ入力はSite中継で502になると推測されます。relay上限との整合は残課題として扱い、Siteは変更しません。

## 既定設定での確認

変更後もoption defaultsは既存値を維持しています。amane-tools-siteの既存 Compose に設定を加えなくても、APIの起動validatorと元のHTTP上限・status/reason分岐を通ります。値を増やした場合も、起動時の設定整合性検証、4 KiB容量台帳、qpdf/Tesseract/PDFiumの子プロセス制限、キャンセル時のkill/wait/cleanupを維持します。
