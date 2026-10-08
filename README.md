# amane-pdf-api

PDFを安全に処理するための小さなWeb APIです。

`amane-tools-site` から受け取ったPDFの暗号化・パスワード解除、lossless構造最適化、結合、ページ回転、ページ抽出・削除・並べ替えを、安全なqpdf処理として提供します。

## 現在の状態

実装済み:

- .NET 10 / ASP.NET Core の最小API、`GET /healthz`
- `POST /api/pdf/protect` によるPDFのAES-256暗号化
- `POST /api/pdf/unlock` による、正しいuser / owner passwordでの暗号化解除
- `POST /api/pdf/optimize` によるPDFのlossless構造最適化
- `POST /api/pdf/merge` による複数PDFのアップロード順での結合
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

### 入力とエラー

暗号化・解除APIでは、`file` は1ファイル、`password` は1項目で必須です。空のpassword、制御文字、UTF-8で127 bytesを超えるpasswordは拒否します。空白はtrimしません。最適化、回転、抽出、削除、並べ替えAPIでは`file`だけを受け付け、`password`を含む予期しないfieldは400で拒否します。
Unicodeはqpdfの`passwordMode=unicode`でUTF-8として渡します。API側ではUnicode正規化やtrimを行いません。

Content-Typeや拡張子だけでPDFを判定せず、実qpdfの`--check`を使います。
入力構造検査のexit code 2（error）と3（warning）は422として拒否し、自動修復したPDFを成功扱いにしません。
既暗号化PDFは、password不要で開けるものや送信したpasswordが一致するものも含め、unlock以外の全操作で拒否します。

| HTTP status | 条件 |
| --- | --- |
| 200 | 暗号化、解除、最適化、結合、回転、抽出、削除、並べ替え済みPDFを返却 |
| 400 | multipart形式不正、必須項目不足/重複、予期しないfield、mergeのファイル数不足/超過、passwordまたはangle/pages仕様違反 |
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
| `Pdf__QpdfTimeoutSeconds` | `30` | qpdfの検査と各PDF処理全体の最大秒数 |
| `Pdf__MaxConcurrentProcesses` | `2` | 同時PDF処理枠。アップロードから送信/削除まで保持 |
| `Pdf__QpdfPath` | `qpdf` | qpdf実行ファイル |
| `Pdf__PrlimitPath` | `/usr/bin/prlimit` | Linuxの制限付き実行に使うprlimit |
| `Pdf__QpdfAddressSpaceLimitBytes` | `339738624`（324 MiB） | Linuxでqpdfに適用するRLIMIT_AS。正のbytes値 |
| `Pdf__QpdfJpegMemory` | `64M` | 全OSのqpdfに渡すJPEGMEM。正のASCII数字＋任意の`M`/`m`、30文字未満、単位換算後のsigned long overflowを拒否 |
| `Pdf__TempRoot` | `/tmp/amane-pdf-api`（Linux標準環境） | 処理専用一時領域の親ディレクトリ |

.NET標準configurationの`Pdf`セクションでも同じ設定ができます。不正な制限値は起動時に拒否します。JPEGMEMの`M`/`m`は1,000,000 bytes、接尾辞なしは1,000 bytes単位です（MiBは1,048,576 bytes）。空白、符号、`MiB`等は受け付けません。単位は[libjpeg-turboのJPEGMEM実装](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/2.1.5/jmemmgr.c)に従います。
Linuxでは同じAS制限で`/bin/true`と`qpdf --version`を起動して0終了を確認し、失敗・timeoutならHTTP待受を開始せず終了します。ログには固定文言だけを出します。非LinuxではJPEGMEMだけを適用し、ASと自己テストは適用しません。
待ち行列は0です。ASP.NET Core標準Concurrency Limiterでbodyを読み始める前に処理枠を取得し、上限超過には503を返します。`GET /healthz`はこの制限の対象外です。

単一PDF APIのリクエスト総量上限はPDF上限 + 64 KiB、mergeでは入力合計上限 + 64 KiBです。multipartのヘッダー/境界/password用の余裕であり、PDF自体の上限は緩めません。
Content-Lengthの早期チェック、Kestrelのbody上限、実読込bytesのカウントを併用します。
MultipartReaderでPDFを直接一時ファイルへストリーム保存し、上限を超える1 byteを検出したら保存を停止します。フォームの全量bufferや二重の一時保存は行いません。
passwordも127 bytesまでに制限し、UTF-8として不正な入力は400で拒否します。

mergeでは11個目（設定した上限の次）のfile partを発見した時点で、新しい一時ファイルへ書き込む前に400で拒否します。単一・合計・requestのサイズ超過は413です。

