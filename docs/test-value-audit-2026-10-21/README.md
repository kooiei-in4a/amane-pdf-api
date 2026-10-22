# テスト価値監査台帳（2026-10-21 UTC）

対象はcommit `465088ed0b575f055beb6851e98d28ed22fb9928`。全MSTest 452メソッドの保護対象を読み、本番の入口・共通helper・processor・外部ツールへの経路と照合した。改善候補は**13項目、16メソッド**。特に4メソッドでは、入力検証を取り除いても既存テストが成功することを隔離コピーで実証した。

**更新**: 13項目すべて対応済み。現在は56ファイル・58クラス・452メソッド・1340ケース。共通取消の重複13行を整理し、1メソッドを統合、binary回帰を1件追加した。レビュー前のRelease一括実行は1338成功・容量2件skipで完走し、容量2件も上限fixtureで別途成功。[レビュー前の改善・実行証拠](evidence/improvements/README.md)を参照。初回監査とLV01/LV02の実験は履歴として保持する。

**レビュー後の更新**: 500entryの検証を本番`WriteZipAsync`の直接呼出へ修正し、raw後始末違反を専用assertで報告する。変異runnerは全失敗行のassert・件数を照合し、元のテスト全62 C#ファイルのhashを確認する。14変異と正常対照は完了。ローカルは3群で全件1338成功・2skip、コード変更コミットのCIは一括1338成功・2skipとDocker検証が成功した。ローカル一括のtesthost crashは履歴に残す。[最新の再検証](evidence/review-fixes/README.md)を参照。下記の各段階の結果は実行当時の履歴であり、最新版と混同しない。

## 台帳の入口

| 資料 | 内容 |
| --- | --- |
| [低価値候補と改善条件](low-value.md) / [CSV](low-value.csv) | 優先度、実証と推定の区別、重複先、残す保証、変更の完了条件。最初に読む一覧。 |
| [全MSTest台帳](tests.md) / [CSV](tests.csv) | 452メソッドそれぞれの保護対象、定義位置、本番参照、検証層、判定根拠、重複・差異、実行数・時間、推奨判断。 |
| [Python・手動検証台帳](scripts.md) / [ファイルCSV](scripts.csv) / [検証scope CSV](script-checks.csv) | 38本と126個のassert-bearing関数/トップレベルscope。CI、任意測定、fixture生成、歴史的probeを区別。 |
| [共通helper・fixture・設定](support.md) | 6つのC#補助ファイル、18固定fixture、installer、計測補助、probe、実行設定。 |
| [再現・実行証拠](evidence/README.md) / [検証結果JSON](evidence/verification.json) | baseline、4メソッドへの変異実験、正常実装での対照、再現用差分、実行上の限界。 |
| [対象ファイル一覧](files.csv) | 対象commit内の132ファイルの役割、サイズ、SHA-256。ソースやfixtureの同定用。 |

## 初回監査の範囲と数え方

| 対象 | 確認した範囲 |
| --- | --- |
| MSTest | `tests/`の全55テストファイル、57 TestClass、452 TestMethod。RedactTests.csに3クラスある。DataRow/DynamicDataを展開した実行結果は1,353ケース。 |
| C#共通補助 | HTTP context、process blocker、PDF/PNG/overlay fixture等の6ファイル。本文と合わせ61 C#ファイル。 |
| Python | `scripts/`25本と`docs/*-design-probe/`13本。8件のunittestは測定driver用。126 scopeはassert/helper呼出の所在数で、実行ケース数ではない。 |
| その他 | 固定fixture18点、shell5本、C計測補助1本、設計probe C#2本、project/runsettings/CI、Python依存lock、probeの既存結果。 |
| 本番照合 | Programのroute/permit、PdfEndpointHelpersの検証順序・期限・取消・後始末、multipart、各processor/builder、制限付き外部プロセス、PDF/画像の解析と出力。対応先を各台帳に記載。 |

