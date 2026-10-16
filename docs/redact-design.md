# 黒塗り（画像化方式）APIの実装設計

2026-10-16 JST。対象は [Issue #35](https://github.com/kooiei-in4a/amane-pdf-api/issues/35) の `POST /api/pdf/redact`。状態は**PR A（#66）で描画基盤を実装済み、PR BでAPIを実装**。PR Bの実測は [redact-validation.md](redact-validation.md) に記録する。フォントを共有するページの扱いは2026-10-16にHumanが案Aを承認した（§12.1）。

記述の区分:

- **【事実】** 基準commitのコード、または固定versionの実toolで確認したこと
- **【設計】** 採用する実装方針
- **【未検証】** 実装時に測定・確認が必要なこと

実測の手順と結果は [redact-design-probe/](redact-design-probe/) にある（`reproduce.sh` と `results-2026-10-16.txt`）。試作の描画programは設計の確認用で、実装ではない。

## 1. 調査の基準

| 項目 | 値 |
| --- | --- |
| 基準commit | main `43cb797`（PR #65 のmerge。#24 情報除去、#25 描画基盤、#29 電子印鑑まで含む） |
| 実tool | `amane-pdf-api:issue72-5d8b298`（`5d8b298`→`43cb797` の差分は `docs/roadmap.md` だけ）。qpdf 12.3.2、pdfcpu 0.16.1、libjpeg-turbo / libturbojpeg 2.1.5、.NET runtime 10.0.12、Ubuntu 26.04.1 |
| PDFium | bblanchon/pdfium-binaries `chromium/8100`（PDFium 157.0.8100.0、2026-10-12）。`pdfium-linux-x64.tgz` sha256 `1c1894a12d4d96ee01400c3874444126254180c5c7d2ab628576b67f8120320a`。`args.gn` は `pdf_enable_v8 = false`、`pdf_enable_xfa = false`、`pdf_use_partition_alloc = false` |
| 計測環境 | Docker、CPU 1、memory 512 MiB または 1.5 GiB、swapなし、network none |
| 読んだもの | Issue #35（本文・comment）、#33、amane-tools-site#47、`docs/roadmap.md`、`docs/security.md`、README、`docs/stamp-image-design.md`、`PdfEndpointHelpers`、`ExternalProcessRunner`、`ProcessMemoryLimits`、`QpdfProcessor`、`PdfcpuProcessor`、`PdfCleanEndpoint`、`CleanCapacity`、`PdfEmbeddedFileStripper`、`PdfPageBoxes`、`PdfOverlayBuilder`、`PdfStampImageEndpoint`、`MultipartPdfUpload`、`TemporaryPdfFiles`、`PdfOptions`、`Program`、`Dockerfile`、CI |

## 2. Issue本文（2026-10-07）からの変更点

| Issue本文 | この設計 | 理由（【事実】は§13） |
| --- | --- | --- |
| #33 の描画program（PDFtoImage 経由）を先に作る | 黒塗りが最初の利用者になる小さな描画programを作る。PDFiumを直接P/Invokeし、JPEGはimageに既にある libturbojpeg で作る。PDFtoImage と SkiaSharp は使わない | PDFtoImage 5.4.0 はPDFium 152固定（PDFiumは週1回更新、最新は157）。SkiaSharpはJPEGを作るためだけの大きなnative依存になる。必要なPDFium関数は17個 |
| （記載なし） | PDFiumは NuGet ではなく GitHub release の tgz を lock + sha256 で取得する | NuGetパッケージにはライセンス文がない。tgzには `LICENSE` と `licenses/` 17件がある。中の `libpdfium.so` はNuGetと同一 |
| （記載なし） | 描画programに BIZ UDPゴシック のTTFを渡し、起動時の自己テストで日本語が描けることを確かめる | フォントを渡さないと、埋め込みのない日本語の文字が**何も描かれずに消えた** |
| （記載なし。qpdfはAS制限） | 描画programには RLIMIT_AS ではなく RLIMIT_DATA（384 MiB）をかける | .NET は AS 1 GiB 以下では起動できず、2 GiB では画像が黙って省かれた。DATA 192 MiB で1600万画素を描けた |
| 手順6の `--remove-unreferenced-resources=yes` で未使用参照を除く | 刈り込みを独立した段にし、**終了コード0以外（警告の3を含む）は422で止める** | 対象外ページのcontentに構文エラーがあると、qpdfは警告を出して刈り込みをやめ、黒塗り対象ページのForm XObjectとフォントが出力に残った |
| `--empty` で組み直せば文書単位の情報は持ち越さない | 最終段でカタログを `/Type`・`/Pages` だけに、ページツリーの根を `/Type`・`/Kids`・`/Count` だけにする | ページラベル `/PageLabels` は `--empty --pages` でも引き継がれた |
| 置き換えページPDFを1枚ずつ作り、`--pages` で組み直し、注釈を除き、文書情報を除く（qpdfの書き込み5回） | 置き換えページ・注釈の除去・カタログの整理を、最終段の1つのupdate JSONで行う（書き込み3回） | update JSONで新しいobjectを作れ、参照されなくなった元のページは出力されなかった（目印なし） |
| 埋め込みファイルの除去と注釈の焼き込みを別の段で行う | 1つのjob（`updateFromJson` + `flattenAnnotations` + `removeAcroform` + `removeStructure`）にする | 2段に分けた場合と残る目印が同じだった |
| （記載なし） | jobに `generateAppearances` を加える | `/NeedAppearances true` の文書では、加えないとフォームの値が焼き込まれずに消えた |
| 黒塗りするページだけを画像にする | 黒塗りするページと**フォントを共有するページも画像にする**（2026-10-16 Human承認、§12.1） | 共有フォントの中に、黒塗りしたページだけで使われた文字（例: 「鈴」）が残った |
| （記載なし） | 描画前に、画像化するページの画像の大きさを検査し、大きすぎれば422 | PDFiumはメモリが足りないと画像を**黙って省く**。600dpiのプログレッシブJPEGは192 MiBで消え、256 MiBで描けた |
| 200dpiで描く | 200dpiを上限に、1ページ1600万画素に収まるようdpiを下げる（`page-too-large` は設けない） | A1以上の大きなページでも黒塗りできるようにする。dpiを下げても塗る範囲は画素単位で外側に丸める |
| `X-Pdf-Redacted-Pages` だけを返す | 加えて `X-Pdf-Rasterized-Pages`（画像にした全ページ）を返す | 黒塗りしていないのに画像になったページを、画面で説明するため |

## 3. 方式

### 3.1 処理の流れ

```text
request
 ├ 描画枠（1つ）を取る。取れなければ upload 前に 503
 ├ upload（file ≤ 30 MiB、regions ≤ 16 KiB）→ regions の形式検査（400）
 ├ 1. qpdf 入力検査（--is-encrypted / --check）            破損・暗号化は 422
 ├ 2. 入力の構造JSON（qpdf + pages）                        ページ数 → regions のページ番号検査（400）
 │                                                          埋め込みファイル除去の update JSON を作る
 ├ 3. job: 除去 + 外観の生成 + 注釈の焼き込み + フォーム・タグ構造の除去 → flat.pdf
 ├ 4. 刈り込み: --empty --remove-unreferenced-resources=yes --pages flat.pdf 1-z → pruned.pdf
 │                                                          終了コード0以外は 422 unsupported-pdf
 ├ 5. pruned.pdf の構造JSON → 画像化するページの決定（§6）・ページの大きさ・画像の大きさの検査
 │                                                          最終段の update JSON を書く
 ├ 6. 画像化する各ページを描画programで描き、範囲を黒で塗って JPEG に
 ├ 7. 最終段: qpdf pruned.pdf --update-from-json=final.json out.pdf
 ├ 8. 出力の検証（§7）。1つでも満たさなければ 500。黒塗りされていないPDFを返さない
 └ 200 application/pdf redacted.pdf、X-Pdf-Redacted-Pages、X-Pdf-Rasterized-Pages
```

### 3.2 なぜ漏れないか

- 画像化したページは、最終段で**新しく作ったpage object**に置き換える。そのページの辞書・content・Resourcesは許可した値だけで、元のページのobjectはどこからも参照されないため出力に書かれない（qpdfは到達できるobjectだけを書く）。
- 出力に残るのは、画像化しなかったページから到達できるobjectと、許可リストにしたカタログ・ページツリーだけ。
- 刈り込みは未使用のResources参照を除くが、ページの私的な参照は除かない。Tから到達する間接objectを役割付きで収集し、全到達位置が中立の役割かつ構造が許可リストに合うobjectだけを拒否集合から除外する。既定は本文として保護し、R以外のページが拒否集合を参照すれば422 `unsupported-pdf` にする。独立したobjectに複製された情報や、画像化しないページ自身の内容の除去は対象外。
- 共有画像・Formは、変換行列・クリップ・CropBoxによって対象外ページでは一部しか表示されないことがあるため、拒否を維持する。中立と認める共有ICC・ExtGState・ProcSet・OC・Function等は設定値として残せるが、その子の本文objectは保護する（§7.3）。この許可リスト方式は2026-10-17のHuman判断（Koo）。Rのフォント共有の式は変えない。
- 文書単位の情報（しおり、名前付き要素、添付、OpenAction、XMP、文書情報、ページラベル）は、`--empty` による組み直しとカタログの許可リストで持ち越さない。フォームとタグ構造は手順3で除き、ページの `/AF` と添付注釈も手順3で除く。注釈は、外観があるものは手順3で焼き込み、残ったものは最終段で全ページから除く。

## 4. 描画program `Amane.Pdf.Renderer`

### 4.1 構成【設計】

```text
src/Amane.Pdf.Renderer/
  Amane.Pdf.Renderer.csproj   net10.0 console、PackageReferenceなし、AllowUnsafeBlocks、InvariantGlobalization
  Program.cs                  引数の解析、render-jpeg / self-test
  Pdfium.cs                   P/Invoke（NativeLibrary.SetDllImportResolver で --pdfium の絶対pathだけを読む）
  TurboJpeg.cs                P/Invoke（libturbojpeg.so.0）
```

- PDFiumは信用できない入力を解析するnative codeなので、APIのprocess内では読み込まない。描画は1ページごとに別processで行い、timeout・cancelでkillする（`ExternalProcessRunner`）。
- 使うPDFium関数: `FPDF_InitLibraryWithConfig`、`FPDF_DestroyLibrary`、`FPDF_LoadDocument`、`FPDF_LoadMemDocument`（自己テスト用）、`FPDF_GetLastError`、`FPDF_GetPageCount`、`FPDF_LoadPage`、`FPDF_GetPageWidthF`、`FPDF_GetPageHeightF`、`FPDF_ClosePage`、`FPDF_CloseDocument`、`FPDFBitmap_Create`、`FPDFBitmap_FillRect`、`FPDFBitmap_GetBuffer`、`FPDFBitmap_GetStride`、`FPDFBitmap_Destroy`、`FPDF_RenderPageBitmap`。
- 使うTurboJPEG関数: `tjInitCompress`、`tjCompress2`（`TJPF_BGRX`、`TJSAMP_420`、品質85、flags 0 = baseline）、`tjFree`、`tjDestroy`。
- `FPDF_InitLibraryWithConfig` の version 2 で `m_pUserFontPaths = [--font-dir, NULL]` を渡す。PDFiumはV8・XFAなしのbuildを使うため、PDF内のJavaScriptは実行されない。

### 4.2 引数と終了コード【設計】

```text
Amane.Pdf.Renderer render-jpeg --pdfium <libpdfium.so> --font-dir <dir> --input <pdf> --page <1始まり>
    --expect-size <幅pt>,<高さpt> --width <px> --height <px> --quality 85 --max-bytes <n> --output <jpg>
    [--fill <左>,<上>,<右>,<下>]...      … 画素。0 ≤ 左 < 右 ≤ 幅、0 ≤ 上 < 下 ≤ 高さ、最大100個
Amane.Pdf.Renderer self-test --pdfium <libpdfium.so> --font-dir <dir>
```

処理（render-jpeg）:

1. 引数を検査する（path は絶対path、`--output` は存在しないこと、数値の範囲、`--fill` の範囲と個数）。
2. 文書とページを開き、`FPDF_GetPageWidthF/HeightF` が `--expect-size` と 0.01 pt 以内で一致することを確かめる。一致しなければ終了コード12。APIの `PdfPageBoxes` とPDFiumが同じ「表示上のページ」を見ていることの確認で、ずれたまま塗ると黒塗りの位置がずれるため。`--expect-size` は `DisplayWidth / UserUnit`（PDFiumはUserUnitを使わない。§13.1）。
3. `FPDFBitmap_Create(幅, 高さ, 0)` → 白で塗る → `FPDF_RenderPageBitmap(…, rotate 0, flags 0)`。
4. 各 `--fill` を `FPDFBitmap_FillRect(…, 0xFF000000)` で塗る。**塗る前の画像はファイルにもstdoutにも出さない。**
5. JPEGに圧縮し、`--max-bytes` を超えたら書かずに終了コード10。超えなければ `--output` を `CreateNew`・0600で作って書く。

| 終了コード | 意味 | APIの扱い |
| --- | --- | --- |
| 0 | 成功 | — |
| 2 | 引数の誤り | 500 |
| 3 | 文書を開けない（`FPDF_GetLastError`） | 422 `unsupported-pdf` |
| 4 | ページを開けない | 422 `unsupported-pdf` |
| 5 | bitmapを確保できない | 422 `too-complex` |
| 6 | JPEGの作成に失敗 | 500 |
| 10 | JPEGが `--max-bytes` を超えた | 422 `output-too-large` |
| 12 | ページの大きさが `--expect-size` と違う | 422 `unsupported-pdf` |
| 20 | 自己テストの失敗 | 起動失敗 |
| その他・signal | — | 500 |

self-test: programに埋め込んだ小さなPDF（埋め込みなしの `/MS-Mincho`・`/UniJIS-UCS2-H` で「日本語」、qpdfで作って定数にする）を `FPDF_LoadMemDocument` で開いて描き、文字の範囲に暗い画素が一定数以上あること、`FPDFBitmap_FillRect` の範囲がすべて黒であること、JPEGが作れることを確かめる。ファイルは書かない。

### 4.3 process の制限【設計】

```text
env --ignore-signal=XFSZ -- prlimit --core=0:0 --data=<384 MiB> --fsize=<max(max-bytes + 1, 8 MiB)> --
    <dotnet> <Amane.Pdf.Renderer.dll> render-jpeg ...
環境変数: DOTNET_EnableDiagnostics=0（診断用のsocketを作らない）。WorkingDirectory は job directory。
```

- `ProcessMemoryLimits.CreateRequest` は常に `--as` を付けるため、AS の代わりに DATA を指定できる引数を追加する。
- **PR Aの実装時の修正（2026-10-16）**: CoreCLRのW^X用memfdにより、起動前のFSIZEが1 MiB以下だとruntimeが起動できなかった（8 MiBでは成功）。起動時だけ最低8 MiBとし、`render-jpeg` 内でPDFiumのload・入力PDFのopenより前にFSIZEのsoft/hard両方を `max-bytes + 1` へ下げる。下げられなければ失敗する。W^Xは有効のまま。`self-test` は出力しないため起動用の8 MiBを使う。実測は [renderer-validation.md](renderer-validation.md)。§13の設計調査時の記録は変更しない。
- RLIMIT_DATA は書き込み可能な実メモリだけを数え、CoreCLRの予約（PROT_NONE）を数えないため .NET でも使える（§13.3）。
- 384 MiB は、16 Mpx のbitmap（64 MB）と600dpi A4のプログレッシブJPEG（256 MiBで描けた）に余裕を持たせた値。コンテナmemoryの見積り（README「1536 MiB ≥ 128 + 256 + 2 × 544 + 64」）では、描画は2つあるPDF処理枠の1つを使い、qpdfのAS 544 MiBの枠に収める。PR Bでは30 MiB/30ページと50 MiB結合同時実行を測定し、両処理の成功・OOMなしを確認した（[検証記録](redact-validation.md)）。
- 1 request内の描画は逐次。描画枠（§8.2）により、描画programが同時に2つ動くことはない。
- 描画programは stdout・stderr に何も書かない。runner は既定どおり両方を読み捨てる。APIのログは既存の固定文言だけで、ページ番号・範囲・ファイル名を出さない。

### 4.4 配布物【設計】

- `third_party/pdfium.lock`（`chromium/8100`、URL、sha256、取り出すmember）と `scripts/install-pdfium.sh`（`install-pdfcpu.sh` と同じ作り。download → sha256 → `lib/libpdfium.so` と `LICENSE`・`licenses/*` だけを取り出す）。
- `/opt/amane-pdf/lib/libpdfium.so`（0444）、`/opt/amane-pdf/renderer/`（`dotnet publish`、UseAppHost=false）、`/opt/amane-pdf/fonts/BIZUDPGothic-Regular.ttf`（`install-pdfcpu.sh` が既に検証しているTTFを、.gob化に加えてコピーする）、`/opt/amane-pdf/licenses/pdfium/`。
- `THIRD_PARTY_NOTICES.md` にPDFium（`licenses/pdfium.txt`。BSD-3-Clause と Apache-2.0）、pdfium-binariesのbuild script（MIT）、PDFiumに同梱される FreeType、libjpeg-turbo、OpenJPEG、lcms2、libpng、zlib、AGG、abseil、ICU、HarfBuzz、fast_float、simdutf、dragonbox、llvm-libc のライセンスを加える。
- PDFiumの更新: pdfium-binaries は週1回release される。lockの版と sha256 を上げるPRで更新し、CIの描画テスト（§10）で確認する。更新の追い方を README に書く。
- CI の host テストは `install-pdfium.sh` で同じ版を取得し、`Pdf__PdfiumPath` などを環境変数で渡す（pdfcpu と同じ扱い）。

### 4.5 将来の利用

#33（PDF→画像）と #36（OCR）は同じprogramに `render-png` などを足して使う。描画枠も共有する（§8.2）。

## 5. API側の設計

### 5.1 クラス【設計】

| 名前 | 種類 | 責務 |
| --- | --- | --- |
| `PdfRedactEndpoint` | 新規 | 描画枠の取得、`ExecuteAsync` の呼び出し（`maxRequestBytes: MaxRedactRequestBytes`）、応答ヘッダー |
| `RedactRegions` | 新規・純粋 | regions の解析と検証（§5.3）、ページごとのまとめ |
| `RedactRaster` | 新規・純粋 | ページの表示寸法から画素数を決め、割合を画素の矩形へ変換する（§5.4） |
| `RedactDocument` | 新規 | pruned.pdf の構造JSONから、ページ、フォントの共有、画像の大きさ、最終段の update JSON を扱う（§6、§7） |
| `PdfRedactProcessor` | 新規 | §3.1 の手順、容量の台帳、ファイルの削除 |
| `PdfRenderer` | 新規 | 描画programの起動（§4.3）、終了コードの変換、起動時の自己テスト |
| `RenderSlot` | 新規 singleton | `SemaphoreSlim(1)`。`Wait(0)` で取り、取れなければ503。#33・#36 と共有する |
| `PdfRedactException` | 新規 | reason（`too-complex` / `unsupported-pdf` / `output-too-large` / `too-many-pages`） |
| `MultipartPdfUpload.ReadFileAndTextAsync` | 変更 | field名、file上限、request上限を引数にする（watermark は `text`・既存の上限を渡す） |
| `CleanCapacity` / `CleanLimitedWriteStream` | 変更 | 上限を超えたときの例外を引数で受け取れるようにする（既定は今の `PdfCleanTooComplexException`） |
| `PdfPageBoxes` | 変更 | `RedactDocument` が同じ JsonDocument を使えるよう、object の取得と `Resolve` を internal にする（JSONを2回parseしない） |
| `ProcessMemoryLimits` | 変更 | AS の代わりに DATA を指定する経路 |
| `PdfEndpointHelpers` | 変更 | `PdfRedactException` の422 |
| `PdfOptions` / `Program` | 変更 | 設定、検証、endpoint（Linuxのみ）、`RenderSlot`、起動時の自己テスト |
| `TemporaryPdfFiles` | 変更 | 黒塗り用のpath（`redact-*.json`、`redact-flat.pdf`、`redact-pruned.pdf`、`redact-page-NNN.jpg`） |

### 5.2 multipart【設計】

- `file`（file part、30 MiB以下）と `regions`（file でない part、16 KiB以下）が1つずつ。順序は問わない。欠落・重複・未知のfield・`regions` がfile partなら400。
- request全体は `MaxRedactInputBytes + MultipartOverheadBytes`（30 MiB + 64 KiB）。`SizeLimitedReadStream` と `ExecuteAsync(maxRequestBytes:)` の両方に渡す（#29 の電子印鑑と同じ）。超過は413（既存の文言）。
- query は受け付けない（1つでもあれば400。情報除去と同じ）。

### 5.3 regions の検証【設計】

| 規則 | 違反 |
| --- | --- |
| UTF-8（不正なbyte列・BOMは不可）、JSON（`MaxDepth = 3`） | 400 |
| 根は配列、要素は1〜100個 | 400 |
| 各要素はobjectで、key は `page`・`x`・`y`・`width`・`height` の5つちょうど（大文字小文字を区別。`JsonDocument` は重複keyを許すので、名前の集合と個数を確かめる） | 400 |
| `page`: JSON number、raw text が `^[1-9][0-9]{0,9}$`、`int` に収まる | 400 |
| `x`・`y`・`width`・`height`: JSON number、raw text が `^[01](\.[0-9]{1,4})?$`、値が1以下（電子印鑑の query と同じ書式。指数・負号・5桁以上は不可） | 400 |
| `width > 0`、`height > 0`、`x + width ≤ 1`、`y + height ≤ 1`（decimal で計算） | 400 |
| 異なるページは20以下 | 400 |
| `page` ≤ ページ数（入力検査の後に判定） | 400 |

範囲の重なり・重複は許す。

### 5.4 画素への変換【設計】

- 表示寸法 `Wpt`・`Hpt` = `PdfPageAttributes.DisplayWidth / DisplayHeight`（CropBox ∩ MediaBox、Rotate 90/270 で入れ替え、UserUnit を掛ける。≤ 14,400 pt は既存の検査）。どちらかが1 pt 未満なら422 `unsupported-pdf`。
- 縮尺 `s = min(200 / 72, sqrt(16,000,000 / (Wpt × Hpt)), 65,535 / Wpt, 65,535 / Hpt)`。画素数 `W = max(1, floor(Wpt × s))`、`H = max(1, floor(Hpt × s))`。A4（595.28×841.89 pt）は1653×2338、A2は3307×4677（ここまで200dpi）、A1以上はdpiが200より下がる。
- 範囲: `左 = floor(x × W)`、`上 = floor(y × H)`、`右 = ceil((x + width) × W)`、`下 = ceil((y + height) × H)`（decimal）。外側へ丸めるので塗り残しは出ない。右・下は W・H で頭打ち。`width > 0` なので必ず `右 > 左`。
- 置き換えページの `MediaBox = [0 0 Wpt Hpt]`（小数点以下4桁に丸め、指数表記を使わない）。画像がページ全体を覆う。

## 6. 画像化するページの決定

### 6.1 フォントの共有【設計。2026-10-16 Human承認】

黒塗りを指定したページの集合を T とする。画像化するページの集合 R を次のとおりにする。

```text
R = T ∪ { q ∉ T | FontKeys(q) ∩ FontKeys(T) ≠ ∅ }      （|R| ≤ 30。超えたら 422 too-many-pages）
```

- `FontKeys(p)` は、pruned.pdf でページ p の content が使うフォントの識別子の集合。刈り込みの後に求める（刈り込み前は、共有のResources辞書や未使用の参照で全ページが共有と判定された。§13.6）。
- たどる範囲: ページの `/Resources` → `/Font` の各フォントと、`/ExtGState` → `/Font` 配列の先頭のフォント。`/XObject` のForm、`/Pattern` のtiling pattern、`/ExtGState` の `/SMask` の `/G`、Type3フォントの、それぞれの `/Resources` を再帰的にたどる（深さ32、訪問数100万で422 `too-complex`）。注釈は最終段で除くのでたどらない。
- 識別子: フォント辞書が間接objectならその参照、直接objectなら raw JSON の文字列。加えて、フォントから `/Parent` 以外でたどれるすべての間接object（`/FontDescriptor`、`/FontFile*`、`/ToUnicode`、`/DescendantFonts`、`/CIDSet`、`/CharProcs` の各stream など）の参照。別のフォント辞書が同じフォントprogramを共有する場合も検出する。
- 共有して画像化したページ（R − T）は黒塗りしないが、そのページの文字は検索できなくなる。その先の連鎖（R − T のページが別のページとフォントを共有する）は追わない。R − T のページの内容は黒塗りの対象ではないため。
- 画像の大きさの検査（§6.2）と画素への変換は R の全ページに行う。

採らなかった案B（R = T とし、フォントに文字の種類が残ることを説明する）は §12.1 を参照。

### 6.2 画像の大きさの検査【PR Bの実測で閾値を確定】

PDFiumはメモリが足りないと画像を黙って省き、ページの一部が白く欠けたまま成功する（§13.3）。黒塗りの安全性は損なわないが、利用者が気づかないまま欠けたページを受け取るのを避けるため、R の各ページからたどれる画像XObject（§6.1 と同じ範囲。画像の `/SMask`・`/Mask` も含む）を描画前に検査する。

| filter（最後のfilter） | 上限 | 根拠 |
| --- | --- | --- |
| `/DCTDecode` | 幅 × 高さ × 成分数 ≤ 90,000,000 | 1600万画素のページでは、元の120,000,000のprogressive画像がDATA 384 MiBで黒く欠け、768 MiBで正しく描けた。90,000,000のbaseline/progressive・Gray/CMYKを測定した |
| `/JPXDecode` | 幅 × 高さ × 成分数 ≤ 30,000,000 | 元の60,000,000ではDATA 384 MiBで省略され、768 MiBでは描けた。30,000,000のRGB・Gray・alpha付きを測定した |
| `/JBIG2Decode` | 幅 × 高さ ≤ 1,000,000,000 | 40000×25000のgeneric region画像をDATA 384 MiB・1600万画素のページで測定し、描けた |
| その他（Flateなど） | 上限なし | 行単位で処理された。40000×40000のFlate画像も約80 MiBで正しく描けた |

- 成分数は `/ColorSpace` から決める（Gray 1、RGB・Lab・CalRGB 3、CMYK 4、ICCBased は `/N`、Indexed・Separation 1、DeviceN は名前の数、`/ImageMask true` は1、不明・なしは4）。
- 超えたら422 `too-complex`。閾値ちょうどの実符号化画像がDATA 384 MiBで描け、寸法を1画素増やすとAPIが422になることをテストする。測定方法・memory.peak・限界は [検証記録](redact-validation.md)。任意の符号化・壊れたstreamの完全な描画を保証する閾値ではない。
- content内のinline image（`BI … ID … EI`）は構造JSONに現れないため検査できない。content streamの大きさに比例するため残余リスクとして記録する。

## 7. qpdf の各段

### 7.1 手順3: 除去と焼き込み（1つのjob）

```json
{"inputFile": "<input.pdf>", "outputFile": "<redact-flat.pdf>", "updateFromJson": "<redact-strip.json>",
 "flattenAnnotations": "all", "generateAppearances": "", "removeAcroform": "", "removeStructure": ""}
```

- `redact-strip.json` は `PdfEmbeddedFileStripper` が手順2の構造JSONから作る（変更がなければ `updateFromJson` を省く）。1つのjobでも添付・`/AF`・添付注釈は消えた（§13.7）。PR Bでは黒塗りしないページに外観付き添付注釈を置き、その外観も目印も残らないことを実toolテストで確認する。
- 終了コード0と3を受け付ける。3（警告）は焼き込めなかった注釈がある場合で、それらは最終段で除く（見た目が消えるだけで、情報は残らない）。2は500（入力は手順1で検査済み）。
- 出力は `RunCleanWriteAsync` と同じ FSIZE の扱いにし、上限を超えたら422 `too-complex`。

### 7.2 手順4: 刈り込み

```text
qpdf --empty --remove-unreferenced-resources=yes --pages <redact-flat.pdf> 1-z -- <redact-pruned.pdf>
```

- **終了コードは0だけを受け付ける。** 部分出力のサイズによらず、非0は422 `unsupported-pdf`。3も422 `unsupported-pdf`（「このPDFは、黒塗りで安全に処理できない構造を含んでいます。」）。content の構文エラーで刈り込みが行われず、未使用の参照が残るため（§13.6）。stderrは読まない。
- この段で文書単位の情報（しおり、名前付き要素、添付の一覧、OpenAction、XMP、文書情報）はなくなる。ページラベルは残るので最終段で除く。

### 7.3 手順5: 最終段の update JSON

`RedactDocument` が pruned.pdf の構造JSON（`--json=2 --json-key=qpdf --json-key=pages --json-stream-data=none --decode-level=none`、32 MiB以下）から書く。

- 前提の確認: ページツリーの根の `/Kids` が全ページを直接持ち、各ページの `/Parent` が根であること、ページ数が入力と同じであること。違えば500（`--pages` の出力は平らになる）。
- Tのページ辞書（`/Parent`を除く）と実効Resourcesから、到達位置ごとの役割を子へ渡して収集する。同じ参照でも別の役割なら探索を省かない。全到達を集めた後で分類する2段階とし、すべての位置が中立の役割で、各役割の構造条件も満たす場合だけ中立とする。1つでも本文の位置があれば拒否集合に入れる。
- ResourcesのExtGState/ColorSpace/Propertiesの各entry、ProcSet、画像のColorSpace、ICCBasedのprofile、Indexed/Patternのbase、Separation/DeviceNのalternateとtint transform、OC/OCMDのOCGs、ExtGStateのTR/TR2/BG/BG2/UCR/UCR2、FunctionのFunctionsの各要素へ役割を渡す。それ以外は本文。名前から設定objectと推測して免除しない。
- 中立objectの子もたどり続ける。ExtGStateのFont・SMask/G、ICCのMetadata等の本文への参照は拒否集合へ入る。Shading、tiling pattern、Halftone、XObject、Thumb、PieceInfo、Metadata、注釈、未知の辞書・stream、Resourcesや各map自体の共有は免除しない。
- R以外のページの、削除する `/Parent`・`/Annots` 以外の値から任意の参照をたどる。中立objectを通過してもその子を検査し、拒否集合または従来どおりRの元page/content・Tのフォント配下へ到達すれば422 `unsupported-pdf`。書き換える実際のcatalog/rootと別途検査する実際のページを境界にする。深さ32・訪問100万は§6の検査と共有する。

中立の構造の許可リスト（共有設定を許可する方針はKooの判断、構造の表はClaudeがPDF 2.0から作成）:

| 役割 | 構造の条件 |
| --- | --- |
| ExtGState | 非stream辞書。Typeなし、またはExtGState。keyはPDF 2.0標準のExtGState keyのみ（HTは除外。拡張keyも除外） |
| ProcSet | 名前だけの配列。参照を含めば中立にしない |
| ColorSpace | ICCBased / Indexed / Separation / DeviceN / Pattern / CalGray / CalRGB / Labで始まる所定長の配列。Indexed lookupは文字列のみ。DeviceN属性辞書は直接辞書のみ |
| ICC | stream。N必須。key ⊆ {N, Alternate, Range, Metadata, Length, Filter, DecodeParms}。Type/Subtypeなし |
| OC | Type=OCG、key ⊆ {Type, Name, Intent, Usage}、またはType=OCMD、key ⊆ {Type, OCGs, P, VE} |
| Function | FunctionTypeは整数の0/2/3/4、Domain必須。Type 0/4はstream、Type 2/3は辞書またはstream。key ⊆ {FunctionType, Domain, Range, Size, BitsPerSample, Order, Encode, Decode, C0, C1, N, Functions, Bounds, Length, Filter, DecodeParms}。Subtypeなし |

ExtGStateのkey一覧は [PDF Associationの仕様由来モデル（Table 57）](https://github.com/pdf-association/arlington-pdf-model/blob/master/tsv/latest/GraphicsStateParameter.tsv) と照合した。非推奨のHTP/TR/TR2を含み、HTとベンダー拡張は含めない。中立の設定値（OCGのName、ICC内の記述文字列、Functionのサンプル値）は残ることを受け入れる。これらは文書全体で共有する設定値として扱い、黒塗り範囲の本文とは区別する。

既知の制限: TikZ/pgf型の文書に見られるような、ExtGState等の間接map辞書そのものの共有が刈り込み後も残る場合は422になる。この構造を模した合成PDFで確認した。共有mapを受け付ける範囲の拡大は今後の改善候補とし、今回の許可リストには追加しない。

- カタログ: `{"/Type": "/Catalog", "/Pages": <根>}` に置き換える。
- ページツリーの根: `{"/Type": "/Pages", "/Kids": [...], "/Count": n}` に置き換える。R のページは新しいpageの参照に差し替える。
- R の各ページに、新しいobject（番号は構造JSONの `maxobjectid` の次から）を3つ作る。
  - page: `/Type /Page`、`/Parent <根>`、`/MediaBox [0 0 Wpt Hpt]`、`/Resources << /XObject << /Im0 <画像> >> >>`、`/Contents <content>`
  - content: `q Wpt 0 0 Hpt 0 0 cm /Im0 Do Q`（base64の `data`）
  - 画像: `/Type /XObject /Subtype /Image /Width W /Height H /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode`、`datafile` に job 内のJPEGの絶対path
- R 以外のページで `/Annots` を持つものは、`/Annots` を除いた辞書全体を書く。
- 書いた後に構造JSONを削除し、JsonDocument を破棄する。

### 7.4 手順7: 最終段

```text
qpdf <redact-pruned.pdf> --update-from-json=<redact-final.json> <output.pdf>
```

終了コード0だけを受け付ける。FSIZE は `min(RedactOutputLimitBytes, 台帳の残り)`。超えたら422 `output-too-large`。

### 7.5 出力の検証（手順8）【設計】

すべて満たさなければ `InvalidOperationException`（500）。

1. `ValidateAsync(output)`（`--is-encrypted` が2、`--check` が0）。
2. 構造JSON（`--json-key=qpdf --json-key=pages --json-key=attachments`、32 MiB以下）で:
   - trailer の key ⊆ `{/Root, /Size, /ID}`
   - カタログの key = `{/Type, /Pages}`、ページツリーの根の key ⊆ `{/Type, /Kids, /Count}`、ページ数が入力と同じ
   - どのページにも `/Annots` がなく、ページ一覧外の `/Type /Page` objectもない
   - R の各ページ: key = `{/Type, /Parent, /MediaBox, /Resources, /Contents}`、`/Resources` = `{/XObject: {/Im0: 参照}}`、`/Contents` は1つのstreamの参照、`/Im0` の辞書の key ⊆ `{/Type, /Subtype, /Width, /Height, /ColorSpace, /BitsPerComponent, /Filter, /Length}` で値が書いたとおり
   - `PdfEmbeddedFileStripper.Verify(document, 0)`（`/AF`・`/EF`・`/RF` がない、`/Type /EmbeddedFile` のstreamがない、添付の一覧が空、XMPと文書情報がない）
3. R の content stream の中身: `qpdf --json=2 --json-key=qpdf --json-object=<参照>… --json-stream-data=inline --decode-level=generalized`（stdout 64 KiB以下）で取り出し、書いたbytesと一致すること。

検証は構造の検査だけで行う。出力全体のQDF展開と目印の検索はテストだけで行う（2026-10-07の判断のとおり）。

## 8. リソース制限

### 8.1 設定（`Pdf` section）【設計】

| 設定 | 初期値 | 説明 |
| --- | --- | --- |
| `MaxRedactInputBytes` | 30 MiB | 入力PDF |
| `MaxRedactRequestBytes`（計算） | 30 MiB + 64 KiB | request全体 |
| `MaxRedactReplacementBytes` | 30 MiB | JPEGの合計。超えたら422 `output-too-large` |
| `RedactOutputLimitBytes` | 62 MiB | 出力PDF |
| `RedactJsonLimitBytes` | 32 MiB | 各構造JSON・update JSON |
| `RedactJobLimitBytes` | 124 MiB | job全体の台帳（tmpfs 256 MiB ≥ 2 × 124 MiB + 8 MiB の既存の式に合わせる） |
| `RenderDataLimitBytes` | 384 MiB | 描画programの RLIMIT_DATA |
| `DotnetPath` / `RendererPath` / `PdfiumPath` / `RenderFontDir` | `/usr/bin/dotnet`、`/opt/amane-pdf/renderer/Amane.Pdf.Renderer.dll`、`/opt/amane-pdf/lib/libpdfium.so`、`/opt/amane-pdf/fonts` | 絶対path |

容量・DATAの数値設定は正で標準値以下、jobは4 KiB切上げ後の入力上限以上であることを起動時に検証する。

定数（設定にしない）: 範囲100個、黒塗りするページ20、画像化するページ30、regions 16 KiB、200dpi、1600万画素、JPEG品質85、§6.2 の画像の上限。

### 8.2 同時実行と時間

- 既存の `pdf` 同時実行枠（2）に加え、描画枠（1）を request 全体で持つ。描画枠は upload の前に取り、取れなければ503（既存の「PDF処理の同時実行上限に達しました。」）。
- 処理時間はupload後から既存の30秒を各段で共有する（`ExecuteAsync`）。実測は12ページで約2.7秒、描画は1ページ0.15〜0.45秒、起動は約50 ms（§13.2）。30ページでも15秒程度の見込み。

### 8.3 容量の台帳とファイルの削除

`CleanCapacity` を流用し、作る前に予約、作った後に実寸を計上、不要になった時点で削除する。

| 時点 | 置かれているもの |
| --- | --- |
| 手順2 | input、入力の構造JSON、strip JSON |
| 手順3 | input、strip JSON、flat（作成後に input・strip・job JSON を削除） |
| 手順4 | flat、pruned（作成後に flat を削除） |
| 手順5 | pruned、構造JSON、final JSON（作成後に構造JSONを削除） |
| 手順6 | pruned、final JSON、JPEG（合計30 MiB以下） |
| 手順7 | pruned、final JSON、JPEG、output（作成後に pruned・final JSON・JPEG を削除） |
| 手順8 | output、出力の構造JSON（検証後に削除） |

- 最も多いのは手順7で、30 MiBの入力なら約 30 + 1 + 30 + 60 MiB。台帳で予約できなければ、手順6・7では422 `output-too-large`、それ以外では422 `too-complex`。
- 成功・失敗・timeout・cancel のすべてで job directory 全体を削除する（既存の `TemporaryPdfFiles`）。塗ったJPEGもここに含まれる。

## 9. エラー

| HTTP | reason | 条件 | 文言（title） |
| --- | --- | --- | --- |
| 400 | — | regions・multipart・query の誤り、ページ番号がページ数を超える | 既存（「リクエストの形式が不正です。」等） |
| 413 | — | 入力30 MiB超、request 30 MiB + 64 KiB超 | 既存 |
| 422 | なし | 入力の破損・暗号化 | 既存 |
| 422 | `too-complex` | JSON・object数・台帳の上限、§6.2 の画像の大きさ、bitmapを確保できない | このPDFは黒塗りの処理上限を超えています。 |
| 422 | `unsupported-pdf` | 刈り込みの警告、PDFiumで開けない、ページの大きさの不一致、ページ属性（`PdfPageBoxes` の例外もここへ変換）、1 pt 未満のページ、残るページから拒否集合への参照（共有画像・Form・私的参照。中立の許可リスト外は既定で拒否） | このPDFは、黒塗りで安全に処理できない構造を含んでいます。 |
| 422 | `output-too-large` | JPEGの合計30 MiB超、JPEGが1枚で残りを超えた、出力の上限 | 黒塗りしたページの画像が大きくなりすぎるため、処理できませんでした。黒塗りするページを減らしてください。 |
| 422 | `too-many-pages` | 画像化するページが30を超える | 黒塗りするページとフォントを共有するページも画像にする必要があり、上限の30ページを超えました。PDFを分割してから黒塗りしてください。 |
| 500 | — | 出力の検証の失敗、想定外の終了コード、描画programの起動失敗 | 既存 |
| 503 | — | 描画枠・`pdf` 枠が使用中 | 既存 |
| 504 | — | 30秒を超えた | 既存 |

成功時: `200`、`application/pdf`、`attachment; filename=redacted.pdf`、`X-Pdf-Redacted-Pages: 1,3`（T、昇順）、`X-Pdf-Rasterized-Pages: 1,2,3`（R、昇順）。

## 10. テストと完了条件

### 10.1 単体（tool不要）

- `RedactRegions`: §5.3 の各規則の400（JSON不正、BOM、深さ、空配列、101個、未知key、key不足、重複key、`"page": 1.0`・`01`・`2147483648`、`x` の `1e-1`・`-0`・`.5`・`0.12345`・`1.0001`、`width: 0`、`x + width > 1`、21ページ、16 KiB超）、成功する境界（`1.0000`、`x + width = 1`、100個、20ページ）。
- `RedactRaster`: A4・A2・A1・極端に細長いページの画素数、1600万画素を超えないこと、外側への丸め（整数ちょうどの境界を含む）、`MediaBox` の書式に指数が出ないこと。
- `RedactDocument`（qpdfが書いたJSONの固定fixture）: フォントの共有の判定（間接・直接のフォント、別のフォント辞書が同じ `/FontFile2` を持つ、Form・Pattern・SMaskの `/G`・Type3 の中のフォント、共有なし、ExtGStateのFont）、共有画像・フォントなしFormの切り抜きと、Resources/Thumb/mask/私的参照の拒否、中立の役割・構造・全到達位置の照合と役割衝突（ICCをXObjectとしても使う等）、中立objectの子の本文の拒否、§6.2 の画像の検査、update JSON の内容。
- 中立Function: FunctionTypeの整数0/2/3/4とDomainを必須とし、Type 0/4はstreamだけを認める。必須key不足、未対応の型、小数・文字列の型、Type 0/4の非stream辞書を拒否する。

### 10.2 実tool（host の qpdf・PDFium）

- 描画program: Rotate 0/90/180/270・CropBox のずれ・CropBox が MediaBox からはみ出す・UserUnit 2 のページで、四隅の色と塗った範囲が期待どおり（試作の `check_geom.py` と同じ方法）。`--expect-size` の不一致で12。埋め込みなしの日本語が描ける（暗い画素が一定数以上）。`--max-bytes` 超過で10、出力ファイルなし。各終了コード。
- 目印の検査: 試作の `gen_markers.py` の構成（本文、Form XObject とその未使用参照・入れ子のFormの未使用参照、フォームの値・名前・外観、添付注釈、外観のないリンク、ページと文書の `/AF`、文書の添付、しおり、名前付き要素、タグ構造の `/ActualText`・`/Alt`、content内の `/ActualText`、`/Properties`、`/PieceInfo`、ページラベル、OpenAction、XMP、文書情報）で、出力をQDF展開してどの目印も残らず、対象外のページの文字（`PUBLIC_*`）が残ること。
- 外観（paperclipなど）を持つ添付注釈を、黒塗りしないページに置いた文書で、添付の目印が出力に残らないこと（§7.1）。
- 共有フォント: pdfcpu で作った2ページの文書（§13.5）で、2ページ目も画像になり、出力に「鈴」などが残らないこと。
- 刈り込みの警告: 対象外のページのcontentに構文エラーがある文書で422 `unsupported-pdf`。
- `/NeedAppearances true` のフォームの値が、対象外のページで見た目として残ること。
- §6.2 の画像の上限ちょうど・超え（DCTのbaselineとプログレッシブ、JPX、JBIG2）。
- 出力の検証の失敗（最終段の update JSON をわざと壊すテスト用の差し替え）で500になり、PDFを返さないこと。

### 10.3 HTTP（WebApplicationFactory）

- 成功（ヘッダー、ファイル名）、400 の各種、413（file・request）、422 の各reason、503（描画枠を保持したまま2件目）、504、cancel。
- 成功・失敗・timeout・cancel で job directory が残らないこと。描画中にtimeoutさせ、描画programが残らないこと。

### 10.4 Docker

- 回転・CropBox の4ページを黒塗りし、Popplerで描いて範囲が黒いことを確かめ、PRに画像を載せる（CIのdocker job。Popplerは検証側だけで使い、imageには入れない）。
- 起動時の自己テストが通ること。フォントdirを空にした設定で起動が失敗すること。
- smoke test に代表的な黒塗りを1件加える。

### 10.5 容量・時間の測定（手動、PRに記録）

CPU 1・memory 1.5 GiB・swapなし・tmpfs 256 MiB・read-only・network none で:

- 30 MiBの入力で20ページを黒塗りし、フォントの共有で30ページを画像化する。時間、`memory.peak`、tmpfs の最大。
- それと同時に50 MiBの結合を実行し、OOM・ENOSPC が起きないこと。
- §6.2 の閾値の画像を描いたときの描画programの `memory.peak`。

### 10.6 文書

- README「PDF黒塗り（Linux）」: API、方式、文字検索ができなくなること、全ページの注釈・リンク・フォームが見た目だけになるかなくなること、しおり・タグ構造・文書情報・添付・ページラベルがなくなること、フォントを共有するページも画像になること、上限、エラー、描画programとPDFiumの更新の追い方。
- `docs/security.md`「黒塗り（画像化方式、Linux）」と「描画program（PDFium）」: §3.2 の理由、検証の内容、process の制限、残余リスク（inline image、PDFiumの黙った省略）。
- `THIRD_PARTY_NOTICES.md`（§4.4）。`docs/roadmap.md` の技術選定の表（PDFiumの使い方）。

## 11. 変更ファイルとPRの分け方【設計】

| PR | 内容 |
| --- | --- |
| A（#35 の前半。#33 の土台） | `src/Amane.Pdf.Renderer`、`third_party/pdfium.lock`、`scripts/install-pdfium.sh`、`install-pdfcpu.sh`（TTFの配置）、Dockerfile、CI、`PdfRenderer`、`RenderSlot`、`ProcessMemoryLimits` の DATA 経路、起動時の自己テスト、§10.2 の描画programのテスト、THIRD_PARTY_NOTICES・README・security の描画programの部分。公開endpointはない |
| B（#35 の後半） | endpoint、`RedactRegions`、`RedactRaster`、`RedactDocument`、`PdfRedactProcessor`、multipart・台帳・`PdfPageBoxes` の変更、エラー、§10 の残り、文書 |

#33（PDF→画像）は A の上に後で載せる。Issue #33 の「PDFtoImage 経由」の記述は、A のmerge時に更新する。

## 12. 判断

### 12.1 Humanの判断（2026-10-16、案Aを承認）

**黒塗りしたページと同じフォントを他のページが使っている場合の扱い**

PDFのフォントは、文書全体で使った文字の形と「どの文字か」の対応表を1つにまとめて持つことが多い（Wordなどで作ったPDF）。黒塗りしたページを画像に置き換えても、同じフォントを他のページが使っていると、そのフォントは出力に残る。その中に、黒塗りしたページだけで使われていた文字が残る。実測では「鈴木一郎 個人番号 9876」を黒塗りしたページと同じフォントを2ページ目が使っており、出力のフォントに「鈴」「一」「郎」「人」「7」などが残った（§13.5）。位置・順番・文章は残らず、「どの文字が使われたか」だけが残る。スキャンした画像だけのPDFでは起きない。

| 案 | 内容 | 失うもの |
| --- | --- | --- |
| **A（採用）** | そのフォントを使う他のページも画像にする。残らない | 画像になるページが増える（Wordで作った文書では多くの場合、全ページ）。そのページの文字検索・コピーができなくなる。画像にするページは30まで（超えたら422で分割を案内） |
| B | 指定したページだけを画像にし、文字の種類が残ることを画面とREADMEで説明する | 名前などに珍しい漢字があると、その字が文書にあったことは分かる |

Claudeの推奨はA（2026-10-07のHuman決定「見た目だけ黒い事故が仕組み上起きない方式に限る」に合う）。**2026-10-16、HumanがAを承認した。** サイト（amane-tools-site#47）に `X-Pdf-Rasterized-Pages` の中継、「黒塗りしていないページも画像になった」旨の表示、`too-many-pages` の文言と日次回数の扱いを加える必要がある（#47 の更新で行う）。

**追加のHuman判断（Koo、2026-10-17）**: 本文を持たないICC・ExtGState・ProcSet・OCG等の共有objectは、§7.3の役割と構造の許可リストで認める。共有画像・Form・私的参照の漏洩拒否とRの式は維持する。許可リスト外は既定で本文として拒否する。OCGのName、ICC内の記述文字列、Functionのサンプル値の残存を設定値として受け入れる。Chrome・Word・InDesign・スキャナ等の実文書で受付改善率を測った判断ではなく、合成PDFで検証する方針。

### 12.2 Claudeが決めたこと（Human未確認。異議がなければこのまま）

1. PDFiumを直接使い、PDFtoImage・SkiaSharpを使わない（§2、§4.1）。
2. PDFiumはGitHub releaseのtgzを lock・sha256 で取得し、ライセンス文を同梱する（§4.4）。
3. 描画programに BIZ UDPゴシック を渡し、起動時に日本語が描けることを確かめる（§4.2）。
4. 描画programは RLIMIT_DATA 384 MiB で制限する（§4.3）。
5. 刈り込みの警告は422で止める（§7.2）。
6. カタログとページツリーの根を許可リストにする（§7.3）。
7. 置き換えを最終段の update JSON で行い、qpdfの書き込みを3回にする（§7.3）。
8. `generateAppearances` を加える（§7.1）。
9. 大きなページはdpiを下げて描き、`page-too-large` を設けない（§5.4）。
10. 画像の大きさの事前検査（§6.2）と、PDFiumのページ寸法の照合（§4.2）を行う。
11. PRをA（描画基盤）とB（黒塗りAPI）に分け、#33 より前に A を作る（§11）。

## 13. 実測の記録（2026-10-16）

手順は `redact-design-probe/reproduce.sh`、出力は `redact-design-probe/results-2026-10-16.txt`。

### 13.1 PDFiumのページの向き・大きさ【事実】

- `FPDF_GetPageWidthF/HeightF` は Rotate 90/270 で幅と高さを入れ替え、CropBox ∩ MediaBox の大きさを返した（CropBox `[-50 -50 300 400]`・MediaBox `[0 0 600 800]` で 300×400）。UserUnit 2 のページは UserUnit を掛けない大きさ（400×300）を返した。
- 四隅を赤・緑・青・黄に塗ったページを描いた結果、表示上の左上は Rotate 0 で青（CropBox の左上）、90 で赤（左下）、180 で緑（右下）、270 で黄（右上）。UserUnit 2・Rotate 90 のページも 90 と同じ。CropBox がずれたページ、はみ出したページも四隅が一致した。
- 割合 (0.4, 0.3, 0.2, 0.1) を floor / ceil で画素にして塗った範囲の内側はすべて黒、2〜3画素外側は元の灰色だった（6種類のページすべて）。

### 13.2 時間・メモリ（CPU 1、memory 512 MiB）【事実】

| ページ | 画素 | 全体 | 描画 | JPEG化 | memory.peak | JPEG |
| --- | --- | --- | --- | --- | --- | --- |
| 文字だけのA4 | 1654×2339 | 50 ms | 0 ms | 10 ms | 22 MiB | 74,621 B |
| 300dpiスキャン（baseline JPEG）のA4 | 1654×2339 | 152 ms | 93 ms | 15 ms | 66 MiB | 879,371 B |
| 図形の多いA4（矩形4万・線2万） | 1654×2339 | 208 ms | 97 ms | 24 ms | 48 MiB | 2,694,027 B |
| 同じ内容のA2 | 3308×4678 | 417 ms | 234 ms | 73 ms | 104 MiB | 8,723,397 B |
| 300dpiスキャンを1600万画素で | 4000×4000 | 281 ms | 167 ms | 48 ms | 136 MiB | 3,746,065 B |

「全体」はprocessの起動から終了まで（containerのshellを含む）。調査中に手動で、12ページ・4 MBの文書で入力検査・構造JSON・job・刈り込み・12ページの描画・最終段・検査を通すと2.7秒だった（qpdfの各段は0.01〜0.19秒）。

### 13.3 process の制限【事実】

| 制限 | 結果（4000×4000でスキャンを描く。正常なら JPEG 3,746,065 B） |
| --- | --- |
| AS 256 MiB〜1 GiB（GCHeapHardLimit や W^X 無効を加えても同じ） | CoreCLR が起動できない（`0x8007000E`） |
| AS 1 GiB + GCHeapHardLimit 192 MiB + GCRegionRange 256 MiB | 起動したが画像が省かれた（250,627 B）。同じ設定で AS 768 MiB は異常終了 |
| AS 2 GiB | 起動したが画像が省かれた（250,627 B） |
| AS 4 GiB | 正常 |
| DATA 128 MiB | 画像が省かれた（250,627 B）。A2 は bitmap を確保できず終了 |
| DATA 192 / 256 / 384 MiB | 正常（A2 も正常） |

600dpi A4 のスキャン（4960×7016）を200dpiで描いた結果: baseline JPEG は DATA 192 MiB 以上で正常（1,083,960 B）。プログレッシブJPEG は 192 MiB で画像が省かれ（61,779 B）、256 MiB 以上で正常。

Flate の巨大画像（20000×20000、40000×40000 の灰色）は DATA 384 MiB で正しく描けた（それぞれ0.26秒・0.72秒、container全体の `memory.peak` 176 MiB、中央の画素値128）。

PDFiumは画像を省いたことを返さない（`FPDF_RenderPageBitmap` は戻り値なし）。

### 13.4 埋め込みのない日本語【事実】

`/MS-Mincho`・`/UniJIS-UCS2-H`・埋め込みなしの「日本語テスト」を 1190×1684 で描いた暗い画素の数: フォントを渡さない場合 **0**、BIZ UDPゴシック を `m_pUserFontPaths` で渡した場合 21,107。Helvetica（PDFium内蔵）は 16,782。imageには `/usr/share/fonts` がない。

### 13.5 共有フォントに残る文字【事実】

pdfcpu 0.16.1 で、1ページ目「鈴木一郎 個人番号 9876」、2ページ目「テスト PUBLIC」を BIZ UDPゴシック で作ると、2ページが同じフォントobjectを使った。1ページ目を画像に置き換えて出力したPDFのフォントの ToUnicode に、1ページ目だけの文字「鈴」「一」「郎」「人」「7」が残った（判定は bfchar と bfrange の先頭の文字だけで、ほかの字も範囲に含まれている可能性がある）。subset されたフォントの字形データにも同じ字の形が含まれると考えられる（【未検証】字形は調べていない）。

試作の判定（`fontshare.py`）は、この文書で「2ページ目も画像化」、目印の文書（ページごとに別のフォント）で「なし」と判定した。

### 13.6 刈り込み（`--remove-unreferenced-resources=yes`）【事実】

| 文書 | 結果 |
| --- | --- |
| 2ページが1つの間接Resources辞書（F1・F2・X1）を共有し、2ページ目だけを残す | 終了コード0、F1・X1（1ページ目だけが使う）は出力にない |
| 対象外のページが F1・X1 を参照するが使わず、content は正常 | 終了コード0、F1・X1 はない |
| 対象外のページの content に構文エラー（閉じない文字列） | **終了コード3（警告）、F1 と X1（1ページ目の Form XObject「SECRETFORM」）が出力に残った** |
| 刈り込み前の構造JSONでフォントの共有を判定 | 共有のResources辞書のため全ページが共有と判定された（刈り込み後は「なし」） |

### 13.7 目印の検査【事実】

目印（`MK_*`）を26か所に置いた3ページの文書（構成は `gen_markers.py`）で、1ページ目を黒塗りした。

- 手順3（1つのjob）の後: 文書単位の目印（しおり、名前付き要素、XMP、文書情報、OpenAction、ページラベル）と1ページ目の目印が残る。添付、`/AF`、フォームの名前・値、タグ構造は消えた。除去と焼き込みを2段に分けた場合と同じ（この比較は調査中に手動で行い、`reproduce.sh` には含めていない）。
- 手順4の後: `MK_BODY1`、`MK_FIELDAP`、`MK_FORMX`、`MK_INLINE_ACTUALTEXT`、`MK_PIECE`、`MK_PIECEINFO`、`MK_PROPS`、`MK_URI1`（いずれも1ページ目）、`MK_URI2`（2ページ目の外観のないリンク）、**`MK_PAGELABEL`（カタログの `/PageLabels`）** が残った。
- 最終段の後: **目印は0個**。`PUBLIC_P2`、`PUBLIC_NESTED`（対象外のページの本文・入れ子のForm）は残った。出力のobjectは11個、カタログは `/Type`・`/Pages` だけ、trailer に `/Info` はない。
- 最終段を `--pages` による組み直しで行った場合も同じ結果だった（手動で確認。`reproduce.sh` は update JSON の方式だけ）。
- 最終段の出力から `--json-object=<番号> --json-stream-data=inline --decode-level=generalized` で、置き換えたページのcontent `q 595 0 0 842 0 0 cm /Im0 Do Q` を取り出せた（§7.5 の3、手動で確認）。

### 13.8 外観のないフォームの値【事実】

`/NeedAppearances true`・外観のないテキスト欄「FIELD VALUE 12345」: `generateAppearances` なしでは終了コード3（「form fields will not be flattened」）、欄の暗い画素0。ありでは終了コード0、暗い画素1,169。

### 13.9 配布物【事実】

- PDFtoImage 5.4.0（2026-08-16）は `bblanchon.PDFium.Linux 152.0.7961` と `SkiaSharp 4.150.1` に依存する。
- `bblanchon.PDFium.Linux 157.0.8100` のNuGetパッケージには header、targets、各 runtime の `libpdfium.so` だけがあり、ライセンス文はない。GitHub release の `pdfium-linux-x64.tgz` には `LICENSE`（build script、MIT）と `licenses/` 17件があり、`lib/libpdfium.so` はNuGetのものとbyte単位で同一。
- runtime image に `libturbojpeg.so.0`（`libturbojpeg0`、`libjpeg-turbo-progs` の依存）が既にある。

## 14. 未検証・残余リスク

- 中立の許可リスト外の共有objectは引き続き422になり得る。許可リストの判断は合成PDFで検証しており、実在の各生成ツールのPDFで受付改善率は未確認。
- §6.2の閾値と§10.5の容量・並行実行はPR Bで測定済み。合成fixtureに対する測定であり、任意のPDFの成功・描画の完全性を保証するものではない。
- 画像の受付検査はPDF辞書の成分数と画像1枚ごとの大きさに基づく。符号化データの実成分数が辞書と異なる場合と、同一ページ内の画像の合計作業量は未検証。描画が省略される可能性があり、閾値だけで完全性を保証しない。
- content内の inline image が大きい場合、PDFiumが黙って省く可能性。黒塗りの安全性には影響しない。
- PDFiumが content の一部を描けない場合（未対応の機能、壊れたstream）も黙って省く。画像にしたページの見た目が元と違う可能性は、READMEで「結果を必ず確認する」案内とあわせて書く。
- 画像にしなかったページの `/PieceInfo`・`/Thumb`・ページ単位のXMPなどは残る（黒塗りの対象ではない）。
- サイトのpdf.jsとPDFiumで、表示上のページ（CropBox ∩ MediaBox と Rotate）の解釈が同じであること。電子印鑑（#29、site#44）と同じ前提で、#47 の受け入れ条件（比較画像）で確かめる。
