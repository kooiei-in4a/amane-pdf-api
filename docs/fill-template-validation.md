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

## 差し込みAPI（Issue #78）

2026-10-19、main `79bd4e8` を基点として検証した。pdfcpu 0.16.1、qpdf 12.3.2、BIZ UDPゴシック v1.051を維持した。API本体の実装と検証はこの節以降を参照する。

### 再現手順

Release build / testの後、次を実行する。Poppler、Pillow、NumPyはホスト側の検証専用。MuPDFはPyMuPDF 1.26.3、署名生成はpyHanko 0.29.1へ固定し、本番imageには含めない。署名とローカルTSAの鍵・証明書は実行時の一時ディレクトリに生成し、秘密鍵は0600、成功・失敗ともディレクトリごと削除する。

```bash
docker build -t amane-pdf-api:ci .
python3 -m venv --system-site-packages /tmp/amane-fill-template-validation
/tmp/amane-fill-template-validation/bin/pip install -r scripts/fill-template-validation-requirements.txt
/tmp/amane-fill-template-validation/bin/python scripts/fill-template-validation.py amane-pdf-api:ci --results /tmp/fill-template-capacity.json
```

スクリプトはHTTP検証に加え、既存overlay検証と同じ方式でReleaseのtest assemblyとSDK runnerをread-only mountする。`FILL_TEMPLATE_CAPACITY_INPUT` / `FILL_TEMPLATE_CAPACITY_FIELDS` を指定したときだけ重いBuilder試験が動くため、既定の`dotnet test`で上限規模の試験を増やさない。

### 契約と判断

- HTTPテストはfile/fieldsの両順序、未知・重複・欠落・filename条件、query、8 key、数値字句と境界、件数・UTF-16長・行数・ページ行数、ページ番号超過、413・503・504、キャンセルを検査する。fieldsの64 KiBちょうどは日本語を含むJSONをUTF-8 bytesとして埋めて作る。サイズ違反が意味検査より先になること、無効fieldsではtoolを起動しないことも確認する。
- 空白は文字表にあるUnicode White_Space（U+0020、U+00A0、U+3000）とLF。表を全走査してこの集合を固定し、空白だけは拒否する。CR、tab、不正UTF-8/BOM/UTF-16、補助平面、異体字セレクタ、未収録文字、禁止した並びを400にする。trim・正規化はしない。
- 配置と比較はdecimal。表示寸法を一度decimalへ変換し、pdfcpuへの出力時だけdoubleに戻す。12 ptの「日」の幅12 ptと左右余白1.2 ptを含む13.2×12 ptは成功、幅13.1 pt・高さ11.9 ptはfield-overflow。単体テストではこれに加え幅0.0000005 ptの超過も拒否する。
- 文字数上限は`%`を書き換える前。`%a`×100は元200 UTF-16単位なので許可し、書き換え後300単位でも描画・抽出が一致する。199の呼出し元上限と既定128の呼出し元では拒否する。JSON bytes予算は書き換え後にも適用する。
- `field-overflow`は固定文言とreasonだけを返し、項目番号を返さない。設計書§5.3へ反映した。
- ページ下端では計算したpdfcpu座標が負になり得る。負のpos.yはページ中央指定として解釈され、通常ページでは約半ページ、最小ページでは押し戻しにより約0.57 ptずれることを追加実測した。負の場合だけpos.y=0とdy=計算した負座標に分け、実座標を保つ。6/6.1/12/72 pt×3揃えと13.2×12 ptの最小ページで実baselineを0.00002 pt以内に検査した。設計書§6.3の送信表現変更としてPRの判断事項に記載した。
- 空行は矩形の高さとページ100行上限に含め、字形のない行の描画要素は生成しない。非空行は1行1要素で、anchor・width・折り返し・pdfcpuの揃えは使わない。

### 配置・抽出・フォーム

| 対象 | 結果 |
| --- | --- |
| A4縦・横、A3、CropBox原点ずれ、Rotate 90/180/270、UserUnit 2/0.5、B5、はがき、寸法混在 | 11ページ×3揃え×2行、66ベースラインでPASS。非対象の表紙も保持 |
| 実content stream | pdfcpuのcmとTdを読み、設計式のbaselineと0.00002 pt以内。qpdfの合成行列をCropBox/Rotate/UserUnitから独立に計算して照合 |
| Poppler描画 | 144 dpiで字形枠内のinkと枠外の空白を確認。UserUnitを換算し、字形のside bearingと画素丸めを含む左端誤差は2.5 pt以内 |
| 100種を超える文字の抽出 | 320種の漢字を含む複数ページで、MuPDF・Popplerの双方が全文字一致 |
| `%` | 末尾・連続・数字の前・`%a`×100、結合濁点、バックスラッシュ、①㈱￥※〒を元文字として抽出 |
| 承認署名 | pyHankoによる実署名を無変更で送信し、422 form-fields |
| DocMDP（認証署名） | pyHanko certifyによる実署名を無変更で送信し、422 form-fields |
| 文書タイムスタンプ | ネットワークなしのDummyTimeStamperによる実タイムスタンプを無変更で送信し、422 form-fields |
| UR3（Reader拡張） | qpdf JSONでPerms/UR3構造を作り、422 form-fields |
| 入力欄のみ | qpdf JSONで通常の間接参照のTx/Widgetを作り、422 form-fields |
| XFA | qpdf JSONでAcroForm/XFA構造を作り、422 form-fields |
| 空のAcroForm | 空辞書でも422 form-fields |
| AcroFormから参照されない直接Widget | Annots配列へ直接埋め込み、422 form-fields |

