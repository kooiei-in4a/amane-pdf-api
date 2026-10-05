# amane-pdf-api

PDFを安全に処理するための小さなWeb APIです。

`amane-tools-site` から受け取ったPDFの暗号化、lossless構造最適化、ページ回転、ページ抽出・削除・並べ替えを、安全なqpdf処理として提供します。

## 現在の状態

実装済み:

- .NET 10 / ASP.NET Core の最小API、`GET /healthz`
- `POST /api/pdf/protect` によるPDFのAES-256暗号化
- `POST /api/pdf/optimize` によるPDFのlossless構造最適化
- `POST /api/pdf/rotate` による全ページまたは指定ページの相対回転
- `POST /api/pdf/extract` による指定ページの抽出
- `POST /api/pdf/delete-pages` による指定ページの削除
- `POST /api/pdf/reorder` による全ページの並べ替え
- 独立したランダムowner password、QPDFJob JSON経由のパスワード入力
- 成功・失敗・キャンセル時の一時ファイル削除
- 正常PDFの検証、破損/警告/既暗号化PDFの拒否、統一Problem Details
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

暗号化APIでは、`file` は1ファイル、`password` は1項目で必須です。空のpassword、制御文字、UTF-8で127 bytesを超えるpasswordは拒否します。空白はtrimしません。最適化、回転、抽出、削除、並べ替えAPIでは`file`だけを受け付け、`password`を含む予期しないfieldは400で拒否します。
Unicodeはqpdfの`passwordMode=unicode`でUTF-8として渡します。API側ではUnicode正規化やtrimを行いません。

Content-Typeや拡張子だけでPDFを判定せず、実qpdfの`--check`を使います。
exit code 2（error）と3（warning）は422として拒否し、自動修復したPDFを成功扱いにしません。
既暗号化PDFは、password不要で開けるものや送信したpasswordが一致するものも含め、v1では全操作で拒否します。

| HTTP status | 条件 |
| --- | --- |
| 200 | 暗号化、最適化、回転、抽出、削除、並べ替え済みPDFを返却 |
| 400 | multipart形式不正、必須項目不足/重複、passwordまたはangle/pages仕様違反 |
| 413 | PDFサイズまたはmultipartリクエスト総量の超過 |
| 422 | 空ファイル、非PDF、破損/警告のあるPDF、既暗号化PDF |
| 500 | qpdf実行環境や処理中の想定外の内部障害 |
| 503 | 同時PDF処理数の上限超過（待ち行列なし） |
| 504 | qpdfの検査と各PDF処理全体の制限時間超過 |

エラーはASP.NET Core標準の`application/problem+json`です。固定文言のみを返し、内部path、password、PDF本文、stack trace、qpdf出力は返しません。

## リソース制限と設定

| 環境変数 | 初期値 | 意味 |
| --- | --- | --- |
| `Pdf__MaxFileBytes` | `52428800`（50 MiB） | 実際に読み込むPDFの最大bytes |
| `Pdf__QpdfTimeoutSeconds` | `30` | qpdfの検査と各PDF処理全体の最大秒数 |
| `Pdf__MaxConcurrentProcesses` | `2` | 同時PDF処理枠。アップロードから送信/削除まで保持 |
| `Pdf__QpdfPath` | `qpdf` | qpdf実行ファイル |
| `Pdf__TempRoot` | `/tmp/amane-pdf-api`（Linux標準環境） | 処理専用一時領域の親ディレクトリ |

.NET標準configurationの`Pdf`セクションでも同じ設定ができます。不正な制限値は起動時に拒否します。
待ち行列は0です。ASP.NET Core標準Concurrency Limiterでbodyを読み始める前に処理枠を取得し、上限超過には503を返します。`GET /healthz`はこの制限の対象外です。

リクエスト総量の上限はPDF上限 + 64 KiBです。multipartのヘッダー/境界/password用の余裕であり、PDF自体の上限は緩めません。
Content-Lengthの早期チェック、Kestrelのbody上限、実読込bytesのカウントを併用します。
MultipartReaderでPDFを直接一時ファイルへストリーム保存し、上限を超える1 byteを検出したら保存を停止します。フォームの全量bufferや二重の一時保存は行いません。
passwordも127 bytesまでに制限し、UTF-8として不正な入力は400で拒否します。

30秒はアップロード完了後のqpdf検査と各PDF処理、または検査・ページ数取得・ページ操作の合計です。timeout/クライアント切断時はprocess treeをkillして終了を待ち、一時ファイルを削除します。アプリ停止時もqpdfをキャンセルします。
暗号化、最適化、回転、抽出、削除、並べ替えは同じ同時実行枠と一時領域・サイズ制限を共有します。既暗号化PDFはすべての処理APIで拒否します。
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
  --cpus 1 --memory 512m --cap-drop ALL \
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

CIではrestore、Release build、全自動テスト、Docker build、Docker runとsmoke testを実行します。
qpdfの存在/versionだけでなく、QPDFJob JSON、Unicode password、AES-256、入力検査に必要な機能を実処理で確認します。必要機能が欠けるimageではCIが失敗します。

smoke testは17件のPOSTとhealthを検証します。正常暗号化、lossless最適化、相対回転とページ指定、代表的なページ抽出、正password/誤password、必須項目不足、空/非PDF/破損/warning/既暗号化、サイズ境界とContent-Lengthなしの413、内部障害の500、ログ非露出、一時ファイル削除が対象です。
全コンテナでnon-root、read-only root filesystem、tmpfs /tmp、CPU 1 / memory 512 MiB、永続Volumeなしを確認します。外部networkを無効にした別コンテナでもloopback HTTPで実暗号化とpassword確認を行います。
各コンテナは成功・失敗ともfinallyで削除します。qpdfのtimeout/process tree kill、同時実行上限とキャンセルは.NETテストで確認します。

確認したqpdf versionは開発環境12.3.2、コンテナ11.9.0です。CIではhost/containerそれぞれのversionをログへ出します。qpdfの完全なversion pinを目的とせず、必要機能を検証します。
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

## ライセンス

このプロジェクトは Apache License 2.0 で公開します。

PDF処理に使用するqpdfもApache License 2.0です。第三者ソフトウェアについては [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。