30秒はアップロード完了後のqpdf検査と各PDF処理（unlockはJSON作成・認証・入力検査・解除・出力検証を含む）、検査・ページ数取得・ページ操作、または全merge入力検証・結合の合計です。timeout/クライアント切断時はprocess treeをkillして終了を待ち、一時ファイルを削除します。アプリ停止時もアップロードとqpdfをキャンセルします。
暗号化、解除、最適化、結合、回転、抽出、削除、並べ替えは同じ同時実行枠と一時領域・サイズ制限を共有します。既暗号化PDFはunlock以外の処理APIで拒否します。

merge入力合計50 MiBは、同時2 requestの入力・出力・小さな処理ファイルをDocker例のtmpfs 256 MiBへ収めやすくする初期値です。出力サイズを数学的に保証する上限ではありません。出力の増加やqpdfのメモリ使用に対しては、tmpfs 256 MiB / memory 1 GiBなど実行環境側の上限を引き続き安全境界として使用します。設定を増やす場合は同時実行数と一時領域・メモリ容量も合わせて調整してください。
ファイルサイズが50 MiB以内でも、大きな画像のデコードやqpdfのbufferが上限に達すると422になり得ます。破損、警告、処理上限超過をstderrで区別しません。全qpdf呼び出し（unlockの各probe・出力検証、64 bytesのページ数取得を含む）へ同じ制限を適用します。出力検証の失敗は500です。

運用では次を目安に、コンテナmemory、AS、同時実行数、tmpfsを一緒に見直してください。

```text
コンテナmemory ≥ .NET API ＋ tmpfs上限 ＋ 同時処理数 × qpdfのAS上限 ＋ 余裕
標準の見積り：1024 MiB ≥ 112 MiB ＋ 256 MiB ＋ 2 × 324 MiB ＋ 8 MiB
```

.NETの継続負荷で確認したRSSは約110 MiB、qpdfの実測ASは最大320.2 MiBです。112 MiBと8 MiBは容量計画用の見積りで、.NETへのhard limitではありません。tmpfs圧力下の同時2件・60要求ではcgroupピーク約711 MiB、OOMなしを確認しました。式は仮想メモリ上限を使った目安で、任意のPDFや将来のruntimeでの成功・OOM回避を数学的に保証しません。標準値の変更やruntime更新時には実HTTP負荷と`memory.peak` / `memory.events`を再測定してください。詳しい数値と測定範囲は [docs/qpdf-memory-validation.md](docs/qpdf-memory-validation.md) に記載しています。

標準値で拒否される大きな画像を扱う例（2 GiB、AS 768 MiB）：

```bash
docker run --rm -p 127.0.0.1:8080:8080 \
  --read-only --tmpfs /tmp:rw,nosuid,nodev,noexec,size=256m \
  --cpus 1 --memory 2g --memory-swap 2g --cap-drop ALL \
  --security-opt no-new-privileges=true \
  -e Pdf__QpdfAddressSpaceLimitBytes=805306368 amane-pdf-api:dev
```

この変更で100 MPカラーbaseline JPEGを含む同じPDFが422から200になることを確認しました。progressive JPEGがJPEGMEMに達する場合は、`Pdf__QpdfJpegMemory`もコンテナ容量と合わせて見直します。

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
- Linuxではprlimit（util-linux。通常はOSに同梱）
- qpdf（QPDFJob JSON / AES-256対応。CIでは実qpdfをインストールして検証）
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
  --cpus 1 --memory 1g --memory-swap 1g --cap-drop ALL \
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

CIはUbuntu 26.04 runnerでrestore、Release build、全自動テスト、Docker build、Docker runとsmoke testを実行します。runtime stageのベースは `mcr.microsoft.com/dotnet/aspnet:10.0-resolute`（Ubuntu 26.04）です。
host/containerのqpdfが12系以降であること、コンテナの `--remove-info` / `--remove-metadata` の存在を確認します。QPDFJob JSON、Unicode password、AES-256、入力検査に必要な機能は実処理で確認します。必要機能が欠けるimageではCIが失敗します。

smoke testは25件のPOSTとhealthを検証します。正常暗号化、解除成功とwrong-password、lossless最適化、入力順を確認する代表的なPDF結合、相対回転とページ指定、代表的なページ抽出、正password/誤password、必須項目不足、空/非PDF/破損/warning/既暗号化、サイズ境界とContent-Lengthなしの413、内部障害の500、ログ非露出、一時ファイル削除が対象です。
全コンテナでnon-root、read-only root filesystem、tmpfs /tmp、CPU 1 / memory 1 GiB（swapなし）、永続Volumeなしを確認します。外部networkを無効にした別コンテナでもloopback HTTPで実暗号化、解除成功とwrong-password、password確認を行います。
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
