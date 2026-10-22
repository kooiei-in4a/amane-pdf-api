# LV01/LV02の修正と変異検証

初回監査で生存した入力検証の変異に対し、対象4メソッドと共通helperを修正した。**この資料はLV01/LV02修正時点のsnapshot**。その後の11項目の改善と現在の全件結果は[最新の記録](../improvements/README.md)を参照。

初回監査は[元の記録](../README.md)に保ち、ここに修正後の結果を記録する。本番のsrcは変更していない。

**runnerの更新**: 記録当時は終了コードとoutcomeの確認までだった。現在の[run-mutations.py](run-mutations.py)は変異ごとのassert/expected/actualも照合し、元の全テストC#ファイルのhashを確認する。[判定テスト](test_mutation_runner.py)と[最新の14変異の再検証](../review-fixes/README.md)を追加した。ここのresults/verification.jsonは当時の実行記録のまま保持する。

## 修正内容

| メソッド | 実装した保証 |
| --- | --- |
| PdfPageNumbersTests.InvalidQueries_Return400BeforeReadingUploadOrRunningQpdf | 正常PDF multipartに不正queryだけを付けて400を確認。別のTestServer要求で本文読取0回と400も確認。 |
| PdfWatermarkTests.InvalidQueryPrecedesUploadAndTools | 正常PDF/text multipartでquery拒否を独立させ、本文読取0回を確認。 |
| PdfStampImageTests.InvalidQueryPrecedesUploadAndTools | 正常PDF/PNG multipartでquery拒否を独立させ、本文読取0回を確認。 |
| PdfFillTemplateTests.Http_StrictFieldsAndMultipart_AreCheckedAfterUploadBeforeTools | multipart各kindのfieldsを正常化。不正kindをassert messageへ追加。query拒否も正常Formと本文未読probeへ変更。不正fieldsの既存検証は維持。 |

本文未読probeはPdfTestContext.AssertRejectedBeforeReadingBodyAsyncで共通化。clientのmultipart直列化回数と混同せず、TestServerの実request pipelineに読取時カウント・例外を出すstreamを渡す。先に本文を読む退行では、同じ400が返る経路が残ってもread回数で失敗する。

既存4メソッドを維持しており、メソッド数452・展開数1353は変わらない。CSVのbaseline_line/case_secondsと初回files hashは履歴として保持し、修正後のsource位置・latest_*結果・current_* hashを併記した。

## 変異と正常対照

7変異は独立に一つずつ隔離コピーへ適用し、毎回Rebuildした。compile失敗や空のtest discoveryを検出成功に数えず、指定したメソッド自身がFailedになることをrunnerで要求する。実験の前後で正常実装へ戻し、4メソッドが全て成功することも確認した。

| 本番変異 | 対象メソッド | 結果と検出点 |
| --- | --- | --- |
| PageNumbersのquery Parseを既定値へ置換 | PageNumbersの1件 | Failed、期待400/実際500。 |
| Watermarkのquery Parseを既定値へ置換 | Watermarkの1件 | Failed、期待400/実際500。 |
| StampImageのquery Parseを既定値へ置換 | StampImageの1件 | Failed、期待400/実際500。 |
| ReadFileAndTextAsyncで未知のtextだけ許容 | FillTemplateの1件 | Failed、unknownケースが期待400/実際500。 |
| FillTemplateのquery拒否を削除 | FillTemplateの1件 | Failed、期待400/実際500。 |
| FillTemplateで受信fieldsを解析せず固定の正常fieldsへ置換 | FillTemplateの1件 | Failed、期待400/実際500。 |
| 共通helperのquery検証をupload読取後へ移動 | 4件すべて | 全てFailed、本文読取回数が期待0/実際1。通常の有効multipart要求は400のままなので、順序の保証を独立に検出する。 |
| 正常対照（実験前） | 4件 | 全てPassed。 |
| 正常対照（実験後） | 4件 | 全てPassed。 |