初回監査の対象はこのcommitの追跡ファイル。source行番号は修正後へ更新し、CSVのbaseline_*は初回位置・件数・時間を保持。files.csvのsha256/bytesは初回、current_sha256/current_bytesは更新後を表す。別worktree、bin/obj、第三者ライブラリ内部のテストは含めない。fixtureは消費側・構造・生成条件を確認したもので、すべてを人の目で画像比較したという意味ではない。新規の監査文書や証拠差分はbaselineの132ファイルに数えない。

## 判断基準

各テストについて、失敗させたい退行、実際に観測するassert、到達する本番の層、入力を作るhelper、他のテストとの差を確認した。テスト名やassert数だけで価値を決めていない。静的な対応表はcoverage計測結果ではなく、全452メソッドに変異を適用したわけでもない。

低価値候補の優先順位は次のとおり。

以下は初回監査での優先順位。現在は全項目対応済み。

1. **LV01・LV02（対応済み）**: 別の不正条件が同じ400を返して検証の削除を隠す。計4メソッドで既存テストの生存と強化後の検出を実証。
2. **LV03・LV07・LV10・LV11**: 未実施なのにPassedとなる容量測定、弱い色空間判定、観測していないfallback回数、binaryを文字列化する比較helper。主張と証拠を合わせる。
3. **LV04〜06・LV08〜09・LV12〜13**: 同じ取消token経路の反復、重い起動を伴う単体確認、色行列の重複、実装順への依存、受付外の防御的枝、ZIP名確認に対する500分割の費用。固有の保証を保持して統合・移管する。

「維持」は削除・縮小を勧める具体的根拠が見つからなかったという意味。任意の退行を検出できる証明ではない。「低価値候補」もメソッド全体が無意味という意味ではない。**丸ごとの削除だけを推奨する項目は0件**。

## 重複に見えて残すべき境界

| 組み合わせ | 両方が必要な理由 |
| --- | --- |
| parser/容量算術の単体とHTTP | 単体は詳細な文法・overflow・丸めを守り、HTTPはrouteがその検証を呼び、正しいstatus/reasonになることを守る。LV01はこの違いを実証した例。 |
| ExternalProcessRunnerと各APIのresource tests | 低層の子孫停止と、request/timeout/application stopから実stageへの伝播・permit返却は別の契約。各stageがあるUnlock/Merge等を共通テストだけに集約しない。 |
| .NETの構造比較とPythonの独立描画 | PdfPageBoxes等を期待値側にも使う比較には相関がある。Poppler/MuPDF/Pillow/固定座標は同じバグで期待値も変わる問題を補う。 |
| Redactの危険共有とneutral共有 | 秘密参照の拒否、対象page拡張、限定allowlist、別roleからの再到達は別の安全境界。単純な正常/異常PDFの2ケースへ縮めない。 |
| 容量単体・real prlimit・Docker | 4KiB会計、exec/signal/FSIZE/ASの実効性、cgroup/tmpfsと同時負荷は別の制約。単一の成功テストでは代替できない。 |
| 起動自己テストとHTTP要求 | 起動前の依存拒否と、起動後のtool障害を固定応答にする経路は別。30秒共通deadlineの実時間テストは長いが固有の退行を守る。 |

## 本番経路と異なる点の扱い

