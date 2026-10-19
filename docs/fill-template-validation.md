# PDFテンプレート差し込み 検証記録

## 文字表（Issue #77）

2026-10-19に、main `f0c1c93` を起点とする実装commit `40e3848` で検証した。対象は固定フォント文字表のみ。API本体、文字列の規則、配置計算、ToUnicodeの作り直しは #78、サイトへの取り込みはサイト #125 で扱う。

### 生成手順

Repository rootで実行する。fontToolsは開発専用であり、CIや本番imageには追加しない。今回使用したversionは4.67.0。

```bash
scripts/install-pdfcpu.sh /tmp/amane-pdfcpu
python3 -m venv /tmp/amane-fonttools
/tmp/amane-fonttools/bin/pip install fonttools
/tmp/amane-fonttools/bin/python scripts/fill-template-glyphs.py /tmp/amane-pdfcpu/fonts/BIZUDPGothic-Regular.ttf
/tmp/amane-fonttools/bin/python scripts/fill-template-glyphs.py /tmp/amane-pdfcpu/fonts/BIZUDPGothic-Regular.ttf --check
/tmp/amane-fonttools/bin/python scripts/watermark-glyphs.py /tmp/amane-pdfcpu/fonts/BIZUDPGothic-Regular.ttf --check
```

`install-pdfcpu.sh` が配布資材とTTFのsha256を照合する。生成スクリプトも `third_party/pdfcpu.lock` の `font_sha256` とTTFの一致を検査し、`unitsPerEm != 2048` は拒否する。ハッシュ不一致の入力を拒否することも開発環境で確認した。

新しい表の `--check` は既存ファイルを書き換えず、生成予定のUTF-8 bytesと完全一致することを確認する。成功時の出力は次のとおり。透かしの `--check` は終了code 0、標準出力なし。

```text
Verified fill template glyph table: 11425 entries, 68550 bytes.
```

### 収録規則と形式

`getBestCmap()` のGIDが0でない文字を起点とする。補助平面、U+FE00–FE0F、一般カテゴリCc、サロゲートは収録しない。LFも表には入れず、#78で別に扱う。字形の共有は補助平面を含むcmap全体から検出し、同じ非0 GIDを持つ組の全文字を除外する。

| 内訳 | 件数・内容 |
| --- | --- |
| cmap | 11,741字、GID 0は0件 |
| 補助平面を除外 | 314字を除外し、BMP 11,427字 |
| 字形共有を除外 | U+02DC / U+0303（どちらもGID 1343）の2字 |
| 補助平面の共有組 | U+29FCE / U+29FD7。補助平面の規則で除外済み |
| 異体字セレクタ・Cc・サロゲート | cmap内0件。除外規則とテストは維持 |
| 収録 | 11,425字（Mn 33字を含む） |

これらの件数と共有組は生成時のassertで固定している。送り幅は `hmtx advance * 1000 // 2048`（1/1000 em単位）。pdfcpuの `pkg/font/install.go` と同じ整数の切り捨てとする。

生成ファイルは `src/Amane.Pdf.Api/FillTemplateGlyphs.g.cs`。scalar昇順で1件6 byte、`u16 scalar / u16 GID / u16 advance` をlittle-endianで並べる。全68,550 byteをbase64化し、100文字ごとの文字列リテラルを `Convert.FromBase64String` に渡す。改行はLFで、同じTTFから同じbytesを生成する。

- `font_sha256`: `595dfefa96dcb281c40f5d560841f3fc623b0d9d12064481a7daf78044462673`
- `data_sha256`: `2ac4f132bf74722ae85e0f2256ede923a34962a6ea52e8f8a414bbcc999ac1ce`（base64復号後のbytes）
- `entries`: `11425`

サイトはヘッダーと文字列リテラルを正規表現で読み、base64を復号するだけで取り込める。C#の実行や構文解析は必要ない。APIは `internal static bool TryGet(int scalar, out ushort glyphId, out ushort advance)` の二分探索のみ。初期化はbase64復号だけで、辞書などを構築しない。

### 実pdfcpuでの照合

```bash
docker build -t amane-pdf-api:ci .
python3 scripts/fill-template-glyphs-validation.py amane-pdf-api:ci
python3 scripts/watermark-validation.py amane-pdf-api:ci
```

新しい検証はPython標準ライブラリだけを使う。既存の透かし検証にはCIと同じPoppler、Pillow、NumPyが必要。今回のホストでは `/usr/bin/python3` で透かし検証を実行した。

