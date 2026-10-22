# 残り11項目の改善・実行証拠

LV01/LV02に続き、LV03〜LV13をすべて改善した。[改善台帳](../../low-value.md)の13項目は全て対応済み。初回の問題、実装、現在の検証先は台帳へ残している。

**このdirectoryの結果・source hash・tests.patchはcommit `ab0eae6`時点の履歴**。レビューで本番ZIP書込の保証の欠落と、失敗原因・runner判定の弱点が見つかった。[修正後の最新結果](../review-fixes/README.md)を参照。以下のLV13は当時のテスト内ZIPループで、本番WriteZipAsyncを通していなかった。現在は直接呼出へ修正済み。

## 変更と検証

| 対象 | 実施した改善 | 保持した保証・確認 |
| --- | --- | --- |
| LV03 | 容量未指定は明示skip、片方だけ指定は失敗。 | CIの環境変数付き測定を維持。同じ上限fixtureで単独・2同時の両方を実行して成功。 |
| LV04 | builder直呼出の18行を6stage取消へ統合。 | 子孫PID回収と、所有者Dispose前の中間物削除。HTTPの取消/timeout/停止配線は既存testを保持。 |
| LV05 | 実rendererの同一token取消を2行から1件へ統合。 | FIFOで実PDFiumへ到達しnative limits/環境・PID回収・出力不在を検証。HTTPの2経路は保持。 |
| LV06 | 事前取消からAPI起動を除去。 | 取消例外、toolへ進まないこと、出力不在、所有者Dispose前後の入力所有権。 |
| LV07 | 色空間のArray判定を構成値とprofile全byte比較へ強化。 | CalRGB/CalGray/ICCの区別、WhitePoint、N/Alternate。実有効ICCの独立描画検証も保持。 |
| LV08 | 8条件それぞれで実rg色とCID/ToUnicodeの文字を比較。 | 既存geometry/opacity/一頁一描画/非露出を維持。CIDをLatin1で情報損失なく扱う。 |
| LV09 | 先頭引数の固定9個だけを見るテストを詳細argvテストへ統合。 | 全argv/private job/permissionsに加えprocess上限9回、input/combined output JSON各1回、job1回。 |
| LV10 | 実qpdf wrapperでfallback回数・対象と途中cleanupを観測。 | 1batch+4個別、全画像を一度ずつ処理、実partial rawの存在と次抽出前の旧directory不在。 |
| LV11 | stdoutをraw bytesで回収し、page/streamの役割・長さ別hashとimage multisetへ変更。 | 不正UTF8の独立固定byte列を守る回帰テストを追加。合法なobject番号の変化は比較から除外。 |
| LV12 | 防御的aliasをAPI起動なしのCompressMetadataTestsへ移管。 | 3段alias、全spool削除、予算返却と再利用。HTTP到達性の保証と混同しない。 |
| LV13 | 名前/overheadのための実qpdf500回を、実bounded ZIPの500entry生成へ移管。 | 最大Unicode名、500part plan、UTF8 flag、capacity/overhead。20種類のHTTP header→ZIP検証は保持。 |

メソッドは1件を既存ケースへ統合し、binary helper回帰を1件追加したため**452件のまま**。新しいmetadataテストへ移したことでファイル56・クラス58。共通取消の重複を13行減らし、展開後は**1340ケース**となる。

今回の対象実行では、500entryのZIP検証は約0.010秒（初回HTTP500分割は約19.388秒）、事前取消は約0.009秒、防御的aliasは約0.083秒だった。1回のhost観測で、CIのwall時間短縮を保証する数字ではない。

## Release全件と容量

Release buildは0 error、NU1900 warningが2件（NuGet脆弱性metadataの取得不能）。

**Releaseの一括実行は1338 Passed / 2 Skipped / 0 Failed、5分32秒で完了**。skipは環境変数未指定のFillTemplate容量測定2件で、他の未実行はない。各メソッドと展開数を[全件CSV](../../tests.csv)に照合した。

その2件も、CIのcapacity_fixture関数から作った50MiB・1000page・200field・1000line・JSON 55,555byte（許可11,425 glyphを含む）の合成入力で有効化し、単独・2同時とも成功した。予約peakはいずれも120,582,144byteで124MiB以下、描画は約1.82〜1.87秒。hostでの実測であり、本番DockerのCPU1・1.5GiB cgroup・tmpfs256MiB条件を再実測したという意味ではない。詳細は[単独](fill-template-budget-1.json)と[2同時](fill-template-budget-2.json)。

