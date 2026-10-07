# PDF機能拡張の方針（2026-10）

amane-pdf-api と amane-tools-site のPDF機能を拡張するための、戦略、採用した判断、判断の根拠となった検証結果をまとめます。

個別の仕様と実装設計は各Issueに記載しています。Issueの一覧と進み具合は [#18](https://github.com/kooiei-in4a/amane-pdf-api/issues/18) で管理します。この文書は、判断の理由と根拠を残すためのものです。

## 前提（2026-10-07時点の事実）

- API main `93807e3`: qpdfだけで、暗号化、lossless最適化、結合、回転、ページ抽出・削除・並べ替えを提供しています。同期処理で、PDFを永続保存しません。1 CPU / 512 MiB / tmpfs 256 MiB / qpdf 30秒 / 同時2処理で動かしています。
- amane-tools-site main `2a0ebb8`: 暗号化、最適化、結合、回転を公開しています。日次の利用回数はPDF全体で1つの枠（未ログイン3回・無料プラン10回）です。抽出・削除・並べ替えはAPIにありますが、サイトでは未公開です。
- コンテナのqpdfは11.9.0（Ubuntu 24.04）、開発環境は12.3.2です。

## 戦略

1. **提出・アップロード上限の需要を取る**: 本格的な圧縮、画像からのPDF作成、分割。行政手続き、就職活動、本人確認、経費精算では、PDF形式とサイズ上限の指定が多くあります。
2. **パスワード付きPDFの入口と出口をそろえる**: 既存の暗号化に加えて解除を用意します。今は全APIが暗号化PDFを拒否するため、解除がないとパスワード付きPDFは他のツールで扱えません。
3. **「保存しない・処理内容を公開する」方針を機能でも示す**: 作成者情報の除去、画像からPDFを作るときの位置情報の除去、安全な黒塗り。
4. **日本の商習慣に合わせる**: 電子印鑑、ページ番号、透かし（社外秘・見本など）。海外の汎用ツールが弱い領域です。
5. **有料プランの目玉候補を用意する**: 目標サイズを指定する圧縮、差分比較。課金の設計は別に行います。
6. **需要を計測して次の投資を決める**: 日次枠を共有にしたため、どのツールが使われているかが今は分かりません。操作別の件数を、個人を特定しない集計値として最初に記録します。

## 技術方針

- PDFの検証と書き出しは、引き続きqpdfが担います。新しいツールは外部コマンドとして呼び、既存のtimeout、process tree kill、一時ディレクトリの削除、同時実行枠を共通で使います。
- 追加するOSSは、permissiveまたは弱いcopyleftのライセンスに限ります。

  | OSS | 用途 | ライセンス |
  | --- | --- | --- |
  | libjpeg-turbo（djpeg / cjpeg / jpegtran） | JPEG画像の縮小・再圧縮・向き補正 | IJG / BSD-3-Clause / zlib |
  | pdfcpu | 文字・画像のスタンプ、画像からのページ作成 | Apache-2.0 |
  | BIZ UDPゴシック | pdfcpuで描く日本語の文字 | OFL-1.1 |
  | PdfPig | 差分比較のための文字抽出 | Apache-2.0 |
  | PDFium（PDFtoImage経由） | ページを画像として描く | BSD-3-Clause / Apache-2.0（PDFtoImageはMIT） |
  | Tesseract | OCR | Apache-2.0 |

  PDFium（PDFtoImage経由）は推奨案で、[#33](https://github.com/kooiei-in4a/amane-pdf-api/issues/33) の着手時の確認（version、更新の追い方、性能）で確定します。

- AGPL / GPLのPDFエンジン（Ghostscript、MuPDF / PyMuPDF、iText、Poppler）は採用しません。このAPIはApache-2.0で公開し、コンテナイメージを配布するためです。
- ベースイメージをUbuntu 26.04に更新し、qpdfを開発環境と同じ12.3.2にそろえます。

## 採用した判断

| 論点 | 採用 | 理由 | 決定 |
| --- | --- | --- | --- |
| 圧縮の方式 | qpdf JSONでJPEG画像だけを取り出し、djpeg / cjpegで縮小・再圧縮して書き戻す | GhostscriptはAGPL。文字・ベクター・構造に触れない。libvipsは依存が大きい（Ubuntu 26.04で129パッケージ） | Claude推奨（2026-10-07、異議なし） |
| パスワード解除の範囲 | 開くパスワードを知っているPDFだけ | 権限パスワードだけのPDFの制限解除は、権利者の保護を外す行為になる | Claude推奨（2026-10-07、異議なし） |
| pdfcpuの使い方 | APIが作った白紙PDFにだけ描き、qpdf `--overlay` で元のPDFに重ねる | 利用者のPDFの解析と合成をqpdfに限る。しおり・リンク・フォームを保ち、回転したページにも対応できる | Claude推奨（2026-10-07、異議なし） |
| 日本語フォント | BIZ UDPゴシック | pdfcpuはTrueTypeアウトラインのフォントだけに対応する | Claude推奨（2026-10-07、異議なし） |
| 新しい応答形式 | ZIP（分割、PDF→画像）とJSON（差分比較）を認める | 複数のファイルや比較結果を返すため | Claude推奨（2026-10-07、異議なし） |
| 日次回数 | 新しいツールもPDF共通枠で1回 | 2026-10-05のHuman決定を維持する。重い処理の重み付けは課金設計で検討する | 既存のHuman決定 |
| 最適化ツール | 残し、圧縮ツールと相互にリンクする | 2026-10-05のHuman決定（最適化ツールを公開する）を維持する | 既存のHuman決定 |
| pdf.jsの導入（サイト） | 電子印鑑と黒塗りのプレビューに限って導入する。version固定で自サイトから配信する | 位置を見ながら決めるにはプレビューが必要。PDFはブラウザ内で描画し、追加でサーバーへ送らない | 2026-10-07 Human承認 |
| 黒塗り | 画像化方式で提供する（P2） | 黒塗りしたページは元の情報がすべて消え、「見た目だけ黒い」事故が仕組み上起きない。対象の書類の多くはもともとスキャン画像 | 2026-10-07 Human承認 |
| OCR | 非同期処理は持たず、同期処理・上限10ページ・OCR専用の同時実行枠1つで提供する（P3） | 実測で1ページ約1〜1.6秒だったため、少数ページなら非同期処理なしで成立する。非同期処理は結果の一時保存が必要になり、方針の変更が大きい | 2026-10-07 Human承認 |
| HEIC | サーバーでは対応しない。拒否した回数を計測し、多ければSafariでだけブラウザ内で変換する | HEVCは特許の対象。iPhoneのSafariは `accept` の指定でJPEGへ自動変換して送る | 2026-10-07 Human承認 |

## 検証記録（2026-10-07）

判断の根拠として、次を実機で確認しました。検証用のファイルはrepositoryに含めていません。

### ベースイメージとqpdf

- `mcr.microsoft.com/dotnet/aspnet:10.0` はUbuntu 24.04.5で、aptのqpdfは11.9.0です。11.9.0の `qpdf --help=all` には `--remove-info` / `--remove-metadata` / `--remove-structure` / `--remove-acroform` / `--jpeg-quality` がありません。
- `mcr.microsoft.com/dotnet/aspnet:10.0-resolute` はUbuntu 26.04.1 LTSで、aptの候補はqpdf `12.3.2-1`、libjpeg-turbo-progs `2.1.5-4ubuntu4`、libvips-tools `8.18.0-1build1`、tesseract-ocr `5.5.0-1build1` です。
- GitHub Actionsの `ubuntu-26.04` runnerは公開プレビューで利用できます。

### パスワード解除（qpdfの終了コード）

qpdf 11.9.0と12.3.2で同じ結果でした。パスワードはQPDFJob JSONで渡しています。

| 入力 | 実行 | exit |
| --- | --- | --- |
| 暗号化なし | `--requires-password`（passwordなし） | 2 |
| 開くパスワードあり | 同上 | 0 |
| 権限パスワードだけ | 同上 | 3 |
| 非PDF・途中で切れた暗号化PDF | 同上 | 2（暗号化なしと区別できないため `--check` で区別する） |
| 開くパスワードあり | `requiresPassword` + 正しいuser / owner password | 3 |
| 開くパスワードあり・権限パスワードだけ | `requiresPassword` + 誤ったpassword | 0 |
| 開くパスワードあり | `decrypt` + 正しいpassword | 0（出力は暗号化なし） |

### 圧縮

Ubuntu 26.04のコンテナ（qpdf 12.3.2、libjpeg-turbo 2.1.5）で、A4・300dpi相当のカラーとグレーのJPEGを1枚ずつ含むPDFに対して確認しました。

1. `qpdf --json=2 --json-key=pages` で、各ページの画像のobject、幅、高さ、filter、色空間を取得できる。
2. `qpdf --json=2 --json-key=qpdf --json-object=N,0 --json-stream-data=file` で、DCT画像の生データ（JPEGそのもの）を個別のファイルへ書き出せる。
3. `djpeg -scale 4/8` → `cjpeg -quality 75 -optimize` で縮小・再圧縮できる。
4. 辞書の `/Width` / `/Height` を書き換えたupdate JSONを `qpdf --update-from-json` で適用すると、`qpdf --check` を通るPDFになる。

結果は 849,774 bytes → 192,467 bytes で、描画して表示が崩れないことを確認しました。

### pdfcpuと日本語フォント

- pdfcpu `v0.16.1`（2026-10-04）はRelease assetの `checksums.txt` でsha256を検証できます。Ubuntu 26.04のaptにはありません。
- Noto Sans CJK（OpenType CFF）は `unsupported font format: OpenType CFF` で使えません。BIZ UDPゴシック（TrueType）は使え、日本語のスタンプ、ページ番号、斜めの透かしを描けました。文字は抽出でき、subset埋め込みで出力は約190 KB増えました。
- 設定ディレクトリを `-c <dir>`（`pdfcpu` の親ディレクトリ）で指定すれば、読み取り専用でも処理できます。
- `--` の後の文字列は、`-` で始まっても `,` を含んでもそのまま描画されます。出力先が既に存在すると失敗します。
- `%p` は物理ページ番号、`%p3` は物理ページ番号+3です。負のoffsetは使えません（`%p-1` は「2-1」と文字のまま出ます）。
- 画像スタンプは `pos:bl, off:100 50, scale:0.1 abs` で、300×300 pxの画像が (100, 50) から 30×30 ptに配置されました。
- 対象ページと同じ大きさ・向きの白紙PDFを作ってpdfcpuで番号を描き、`qpdf --overlay` で重ねる方式で、表紙を除いて2ページ目を「- 1 -」から始める番号付けができました。`/Rotate` 90 / 180 / 270 のページとCropBox付きのページでも、表示上の下中央に正しい向きで入りました。

### 文書情報の除去（qpdf 12.3.2）

`--remove-info --remove-metadata --remove-attachment=<key>` で、文書情報は `/ModDate` だけが残り、添付ファイルは中身も含めて消えました。qpdfはProducerを追加しません。trailerの `/ID` は残ります。QPDFJob JSONのkeyは `removeInfo` / `removeMetadata` / `removeAttachment`（配列）です。

### OCR（Tesseract 5.5.0、1 CPU / 512 MiB）

契約書風の日本語文（338文字）を描いた合成画像での結果です。実際のスキャンより条件が良いことに注意してください。

| 画像 | 文字の一致率（再現率 / 適合率） | 時間 |
| --- | --- | --- |
| 横書き 300dpi | 96.7% / 99.1% | 1.08秒 |
| 横書き 200dpi・傾き・ぼかし・JPEG品質40 | 96.2% / 99.1% | 0.97秒 |
| 縦書き 300dpi（`jpn_vert`） | 97.3% / 97.1% | 1.60秒 |

「甲」を「皿」と読むなど、重要な1文字の誤りがありました。検索用には実用的ですが、そのままコピーして使うには校正が必要です。OCRmyPDFはv17からGhostscriptなしで動きますが、Pythonが必要になるため使いません。

### HEIC

- iPhoneのSafariは、`<input type="file" accept="image/jpeg,image/png">` のように形式を指定すると、HEICを最初に書いた形式へ自動変換して送ります。Safari 17以降、`image/heic` を書くと逆にJPEG / PNGがHEICへ変換されることがあります。
- HEVCは特許の対象で、FedoraはHEVCのデコーダーを同梱していません（RPM Fusionで別配布）。

## 優先度

| 優先度 | 意味 | 主な内容 |
| --- | --- | --- |
| P0 | 第1弾 | 操作別の計測、Ubuntu 26.04 / qpdf 12、外部コマンド実行の共通化、パスワード解除、圧縮 |
| P1 | 第2弾 | ページ抽出・削除（サイト）、分割、作成者情報の除去、pdfcpuの導入、画像からのPDF作成 |
| P2 | 第3弾 | ページ番号、透かし、電子印鑑、目標サイズの圧縮、差分比較、ページの描画、黒塗り |
| P3 | 条件付き | フォームと注釈の焼き込み、割付・A4統一、OCR、HEICの計測と見直し |

各Issueの番号、依存関係、推奨する着手順は [#18](https://github.com/kooiei-in4a/amane-pdf-api/issues/18) を参照してください。

## 見送るもの

| 機能 | 理由 |
| --- | --- |
| Office（Word / Excel / PowerPoint）→PDF | LibreOfficeでは日本語の文書（Excel方眼紙、MSフォントの置き換え）でレイアウトが崩れやすく、品質への苦情につながりやすい。信用できないOffice文書を解析する攻撃面とメモリ使用量も大きい。利用者の多くは手元のOfficeで変換できる |
| PDF→Word / Excel | OSSの変換品質が商用製品と大きく違う。代表的なpdf2docxはPyMuPDF（AGPL）に依存する |
| 電子署名・タイムスタンプ | APIで秘密鍵を扱わない方針。認定タイムスタンプは有償サービスが必要 |
| PDF/A変換 | 一般利用者の需要が小さく、検証にveraPDFなどが必要 |
| 外部開発者向けの汎用API販売 | 海外の既存サービスが安くて強い。README の方針どおり、当面はサイトでの提供を優先する |
| ページ並べ替えのUI | 2026-10-05のHuman決定でNOT_NOW |
| サーバーでのHEICの変換 | 上記の判断のとおり |

## 運用

- 各機能の本番反映（amane-infraでのAPIイメージ更新、サイトのversion更新）は、それぞれ別の作業として行います。
- merge、tag、Release、deployはHumanの承認後に行います。
- 判断を変える場合は、該当するIssueで議論し、この文書を更新します。