本番image内の `/opt/amane-pdf/bin/pdfcpu`（0.16.1）を、ラッパーを挟まず直接呼ぶ。コンテナは既存の検証harnessでCPU 1、メモリ1.5 GiB、swapなし、読み取り専用root、256 MiBのtmpfsとして起動する。`mktemp -d` で0700の作業ディレクトリを作り、JSONは0600で渡し、成功・失敗とも `finally` で削除する。

各文字を独立した要素として、1ページ最大256要素で描く。qpdf JSONで各要素の `Tj` を読み、PDF文字列のescapeを復号したIdentity-Hの2 byte GIDを照合する。幅は使用したCIDFontの `/W`（省略時は `/DW`）を読み、表と整数で比較する。Unicode抽出はToUnicodeの不具合があるため判定に使わない。

| 検証 | 2026-10-19の結果 | 所要時間 |
| --- | --- | --- |
| 収録全文字 | 11,425字を45ページに描画。欠落0、GID不一致0、幅不一致0 | 0.70秒 |
| BMP全域（サロゲート・Ccを除く） | 63,423字を検査。描画11,427字が「表 ∪ {U+02DC, U+0303}」と完全一致。収録字のGID・幅も再照合 | 7.14秒 |
| 全検証 | コンテナ起動・終了、特別扱いの確認、後片付けを含む | 10.08秒 |

BMP全域の検査は、1回4,096字以下のPDFに分割し、pdfcpuとqpdf JSONの1回あたりの処理量を抑える。異体字セレクタもこの検査対象に含み、描画されないことを確認する。

### 報告対象の文字

次の文字はいずれも `Tj` が空でなく、GIDと送り幅が表に完全一致した。空白についてはインクの有無ではなく、文字と送り幅が保持されたことを確認している。

| 文字 | GID | 送り幅（1/1000 em） | 結果 |
| --- | --- | --- | --- |
| U+00AD soft hyphen（Cf） | 1655 | 839 | そのまま1字形を出力。除外・書き換えなし |
| U+0020 SPACE | 1256 | 333 | そのまま1字形を出力 |
| U+00A0 NO-BREAK SPACE | 1257 | 333 | そのまま1字形を出力 |
| U+3000 IDEOGRAPHIC SPACE | 1248 | 666 | そのまま1字形を出力。このフォントの実際の幅であり1000には補正しない |
| U+3099 結合濁点（Mn） | 13811 | 1000 | そのまま1字形を出力。Mn全33字についても欠落0 |
| U+0025 `%` | 1262 | 1000 | 検証入力では `%%` にして渡す |
| U+005C `\` | 1323 | 500 | 単独でそのまま描画できる |

`%` 1個をそのまま渡す追加検証では、要素自体が消え、`Tj` も出なかった。`%%` にすると1字分のGID 1262を出力する。§6.4に従うこの変換以外に、文字の特別扱いはない。API側の `%` 書き換えや禁止する並びの検証は #78 の範囲であり、ここでは変更していない。

### 単体テスト・既存機能との整合

`FillTemplateGlyphsTests` ではヘッダーのfont hashとlock、データhash、件数、scalarの順序・重複、全GIDの非0・一意性、幅1〜1000、独立計算の代表10値、禁止範囲と必要な記号を確認する。全文字についてC#の検索結果と埋め込みbytesを比較する。

透かし用の表は読み取りだけとし、`WatermarkText.IsDrawable` がtrueの全文字が新しい表にも存在し、ASCII幅が `WatermarkGlyphs.AsciiAdvances[i] * 1000 / 2048` と一致することを確認する。

```bash
dotnet build --configuration Release
dotnet test --configuration Release
git diff --exit-code origin/main -- scripts/watermark-glyphs.py scripts/watermark-validation.py src/Amane.Pdf.Api/WatermarkGlyphs.g.cs src/Amane.Pdf.Api/WatermarkText.cs
```

ホストのRelease testには `.github/workflows/ci.yml` のbuild-test jobと同じ固定pdfcpu・PDFium・JPEGツール・qpdfが必要。今回もそれらを用意し、`Pdf__PdfcpuPath`、`Pdf__PdfcpuConfigDir`、`Pdf__RenderFontDir`、`Pdf__PdfiumPath`、`Pdf__DotnetPath`、`Pdf__RendererPath` をホスト上の対応する絶対パスに設定した。qpdfはCI同様に `/usr/bin/qpdf` のコピーをPATHの先頭に置いた。

CIではfontToolsと `--check` を使わず、build-test jobの単体テストで生成データの整合を確認し、docker jobの新しい検証stepで実フォントとの一致を確認する。