前回の2回のtest host crashは今回は再現しなかった。原因が判明・修正されたという主張はしない。今回のhost資源peakはRSS 1,042,248KiB（約1018MiB）、FD486、35threads。[推移](host-resources.json)と[集計](verification.json)を保存した。過去の中断は[LV01/LV02時点の記録](../validation-fix/README.md)に残す。

Docker suiteは今回再実行していない。本番srcは変更せず、テストと台帳だけを変更した。

## 比較・cleanupの変異

6変異を一つずつ隔離コピーへ適用し、Rebuild後に対象メソッドを実行した。保存した失敗messageを確認した範囲では対象の退行に起因している。ただし当時のrunnerはFailedの有無を判定するだけで、狙ったassertかどうかは自動照合していなかった。現在のrunnerはこの点を修正済み。

| 変異 | Failed | Passed | 検出内容 |
| --- | ---: | ---: | --- |
| colorspace-whitepoint-replaced | 3 | 0 | ArrayのままColorSpace/WhitePointを変更しても検出。 |
| watermark-color-forced-gray | 4 | 4 | red指定の4行が失敗。元からgrayの4行は正常。 |
| watermark-text-forced-draft | 4 | 4 | 日本語の4行が失敗。元からDRAFTの4行は正常。 |
| overlay-intermediate-cleanup-removed | 5 | 1 | 中間物がある5stageで失敗。final到達前には中間物除去が済んでいるため、その1行は変異の影響がない。 |
| compress-raw-cleanup-removed | 1 | 0 | raw残留をwrapperのexit 97で知らせた結果、再圧縮数の期待4/実際0で失敗。cleanup専用assertによる失敗ではない。 |
| binary-capture-lossy-utf8 | 1 | 0 | helperのUTF8文字列往復で固定byte列が壊れることを検出。これは本番でなく比較oracleの変異。 |

**6/6変異を検出**。対象27 parameter caseのうち18件が意図した失敗、9件が変異に影響されず成功。正常対照は実験前後とも19/19成功。全suiteのmutation scoreではない。LV01/LV02の入力検証7変異は[前段の証拠](../validation-fix/README.md)を参照する。

[results.json](results.json)に結果・expected/actual・TRX/assembly/source/patch hashを保存。[verification.json](verification.json)にRelease全件・容量・当時のtest source hashを保存した。生のPDF本文や全TRXを転載していない。

## 再現

この段階を再現する場合、runnerとpatchは**両方ともcommit `ab0eae6`から取得する**。現在のrunnerは修正後のテストと7変異を対象にするため、旧patchと組み合わせない。最新版の再現手順は[レビュー修正後](../review-fixes/README.md#再現)を参照。

必要な依存とPdf__*設定は[初回の環境説明](../README.md)を参照。基準commit `465088ed0b575f055beb6851e98d28ed22fb9928`の使い捨てcheckoutへ[tests.patch](tests.patch)を適用し、Release buildする。patchは前段の入力検証修正も含む。移管先の新規CompressMetadataTests.csも含めている。

```bash
# AUDIT_IMPROVEMENTSはこのdirectoryの絶対path。
git apply "$AUDIT_IMPROVEMENTS/tests.patch"
dotnet build --configuration Release -m:1 --disable-build-servers
dotnet test --configuration Release --no-build -m:1
python3 "$AUDIT_IMPROVEMENTS/run-mutations.py" --source . --output /tmp/pdf-test-value-mutations
```

変異runnerは[既存の隔離runner](../validation-fix/run-mutations.py)を再利用するため、evidenceをdirectoryごと保持する。sourceを一時directoryへコピーし、そのコピーでのみ変異を書き込む。各変異の.patchもここに保存した。parameter行の一部だけが変異の影響を受けるため、成功行と失敗行を区別して結果に残す。

容量測定は通常のdotnet testではskipと表示される。CIと同じ入力を作り、FILL_TEMPLATE_CAPACITY_INPUT/FIELDS/RESULTSを指定した別実行で確認する。既存CIのbuilder_capacityはこの経路を呼び、結果JSONの不在も失敗になる。
