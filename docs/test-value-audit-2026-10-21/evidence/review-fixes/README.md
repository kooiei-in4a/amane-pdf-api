# PRレビュー指摘の修正と再検証

PR #85のレビューで、500entryのZIP検証に本番の書込ループを通らない保証の欠落が見つかった。あわせて、fallbackの失敗原因の表示と変異runnerの判定を改善した。本番srcは変更していない。

## 修正した保証

| 指摘 | 修正 | 検証する退行 |
| --- | --- | --- |
| 500entryのZIPループがテスト内の再実装 | `PdfSplitProcessor.WriteZipAsync`を直接呼ぶ。qpdfの抽出・検査だけ固定PDFを返すshellに置換。 | 本番entryの名前・付加情報・圧縮・容量判定を通し、500件の名前、各PDFのbyte一致、UTF8 flag、overhead上限、途中part削除を確認。entryごとの2200byteコメント追加変異を検出する。 |
| raw後始末違反が再圧縮数4→0として現れる | wrapperは違反markerを残して実qpdfを続行。件数headerより先にmarker不在をassert。 | `Raw extraction directory from a previous attempt still exists.`という原因を示すassertで失敗する。batchの実partial raw・1batch+4個別処理の確認は保持。 |
| runnerがFailedだけで検出成功とする | 変異ごとに行数・失敗数・assert/expected/actualまたは専用messageを照合。全失敗行が条件を満たすことを要求。source文字列の置換による判定緩和を除去。 | build失敗、crash、空の発見、余計な失敗、別assertでの失敗を拒否。正常対照は4件／20件すべて成功を要求。 |
| 実験元sourceのhash確認が5ファイルのみ | 両runnerともテスト直下の全62 C#ファイルを記録し、実験後に元sourceの不変を確認。 | 今回の対象テストとhelperを含む。変異は一時コピーだけへ書き、毎回Rebuildする。 |

ZIPテストはHTTPの受付や500頁の実抽出を検証するものではない。HTTP header→ZIPの既存20行列と、実qpdfを使う既存Splitテストが別に残る。旧テスト内のZIPループでは、部品は本物でも本番の組み立てを保証できていなかった。その約0.010秒という観測を現在のテストの速度として扱わない。

## 実行結果

実行結果とsource/runner/patch hashは[verification.json](verification.json)に保存した。入力検証の実行明細は[results-validation.json](results-validation.json)、改善側は[results-improvements.json](results-improvements.json)。

| 実行 | 結果 |
| --- | --- |
| Release build | 成功、0 error、NU1900 warning 1件。 |
| 修正した2テスト | 2成功。500entryは約6.755秒（1回のhost観測で、CI短縮時間の保証ではない）。 |
| runner判定テスト | 再レビュー後は9成功。前回の7件に最新記録・parameter識別の確認を追加。 |
| 入力検証7変異 | 7/7で期待assertと失敗件数が一致。意図した失敗10件、前後の正常対照は各4成功。 |
| 改善側7変異 | 7/7で期待assertと失敗件数が一致。意図した失敗19件、影響しない行9成功、前後の正常対照は各20成功。 |
| Release一括 | **1303成功・2skipの時点でtesthost crash、残り35ケース未完了**。assert失敗0だが全件完走ではない。 |
| Release全件・分割再実行 | **3群合計1338成功・2skip・失敗0**。第1群500成功、第2群397成功・2skip、第3群441成功。58クラスを重複なく割り当て、全452メソッド・1340ケースが揃うことを確認。 |
| コード変更コミットのCI | **build-test／dockerとも成功**。CI一括は1338成功・2skip・失敗0（4分32秒）。Dockerの上限fixtureは単独・2並列とも成功し、残存job/process・OOMは0。 |

分割再実行の最初の2回は、自動承認処理のBad Requestで実行前に停止した。安全性の否認ではなく承認システムのエラーで、環境更新後に再試行し3群とも完了した。[batches.json](batches.json)は58クラスの割当、verification.jsonのfull_suite.runsは各実行の時刻・件数・TRX hash。分割成功を一括完走とは表現しない。

改善側の最初の実行も、既存6変異後のZIP変異のbuild中に終了コード143で中断した。この実行は完了結果に数えず、正常対照を含む7変異を新しい出力先で再実行し完了した。本番変異のAPI DLL hashは正常対照から変化し、実験後は正常対照のhashへ戻ることも確認した。binary helper変異はテスト側の変異なのでAPI DLLは変わらない。