- `PdfTestContext`は実ProgramをWebApplicationFactoryで起動する。HTTP pipelineを通るが実Kestrel socket/cgroupではない。Qpdf/Djpeg/Cjpeg/timeoutの一部設定は自己テスト成功後に差し替える設計で、要求処理中の障害注入として有効。起動設定の妥当性を確認したと解釈しない。
- builder/renderer直接呼出では、テスト所有のCTSやTemporaryPdfFiles.Disposeを利用する。これをHTTPの504変換、ApplicationStoppingの接続、endpoint自身の削除保証と数えない。
- 人工JSON/壊れたPNGは特定の構造分岐を狙う。受付可能なPDFの保証は実PDF/HTTP側を確認する。特にLV12のalias構造は共通validatorで拒否されることがテスト内にも明記されている。
- PNG fixtureのCRC生成には本番PngSanitizer.UpdateCrcを使う相関がある。StampFixturesはpacked depth/Adam7を生成する一方、FromImagesFixturesの構造用ケースをすべてdecoder用の有効画像と扱わない。独立zlib/Pillow/実pdfcpuの層を残す。
- 一部のLinux専用テストは非Linuxで単にreturnし、他はAssert.Inconclusiveを使う。今回はLinuxで実行したため、そのplatform gateを未実行理由に数えていない。通常実行で空振りだったのは別途明記したFillTemplateCapacityの2メソッド。
- 古いprobeは設計実験として価値を残すが現行回帰suiteには数えない。`gen_geometry.py`は現在のCIでも使うため、probeディレクトリごと削除する判断は誤り。
- 手動測定のPASSは契約を読む必要がある。split-validation.pyは200/422/504を許容して資源を測る。decoder閾値探索は失敗域も記録する。これらを「すべての入力が正常処理された」証拠にしない。

## 初回監査の実行結果と限界

| 実行 | 結果 |
| --- | --- |
| Release build | 成功、0 error。NuGet脆弱性metadataへ到達できずNU1900が2件。 |
| .NET全件 | **1,353 Passed / 0 Failed / 0 Skipped、5分39秒**。そのうち容量測定2件は条件未指定の早期returnで、上限容量を測っていない。 |
| Python測定driver | **8件成功**。 |
| JPEG必須optionの実変換 | 成功。 |
| query検証削除の変異 | 既存3件成功 → 正常bodyへ強化すると3件失敗 → 正常本番コードでは3件成功。 |
| 未知multipart field許容の変異 | 既存1件成功 → 正常fieldsへ強化すると1件失敗 → 正常本番コードでは1件成功。 |
| Docker CI全体・手動上限/混在測定 | ソースと呼出関係を確認。**今回は再実行していない**。 |

依存toolは既存Docker imageからpdfcpu/font/PDFiumを取り出し、API・renderer・test assemblyは対象sourceからビルドした。host実行は本番DockerのCPU1・メモリ1.5GiB・tmpfs256MiBを再現していない。詳細なtool版・hash・実行時刻・変異結果は[検証結果JSON](evidence/verification.json)に固定した。

実時間は今回1回の観測であり性能傾向の保証ではない。候補の時間を合計してCI短縮時間と見積もらない。改善を実装する際は対象の強化/統合を行い、残す保証が検出できることを確認してから台帳の状態を更新する。

## LV01/LV02修正時点の確認（履歴）

4メソッドを正常入力と不正条件の独立した組合せへ修正し、queryはサーバー側本文読取0回も確認。正常対照は変異実験の前後とも4/4成功。7変異は全て検出（個別6実行と、順序逆転の4メソッド実行で計10 Failed）。Releaseは3群に分け、重複なく全1,353件が成功。一括実行はhost crashが2回発生し原因未確定。容量2件の条件未指定は初回と同じ。Docker検証は再実行していない。詳細は[修正証拠](evidence/validation-fix/README.md)。

## 残り11項目の改善結果

取消の重複を18→6・2→1へ整理し、API起動を伴う事前取消・防御的metadata・500分割を軽い層へ移管。色空間/ICC・実描画色と文字・fallback回数/途中cleanup・raw byte保存のassertを強化した。追加の6変異を全て検出し、正常対照19ケースは実験前後とも成功。容量2件も50MiB/1000頁/200field/1000行で有効化して成功。

Release全件は一括で**1338成功・2スキップ・失敗0（5分32秒）**。前回のhost crashは今回は再現せず、観測peakはRSS約1018MiB・FD486・35threadsだった。原因が判明したという主張はせず、過去の中断は履歴に残す。Docker全体は今回再実行していない。[改善証拠](evidence/improvements/README.md)にsource/patch hash・TRX集計・capacity実測・host推移を保存。
