# amane-pdf-api

PDFを安全に処理するための小さなWeb APIです。

`amane-tools-site` から受け取ったPDFの暗号化・パスワード解除、lossless構造最適化、結合、ページ回転、ページ抽出・削除・並べ替えを、安全なqpdf処理として提供します。

## 現在の状態

実装済み:

- .NET 10 / ASP.NET Core の最小API、`GET /healthz`
- `POST /api/pdf/protect` によるPDFのAES-256暗号化
- `POST /api/pdf/unlock` による、正しいuser / owner passwordでの暗号化解除
- `POST /api/pdf/optimize` によるPDFのlossless構造最適化
- Linuxの `POST /api/pdf/compress` による対象JPEG画像の縮小・再圧縮
- `POST /api/pdf/merge` による複数PDFのアップロード順での結合
- `POST /api/pdf/split` によるPDF分割とZIP返却
- `POST /api/pdf/rotate` による全ページまたは指定ページの相対回転
- `POST /api/pdf/extract` による指定ページの抽出
- `POST /api/pdf/delete-pages` による指定ページの削除
- `POST /api/pdf/reorder` による全ページの並べ替え
- 独立したランダムowner password、QPDFJob JSON経由のパスワード入力
- 成功・失敗・キャンセル時の一時ファイル削除
- 正常PDFの検証、破損/警告の拒否、unlock以外での既暗号化PDFの拒否、統一Problem Details
- Unicodeパスワード対応
- PDF 50 MiB / qpdf処理30秒 / 同時2処理 / 待ち行列0の制限
- 実qpdfを使った自動テスト、Docker build、実コンテナE2Eを含むGitHub Actions

API単体の実装とDocker / CIの実処理検証が完了しています。

`amane-tools-site` との接続は別Repositoryの責務です。

## 方針

このAPIはPDF処理だけを担当します。

- データベースを持たない
- 利用者アカウントを持たない
- PDFやパスワードを永続保存しない
- PDF本文、ファイル名、パスワードをログへ記録しない
- PDF処理にはqpdfを使用する
- `amane-tools-site` とは別コンテナで動かす
- 初期段階では、不特定多数向けの汎用APIとして公開しない

ソースコードは公開し、どのようにPDFを処理しているか確認できる状態にします。

## API

### PDF暗号化

```text
POST /api/pdf/protect
Content-Type: multipart/form-data

file      PDFファイル
password  PDFを開くためのパスワード
```

成功時は `200 OK`、`Content-Type: application/pdf` で暗号化済みPDFを返します。
qpdfがexit code 0で終了するまでレスポンスを開始しません。利用者ファイル名は保存先にも返却名にも使いません。

```bash
read -r -s -p 'PDF password: ' pdf_password
printf '\n'
printf '%s' "$pdf_password" | curl --fail-with-body \
  -F file=@tests/Amane.Pdf.Api.Tests/Fixtures/sample.pdf \
  -F 'password=<-' http://127.0.0.1:8080/api/pdf/protect -o protected.pdf
unset pdf_password
```

このBashの例ではパスワードを対話入力し、stdinからcurlへ渡します。パスワード値をcommand line argvやshell履歴に載せません。

### PDFパスワード解除

```text
POST /api/pdf/unlock
Content-Type: multipart/form-data

file      開くパスワードが必要なPDF
password  正しいuser passwordまたはowner password
```

開くパスワードが設定されたPDFだけを対象にし、開くパスワードがなく印刷禁止などの権限制限だけが付いたPDFは、正しいowner passwordを渡しても拒否します。AES-256、AES-128、RC4で暗号化されたPDFを受け付けます。passwordの形式はprotectと共通で、UTF-8で127 bytes以下、空・制御文字不可、trim・Unicode正規化なしです。

パスワードなしの判定、送信passwordでの認証、認証後の構造検査、解除、出力検証を順に実行します。passwordはprivateなQPDFJob JSONで渡し、qpdfのargvには載せません。入力のerror / warningを拒否し、出力が非空・未暗号化で、passwordなしの構造検査を通ることを確認してから返します。

成功時は `200 OK`、`Content-Type: application/pdf`、`Content-Disposition: attachment; filename=unlocked.pdf` です。出力には暗号化に伴うowner password・権限制限・暗号化を残しません。電子署名付きPDFは拒否しませんが、解除によるPDFの書き換えで署名は無効になります。

```bash
read -r -s -p 'PDF password: ' pdf_password
printf '\n'
printf '%s' "$pdf_password" | curl --fail-with-body \
  -F file=@protected.pdf \
  -F 'password=<-' http://127.0.0.1:8080/api/pdf/unlock -o unlocked.pdf
unset pdf_password
```

unlockの422だけに、固定の `reason` extensionを付けます。

| reason | 条件 | 固定title |
| --- | --- | --- |
| `not-encrypted` | 構造検査を通る未暗号化PDF | パスワードが設定されたPDFが必要です。 |
| `no-open-password` | passwordなしで開ける暗号化PDF | 開くためのパスワードが設定されていないPDFは解除できません。 |
| `wrong-password` | qpdfが送信passwordで開けないと判定 | パスワードが正しくありません。 |
| `invalid-pdf` | 空、非PDF、破損、入力検査のerror / warning、解除のwarning | 正常なPDFが必要です。 |