上記8種類はobject streamを生成した版でも拒否した（合計16/16）。実署名版の書換えは署名を無効にするため、署名が有効なままの原本も別に検査する。単体・HTTPテストには#xxエスケープ、入れ子辞書・配列、stream辞書、FT Sig / Type Sig / Type DocTimeStamp、フォーム検出がページ番号超過より先になる条件を含めた。

ToUnicodeはレイヤーごとに毎回参照番号を取得し、Type0が0個・2個なら失敗する。CMapの100件ブロック分割、元の`%`・結合文字との対応を単体検査し、metadata取得・フォント検出・更新toolの失敗は固定500にする。入力検査とToUnicode更新中のタイムアウト・キャンセルでもprocess treeの終了を待ち、jobを削除する。成功時も中間ファイルが残らないことを検査した。

### 設計書§10との負荷条件の対応

DockerはCPU 1、memory 1.5 GiB、swapなし、read-only、tmpfs 256 MiB、非root、cap-drop ALL。入力PDF、fieldsと実測の詳細は[測定JSON](fill-template-capacity.json)に記録した。入力のpaddingはCatalogから参照される未圧縮streamで、合成後は圧縮される。任意の50 MiB PDFの成功保証ではない。

| §10の条件 | fixture / 結果 |
| --- | --- |
| 全1,000要素 | 200項目×5非空行=1,000要素 |
| 1ページ100要素×10ページ | 各20項目、ページ1〜9と1000に100行ずつ |
| 異なる字形を最大限含む | 許可表の全11,425字形。fields 55,555 bytesで64 KiB内 |
| 72 pt | 全項目72 pt |
| 描画範囲1,000ページ | 最小1・最大1000、間の白紙も生成 |
| 入力50 MiB | 52,428,800 bytesちょうど |
| ToUnicode再作成を含む30秒以内 | HTTP 2.813秒。Builder単独2.023秒、同時2件4.223/4.288秒 |
| job容量 | 予約ピーク120,578,048 bytes（約115 MiB）/job、上限130,023,424 bytes（124 MiB）。HTTPの実ファイル使用量の標本ピーク56,238,080 bytes |
| メモリ | HTTPコンテナ212,398,080 bytes、単独・同時2件の測定ランナー込み417,615,872 bytes。OOM/oom_killとも0 |
| 出力と後始末 | HTTP出力3,541,850 bytes・1,000ページ。中間物・job・子processの残存0、health正常 |

実ファイル標本は50 ms間隔の要求とDocker execの所要時間を含むため、厳密な瞬間最大値ではない。容量台帳の予約ピークはBuilderのobserveから直接取得し、Unicode metadata・更新JSON・修正レイヤーを含む。書込み前予約・FSIZE sentinel・4 KiB切上げ・実サイズ確定は既存OverlayCapacityを使う。

### コマンドと既定テスト時間（初回測定）

| コマンド | 最終結果 |
| --- | --- |
| `dotnet build --configuration Release` | PASS、警告0・エラー0 |
| `dotnet test --configuration Release` | PASS、1,355件、5分20秒。関連36件もPASS（9秒）。下端補正を含む最終全テストとCIの結果・時間は[PR #81](https://github.com/kooiei-in4a/amane-pdf-api/pull/81)に記録 |
| `docker build -t amane-pdf-api:ci .` | PASS |
| `python3 scripts/docker-smoke.py amane-pdf-api:ci` | PASS、36 POST checks、60.126秒 |
| `python3 scripts/fill-template-validation.py amane-pdf-api:ci` | 固定版venvでPASS。配置・文字抽出・8種フォーム・単独/同時2件負荷・削除・health |
| `python3 scripts/fill-template-glyphs-validation.py amane-pdf-api:ci` | PASS、9.262秒 |
| `python3 scripts/overlay-validation.py amane-pdf-api:ci --render` | PASS、4.238秒 |
| `python3 scripts/watermark-validation.py amane-pdf-api:ci` | PASS、48.054秒 |
| `python3 scripts/page-numbers-validation.py amane-pdf-api:ci` | PASS、11.614秒 |
| `python3 scripts/stamp-image-validation.py amane-pdf-api:ci` | PASS、22.125秒 |

比較用の`79bd4e8`は同一ホストで1,334件・5分28秒だった。今回1,355件との差は21件、実時間は8秒短かったが、実行時の負荷・キャッシュ等による変動を含むため高速化を示す数値ではない。差し込みと文字表を含む関連36件は約9〜10秒。通常CIのbuild-testは従来どおり10分のまま、上限負荷はDocker jobから有効化する。

ホストのqpdfには既存CIの`Prepare host qpdf`と同じ専用コピーを使用した（/usr/bin/qpdfのAppArmor profileが拡張子なしのraw出力を拒否する環境への対応）。Dockerは製品imageのqpdfを使用した。基点mainの[CI実行](https://github.com/kooiei-in4a/amane-pdf-api/actions/runs/39191721366)はbuild-test 351秒（Test step 301秒）、docker 301秒。今回のCI全jobの状態と所要時間は実装PRの検証結果へ記録する。
