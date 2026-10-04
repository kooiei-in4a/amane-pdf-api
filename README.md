# amane-pdf-api

PDFを安全に処理するための小さなWeb APIです。

最初の用途は、`amane-tools-site` から受け取ったPDFへパスワードを設定し、暗号化済みPDFを返すことです。

## 現在の状態

初期化段階です。

実装済み:

- .NET 10 / ASP.NET Core の最小API
- `GET /healthz`
- Docker build
- 自動テストとGitHub Actions
- qpdfを含む実行コンテナの骨格

未実装:

- PDFアップロード
- パスワード設定
- qpdf呼び出し
- ファイルサイズ・処理時間・同時実行数の制限
- `amane-tools-site` との接続

PDF暗号化機能は、初期化とは別のIssue / Pull Requestで実装します。

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

## 予定しているAPI

```text
POST /api/pdf/protect
Content-Type: multipart/form-data

file      PDFファイル
password  PDFを開くためのパスワード
```

成功時は暗号化済みPDFを返す予定です。

このエンドポイントはまだ実装していません。

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
- Docker（コンテナ確認を行う場合）

ビルドとテスト:

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
```

ローカル起動:

```bash
dotnet run --project src/Amane.Pdf.Api
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

本番公開までに、少なくとも次を実装します。

- アップロードサイズ上限
- 処理時間上限
- 同時実行数上限
- Rate Limit
- 一時ファイルの確実な削除
- qpdf実行時のパスワード露出防止
- コンテナのCPU / メモリ制限
- PDF処理コンテナから不要な外部通信を許可しない構成

詳細は [docs/security.md](docs/security.md) を参照してください。

## ドキュメント

人が読むドキュメントは原則として日本語で記述します。

コード、API名、製品名、コマンド、標準的な技術用語など、日本語化すると分かりにくくなる部分は英語表記を使用します。

## ライセンス

このプロジェクトは Apache License 2.0 で公開します。

PDF処理に使用するqpdfもApache License 2.0です。第三者ソフトウェアについては [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。