reasonはqpdfの判定に基づく分類であり、破損の種類によっては `wrong-password` になり得ます。開くパスワードの有無と認証可否を構造検査より先に判定します。入力検査後の解除errorや、出力検証の失敗は内部障害として500を返します。既存APIの422のtitle・bodyは変更せず、reasonも付けません。

### PDF lossless最適化

```text
POST /api/pdf/optimize
Content-Type: multipart/form-data

file  PDFファイル
```

qpdfのobject stream生成とFlate再圧縮を使用し、画像の解像度やJPEG品質を意図的に変更せずPDF構造を最適化します。画像downsamplingを行う高圧縮機能ではなく、入力よりファイルサイズが小さくなることは保証しません。出力が入力より大きい場合も正常な結果として返します。
使用するoptionは `--object-streams=generate`、`--recompress-flate`、`--compression-level=9` です。

成功時は `200 OK`、`Content-Type: application/pdf`、固定ファイル名 `optimized.pdf` で返します。既存APIと同じPDFサイズ、qpdf処理時間、同時実行数、入力検証の制限が適用されます。

```bash
curl --fail-with-body \
  -F file=@tests/Amane.Pdf.Api.Tests/Fixtures/sample.pdf \
  http://127.0.0.1:8080/api/pdf/optimize \
  -o optimized.pdf
```

### PDF結合

```text
POST /api/pdf/merge
Content-Type: multipart/form-data

file  1つ目のPDF
file  2つ目のPDF
file  3つ目のPDF（任意）
...
```

同名の `file` fieldを複数指定し、multipart内の出現順で各PDFの全ページを結合します。2〜10ファイルを受け付け、1ファイル最大50 MiB、入力PDF合計最大50 MiBが既定値です。ページ指定やpassword fieldは受け付けず、既暗号化PDFは拒否します。各入力を実qpdfで逐次検証し、空、非PDF、破損、warningのある入力が1件でもあれば全体を拒否します。

成功時は `200 OK`、`Content-Type: application/pdf`、`Content-Disposition: attachment; filename=merged.pdf` で返します。利用者ファイル名は内部path、qpdf argv、返却名、ログへ使用しません。

```bash
curl --fail-with-body \
  -F file=@first.pdf \
  -F file=@second.pdf \
  http://127.0.0.1:8080/api/pdf/merge \
  -o merged.pdf
```

qpdfの `--empty --pages` を使用するため、入力PDFの文書レベルmetadataやoutline（しおり）の保持・統合は保証しません。v1の目的はページ内容の結合です。出力PDFは入力合計より大きくなる可能性があります。

アップロード完了後、全入力の検証と最終結合を合わせて1つの30秒のtimeout予算を使用します。入力ごとにtimeoutをリセットせず、request内でqpdfを並列実行しません。1 merge requestは既存APIと共有する同時実行枠の1枠を、アップロードから返却・削除まで保持します。

### PDFページ回転

```text
POST /api/pdf/rotate?angle=90
POST /api/pdf/rotate?angle=90&pages=1,3-5
Content-Type: multipart/form-data

file  PDFファイル
```

`angle` は `90`、`180`、`270` のいずれかを文字列として指定します。入力PDFが現在持つ回転状態に対する、時計回りの相対回転です。例えば90度回転済みのPDFへ `angle=90` を適用すると180度になります。

`pages` を省略すると全ページを回転します。指定する場合は、ASCII数字による1始まりのページ番号と、カンマ、ハイフンだけを使用できます。`1`、`1,3,5`、`1-3`、`5,1,3-4` のように指定でき、順序は自由です。先頭0、空要素、空白、逆range、重複するページ、実ページ数を超える指定は拒否します。文字列長の上限は4096文字です。

成功時は `200 OK`、`Content-Type: application/pdf`、固定ファイル名 `rotated.pdf` で返します。

```bash
curl --fail-with-body \
  -F file=@tests/Amane.Pdf.Api.Tests/Fixtures/sample.pdf \
  'http://127.0.0.1:8080/api/pdf/rotate?angle=90&pages=1' \
  -o rotated.pdf
```

### PDF画像圧縮（Linux）

```text
POST /api/pdf/compress?level=standard
POST /api/pdf/compress?level=strong
Content-Type: multipart/form-data

file  PDFファイル一つ
```

| level | 長辺の目標（下限の目安） | JPEG品質 |
| --- | ---: | ---: |
| standard | 1754 px | 75 |
| strong | 1169 px | 60 |

`level`は一つだけ必須です。未知の値・query key・重複は400です。multipartも`file`一つだけとし、passwordなどの追加fieldは400です。成功は200、`application/pdf`、固定名`compressed.pdf`です。非Linuxにはこのendpointを登録しません。lossless構造最適化の`optimize`は従来どおりです。

目標は上限ではなく、原画像が十分大きい場合の下限の目安です。長辺が目標以下なら縮小せず、拡大もしません。それ以外は整数の`M = min(8, max(1, ceil(目標×8÷長辺)))`を選び、djpegの`-scale M/8`で縮小します。長辺は目標以上、通常は目標の約2倍までになります。極端な縦横比では1/8でも2倍を超えます。8064×6048は両levelとも長辺2016 pxになりますが、品質75／60の違いは残ります。

