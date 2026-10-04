# amane-pdf-api

PDFを安全に処理するための小さなWeb APIです。

最初の用途は、`amane-tools-site` から受け取ったPDFへパスワードを設定し、暗号化済みPDFを返すことです。

## 現在の状態

実装済み:

- .NET 10 / ASP.NET Core の最小API、`GET /healthz`
- `POST /api/pdf/protect` によるPDFのAES-256暗号化
- 独立したランダムowner password、QPDFJob JSON経由のパスワード入力
- 成功・失敗・キャンセル時の一時ファイル削除
- 実qpdfを使った自動テスト、Docker build、GitHub Actions

後続Issueで実装する項目:

- 詳細なPDF入力検証とエラー仕様（#3）
- ファイルサイズ・処理時間・同時実行数の制限（#4）
- Docker上のE2EをCIで検証（#5）

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

```text
POST /api/pdf/protect
Content-Type: multipart/form-data

file      PDFファイル
password  PDFを開くためのパスワード
```

成功時は `200 OK`、`Content-Type: application/pdf` で暗号化済みPDFを返します。
qpdfがexit code 0で終了するまでレスポンスを開始しません。利用者ファイル名は保存先にも返却名にも使いません。

```bash
curl --fail-with-body -F file=@tests/Amane.Pdf.Api.Tests/Fixtures/sample.pdf \
  -F password=example-password http://localhost:8080/api/pdf/protect -o protected.pdf
```

このパスワードは動作確認用の例です。実パスワードをshell履歴へ残さない方法は呼び出し側で用意してください。

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

ビルドとテスト:

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

ローカル起動:

```bash
dotnet run --project src/Amane.Pdf.Api --urls http://localhost:8080
```

起動後:

```text
GET /healthz
```

は `200 Healthy` を返します。

Docker:

```bash
docker build -t amane-pdf-api:dev .
docker run --rm -p 8080:8080 amane-pdf-api:dev
```

コンテナ内のqpdf確認:

```bash
docker run --rm --entrypoint qpdf amane-pdf-api:dev --version
```

## セキュリティ

PDFは外部から受け取る信用できない入力として扱います。

一時ファイル削除とqpdfへの安全な入力は実装済みです。後続Issueと入口側の構成で次を対応します。

- アップロードサイズ上限
- 処理時間上限
- 同時実行数上限
- 利用者/IP単位Rate Limit（入口側の責務）
- コンテナのCPU / メモリ制限
- PDF処理コンテナから不要な外部通信を許可しない構成

詳細は [docs/security.md](docs/security.md) を参照してください。

## ドキュメント

人が読むドキュメントは原則として日本語で記述します。

コード、API名、製品名、コマンド、標準的な技術用語など、日本語化すると分かりにくくなる部分は英語表記を使用します。

## ライセンス

このプロジェクトは Apache License 2.0 で公開します。

PDF処理に使用するqpdfもApache License 2.0です。第三者ソフトウェアについては [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。