testhost crashは以前にも発生しており、原因未確定のまま記録する。tests.csvは今回の3群による全件結果で更新した。CIは[コード変更コミットb195587の実行](https://github.com/kooiei-in4a/amane-pdf-api/actions/runs/39865598926)で一括完走しており、[ci.json](ci.json)にjob ID・件数・容量結果を記録。全テストC#ファイルのhashが現在と一致することも照合した。この後の台帳更新はテストと本番のsourceを変更していない。最新headの状態は[PR #85](https://github.com/kooiei-in4a/amane-pdf-api/pull/85)のChecksを参照。

入力検証7変異と改善側の既存6変異の差分は、[validation-fix](../validation-fix/README.md)と[improvements](../improvements/README.md)のpatchと同じ。追加のZIP変異は[split-entry-comment-inflated.patch](split-entry-comment-inflated.patch)。Failed件数だけでなく、各結果の`expected_failure`と`matched_assertions`を確認する。正常対照は各群の実験前後に実行する。

[runner判定テスト](../validation-fix/test_mutation_runner.py)の元の7メソッドは、記録済みのassert照合、旧fallback判定の拒否、環境由来の失敗の拒否、一部の正しい失敗による別失敗の隠蔽の拒否、影響を受けない行の維持、未検出/空発見/中断/終了コード/別assertの拒否、正常対照での失敗の拒否を守る。監査用に追加した検証で、初回対象のPython38本には含めない。

以前の実行結果・hash・tests.patchは各段階の履歴として保持する。特にimprovementsの旧raw変異は、実際にはheaderの期待4/実際0で失敗した記録であり、現在の専用cleanup assertによる結果と区別する。

## 再レビューで残った軽微な2点への対応

判定テストを9メソッドへ増やし、最新のresults-validation/results-improvements.jsonの**全14変異と前後4回の正常対照**を現在の判定関数へ渡すsubTestを追加した。変異名が各7種すべて揃うことと、正常対照がbefore/after各1件であることも確認する。CIのbuild-testにも`Verify mutation verdict runner`を追加し、この9件を実行する。

runnerはTRXの`testName`を`case_name`として保持する。overlay変異では6stageの名前と成否を一対一で照合し、**finalだけPassed、input/blank/draw/overlay/restoreはFailed**を要求する。件数と失敗messageが同じでも、成功行のstageを入れ替えた場合、ケース名が欠落・重複した場合は拒否する。2つ目の追加テストはこの3種の誤判定を防ぐ。

最新の14変異と4正常対照には、記録済みSHA-256と一致する元TRXからcase_nameを追記した。追記前のoutcome・時刻・失敗内容・assembly hashはすべて元TRXと一致することを確認し、変更していない。この14変異を今回すべて再実行したという意味ではない。古いvalidation-fix/improvementsの記録はparameter名を持たない履歴として保持し、従来のassert照合を続ける。

さらにoverlayだけは新しいrunnerで隔離変異を再実行し、前後の正常対照各6成功、変異は期待したassertで5失敗・finalだけ1成功を確認した。[新しい結果](results-overlay-case-check.json)にはexpected_case_outcomesとmatched_case_outcomesを保存している。実験は改善runnerの対象をoverlay1変異・1メソッド・正常対照6ケースへ絞ったもので、本番srcとC#テストは変更していない。Release buildも0 error・NU1900 warning 1件で成功した。

verification.jsonのrunner hashは現在のファイルを表す。従来の14変異を実行したrunnerのhashはrecorded_mutations_*へ、前回7件の判定結果はrunner_unit_tests_before_rereviewへ保持し、追記・再照合と実験の再実行を区別する。

## 再現

Linux x86_64、global.jsonの.NET SDK、Python3、qpdf 12以降、libjpeg-turbo-progs、util-linux、curlが必要。過去のローカルDocker imageを使わずに依存を揃えるには、CIと同じlock検証付きinstallerを使う。以下は準備手順であり、今回installerの新規downloadは再実行していない。今回は初回監査で抽出済みの依存を再利用した。

```bash
AUDIT_DEPS=/tmp/pdf-review-deps
scripts/install-pdfcpu.sh "$AUDIT_DEPS/pdfcpu"
scripts/install-pdfium.sh "$AUDIT_DEPS/pdfium"
mkdir -p "$AUDIT_DEPS/qpdf-bin"
cp /usr/bin/qpdf "$AUDIT_DEPS/qpdf-bin/qpdf"
export PATH="$AUDIT_DEPS/qpdf-bin:$PATH"
export Pdf__PdfcpuPath="$AUDIT_DEPS/pdfcpu/bin/pdfcpu"
export Pdf__PdfcpuConfigDir="$AUDIT_DEPS/pdfcpu/config"
export Pdf__RenderFontDir="$AUDIT_DEPS/pdfcpu/fonts"
export Pdf__PdfiumPath="$AUDIT_DEPS/pdfium/lib/libpdfium.so"
export Pdf__DotnetPath="$(command -v dotnet)"
export Pdf__RendererPath="$PWD/src/Amane.Pdf.Renderer/bin/Release/net10.0/Amane.Pdf.Renderer.dll"
```

installerのprefixは空または未作成であること。qpdfのコピーはUbuntuのAppArmorによる拡張子制約を避けるため。コンテナで実行する場合は`--init`等で孤児processを回収できる構成にし、元sourceとコピーのRebuildに必要なSDK・tool・pathも揃える。

現在のPR checkoutで以下を順番に実行する。過去のcommit `ab0eae6`から再現する場合は、先に[tests.patch](tests.patch)を適用し、runnerはこの修正後のevidence directoryから実行する。出力directoryは実行ごとに新しい場所を使う。

```bash
dotnet build --configuration Release -m:1 --disable-build-servers
dotnet test --configuration Release --no-build -m:1
python3 docs/test-value-audit-2026-10-21/evidence/validation-fix/test_mutation_runner.py
python3 docs/test-value-audit-2026-10-21/evidence/validation-fix/run-mutations.py \
  --source . --output /tmp/pdf-review-validation-repro
python3 docs/test-value-audit-2026-10-21/evidence/improvements/run-mutations.py \
  --source . --output /tmp/pdf-review-improvements-repro
```

正常対照・変異・全件テストは同時実行しない。今回の検証はhost上で、Docker全体の再実行やcgroup/tmpfs制約の再現ではない。容量fixtureを有効化する2件はこの修正では再実行せず、前段の結果と区別する。
