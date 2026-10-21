# 低価値候補の改善台帳

初回監査の13項目はすべて対応済み。LV01/LV02の4メソッドに続き、残り11項目も改善・検証した。[実行証拠](evidence/improvements/README.md)／[CSV](low-value.csv)。初回の事実は履歴として保持する。

| ID | 優先度 | 初回の論点 | 状態 |
| --- | --- | --- | --- |
| [LV01](#lv01) | 高 | 不正queryの検証が、不正body由来の400で隠れる | 対応済み |
| [LV02](#lv02) | 高 | multipart拒否と不正fieldsの検証が同時に発火する | 対応済み |
| [LV03](#lv03) | 中 | 通常実行のPassedに、未実施の上限容量測定2件が混ざる | 対応済み |
| [LV04](#lv04) | 中 | builder直呼出でcancel/timeout/stoppingを18行反復する | 対応済み |
| [LV05](#lv05) | 低 | 実renderer取消の2行は同じtoken経路を通る | 対応済み |
| [LV06](#lv06) | 低 | 事前cancelと所有者Disposeを重いAPI contextで確認する | 対応済み |
| [LV07](#lv07) | 中 | 色空間の保存をArrayというshapeだけで判定する | 対応済み |
| [LV08](#lv08) | 低 | 透かし8行の色差を当該assertが確認しない | 対応済み |
| [LV09](#lv09) | 低 | 9回の呼出順序からprivate pathやJSON統合を主張している | 対応済み |
| [LV10](#lv10) | 中 | fallback回数と途中raw削除を結果200だけでは観測できない | 対応済み |
| [LV11](#lv11) | 中 | binary stream保存のhashがUTF-8文字列化を経由する | 対応済み |
| [LV12](#lv12) | 低 | 共通PDF検証で拒否するaliasをAPI contextで注入する | 対応済み |
| [LV13](#lv13) | 低 | ZIP名前の上限確認に実qpdfで500分割する | 対応済み |

比較対象は初回1353→現在1340ケース（共通取消の重複13行を削減）。テストメソッドは1件を統合し、binary helper回帰を1件追加して452件を維持。通常実行は1338成功・容量2件skip。容量2件も上限fixtureで別途有効化して成功した。

[全件台帳](tests.md)のsource位置と件数は現在。baseline_*列と[統合履歴](retired-tests.csv)で変更前を参照できる。

<a id="lv01"></a>

## LV01 不正queryの検証が、不正body由来の400で隠れる

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfPageNumbersTests.InvalidQueries_Return400BeforeReadingUploadOrRunningQpdf](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L19), [PdfWatermarkTests.InvalidQueryPrecedesUploadAndTools](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L26), [PdfStampImageTests.InvalidQueryPrecedesUploadAndTools](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L85)。

**初回監査の問題**: 3メソッドとも不正queryに非multipartのStringContentを併用する。本番endpointのquery Parseを既定値の生成に置換しても既存3件はすべて成功した。bodyだけを正常なmultipartへ変更すると3件とも400期待/500実際で失敗し、本番コードを戻すと3件とも成功した。500はテスト指定の実行禁止toolへ処理が進んだ結果。

**元の改善条件**: 不正なのはqueryだけのHTTPケースへ置換し、別ケースで読取禁止stream/読取カウンタ=0とtool未起動を確認する。query Parse削除変異で赤、正常実装で緑になること。削除せず境界保証を回復する。

**実装と検証**: 3メソッドを正常multipartへ変更し、別のTestServer要求で本文読取0回をassert。各Parse削除変異3種を各対応メソッドが検出。共通helperでquery検証をupload後へ移す変異も3件すべて検出。

<a id="lv02"></a>

## LV02 multipart拒否と不正fieldsの検証が同時に発火する

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfFillTemplateTests.Http_StrictFieldsAndMultipart_AreCheckedAfterUploadBeforeTools](../../tests/Amane.Pdf.Api.Tests/PdfFillTemplateTests.cs#L25)。

**初回監査の問題**: multipart kind行列のfieldsが空配列[]で、単独でも不正。ReadFileAndTextAsyncで未知のtextフィールドだけ受け入れる変異を入れてもメソッド全体は成功した。行列内の3つのStringContentを正常fieldsへ置換すると400期待/500実際で失敗し、本番を戻すと成功した。全8 kindの個々の漏れを実証したわけではなく、実証対象はunknown=textの1枝。

**元の改善条件**: 未知/重複part試験では全ての他partを正常化し、fieldsの文法試験は独立する。少なくとも未知text許容変異を検出すること。missing等は欠落箇所以外を有効にする。query受付の順序も別に検証する。

**実装と検証**: multipart行列のfieldsを正常化し、不正kindをassert messageへ追加。query試験も正常Formへ変更して本文未読をassert。未知text許容・fields解析削除・query拒否削除の3変異と共通の順序逆転変異を検出。

<a id="lv03"></a>

## LV03 通常実行のPassedに、未実施の上限容量測定2件が混ざる

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [FillTemplateCapacityTests.UpperLimits_Single](../../tests/Amane.Pdf.Api.Tests/FillTemplateCapacityTests.cs#L12), [FillTemplateCapacityTests.UpperLimits_TwoConcurrent](../../tests/Amane.Pdf.Api.Tests/FillTemplateCapacityTests.cs#L16)。

**初回監査の問題**: FILL_TEMPLATE_CAPACITY_INPUTまたはFIELDSが未指定なら共有helperがassert前にreturnする。今回も環境変数を指定しておらず2件はPassedだが容量を測っていない。一方、CIのfill-template-validation.py.builder_capacityは両変数を渡して実行する。CI全体から欠落しているわけではない。

**元の改善条件**: 既存FillTemplateCapacityカテゴリで通常suiteから明示除外するか、未設定時はInconclusiveで理由を出す。Docker CIの必須実行と失敗伝播を維持し、上限fixture付きの結果を別集計する。

**実装と検証**: 未指定はAssert.Inconclusive、片方だけの指定は失敗に変更。CI既存の環境変数付き呼出を維持し、同じ50MiB/1000頁/200field/1000行fixtureで単独・2同時の両方を実測して成功。

<a id="lv04"></a>

## LV04 builder直呼出でcancel/timeout/stoppingを18行反復する

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfOverlayResourceTests.EveryStage_CancellationStopsProcessTree_AndBuilderRemovesIntermediates](../../tests/Amane.Pdf.Api.Tests/PdfOverlayResourceTests.cs#L20)。

**初回監査の問題**: 6stage×3kind。caller/stoppingはテスト所有CTSで、timeoutもlinked CTSへのCancelAfter(20)。実IHostApplicationLifetimeやendpoint期限は接続せず、builderからは取消tokenとして同じ。子孫PID消滅は実観測するが、job削除はテストがFiles.Disposeした後に確認する。

**元の改善条件**: builderでは各6stageの取消を残し、3kindの差は実HTTP/lifetimeで検証する。中間物削除はbuilder責務と所有者Dispose責務を分けてassert。全メソッド削除はしない。

**実装と検証**: 直接builderは6stageの取消へ統合（18→6）。所有者Dispose前に中間物除去を検証し、input削除後の最終outputは所有者の責務として区別。HTTPのtimeout/cancel/stoppingは既存testを維持。cleanup削除変異は5stageで検出（finalは既に中間物が消えた後）。

<a id="lv05"></a>

## LV05 実renderer取消の2行は同じtoken経路を通る

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfRendererTests.Cancellation_KillsRealRenderer_WithExactLimits_AndRemovesOutput](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L186)。

**初回監査の問題**: false/trueの差は同じdeadline CTSのCancelとCancelAfter(50)。FIFOで実PDFiumプロセスを止め、/procのDATA/FSIZE/CORE/環境とPID消滅を検証しており、その部分は強い。最後のjob削除は明示Files.Dispose後。

**元の改善条件**: 実PDFium到達、正確なlimits、子PID回収を残したまま直接token取消を1行へ統合可能か検討する。HTTP2経路は残す。時間差raceを目的に残すならその目的と観測を明示する。

**実装と検証**: 同一token経路を1件へ統合（2→1）。FIFOで実PDFium到達、native limits/環境、PID回収、出力不在を維持。HTTPのtimeout/cancel 2経路は変更せず保持。

<a id="lv06"></a>

## LV06 事前cancelと所有者Disposeを重いAPI contextで確認する

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfProtectTests.PreCancelledJob_DoesNotStartQpdf_AndOwnerDisposalRemovesFiles](../../tests/Amane.Pdf.Api.Tests/PdfProtectTests.cs#L93)。

**初回監査の問題**: PdfTestContextを作るがHTTP要求はせず、事前cancelしたtokenで新しいQpdfProcessor.ProtectAsyncを直接呼ぶ。TemporaryPdfFilesのusingを抜けた後にAssertCleanする。処理中のqpdfやendpointによる後始末は観測していない。

**元の改善条件**: 軽量temporary directoryとprocessorで事前cancelを確認し、期待するjob書込/未起動を直接観測する。既存HTTPのcancel・PID・cleanup保証は残す。

**実装と検証**: WebApplicationFactoryを除去し、事前取消を直接processorで検証。起動不可tool path、取消例外、出力不在、所有者Dispose前の入力保持とDispose後の削除を確認。

<a id="lv07"></a>

## LV07 色空間の保存をArrayというshapeだけで判定する

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfCompressTests.SupportedColorSpaces_ArePreserved](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L135)。

**初回監査の問題**: CalRGB/CalGray/ICCBasedの3行で、再圧縮数1とcolorspace.ValueKind==Arrayまでを検証する。色空間名、WhitePoint、参照先ICC byte等の等値比較はない。ICCBased fixtureは128個の0 byteで、コメントどおりprofile妥当性を候補選択の対象外にしている。

**元の改善条件**: 候補受付テストなら名前を限定する。保存を主張するならColorSpace名と構成値、正規化した参照先profileのbytesを比較する。独立描画テストは維持。

**実装と検証**: ColorSpace名・要素数・WhitePoint、ICCのN/Alternate/全byteを比較。変化を検知しやすい非一様な合成profileへ変更。Arrayを保ってWhitePoint/色空間を上書きする変異を3行全てで検出。profile妥当性の実描画は既存独立Pythonを維持。

<a id="lv08"></a>

## LV08 透かし8行の色差を当該assertが確認しない

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfWatermarkTests.AllStylesAndColorsPreserveGeometryAndDrawOnceOnEveryPage](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L48)。

**初回監査の問題**: 8 DataRowはstyle/color/textを変えるが主assertはpage geometry、Tj数、opacity、存在/秘密露出等。実color値や抽出文字の一致を直接assertしていない。geometry比較も本番PdfPageBoxesを両側に使う。

**元の改善条件**: HTTP styleごとの代表とprivacyを残し、色行列は実色/文字をassertするかPythonへ集約する。色が効かない変異で赤になる確認後に行数を縮小する。

**実装と検証**: 8行を維持し実rg色を数値比較、drawn fontのToUnicodeとCID文字列から描画文字も比較。Latin1でCID byteを保持。色のgray固定はred 4行、DRAFT固定は日本語4行で検出。

<a id="lv09"></a>

## LV09 9回の呼出順序からprivate pathやJSON統合を主張している

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfCleanTests.JobAndArguments_UseFixedOptionsPrivatePathsPermissionsAndOutputCleanup](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L260)。

**初回監査の問題**: BlockingQpdf.Callsは各呼出の第1引数を記録する。メソッドは9個の先頭引数の固定順序とcleanupを比較するだけで、全argv/private pathや--json-keyの集合を検証していない。

**元の改善条件**: 隣接の詳細argv検証に必要な呼出上限/重複入力検査なしを集約する。固定順序が不可欠なら理由を残す。private path保証を先頭引数だけのテストへ委ねない。

**実装と検証**: 先頭引数9個の固定順序だけを確認する1メソッドを削除し、全argv/private job/permissionsを検証する既存メソッドへ統合。起動上限9回、入力/統合出力JSON各1回、job1回を追加。

<a id="lv10"></a>

## LV10 fallback回数と途中raw削除を結果200だけでは観測できない

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfCompressResourceTests.BatchStdoutOverflow_FallsBackOncePerImage_AndCleansPartialRaw](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L224)。

**初回監査の問題**: StdoutLimit=1500、4画像でHTTP200/再圧縮数4/最終AssertCleanを確認する。batch失敗後の起動回数、画像ごとのfallback回数、次の試行前のpartial raw削除は記録していない。

**元の改善条件**: 実qpdfをexecするwrapperでbatch抽出と単画像fallbackの対象/回数を数え、次の抽出時点で前回rawが消えていることを観測する。実回復成功のassertは残す。

**実装と検証**: 実qpdfを包む観測wrapperで1 batch+画像ごと1回の計5回と対象の一意な対応を確認。overflow時にrawが実在したこと、次の抽出前に旧raw directoryが無いことを確認。途中cleanup削除変異を検出。

<a id="lv11"></a>

## LV11 binary stream保存のhashがUTF-8文字列化を経由する

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfCleanTests.Clean_RemovesMetadataAllAttachmentPathsAuthorsAndPopups_PreservesPageStreamsAndOrdinaryData](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L42), [PdfCleanTests.BinaryStreamCapture_PreservesInvalidUtf8Bytes](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L118)。

**初回監査の問題**: ContentHashAsyncはqpdf --filtered-stream-dataをPdfTestContext.QpdfAsyncのStandardOutput.ReadToEndAsyncでstring化し、contentと画像を連結してUTF8再encode後hashする。不正UTF8 byteは置換文字へ潰れるため、raw byte差を失う。stream境界の区切りもない。

**元の改善条件**: stdoutをBaseStreamでbytesとして回収し、page contentと画像それぞれを長さ/役割付きで比較する。object番号の合法な変化は正規化する。テスト全体を削除しない。

**実装と検証**: QpdfBytesAsyncがstdoutをBaseStreamでbyte回収。page/streamの順序・長さ付きhashとimage hashのmultisetを比較し、連結境界を分離。既知の不正UTF8 byteを守る新規回帰テストを追加し、文字列往復する変異で検出。

<a id="lv12"></a>

## LV12 共通PDF検証で拒否するaliasをAPI contextで注入する

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [CompressMetadataTests.DefensiveAliasChains_ReleaseSpoolBudget_ForReuse](../../tests/Amane.Pdf.Api.Tests/CompressMetadataTests.cs#L10)。

**初回監査の問題**: テスト自身のコメントどおり、indirect objectの値が別referenceである構造は共通validatorが拒否する。起動後QpdfPathをshellへ差し替え、CompressMetadataを直接呼んで3段aliasの予約解放/再利用を確認する。HTTPは通らない。

**元の改善条件**: 共通入口が拒否する前提を維持する限り、API起動を伴わないmetadata単体へ寄せる。alias深度/循環/予算返却という固有保証は残す。

**実装と検証**: 防御的aliasをCompressMetadataTestsへ移動。APIを起動せず、3段aliasの全spool削除・予算0への返却・再利用を維持。受付可能PDFのテストとは明示して分離。

<a id="lv13"></a>

## LV13 ZIP名前の上限確認に実qpdfで500分割する

状態: **対応済み**。優先度は初回監査の改善順。

現在の検証: [PdfSplitFileNameTests.MaximumUnicodeNameAnd500Entries_FitExistingZipOverhead](../../tests/Amane.Pdf.Api.Tests/PdfSplitFileNameTests.cs#L138)。

**初回監査の問題**: 名前/UTF8 flag/ZIP overheadをassertするため、MaxSplitParts=500・QpdfTimeoutSeconds=120へ上げ、500pageを実HTTPで分割する。今回19.388秒。既定100parts/30秒とは別条件であり、テスト内コメントも意図を明示している。

**元の改善条件**: 最大Unicode名×500entryの実ZIP生成/overheadを低層で検証し、代表HTTPケースは維持する。500partsの設定対応を保証したい場合は明示カテゴリの結合測定として残す。削減時間は変更後に実測し、19秒がそのままwall短縮とは見積もらない。

**実装と検証**: 500回の実qpdf分割を除去。最大Unicode名・500part plan・本番のbounded ZIP stream/capacityで実ZIPを生成し、名前/UTF8 flag/overheadを確認。20 headerケースのHTTP結合検証は維持。