対象はページResourcesから直接参照される固有の画像XObjectで、DCTDecodeだけ、DecodeParmsなし、BitsPerComponent 8、生JPEG 32 KiB以上、初期値1億画素以下です。JPEGのprecision 8・成分数1／3・実寸とPDF辞書が一致する必要があります。DeviceGray／DeviceRGB、Nが一致するICCBased、辞書を持つCalGray／CalRGBを扱います。ImageMask=true、Decode、color key Mask配列、Matte付きSMask、未解決・循環参照は除外します。Form内、inline image、CMYK、Indexed／Lab／Separation／DeviceN、Flate／JPEG 2000／JBIG2／CCITTなどの再圧縮は対象外です。ColorSpaceとprofile、保持対象のMask／SMask、Interpolateは維持します。ICC header・acsp、Range／Alternate、WhitePointの値域は検査しません。

2026-10-08のHuman承認により、JPEGは最初のEOIまでを本体として解析し、それ以降のデータを無視します。MPFのHDR gain mapなどの後続データも対象判定を妨げません。置換した場合、その後続データは新JPEGへ引き継がれません。EOIより前の不正なmarker・segment、height 0／DNL、複数・対応外SOF、SOSなしは引き続き拒否します。

新JPEGが元JPEGより10%以上小さい場合だけ置き換えます。文字・ベクター・フォント・ページの表示上の意味を保ち、共有画像objectを一度だけ処理します。画像単位の変換失敗・時間／容量制限ではその画像を保持し、採用済みの途中結果で最終処理へ進みます。progressiveは固定の`-maxmemory 64M`・AS 64 MiBでdjpegが`Backing store not supported`となる場合があります。12 MP（4000×3000）の4:4:4でも失敗を確認しました。失敗する目安は4:2:0で約21 MP以上、4:4:4で約11 MP以上ですが、画像により異なり、受付境界を保証する値ではありません。失敗した画像は元のまま保持されます。任意のJPEGやPDFの圧縮成功・サイズ削減は保証しません。

`X-Pdf-Images-Recompressed`は置き換えた固有object数です。共有画像が何ページに出ても1個と数えます。0は「画像を置き換えていない」ことだけを意味し、対象なし、削減不足、変換失敗、時間／容量制限を区別しません。置換0個や出力の増大も正常PDFなら200です。出力検証と送信ファイルのopen後に付け、エラーには付けません。

アップロード完了後・入力検証前から30秒を共有し、同じ開始時刻から21秒で画像処理を打ち切ります。一枚のdjpeg／cjpegは合計2秒、バッチ抽出も2秒です。残り9秒で書き出し・出力検証を行いますが、成功を保証する予算ではなく、hard timeoutは504です。request中断・アプリ停止は画像単位の失敗として扱いません。

最終書き出しは`--object-streams=generate --compression-level=9`だけを指定し、stream-data／recompress-flate／decode-levelを指定しません。単独DCT／Flate／RunLength／JPX／JBIG2／CCITTは生データを保持します。LZW／ASCIIHex／ASCII85や複合filterはqpdfの一般化された可逆decodeによりfilterが変わり得ます。デコード後データ・表示上の意味は保持します。qpdfによるobject番号の変更は許容します。

```bash
curl --fail-with-body -F file=@input.pdf \
  'http://127.0.0.1:8080/api/pdf/compress?level=standard' -o compressed.pdf
```

