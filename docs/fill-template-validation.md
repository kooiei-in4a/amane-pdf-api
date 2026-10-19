# PDFテンプレート差し込み 検証記録

現行の固定版はpdfcpu 0.16.2で、そのToUnicodeを直接使う。現在の結果は末尾の「ToUnicode回避処理の削除後の検証」を参照。更新時の比較、#77/#78の測定は当時の記録として保持する。

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

2026-10-19、レビュー修正後の測定対象commitは[`8bae618`](https://github.com/kooiei-in4a/amane-pdf-api/commit/8bae618e5566f40a696d0c52e1095e3ac5017a96)。Python lock・CI設定・検証スクリプトをこのcommitで確定して全コマンドを再測定した。後続commitは結果の文書・JSON記録だけで、実行構成は同じ。pdfcpu 0.16.1、qpdf 12.3.2、BIZ UDPゴシック v1.051を維持した。

### 再現手順

Release build / testの後、次を実行する。Popplerはホストの外部コマンドとして使用する。Python検証ライブラリはPillow・NumPyを含め、全間接依存のversionとSHA-256をlockへ固定する。system-site-packagesを使わない新規venvへ--require-hashesと--only-binary=:all:で導入し、未固定のビルド依存も取得しない。MuPDFはPyMuPDF 1.26.3、署名生成はpyHanko 0.29.1を維持し、本番imageには含めない。署名とローカルTSAの鍵・証明書は実行時の一時ディレクトリに生成し、秘密鍵は0600、成功・失敗ともディレクトリごと削除する。

```bash
docker build -t amane-pdf-api:ci .
python3 -m venv /tmp/amane-fill-template-validation
/tmp/amane-fill-template-validation/bin/pip install --require-hashes --only-binary=:all: -r scripts/fill-template-validation-requirements.txt
/tmp/amane-fill-template-validation/bin/python scripts/fill-template-validation.py amane-pdf-api:ci --results /tmp/fill-template-capacity.json
```

lockはLinux x86_64 / Python 3.14を対象とし、直接依存を`.in`、全依存とハッシュを`.txt`に記録する。固定版pip-toolsでの再生成手順はlock先頭に記載した。venvのpipとPython自体、Poppler/qpdf、Docker、.NET、ほかの検証スクリプトが使うホストのPIL/NumPyはこのPython lockの対象外。差し込み検証ではホストのPIL/NumPyを参照せず、lock内の版を使う。

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

DockerはCPU 1、memory 1.5 GiB、swapなし、read-only、tmpfs 256 MiB、非root、cap-drop ALL。入力PDF、fieldsと実測の詳細は[測定JSON](fill-template-capacity.json)に記録した。入力のpaddingはCatalogから参照される未圧縮streamで、合成後は圧縮される。任意の50 MiB PDFの成功保証ではない。時間は小数点以下3桁に丸め、JSONには生値も保持する。

| §10の条件 | fixture / 結果 |
| --- | --- |
| 全1,000要素 | 200項目×5非空行=1,000要素 |
| 1ページ100要素×10ページ | 各20項目、ページ1〜9と1000に100行ずつ |
| 異なる字形を最大限含む | 許可表の全11,425字形。fields 55,555 bytesで64 KiB内 |
| 72 pt | 全項目72 pt |
| 描画範囲1,000ページ | 最小1・最大1000、間の白紙も生成 |
| 入力50 MiB | 52,428,800 bytesちょうど |
| ToUnicode再作成を含む30秒以内 | HTTP 3.359秒。Builder単独2.101秒、同時2件4.561/4.508秒 |
| job容量 | 予約ピーク120,578,048 bytes（約115 MiB）/job、上限130,023,424 bytes（124 MiB）。HTTP実ファイル標本ピーク59,215,872 bytes |
| メモリ | HTTPコンテナ207,147,008 bytes、単独・同時2件の測定ランナー込み378,523,648 bytes。OOM/oom_killとも0 |
| 出力と後始末 | HTTP出力3,541,850 bytes・1,000ページ。中間物・job・子processの残存0、health正常 |

実ファイル標本は50 ms間隔の要求とDocker execの所要時間を含むため、厳密な瞬間最大値ではない。容量台帳の予約ピークはBuilderのobserveから直接取得し、Unicode metadata・更新JSON・修正レイヤーを含む。書込み前予約・FSIZE sentinel・4 KiB切上げ・実サイズ確定は既存OverlayCapacityを使う。

ToUnicodeの作り直しの途中でjob容量・metadata等の上限を超えた場合も、現行のcatch (Exception)によって500になる。この箇所では422 too-complexにしない。今回の最大負荷でのjob予約ピークは120,578,048 bytesで、上限の124 MiB（130,023,424 bytes）には届かなかった。APIコード・例外処理は変更していない。

### コマンドと既定テスト時間

| コマンド | 結果 | 所要時間（コマンド全体） |
| --- | --- | --- |
| `dotnet build --configuration Release` | PASS、警告0・エラー0 | 8.389秒 |
| `dotnet test --configuration Release` | PASS、1,355件、test runner表示 5 m 37 s | 341.637秒 |
| `docker build -t amane-pdf-api:ci .` | PASS | 2.444秒 |
| `python3 scripts/docker-smoke.py amane-pdf-api:ci` | PASS | 64.180秒 |
| `python3 scripts/fill-template-validation.py amane-pdf-api:ci` | PASS、ハッシュ固定の隔離venv。配置・抽出・8種フォーム・単独/同時2件負荷・削除・health | 24.103秒 |
| `python3 scripts/fill-template-glyphs-validation.py amane-pdf-api:ci` | PASS | 10.927秒 |
| `python3 scripts/overlay-validation.py amane-pdf-api:ci --render` | PASS | 4.670秒 |
| `python3 scripts/watermark-validation.py amane-pdf-api:ci` | PASS | 51.699秒 |
| `python3 scripts/page-numbers-validation.py amane-pdf-api:ci` | PASS | 12.726秒 |
| `python3 scripts/stamp-image-validation.py amane-pdf-api:ci` | PASS | 19.172秒 |

上表・負荷表・測定JSON・PR本文は同じ再測定から転記した。測定commitは`8bae618e5566f40a696d0c52e1095e3ac5017a96`、lockのSHA-256は`8e96c999eace40e56962eec35b52a09a8e2cad4c046d99109f31643509e07d34`。JSONのmeasurementにコマンド全体の時間・version・検証対象を記録した。

比較用の`79bd4e8`では1,334件・5分28秒だった（以前の測定）。今回は1,355件、test runner表示5 m 37 s。ホスト負荷・キャッシュ等の変動を含むため、時間差をこのレビュー修正だけの影響とは解釈しない。通常CIのbuild-testは従来どおり10分のまま、上限負荷はDocker jobから有効化する。

新規venvへの全19ライブラリの--require-hashes導入、pip check、改変したSHA-256が拒否されることを確認した。PIL・NumPy・MuPDF・pyHanko・cryptography・asn1cryptoが隔離venv内から読み込まれ、system-site-packagesを参照しないことも確認した。

ホストqpdfには既存CIのPrepare host qpdfと同じ専用コピーを使用した。Dockerは製品imageのqpdfを使用した。結果記録を含むPR最終headのCI全jobと所要時間は[PR #81](https://github.com/kooiei-in4a/amane-pdf-api/pull/81)に記録する。


## 0.16.2 への更新の検証（Issue #82）

2026-10-19 JST、比較元`87acad7`。旧版の#77/#78測定と [fill-template-capacity.json](fill-template-capacity.json) は保持し、新しい実測は [pdfcpu-0.16.2-results.json](pdfcpu-0.16.2-results.json) へ分けた。取得物・module差分・挙動の比較表・全CIコマンド・実時間は [pdfcpu-validation.md](pdfcpu-validation.md) に記録する。

### 文字・配置とToUnicode

両版とも全11,425字の欠落・GID・送り幅不一致は0、BMP全域と透かし表の照合もPASS。生成表は変更不要。`%p/%P/%t/%v`、U+005C＋n、単独%の消失と連続%の書換え、未収録字形の欠落も版の置換値以外は同じ。baselineは上端−1802/2048 s、pdfcpu座標はbaseline−ceil(246/2048 s)、送り幅はhmtx×1000/2048切捨てのまま。

| font size | 負pos.yの値 | 直接指定のbaseline（両版） | pos.y=0＋負dyのbaseline（両版） |
| --- | ---: | ---: | ---: |
| 6 | -0.279296875 | 501.00000 | 0.72070 |
| 6.1 | -0.267285156 | 501.00000 | 0.73271 |
| 12 | -0.558593750 | 502.00000 | 1.44141 |
| 72 | -0.351562500 | 509.00000 | 8.64844 |

600×1000 ptページで独立にcontent streamを検査した結果。12 ptの期待値は246/2048×12＝1.44140625 ptで、回避ありの誤差は0.00002 pt未満。既存の66 baseline・下端全3揃え・最小ページ・Poppler描画の検証もPASSし、§6.3の変更は不要。

0.16.2の回避なしだけで、338種・4ページ（結合文字、%、U+005C、①㈱￥※〒を含む）の全文字をMuPDFとPopplerの双方で正しく抽出できた。99/100/101/200字形の境界でも正しいbfchar分割と抽出。元CMapの差し替え後もType0=1、100件以下のブロック、対応表・抽出・描画の一致を確認した。旧版・新版の回避ありのCMap対応表と全ページの描画pixelもhash一致。APIの回避処理は保持し、本番コードへ実験スイッチは追加していない。

再現はCIと同じハッシュ固定venvで実行する。

```bash
python3 -m venv /tmp/amane-fill-template-validation
/tmp/amane-fill-template-validation/bin/pip install --require-hashes --only-binary=:all: -r scripts/fill-template-validation-requirements.txt
/tmp/amane-fill-template-validation/bin/python scripts/fill-template-validation.py amane-pdf-api:ci --results /tmp/fill-template-0.16.2.json
/tmp/amane-fill-template-validation/bin/python scripts/pdfcpu-update-validation.py amane-pdf-api:ci --results /tmp/pdfcpu-0.16.2.json
# 87acad7から作った比較元imageにも同じ更新検証を実行できる。
/tmp/amane-fill-template-validation/bin/python scripts/pdfcpu-update-validation.py amane-pdf-api:issue-82-baseline --expected-version 0.16.1 --results /tmp/pdfcpu-0.16.1.json
```

### 最大負荷での回避処理の時間と予約率

50 MiB入力、200項目×5行＝1,000要素、全11,425字形、全72 pt、1ページ100要素×10ページ、範囲1〜1000ページ、fields 55,555 bytes。条件とfixtureは#78と同じ。30秒・同時2件・job 124 MiB・AS 1 GiB・GOMEMLIMIT 200 MiBを維持した。

| 0.16.2 Builder | 処理全体 秒 | ToUnicode全体 秒 | 処理時間に占める割合 | job予約ピーク bytes | 124 MiBに対する予約率 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1件同時・job 1 | 1.763 | 0.158 | 8.98% | 120,578,048 | 92.74% |
| 2件同時・job 1 | 3.665 | 0.216 | 5.88% | 120,578,048 | 92.74% |
| 2件同時・job 2 | 3.640 | 0.144 | 3.95% | 120,578,048 | 92.74% |

ToUnicode時間は、既存observeの「白紙作成後〜レイヤーmetadata読取終了」から測定したdraw delegate時間を引き、CMap作成・更新JSON・qpdf更新・ファイル差し替え/削除までを加えた値。追加metadata取得を含み、既存の`to-unicode` stage単独の値より範囲が広い。計測コードはテストのみ。

HTTP最大負荷は0.16.1で2.853秒、0.16.2で2.857秒。0.16.2の出力は3,541,852 bytes・1000ページ、標本jobピーク58,957,824 bytes、HTTP cgroup memory.peak 204,812,288 bytes、Builder測定ランナー込み401,743,872 bytes。OOM/oom_kill 0、残存job・子process 0、health正常。標本値は50 ms間隔とDocker execの時間を含み、厳密な瞬間最大値ではない。

予約率は回避処理を含むjob全体の容量台帳ピーク / 124 MiB。ToUnicode専用の予約上限は追加metadata 40 MiB、更新JSON 8 MiB、修正PDF 8 MiB（外部書込みはsentinelと4 KiB切上げを含む）で、これらは順に予約・確定・削除される。予約上限の単純な合算はしない。**回避を外した場合のjobピークや時間短縮量は未測定**で、40 MiB分だけ全体ピークが減るとは判断しない。

### 判断案と、別依頼で外す場合の影響範囲

**案：本PRでは保持し、Humanの判断後に別依頼で外す。** 本家のみで両extractor・境界・複数ページの条件を満たし、APIの再作成による抽出/描画上の追加効果は確認されなかった。外すことで独自CMap生成、出力構造への依存、追加qpdf起動と容量予約を減らせる。現在の回避ありでも時間・容量の上限内なので、本PRで削除を急ぐ必要はない。

| 影響範囲 | 別依頼で必要な作業 |
| --- | --- |
| コード | PdfOverlayBuilderのToUnicode作り直し区間・専用中間物・fillTemplate引数の用途を整理し、FillTemplateUnicodeを削除。フォーム検出と入力fieldsの用途は保持 |
| 容量 | 追加metadata・更新JSON・修正PDFの予約と削除を除き、OverlayCapacityの既存上限を維持して最大負荷・同時2件を再測定。全体ピークの変化を記録 |
| テスト | FillTemplateFieldsTestsのCMap生成、FillTemplateStructureTestsの参照/Type0検査、PdfFillTemplateTestsのUnicode専用500/cancel/timeout、FillTemplateCapacityTestsの計測を整理。抽出・字形・配置・cleanupとType0/100件境界の外部regressionは維持 |
| 検証script / CI | fill-template-validation.py、pdfcpu-update-validation.pyの回避あり/なしの比較と計測条件を見直す |
| 設計 | fill-template-design.md §6.6・§8（および§3・§5.3の文字対応表修正/失敗条件）、必要なら共通設計の処理表を更新 |
| README / security | APIがCMapを作り直す説明、中間物と500の扱いを更新 |
| 検証記録 | fill-template-validation.md・pdfcpu-validation.mdへ削除後の実測を追記。今回と既存の測定JSONは当時の記録として残す |

ほかに設計から外れる変更はない。フォント、文字規則、%の書換え、負座標の回避、フォーム検出、時間・容量・同時実行の制限は維持する。回避処理の削除を本PRの完了条件には含めない。


## ToUnicode回避処理の削除後の検証（Issue #82、2026-10-19）

Human決定（2026-10-19）と[PR #83](https://github.com/kooiei-in4a/amane-pdf-api/pull/83)の実測を根拠に、APIのToUnicode再作成を削除した。過去の節と測定JSONは変更せず、[今回のraw測定JSON](fill-template-tounicode-removal-results.json)へ分けて保存する。過去節の比較用script・`--expected-version`の再現には当時のcommitを使う。

測定対象 `6e5ed11`（製品コード `f8688e4`）、比較元はPR #83の0.16.2・回避処理あり。Linux x86_64、.NET SDK 10.0.400、qpdf 12.3.2、Python 3.14.4、Poppler 26.01.0。各コマンドは同一ホストで各1回の実測。時間差にはcache・ホスト負荷・計測driverの変更が含まれ、削除だけによる速度差とは解釈しない。

`dotnet test --configuration Release` は **1355→1353件（削除5・追加3）、skip 0**。所要時間は **320.701→316.098秒（約4.6秒短縮）**。既定のテストでは最大負荷を実行せず、CI docker jobが有効化する。

### CIで守るToUnicode

- a：実HTTP APIで4ページ・338字形（320種の漢字、結合濁点、%の末尾/連続/数字前、`%a`×100、U+005C、①㈱￥※〒）を生成。MuPDF 1.26.3とPopplerでページ内の正規化や空白削除をせず全文字一致。
- b：API出力のType0はちょうど1つ。bfcharは各1〜100件、宣言数＝実件数、GID→元文字が描画入力と完全一致。99/100/101/200字形の境界も検証。
- c：`fill-template-glyphs-validation.py`がpdfcpuで全11,425字を45ページに描き、全GID→元文字を独立した文字表と照合。不一致0。100件×114＋25件、Type0=1。全字の欠落・GID・幅不一致も0。BMP 63,423字とMn 33字の既存照合もPASS。文字表の変更なし。

| fixture | ページ | 字形 | bfchar各ブロック | MuPDF / Poppler | 対応不一致 |
| --- | ---: | ---: | --- | --- | ---: |
| 99 | 1 | 99 | 99 | 両方全文字一致 | 0 |
| 100 | 1 | 100 | 100 | 両方全文字一致 | 0 |
| 101 | 1 | 101 | 100/1 | 両方全文字一致 | 0 |
| 200 | 1 | 200 | 100/100 | 両方全文字一致 | 0 |
| multiple-pages | 4 | 338 | 100/100/100/38 | 両方全文字一致 | 0 |

検証a/bは`pdfcpu-update-validation.py`に残し、「回避あり／なし」の比較を削除して実API出力の検査へ置き換えた。既存の`fill-template-validation.py`の複数ページ抽出も維持。cは全字描画scriptへ統合した。3 scriptすべてをCI docker jobのハッシュ固定venvで実行する。文字置換・未収録字形・負pos.y/負dy・baselineの直接pdfcpu検証も維持した。検査器の追加確認では、100字形打切り・宣言数不一致・101件ブロック・空ブロック・誤文字・重複GID・Type0 0/2個をすべて拒否した。

### テストの削除と追加

- `FillTemplateFieldsTests.Cmap_UsesOriginalPercentCombiningMarksAndBlocksOfAtMost100`。
- `FillTemplateStructureTests.Unicode_ResolvesFreshObjectNumber_AndRequiresExactlyOneType0Font`。
- `PdfFillTemplateTests.Http_UnicodeFailuresAre500AndRemoveEveryIntermediate`（metadata・font・updateの故障500）。
- `Http_CancellationAndTimeout_WaitForProcessAndCleanup` の `cancel/unicode` と `timeout/unicode` の2 DataRow。
- `FillTemplateCapacityTests` のToUnicode専用stopwatch・stage assertion・時間/割合。容量試験自体は単独・同時2件とも維持し、各条件を別コンテナで計測する。

`Http_ExistingOverlayEndpointsKeepFormDetectionDisabled` の3ケースを追加。空AcroFormを含む同じPDFを差し込みでは422 form-fields、透かし・ページ番号・押印では200にする。フォーム構造・入力検証・配置・入力段階のcancel/timeout・後始末の既存テストは維持した。

### 最大負荷の比較

50 MiB、200項目×5行＝1,000要素、全11,425字形、72 pt、1ページ100要素×10ページ、描画範囲1〜1000ページ、fields 55,555 bytes。CPU 1、memory 1.5 GiB、swapなし、read-only、tmpfs 256 MiB、job上限124 MiB、30秒を維持した。

| 経路 / 同時数 / job | PR #83 秒 | 削除後 秒 | PR #83 job予約ピーク bytes | 削除後 job予約ピーク bytes | PR #83 memory.peak bytes | 削除後 memory.peak bytes | OOM / 残存 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| HTTP / 1 | 2.857 | 2.471 | 未採取 | 未採取 | 204,812,288 | 193,126,400 | 全件0 |
| HTTP / 2 / 1 | — | 4.783 | — | 未採取 | — | 318,119,936（2件共通） | 全件0 |
| HTTP / 2 / 2 | — | 4.759 | — | 未採取 | — | 318,119,936（2件共通） | 全件0 |
| Builder / 1 / 1 | 1.763 | 1.637 | 120,578,048 | 120,582,144 | 401,743,872† | 219,795,456 | 全件0 |
| Builder / 2 / 1 | 3.665 | 3.221 | 120,578,048 | 120,582,144 | 401,743,872† | 375,635,968 | 全件0 |
| Builder / 2 / 2 | 3.640 | 3.242 | 120,578,048 | 120,582,144 | 401,743,872† | 375,635,968 | 全件0 |

† PR #83のBuilder memory.peakは単独＋同時2件を同じコンテナで順に測った累積値。今回は条件ごとに別コンテナで取得し、どちらもSDK/test runnerを含む。HTTPの単独・同時2件も新規コンテナで測り、同時2件の過去値はない。HTTPは予約台帳を公開しないためjob予約値は未採取で、同じBuilderの実予約ピークと区別する。HTTPの50 ms標本によるjob使用量ピークは単独 56,066,048 bytes、同時2件 59,224,064 bytes（各jobの最大）で、厳密な瞬間最大値・予約値ではない。

全HTTP/BuilderでOOM・oom_kill 0、残存job・子process 0。各HTTPのhealth正常、各出力1000ページ。Builderは最終PDF以外の中間物がなく、job dispose後も残存0を確認した。

### 段階ごとの予約と全体ピーク

全体job予約ピークは **120,578,048→120,582,144 bytes（増減 +4,096 bytes、92.74% / 124 MiB）**。削除した専用段階の予約上限は、レイヤーmetadata 40 MiB＋外部書込みsentinelの4 KiB切上げ、更新JSON 8 MiB、修正PDF 8 MiB＋sentinelの4 KiB切上げ。順番に予約・確定・削除するため合算できず、その分だけ全体ピークが下がることも意味しない。

削除によって全体ピークは減らず、4 KiB増えた。PR #83のレイヤーは2,953,277〜2,953,281 bytes（4 KiB切上げ2,957,312 bytes）で、今回のpdfcpuのCMapを保持したレイヤーは2,959,106〜2,959,169 bytes（同2,961,408 bytes）。この差が全体ピークの4,096 bytes増加に対応する。最大予約を決めるのは重ね合わせ時の入力・属性更新JSON・レイヤー保持と、overlaid.pdfの62 MiB書込み予約＋sentinel。実ファイルの4 KiB切上げとこの予約を測定テストで記録した。

| overlay書込み時の予約 | bytes |
| --- | ---: |
| `input.pdf` | 52,428,800 |
| `overlay-update.json` | 176,128 |
| `overlay-layer.pdf` | 2,961,408 |
| `overlaid.pdf write + sentinel` | 65,015,808 |
| 合計 | 120,582,144 |


### 全コマンド

| コマンド（imageは `amane-pdf-api:ci`） | PR #83 秒 | 削除後 秒 | 結果 |
| --- | ---: | ---: | --- |
| `dotnet build --configuration Release` | 8.149 | 7.970 | PASS |
| `dotnet test --configuration Release` | 320.701 | 316.098 | PASS |
| `docker build -t amane-pdf-api:ci .` | 10.592 | 1.420 | PASS |
| `python3 scripts/from-images-followup-test.py` | 0.214 | 0.214 | PASS |
| `python3 scripts/docker-smoke.py amane-pdf-api:ci` | 52.024 | 51.929 | PASS |
| `python3 scripts/renderer-validation.py amane-pdf-api:ci` | 4.122 | 4.172 | PASS |
| `python3 scripts/redact-validation.py amane-pdf-api:ci` | 2.419 | 2.419 | PASS |
| `python3 scripts/compress-validation.py --render amane-pdf-api:ci` | 29.080 | 29.277 | PASS |
| `python3 scripts/pdfcpu-validation.py amane-pdf-api:ci` | 2.469 | 2.519 | PASS |
| `python3 scripts/overlay-validation.py amane-pdf-api:ci --render` | 4.126 | 4.126 | PASS |
| `python3 scripts/page-numbers-validation.py amane-pdf-api:ci` | 10.585 | 10.736 | PASS |
| `python3 scripts/watermark-validation.py amane-pdf-api:ci` | 45.080 | 44.880 | PASS |
| `<venv>/bin/python scripts/fill-template-glyphs-validation.py amane-pdf-api:ci` | 8.780 | 8.881 | PASS |
| `<venv>/bin/python scripts/fill-template-validation.py amane-pdf-api:ci` | 20.366 | 29.594 | PASS |
| `<venv>/bin/python scripts/pdfcpu-update-validation.py amane-pdf-api:ci` | 6.179 | 4.523 | PASS |
| `python3 scripts/from-images-validation.py amane-pdf-api:ci` | 4.774 | 4.775 | PASS |
| `python3 scripts/stamp-image-validation.py amane-pdf-api:ci` | 15.996 | 15.946 | PASS |

新規の隔離venvへ`pip install --require-hashes --only-binary=:all: -r scripts/fill-template-validation-requirements.txt`で全19依存を導入し、`pip check`もPASS。CI docker jobの全検証stepを実行した。再現コマンドはREADMEの開発手順。結果JSONは`fill-template-validation.py --results`、`pdfcpu-update-validation.py --results`、`fill-template-glyphs-validation.py --results`で保存できる。
