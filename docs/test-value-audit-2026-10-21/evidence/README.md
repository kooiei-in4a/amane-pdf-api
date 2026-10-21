# 初回監査の実行証拠と限定変異の再現

**最新の改善結果**は[improvements/README.md](improvements/README.md)。

**更新**: 以下は修正前の監査実験の履歴。現在のLV01/LV02修正・7変異・全件確認は[validation-fix/README.md](validation-fix/README.md)を参照。

対象commitは`465088ed0b575f055beb6851e98d28ed22fb9928`。2026-10-21 UTCにLinux上で実行した。集計とTRXのSHA-256を[verification.json](verification.json)に保存した。TRXの各methodの展開数と時間は[tests.csv](../tests.csv)に対応する。1353ケースの表示引数やPDFの本文を台帳へ転載していない。

## baseline

SDK 10.0.402 / runtime 10.0.13、qpdf 12.3.2、libjpeg-turbo 2.1.5、pdfcpu 0.16.2。既存Docker image `amane-pdf-api:site134-465088e`（image ID先頭 `67d204ed4e10`）からpdfcpu/config/font/PDFiumだけを抽出した。API・renderer・test assemblyは対象commitをローカルでRelease buildしている。このimageが現在の公開本番imageと同一だという確認はしていない。

必要な`Pdf__PdfcpuPath`、`Pdf__PdfcpuConfigDir`、`Pdf__RenderFontDir`、`Pdf__PdfiumPath`、`Pdf__DotnetPath`、`Pdf__RendererPath`を設定し、次を実行した。

```bash
dotnet build --configuration Release -m:1 --disable-build-servers -v:normal
dotnet test --configuration Release --no-build -m:1 \
  --logger 'trx;LogFileName=baseline.trx' --results-directory /tmp/pdf-api-audit-results
/usr/bin/python3 scripts/from-images-followup-test.py
/usr/bin/python3 scripts/compress-validation.py --tools
```

Ubuntuのqpdf AppArmor profileによる出力path制約を避けるため、CIと同様に`/usr/bin/qpdf`を一時directoryへコピーしてPATHの先頭に置いた。VSTestのsocket・子processを動かすためsandbox外実行を使用した。初回sandbox内の起動失敗を製品テストの失敗として数えていない。

Release buildは0 error、NU1900 warning 2件。NuGetの脆弱性metadata取得は完了していない。.NETは1353成功、Pythonは8成功、JPEG実変換成功。Docker CI全体・手動容量測定は今回の成功結果に含めない。

## 変異結果

| 対象 | 本番変異と既存テスト | 本番変異と強化テスト | 正常本番と強化テスト |
| --- | --- | --- | --- |
| LV01: query 3メソッド | 3成功（変異を見逃す） | 3失敗（期待400、実際500） | 3成功 |
| LV02: multipart 1メソッド | 1成功（変異を見逃す） | 1失敗（期待400、実際500） | 1成功 |

失敗は検出力を確認するための意図した結果で、baselineの失敗ではない。対象テストは「実行してはいけないtool path」を指定しているため、validatorが欠けて処理が先に進むと500になる。200を返す必要はなく、期待400から外れることが変異検出になる。

これは4メソッドへの限定実験。全suiteのmutation scoreや他ケースの健全性を表す値ではない。LV02はunknown=textの拒否を意図的に緩めた1枝であり、全multipart kindをそれぞれ変異評価したわけではない。

差分は隔離コピーへだけ適用した。初回実験時点では監査worktreeのsrc/testsも対象commitと同じだった。現在はtestsの4メソッドと共通helperを修正済み。以下のpatchは**実験再現用で、そのままの採用案ではない**。例えばbody未読の順序確認には、さらに読取カウンタ等のassertが必要である。

## LV01の再現

依存を用意した対象commitの使い捨てcheckoutで、証拠directoryの絶対pathを`AUDIT_EVIDENCE`へ設定する。各状態を変更するたびにRebuildし、古いDLLを`--no-build`で誤って再利用しない。

```bash
git apply "$AUDIT_EVIDENCE/query-validation-removed.patch"
dotnet build -c Release --no-restore -t:Rebuild -m:1
dotnet test -c Release --no-build --filter \
  'FullyQualifiedName~InvalidQueries_Return400BeforeReadingUploadOrRunningQpdf|FullyQualifiedName~InvalidQueryPrecedesUploadAndTools'
# 3件成功: query Parseの削除を既存テストが見逃す。

git apply "$AUDIT_EVIDENCE/query-valid-body.patch"
dotnet build -c Release --no-restore -t:Rebuild -m:1
dotnet test -c Release --no-build --filter \
  'FullyQualifiedName~InvalidQueries_Return400BeforeReadingUploadOrRunningQpdf|FullyQualifiedName~InvalidQueryPrecedesUploadAndTools'
# 3件失敗: 無関係なbody不正を除くと変異を検出。

git apply -R "$AUDIT_EVIDENCE/query-validation-removed.patch"
dotnet build -c Release --no-restore -t:Rebuild -m:1
dotnet test -c Release --no-build --filter \
  'FullyQualifiedName~InvalidQueries_Return400BeforeReadingUploadOrRunningQpdf|FullyQualifiedName~InvalidQueryPrecedesUploadAndTools'
# 3件成功: 強化テストは正常実装で通る。
```

差分: [本番変異](query-validation-removed.patch)、[テスト側の正常body](query-valid-body.patch)。query検証だけでなく順序を守る改善案は[LV01](../low-value.md#lv01)を参照。

## LV02の再現

別の使い捨てcheckoutで同様に実行する。

```bash
git apply "$AUDIT_EVIDENCE/multipart-unknown-field-allowed.patch"
dotnet build -c Release --no-restore -t:Rebuild -m:1
dotnet test -c Release --no-build --filter \
  'FullyQualifiedName~Http_StrictFieldsAndMultipart_AreCheckedAfterUploadBeforeTools'
# 1件成功。

git apply "$AUDIT_EVIDENCE/multipart-valid-fields.patch"
dotnet build -c Release --no-restore -t:Rebuild -m:1
dotnet test -c Release --no-build --filter \
  'FullyQualifiedName~Http_StrictFieldsAndMultipart_AreCheckedAfterUploadBeforeTools'
# 1件失敗。

git apply -R "$AUDIT_EVIDENCE/multipart-unknown-field-allowed.patch"
dotnet build -c Release --no-restore -t:Rebuild -m:1
dotnet test -c Release --no-build --filter \
  'FullyQualifiedName~Http_StrictFieldsAndMultipart_AreCheckedAfterUploadBeforeTools'
# 1件成功。
```

差分: [本番変異](multipart-unknown-field-allowed.patch)、[テスト側の正常fields](multipart-valid-fields.patch)。完成した修正ではケースごとの原因分離と失敗箇所の識別も行う。

## 監視器の適用範囲の比較

[monitor-witness.json](monitor-witness.json)は既存DriverTests.test_monitor_handles_qpdf_inspection_exit_and_rejects_unlimited_writerと同形式の模擬/proc入力を両Monitor.loopへ与えた結果。`qpdf --check input.pdf`、AS=570425344、CORE=0、FSIZE=unlimited、1 sampling iterationという条件。

旧MonitorはAssertionError、新Monitorは1 sampleを記録した。ただし旧Monitorの現在のimage専用経路ではqpdfへFSIZEも設定するため、これは現行capacityの失敗を再現した証拠ではない。適用範囲を広げる際の差として記録し、低価値候補件数には含めていない。
