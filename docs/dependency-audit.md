# 依存監査

## 実行と判定

- CIのPR・main pushと定期監査は同じ `scripts/nuget-audit.sh` を使う。推移的依存も対象にし、High/Critical（NU1903/NU1904）、監査データ取得失敗（NU1900/NU1905）、restore失敗で失敗する。Low/Moderateは警告。
- `Dependency audit` のNuGetは毎日04:17 JST、外部依存は毎週月曜04:43 JST。GitHubの都合で遅延する場合がある。mainへマージしてからscheduleが有効になる。ActionsのRun workflowでは両方を手動実行できる。定期監査は全API回帰テストを実行しない。
- 外部依存はDockerfileから `--pull` で検証用イメージを作る。Trivy v0.76.1のarchiveをSHA-256で検証して実行し、その時点のDBでOSとGo binaryを照合する。High/Criticalは修正版の有無を問わず失敗。OS/Goの期待するパッケージ・版の検出漏れ（OSパッケージの空・欠落した版も含む）、ツール・DB・PDFium更新情報の取得失敗も成功扱いしない。
- Actions summaryに結果、artifactに全重大度のJSON、パッケージ一覧、image ID、DB/ツール情報、PDFium更新情報を14日保存する。通知は既存のGitHub Actions通知設定を利用し、独自のメールやSlack送信はしない。

## 検査範囲と限界

| 対象 | 自動検査 | 更新時の入口 |
|---|---|---|
| qpdf / libqpdf、libjpeg-turbo | runtime UbuntuのOSパッケージと配布元の脆弱性情報を照合 | Dockerfileのapt、ベースイメージを更新してbuild・回帰テスト |
| pdfcpu、内包Go runtime・module | binaryのメタデータからGo依存を照合。固定inventoryの全Linux moduleが検出されたかも確認 | `third_party/pdfcpu.lock`、module inventory、ライセンス、既存の更新検証 |
| PDFiumと内包するFreeType/ICU等 | **CVE自動照合の対象外**。採用版・SHA・配布元の最新releaseとの差分を記録 | `third_party/pdfium.lock`、ライセンス、renderer/redact検証 |
| NuGet | restoreで推移的依存を含むNuGet監査 | csproj更新、Release build・test |

OSの識別可能なUbuntu版とサポート状態を確認し、対象OSパッケージの版は固定値と比較せず、空・欠落・不正な値を失敗にする。Trivyが省略する `EOSL=false` は正常として扱う。

ベースイメージ内の `/usr/bin/pebble` は、ネットワークなし・read-onlyのコンテナでスキャナーとは独立に存在確認し、`native-presence.json` に保存する。存在する場合はGo binary検査結果とGo runtimeの版が必須。実際に削除された場合は要求しない。存在確認自体の失敗・情報欠落も成功扱いしない。これは既知の追加binaryへの確認であり、あらゆる未知のGo binaryの網羅を保証するものではない。

TrivyのGo検査はbinary内のメタデータに依存するため、inventory不一致は要調査。検出ゼロは既知の検査対象についての結果であり、安全性の証明ではない。特にPDFiumの「最新版と一致」は脆弱性なしを意味しない。配布元の新releaseも脆弱性そのものではないので、その差だけでCIを落とさない。

PDFiumは[配布release](https://github.com/bblanchon/pdfium-binaries/releases)、[PDFium upstream](https://pdfium.googlesource.com/pdfium/)、[Chrome security release情報](https://chromereleases.googleblog.com/search/label/Stable%20updates)のPDFium関連修正を人が確認し、内包ライブラリを含めて対応を判断する。Chromiumとの版の対応や非公開の脆弱性情報には限界があり、ChromeのCVEを機械的にこのbinaryへ適用しない。

失敗時はsummaryとartifactで「脆弱性」「検査対象の欠落」「取得障害」を切り分ける。一時障害は再実行、脆弱性は更新PRで修正する。監査を通すために重大度を下げたり未修正の脆弱性を除外しない。自動更新・自動merge・image公開・本番反映はしない。

Trivyの範囲は[Go binary検査](https://trivy.dev/docs/latest/guide/coverage/language/golang/)と[OS検査](https://trivy.dev/docs/latest/guide/coverage/os/)を参照。scanner自身の版・SHA更新もPRでレビューする。