サイト側のUI・利用者向け制限・ヘッダーの伝達は[amane-tools-site #38](https://github.com/kooiei-in4a/amane-tools-site/issues/38)で扱います。

### PDFページ抽出・削除・並べ替え

```text
POST /api/pdf/extract?pages=1-3,7
POST /api/pdf/delete-pages?pages=2,5
POST /api/pdf/reorder?pages=3,1,2,4-10
Content-Type: multipart/form-data

file  PDFファイル
```

抽出は指定ページだけを指定順で返し、削除は指定ページ以外を元の順序で返します。並べ替えでは入力PDFの全ページをちょうど1回ずつ指定する必要があります。全ページ削除、並べ替えでのページ欠落、範囲外、重複は400です。

ページ操作では元PDFのしおりなどの文書レベル情報を引き継ぎます。このため、削除または未抽出のページを指していたしおりや名前付きDestinationは、参照先のない項目として残る場合があります。選択対象外ページのページ本文は出力に含まれません。

`pages` は3 APIとも必須です。回転APIと同じparserを使用し、ASCII数字、`,`、`-` だけを受け付けます。例えば `1`、`1,3,5`、`1-3`、`5,1,3-4`、`1-3,7,10-12` を指定できます。空白、先頭0、0、負数、空要素、逆range、重複は使用できず、文字列長は最大4096文字です。qpdf固有のpage range構文は公開しません。

成功時は `200 OK`、`Content-Type: application/pdf` で、それぞれ固定ファイル名 `extracted.pdf`、`pages-deleted.pdf`、`reordered.pdf` を返します。

```bash
curl --fail-with-body \
  -F file=@tests/Amane.Pdf.Api.Tests/Fixtures/sample.pdf \
  'http://127.0.0.1:8080/api/pdf/extract?pages=1' \
  -o extracted.pdf
```

4096文字の上限は並べ替えにも適用されます。大量ページを1ページずつ逆順指定するなど、完全なページ列が4096文字を超える並べ替えは初版では対象外です。別のJSON body APIはありません。

### PDF分割（ZIP）

```text
POST /api/pdf/split?every=2
POST /api/pdf/split?ranges=1-3,4,5-10
Content-Type: multipart/form-data

file  PDFファイル
```

`every`か`ranges`の一方だけ、値も一つだけを指定します。未知のquery key、重複は400です。keyは大文字小文字を区別せず、`EVERY=2`も受け付けます。`every=2&Every=3`は重複として拒否します。`every`は先頭0のないASCII数字、1〜99999です。先頭からNページずつ分け、最後が短くても構いません。`ranges`は既存ページ指定と同じ4096文字以内の規則で、カンマ区切りの各範囲を1ファイルにします。指定順を維持し、指定しないページはページ一覧に含めません。

結果は2〜100ファイル（初期値）で、範囲外、1ファイルになる指定、分割数超過は400です。成功は完成したZIPの200、`application/zip`、`Content-Length`、固定名`split.zip`です。ZIP内の名前は`part-001_p1-3.pdf`、単一ページなら`part-002_p4.pdf`です。Stored（method 0）、各entryの時刻は1980-01-01 00:00:00です。文書のID等を固定する機能ではなく、ZIP全体のbytes一致は保証しません。

元PDFを主入力にするため、文書情報・添付・しおり等が残ることがあります。選択しないページへのしおりは参照先がなくなる場合があります。情報の消去・無害化は保証しません。

ZIP内のPDF合計は初期値50 MiB、入力・作成中ZIP・現在のパートの一時容量予算は124 MiBです。PDF合計・最大パート・指定順によって一時容量上限に達することがあり、合計50 MiB以内でも拒否します。容量上限は422 `reason: "output-too-large"`、固定title「分割処理の容量上限に達したため、処理できませんでした。含めるページを減らすか、分け方を変更してお試しください。」です。途中のZIPは返しません。破損・暗号化等の共通422にはreasonを付けません。

```bash
curl --fail-with-body -F file=@input.pdf \
  'http://127.0.0.1:8080/api/pdf/split?every=2' -o split.zip
```

サイトのUI・日次回数の返却は[amane-tools-site #40](https://github.com/kooiei-in4a/amane-tools-site/issues/40)の責務です。splitの上流400と検証済み422 `output-too-large`だけを返却し、その他の422は返却しません。サイトの公開はAPI splitのdeploy後に行います。core dumpに関する公開条件は[FSIZE core対策の検証記録](docs/fsize-core-validation.md)、分割の容量測定は[分割の検証記録](docs/split-validation.md)を参照してください。

### 入力とエラー

暗号化・解除APIでは、`file` は1ファイル、`password` は1項目で必須です。空のpassword、制御文字、UTF-8で127 bytesを超えるpasswordは拒否します。空白はtrimしません。最適化、圧縮、分割、回転、抽出、削除、並べ替えAPIでは`file`だけを受け付け、`password`を含む予期しないfieldは400で拒否します。
Unicodeはqpdfの`passwordMode=unicode`でUTF-8として渡します。API側ではUnicode正規化やtrimを行いません。

Content-Typeや拡張子だけでPDFを判定せず、実qpdfの`--check`を使います。
入力構造検査のexit code 2（error）と3（warning）は422として拒否し、自動修復したPDFを成功扱いにしません。
既暗号化PDFは、password不要で開けるものや送信したpasswordが一致するものも含め、unlock以外の全操作で拒否します。

| HTTP status | 条件 |
| --- | --- |
| 200 | 処理済みPDF、splitは完成ZIPを返却 |
| 400 | multipart/query形式不正、必須項目不足/重複、予期しないfield、mergeのファイル数不足/超過、passwordまたはページ指定等の仕様違反、splitの分割数不足/超過 |
| 413 | 単一PDF、merge入力合計、またはmultipartリクエスト総量のサイズ超過 |
| 422 | 空ファイル、非PDF、破損/警告のあるPDF、処理上限超過、unlock以外の既暗号化PDF。unlockは上記reason表を参照 |
| 500 | qpdf実行環境や処理中の想定外の内部障害 |
| 503 | 同時PDF処理数の上限超過（待ち行列なし） |
| 504 | qpdfの検査と各PDF処理全体の制限時間超過 |

共通422のtitleは「このPDFは処理できません。PDFの破損・パスワード設定や、画像が大きすぎないか確認してください。」です。reasonは追加しません。unlock専用の4つのreasonとtitleは既存のままです。

エラーはASP.NET Core標準の`application/problem+json`です。固定文言のみを返し、内部path、password、PDF本文、stack trace、qpdf出力は返しません。

## リソース制限と設定

| 環境変数 | 初期値 | 意味 |
| --- | --- | --- |
| `Pdf__MaxFileBytes` | `52428800`（50 MiB） | 実際に読み込むPDFの最大bytes |
| `Pdf__MaxMergeFiles` | `10` | mergeの最大ファイル数。設定可能な範囲は2〜10 |
| `Pdf__MaxMergeInputBytes` | `52428800`（50 MiB） | 実際に読み込むmerge入力PDF合計の最大bytes |
| `Pdf__MaxSplitParts` | `100` | 分割数上限。設定範囲2〜500 |
| `Pdf__MaxSplitOutputBytes` | `52428800`（50 MiB） | ZIP内PDFの実サイズ合計。MaxFileBytesから独立 |
| `Pdf__MaxSplitJobBytes` | `130023424`（124 MiB） | splitの入力・ZIP・現在のパートの容量予算 |
| `Pdf__QpdfTimeoutSeconds` | `30` | qpdfの検査と各PDF処理全体の最大秒数 |
| `Pdf__MaxConcurrentProcesses` | `2` | 同時PDF処理枠。アップロードから送信/削除まで保持 |
| `Pdf__QpdfPath` | `qpdf` | qpdf実行ファイル |
| `Pdf__PrlimitPath` | `/usr/bin/prlimit` | Linuxの制限付き実行に使うprlimit |
| `Pdf__QpdfAddressSpaceLimitBytes` | `570425344`（544 MiB） | Linuxでqpdfに適用するRLIMIT_AS。正のbytes値 |
| `Pdf__QpdfJpegMemory` | `600M` | 全OSのqpdfに渡すJPEGMEM。正のASCII数字＋任意の`M`/`m`、30文字未満、単位換算後のsigned long overflowを拒否 |
| `Pdf__DjpegPath` / `Pdf__CjpegPath` | `djpeg` / `cjpeg` | LinuxのJPEG変換実行ファイル |
| `Pdf__JpegAddressSpaceLimitBytes` | `67108864`（64 MiB） | djpeg／cjpegのRLIMIT_AS |
| `Pdf__CompressJobLimitBytes` | `130023424`（124 MiB） | job全体のファイル割当上限 |
| `Pdf__CompressPnmLimitBytes` | `25165824`（24 MiB） | 縮小後PNMの安全上限 |
| `Pdf__CompressMaxPixels` | `100000000` | 画像一枚の画素数上限 |
| `Pdf__CompressMaxImages` | `500` | Length降順で処理する固有画像数 |
| `Pdf__CompressJsonLimitBytes` | `16777216`（16 MiB） | pages／metadata／update JSONの上限 |
| `Pdf__CompressJsonDepth` | `64` | JSONと参照解決の深さ上限 |
| `Pdf__CompressSpoolLimitBytes` | `16777216`（16 MiB） | 辞書spoolの合計割当上限 |
| `Pdf__CompressStdoutLimitBytes` | `1048576`（1 MiB） | バッチ抽出JSONの保持上限 |
| `Pdf__CompressSoftTimeoutSeconds` | `21` | 共通開始時刻から画像処理を打ち切る秒数。`QpdfTimeoutSeconds`未満が必須 |
| `Pdf__CompressImageTimeoutSeconds` | `2` | 一枚のdjpeg＋cjpegの共有秒数 |
| `Pdf__CompressBatchTimeoutSeconds` | `2` | バッチ抽出の最大秒数 |
| `Pdf__TempRoot` | `/tmp/amane-pdf-api`（Linux標準環境） | 処理専用一時領域の親ディレクトリ |

.NET標準configurationの`Pdf`セクションでも同じ設定ができます。不正な制限値は起動時に拒否します。JPEGMEMの`M`/`m`は1,000,000 bytes、接尾辞なしは1,000 bytes単位です（MiBは1,048,576 bytes）。空白、符号、`MiB`等は受け付けません。単位は[libjpeg-turboのJPEGMEM実装](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/2.1.5/jmemmgr.c)に従います。
Linuxでは共通の起動経路を`/usr/bin/env --ignore-signal=XFSZ -- prlimit --core=0:0 --as=... [--fsize=...] -- tool args...`とし、FSIZE超過によるSIGXFSZを無視してcore生成を防ぎます。CORE=0は補助対策であり、単独ではpipe collectorへの受渡しを止めません。SIGSEGV／ABRT等の別signalによるcoreはこの対策の対象外です。shellは使わず、envとprlimitはexecで実toolへ置き換わります。
Linuxでは同じ経路で`/bin/true`と`qpdf --version`を起動して0終了を確認し、失敗・timeoutならHTTP待受を開始せず終了します。env不在・オプション非対応でも無対策の経路へfallbackしません。ログには固定文言だけを出します。Linuxではdjpeg／cjpegも小さな画像を実際に変換し、-maxmemory／-maxscans／-strict／-scaleとAS／fsize付き起動を確認します。失敗時の固定ログは「PDF処理のメモリ制限の自己テストに失敗しました。」です。非LinuxではJPEGMEMだけを適用し、Linuxの制限と自己テストは適用しません。
待ち行列は0です。ASP.NET Core標準Concurrency Limiterでbodyを読み始める前に処理枠を取得し、上限超過には503を返します。`GET /healthz`はこの制限の対象外です。

単一PDF APIのリクエスト総量上限はPDF上限 + 64 KiB、mergeでは入力合計上限 + 64 KiBです。multipartのヘッダー/境界/password用の余裕であり、PDF自体の上限は緩めません。
Content-Lengthの早期チェック、Kestrelのbody上限、実読込bytesのカウントを併用します。
MultipartReaderでPDFを直接一時ファイルへストリーム保存し、上限を超える1 byteを検出したら保存を停止します。フォームの全量bufferや二重の一時保存は行いません。
passwordも127 bytesまでに制限し、UTF-8として不正な入力は400で拒否します。

mergeでは11個目（設定した上限の次）のfile partを発見した時点で、新しい一時ファイルへ書き込む前に400で拒否します。単一・合計・requestのサイズ超過は413です。

30秒はアップロード完了後のqpdf検査と各PDF処理（unlockはJSON作成・認証・入力検査・解除・出力検証を含む）、検査・ページ数取得・ページ操作、または全merge入力検証・結合の合計です。timeout/クライアント切断時はprocess treeをkillして終了を待ち、一時ファイルを削除します。アプリ停止時もアップロードとqpdfをキャンセルします。
暗号化、解除、最適化、圧縮、結合、分割、回転、抽出、削除、並べ替えは同じ同時実行枠と一時領域を共有します。既暗号化PDFはunlock以外の処理APIで拒否します。

merge入力合計50 MiBは、同時2 requestの入力・出力・小さな処理ファイルをDocker例のtmpfs 256 MiBへ収めやすくする初期値です。出力サイズを数学的に保証する上限ではありません。出力の増加やqpdfのメモリ使用に対しては、tmpfs 256 MiB / memory 1.5 GiBなど実行環境側の上限を引き続き安全境界として使用します。設定を増やす場合は同時実行数と一時領域・メモリ容量も合わせて調整してください。
ファイルサイズが50 MiB以内でも、大きな画像のデコードやqpdfのbufferが上限に達すると422になり得ます。破損、警告、処理上限超過をstderrで区別しません。全qpdf呼び出し（unlockの各probe・出力検証、64 bytesのページ数取得を含む）へ同じ制限を適用します。出力検証の失敗は500です。

運用では次を目安に、コンテナmemory、AS、同時実行数、tmpfsを一緒に見直してください。

```text
コンテナmemory ≥ .NET API ＋ tmpfs上限 ＋ 同時処理数 × qpdfのAS上限 ＋ 余裕
標準の見積り：1536 MiB ≥ 128 MiB ＋ 256 MiB ＋ 2 × 544 MiB ＋ 64 MiB
```

標準環境はmemory 1.5 GiB、memory-swapも1.5 GiB（swapなし）、CPU 1、tmpfs 256 MiB、同時2です。6000×4000（24 MP）と8064×6048（約48 MP）のカラーJPEGについて、baseline 4:2:0、baseline 4:4:4、progressive 4:2:0を含む合成写真PDFを、設定を上書きせず同時2件の実HTTPで処理できることを確認しました。12 MPはbaseline 4:2:0だけを確認しています。この結果は任意の画像形式・PDFの成功を保証するものではありません。

qpdfの観測VmSizeは対象写真で最大457.3 MiB、追加で確認した100 MPグレーprogressiveでは487.2 MiBでした。AS 544 MiBはそれぞれ約86.7 MiB（19.0%）／56.8 MiB（11.7%）の余裕を持ちます。JPEGMEM 600Mは600,000,000 bytesで、標準AS全体の570,425,344 bytesより大きくし、JPEGの予算が先に処理範囲を狭めないようにしています。巨大progressiveの早期拒否には、ASを増やす運用で効果を確認しています。

128 MiB（.NET API）と64 MiB（余裕）は容量計画用の見積りで、.NETへのhard limitではありません。tmpfs圧力下の各形式60要求ではAPI RSS最大109.2 MiB、全標準負荷のcgroup peak最大982.7 MiB、OOMイベント0でした。managed heapとGC committedも測定しました。詳しい数値は [docs/qpdf-memory-validation.md](docs/qpdf-memory-validation.md) に記載しています。式は仮想メモリ上限を使った目安で、任意のPDFや将来のruntimeでの成功・OOM回避を数学的に保証しません。標準値の変更やruntime更新時には実HTTP負荷と`memory.peak` / `memory.events`を再測定してください。

compressのファイル台帳は4 KiB単位で計上します。pages／metadata JSONはファイル出力時のfsizeで制限し、候補辞書を最大500参照ずつspoolします。ページ、Parent、Resources、XObjectは最大500ページの単位内で階層ごとに一括取得し、画像参照を集めたらページ側辞書と別名の連鎖を削除してspool・容量台帳から外します。低いspool設定では単位を小さくします。画像辞書とSMask／Mask／ICCは、その後に判定へ必要な間だけ保持します。raw抽出は最大50画像、`S = round4KiB(最大Length＋4KiB)`とし、Length合計と`n×S`の両方がPNM・新JPEGを残した領域へ収まるバッチを選びます。rawのfsizeはS、抽出JSONは上限付きstdoutです。PNMは`ceil(幅×M/8)×ceil(高さ×M/8)×成分数＋header`の実サイズに512 bytesを加えて予約・fsizeを設定します。新JPEGは元の90%と残り容量でfsizeを決めます。

```text
I = 入力実サイズ、R = 採用した元JPEGの実サイズ合計、A = 採用JPEGの実サイズ合計
J = update JSON実サイズ、H = 4 MiB、L = job上限
E = I − R + A、F = E + H
採用条件: I + A + J + F ≤ L （割当量も4 KiB単位で別途確認）
標準tmpfs: 256 MiB ≥ 2 × 124 MiB + 8 MiB
```

入力・metadata・raw・PNM・新JPEG・採用JPEG・JSONの同時存在も台帳で制限します。update.jsonの結合時はentry断片群と完成JSONが同時に存在するため、その合計割当量（各ファイルを4 KiBに切り上げ）も採用前に確認します。不採用の中間物を削除し、最終処理前には不要なraw／PNM／metadataを削除します。出力見積りは数学的上界ではなく、最終出力の実サイズがfsize上限以上なら、exit 0でも部分出力を削除して500です。同値も安全側に拒否します。画像段階の書込みエラーは不採用、126／127は500です。SIGXFSZ無視時のqpdfはJSONが途中で切れてもexit 0となり得るため、pages／metadata JSONの実サイズがfsize上限以上なら既知のmetadata上限として画像処理を終了します。同値でも安全側に判定し、既に採用した画像があれば保持して最終処理へ進みます。想定外のmetadata異常や最終処理の2／3を含む失敗は500です。

APIの制限は設定で調整できる安全境界です。job上限・PNM・画素数・画像数・AS・同時処理数を増やすときは、tmpfsとコンテナmemoryも上の式と合わせて見直してください。qpdfとJPEGツールは各job内で逐次起動するため、メモリ計画には両者のASの大きい方を使います。実HTTPの圧縮率・画質、段階別時間、memory.peak／memory.events、tmpfsは[圧縮の実測記録](docs/compress-validation.md)に記載します。数値は対象fixtureでの事実であり、他のPDFの保証ではありません。

splitは入力・ZIP・1パートを4 KiBへ切り上げて数えます。次のパートには、入力と既存ZIPを除いた予算からZIP余裕1 MiB・FSIZE検出用4 KiBを引き、残りの半分を4 KiBへ切り下げて予約します。パートとZIPコピーの重複を含むためです。ZIPの最終ファイル長は採用したPDF合計＋1 MiB以内とし、ヘッダーの巻き戻し書込みは累計bytesではなく最終長で検査します。

`MaxSplitOutputBytes`は正、最大4,293,918,718 bytesで、完成ZIPをuint.MaxValue未満に保ちZIP64を使いません。`MaxSplitJobBytes`は`A(MaxFileBytes) + 1 MiB + 3×4096`以上、`long.MaxValue−4095`以下です（Aは4 KiB切り上げ）。overflowを含む不正な設定は起動時に拒否します。PDF合計の上限を増やしてもjob予算・tmpfsは増えません。4 KiB pageのx86_64 Linux Dockerが容量検証の前提です。64 KiB pageのarm64等は再測定が必要です。

Linuxのパート生成は共通AS/JPEGMEMと`RLIMIT_FSIZE=partBudget+1`を使い、終了待ち後に実サイズを比較します。exit 0でも予算を超えれば422です。非Linuxにもsplitを登録しますが、qpdf書出し中のFSIZE/ASのhard limitは適用できず、処理後とZIPコピー時のサイズ検査だけです。全パートの生成・検査・コピー・ZIP最終化は、アップロード後の一つの30秒を共有します。100/500パートや任意PDFの時間内成功を保証しません。

標準値で拒否される大きな画像を扱う例（2 GiB、AS 768 MiB）：

```bash
docker run --rm -p 127.0.0.1:8080:8080 \
  --read-only --tmpfs /tmp:rw,nosuid,nodev,noexec,size=256m \
  --cpus 1 --memory 2g --memory-swap 2g --cap-drop ALL \
  --security-opt no-new-privileges=true \
  -e Pdf__QpdfAddressSpaceLimitBytes=805306368 \
  -e Pdf__QpdfJpegMemory=900M amane-pdf-api:dev
```

この変更で100 MPカラーbaseline 4:2:0 JPEGを含む同じPDFが422から200になることを確認しました。ASを増やす場合はJPEGMEMも見直してください。JPEGをASより先に制限しない目安は、JPEGMEMのbytes値をASのbytes値以上にすることです。例の900Mは900,000,000 bytes、ASは805,306,368 bytesです。巨大progressiveを先に拒否する目的でJPEGMEMを低くする場合は、受け付ける範囲が狭くなることを実測してください。

利用者/IP単位の利用回数制限やアップロード接続の運用制御は、`amane-tools-site` / Caddy等の入口側の責務です。

## 構成

```text
Amane.Pdf.Api.slnx
src/
  Amane.Pdf.Api/
    Program.cs
    Amane.Pdf.Api.csproj
tests/
  Amane.Pdf.Api.Tests/
docs/
  security.md
Dockerfile
```

意図的に小さな構成から開始します。必要になるまで、DB、メッセージキュー、複雑なレイヤー分割は導入しません。

## 開発環境

必要なもの:

- .NET 10 SDK
- Linuxではprlimit（util-linux）と、`--ignore-signal=XFSZ`に対応する`/usr/bin/env`（通常はOSに同梱）
- qpdf（QPDFJob JSON / AES-256対応。CIでは実qpdfをインストールして検証）
- Linuxではlibjpeg-turbo-progs（djpeg／cjpegと必要なオプション）
- Docker（コンテナ確認を行う場合）
- Python 3（Docker smoke test。追加package不要）

ビルドとテスト:

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

ローカル起動:

```bash
dotnet run --project src/Amane.Pdf.Api --urls http://127.0.0.1:8080
```

起動後:

```text
GET /healthz
```

は `200 Healthy` を返します。

Docker:

```bash
docker build -t amane-pdf-api:dev .
docker run --rm -p 127.0.0.1:8080:8080 \
  --read-only --tmpfs /tmp:rw,nosuid,nodev,noexec,size=256m \
  --cpus 1 --memory 1.5g --memory-swap 1.5g --cap-drop ALL \
  --security-opt no-new-privileges=true amane-pdf-api:dev
```

コンテナ内のqpdf確認:

```bash
docker run --rm --entrypoint qpdf amane-pdf-api:dev --version
```

## Docker / CIの検証

CIと同じ実コンテナsmoke test:

```bash
docker build -t amane-pdf-api:ci .
python3 scripts/docker-smoke.py amane-pdf-api:ci
```

CIはUbuntu 26.04 runnerでrestore、Release build、全自動テスト、Docker build、Docker runとsmoke test、合成fixtureの構造・データ・Poppler描画比較を実行します。poppler-utils／Pillow／NumPyはCI・測定専用で、runtimeには追加しません。Ubuntu hostのqpdf AppArmorは拡張子なしのraw出力を拒否するため、hostテストでは同じ配布バイナリの一時コピーをPATHへ置きます。runtimeのRunnerや起動時自己テストには特例を設けません。runtime stageのベースは `mcr.microsoft.com/dotnet/aspnet:10.0-resolute`（Ubuntu 26.04）です。
host/containerのqpdfが12系以降であること、コンテナの `--remove-info` / `--remove-metadata` の存在を確認します。QPDFJob JSON、Unicode password、AES-256、入力検査に必要な機能は実処理で確認します。必要機能が欠けるimageではCIが失敗します。

smoke testは既存APIに加え、compressの両level・カラー／グレーの実圧縮、必要オプション、権限、起動失敗、実FSIZE超過時のdjpeg／cjpeg／ddの通常エラー終了、3つのJPEG copyrightファイルとhealthを検証します。共通起動経路のCORE／AS／FSIZEとSIGXFSZ無視の継承も確認します。正常暗号化、解除成功とwrong-password、lossless最適化、入力順を確認する代表的なPDF結合、相対回転とページ指定、代表的なページ抽出、正password/誤password、必須項目不足、空/非PDF/破損/warning/既暗号化、サイズ境界とContent-Lengthなしの413、内部障害の500、ログ非露出、一時ファイル削除が対象です。
splitはevery=1、Stored/固定時刻/名前、別TZのDOS時刻、APIと同じenv／prlimit経由の終了コード、HTTPの同値/1 byte超過/実FSIZE超過、cat 500とcleanup/healthを確認します。容量・同時実行・100パートの時間は通常のsmokeへ入れず、`sudo python3 scripts/split-validation.py IMAGE /tmp/split-metrics.json`で別に測定します。[分割の検証記録](docs/split-validation.md)と[FSIZE core対策の検証記録](docs/fsize-core-validation.md)に結果と公開条件を記載しています。
全コンテナでnon-root、read-only root filesystem、tmpfs /tmp、CPU 1 / memory 1.5 GiB（swapなし）、永続Volumeなしを確認します。外部networkを無効にした別コンテナでもloopback HTTPで実暗号化、解除成功とwrong-password、password確認を行います。
小さいASで通常PDFの成功と画像PDFの422、標準ASへ増やした同じPDFの成功、自己テスト失敗時の非0終了と固定ログ、util-linux copyrightの存在も確認します。各コンテナは成功・失敗ともfinallyで削除します。qpdfのtimeout/process tree kill、同時実行上限とキャンセルは.NETテストで確認します。

確認したqpdf versionは開発環境12.3.2、コンテナ12.3.2です。CIではhost/containerそれぞれのversionをログへ出します。qpdfはaptから導入し、完全なversion pinを目的とせず、必要機能を検証します。
2026-10時点、commit `f847c68`でqpdf 11.9.0のunlock動作を手動確認済みです。CIの対象は12系以降です。
将来releaseを行う場合は、そのCI runのimage digestとqpdf versionを使用imageと対応付けて記録してください。

DB、Secret、PDFの永続Volumeは不要です。writable領域は/tmpだけで成立します。CPU/memory/tmpfsの容量は起動オプションで外側から調整できます。
実行時に外向き通信は不要で、`--network none`でも処理可能です。サービス間の到達性と入口側の利用回数制限は実行環境で設定します。
これらの確認はローカル/CIの検証であり、本番deployやGHCR publishは行いません。

## セキュリティ

PDFは外部から受け取る信用できない入力として扱います。

入力検証、サイズ/処理時間/同時実行制限、一時ファイル削除、qpdfへの安全なパスワード入力を実装しています。
利用者/IP単位Rate Limit、コンテナのCPU/メモリ上限、外向き通信の制御は入口と実行環境の責務です。

詳細は [docs/security.md](docs/security.md) を参照してください。

## ドキュメント

人が読むドキュメントは原則として日本語で記述します。

コード、API名、製品名、コマンド、標準的な技術用語など、日本語化すると分かりにくくなる部分は英語表記を使用します。

- [docs/security.md](docs/security.md): セキュリティ方針
- [docs/roadmap.md](docs/roadmap.md): PDF機能拡張の方針、採用した判断と検証記録

## ライセンス

このプロジェクトは Apache License 2.0 で公開します。

PDF処理に使用するqpdfもApache License 2.0です。第三者ソフトウェアについては [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。
