# 外部依存のHigh対応（Issue #91）

## 更新内容

2026-10-25。監査PR #90で検出したHigh 5件のうち、pdfcpu内のGo 1.27.2が1件、ベースイメージのpebble内Go 1.27.1が4件。到達可能性の立証とは区別し、監査条件の変更・除外をせず、実行物を更新・除去する。

- pdfcpu 0.16.3の公式配布archiveもGo 1.27.2だった。単に配布版を更新するだけではCVE-2026-111943が残るため、公式moduleソースを固定Go 1.27.3 SDKでビルドする。
- SDKはGo公式download metadataのSHA-256、pdfcpuソースはGo checksum databaseのmodule/go.mod checksumへ固定する。Go proxyとchecksum databaseを明示し、toolchain自動切替を禁止。成果物のGo版・全Linux module・ライセンスを実行前に照合する。
- pdfcpu 0.16.3ではrunewidth / x/crypto / x/image / x/textも更新される。moduleは12件、Linux binaryは10件のまま。更新されたmoduleとGo SDKのLICENSE/PATENTS・runtime vendor表示、pdfcpuのLICENSEと内部pkcs7/lzwは既存原文と一致した。ソースの著作権表示を補完した。
- `main.date` は固定ソースcommit時刻を使い、`-trimpath`・CGO無効・linux/amd64/v1でビルドする。SDK・取得物・module/cacheはbuild時の一時領域に限定し、runtimeへコピーしない。
- pebbleはdpkg管理外のbase image内binaryで、このAPIのENTRYPOINT・process runnerから呼ばない。apt導入後に `/usr/bin/pebble` を除去する。Docker smokeでpebble・Goコンパイラの不在を検査する。

## 互換性と検証

pdfcpu 0.16.3はdecoded-byte上限とcreate/formのローカル画像参照制限を含む。APIのcreate JSONは文字だけを生成し、画像のCLI import/stampは従来のjob内固定pathを使う。画像・文字の既存E2Eで互換性を検証する。過去の版の測定値は書き換えない。

ローカルでは公式配布物・Go SDK・font・moduleの固定情報とライセンスを照合し、installer/validatorの構文を確認した。SDK展開は一時領域quotaで失敗したため、実build・全テスト・Docker E2E・監査の結果はGitHub Actionsで確認する。結果と実行URLは確認後に追記する。

## 参照

- [pdfcpu v0.16.3](https://github.com/pdfcpu/pdfcpu/releases/tag/v0.16.3)
- [Goの該当脆弱性](https://pkg.go.dev/vuln/GO-2026-6809)
- [Go SDKの公式checksum](https://go.dev/dl/?mode=json&include=all)
- [pdfcpu moduleのchecksum](https://sum.golang.org/lookup/github.com/pdfcpu/pdfcpu@v0.16.3)
