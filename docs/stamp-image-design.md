# 電子印鑑（PNG画像のスタンプ）APIの実装設計

2026-10-13 JST。対象は [Issue #29](https://github.com/kooiei-in4a/amane-pdf-api/issues/29) の `POST /api/pdf/stamp-image`。状態は**PR #64のブランチで実装済み・mainへ未merge**である。2026-10-13の実装指示に基づく採用結果を§13に記載する。§1・§3・§12の設計調査時点の事実と、現在の実装・検証結果を区別する。実装後の検証結果・制約の正本は [stamp-image-validation.md](stamp-image-validation.md) を参照する。

この文書では、記述を次の3種類に分ける。

- **【事実】** 基準commitのコード・文書から確認したこと、または固定versionの実toolで確認したこと
- **【設計】** 採用した実装方針。実装済みか未対応かは本文と§13に記載する
- **【未検証】** 追加確認が必要な挙動。現在の未確認事項は検証記録に記載する

## 1. 調査の基準

| 項目 | 値 |
| --- | --- |
| 基準commit | main `74ff1eaac8863fba45c35ff3a7c26ef3004420dc`（PR #63「#28 透かし」のmerge。#25 pdfcpu基盤、#26 画像→PDF、#27 ページ番号、#28 透かしを含む） |
| 実tool | `amane-pdf-api:issue28` image（treeは `74ff1ea` と同一。PR #63 head `acf7115` との差分なしを `git diff` で確認）。pdfcpu 0.16.1、qpdf 12.3.2、Ubuntu 26.04.1 |
| 描画比較 | host の Poppler（pdftoppm）、Pillow、numpy。配布imageには追加しない |
| 読んだ文書 | AGENTS.md、Issue #29本文（commentなし）、README.md、docs/security.md、docs/pdfcpu-design.md、docs/overlay-validation.md、amane-tools-site#44本文とcomment |
| 読んだコード | `PdfOverlayBuilder`、`PdfPageBoxes`、`PdfcpuProcessor`、`PdfcpuLayer`、`OverlayCapacity`、`PdfEndpointHelpers`、`MultipartPdfUpload`、`SizeLimitedReadStream`、`PdfOptions`、`Program`、`TemporaryPdfFiles`、`ImageInputInspector`、`PngSanitizer`、`FromImagesCapacity`、`JpegNormalizer.CheckResult`、`ExternalProcessRunner`、`ProcessMemoryLimits`、`PdfPageNumbersEndpoint`、`PdfWatermarkEndpoint`、関連テストの構成 |

### 1.1 mainで確認した事実（設計の前提）

| 対象 | 【事実】 |
| --- | --- |
| `PdfOverlayBuilder.BuildAsync(files, resolveRange, draw, token, observe, fillOpacity)` | 入力の `qpdf --check`（1回）→ metadata（ページ数・Box・Rotate・UserUnit）→ `resolveRange(count)` → 回転なし・表示寸法の白紙PDF → `draw(canvas)` → レイヤー検査 → `qpdf --overlay`（対象範囲だけ）→ 属性復元 → `--check` → 最終metadata比較、を1つの台帳 `OverlayCapacity`（124 MiB）で行う |
| `OverlayCanvas` | `BlankPath`、`LayerPath`、`Pages`（各ページの `DisplayWidth`/`DisplayHeight`）、`JsonBudget`、`PdfBudget` を渡す。draw後、Builderは `files.PdfcpuLayerJsonPath` と `LayerPath` の両方を `Commit` する。**`Commit` は file が存在しないと500になる**（`OverlayCapacity.Commit`） |
| 表示寸法 | `DisplayWidth = (Rotate 90/270 ? Crop.Height : Crop.Width) × UserUnit`。Crop は CropBox ∩ MediaBox。Rotate は90の倍数のみ（他は422 `unsupported-pdf`）、UserUnit は 0 < u ≤ 75,000、表示各辺は 0 < 辺 ≤ 14,400 pt |
| Builderの容量台帳 | 台帳はBuilder内で生成し、入力PDFとBuilder自身の中間物だけを計上する。**draw用の別入力（画像）を計上する仕組みはない** |
| `PdfcpuCapacityException` | Builderが422 `too-complex`（「このPDFは描画の処理上限を超えています。」）へ一律に変換する |
| `PdfcpuProcessor` | 文字用の `CreateAsync`（JSON経由）と画像→PDF用の `ImportImageAsync` がある。画像stamp用メソッドはない。`CreateRequest` は prlimit（CORE=0、AS 1 GiB、FSIZE）と GOMEMLIMIT=200MiB・GOMAXPROCS=1 などの環境変数、job内HOME、`-c 設定dir --offline` を共通化している |
| `ExternalProcessRunner` | shellなし・ArgumentList。`StdoutLimit` 指定時は stdout を `byte[limit]` に保持し、超過時は EOF まで読み捨てて `null` を返す（読み取り単位は64 bytes）。stderr は破棄。cancel時は process tree を kill し、終了を待ってから戻る |
| `PdfEndpointHelpers.ExecuteAsync` | query検証 → request上限設定 → job作成 → upload → 共有30秒timeoutで処理 → 送信。`images` 引数は 413/504 の文言を画像→PDF用に切り替える。`PdfImageInputException` は画像→PDF専用の文言（JPEGへの言及、「画像から作るPDF」）で422にする |
| `MultipartPdfUpload` | `file`＋`password`、`file`＋`text`、複数 `file` の読み取りがある。`file`＋`image` はない。いずれも `SizeLimitedReadStream`（request全量）と part ごとの上限、MIME epilogue の読み切りを行う |
| `PngSanitizer.WriteAsync(input, output, budget, pixelLimit, workingLimit, token)` | signature、全chunkのCRC、IHDR（最初・13 bytes・1回）、bit depth/color type の組合せ、compression/filter method 0、interlace 0/1、PLTE/tRNS の位置と長さ、IDAT の連続、IEND、予約bit、未知critical chunk、APNG（acTL/fcTL/fdAT）を検査する。IHDR/PLTE/tRNS/IDAT/IEND だけを書き出し、ancillary chunk と IEND 後のデータを除く。**画素数は総数（w×h ≤ pixelLimit）だけを検査し、各辺は検査しない。IDAT の zlib データは展開しない** |
| `ImageInputInspector` | `IsPng`（signatureと空判定）と `ReadJpeg` だけ。PNGの構造検査は `PngSanitizer` が担う |
| request上限 | `MaxRequestBytes = MaxFileBytes + 64 KiB`。mergeと画像→PDFは専用の上限を `ExecuteAsync(maxRequestBytes:)` と multipart の `SizeLimitedReadStream` の両方へ渡している |
| query key | page-numbers/watermark は key の大文字小文字を区別せず、未知key・重複・空値を400にする |
| ページ範囲の400 | page-numbers は `resolveRange` 内で `BadHttpRequestException` を投げ、入力検査後に400を返す。入力検査やページ数取得を二重に行わない（#27 の測定で二重化が時間基準を超えると確認済み） |

## 2. Issue末尾の既存草案（2026-10-07）との差分

| 草案 | 最新mainと実測を踏まえた設計 | 理由 |
| --- | --- | --- |
| `ValidateAsync(input)` → `pageCount` → page超過は400 | Builderの `resolveRange` で400を返す。endpointでは入力検査とページ数取得を行わない | 入力検査の二重化を避ける（#27の測定） |
| `PdfPageBoxes`（対象ページだけ）から W/H を取得 | draw に渡される `canvas.Pages[0]` の表示寸法を使う | Builderが既に取得している。metadataを再取得しない |
| `BuildAsync(files, page, page, draw)` | resolver overload に、画像を台帳へ載せる `drawInputs` と、JSONを使わない `layerJson: false` を追加して使う | 画像を台帳に計上するため。JSONなしのdrawはBuilderの `Commit` で500になるため |
| `stamp add --mode image -- … <blank> <layer>` | `stamp add -m image -- <job>/stamp.png "<説明>" <blank> -`。出力は stdout で受け取り、`StdoutLimit` で上限を判定する | `-mode` は失敗した（`--mode` と `-m` は成功）。ファイル出力でFSIZEに達すると、pdfcpuが部分出力を削除して exit 1 になり、容量超過と区別できない（§3.1） |
| IHDR・寸法・depth/color type だけを検査 | `PngSanitizer` を再利用し、各辺の検査と IDAT の展開検査（新規）を追加する | sanitizerは総画素数だけを検査し、IDATを展開しない。展開検査がないと pdfcpu の exit 1 を入力不正と判別できない |
| `ImageInputInspector` でPNG検査（#26と共有） | PNG検査は `PngSanitizer`。`ImageInputInspector` は使わない | mainのPNG検査は `PngSanitizer` にある |
| interlace・16 bit を読めない場合も422 | 全color type・bit depth・Adam7・tRNSで pdfcpu 0.16.1 が成功し、透過も正しく描かれた（§12.1）。対応範囲として受理する | 実測 |
| 失敗はすべて422 `unsupported-image` | 入力不正 422 `unsupported-image`、画像起因の容量超過 422 `image-too-complex`（新規）、PDF起因の422、内部障害500に分ける | 26 KBの正当なPNGでもレイヤーが8 MiBを超えた（§12.1） |
| `/SMask` の存在だけで透過を確認 | 構造確認（`/SMask`、Indexedなど）に加え、Popplerの描画で透明・半透明の画素値を比較する | Issueの完了条件 |
| 記載なし | pdfcpu `stamp` は透かし用の OCG と `/Artifact` を追加する（§3.1）。READMEに記載する | 実測 |
| 記載なし | 413の文言、画像→PDF用の422文言を流用しない（§7.3） | 文言がPDFだけ・JPEGに言及していて、押印APIでは誤解を招く |

## 3. 採用方式

### 3.1 pdfcpu の描画方法の比較（実測）

回転なし・表示寸法の白紙PDF（#25 と同じ形式）に、pdfcpu 0.16.1 で300×200 pxのPNGを描く2つの方法を比較した。

| 観点 | A: `stamp add -m image`（`pos:bl, off:L B, scale:s abs, rot:0`） | B: `create` JSONの `image` 要素（`pos`、`width`、`height`） |
| --- | --- | --- |
| 配置精度（§4の47 caseのうち配置21 case） | 全caseで表示座標の誤差 ≤ 0.0001 pt | **画素数を超えて拡大しない**。指定枠より画像の画素数（px=pt換算）が小さいと原寸で枠の中央へ置かれ、最大250 ptずれた |
| 壊れたPNG（zlib破損・Adler不一致・データ不足/過剰・不正filter） | exit 1（`decode image: …`） | **exit 0 のまま画像を省略**した（レイヤー797 bytes） |
| 出力構造 | `/Artifact <</Subtype /Watermark /Type /Pagination>> BDC q 1 0 0 1 L B cm /GS0 gs /Fm0 Do Q EMC`。Form XObject（`/OC` 付き）内で `w 0 0 h 0 0 cm /Im0 Do`。レイヤーのCatalogに OCG「Watermark」の `/OCProperties` | ページ直下で `w 0 0 h L B cm /Im0 Do`。OCGなし |
| 画像XObject | 同じ（下記）。`/Interpolate true`、alpha・tRNSは `/SMask` | 同じ |
| 数値の渡し方 | argv の説明文字列（API生成の数値） | 0600のJSON |

【設計】**A（stamp）を採用する。** B は小さい印影画像を拡大できず、配置仕様を満たさない。また、壊れた画像を黙って省略するため不適である。

A の副作用は次のとおり。

- 【事実】qpdf `--overlay` 後の出力では、OCG「Watermark」が Form XObject の `/OC` から参照される。出力の Catalog の `/OCProperties` には登録されない（入力に `/OCProperties` がない場合はCatalogにそれ自体がない）。`qpdf --check` は成功した。
- 【事実】入力PDFが自分のOCGを持ち、`/D /BaseState /OFF` の場合も、Popplerでは印影が表示され、入力側のOFFのレイヤーは非表示のままだった。
- 【未検証】pdf.js、Acrobat、その他のviewerでの表示。pdf.js は未登録のOCGを表示する実装だと認識しているが、今回はソース・実機とも確認していない。印影は「透かし（Artifact）」として扱われ、編集ソフトの透かし削除機能などで消せる可能性がある。

OCGを除去する追加処理（qpdf JSONでレイヤーの `/OC` と `/OCProperties` を消す）も可能だが、qpdfの呼び出しと検査が1段増える。印影には改ざん防止の効力がないため、【設計】除去は行わず、READMEに記載する。§13の決定1として採用済みである。

### 3.2 処理フローと責務

```text
[query検証]   StampImageQuery.Parse(query)                       → 400（アップロード前）
[upload]      MultipartPdfUpload.ReadFileAndImageAsync           → input.pdf / stamp-upload.bin、400 / 413
------------- ここから共有30秒timeout（ExecuteAsync） -------------
[PNG検査]     StampImageInput.PrepareAsync
                PngSanitizer.WriteAsync → stamp.png（upload削除）  → 422 unsupported-image
                各辺 ≤ 2000 px                                     → 422 unsupported-image
                PngDataValidator.ValidateAsync（IDAT展開検査）      → 422 unsupported-image
[PDF検証以降] PdfOverlayBuilder.BuildAsync(files, query.ResolveRange, draw, token,
                drawInputs: [stamp.png], layerJson: false)
                qpdf --check（1回）                                → 422（reasonなし）
                metadata・ページ属性                                → 422 too-complex / unsupported-pdf
                ResolveRange(count)：page > count                  → 400
                白紙PDF（対象1ページ、回転なし・表示寸法）
                draw:
                  StampPlacement.Calculate(query, image, canvas.Pages[0])  → 400（はみ出し）
                  PdfcpuProcessor.StampImageAsync → canvas.LayerPath
                     stdout超過 → 422 image-too-complex、exit 1 → 422 unsupported-image、
                     その他の異常 → 500
                レイヤー検査 → stamp.png・白紙削除 → qpdf --overlay --to=page
                → 属性復元 → qpdf --check → 最終metadata比較           → 500（生成物の異常）
[応答]        SendFileAsync：200、application/pdf、stamped.pdf
[cleanup]     Builderのfinallyで中間物を削除、TemporaryPdfFiles.Disposeでjobを削除
```

【設計】PNG検査をPDF検証より先に行う。PNG検査は1 MiB以下の入力を数msで処理でき（§12.1）、不正な画像のときにqpdfの起動を省ける。PNGとPDFの両方が不正な場合は、PNGのエラー（422 `unsupported-image`）を返す。

はみ出し判定の400は、表示寸法が分かるdrawの時点まで確定しない。そのため、入力検査・metadata・白紙生成の後に400を返す。page-numbersの「ページ数に依存する400は入力検査後」と同じ扱いである。

### 3.3 クラス・メソッド設計

新規の型はAPI assembly内の `internal` とする。テストは既存の `InternalsVisibleTo` を使う。

#### `StampImageQuery`（新規、`PdfStampImageEndpoint.cs` 内）

```csharp
internal sealed record StampImageQuery(int Page, decimal X, decimal Y, decimal Width)
{
    internal static StampImageQuery Parse(IQueryCollection query);   // 不正は BadHttpRequestException（400）
    internal (int From, int To) ResolveRange(int pageCount);         // Page > pageCount は BadHttpRequestException
}
```

#### `StampPlacement`（新規、純粋関数、`StampPlacement.cs`）

```csharp
internal readonly record struct StampPlacement(double Left, double Bottom, double Width, double Height,
    double Scale, double PageWidth, double PageHeight)
{
    internal const double Tolerance = 1e-6; // pt
    internal static StampPlacement Calculate(StampImageQuery query, StampImage image, PdfcpuPageSize page);
    internal string Description { get; }    // "pos:bl, off:L B, scale:S abs, rot:0"
}
```

- 引数：query（decimal）、PNGの画素数（1〜2000）、表示寸法（有限、0 < 辺 ≤ 14,400）。
- 戻り値：白紙ページ座標（左下原点、pt）での配置。
- 例外：はみ出しは `BadHttpRequestException`（400）。非有限値・範囲外の引数は `ArgumentOutOfRangeException`（呼び出し側の誤りとして500）。

#### `StampImage` / `StampImageInput` / `PngDataValidator`（新規、`StampImageInput.cs`）

```csharp
internal sealed record StampImage(string Path, int Width, int Height);

internal static class StampImageInput
{
    internal const int MaxSide = 2000;
    internal static bool IsValid(PdfOptions options);                    // 起動時の設定検証（§6.1）
    internal static Task<StampImage> PrepareAsync(TemporaryPdfFiles files, PdfOptions options, CancellationToken token);
}

internal static class PngDataValidator
{
    internal static Task ValidateAsync(string sanitizedPngPath, CancellationToken token);
}

internal enum PdfStampImageReason { Unsupported, TooComplex }
internal sealed class PdfStampImageException(PdfStampImageReason reason) : Exception { … }
```

`PrepareAsync` は次の順に処理する。

1. `PngSanitizer.WriteAsync(files.StampUploadPath, files.StampImagePath, budget: upload長, pixelLimit: 2000×2000, workingLimit: options.ImagePngWorkingLimitBytes, token)` を呼ぶ。
   - `PdfImageInputException` の Unsupported・Empty は `PdfStampImageException(Unsupported)` に変換する。
   - Capacity は500に変換する。sanitizerは chunk を除くだけなので出力が入力を超えることはなく、Capacity は内部不整合を意味する。
   - **`PdfImageInputException` を endpoint へ漏らさない**（画像→PDF用の文言になるため）。
2. `files.StampUploadPath` を削除する。
3. `Width > 2000 || Height > 2000` なら Unsupported とする（例：4000×1000 は総画素数の検査を通るため）。
4. `PngDataValidator.ValidateAsync` を呼ぶ。
5. `StampImage(files.StampImagePath, w, h)` を返す。

`PngDataValidator` は sanitize 済みのファイルを chunk 単位で読み、IHDR から幅・高さ・bit depth・color type・interlace を再取得する。実装では上限1 MiBのencodedファイルからIDATだけを上限内のMemoryStreamに連結し、`ZLibStream` で行ごとに展開する。圧縮データの追加バッファは最大1 MiB（capacityの丸めを含め最大2 MiB）、展開画素全体は保持しない。各 pass（Adam7は7 pass）の各行について、`1 + ceil(幅 × channel数 × depth / 8)` bytes を `ReadExactly` で読み、先頭の filter type が 0〜4 であることを確認する。最後に `Read` が0（EOF）を返すことを確認する。実装ではzlib headerのCM/CINFO/FCHECK/FDICTとAdler-32も明示的に検査し、ZLibStream単体で通る場合のある末尾欠損を拒否する。

- `InvalidDataException`（zlib破損、Adler-32不一致）と `EndOfStreamException`（データ不足）は Unsupported にする。EOF後にデータが残る場合も Unsupported にする。
- 作業memoryは上限1 MiBのencoded IDAT（capacity ≤ 2 MiB）、1行分（最大16,001 bytes）と zlib の状態である。圧縮後のbytesから展開後memoryを推定せず、展開後のbytes数は画素数から決まる。
- 行ごとにtokenを渡した `ReadExactlyAsync` で中断を確認する。
- 【事実】.NET 10 の `ZLibStream` は Adler-32 の不一致を `InvalidDataException` として報告し、データ不足・過剰・不正filterも上の手順で検出できた（§12.1）。

#### `PdfcpuProcessor.StampImageAsync`（新規メソッド）

```csharp
internal async Task StampImageAsync(TemporaryPdfFiles files, StampImage image, StampPlacement placement,
    string blankPath, string outputPath, long pdfBudget, CancellationToken token);
```

- 事前検査（違反は `ArgumentException`、500）
  - `RequireJobPath` で `image.Path`・`blankPath`・`outputPath` を確認する。
  - `Path.GetFileName(image.Path) == "stamp.png"` であること。
  - blank が入力PDFでも出力でもなく、出力がまだ存在しないこと。
  - `pdfBudget` が 0 より大きく `LayerFileLimitBytes`（8 MiB）以下であること。
  - blank の長さが 0 より大きく8 MiB以下であること。
  - placement が有限値で、ページ内にあること（防御的な再確認）。
- 起動：`ExternalProcessRunner.RunAsync(CreateRequest(files, ["stamp", "add", "-m", "image", "--", image.Path, placement.Description, blankPath, "-"], fileBudget: 1, stdoutLimit: (int)pdfBudget), token)`
- 結果の判定（上から順に評価する）
  1. exit が 126/127/負/128以上（起動失敗・signal）なら `InvalidOperationException`（500）
  2. `Stdout is null`（出力が `pdfBudget` を超過）なら `PdfStampImageException(TooComplex)`
  3. exit 1 なら `PdfStampImageException(Unsupported)`（§7.2）
  4. exit がその他の非0（Go runtime の fatal error は exit 2）、または stdout が空なら `InvalidOperationException`（500）
- 書き出し：stdout の bytes を `TemporaryPdfFiles.CreatePrivateFile(outputPath)`（0600）に書き、既存の `CheckOutputAsync(files, outputPath, 1, token)`（非暗号化・`--check`・ページ数）で検査する。
- `PdfcpuCapacityException` は使わない。Builderが `too-complex`（PDF起因の文言）へ変換するためである。

#### `PdfOverlayBuilder.BuildAsync`（変更）

```csharp
internal async Task BuildAsync(TemporaryPdfFiles files, Func<int, (int From, int To)> resolveRange,
    Func<OverlayCanvas, CancellationToken, Task> draw, CancellationToken token,
    Action<OverlayStage>? observe = null, double? fillOpacity = null,
    IReadOnlyList<string>? drawInputs = null, bool layerJson = true);
```

- `drawInputs`：各pathがjob直下の既存の非空fileで、入力・出力・中間物のpathと重ならないことを確認する。入力PDFの予約直後に `capacity.Reserve(path, length)` で台帳に載せる。draw とレイヤー検査の成功後、白紙と同時に `capacity.Delete` する。finally の削除対象にも加える。
- `layerJson: false`：`PdfcpuLayerJsonPath` の予約と `Commit` を行わず、`canvas.JsonBudget` を 0 にする（`CreateAsync` は 0 を拒否するため、誤用は500になる）。
- 既存の呼び出し（page-numbers、watermark）の動作は変えない。

#### `MultipartPdfUpload.ReadFileAndImageAsync`（新規）

```csharp
internal static Task ReadFileAndImageAsync(HttpRequest request, TemporaryPdfFiles files, PdfOptions options,
    CancellationToken token);
```

- `SizeLimitedReadStream(body, options.MaxStampRequestBytes)` を使う。
- `file` は `CopyFileAsync(…, files.InputPath, options.MaxFileBytes)`、`image` は `CopyFileAsync(…, files.StampUploadPath, options.MaxStampImageBytes)`。
- 完了後に epilogue を読み切る（§5.2）。

#### その他の変更

- `TemporaryPdfFiles`：`internal string StampUploadPath`（`stamp-upload.bin`）と `StampImagePath`（`stamp.png`）を追加する。どちらも `Path.GetFullPath(DirectoryPath)` 配下とする。
- `PdfOptions`：`MaxStampImageBytes`（既定 1 MiB）と `MaxStampRequestBytes => checked(MaxFileBytes + MaxStampImageBytes + MultipartOverheadBytes)` を追加する。
- `PdfEndpointHelpers.ExecuteAsync`：引数 `string? tooLargeTitle = null` を追加し、`PdfStampImageException` の catch を追加する（§7）。
- `Program`：Linux の endpoint 群に `app.MapPost("/api/pdf/stamp-image", PdfStampImageEndpoint.HandleAsync).RequireRateLimiting("pdf")` を追加し、Validate に `StampImageInput.IsValid` を追加する。
- `PdfcpuProcessor.ValidateStartupAsync`：日本語描画の自己テストに続けて、合成RGBA PNG（`PdfFromImagesProcessor.StartupPng`、16×24）の stamp を白紙へ描き、stdout・検査を通す自己テストを追加する。失敗時は既存の `PdfcpuStartupException` とする。

endpoint の骨格は次のとおり。

```csharp
StampImageQuery? query = null;
return PdfEndpointHelpers.ExecuteAsync(context, options.Value, "stamped.pdf",
    () => query = StampImageQuery.Parse(context.Request.Query),
    (files, token) => MultipartPdfUpload.ReadFileAndImageAsync(context.Request, files, options.Value, token),
    async (files, token) =>
    {
        var image = await StampImageInput.PrepareAsync(files, options.Value, token);
        await overlay.BuildAsync(files, query!.ResolveRange,
            (canvas, drawToken) => pdfcpu.StampImageAsync(files, image,
                StampPlacement.Calculate(query, image, canvas.Pages[0]),
                canvas.BlankPath, canvas.LayerPath, canvas.PdfBudget, drawToken),
            token, drawInputs: [image.Path], layerJson: false);
    },
    maxRequestBytes: options.Value.MaxStampRequestBytes,
    tooLargeTitle: "PDF・画像またはリクエストのサイズ上限を超過しました。");
```

## 4. 配置計算

### 4.1 式

白紙PDFは回転なしで、MediaBox `[0 0 W H]`（W/H は Builder の表示寸法、pt、UserUnit込み）である。そのため、表示座標をそのまま白紙の座標として使える。

```text
W, H   = canvas.Pages[0].Width / Height（pt）
pw, ph = PNG の幅・高さ（px）
w      = width × W
h      = w × ph / pw
left   = x × W
top    = y × H
bottom = H − top − h
scale  = w / pw          （pdfcpu の abs：px × scale = pt）
pdfcpu = "pos:bl, off:{left} {bottom}, scale:{scale} abs, rot:0"
```

- 【事実】`rot:0` を省くと、pdfcpu の既定の対角回転（`d:1`）が適用され、`0.5547 0.8320 -0.8320 0.5547 …` の回転行列になった。`rot:0` は必須である。
- 【事実】`pos:bl` の `off` は右・上が正で、白紙の左下から画像の左下までの距離になった。pdfcpu は `off` を content stream へ小数5桁、Form の行列を6桁で書いた。

### 4.2 はみ出し判定・丸め

- 横方向：decimal で `x + width > 1` なら400とする（例：x=0.88、width=0.12 はちょうど右端に接するので受理）。
- 縦方向：double で `top + h > H + Tolerance` なら400とする（`Tolerance = 1e-6 pt`）。受理した場合は `bottom = Math.Max(0, H − top − h)` に丸める。誤差は 1e-6 pt 以下である。
- 横も防御的に `left + w ≤ W + Tolerance` を確認する。
- `x = 1` は `width ≥ 0.02` のため常に横方向で400、`y = 1` は `h > 0` のため常に縦方向で400になる。
- 縦長のPNG（例：1×2000 px、width=0.5）は、多くのページで縦方向に400になる。仕様どおりとする。
- 数値の文字列化：実装では `left`・`bottom`・`scale` を `ToString("R", InvariantCulture)` とする。小さいページのscaleが固定小数書式で0へ丸められるのを防ぐ。指数表記はpdfcpuで受理され、argvにはAPIの検証済み計算値だけを入れる。leftは非負のqueryとページ幅の積、bottomは `Math.Max(0, H − top − h)` で計算する。queryが指数表記を受け付けない仕様（§5.1）と、APIが生成するargvの数値書式は別である。
  - 【事実】レビューでpdfcpu 0.16.1が `5.68E-14`・`3E-05`・`4E1` をそれぞれ正しい数値として読むことを確認済み。`:R`で生成する大文字Eの指数表記を使える。設計probeでは小数12桁のscaleと `-0` も受理した。
- 精度：表示上の誤差は pdfcpu の書式（5〜6桁）と上記の丸めを合わせて 1e-4 pt 程度である。完了条件の1 ptに対して十分に小さい。

### 4.3 CropBox・Rotate・UserUnit

- CropBox の原点のずれ、Rotate、UserUnit は、Builder の既存処理（対象ページの Box を Crop に揃え、Rotate を正規化して overlay し、その後に属性を復元する）で反映される。印影の処理で追加の座標変換はしない。
- UserUnit は W/H の両方に同じ倍率で掛かる。そのため x/y/width の**割合**は UserUnit の扱いに依存しない。site のプレビュー（pdf.js）が UserUnit を反映するかどうかに関わらず、割合は一致する（§11）。
- Rotate が90の倍数でないページは、既存の判定で422 `unsupported-pdf` になる。

### 4.4 実toolでの確認（§12.1 で再現）

- 対象：CropBox `[50 80 650 880]`（原点のずれあり）× Rotate 0/90/180/270、UserUnit 2（Rotate 90）、UserUnit 0.5（Rotate 270）、A4 MediaBoxのみ。各ページに例示（0.72/0.08/0.12）、左上端（0/0/0.5）、右下端に接する位置の3通り。
- 方法：出力の content stream を q/Q/cm/Do と Form `/Matrix` を追って合成し、画像の単位正方形の (0,1)/(1,1)/(0,0) を表示座標（CropBox原点・Rotate・UserUnit反映、左上原点）へ変換して期待値と比較した。
- 結果：stamp 方式の21 case すべてで誤差 ≤ 0.0001 pt。非対象ページに XObject は追加されなかった。
- 描画：Popplerの `-cropbox` で4隅の色（左上赤・右上緑・左下青・右下黄）を確認し、回転4種・UserUnit・CropBoxで向きが正しかった。縮小時の補間で値は最大2ずれた（255→253）。テストの色比較には許容差を設ける。

## 5. 入力検証

### 5.1 query

【設計】key は4つすべて必須で、他の key は認めない。key は大文字小文字を区別しない（page-numbers/watermark と同じ。`IQueryCollection` も区別しない）。`Page=1&page=2` は重複として400にする。値の比較は ASCII の文字だけで行い、`\d` と正規表現は使わない。

| key | 書式（すべてASCII） | 最大長 | 読み取り | 範囲 | 違反 |
| --- | --- | ---: | --- | --- | --- |
| `page` | `[1-9][0-9]*`（先頭0不可） | 10 | `int.TryParse(…, NumberStyles.None, InvariantCulture)`。失敗（overflow）は400 | 1以上。ページ数以下かは入力検査後に判定 | 400 |
| `x` / `y` | `D` または `D.F`。D は `[0-9]` 1文字、F は `[0-9]` 1〜4文字 | 6 | 文字検査の後に `decimal.TryParse(…, NumberStyles.AllowDecimalPoint, InvariantCulture)` | 0〜1 | 400 |
| `width` | 同上 | 6 | 同上 | 0.02〜0.5 | 400 |

- 拒否する例：`.5`、`1.`、`00.5`、`+0.5`、`-0`、`0.5e1`、`0,5`、` 0.5`、`0.12345`、全角数字、`٠.١٢`（アラビア・インド数字）、空値、`?page`（値なし）。
- 受理する例：`0`、`1`、`0.7200`、`1.0000`（x/y。ただし§4.2により結果は400）。
- query の検証はアップロード前に行う（watermark の `InvalidQueryPrecedesUploadAndTools` と同じ）。

### 5.2 multipart

| 条件 | 扱い |
| --- | --- |
| Content-Type が multipart/form-data でない、boundary がないか128文字超 | 400（既存の `ReadBoundary`） |
| part の順序 | 自由。`image` が先でもよい |
| `file`：filename（`filename` または `filename*`）付き、1件 | 必須。`MaxFileBytes` 超過は読み取り中に413 |
| `image`：filename付き、1件 | 必須。`MaxStampImageBytes`（1 MiB）超過は1,048,577 byte目で413 |
| 欠落・重複・未知の field（`password`、`text` など）、filenameのない `file`/`image` | 400 |
| request全量 | `Content-Length` > 53,542,912 なら読む前に413。chunked でも `SizeLimitedReadStream` で413 |
| 空の `file` | 既存APIと同じく qpdf の検査で422（reasonなし） |
| 空の `image` | 422 `unsupported-image`（PNG signatureがない。§13 決定3） |
| part の Content-Type、利用者の filename・拡張子 | 使わない。ログにも出さない。保存名はAPIが決めた `stamp-upload.bin` → `stamp.png` |
| MIME epilogue | 読み切ってから処理を始める（既存と同じ） |

### 5.3 PNG

| 項目 | 扱い | 根拠 |
| --- | --- | --- |
| 判定 | 先頭8 bytesのPNG signatureで判定する。Content-Type・拡張子は使わない | 【事実】sanitizer |
| IHDR | 最初・13 bytes・1回。幅・高さとも1〜2000（総画素数4,000,000で早期に拒否し、各辺は sanitize 後に検査） | 【事実】sanitizer＋【設計】各辺の検査 |
| bit depth / color type | gray 1/2/4/8/16、RGB 8/16、palette 1/2/4/8、gray+alpha 8/16、RGBA 8/16 を受理 | 【事実】設計probeの描画確認に加え、実装の`AcceptsAllPngDepthsAndAdam7IncludingPaletteTransparency`で全depth/color type・Adam7・tRNSの受付と透過用SMaskを検証済み |
| interlace | Adam7 を受理 | 【事実】RGBA 8/16、palette 8 の Adam7 で成功 |
| PLTE / tRNS | 仕様どおりの位置・長さ・値域だけ受理。tRNS（gray・RGB の color key、palette の alpha）の透過を保持 | 【事実】sanitizer。color key・palette alpha の透過を描画で確認 |
| alpha | alpha・tRNS は pdfcpu が `/SMask`（DeviceGray）にする。16 bit は 16 bpc のまま。1 bit gray と 4 bit palette は8 bitに展開される | 【事実】出力構造 |
| CRC・chunk順序・予約bit・未知critical | 422 | 【事実】sanitizer |
| IDAT | 連続していること（sanitizer）。zlib・Adler-32・filter type・データの過不足は `PngDataValidator` で検査 | 【設計】 |
| APNG（acTL/fcTL/fdAT） | 422（先頭フレームだけを使うことはしない） | 【事実】sanitizer |
| metadata（tEXt/iTXt/zTXt/eXIf/iCCP/gAMA/cHRM/sRGB/pHYs/tIME 等） | CRCを検査したうえで除去する。pHYs（解像度）は配置に影響しない | 【事実】sanitizer。【設計】scale abs は px 基準 |
| IEND後のデータ | 出力しない（無視） | 【事実】sanitizer |
| 色 | ICC・gamma の除去で見え方がわずかに変わることがある | 画像→PDFと同じ |

【設計】`PngSanitizer` の公開仕様は変えない。from-images（#26）の挙動は変更しない。`PngDataValidator` を from-images にも適用するかは、この Issue の対象外とする。

## 6. リソース制限と安全性

### 6.1 設定と起動時の検証

| 設定 | 既定値 | 検証（`StampImageInput.IsValid`、Linuxのみ） |
| --- | ---: | --- |
| `Pdf__MaxStampImageBytes` | 1,048,576（1 MiB） | 0より大きく1 MiB以下。広げる場合は容量・時間を再測定する |
| `MaxStampRequestBytes`（計算値） | 53,542,912（50 MiB + 1 MiB + 64 KiB） | 計算前に `MaxFileBytes ≤ long.MaxValue − 64 KiB − MaxStampImageBytes` を確認し、overflowを例外にせず設定エラーとする。プロパティ自体は `checked` |
| 画像の各辺 | 2000 px（定数 `StampImageInput.MaxSide`） | 設定にしない |
| レイヤー上限 | `OverlayGeneratedLimitBytes`（8 MiB、既存） | 既存の `OverlayCapacity.IsValid` |
| job上限 | `OverlayJobLimitBytes`（124 MiB、既存） | `≥ Allocated(MaxFileBytes) + 2 × Allocated(MaxStampImageBytes)`（Builder開始前の upload＋sanitize 済みファイルの合計） |
| 処理時間・同時実行 | 30秒・2枠・待ち0（既存） | 既存 |

他APIの request 上限（`MaxRequestBytes`、merge、from-images）は変更しない。stamp だけが `ExecuteAsync(maxRequestBytes: MaxStampRequestBytes)` と `ReadFileAndImageAsync` の `SizeLimitedReadStream(MaxStampRequestBytes)` の両方を使う。

### 6.2 job 容量の計上

| 段階 | jobにあるfile | 計上 |
| --- | --- | --- |
| upload完了 | `input.pdf` ≤ 50 MiB、`stamp-upload.bin` ≤ 1 MiB | 台帳の外。上限は起動時の検証（§6.1）で保証する |
| sanitize | ＋`stamp.png` ≤ upload長（sanitizerの `budget`） | 同上 |
| sanitize後 | `stamp-upload.bin` を削除 | ― |
| Builder開始 | `input.pdf`・`stamp.png` を予約（`drawInputs`） | `OverlayCapacity`（4 KiB切り上げ） |
| metadata・調整JSON・白紙 | 既存どおり（予約→実サイズ→削除） | 既存 |
| draw | `overlay-layer.pdf` ≤ 8 MiB（FSIZE sentinel分を含めて予約）。pdfcpu の JSON は予約しない | 既存の `PdfBudget` |
| draw後 | `stamp.png`・白紙を削除し、予約を解放 | `drawInputs` |
| overlay以降 | 既存どおり（overlaid・最終 ≤ 62 MiB） | 既存 |

- 【事実】draw 時点の残容量は約70 MiB（124 − 50 − 小さな中間物 − 1）であり、`PdfBudget` は常に `OverlayGeneratedLimitBytes`（8 MiB）になる。そのため stdout の超過は画像に起因すると判断できる。
- managed memory：runner の stdout 用バッファ（`byte[PdfBudget]` ≤ 8 MiB と、切り出しのコピー ≤ 8 MiB）、`PngDataValidator` のencoded IDATバッファ（capacity ≤ 2 MiB）・1行（≤ 16,001 bytes）と zlib の状態、既存の metadata DOM。
- 子process：【事実】pdfcpu 単体を別containerで実行したときの cgroup memory.peak は、2000×2000 の 8 bit RGBA で約64 MiB、16 bit RGBA Adam7 で約119 MiB、16 bit で高entropyの最悪例で約181 MiB（8 MiBで打ち切った出力のtmpfs分を含む）だった。圧縮後の PNG bytes ではなく、画素数・bit depth・画像の複雑さで決まる。

### 6.3 外部 process

```text
/usr/bin/env --ignore-signal=XFSZ -- /usr/bin/prlimit --core=0:0 --as=1073741824:1073741824 --fsize=1:1 --
  <PdfcpuPath> -c <PdfcpuConfigDir> --offline stamp add -m image --
  <job>/stamp.png "pos:bl, off:<L> <B>, scale:<S> abs, rot:0" <job>/overlay-blank.pdf -
```

- argv に入るのは、設定の path、API が作った job 内の path、§4 で計算した数値だけである。利用者の filename・文字列・path は入らない。shell を使わない。
- `--fsize=1`：stdout は pipe のため FSIZE の対象外である。pdfcpu が file を書こうとすると失敗する（fail closed）。【事実】この条件で stamp は成功し、job に file を作らなかった。
- 環境変数・HOME・GOMEMLIMIT・GOMAXPROCS=1・GOTRACEBACK=none・WorkingDirectory は既存の `CreateRequest` を使う。AS は 1 GiB（【事実】256 MiB 以下では Go runtime が exit 2 で起動に失敗した）。
- stdout の上限：【事実】既存 runner の64 bytes単位の読み取りで、8 MiB を約0.15秒、12 MB（上限超過）を約0.2秒で処理し、超過は `null` になった。runner は変更しない。
- timeout・cancel：共有30秒の token を渡す。cancel時は runner が process tree を kill し、終了を待つ。

### 6.4 cleanup

| 状況 | 一時ファイル | 子process |
| --- | --- | --- |
| 成功 | Builder が中間物を削除。応答送信後に `TemporaryPdfFiles.Dispose` で job を削除 | 各段階で終了済み |
| 400/422/500（PNG・PDF・pdfcpu） | Builder の finally（`drawInputs` を含む）と Dispose | runner は終了を待ってから戻る |
| timeout（504） | 同上 | kill して終了を待つ |
| client切断・送信中断 | 同上（`RequestAborted`） | 同上 |
| アプリ停止 | 同上（`ApplicationStopping` → `context.Abort`） | 同上 |

印影画像は job 内の0600ファイル（`stamp-upload.bin` → `stamp.png`）と、pdfcpu の stdout を受けるメモリにだけ置く。保存・再利用はしない。出力PDFには当然含まれる。

### 6.5 ログ

stamp処理独自のログにPDF本文、PNG bytes、利用者のfilename、queryの値、pdfcpuのstdout/stderrを追加しない。runnerはstderrを破棄し、500では既存の固定Errorメッセージ「PDF処理で内部障害が発生しました。」だけを出す。ASP.NET Core標準アクセスログにはURL/queryが含まれる場合があり、その抑制はstamp機能で追加していない。

**未実装**：設計時に提案した、pdfcpu exit 1での固定Warning「印影画像の描画がpdfcpuで失敗しました。」は出力していない。現在は422 `unsupported-image`へ変換して応答するだけである。機能コードの挙動を維持するため、このレビュー修正ではログを追加しない。

## 7. エラー契約

### 7.1 一覧

| 条件 | status | reason | title（実装の文言） |
| --- | ---: | --- | --- |
| query の欠落・重複・未知key・空値・書式・範囲・`page` の overflow | 400 | なし | リクエストの形式が不正です。（既存） |
| multipartのContent-Type、boundary、section（BadHttpRequestExceptionへ変換） | 400 | なし | リクエストの形式が不正です。 |
| 未変換のInvalidDataException | 400 | なし | multipart/form-data の形式が不正です。 |
| field の欠落・重複・未知、filename のない part | 400 | なし | リクエストの形式が不正です。 |
| `page` > ページ数（入力検査後） | 400 | なし | リクエストの形式が不正です。 |
| はみ出し（metadata 取得後） | 400 | なし | リクエストの形式が不正です。 |
| PDF > 50 MiB、画像 > 1 MiB、request > 53,542,912 bytes | 413 | なし | **PDF・画像またはリクエストのサイズ上限を超過しました。**（新規） |
| PNG でない・空、構造・CRC・IHDR・各辺0または2001以上・APNG・未知 critical、IDAT（zlib・Adler・filter・過不足）、検査済み PNG での pdfcpu exit 1 | 422 | `unsupported-image` | **この印影画像は処理できません。PNGの形式、破損や画像の大きさを確認してください。** |
| 正当な PNG だが、生成レイヤーが8 MiBを超える | 422 | `image-too-complex`（新規） | **印影画像の描画が処理上限を超えています。画像を小さくするか単純にしてください。** |
| PDF の破損・警告・暗号化・空 | 422 | なし | このPDFは処理できません。PDFの破損・パスワード設定や、画像が大きすぎないか確認してください。 |
| PDF の処理上限（metadata・object数・50 MiB・job・出力62 MiB） | 422 | `too-complex` | このPDFは描画の処理上限を超えています。（既存） |
| 対応外のページ属性（Rotate・Box・UserUnit・寸法） | 422 | `unsupported-pdf` | このPDFのページ属性は描画に対応していません。 |
| 同時実行の上限 | 503 | なし | PDF処理の同時実行上限に達しました。 |
| 共有30秒の超過 | 504 | なし | PDF処理が制限時間を超過しました。（既存。`images: false`） |
| pdfcpu の起動失敗（126/127）・signal・exit 2以上・空出力、生成PDFの検査失敗、managed I/O 障害、cleanup 失敗 | 500 | なし | PDF処理に失敗しました。（既存） |

### 7.2 pdfcpu の終了状態の判別

| 結果 | 判定 | 根拠 |
| --- | --- | --- |
| exit 0、stdout が上限以内で非空 | 成功。検査へ進む | ― |
| stdout が上限を超過（`null`） | 422 `image-too-complex` | 【事実】pipe には FSIZE が掛からず、runner の上限判定は確定的。blank は API 生成で小さく、PDF 側の要因はない |
| exit 1 | 422 `unsupported-image` | 【事実】pdfcpu の通常エラーは exit 1（壊れたPNGで確認）。PNG は .NET 側で構造と展開を検査済みで、blank・設定は API と起動時の自己テストで保証される。残る原因は「仕様上は正しい PNG を pdfcpu が読めない」場合であり、利用者には画像の非対応として返す（§13 決定5） |
| exit 2 | 500 | 【事実】Go runtime の fatal error（AS 不足など）は exit 2。最大の入力でも AS 1 GiB で再現しなかった |
| 126/127、負、128以上 | 500 | 起動失敗・signal（既存の扱いと同じ） |
| exit 0 で stdout が空、生成PDFの検査失敗 | 500 | 生成物の異常 |

stderr の文言から原因を推測しない（既存の方針と同じ）。

### 7.3 既存の文言を流用する際の問題

- `PdfImageInputException` の422は画像→PDF専用である。Unsupported は「JPEG・PNG」、Capacity は「画像から作るPDF」と言及しており、押印APIには合わない。【設計】stamp では `PdfStampImageException` に変換し、`PdfImageInputException` を endpoint へ漏らさない。漏れていないことをテストで確認する。
- 413 の文言は `images: true` だと「画像または」、`false` だと「PDFまたは」となり、どちらも片方しか言及しない。【設計】`tooLargeTitle` で押印用の文言を指定する。
- 504 の `images: true` の文言は「画像からPDFへの変換」である。【設計】stamp は `images: false` とする。
- Builder が `PdfcpuCapacityException` を変換した422 `too-complex` は「このPDFは」という文言である。【設計】画像に起因する超過には使わない。

## 8. テストと完了条件

### 8.1 実装済みの.NET単体テスト（tool不要）

- `StampPlacementTests`：必須query・ASCII書式・重複/未知key・overflow・範囲、表示寸法と縦横比、右下端の境界、はみ出し、縦長画像、culture、小さいscaleを0へ丸めないことを検査する。queryのテストもこのクラスに含む。
- `StampImageInputTests`：全depth/color type・Adam7の1×1/17×23 px、複数IDAT（空chunkを含む）、zlib/Adler破損、データの過不足、filter不正、trailer欠損、FDICT、各辺超過、metadataとuploadの削除を検査する。`PngDataValidator`専用のテストクラスは作らず、このクラスから`PrepareAsync`経由で検査する。
- 設定の純粋な検証は同クラス、`MaxStampImageBytes`の0/1 MiB超での起動拒否は`PdfcpuProcessorTests.InvalidOptions_PreventStartup`で確認する。
- fixtureは`StampFixtures`と既存`FromImagesFixtures.Chunk`で生成する。

### 8.2 実装済みの.NET実toolテスト（hostのqpdf・pdfcpu）

- `PdfStampImageTests.PlacesAtDisplayedPositionForEveryRotationCropAndUserUnit_OnlyTargetPageChanges`：7ページのfixture（Rotate 0/90/180/270/−90/630、CropBoxのずれ、UserUnit 0.5/1/2）へ1ページずつ押す。画像・Formの行列を合成し、3点を表示座標へ変換して1 pt以内で比較する。ページ数・ページ属性・対象外Contents・SMaskを確認する。
- `AcceptsAllPngDepthsAndAdam7IncludingPaletteTransparency`：全depth/color type・Adam7、gray/RGB/paletteのtRNSとalpha付き画像を実APIへ送り、成功と透過用SMaskを検査する。
- `SmallValidPngWithHugeLayer_ReturnsImageTooComplex`：2000×2000のUp filter画像が8 MiBを超え、422 `image-too-complex`とcleanupになることを検査する。
- `PdfErrorsAndToolFailuresKeepDistinctContracts`：PDFの空/破損/暗号化・unsupported-pdf・too-complex、toolのexit 1/2/126/127/137と内部500を確認する。
- 上限直前のレイヤーは.NETテストとは別に§8.5の実HTTP容量測定で確認した。しおり/Link/フォーム、継承Box、argvの詳細について、stamp専用の新しい検査を追加したとは扱わない。共通overlayの既存テストとは区別する。

### 8.3 実装済みのHTTP・cleanup検証（WebApplicationFactory）

- `InvalidQueryPrecedesUploadAndTools`：不正queryの400がuploadやrequest-stageのtool呼び出しより先に返る。
- `InvalidMultipartImageAndPlacement_ReturnExpectedProblems`：必須field、重複、未知field、filenameなし、不正PNG、各辺、page超過とはみ出し。正常系ではimage/fileの順序を入れ替える。
- `IndividualAndTotalUploadLimits_WorkWithAndWithoutContentLength`：partとrequest全量の413、Content-Lengthあり/なし。PDFのpart境界は小さく設定して確認する。
- `MaxPdfPlusMaxPng_IsAccepted_WithoutRaisingOtherEndpointsLimit`：xref手前に余白を入れた50 MiB PDFと、通常サイズのPNGを**IEND後の0埋めで1 MiBにした**入力が200になり、他endpointの上限は広がらないことを検査する。これは2000×2000の画像を使うテストではない。
- 正常応答のapplication/pdf・stamped.pdf、Problem Detailsのreason、利用者のfilename・metadata・内部pathが応答やログへ漏れないことを確認する。
- `TimeoutCancelAndBusySlot_DeleteImageAndWaitForProcessTree`と`EveryStageWaitsForChildrenAndDeletesStamp`：入力metadata・stamp描画・overlayでtimeout/cancel/アプリ停止を注入し、process treeの終了、印影を含むjob削除、503と共有枠の解放を確認する。送信中断専用のstampテストを追加したとは扱わない。

### 8.4 実装済みの描画比較（CIのdocker job、Poppler）

実行コマンドは `python3 scripts/stamp-image-validation.py IMAGE`。`--render`という引数はない。

- 実APIへ、CropBoxに原点のずれがある12ページ（Rotate 0/90/180/270 × UserUnit 0.5/1/2）のPDFを送る。RGBA 8 bitとRGBA 16 bit Adam7の四隅に色がある非対称PNGを、各対象ページへx/y=0.2、width=0.3で押す。
- OCGなしの入力24ケースに加え、Catalogの`/OCProperties`に入力側OCGと`/D /BaseState /OFF`を持つ入力24ケースを実行する。OFFレイヤーには全面赤の描画を置き、印影が表示され、入力側の赤は非表示のまま、出力のBaseStateもOFFのままであることを検査する。
- Popplerは `pdftoppm -cropbox -r 144`で描画する。実際のraster寸法から割合の期待位置を求め、四隅の赤・緑・青・黄、完全透明部分の灰色204、alpha 128の黒と下地の合成値102を、RGB許容差3以内で比較する。
- 対象ページの背景と、12 dpiで描いた非対象ページの全画素が灰色204（許容差1）を保つことを確認する。これは合成した灰色fixtureの確認であり、任意の元PDFとの全画素比較ではない。
- 配置行列の合成と表示座標への変換（3点、許容差1 pt）は.NETの`PlacesAtDisplayedPositionForEveryRotationCropAndUserUnit_OnlyTargetPageChanges`が担当する。Pythonの描画比較では行列を解析しない。
- `qpdf --check`、印影を含むjob削除も確認する。結果・48ケースのJSONを `docs/stamp-image-validation.md` と `docs/stamp-image-render-results.json` に記録する。pdf.js・Acrobatの確認はサイト#44に残る。

### 8.5 実施済みの容量・時間測定（手動、実HTTP）

`python3 scripts/stamp-image-validation.py IMAGE --capacity --output DIR`を実行する。`--capacity-image PNG_PATH`を付けると指定PNGをそのまま使い、幅・高さ・bit depth・color type・interlace・IENDまでのbytesと後続bytesを実データから記録する。固定値の画像属性を記録しない。

- 条件：CPU 1、memory 1536 MiB、swapなし、tmpfs 256 MiB、non-root、read-only root、network none。実APIの2件にそれぞれ既存の共有30秒deadlineを適用する。測定driverはhostで動き、cgroupのmemory.peakには含まない。
- PDFは既存overlay測定fixtureを50 MiBに調整したもの（50,000 reachable object、1000ページ、入力metadata 33,030,150 bytes）。対象は1000ページ目の14,400 pt四方、x/y=0.2、width=0.5。
- 既存測定は2000×2000・16 bit RGBA Adam7を、**IEND後の0埋めを含め1 MiBにした**入力。2件とも200、20.191 / 20.150秒、出力各52,385,464 bytes、memory.peak 745,349,120 bytes、OOM・残存job/process 0。uploadの上限と最大寸法を確認する測定であり、レイヤーの8 MiB直前を試すものではない。
- レビュー時の追加測定は、列ごとの乱数に行ごとに+1するUp filterの8 bit RGBA、2000×1395 px、PNG本体20,917 bytes（元driverのHTTP uploadはIEND後0埋めで1 MiB）。単体stampのレイヤー8,372,407 bytes（8 MiB上限の約99.8%）でも2件とも200、19.7 / 19.5秒、出力各60,678,901 bytes、memory.peak 759,238,656 bytes、OOM 0だった。提供された測定記録として検証文書へ追記する。
- 現行driverが測るのは、upload/downloadを含むHTTP時間・出力bytes・cgroup memory.peakとmemory.events・残存job/processである。台帳の制限はAPIで適用するが、stageごとのobserve、台帳peak、tmpfs使用量のサンプリングはこのdriverで記録しない。FSIZEもこの測定で再取得せず、既存の起動/runnerテストで確認する。
- 単独処理と最小ページの測定、任意の画像に対するmemoryの最大値は未確認。16 bit Adam7の1条件を「pdfcpu memoryの最悪値」とは扱わない。
- 合格基準は各処理24秒以内、OOM・残存ゼロ。結果は使用した合成入力の値であり、文字レイヤーや画像→PDFの測定を性能保証へ流用しない。

### 8.6 Docker smoke

- `scripts/docker-smoke.py`の`main`に押印のsmokeを追加済み。専用の`stamp_smoke`関数は作らない。
  - 固定の合成16×24 RGBA PNG を1ページの PDF に `page=1&x=0.7&y=0.1&width=0.12` で押し、200・`stamped.pdf` であることを確認する。
  - container内で`qpdf --check`と1ページ・Rotate 0を確認する。SMaskの構造検査は.NETテストで行う。非PNGの422もsmokeで確認する。
  - job が残っていないこと（`assert_clean`）を確認する。
- 起動時の自己テストに stamp を含めたことで、壊れた pdfcpu では起動に失敗することを確認する（既存の `assert_startup_failure` の方式）。

### 8.7 完了条件（Issueの完了条件に対応）

- 【Issue】Rotate 0/90/180/270・CropBox オフセット（＋UserUnit）で、表示上の位置・幅・向きが1 pt以内（§8.2、§8.4）。
- 【Issue】透過・半透明の保持（§8.2 の構造と §8.4 の描画）。
- 【Issue】query・画像の400/413/422の網羅（§8.1、§8.3）。
- 【Issue】qpdf の検査を通してから返す（Builder の既存処理と `CheckOutputAsync`）。
- 【Issue】成功・失敗・timeout・cancel で一時ファイルが残らない（§8.3）。
- 【Issue】Docker smoke に代表的な押印を追加（§8.6）。README・security の更新（§10）。
- 【Issue】Release build・test・Docker smoke の PASS。PR のセルフレビュー。
- 【追加】容量・時間の測定記録（§8.5）、描画比較スクリプトを CI で必須実行（§8.4）、起動時の stamp 自己テスト。

## 9. 変更ファイルと実装順序

| ファイル | 変更 |
| --- | --- |
| `src/Amane.Pdf.Api/PdfStampImageEndpoint.cs`（新規） | endpoint、`StampImageQuery` |
| `src/Amane.Pdf.Api/StampPlacement.cs`（新規） | 配置の純粋関数 |
| `src/Amane.Pdf.Api/StampImageInput.cs`（新規） | `StampImage`、`StampImageInput`、`PdfStampImageException` |
| `src/Amane.Pdf.Api/PngDataValidator.cs`（新規） | IDATの展開検査 |
| `src/Amane.Pdf.Api/PdfcpuProcessor.cs` | `StampImageAsync`、起動時の stamp 自己テスト |
| `src/Amane.Pdf.Api/PdfOverlayBuilder.cs` | `drawInputs`、`layerJson` |
| `src/Amane.Pdf.Api/MultipartPdfUpload.cs` | `ReadFileAndImageAsync` |
| `src/Amane.Pdf.Api/PdfEndpointHelpers.cs` | `tooLargeTitle`、`PdfStampImageException` の catch |
| `src/Amane.Pdf.Api/TemporaryPdfFiles.cs` | `StampUploadPath`、`StampImagePath` |
| `src/Amane.Pdf.Api/PdfOptions.cs`、`Program.cs` | 設定、検証、endpoint の登録 |
| `tests/Amane.Pdf.Api.Tests/`（`StampPlacementTests.cs`、`StampImageInputTests.cs`、`PdfStampImageTests.cs`、`StampFixtures.cs`、`PdfcpuProcessorTests.cs`） | §8.1〜§8.3、§8.5 |
| `scripts/stamp-image-validation.py`（新規）、`scripts/docker-smoke.py`、`.github/workflows/ci.yml` | §8.4、§8.6 |
| `README.md`、`docs/security.md` | §10 |
| `docs/stamp-image-validation.md`（新規） | 実測の記録 |
| `docs/stamp-image-design.md`（この文書） | 設計の正本。§13 決定6（Issueの案内は未反映） |

Dockerfile と THIRD_PARTY_NOTICES は変更しない（新しい依存はない）。

実装順序（PRは1本を想定）

1. `StampPlacement`・`StampImageQuery` と単体テスト。
2. `PngDataValidator`・`StampImageInput` とテスト。
3. Builder の `drawInputs`/`layerJson` とテスト（既存テストの回帰確認）。
4. `PdfcpuProcessor.StampImageAsync`・起動時の自己テストと実 tool テスト。
5. multipart・`ExecuteAsync`・endpoint・設定・HTTP テスト。
6. 描画比較スクリプト・Docker smoke・CI。
7. 容量・時間の測定と記録。README・security の更新。

## 10. 文書の更新内容

### README（「PDF電子印鑑（Linux）」の節を追加）

- API・curl の例・query の表・multipart（`file`、`image`、filename 必須、順序は自由）。
- 座標系：表示上（CropBox・Rotate・UserUnit を反映）のページの左上を原点とする、画像の左上の位置の割合。幅の割合。高さは縦横比から決まる。はみ出しは400。
- 上限：PDF 50 MiB、画像 1 MiB、各辺1〜2000 px、request 53,542,912 bytes、レイヤー 8 MiB、共有30秒・2枠。
- PNG の対応範囲（§5.3）。metadata・ICC・gamma の除去。APNG は対象外。
- エラーの表（§7.1）。
- 次の注意を明記する。
  - **電子印鑑は印影の画像をPDFに貼り付けるもので、電子署名ではない。改ざん防止・本人確認の効力はない。** 印影は PDF 編集ソフトで削除・複製できる。
  - 印影は透かし用の任意コンテンツ（OCG「Watermark」）・Artifact として追加され、viewer によっては透かしとして扱われる。
  - 注釈・フォームは印影より前面に表示される。
  - pdfcpu が画像に `/Interpolate true` を付けるため、PDF/A の要件を満たさなくなる（PDF/A-1 では `/SMask` の透過も不可。規格上の判断で、validator では未確認）。出力の PDF version は 1.7 になる（【事実】PDF 1.4 の入力で確認。レイヤーが 1.7 のため）。
- 設定の表に `Pdf__MaxStampImageBytes` を追加する。

### docs/security.md（「電子印鑑（Linux、#29）」の節を追加）

- PNG の判定と検査（sanitizer と展開検査）、metadata の除去、保存名 `stamp.png`。
- 一時保存と削除（0600、upload は sanitize 後に削除、draw 後に削除、cleanup の表）。保存・再利用はしない。
- 容量の制限（§6）と台帳への計上。
- pdfcpu の argv（job 内の path と計算した数値だけ）、stdout の上限、FSIZE=1、AS、ログに出さない情報。
- 既存の「描画modelは文字だけに限定し、…画像…を受け付けません」を次のように改める：「文字描画は文字modelだけ。画像は stamp API が検査した job 内の `stamp.png` だけを、pdfcpu の stamp に渡す」。

## 11. amane-tools-site#44 との接続条件

| 項目 | 条件 |
| --- | --- |
| 座標 | pdf.js の `page.getViewport({ rotation: page.rotate })`（view = CropBox ∩ MediaBox）の表示上の座標から、`x = left / viewport.width`、`y = top / viewport.height`、`width = 印影幅 / viewport.width` を求める。割合なので、viewport の scale と UserUnit の扱いに依存しない |
| 丸め | `x`・`y` は小数4桁へ**切り捨て**る。`x + width ≤ 1`、`y + width × (viewport.width / viewport.height) × (ph / pw) ≤ 1` をブラウザでも保つ。API の縦方向の許容差は 1e-6 pt |
| 書式 | ASCII の数字と `.` だけ。`page` は先頭0なし。key は小文字 |
| multipart | `file`（filename 付き）と `image`（filename 付き、例：`stamp.png`）。filename のない part は400 |
| request 上限 | site の中継は 53,542,912 bytes（50 MiB + 1 MiB + 64 KiB）を、Content-Length の早期判定・Kestrel の body 上限・転送 bytes の3か所で使う（site#44 の設計どおり） |
| 許可する reason | `unsupported-image` に加えて、採用する場合は `image-too-complex`。`too-complex`・`unsupported-pdf` の扱いは site の既存方針に従う |
| 400 の返金 | page 超過・はみ出しはアップロード後に判定されるため、`RefundBadRequest: true`（site#44 の設計どおり） |
| ブラウザ側の画像検査 | 1 MiB・各辺2000 px。canvas で作る印影（512×512、8 bit RGBA、非 interlace）は API の対応範囲内 |
| 表示確認 | 出力（`/SMask`、16 bit、OCG 付きの Form）の pdf.js での表示は【未検証】。site#44 のブラウザテストで確認する |
| Rotate | pdf.js は90の倍数でない Rotate を0として扱うが、API は422 `unsupported-pdf` を返す |

## 12. 検証の記録

### 12.1 検証済み事項（2026-10-13、`amane-pdf-api:issue28`、CPU 1・1.5 GiB・swap なし・read-only・network none）

| # | 内容 | 結果 |
| --- | --- | --- |
| V1 | `stamp add` の flag | `-m image` と `--mode image` は成功、`-mode image` は exit 1（`mode must be one of`） |
| V2 | stamp の配置（21 case） | 表示座標の誤差 ≤ 0.0001 pt（§4.4）。非対象ページは変化なし |
| V3 | create の配置 | 画素数を超えて拡大せず、最大250 ptずれた（§3.1） |
| V4 | 壊れた PNG | stamp は exit 1、create は exit 0 で画像を省略 |
| V5 | 形式（17種）の構造と描画 | 全形式が成功。alpha 128 は下地229の上で114。tRNS・palette alpha も透過 |
| V6 | 向き | Poppler で4隅の色を確認（回転4種・UserUnit・CropBox）。差は最大2 |
| V7 | 出力の構造 | Artifact・OCG・GS0（CA=1）・Form・Image（`/Interpolate true`、`/SMask`）。qpdf --check 成功 |
| V8 | 入力が OCG（BaseState OFF）を持つ場合 | Poppler で印影は表示、入力側の OFF レイヤーは非表示のまま |
| V9 | レイヤーのサイズ | 2000×2000 の通常の印影は 45,819 bytes、16 bit Adam7 は 76,494 bytes。Up filter の構成（PNG 26,532 bytes / 51,260 bytes）は 12,002,689 / 32,004,424 bytes で、8 MiB の FSIZE では pdfcpu が部分出力を削除して exit 1 |
| V10 | stdout 出力 | `-` で正常な PDF。FSIZE=1 でも成功し、file を作らない。pipe を閉じると exit 141（SIGPIPE） |
| V11 | 時間と memory（pdfcpu 単体、1 CPU、containerごとの cgroup memory.peak） | 2000×2000：8 bit 約0.16秒・約64 MiB、16 bit Adam7 約0.27秒・約119 MiB、16 bit 高entropy 約0.28秒・約181 MiB（出力を8 MiBで打ち切り）／約218 MiB（32 MB の出力を tmpfs へ書いた場合） |
| V12 | AS | 1 GiB で成功。256/128/64 MiB は Go runtime の起動失敗で exit 2 |
| V13 | 数値の書式 | 小数12桁の scale、`-0` を受理。`rot` の省略で対角回転 |
| V14 | 極端な寸法 | 14,400 pt のページに1×1 px を scale 7200 で配置、3 pt のページに2000 px を scale 0.00003 で配置。どちらも成功 |
| V15 | PDF version | PDF 1.4 の入力でも、overlay 後の出力は 1.7 |
| V16 | .NET の IDAT 検査（試作） | 正常な全 fixture を通し、zlib 破損・Adler 不一致（`InvalidDataException`）、データ不足（`EndOfStreamException`）、データ過剰、不正 filter を検出。32 MB の展開で約9 ms（host） |
| V17 | runner の stdout 読み取り（同じアルゴリズムの試作） | 8 MiB で148 ms、12 MB で上限超過（`null`）を197 ms（host） |

再現方法（Docker・host の python3、numpy、Pillow、qpdf、pdftoppm が必要）

```bash
sh docs/stamp-image-design-probe/reproduce.sh amane-pdf-api:issue28 "$(mktemp -d)"
```

- `make_png.py`：PNG fixture（各形式、破損、2000 px、高entropy）を生成する。
- `make_cases.py`：白紙・対象ページの qpdf JSON と、stamp/create の入力を生成する。
- `docker-matrix.sh`：container 内で stamp と create、overlay、`qpdf --check` を実行する。
- `analyze.py`：行列を合成して表示座標の誤差を出す。
- `IdatCheck.cs`：V16 の試作（`dotnet new console` の `Program.cs` として実行）。

V1〜V4・V9 は `reproduce.sh` で再現できる。V8・V10〜V15 は同じ image での個別の docker 実行、V5・V6 は結果PDFの Poppler 描画、V16・V17 は host の .NET 試作で確認した。これらの個別手順は、実装PRの `scripts/stamp-image-validation.py` と .NET テストへ取り込む。probe は CI では実行しない。

### 12.2 設計調査時点の未検証事項（実装後の結果は検証記録を参照）

| 項目 | 確認方法 |
| --- | --- |
| pdf.js・Acrobat などでの表示（`/SMask`、16 bpc、未登録の OCG、Artifact） | site#44 のブラウザテスト、Human の受け入れ確認 |
| 上限規模の PDF と最大画像の同時2件の時間・memory | §8.5 |
| gray 2/4 bit、palette 1/2 bit、16 bit+tRNS、RGB の PLTE 付き | §8.1・§8.2 |
| IDAT の最後で zlib の終端後にデータがある場合の Go の挙動（.NET の検査では検出できない） | 実 tool テスト（exit 1 なら422、成功なら無視） |
| zlib の preset dictionary（FDICT）での .NET の例外 | 単体テスト |
| 実 API の Builder 経由での配置（probe は Builder の調整後の状態を再現したもの） | §8.2 |

## 13. 決定状況と残る対応

| # | 事項 | 決定・実装状況 | 理由 |
| --- | --- | --- | --- |
| 1 | stamp が追加する OCG・Artifact | 採用済み。除去せずREADMEに記載。PopplerのOFFレイヤー回帰検証を追加。pdf.js/Acrobatは未確認 | create は拡大できず使えない。除去には qpdf の処理が1段増える。印影に改ざん防止の効力はない。Poppler では入力の OCG に関係なく表示される |
| 2 | 画像起因のレイヤー超過の reason | APIは`image-too-complex`で実装済み。サイト#44の許可・伝達は未対応 | 利用者が取るべき対処（画像を単純にする）が `unsupported-image`（形式・破損）と異なる。site の許可 reason に1つ追加が必要 |
| 3 | 空の `image` | 422 `unsupported-image`で実装済み | PNG signature がない入力として、他の非 PNG と同じ扱いにする。画像→PDF の「reason なし422」は複数画像のための区別であり、単一画像の押印では reason があるほうが site で扱いやすい |
| 4 | 16 bit・Adam7・palette・gray・tRNS | 採用済み。全depth/color typeとAdam7・tRNSを実toolで検証 | 実 tool で成功と透過を確認した。利用者が持つ既存の印影 PNG を拒否しない。pdf.js での表示は site で確認する |
| 5 | 検査済み PNG での pdfcpu exit 1 | 422 `unsupported-image`で実装済み。専用Warningは未実装（§6.5） | 入力のうち利用者に由来するのは PNG だけである。500 にすると利用者が再試行を繰り返す。stdout/stderrや入力由来の診断情報は記録しない |
| 6 | 設計の正本の置き場所 | PR #64に本書を追加済み。Issue末尾の旧設計から本書への案内への置換は未反映 | Issue とコードの乖離を防ぐ |
| 7 | はみ出しの許容差 | 実装済み。縦方向1e-6 pt、横方向はdecimalで厳密 | 端にちょうど接する指定を、浮動小数点の誤差で400にしない |
| 8 | query key の大文字小文字 | 区別しない仕様で実装済み（既存APIと同じ） | 一貫性 |
| 9 | 印影の重なり順 | 採用済み。本文の前面、注釈・フォームの背面（overlay方式の既定） | 既存の透かしと同じ |

## 14. セルフレビュー

- **重複の排除**：入力の `qpdf --check`・ページ数・Box・UserUnit の取得は Builder の1回だけで、endpoint は再取得しない。PNG の展開は .NET（ストリーミング）と pdfcpu の2回になる。.NET 側は判別のための検査で、画素を保持しない。
- **既存契約への影響**：Builder の追加引数は既定値で従来どおり動く。`ExecuteAsync` の `tooLargeTitle` は省略時に従来の文言になる。他 API の request 上限・sanitizer・runner は変えない。
- **判別の確定性**：容量超過（stdout が `null`）は確定的に判定できる。exit 1 を422とするのは推論であり、その前提（PNG の完全な検査、blank と設定の保証）を明記して、決定5として採用した。
- **安全性**：argv に利用者の文字列が入らない。印影は0600の一時ファイルとメモリだけに置く。FSIZE=1 で予期しない書き込みを止める。ログには固定文言だけを出す。
- **リスク**：上限規模PDFの同時2件は§8.5の2条件で各24秒以内だった。任意入力の保証には使わない。OCGのPoppler表示は回帰検証済みで、pdf.js/Acrobatは未確認である。
- **範囲外**：複数画像・複数ページ、印影の生成、電子署名、PNG 以外、UI、deploy は扱わない。from-images への IDAT 検査の適用も扱わない。
- **自己矛盾の確認**：Issue の「image は PNG のみ・1 MiB・2000 px」「request 上限」「stamped.pdf」「保存しない」「README の注意」をすべて §5〜§10 に反映した。Issue 草案の正規表現と §5.1 の文字検査は、範囲の検査後に同じ値の集合を受理する。

## 15. 実装への反映

- `PdfStampImageEndpoint`、`StampImageInput`、`PngDataValidator`、`StampPlacement`を追加した。PNGの構造検査には既存PngSanitizerを使い、from-imagesの契約は維持した。
- `drawInputs`と`layerJson`を追加し、印影を共通台帳へ載せ、JSONを使わない描画を受け付ける。stamp用起動自己テストも追加した。
- 画像起因のレイヤー上限は`image-too-complex`、入力不正は`unsupported-image`にした。サイト#44の対応は別作業である。
- 数値はInvariantCultureのround-trip形式へ変更し、IDAT検査は1 MiB以下のencodedデータだけを保持する簡潔な方式にした。
- `scripts/stamp-image-validation.py`に実HTTPの描画比較と最大入力同時2件の測定をまとめた。設計probeは履歴・方式比較用であり、CIでは実装を通る検証を使う。