**7変異を全て検出、survived=0**。対象メソッドの失敗実行は計10件（個別6件＋順序変更4件）。1353ケース全体のmutation scoreではなく、すべての個別query文法やmultipart kindの内部guardを一つずつ取り除いたという意味でもない。今回の目的は、既知の4メソッドの見逃しと、その修正が主張する入力/順序の保証を確かめること。

500は、受理してはいけない要求がテスト用の実行禁止tool pathまで進むことで返る固定内部エラー。テストの400期待から外れたことが検出根拠である。意図した変異失敗を正常実装の回帰失敗として集計しない。

## 実行結果

- Release build成功、0 error。NuGet脆弱性metadataを取得できないNU1900 warningが2件。
- 正常実装のReleaseは、57クラスを重複のない3群に分けて順次実行し、全1353ケースがPassed / 0 Failed / 0 Skipped。メソッドごとのケース数が初回監査と一致し、抜けと重複がないことも確認。初回同様、条件未指定のFillTemplate容量2件は早期returnで上限未測定。
- 通常の一括実行は2回中断した。1回目は変異実験と同時実行中に1236 Passed / 0 Failed、2回目は単独実行でも1306 Passed / 0 Failedの時点でhost crash。原因は未確定。2回目に実行中だった既存の1000ページテストは単独で成功した。中断した実行を全件成功の根拠には使わず、3群の結果を全件台帳と照合した。一括実行が最後まで完了しない問題は未解決として記録する。
- Docker suiteは今回再実行していない。

[results.json](results.json)に変異/対照ごとの時刻、結果、失敗の先頭行とexpected/actual、TRX hash、API assembly hash、固定テストhashを保存。[verification.json](verification.json)にRelease全件と修正差分のhashを保存した。生のTRX/logは実行時output directoryに残し、本文/表示引数を台帳へ転載しない。

## 再現

依存toolとPdf__*設定を初回と同じように用意する。対象commit `465088ed0b575f055beb6851e98d28ed22fb9928`の使い捨てcheckoutに[tests.patch](tests.patch)を適用してRelease buildする。このpatchは4テストファイルとPdfTestContextだけを変更する。

```bash
# AUDIT_FIXは、このevidence/validation-fix directoryの絶対path。
git apply "$AUDIT_FIX/tests.patch"
dotnet build --configuration Release -m:1 --disable-build-servers
python3 "$AUDIT_FIX/run-mutations.py" --source . --output /tmp/pdf-validation-mutations-repro
```

[run-mutations.py](run-mutations.py)はsourceを一時directoryへコピーし、productionの変異をそのコピーだけへ適用する。sourceのRelease build出力/objを使うため事前buildが必要。実験前後の正常対照・各変異でRebuildするので、mtimeや古いDLLの再利用で緑になることを防ぐ。実験用コピーは終了時に削除し、patch・TRX・log・results.jsonはoutputへ残す。

各本番変異は同directoryの.patchにも保存した。単独再現は使い捨てcheckoutへtests.patchと対象変異patchを適用し、`dotnet build -c Release --no-restore -t:Rebuild -m:1`後に該当メソッドで`dotnet test -c Release --no-build --filter ...`する。元worktreeには本番変異を書き込まない。

## 分割したRelease全件の再現

[batches.json](batches.json)は初回のcase秒を基に均等化した57クラスの割当。各classが一群だけに属することと、452メソッドの展開数の合計1353が初回と一致することを照合した。実行は各群に含まれるclassのfilterをORで結ぶ。

```bash
# 全3群を順に実行する。
python3 - "$AUDIT_FIX/batches.json" <<'PYCODE'
import json, subprocess, sys
for i, group in enumerate(json.load(open(sys.argv[1])), 1):
    selected = '|'.join('FullyQualifiedName~Amane.Pdf.Api.Tests.' + c + '.' for c in group['classes'])
    subprocess.run(['dotnet', 'test', '--configuration', 'Release', '--no-build', '-m:1',
                    '--filter', selected, '--logger', f'trx;LogFileName=batch-{i}.trx',
                    '--results-directory', '/tmp/pdf-validation-release-batches'], check=True)
PYCODE
```
