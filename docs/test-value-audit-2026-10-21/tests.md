# 全MSTestメソッド台帳

初回commit `465088ed0b575f055beb6851e98d28ed22fb9928`からの改善後の現在値。56ファイル・58クラス・452メソッド、展開1340ケース。レビュー修正後は3群で全件を確認し、1338成功・容量2件skip。表とCSVの現在の結果はこの3群による。CIはコード変更コミットで一括1338成功・2skip、Dockerの容量2件も成功。ローカル一括のtesthost crashは履歴に残す。

[最新の再検証](evidence/review-fixes/README.md)。[最新の再検証](evidence/review-fixes/README.md)。[CSV](tests.csv)に現在の判定根拠/経路/件数/時間とbaseline_*の初回値を併記。source行番号は現在。[統合履歴](retired-tests.csv)に削除した1メソッドの統合先を記録。[改善台帳](low-value.md)13項目は対応済み。

## CleanCapacityTests

本番参照: [CleanCapacity.cs](../../src/Amane.Pdf.Api/CleanCapacity.cs), [PdfCleanEndpoint.cs](../../src/Amane.Pdf.Api/PdfCleanEndpoint.cs)。

層: 単体・設定起動。判定: 固定値・丸め境界・例外。

重複・補完: PdfCleanTestsの実ファイル上限と補完。算術境界はここで網羅。

適用範囲: 予約会計を検証。cgroup/tmpfs実容量は手動clean-validation.py。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T001 | [CleanCapacityTests.Reservations_RoundEveryFileAndReturnActualAllocationAfterWriting](../../tests/Amane.Pdf.Api.Tests/CleanCapacityTests.cs#L7) | 1 | ファイルごとの4KiB丸め・書込予約と実使用量返却・予算超過拒否 | 維持 |
| T002 | [CleanCapacityTests.BoundedWriter_RejectsBeforeDiskWriteAndHonorsCancellation](../../tests/Amane.Pdf.Api.Tests/CleanCapacityTests.cs#L21) | 1 | 容量超過前の書込停止とキャンセル優先 | 維持 |
| T003 | [CleanCapacityTests.Startup_RejectsInvalidLimitsOverflowAndJobSmallerThanRoundedUpload](../../tests/Amane.Pdf.Api.Tests/CleanCapacityTests.cs#L35) | 1 | 起動時の容量設定範囲・overflow・丸め済uploadの最低予算 | 維持 |

## CompressMetadataTests

本番参照: [CompressMetadata.cs](../../src/Amane.Pdf.Api/CompressMetadata.cs), [CompressCapacity.cs](../../src/Amane.Pdf.Api/CompressCapacity.cs), [PdfCompressProcessor.cs](../../src/Amane.Pdf.Api/PdfCompressProcessor.cs)。

層: 防御的単体・制限付きshell。判定: 3段spool実ファイル・予約量・削除と再利用。

重複・補完: compress-validation.py --renderは有効ICCと独立Popplerの画質。単体のshape確認と相互補完。

適用範囲: 共通validatorが拒否する防御的内部枝。API起動やHTTPを通した保証とは数えない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T064 | [CompressMetadataTests.DefensiveAliasChains_ReleaseSpoolBudget_ForReuse](../../tests/Amane.Pdf.Api.Tests/CompressMetadataTests.cs#L10) | 1 | 受付外の3段alias解決・spool全削除・予約返却/再利用をAPI起動なしで確認 | LV12：対応済み・維持（改善検証済み） |

## ExternalProcessRunnerTests

本番参照: [ExternalProcessRunner.cs](../../src/Amane.Pdf.Api/ExternalProcessRunner.cs), [ProcessMemoryLimits.cs](../../src/Amane.Pdf.Api/ProcessMemoryLimits.cs)。

層: 実プロセス・低層。判定: 実PID・stdout byte上限・argv・環境・例外。

重複・補完: 各APIの子孫停止テストは呼出配線を確認する別の層。

適用範囲: shell/人工子プロセスによる故障注入。HTTP timeoutやrate limitは対象外。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T004 | [ExternalProcessRunnerTests.Arguments_ArePassedWithoutShellInterpretation](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L11) | 1 | 空白・特殊文字・改行等のargvをshell展開せず渡す | 維持 |
| T005 | [ExternalProcessRunnerTests.Environment_AddsAndOverridesOnlySpecifiedVariables](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L27) | 1 | 指定した環境変数だけ追加/上書きし親環境を保持 | 維持 |
| T006 | [ExternalProcessRunnerTests.Environment_RejectsInvalidKeysBeforeStarting](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L64) | 7 | 不正な環境変数名をprocess開始前に拒否 | 維持 |
| T007 | [ExternalProcessRunnerTests.WorkingDirectory_IsApplied](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L78) | 1 | 作業ディレクトリと相対出力先を適用 | 維持 |
| T008 | [ExternalProcessRunnerTests.StdoutLimit_ReturnsOnlyCompleteOutputWithinLimit](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L100) | 7 | stdout容量以内だけ完全取得し超過時はnull | 維持 |
| T009 | [ExternalProcessRunnerTests.StdoutLimit_IsMeasuredInBytes](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L121) | 2 | stdout上限を文字数でなくUTF-8 byte数で計算 | 維持 |
| T010 | [ExternalProcessRunnerTests.StdoutLimit_RejectsNegativeLimitBeforeStarting](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L140) | 1 | 負のstdout上限をprocess開始前に拒否 | 維持 |
| T011 | [ExternalProcessRunnerTests.Run_ReturnsExitCodeAndDiscardsOutputByDefault](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L156) | 3 | exit code保持と既定のstdout/stderr破棄 | 維持 |
| T012 | [ExternalProcessRunnerTests.LargeStdoutAndStderr_AreDrainedWithoutBlocking](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L171) | 2 | 大量stdout/stderrの同時drainでdeadlockしない | 維持 |
| T013 | [ExternalProcessRunnerTests.StartFailure_Propagates](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L184) | 1 | 起動できない実行ファイルのWin32Exception伝播 | 維持 |
| T014 | [ExternalProcessRunnerTests.PreCancelled_DoesNotStartOrAttemptToStartProcess](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L195) | 2 | 開始前cancelは存在しない実行ファイルも起動しない | 維持 |
| T015 | [ExternalProcessRunnerTests.Cancellation_ThrowsAndStopsParentAndChild](../../tests/Amane.Pdf.Api.Tests/ExternalProcessRunnerTests.cs#L214) | 2 | 実行中cancelが親子process停止を待って例外を返す | 維持 |

## FillTemplateCapacityTests

本番参照: [PdfOverlayBuilder.cs](../../src/Amane.Pdf.Api/PdfOverlayBuilder.cs), [FillTemplateFields.cs](../../src/Amane.Pdf.Api/FillTemplateFields.cs), [OverlayCapacity.cs](../../src/Amane.Pdf.Api/OverlayCapacity.cs)。

層: 直接builder・条件付き容量測定。判定: 実描画・予約peak・経過時間・削除。

重複・補完: FillTemplateFieldsTestsの境界とfill-template-validation.pyのHTTP容量と補完。

適用範囲: 未指定時は明示skip。片方だけ指定は失敗。環境変数ありの上限測定はCIおよび今回のhost単独/2同時で成功。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T016 | [FillTemplateCapacityTests.UpperLimits_Single](../../tests/Amane.Pdf.Api.Tests/FillTemplateCapacityTests.cs#L12) | 1 | 上限fixtureの単独描画・予約peak124MiB/30秒/中間物削除（未設定は明示skip） | LV03：対応済み・維持（改善検証済み） |
| T017 | [FillTemplateCapacityTests.UpperLimits_TwoConcurrent](../../tests/Amane.Pdf.Api.Tests/FillTemplateCapacityTests.cs#L16) | 1 | 上限fixtureの2同時描画・予約peak/30秒/中間物削除（未設定は明示skip） | LV03：対応済み・維持（改善検証済み） |

## FillTemplateFieldsTests

本番参照: [FillTemplateFields.cs](../../src/Amane.Pdf.Api/FillTemplateFields.cs), [PdfFillTemplateEndpoint.cs](../../src/Amane.Pdf.Api/PdfFillTemplateEndpoint.cs)。

層: 単体。判定: 独立の固定座標・decimal境界・厳格JSON例外。

重複・補完: PdfFillTemplateTestsはHTTP取り込みと配線。全不正JSON行の再走は縮小余地があるが入口の検証は残す。

適用範囲: pdfcpuでの実描画は行わない。font/baselineは独立Python描画で補完。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T018 | [FillTemplateFieldsTests.StrictJson_RejectsInvalidContractsAndMalformedUtf8](../../tests/Amane.Pdf.Api.Tests/FillTemplateFieldsTests.cs#L52) | 1 | 必須・重複・未知キー、型、数値表記、文字、UTF-8、個数の厳格拒否 | 維持 |
| T019 | [FillTemplateFieldsTests.Text_PreservesWhitespaceNormalizationAndLiteralPercent](../../tests/Amane.Pdf.Api.Tests/FillTemplateFieldsTests.cs#L62) | 1 | 空白のみの拒否と空白・改行・結合文字・literal percentの保持 | 維持 |
| T020 | [FillTemplateFieldsTests.NumericAndCountBoundaries_AreExact](../../tests/Amane.Pdf.Api.Tests/FillTemplateFieldsTests.cs#L75) | 1 | fontSize・座標・page・全体/頁内個数の境界と対象ページ範囲 | 維持 |
| T021 | [FillTemplateFieldsTests.Placement_FixedCoordinatesAndBaseline_AndBlankLines](../../tests/Amane.Pdf.Api.Tests/FillTemplateFieldsTests.cs#L90) | 1 | 固定期待座標で左右中央配置・baseline・空行間隔 | 維持 |
| T022 | [FillTemplateFieldsTests.Overflow_ExactFitAndPointOnePtOverflow_UseDecimal](../../tests/Amane.Pdf.Api.Tests/FillTemplateFieldsTests.cs#L112) | 1 | decimalのぴったり収容と0.1pt超過・下端配置のpdfcpu補正 | 維持 |

## FillTemplateGlyphsTests

本番参照: [FillTemplateGlyphs.g.cs](../../src/Amane.Pdf.Api/FillTemplateGlyphs.g.cs), [WatermarkGlyphs.g.cs](../../src/Amane.Pdf.Api/WatermarkGlyphs.g.cs)。

層: 単体・生成データ。判定: hash/順序/lookup・代表GID/advance固定値。

重複・補完: fill-template-glyphs-validation.pyは実pdfcpu/ToUnicode/字体を検証するので重複削除不可。

適用範囲: 全lookupの自己整合だけではfontとの一致は証明できない。代表固定値とPython実描画を併用。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T023 | [FillTemplateGlyphsTests.GeneratedDataMatchesHashesAndLookup](../../tests/Amane.Pdf.Api.Tests/FillTemplateGlyphsTests.cs#L19) | 1 | 固定font hashとの結合・データchecksum・昇順/一意GID・全lookup整合（字形真値は別検証） | 維持 |
| T024 | [FillTemplateGlyphsTests.ExcludedScalarsAreAbsent](../../tests/Amane.Pdf.Api.Tests/FillTemplateGlyphsTests.cs#L53) | 1 | 制御文字・surrogate・IVS・非BMP・曖昧GID文字の拒否 | 維持 |
| T025 | [FillTemplateGlyphsTests.RequiredSymbolsAndSpecialCharactersArePresent](../../tests/Amane.Pdf.Api.Tests/FillTemplateGlyphsTests.cs#L67) | 1 | 業務用記号・結合文字・空白の許可 | 維持 |
| T026 | [FillTemplateGlyphsTests.RepresentativeGlyphsHaveIndependentExpectedValues](../../tests/Amane.Pdf.Api.Tests/FillTemplateGlyphsTests.cs#L79) | 10 | 代表10文字のGID/advanceを独立固定値と比較 | 維持 |
| T027 | [FillTemplateGlyphsTests.ExistingWatermarkCoverageAndAsciiWidthsAgree](../../tests/Amane.Pdf.Api.Tests/FillTemplateGlyphsTests.cs#L87) | 1 | 既存watermark文字集合とASCII幅との後方互換 | 維持 |

## FillTemplateStructureTests

本番参照: [PdfFormDetector.cs](../../src/Amane.Pdf.Api/PdfFormDetector.cs), [FillTemplateFields.cs](../../src/Amane.Pdf.Api/FillTemplateFields.cs)。

層: 単体・構造探索。判定: 人工JSONの許可/拒否・深度・キャンセル。

重複・補完: PdfFillTemplateTestsとPython pyHanko実署名でPDF→JSONの入口を補完。

適用範囲: 人工のdirect/indirect構造。実署名の暗号検証をするテストではない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T028 | [FillTemplateStructureTests.Forms_RecursivelyScanDirectArraysDictionariesAndStreamDictionaries](../../tests/Amane.Pdf.Api.Tests/FillTemplateStructureTests.cs#L9) | 1 | フォーム/署名のdirect/array/stream/object stream/escape名検出と文字列誤検知防止・中止 | 維持 |

## FromImagesCapacityMeasurementTests

本番参照: [PdfFromImagesProcessor.cs](../../src/Amane.Pdf.Api/PdfFromImagesProcessor.cs), [FromImagesCapacity.cs](../../src/Amane.Pdf.Api/FromImagesCapacity.cs)。

層: 直接processor・容量測定。判定: 予約peak・中間物削除・期待status。

重複・補完: from-images-validation.py --reservationsから上限fixtureを投入。FromImagesDesignTestsは会計境界。

適用範囲: 環境変数なしでも小PNGの実処理は実行。通常の成功は上限容量成功を意味しない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T029 | [FromImagesCapacityMeasurementTests.MeasureActualReservations_WithSyntheticInputs](../../tests/Amane.Pdf.Api.Tests/FromImagesCapacityMeasurementTests.cs#L14) | 1 | 実processorの入力削除・中間物削除・予約ピークと期待status（通常は小PNGのみ） | 維持 |

## FromImagesDesignTests

本番参照: [FromImagesCapacity.cs](../../src/Amane.Pdf.Api/FromImagesCapacity.cs), [JpegNormalizer.cs](../../src/Amane.Pdf.Api/JpegNormalizer.cs), [ExifOrientation.cs](../../src/Amane.Pdf.Api/ExifOrientation.cs), [ImageInputInspector.cs](../../src/Amane.Pdf.Api/ImageInputInspector.cs), [PdfFromImagesProcessor.cs](../../src/Amane.Pdf.Api/PdfFromImagesProcessor.cs)。

層: 単体・一部実ツール。判定: 固定計算値・marker bytes・typed exception。

重複・補完: PdfFromImagesTestsは実HTTP/ツール、Pythonは独立Pillow/Poppler。

適用範囲: 細かい内部判定を検証。合成入力やtool結果だけではHTTP全体を保証しない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T030 | [FromImagesDesignTests.StandardScale_UsesFloorAndRoundsDecodedDimensionsUp](../../tests/Amane.Pdf.Api.Tests/FromImagesDesignTests.cs#L14) | 4 | JPEG縮小比のceil境界と丸め後の寸法 | 維持 |
| T031 | [FromImagesDesignTests.Layout_UsesExactA4AndTenMillimeterMarginsRegardlessOfCulture](../../tests/Amane.Pdf.Api.Tests/FromImagesDesignTests.cs#L22) | 1 | 文化設定に依存しないA4縦横・10mm余白 | 維持 |
| T032 | [FromImagesDesignTests.Capacity_ReservesRoundedSentinelAndReleasesOnlyAfterDeletion](../../tests/Amane.Pdf.Api.Tests/FromImagesDesignTests.cs#L44) | 1 | sentinelと4KiB丸め予約・実サイズ返却・削除後解放 | 維持 |
| T033 | [FromImagesDesignTests.Settings_Leave64MiBForLibrariesAndRejectOverflow](../../tests/Amane.Pdf.Api.Tests/FromImagesDesignTests.cs#L66) | 1 | JPEGメモリのlibrary余白と容量設定overflow/上限 | 維持 |
| T034 | [FromImagesDesignTests.Exif_HandlesByteOrdersDuplicatesAndHostileOffsets](../../tests/Amane.Pdf.Api.Tests/FromImagesDesignTests.cs#L79) | 1 | EXIF byte order・orientation 1〜8と破損/重複/不正offsetのfallback | 維持 |
| T035 | [FromImagesDesignTests.Trim_UsesEightPixelsForGrayAndAllowsSmallTranspose](../../tests/Amane.Pdf.Api.Tests/FromImagesDesignTests.cs#L97) | 1 | 灰色/色MCUのtrimと極小画像transposeの可否 | 維持 |
| T036 | [FromImagesDesignTests.ToolResult_ClassifiesObservableSentinelBeforeExitButNeverInfersDeletedOutput](../../tests/Amane.Pdf.Api.Tests/FromImagesDesignTests.cs#L106) | 1 | 観測できる容量超過とtool失敗・出力消失の分類 | 維持 |
| T037 | [FromImagesDesignTests.MetadataInspection_RejectsAppMarkersInLaterScansAndTrailingBytes](../../tests/Amane.Pdf.Api.Tests/FromImagesDesignTests.cs#L130) | 1 | 後続scanのAPP markerとEOI後の秘密データを拒否 | 維持 |

## HealthEndpointTests

本番参照: [Program.cs](../../src/Amane.Pdf.Api/Program.cs)。

層: HTTP。判定: 200とHealthy本文。

重複・補完: Resource系テストは負荷中のhealth到達性であり別目的。

適用範囲: 通常起動時のhealthだけ。依存障害時の起動拒否は別テスト。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T038 | [HealthEndpointTests.Healthz_ReturnsHealthy](../../tests/Amane.Pdf.Api.Tests/HealthEndpointTests.cs#L9) | 1 | 実Program起動後に/healthzが200とHealthy本文を返す | 維持 |

## JpegHeaderTests

本番参照: [JpegHeader.cs](../../src/Amane.Pdf.Api/JpegHeader.cs), [ImageInputInspector.cs](../../src/Amane.Pdf.Api/ImageInputInspector.cs)。

層: 単体。判定: SOF/SOS/EOIの構成と固定寸法・拒否。

重複・補完: FromImagesDesign/HTTP/Popplerでmetadata除去と描画を補完。

適用範囲: 意図的に壊したJPEG marker。画素decodeの正しさは判定対象外。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T039 | [JpegHeaderTests.SupportedFrames_ParseThroughScansAndStuffedBytes](../../tests/Amane.Pdf.Api.Tests/JpegHeaderTests.cs#L10) | 3 | baseline/extended/progressive SOFをscan・stuffed byte越しに解析 | 維持 |
| T040 | [JpegHeaderTests.MalformedAndUnsupportedMarkers_AreRejectedAfterFirstSof](../../tests/Amane.Pdf.Api.Tests/JpegHeaderTests.cs#L14) | 1 | SOF後の破損・未対応marker・SOS/EOI欠落・サイズ/画素上限の拒否 | 維持 |
| T041 | [JpegHeaderTests.FirstEoi_EndsPrimaryJpeg_WithoutParsingTrailingGainMapData](../../tests/Amane.Pdf.Api.Tests/JpegHeaderTests.cs#L31) | 1 | 最初のEOIまでを主JPEGとしgainmap等の後続を解析対象外にする | 維持 |
| T042 | [JpegHeaderTests.FinalizationCapacity_IncludesEntryAndUpdateCoexistence_AndRounding](../../tests/Amane.Pdf.Api.Tests/JpegHeaderTests.cs#L39) | 1 | 圧縮最終化のentries/update共存予算・丸め・解放 | 維持 |
| T043 | [JpegHeaderTests.CeilScaleAndPnmWorstCase_UseCheckedIntegerBounds](../../tests/Amane.Pdf.Api.Tests/JpegHeaderTests.cs#L53) | 1 | 縮小ceilとPNM最悪容量の整数境界・予算overflow | 維持 |

## OverlayCapacityTests

本番参照: [OverlayCapacity.cs](../../src/Amane.Pdf.Api/OverlayCapacity.cs), [PdfOverlayBuilder.cs](../../src/Amane.Pdf.Api/PdfOverlayBuilder.cs)。

層: 単体。判定: 固定4KiB会計・overflow・実予約peak。

重複・補完: PdfOverlayResourceTestsは障害経路。overlay-validation.py --capacityが上限fixture。

適用範囲: MeasureRealBuilderは通常も小fixtureを実描画。通常実行だけでは上限負荷の保証なし。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T044 | [OverlayCapacityTests.SentinelAndBlockRounding_AreReservedBeforeWriting](../../tests/Amane.Pdf.Api.Tests/OverlayCapacityTests.cs#L12) | 1 | sentinel付き外部書込と4KiB丸めの事前予約 | 維持 |
| T045 | [OverlayCapacityTests.ManagedWrite_StopsBeforeOverflow_AndReleaseFollowsDeletion](../../tests/Amane.Pdf.Api.Tests/OverlayCapacityTests.cs#L26) | 1 | managed writeの容量前停止・中止・削除後解放 | 維持 |
| T046 | [OverlayCapacityTests.Settings_CannotRaiseMeasuredEnvelope](../../tests/Amane.Pdf.Api.Tests/OverlayCapacityTests.cs#L43) | 1 | 実測済み上限を設定で引上げ不可・最低upload領域 | 維持 |
| T047 | [OverlayCapacityTests.MeasureRealBuilder_Single](../../tests/Amane.Pdf.Api.Tests/OverlayCapacityTests.cs#L55) | 1 | 実builder単独の時間/描画サイズ/削除（通常6頁、外部指定で1000頁） | 維持 |
| T048 | [OverlayCapacityTests.MeasureRealBuilder_Concurrent](../../tests/Amane.Pdf.Api.Tests/OverlayCapacityTests.cs#L59) | 1 | 実builder2同時の時間/描画サイズ/削除（通常6頁、外部指定で1000頁） | 維持 |

## PdfCleanResourceTests

本番参照: [PdfCleanEndpoint.cs](../../src/Amane.Pdf.Api/PdfCleanEndpoint.cs), [PdfEmbeddedFileStripper.cs](../../src/Amane.Pdf.Api/PdfEmbeddedFileStripper.cs), [CleanCapacity.cs](../../src/Amane.Pdf.Api/CleanCapacity.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs)。

層: HTTP・実qpdf・故障注入。判定: PDF構造/marker・固定応答・実PID・argv・上限。

重複・補完: PdfEmbeddedFileStripperTestsはグラフ境界。Python clean-validation.pyは手動大容量。

適用範囲: ContentHashAsyncのUTF-8経由比較と9呼出の名前に弱点。LV09/LV11。実プロセス停止はHTTPで検証。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T049 | [PdfCleanResourceTests.Timeout_CoversWholeJobStopsTreeAndReleasesPermit](../../tests/Amane.Pdf.Api.Tests/PdfCleanResourceTests.cs#L13) | 2 | JSON/clean stage timeoutで親子停止・全job削除・permit返却 | 維持 |
| T050 | [PdfCleanResourceTests.CancellationOrStopping_StopsTreeAndDeletesAllFiles](../../tests/Amane.Pdf.Api.Tests/PdfCleanResourceTests.cs#L30) | 2 | clean中cancel/app停止で親子停止・全ファイル削除 | 維持 |
| T051 | [PdfCleanResourceTests.SharedLimiter_RejectsAllPdfApisWhileCleanRuns_HealthAndNextJobWork](../../tests/Amane.Pdf.Api.Tests/PdfCleanResourceTests.cs#L50) | 1 | clean中の6 API共有permit・healthと後続復旧 | 維持 |

## PdfCleanTests

本番参照: [PdfCleanEndpoint.cs](../../src/Amane.Pdf.Api/PdfCleanEndpoint.cs), [PdfEmbeddedFileStripper.cs](../../src/Amane.Pdf.Api/PdfEmbeddedFileStripper.cs), [CleanCapacity.cs](../../src/Amane.Pdf.Api/CleanCapacity.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs)。

層: HTTP・実qpdf・故障注入。判定: PDF構造/marker・固定応答・実PID・argv・上限。

重複・補完: PdfEmbeddedFileStripperTestsはグラフ境界。Python clean-validation.pyは手動大容量。

適用範囲: LV09/LV11対応済み。全argvとprocess budget、raw bytesの役割/長さ別比較を検証。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T052 | [PdfCleanTests.Clean_RemovesMetadataAllAttachmentPathsAuthorsAndPopups_PreservesPageStreamsAndOrdinaryData](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L42) | 1 | metadata/attachment/author除去、通常構造保持、page streamごととimage multisetのraw byte保存 | LV11：対応済み・維持（改善検証済み） |
| T453 | [PdfCleanTests.BinaryStreamCapture_PreservesInvalidUtf8Bytes](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L118) | 1 | 実qpdf stdoutから不正UTF8 byteを一切変換せず回収する | LV11：対応済み・維持（改善検証済み） |
| T053 | [PdfCleanTests.MultiPageRotationAndOrder_ArePreserved](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L135) | 1 | 複数頁の順序・回転保持 | 維持 |
| T054 | [PdfCleanTests.IncrementalUpdate_DropsOldInfoBytes](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L149) | 1 | incremental update以前のInfo秘密byteを除去 | 維持 |
| T055 | [PdfCleanTests.InvalidOrEncryptedInput_UsesExisting422](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L173) | 5 | 空/非PDF/warning/暗号化入力は共通422 | 維持 |
| T056 | [PdfCleanTests.UnexpectedFieldsQueryDuplicateMissingAndOversize_AreRejectedBeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L188) | 1 | 未知field/query・欠落/重複・過大fileをqpdf前拒否 | 維持 |
| T057 | [PdfCleanTests.ActualLimits_ReturnTooComplex](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L207) | 3 | 実JSON/PDF/job上限はtoo-complex | 維持 |
| T058 | [PdfCleanTests.UnexpectedFailuresAndResiduals_AreSanitized500](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L232) | 12 | 内部tool異常/不正生成物/残留情報/頁数変化は安全な500 | 維持 |
| T059 | [PdfCleanTests.JobAndArguments_UseFixedOptionsPrivatePathsPermissionsAndOutputCleanup](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L260) | 1 | 全argv/private job/permissionsと出力cleanup、process起動予算9回・統合JSONとjobの回数 | LV09：対応済み・維持（改善検証済み） |
| T060 | [PdfCleanTests.FsizeBoundary_RejectsEqualityAndOneBelow_AcceptsOneAbove](../../tests/Amane.Pdf.Api.Tests/PdfCleanTests.cs#L311) | 2 | 実FSIZEの等号飽和/1byte前後の分類 | 維持 |

## PdfCompressResourceTests

本番参照: [PdfCompressEndpoint.cs](../../src/Amane.Pdf.Api/PdfCompressEndpoint.cs), [PdfCompressProcessor.cs](../../src/Amane.Pdf.Api/PdfCompressProcessor.cs), [CompressMetadata.cs](../../src/Amane.Pdf.Api/CompressMetadata.cs), [CompressCapacity.cs](../../src/Amane.Pdf.Api/CompressCapacity.cs)。

層: HTTP・実JPEG/qpdf・故障注入。判定: 再圧縮数・PDF構造・stream・実limits・例外分類。

重複・補完: compress-validation.py --renderは有効ICCと独立Popplerの画質。単体のshape確認と相互補完。

適用範囲: 人工qpdf JSONは障害/内部枝の検証。色空間・fallback回数の主張と観測に差、参照aliasは入口外。LV07/LV10/LV12。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T062 | [PdfCompressResourceTests.PageMetadata_IsReleasedBetweenBatches_AndBeforeImageExtraction](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L18) | 5 | 大量頁metadataをbatch間/抽出前に解放しprocess数を制限 | 維持 |
| T063 | [PdfCompressResourceTests.DeletingPageMetadata_ReleasesSpoolAndCapacityForTheNextBatch](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L45) | 1 | metadata削除でspoolとjob予算を返し次batchに再利用 | 維持 |
| T065 | [PdfCompressResourceTests.MultiPageMetadata_IsFetchedPerLevel_IncludingInheritedResourcesAndImageDependencies](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L78) | 5 | 継承/間接resources/画像依存を階層batchで取得しprocess増加抑制 | 維持 |
| T066 | [PdfCompressResourceTests.SoftDeadlineMustPrecedeHardDeadline](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L101) | 3 | soft deadlineはhard deadline未満 | 維持 |
| T067 | [PdfCompressResourceTests.JpegWithDataAfterEoi_RecompressesPrimaryImage](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L107) | 2 | EOI後の付加dataを無視して主画像だけ再圧縮 | 維持 |
| T068 | [PdfCompressResourceTests.NewJpegReinspection_StillRejectsInvalidBodyDimensionsOrComponents](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L138) | 3 | cjpeg生成物のmarker/寸法/成分不整合を再検査 | 維持 |
| T069 | [PdfCompressResourceTests.ColorSpaceReferences_ResolveSafely_AndRequireMatchingIntegerN](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L164) | 5 | colorspace間接参照・循環・Nの整数/成分整合 | 維持 |
| T070 | [PdfCompressResourceTests.PdfAndJpegDimensionOrComponentMismatch_IsExcluded](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L190) | 2 | PDF宣言とJPEG寸法/成分の不一致を除外 | 維持 |
| T071 | [PdfCompressResourceTests.JobCapacityLimit_SkipsImages_AndIncreasingItProcessesTheSamePdf](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L203) | 1 | job容量不足で画像skipし増枠で同PDFを再圧縮 | 維持 |
| T072 | [PdfCompressResourceTests.BatchStdoutOverflow_FallsBackOncePerImage_AndCleansPartialRaw](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L224) | 1 | batch overflow後の1回/画像fallback・partial raw削除を専用assertで直接検証・最終後始末 | LV10：対応済み・維持（改善検証済み） |
| T073 | [PdfCompressResourceTests.MaxImagesAndJpegAddressSpace_AreAppliedToRealProcessing](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L274) | 1 | 最大画像数と実JPEG AS制限・増枠による回復 | 維持 |
| T074 | [PdfCompressResourceTests.ImageExitCodes_AreDistinctFromFinalFailures](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L302) | 4 | 画像tool終了codeと最終失敗の区別・秘密非露出 | 維持 |
| T075 | [PdfCompressResourceTests.RealAsFsizeAndPrivatePermissions_AreAppliedToJpegCommands](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L318) | 1 | JPEG toolへ実AS/FSIZEとprivate権限を適用 | 維持 |
| T076 | [PdfCompressResourceTests.InputMemoryLimit_KeepsCommon422_AndOutputCheckFailureIs500](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L338) | 1 | 入力memory失敗422と出力check失敗500を区別 | 維持 |
| T077 | [PdfCompressResourceTests.UnexpectedMetadata_Is500](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L358) | 1 | 予期しないmetadata JSONは500 | 維持 |
| T078 | [PdfCompressResourceTests.ExtractedPathMustMatchObjectAndStayInsidePrivateJob](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L373) | 1 | 抽出datafileは対象objectのprivate pathのみ・外部fileを保持 | 維持 |
| T079 | [PdfCompressResourceTests.ApplicationStopping_TerminatesImageTreeAndCleansJob](../../tests/Amane.Pdf.Api.Tests/PdfCompressResourceTests.cs#L393) | 1 | app停止で画像process treeを停止してjob削除 | 維持 |

## PdfCompressTests

本番参照: [PdfCompressEndpoint.cs](../../src/Amane.Pdf.Api/PdfCompressEndpoint.cs), [PdfCompressProcessor.cs](../../src/Amane.Pdf.Api/PdfCompressProcessor.cs), [CompressMetadata.cs](../../src/Amane.Pdf.Api/CompressMetadata.cs), [CompressCapacity.cs](../../src/Amane.Pdf.Api/CompressCapacity.cs)。

層: HTTP・実JPEG/qpdf・故障注入。判定: 再圧縮数・PDF構造・stream・実limits・例外分類。

重複・補完: compress-validation.py --renderは有効ICCと独立Popplerの画質。単体のshape確認と相互補完。

適用範囲: 人工qpdf JSONは障害/内部枝の検証。色空間・fallback回数の主張と観測に差、参照aliasは入口外。LV07/LV10/LV12。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T080 | [PdfCompressTests.RealCompression_PreservesSharedPagesTextVectorsFonts](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L21) | 4 | 実JPEG再圧縮で共有画像・頁・text/vector/font保存と縮小 | 維持 |
| T081 | [PdfCompressTests.CeilScale_NoEnlargement_AndLevelsCanHaveSameResolution](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L52) | 4 | ceil縮小・拡大禁止とlevel間同解像度の実画像寸法 | 維持 |
| T082 | [PdfCompressTests.InvalidQuery_Is400BeforeProcesses](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L74) | 5 | level queryの欠落/重複/未知/表記違反をtool前400 | 維持 |
| T083 | [PdfCompressTests.MultipartAndInputErrors_KeepExistingContract](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L86) | 1 | 圧縮のmultipart・サイズ・空/暗号入力の既存契約 | 維持 |
| T084 | [PdfCompressTests.NoImages_ReturnsZero_AndMayBeLarger](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L101) | 1 | 画像なしの0件ヘッダ・有効PDFと出力増大許容 | 維持 |
| T085 | [PdfCompressTests.ExcludedDictionary_KeepsRawJpeg](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L118) | 3 | Decode/DecodeParms/Mask除外で元JPEG byteを保存 | 維持 |
| T086 | [PdfCompressTests.SupportedColorSpaces_ArePreserved](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L135) | 3 | CalRGB/CalGray/ICCのfamily・WhitePoint・profile属性と全byteを再圧縮後も保存 | LV07：対応済み・維持（改善検証済み） |
| T087 | [PdfCompressTests.ConfiguredImageLimits_UseValidZeroReplacementResult](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L173) | 6 | 各画像予算不足時は0件で有効なPDFへfallback | 維持 |
| T088 | [PdfCompressTests.ImageFailureVersusStartFailure_IsDistinct](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L191) | 2 | 画像処理失敗と実行不能を200/500に区別 | 維持 |
| T089 | [PdfCompressTests.FinalWriteFailure_Is500_WithNoSuccessHeader](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L207) | 3 | 最終PDF書込失敗は500・成功headerなし | 維持 |
| T090 | [PdfCompressTests.SoftDeadlineIncludesInputCheck_StopsImageProcess_AndReturnsPartialResult](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L219) | 1 | 入力checkを含むsoft deadlineで画像処理停止し部分結果 | 維持 |
| T091 | [PdfCompressTests.ImageTimeoutVersusHardTimeout_TerminatesTree](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L238) | 2 | 画像deadlineとhard deadlineで200/504を区別し親子停止 | 維持 |
| T092 | [PdfCompressTests.CancelAndConcurrency_KeepSharedLimiter_AndTerminateTree](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L253) | 1 | 圧縮の共有permitとHTTP cancelで親子停止 | 維持 |
| T093 | [PdfCompressTests.JpegStartupFailure_HasNoBypass](../../tests/Amane.Pdf.Api.Tests/PdfCompressTests.cs#L277) | 3 | JPEG tool/メモリ自己テスト失敗を起動時に拒否 | 維持 |

## PdfEmbeddedFileStripperTests

本番参照: [PdfEmbeddedFileStripper.cs](../../src/Amane.Pdf.Api/PdfEmbeddedFileStripper.cs), [PdfJsonElementIdentityComparer.cs](../../src/Amane.Pdf.Api/PdfJsonElementIdentityComparer.cs)。

層: 単体。判定: 構造/参照graph・容量・保存値と除去値。

重複・補完: PdfCleanTestsが実PDFとqpdf出力の到達性を補完。

適用範囲: 人工JSONで隠れた参照・循環を網羅。実PDF受付を証明するものではない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T094 | [PdfEmbeddedFileStripperTests.Strip_AllowlistAndBothPopupDirections_PreserveOrdinaryAnnotations](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L49) | 2 | 添付注釈allowlist・双方向popup除去と通常注釈保持 | 維持 |
| T095 | [PdfEmbeddedFileStripperTests.AliasesAndSharedAnnots_ResolveSubtypeParentPopupAndNames](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L73) | 1 | alias/共有AnnotsのSubtype/Parent/Popup/Names参照解決 | 維持 |
| T096 | [PdfEmbeddedFileStripperTests.DirectDictionariesAndArrays_RemoveTargetsWithoutNullReplacingOrdinaryDictionaries](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L94) | 1 | direct辞書/配列の対象だけ除去し通常辞書保持 | 維持 |
| T097 | [PdfEmbeddedFileStripperTests.NonTargetWrongTypes_ArePreserved](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L116) | 3 | 非対象で型が不正なNames/Annots値を壊さない | 維持 |
| T098 | [PdfEmbeddedFileStripperTests.StreamDictionaryUpdate_OmitsDataAndRemovesAllThreeKeys](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L128) | 1 | stream更新はdataを再作成せずAF/EF/RFだけ除去 | 維持 |
| T099 | [PdfEmbeddedFileStripperTests.Verify_RejectsResidualKeys](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L144) | 6 | 出力添付関連キーの残留を検出 | 維持 |
| T100 | [PdfEmbeddedFileStripperTests.Verify_RejectsEveryOutputInvariantAndUsesResolvedPopupCounts](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L152) | 1 | attachments/metadata/Info/annotation/stream/popupの出力不変条件 | 維持 |
| T101 | [PdfEmbeddedFileStripperTests.LimitsAndMalformedJson_HaveSpecificFixedExceptions](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L177) | 1 | JSON深さ・破損・参照chain超過とcancelの例外区別 | 維持 |
| T102 | [PdfEmbeddedFileStripperTests.ReferenceCycle_IsTooComplex](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L192) | 1 | 循環参照はtoo-complex | 維持 |
| T103 | [PdfEmbeddedFileStripperTests.ObjectLimit_AcceptsExactBoundaryAndRejectsOneMore](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L205) | 2 | object数50000ぴったり/1件超過のstrip/verify共通境界 | 維持 |
| T104 | [PdfEmbeddedFileStripperTests.IdentityComparer_DistinguishesEqualDirectValuesAndSurvivesGc_WithCopiedInput](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L232) | 1 | 同値direct objectのidentity区別・GC後・別document/既定値 | 維持 |
| T105 | [PdfEmbeddedFileStripperTests.ManyPagesAndAnnotations_StripAndVerifyFinishWithinFiveSeconds](../../tests/Amane.Pdf.Api.Tests/PdfEmbeddedFileStripperTests.cs#L258) | 3 | 大量頁/注釈のstrip+verifyとpopup保持を5秒以内 | 維持 |

## PdfFillTemplateTests

本番参照: [PdfFillTemplateEndpoint.cs](../../src/Amane.Pdf.Api/PdfFillTemplateEndpoint.cs), [FillTemplateFields.cs](../../src/Amane.Pdf.Api/FillTemplateFields.cs), [PdfFormDetector.cs](../../src/Amane.Pdf.Api/PdfFormDetector.cs), [PdfOverlayBuilder.cs](../../src/Amane.Pdf.Api/PdfOverlayBuilder.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs)。

層: HTTP・実pdfcpu/qpdf・故障注入。判定: status/reason・構造・配置・process/cleanup。

重複・補完: Fields/Structure単体の詳細行列とHTTP入口を分担。PythonはPoppler/MuPDF/実署名/上限容量。

適用範囲: LV02対応済み。multipart試験は正常fieldsを使用し、未知text許容・fields解析削除・query拒否削除・順序逆転の変異を検出。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T106 | [PdfFillTemplateTests.Http_StrictFieldsAndMultipart_AreCheckedAfterUploadBeforeTools](../../tests/Amane.Pdf.Api.Tests/PdfFillTemplateTests.cs#L25) | 1 | 不正fieldsのHTTP400と、正常fieldsを伴う各不正multipartの独立拒否・queryの本文未読拒否 | LV02：対応済み・維持（改善検証済み） |
| T107 | [PdfFillTemplateTests.Http_SizeBoundariesAndUploadCompletionPrecedeSemanticChecks](../../tests/Amane.Pdf.Api.Tests/PdfFillTemplateTests.cs#L65) | 1 | fields 64KiB/file境界とupload完了がsemantic検査より優先 | 維持 |
| T108 | [PdfFillTemplateTests.Http_PageRangeOverflowAndPdfErrors_KeepFixedReasons](../../tests/Amane.Pdf.Api.Tests/PdfFillTemplateTests.cs#L92) | 1 | 範囲/overflow/不正PDF/1001頁/未対応boxの固定reason | 維持 |
| T109 | [PdfFillTemplateTests.Http_FormsIncludeEscapedNamesObjectStreamsAndDirectValues_AndPrecedePageCheck](../../tests/Amane.Pdf.Api.Tests/PdfFillTemplateTests.cs#L114) | 1 | escape/object stream/direct/署名markerの拒否を頁数検査より優先 | 維持 |
| T110 | [PdfFillTemplateTests.Http_ExistingOverlayEndpointsKeepFormDetectionDisabled](../../tests/Amane.Pdf.Api.Tests/PdfFillTemplateTests.cs#L149) | 3 | fillだけform拒否し既存watermark/page-number/stampは受理 | 維持 |
| T111 | [PdfFillTemplateTests.Http_ThousandLinesAndCallerLimits_AndExistingWatermarkPercentPolicy](../../tests/Amane.Pdf.Api.Tests/PdfFillTemplateTests.cs#L176) | 1 | 最大1000行とcaller別文字/行制限・watermarkのpercent拒否を維持 | 維持 |
| T112 | [PdfFillTemplateTests.Http_CancellationAndTimeout_WaitForProcessAndCleanup](../../tests/Amane.Pdf.Api.Tests/PdfFillTemplateTests.cs#L215) | 2 | HTTP cancel/timeoutでprocess停止・job削除 | 維持 |
| T113 | [PdfFillTemplateTests.Http_SharedTwoSlotsRejectThirdWith503](../../tests/Amane.Pdf.Api.Tests/PdfFillTemplateTests.cs#L238) | 1 | fill/page-numberの2slot共有と第3要求503 | 維持 |

## PdfFromImagesPrivacyTests

本番参照: [PdfFromImagesEndpoint.cs](../../src/Amane.Pdf.Api/PdfFromImagesEndpoint.cs), [PdfFromImagesProcessor.cs](../../src/Amane.Pdf.Api/PdfFromImagesProcessor.cs), [ImageInputInspector.cs](../../src/Amane.Pdf.Api/ImageInputInspector.cs), [JpegNormalizer.cs](../../src/Amane.Pdf.Api/JpegNormalizer.cs), [PngSanitizer.cs](../../src/Amane.Pdf.Api/PngSanitizer.cs), [FromImagesCapacity.cs](../../src/Amane.Pdf.Api/FromImagesCapacity.cs)。

層: HTTP・実ツール・故障注入。判定: PDF構造/metadata byte・status・process/limits・解放。

重複・補完: Header/Sanitizer単体は入力境界、Pythonは独立画素比較。privacyの後続scan/末尾は通常Exifケースと異なる。

適用範囲: ツールwrapperは現実的なexit/停止の注入。metadata目印がないことと任意秘密情報の不在は同一ではない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T114 | [PdfFromImagesPrivacyTests.GpsDateAndCamera_AreRemovedFromPdfAndEveryExtractedStream](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesPrivacyTests.cs#L12) | 2 | GPS有理数・撮影日・camera・ExifをPDF/全抽出stream/QDF/logから除去 | 維持 |

## PdfFromImagesResourceTests

本番参照: [PdfFromImagesEndpoint.cs](../../src/Amane.Pdf.Api/PdfFromImagesEndpoint.cs), [PdfFromImagesProcessor.cs](../../src/Amane.Pdf.Api/PdfFromImagesProcessor.cs), [ImageInputInspector.cs](../../src/Amane.Pdf.Api/ImageInputInspector.cs), [JpegNormalizer.cs](../../src/Amane.Pdf.Api/JpegNormalizer.cs), [PngSanitizer.cs](../../src/Amane.Pdf.Api/PngSanitizer.cs), [FromImagesCapacity.cs](../../src/Amane.Pdf.Api/FromImagesCapacity.cs)。

層: HTTP・実ツール・故障注入。判定: PDF構造/metadata byte・status・process/limits・解放。

重複・補完: Header/Sanitizer単体は入力境界、Pythonは独立画素比較。privacyの後続scan/末尾は通常Exifケースと異なる。

適用範囲: ツールwrapperは現実的なexit/停止の注入。metadata目印がないことと任意秘密情報の不在は同一ではない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T115 | [PdfFromImagesResourceTests.Timeout_KillsTreeCleansFilesAndReturnsPermit](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesResourceTests.cs#L14) | 2 | 画像PDF check/merge timeoutで親子停止・削除・permit返却 | 維持 |
| T116 | [PdfFromImagesResourceTests.CancellationOrStopping_KillsTreeAndCleansAllIntermediates](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesResourceTests.cs#L32) | 2 | 画像PDF作成のcancel/app停止で全中間物削除 | 維持 |
| T117 | [PdfFromImagesResourceTests.TwoImageJobs_ShareExistingLimiterAndRejectThirdPdfRequest](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesResourceTests.cs#L56) | 1 | standard/originalの2jobが既存PDF APIとpermit共有 | 維持 |

## PdfFromImagesTests

本番参照: [PdfFromImagesEndpoint.cs](../../src/Amane.Pdf.Api/PdfFromImagesEndpoint.cs), [PdfFromImagesProcessor.cs](../../src/Amane.Pdf.Api/PdfFromImagesProcessor.cs), [ImageInputInspector.cs](../../src/Amane.Pdf.Api/ImageInputInspector.cs), [JpegNormalizer.cs](../../src/Amane.Pdf.Api/JpegNormalizer.cs), [PngSanitizer.cs](../../src/Amane.Pdf.Api/PngSanitizer.cs), [FromImagesCapacity.cs](../../src/Amane.Pdf.Api/FromImagesCapacity.cs)。

層: HTTP・実ツール・故障注入。判定: PDF構造/metadata byte・status・process/limits・解放。

重複・補完: Header/Sanitizer単体は入力境界、Pythonは独立画素比較。privacyの後続scan/末尾は通常Exifケースと異なる。

適用範囲: ツールwrapperは現実的なexit/停止の注入。metadata目印がないことと任意秘密情報の不在は同一ではない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T118 | [PdfFromImagesTests.JpegOrientations_AreAppliedForColorAndGrayAndMetadataRemoved](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesTests.cs#L15) | 2 | 全EXIF orientationの色/gray寸法とmetadata除去（pixel方向は別Python検証） | 維持 |
| T119 | [PdfFromImagesTests.MixedInputs_KeepUploadOrderAndAcceptSniffedPngVariants](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesTests.cs#L48) | 1 | JPEG/PNG各種の混在upload順とsniff受理 | 維持 |
| T120 | [PdfFromImagesTests.SixteenBitPng_RejectsInsufficientWorkingBudgetBeforeImport](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesTests.cs#L77) | 4 | 16bit colorの拡張作業予算不足をimport前拒否、8bit対照成功 | 維持 |
| T121 | [PdfFromImagesTests.EightBitColorPng_RejectsBeforeImportWhileOpaqueGray16RemainsAccepted](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesTests.cs#L103) | 4 | 8bit色画像の作業予算不足をimport前拒否、opaque gray16対照成功 | 維持 |
| T122 | [PdfFromImagesTests.Errors_AreFixedProblemsWithoutLeakingMetadataPathsOrToolOutput](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesTests.cs#L123) | 1 | query/part/画像/各容量の固定reasonと秘密抑止 | 維持 |
| T123 | [PdfFromImagesTests.ToolFailures_AreDistinguishedFromInvalidInputAndSuccessfulInvalidOutputs](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesTests.cs#L158) | 1 | jpegtranの入力不適合/起動不能/signal/成功出力欠落を区別 | 維持 |
| T124 | [PdfFromImagesTests.TinyTranspose_IsAcceptedButTrimWithoutAFullMcuIsUnsupported](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesTests.cs#L177) | 1 | 極小JPEG transpose受理とMCU不足trim拒否 | 維持 |
| T125 | [PdfFromImagesTests.UploadDeletionFailure_StopsBeforeImportWithoutReleasingReservation](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesTests.cs#L198) | 1 | 入力削除失敗時は予約を解放せずimport前停止 | 維持 |
| T126 | [PdfFromImagesTests.GeneratedPdfFailure_IsInternalEvenWithAnOversizedPartialFileAndSignalExit](../../tests/Amane.Pdf.Api.Tests/PdfFromImagesTests.cs#L220) | 1 | 生成PDF結合のsignal障害は過大partialでも内部500 | 維持 |

## PdfMergeResourceTests

本番参照: [PdfMergeEndpoint.cs](../../src/Amane.Pdf.Api/PdfMergeEndpoint.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs), [PdfEndpointHelpers.cs](../../src/Amane.Pdf.Api/PdfEndpointHelpers.cs)。

層: HTTP・実qpdf・故障注入。判定: page順序・bytes読取量・予算共有・実PID・固定応答。

重複・補完: 共通resourceテストとは複数upload/入力ごとの検証/合算上限が異なるため維持。

適用範囲: TestServerは実Kestrel socketではない。実cgroupはDocker smoke/容量検証で補完。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T127 | [PdfMergeResourceTests.FileAndTotalSizeBoundaries_AreEnforcedBeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L19) | 4 | 単体と入力合計のぴったり/1byte超過容量境界 | 維持 |
| T128 | [PdfMergeResourceTests.HugeMerge_StopsReadingEarly_WithAndWithoutContentLength](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L36) | 2 | 巨大mergeをContent-Length有無に応じ早期停止 | 維持 |
| T129 | [PdfMergeResourceTests.TotalOverflow_NeverWritesBeyondAggregateLimit](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L55) | 1 | 入力合計上限を超えて実ファイルに書き込まない | 維持 |
| T130 | [PdfMergeResourceTests.RequestEnvelopeOverflow_CountsEpilogue_WithAndWithoutContentLength](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L74) | 2 | MIME前後領域を含むrequest全体上限と事前停止 | 維持 |
| T131 | [PdfMergeResourceTests.Timeout_KillsProcessTree_CleansEveryInput_AndReleasesPermit](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L106) | 3 | 検証/結合各stage timeoutで全入力削除・permit返却 | 維持 |
| T132 | [PdfMergeResourceTests.TimeoutBudget_IsSharedAcrossInputValidations](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L127) | 1 | 複数入力の検証で時間予算を再設定せず共有 | 維持 |
| T133 | [PdfMergeResourceTests.Merge_ReceivesOnlyRemainingTimeoutBudget_AfterAllValidations](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L144) | 1 | 結合stageには検証後の残り時間だけを付与 | 維持 |
| T134 | [PdfMergeResourceTests.CancellationOrApplicationStopping_KillsProcessTree_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L168) | 4 | 検証/結合中のrequest cancelとapp停止で親子停止・削除 | 維持 |
| T135 | [PdfMergeResourceTests.CancelledOrStoppingUpload_CleansPartialInputs](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L196) | 2 | upload中cancel/app停止で部分入力を削除 | 維持 |
| T136 | [PdfMergeResourceTests.MergeWithTenInputs_HoldsOnePermit_SharedWithExistingApis_WithoutQueue](../../tests/Amane.Pdf.Api.Tests/PdfMergeResourceTests.cs#L215) | 1 | 10入力mergeが1permitだけ使い他APIと共有・終了後復旧 | 維持 |

## PdfMergeTests

本番参照: [PdfMergeEndpoint.cs](../../src/Amane.Pdf.Api/PdfMergeEndpoint.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs), [PdfEndpointHelpers.cs](../../src/Amane.Pdf.Api/PdfEndpointHelpers.cs)。

層: HTTP・実qpdf・故障注入。判定: page順序・bytes読取量・予算共有・実PID・固定応答。

重複・補完: 共通resourceテストとは複数upload/入力ごとの検証/合算上限が異なるため維持。

適用範囲: TestServerは実Kestrel socketではない。実cgroupはDocker smoke/容量検証で補完。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T137 | [PdfMergeTests.Merge_ReturnsAllPages_ValidPdf_FixedFilename_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L20) | 3 | 2/3/10入力の全頁結合・固定名・既定上限・秘密非露出・削除 | 維持 |
| T138 | [PdfMergeTests.Merge_PreservesMultipartOrder_AndEveryPageWithinEachInput](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L38) | 1 | multipart順と各入力内のページ順/回転・MIME非依存 | 維持 |
| T139 | [PdfMergeTests.InvalidFileCount_Returns400BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L58) | 4 | 結合の入力数境界違反をqpdf前に拒否 | 維持 |
| T140 | [PdfMergeTests.EleventhPart_IsRejectedWithoutCreatingItsTemporaryFile](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L70) | 1 | 11番目partはファイルを作らず拒否、先行10個のサイズ/権限 | 維持 |
| T141 | [PdfMergeTests.UnexpectedFields_Return400BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L99) | 3 | password/別名file/文字partをqpdf前に拒否 | 維持 |
| T142 | [PdfMergeTests.MalformedMultipart_Returns400](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L112) | 3 | merge malformed multipartの400 | 維持 |
| T143 | [PdfMergeTests.InvalidMiddlePdf_RejectsWholeMerge_AndCleansAllInputs](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L126) | 4 | 中間入力が空/破損/warningなら全結合拒否・全入力削除 | 維持 |
| T144 | [PdfMergeTests.EncryptedMiddlePdf_IsRejectedIncludingEmptyPassword](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L150) | 2 | 中間入力の暗号化PDFを空passwordでも拒否 | 維持 |
| T145 | [PdfMergeTests.ValidationAndMerge_UseOnlyGeneratedPaths_Sequentially](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L165) | 1 | 入力検証の順序・生成pathのみをqpdfに渡す | 維持 |
| T146 | [PdfMergeTests.QpdfFailure_DiscardsStdoutAndStderr_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L200) | 4 | 結合/検証stage異常のcode分類・出力非露出・削除 | 維持 |
| T147 | [PdfMergeTests.InvalidMergeConfiguration_IsRejectedOnStartup](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L223) | 8 | merge設定の範囲と加算overflowを起動時に拒否 | 維持 |
| T148 | [PdfMergeTests.ValidMergeConfiguration_IsApplied_IndependentlyOfSingleFileLimit](../../tests/Amane.Pdf.Api.Tests/PdfMergeTests.cs#L233) | 1 | 単一ファイル上限と独立した結合上限の設定反映 | 維持 |

## PdfOptimizeTests

本番参照: [PdfOptimizeEndpoint.cs](../../src/Amane.Pdf.Api/PdfOptimizeEndpoint.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs), [PdfEndpointHelpers.cs](../../src/Amane.Pdf.Api/PdfEndpointHelpers.cs)。

層: HTTP・実qpdf。判定: page/rotation・許可argv・固定応答。

重複・補完: PdfResourceLimitTestsがtimeout/共有permitを担当。Docker smokeがコンテナ経路。

適用範囲: losslessでも出力増加は許可。byte一致を期待しない現行契約に一致。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T149 | [PdfOptimizeTests.Optimize_ReturnsValidPdfWithSamePages_FixedName_AndAllowsLargerOutput](../../tests/Amane.Pdf.Api.Tests/PdfOptimizeTests.cs#L11) | 1 | 最適化後の有効性・固定filename・小fixtureで入力より大きい出力も許容 | 維持 |
| T150 | [PdfOptimizeTests.Optimize_PreservesPageCountOrderAndRotations_WithUntrustedContentType](../../tests/Amane.Pdf.Api.Tests/PdfOptimizeTests.cs#L27) | 1 | 最適化でページ数/順序/回転と非信頼MIME受理を保持 | 維持 |
| T151 | [PdfOptimizeTests.Optimize_UsesOnlyExpectedLosslessArguments](../../tests/Amane.Pdf.Api.Tests/PdfOptimizeTests.cs#L42) | 1 | 実qpdfへのlossless引数だけを許可し破壊的変換を防ぐ | 維持 |
| T152 | [PdfOptimizeTests.InvalidPdf_Returns422](../../tests/Amane.Pdf.Api.Tests/PdfOptimizeTests.cs#L86) | 4 | optimizeの空/非PDF/破損/warning拒否 | 維持 |
| T153 | [PdfOptimizeTests.EncryptedPdf_Returns422](../../tests/Amane.Pdf.Api.Tests/PdfOptimizeTests.cs#L102) | 1 | optimizeの暗号化PDF拒否 | 維持 |
| T154 | [PdfOptimizeTests.OptimizeFailure_IsSanitizedAndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfOptimizeTests.cs#L116) | 3 | 最適化stage終了codeの422/500・秘密非露出・削除 | 維持 |
| T155 | [PdfOptimizeTests.PasswordAndDuplicateFile_AreRejectedBeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfOptimizeTests.cs#L141) | 1 | file欠落/重複・password指定をtool実行前に拒否 | 維持 |
| T156 | [PdfOptimizeTests.MalformedRequests_Return400BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfOptimizeTests.cs#L159) | 3 | malformed multipartをtool実行前に拒否 | 維持 |

## PdfOverlayBuilderTests

本番参照: [PdfOverlayBuilder.cs](../../src/Amane.Pdf.Api/PdfOverlayBuilder.cs), [PdfPageBoxes.cs](../../src/Amane.Pdf.Api/PdfPageBoxes.cs), [PdfcpuProcessor.cs](../../src/Amane.Pdf.Api/PdfcpuProcessor.cs), [OverlayCapacity.cs](../../src/Amane.Pdf.Api/OverlayCapacity.cs)。

層: 直接builder・実ツール・故障注入。判定: geometry構造・内容/リンク保持・中間物・例外。

重複・補完: 各overlay HTTP endpointが受付/応答を検証。overlay-validation.pyは独立Poppler比較。

適用範囲: Geometry比較の一部で本番PdfPageBoxesを期待値側にも使用。独立Pythonと固定値単体を残す。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T157 | [PdfOverlayBuilderTests.BlankOpacityUsesOnePrivateStreamPerPageAndDefaultHasNoContents](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L11) | 2 | 透明blankの頁専用content/alphaと通常blank・描画後Tj数 | 維持 |
| T158 | [PdfOverlayBuilderTests.InvalidOpacityPrecedesAnyFileOrToolOperation](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L67) | 6 | 不正opacityをファイル/tool/draw前に拒否 | 維持 |
| T159 | [PdfOverlayBuilderTests.Geometry_RealText_AdjustmentPrecedesOverlay_AndRestoresOriginalAttributes](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L77) | 1 | 実合成で全頁属性と元内容保持・中間物削除（描画は外部export検証あり） | 維持 |
| T160 | [PdfOverlayBuilderTests.ComplexFixture_PreservesImageBookmarkLinkAndForm](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L107) | 1 | 実合成で画像hash・bookmark先・link・formを保存 | 維持 |
| T161 | [PdfOverlayBuilderTests.InheritedAttributes_RealTools_PreserveEffectiveValues](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L160) | 1 | 実qpdf/pdfcpuで継承属性の実効値を保存 | 維持 |
| T162 | [PdfOverlayBuilderTests.QpdfOutputMissing_IsInternalFailure_AndIntermediatesAreRemoved](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L176) | 5 | qpdf出力欠落のexit別固定内部例外と中間物削除 | 維持 |
| T163 | [PdfOverlayBuilderTests.PdfcpuNoOutputExitOne_DoesNotInferCapacityFromStderr](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L188) | 1 | pdfcpu stderrだけで容量不足と推定しない | 維持 |
| T164 | [PdfOverlayBuilderTests.InvalidGeneratedOutput_IsFixedInternalFailure](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L206) | 8 | 生成PDF/頁数/JSON/属性破損を固定内部例外へ | 維持 |
| T165 | [PdfOverlayBuilderTests.CleanupFailure_PreservesOriginalFailure_AndAttemptsEveryFile](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L243) | 1 | cleanup自体の障害でも元例外保持し全ファイル削除を試行 | 維持 |
| T166 | [PdfOverlayBuilderTests.CleanupFailure_AfterValidation_DoesNotReturnOutput](../../tests/Amane.Pdf.Api.Tests/PdfOverlayBuilderTests.cs#L262) | 1 | 成功後cleanup障害はoutputを返さず例外 | 維持 |

## PdfOverlayResourceTests

本番参照: [PdfOverlayBuilder.cs](../../src/Amane.Pdf.Api/PdfOverlayBuilder.cs), [PdfEndpointHelpers.cs](../../src/Amane.Pdf.Api/PdfEndpointHelpers.cs), [OverlayCapacity.cs](../../src/Amane.Pdf.Api/OverlayCapacity.cs), [ExternalProcessRunner.cs](../../src/Amane.Pdf.Api/ExternalProcessRunner.cs)。

層: 直接builder/helper・故障注入。判定: 実子孫停止・typed reason・予算。

重複・補完: HTTP PageNumbers/Watermark/Stamp testsが実request timeout/停止の配線を補完。

適用範囲: builderは1tokenの6stage取消を検証。HTTP wiringは別層。中間物削除を所有者Dispose前に観測。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T167 | [PdfOverlayResourceTests.EveryStage_CancellationStopsProcessTree_AndBuilderRemovesIntermediates](../../tests/Amane.Pdf.Api.Tests/PdfOverlayResourceTests.cs#L20) | 6 | 各6stageで子孫停止を待ち、所有者Dispose前にbuilderの全中間物が消える | LV04：対応済み・維持（改善検証済み） |
| T168 | [PdfOverlayResourceTests.Helper_UsesOverlayReasonAndFixedErrors_WithoutEndpoint](../../tests/Amane.Pdf.Api.Tests/PdfOverlayResourceTests.cs#L79) | 3 | helper直呼出しのoverlay reason/500と秘密抑止・job削除 | 維持 |
| T169 | [PdfOverlayResourceTests.ThousandAndOneTargets_AreRejectedBeforeDrawing](../../tests/Amane.Pdf.Api.Tests/PdfOverlayResourceTests.cs#L105) | 1 | 1001対象頁をdraw前に拒否 | 維持 |
| T170 | [PdfOverlayResourceTests.TargetPageAndJsonAndOutputBudgets_RejectProvenOverflow](../../tests/Amane.Pdf.Api.Tests/PdfOverlayResourceTests.cs#L128) | 1 | 対象頁/JSON/外部出力の観測可能な容量超過 | 維持 |

## PdfPageBoxesTests

本番参照: [PdfPageBoxes.cs](../../src/Amane.Pdf.Api/PdfPageBoxes.cs)。

層: 単体。判定: 固定Media/Crop/Rotate/UserUnit値・参照・境界。

重複・補完: OverlayBuilder/Renderer/各HTTP/Pythonは実PDFで補完。

適用範囲: 継承/alias/cycle用人工JSON。復元後の新object IDを使うことが重要。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T171 | [PdfPageBoxesTests.InheritedNullAndIndirectValues_RespectNonInheritedAttributes](../../tests/Amane.Pdf.Api.Tests/PdfPageBoxesTests.cs#L11) | 1 | 継承/null/間接boxと非継承UserUnit/Trim・表示寸法 | 維持 |
| T172 | [PdfPageBoxesTests.UnsupportedGeometry_IsTyped](../../tests/Amane.Pdf.Api.Tests/PdfPageBoxesTests.cs#L37) | 10 | 未対応box/回転/UserUnitを型付き例外で拒否 | 維持 |
| T173 | [PdfPageBoxesTests.Chains_AreBoundedAndDetectCycles](../../tests/Amane.Pdf.Api.Tests/PdfPageBoxesTests.cs#L49) | 4 | 親/参照chainの循環・深さを制限 | 維持 |
| T174 | [PdfPageBoxesTests.Restore_ReevaluatesCurrentParents_AndNeverCopiesOldReferences](../../tests/Amane.Pdf.Api.Tests/PdfPageBoxesTests.cs#L73) | 2 | 合成後の親を再評価し旧参照をコピーせず属性復元 | 維持 |
| T175 | [PdfPageBoxesTests.MetadataLimitsAndMalformedJson_AreDistinct_AndCancelIsObserved](../../tests/Amane.Pdf.Api.Tests/PdfPageBoxesTests.cs#L97) | 1 | metadata byte/件数/深さ・不正JSON・cancelの区別 | 維持 |
| T176 | [PdfPageBoxesTests.ObjectLimit_ExactAndOneMore](../../tests/Amane.Pdf.Api.Tests/PdfPageBoxesTests.cs#L113) | 2 | 50000/75000 object境界 | 維持 |
| T177 | [PdfPageBoxesTests.IndirectLocalValues_RestoreAsNumbers_WithCurrentObjectIds](../../tests/Amane.Pdf.Api.Tests/PdfPageBoxesTests.cs#L124) | 1 | 間接local属性を新object idの数値へ復元 | 維持 |
| T178 | [PdfPageBoxesTests.ChangedParentCropOutsideRestoredMedia_MaterializesOriginalCrop](../../tests/Amane.Pdf.Api.Tests/PdfPageBoxesTests.cs#L147) | 1 | 変更親Cropが復元Media外なら元Cropを明示 | 維持 |

## PdfPageNumbersTests

本番参照: [PdfPageNumbersEndpoint.cs](../../src/Amane.Pdf.Api/PdfPageNumbersEndpoint.cs), [PdfcpuLayer.cs](../../src/Amane.Pdf.Api/PdfcpuLayer.cs), [PdfOverlayBuilder.cs](../../src/Amane.Pdf.Api/PdfOverlayBuilder.cs), [PdfEndpointHelpers.cs](../../src/Amane.Pdf.Api/PdfEndpointHelpers.cs)。

層: HTTP・単体layer・builder。判定: 固定layer値・page構造・HTTP/実PID・tool回数。

重複・補完: page-numbers-validation.pyは抽出文字と表示座標を独立比較。

適用範囲: LV01対応済み。正常multipartと本文未読probeでParse削除・順序逆転を検出。幾何の自己比較はPythonを残して補完。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T179 | [PdfPageNumbersTests.InvalidQueries_Return400BeforeReadingUploadOrRunningQpdf](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L19) | 1 | 正常multipartを伴う不正queryの400・本文読取0回・tool到達前の拒否 | LV01：対応済み・維持（改善検証済み） |
| T180 | [PdfPageNumbersTests.Layer_CountsFromSelectedPage_UsesPhysicalSizesAndInvariantNumbers](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L47) | 3 | 選択先頭からの採番・3書式・物理配置のlayer値 | 維持 |
| T181 | [PdfPageNumbersTests.Range_EnforcesFinalNumberBoundary_WithoutIntegerOverflow](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L61) | 1 | 最終番号99999・from範囲とoverflow | 維持 |
| T182 | [PdfPageNumbersTests.Http_PreservesFirstPageContent_PageCountAndAttributes](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L76) | 3 | HTTP3書式のcover不変・対象描画呼出・頁属性保持（文字内容は別Python） | 維持 |
| T183 | [PdfPageNumbersTests.Http_KeepsBookmarkDestination_FormAndLink](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L101) | 1 | HTTPでbookmark先・form・linkを保持 | 維持 |
| T184 | [PdfPageNumbersTests.Http_PageCountDependentErrors_Return400_And99999IsAccepted](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L126) | 1 | 頁数依存の番号超過と99999受理 | 維持 |
| T185 | [PdfPageNumbersTests.Http_ThousandTargetLimit_CountsOnlyNumberedPages](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L146) | 1 | 1000対象page制限はcoverを含めず計算 | 維持 |
| T186 | [PdfPageNumbersTests.Http_InvalidPdfAndMultipart_AreRejectedWithoutExposure](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L163) | 1 | PDF入力検証を範囲検証より優先・multipart拒否・秘密抑止 | 維持 |
| T187 | [PdfPageNumbersTests.Http_FileSizeBoundary_IsEnforced](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L192) | 1 | page-numberのfileサイズぴったり/超過 | 維持 |
| T188 | [PdfPageNumbersTests.Http_OverlayCapacityAndUnsupportedAttributes_KeepTheirReasons](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L206) | 1 | overlay容量と未対応属性のreason維持 | 維持 |
| T189 | [PdfPageNumbersTests.Http_RunsInputCheckOnce_WithoutSeparatePageCountProbe](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L228) | 1 | 入力check一度・独立頁数probe省略 | 維持 |
| T190 | [PdfPageNumbersTests.Http_ToolFailure_ReturnsFixed500_AndCleansJob](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L248) | 2 | qpdf/pdfcpu消失は固定500・path非露出・削除 | 維持 |
| T191 | [PdfPageNumbersTests.Http_TimeoutAndBusyPermit_CleanUpAndReleaseSharedSlot](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L264) | 1 | timeout/満員時の共有slotと解放 | 維持 |
| T192 | [PdfPageNumbersTests.Http_Cancel_WaitsForProcessTreeAndDeletesJob](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L286) | 1 | HTTP cancelで親子停止・job削除 | 維持 |
| T193 | [PdfPageNumbersTests.Builder_ResolverReceivesCount_AndFailureRemovesIntermediates](../../tests/Amane.Pdf.Api.Tests/PdfPageNumbersTests.cs#L302) | 1 | builder resolverへ頁数を渡し失敗時中間物削除 | 維持 |

## PdfPageSelectionOperationTests

本番参照: [PdfPageSelectionEndpoints.cs](../../src/Amane.Pdf.Api/PdfPageSelectionEndpoints.cs), [PdfPageSelection.cs](../../src/Amane.Pdf.Api/PdfPageSelection.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs)。

層: HTTP・実qpdf。判定: 選択順・残存/消失content・argv・固定応答。

重複・補完: Rotateは角度、Splitは複数output/ZIP。query共通でも実operationの配線差を保持。

適用範囲: 共通selection logicだけで各endpointの差を保証できない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T194 | [PdfPageSelectionOperationTests.Extract_SelectsPagesInRequestedOrder](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L14) | 3 | extractは指定順に頁を選択し有効PDF/固定名を返す | 維持 |
| T195 | [PdfPageSelectionOperationTests.DeletePages_RemovesPagesAndPreservesRemainingOrder](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L35) | 4 | deleteは指定頁を除き残存順序を保持 | 維持 |
| T196 | [PdfPageSelectionOperationTests.DeletePages_RemovesDeletedPageContent](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L52) | 1 | 削除頁の内容markerを出力から除去し残存内容を維持 | 維持 |
| T197 | [PdfPageSelectionOperationTests.Reorder_RequiresAndUsesEveryPageOnce](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L82) | 3 | reorderは全頁一度ずつ指定順に並べる | 維持 |
| T198 | [PdfPageSelectionOperationTests.InvalidOperationSpecificPages_Return400](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L107) | 8 | 範囲外/重複/全削除/欠落pageの操作固有拒否 | 維持 |
| T199 | [PdfPageSelectionOperationTests.MissingOrDuplicatePages_Return400BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L119) | 3 | 3操作のpage query欠落/重複をqpdf前に拒否 | 維持 |
| T200 | [PdfPageSelectionOperationTests.OversizedReorderPages_Returns400BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L129) | 1 | reorderの過長page queryをqpdf前に拒否 | 維持 |
| T201 | [PdfPageSelectionOperationTests.PasswordField_Returns400BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L141) | 3 | 3操作のpassword指定をqpdf前に拒否 | 維持 |
| T202 | [PdfPageSelectionOperationTests.DeletePages_PassesCompactComplementRatherThanRawQueryToQpdf](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L149) | 1 | deleteの補集合をcompact範囲にしてqpdfへ渡す | 維持 |
| T203 | [PdfPageSelectionOperationTests.InvalidWarningAndEncryptedPdf_Return422](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L181) | 3 | 3操作の非PDF/破損/warning/暗号化入力拒否 | 維持 |
| T204 | [PdfPageSelectionOperationTests.PageSelectionFailure_IsSanitizedAndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfPageSelectionOperationTests.cs#L208) | 3 | 選択stageのexit code分類と安全応答・削除 | 維持 |

## PdfProtectTests

本番参照: [PdfProtectEndpoint.cs](../../src/Amane.Pdf.Api/PdfProtectEndpoint.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs), [TemporaryPdfFiles.cs](../../src/Amane.Pdf.Api/TemporaryPdfFiles.cs)。

層: 直接processor/job。判定: 実暗号化/復号・private job・argv・固定応答。

重複・補完: PdfResourceLimitTestsで実request中のcancel/timeout。Validationは受付、Protectは暗号化結果/内部job。

適用範囲: 事前cancelの直接呼出は実処理中の取消ではない。LV06。QpdfJobFailureとFailedQpdfは入口/直接呼出が異なる。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T205 | [PdfProtectTests.Job_UsesPrivateFiles_IndependentOwnerPassword_AndSafeArgv](../../tests/Amane.Pdf.Api.Tests/PdfProtectTests.cs#L12) | 1 | private job/input権限・別owner password・パスワードのargv非露出 | 維持 |
| T206 | [PdfProtectTests.Protect_EncryptsWithAes256_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfProtectTests.cs#L55) | 2 | 実AES256暗号化・正誤password検証・非信頼filename・削除 | 維持 |
| T207 | [PdfProtectTests.FailedQpdf_DoesNotExposeData_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfProtectTests.cs#L80) | 1 | qpdf消失時500・秘密非露出・削除 | 維持 |
| T208 | [PdfProtectTests.PreCancelledJob_DoesNotStartQpdf_AndOwnerDisposalRemovesFiles](../../tests/Amane.Pdf.Api.Tests/PdfProtectTests.cs#L93) | 1 | 事前取消でtoolへ進まず、出力なし・所有者の入力保持とDispose後の削除 | LV06：対応済み・維持（改善検証済み） |

## PdfRedactTests

本番参照: [PdfRedactEndpoint.cs](../../src/Amane.Pdf.Api/PdfRedactEndpoint.cs), [PdfRedactProcessor.cs](../../src/Amane.Pdf.Api/PdfRedactProcessor.cs), [RedactDocument.cs](../../src/Amane.Pdf.Api/RedactDocument.cs), [RedactRaster.cs](../../src/Amane.Pdf.Api/RedactRaster.cs), [PdfRenderer.cs](../../src/Amane.Pdf.Api/PdfRenderer.cs)。

層: HTTP・実PDFium/qpdf・故障注入。判定: 秘密marker/元font消失・独立decode pixel・構造・status・実PID。

重複・補完: Security/Neutral単体・実HTTPとPython独立Popplerを併用。対象外pageからの復活防止は削れない。

適用範囲: marker fixtureで既知の漏れを検知。任意PDFの完全秘匿の数学的証明ではない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T209 | [PdfRedactTests.Redact_RemovesEveryMarkerIncludingAttachmentAppearance_AndKeepsPublicText](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L48) | 1 | 指定ページの秘密マーカー・添付・appearance・文書情報を除去し非対象の公開文字とPDF契約を残す | 維持 |
| T210 | [PdfRedactTests.Redact_SharedEmbeddedJapaneseFont_RasterizesSecondPageAndDropsGlyphData](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L69) | 1 | 共有日本語フォントを持つ非指定ページも画像化し埋込みglyphとToUnicodeを残さない | 維持 |
| T211 | [PdfRedactTests.NeedAppearances_ValueIsGeneratedOnNonTargetPage](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L93) | 1 | 非対象ページのNeedAppearancesフォーム値を外観生成後に保持する | 維持 |
| T212 | [PdfRedactTests.PruningWarning_IsUnsupported_AndNeverSendsPdf](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L113) | 1 | pruning実処理の警告をunsupported-pdfとしPDFを送らない | 維持 |
| T213 | [PdfRedactTests.PruningNonzeroExit_IsUnsupported_EvenWhenPartialOutputReachesFileLimit](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L128) | 2 | pruningが非0終了した場合は部分出力が上限に達していてもunsupported-pdfを優先する | 維持 |
| T214 | [PdfRedactTests.CapacityFailures_HaveRedactReasonsAndNoHeaders](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L146) | 3 | JSON・出力・置換画像容量超過を所定reasonに分類し成功ヘッダを返さない | 維持 |
| T215 | [PdfRedactTests.TooManySharedPages_Is422_AndSingleSmallPageIsUnsupported](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L155) | 1 | 共有フォントによる画像化ページ過多と極小ページをそれぞれ拒否する | 維持 |
| T216 | [PdfRedactTests.BadMultipartQueryRegionsAndPageNumbers_Are400_WithoutToolOrHeaderLeaks](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L170) | 1 | 不正query・multipart・regions・範囲外ページを400にし内部情報や成功ヘッダを出さない | 維持 |
| T217 | [PdfRedactTests.ExactUploadAndTextLimits_AreAccepted_AndFileRequestOverflowIs413](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L196) | 1 | アップロードとregionsの上限ちょうどを許容しファイル・request超過を413にする | 維持 |
| T218 | [PdfRedactTests.OccupiedRenderSlot_Returns503BeforeReadingUpload_AndReleasesAfterFailure](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L211) | 1 | 描画枠占有時はupload解析前に503とし後続失敗後に枠を解放する | 維持 |
| T219 | [PdfRedactTests.ActualRenderer_TimeoutAndCancel_RemoveProcessAndWholeJob](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L225) | 2 | 実Renderer実行中のHTTP期限超過・client取消でプロセス・ジョブを回収し描画枠を戻す | 維持 |
| T220 | [PdfRedactTests.CorruptedFinalUpdate_Is500EvenIfQpdfAcceptsIt](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L258) | 5 | qpdfが受容する最終更新の内容・catalog・image・annotation等の改ざんを500にする | 維持 |
| T221 | [PdfRedactTests.StreamingRequestOverflow_AndEncryptedPdf_AreRejectedAndCleaned](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L286) | 1 | Content-Length不明のrequest超過と暗号化PDFを拒否し一時ファイルを消す | 維持 |
| T222 | [PdfRedactTests.InvalidRedactSettings_PreventStartup](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L309) | 11 | 黒塗り容量設定の不正値を起動時に拒否する | 維持 |
| T223 | [PdfRedactTests.RealEncodedImages_AtMeasuredThreshold_DrawCorrectly_AndLargerDimensionsAre422](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L339) | 5 | 実DCT/JPX/JBIG2画像の上限で描画画素を確認し寸法を1増やすとtoo-complexにする | 維持 |
| T224 | [PdfRedactTests.NonTargetPrivateReferences_CannotRetainOriginalPageContentOrFont](../../tests/Amane.Pdf.Api.Tests/PdfRedactTests.cs#L371) | 5 | 非対象ページのprivate参照から元本文・フォントを復活させず拒否または安全な正規化出力にする | 維持 |

## PdfRendererTests

本番参照: [PdfRenderer.cs](../../src/Amane.Pdf.Api/PdfRenderer.cs), [RenderSlot.cs](../../src/Amane.Pdf.Api/RenderSlot.cs), [Program.cs](../../src/Amane.Pdf.Renderer/Program.cs), [NativeLimits.cs](../../src/Amane.Pdf.Renderer/NativeLimits.cs), [Pdfium.cs](../../src/Amane.Pdf.Renderer/Pdfium.cs), [TurboJpeg.cs](../../src/Amane.Pdf.Renderer/TurboJpeg.cs)。

層: 直接renderer/起動・実PDFium。判定: JPEG pixel・exit code・実limits/PID・silent出力。

重複・補完: PdfRedactTestsはHTTP wiring、renderer-validation.pyはDocker/font配置。

適用範囲: 直接renderer取消は1ケース。native limits/PID/出力を確認し、HTTP timeout/cancel配線はRedact HTTP testsで補完。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T225 | [PdfRendererTests.Slot_HasOneImmediatePermit_AndLeaseCanOnlyReleaseOnce](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L17) | 1 | 描画枠が即時取得の1permitで二重Disposeしても余分な枠を解放しない | 維持 |
| T226 | [PdfRendererTests.KnownExitCodes_HaveReasonsForRedact](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L35) | 5 | 既知Renderer終了コードを黒塗りAPIの所定reasonへ変換する | 維持 |
| T227 | [PdfRendererTests.OtherExitCodes_AreInternalFailures](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L45) | 9 | その他のRenderer終了コードを内部失敗にする | 維持 |
| T228 | [PdfRendererTests.RealRenderer_RotationCropIntersectionUserUnit_AndPixelFills](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L51) | 6 | 実PDFiumで回転・crop交差・UserUnitを反映し黒塗り内外の画素と寸法を守る | 維持 |
| T229 | [PdfRendererTests.RealRenderer_SizeMismatchOverflowAndExistingOutput_DoNotWrite](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L87) | 1 | 寸法不一致・出力予算不足・既存出力時に書かず許容誤差の境界を守る | 維持 |
| T230 | [PdfRendererTests.RealRenderer_NativeFailures_ReturnDocumentPageBitmapAndJpegCodes](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L122) | 1 | 実Rendererの文書・ページ・bitmap・JPEG失敗を所定コードにする | 維持 |
| T231 | [PdfRendererTests.RealRenderer_InvalidArguments_AreSilentAndLeaveNoFile](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L149) | 18 | 不正CLI引数を無出力・ファイル作成なしで終了2にする | 維持 |
| T232 | [PdfRendererTests.RealRenderer_DuplicateMissingAndExcessFills_AreRejected](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L159) | 1 | 重複引数・fill不足・fill101個を拒否し100個を受け入れる | 維持 |
| T233 | [PdfRendererTests.SelfTest_RequiresJapaneseFont_AndCreatesNoFiles](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L171) | 1 | Renderer自己診断が日本語フォントを要求しファイルを作成しない | 維持 |
| T234 | [PdfRendererTests.Cancellation_KillsRealRenderer_WithExactLimits_AndRemovesOutput](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L186) | 1 | 実PDFiumの正確なnative limits/環境、取消後PID回収と出力不在 | LV05：対応済み・維持（改善検証済み） |
| T235 | [PdfRendererTests.EmptyFontDirectory_PreventsApiStartup_WithOnlyFixedRendererLog](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L235) | 1 | 空フォントディレクトリでAPI起動を止め固定Rendererログだけを出す | 維持 |
| T236 | [PdfRendererTests.InvalidSettings_PreventStartup](../../tests/Amane.Pdf.Api.Tests/PdfRendererTests.cs#L255) | 6 | Renderer設定の不正値で起動を止める | 維持 |

## PdfResourceLimitTests

本番参照: [PdfEndpointHelpers.cs](../../src/Amane.Pdf.Api/PdfEndpointHelpers.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs), [Program.cs](../../src/Amane.Pdf.Api/Program.cs), [ExternalProcessRunner.cs](../../src/Amane.Pdf.Api/ExternalProcessRunner.cs)。

層: HTTP・実子プロセス・故障注入。判定: 読取byte量・実PID・共有permit・固定status・削除。

重複・補完: endpointごとのroute登録/handler配線が必要。Merge/Unlock等は固有stageと別予算。

適用範囲: Artificial blocking processは停止を決定的に観測するため。実PDF変換負荷はDocker測定で別確認。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T237 | [PdfResourceLimitTests.FileOnlyEndpointSizeBoundary_IsEnforcedBeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L18) | 4 | rotate/optimizeのファイル容量ぴったり受理と1byte超過413 | 維持 |
| T238 | [PdfResourceLimitTests.FileSizeBoundary_IsEnforcedBeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L34) | 2 | protectのファイル容量ぴったり受理と1byte超過413 | 維持 |
| T239 | [PdfResourceLimitTests.HugeBody_StopsReadingEarly_WithOrWithoutContentLength](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L50) | 2 | 巨大uploadのContent-Length有無による早期停止と読込量 | 維持 |
| T240 | [PdfResourceLimitTests.AdvertisedRequestAboveEnvelopeLimit_IsRejectedBeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L72) | 1 | protectの広告されたrequest全体容量超過を早期413 | 維持 |
| T241 | [PdfResourceLimitTests.ActualRequestBytes_IncludeMetadata_WhenContentLengthIsMissing](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L87) | 1 | Content-LengthなしでもMIME metadata/epilogueを容量に算入 | 維持 |
| T242 | [PdfResourceLimitTests.Timeout_KillsProcessTree_CleansFiles_AndReleasesPermit](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L118) | 3 | protect各stage timeoutの親子停止・削除・permit返却 | 維持 |
| T243 | [PdfResourceLimitTests.RotateTimeout_KillsPageCountOrRotateProcess_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L147) | 2 | rotateページ数取得/回転stage timeoutの停止と削除 | 維持 |
| T244 | [PdfResourceLimitTests.PageSelectionTimeout_KillsProcessTree_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L166) | 1 | extractのpage選択stage timeoutの停止と削除 | 維持 |
| T245 | [PdfResourceLimitTests.OptimizeTimeout_KillsProcessTree_CleansFiles_AndReleasesPermit](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L184) | 1 | optimize timeoutの停止・削除・permit返却 | 維持 |
| T246 | [PdfResourceLimitTests.ConcurrencyLimit_RejectsThirdRequest_WithoutQueue_AndKeepsHealthAvailable](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L208) | 1 | 2処理中の第3要求503・queueなし・health応答 | 維持 |
| T247 | [PdfResourceLimitTests.ProtectAndRotate_ShareConcurrencyLimiter](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L240) | 1 | protect/rotate間の共有permit | 維持 |
| T248 | [PdfResourceLimitTests.PageSelectionEndpoints_ShareConcurrencyLimiter](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L269) | 1 | extract/delete/reorderの共有permit参加 | 維持 |
| T249 | [PdfResourceLimitTests.Optimize_SharesConcurrencyLimiter](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L305) | 1 | optimizeがprotectとpermitを共有 | 維持 |
| T250 | [PdfResourceLimitTests.OptimizeCancellation_KillsProcessTree_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L334) | 1 | optimizeの実行中HTTP cancelで親子停止・削除 | 維持 |
| T251 | [PdfResourceLimitTests.RotateCancellationDuringPageCount_KillsProcessTree_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L356) | 1 | rotateページ数取得中のHTTP cancelで停止・削除 | 維持 |
| T252 | [PdfResourceLimitTests.PageSelectionCancellation_KillsProcessTree_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L378) | 1 | extractのHTTP cancelで停止・削除 | 維持 |
| T253 | [PdfResourceLimitTests.ClientCancellation_KillsProcessTree_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L400) | 1 | protectのHTTP cancelで停止・削除 | 維持 |
| T254 | [PdfResourceLimitTests.CancelledUpload_CleansPartialFile](../../tests/Amane.Pdf.Api.Tests/PdfResourceLimitTests.cs#L422) | 1 | upload途中cancelで部分ファイル削除 | 維持 |

## PdfRestrictTests

本番参照: [PdfRestrictEndpoint.cs](../../src/Amane.Pdf.Api/PdfRestrictEndpoint.cs), [PdfRestriction.cs](../../src/Amane.Pdf.Api/PdfRestriction.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs)。

層: HTTP・直接job・実qpdf。判定: qpdf permissions/復号・UTF-8契約・argv/limits・status。

重複・補完: Protect/Unlockとは暗号化目的・owner/user passwordと権限が異なるので維持。

適用範囲: パスワードを加工しない境界と情報非露出を明示的に守る。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T255 | [PdfRestrictTests.Restrict_MapsPermissions_PreservesPagesAndContent_WithoutOpenPassword](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L57) | 5 | 印刷/抽出/編集権限とAES256・owner認証・頁/stream保存 | 維持 |
| T256 | [PdfRestrictTests.Restrict_AuthenticatesDistinctUtf8Passwords_WithoutTrimmingOrNormalization](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L108) | 4 | UTF-8・空白・正規化差を持つ別passwordの正誤認証 | 維持 |
| T257 | [PdfRestrictTests.MissingRequiredField_Is400](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L133) | 5 | 必須field欠落はtool前400 | 維持 |
| T258 | [PdfRestrictTests.DuplicateOrUnknownField_Is400](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L150) | 7 | 各field重複/未知fieldは400 | 維持 |
| T259 | [PdfRestrictTests.InvalidField_Is400](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L178) | 16 | permission表記・空/制御password・同一user/owner拒否 | 維持 |
| T260 | [PdfRestrictTests.InvalidPasswordBytes_Is400](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L193) | 4 | owner/user双方の不正UTF-8・byte上限拒否 | 維持 |
| T261 | [PdfRestrictTests.WrongPartKind_Is400](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L210) | 6 | fileと文字fieldのpart種別厳格化 | 維持 |
| T262 | [PdfRestrictTests.AllAllowedAndQuery_Are400BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L227) | 1 | 全許可の無意味な制限とquery指定をqpdf前に拒否 | 維持 |
| T263 | [PdfRestrictTests.InvalidOrEncryptedPdf_IsCommon422](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L246) | 5 | 空/壊れ/warning/暗号化入力は共通422 | 維持 |
| T264 | [PdfRestrictTests.OversizedFileAndEpilogue_Are413](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L274) | 1 | 単体ファイルとepilogueのサイズ超過413 | 維持 |
| T265 | [PdfRestrictTests.Password127BytesAndFileAtLimit_AreAccepted](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L291) | 1 | 127byte user/ownerとぴったりfileの受理 | 維持 |
| T266 | [PdfRestrictTests.EpilogueWithoutContentLength_IsCountedBeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L303) | 1 | Content-Lengthなしのepilogue算入とqpdf前停止 | 維持 |
| T267 | [PdfRestrictTests.Cancellation_StopsProcessTreeAndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L326) | 1 | restrictのHTTP cancelで親子停止・削除・秘密非露出 | 維持 |
| T268 | [PdfRestrictTests.Job_UsesPrivateFiles_ExplicitPermissions_AndSafeArgv](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L356) | 1 | private job/argv・明示的permissionとToString秘密抑止 | 維持 |
| T269 | [PdfRestrictTests.FailureAndTimeout_KeepCommonResponsesAndCleanFiles](../../tests/Amane.Pdf.Api.Tests/PdfRestrictTests.cs#L395) | 2 | restrictのtool消失/timeoutの共通応答と削除 | 維持 |

## PdfRotateTests

本番参照: [PdfRotateEndpoint.cs](../../src/Amane.Pdf.Api/PdfRotateEndpoint.cs), [PdfPageSelection.cs](../../src/Amane.Pdf.Api/PdfPageSelection.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs)。

層: HTTP・実qpdf。判定: 固定rotation/page順・未読body・argv/回数・status。

重複・補完: PageSelectionはページ集合の別操作。DuplicateOrOversizedPagesQueryはLV01の改善見本。

適用範囲: 小fixture中心。巨大ページ番号は展開を防ぐ境界であり巨大実PDFは不要。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T270 | [PdfRotateTests.Angle_RotatesAllPages_AndReturnsFixedFileName](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L18) | 3 | 90/180/270度回転と固定filename | 維持 |
| T271 | [PdfRotateTests.Pages_RotatesOnlySelectedPages](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L37) | 6 | 指定page集合だけ回転し非対象を保持 | 維持 |
| T272 | [PdfRotateTests.Rotation_IsRelative_ForRepeatedAndInitiallyRotatedPdf](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L52) | 1 | 既存回転へ相対角度を加算 | 維持 |
| T273 | [PdfRotateTests.InvalidAngle_Returns400_BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L84) | 8 | 不正angleをqpdf前に拒否 | 維持 |
| T274 | [PdfRotateTests.MissingOrDuplicateAngle_Returns400](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L92) | 1 | angle欠落/重複を拒否 | 維持 |
| T275 | [PdfRotateTests.InvalidPages_Returns400_BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L114) | 12 | 不正page構文・重複・空集合をqpdf前に拒否 | 維持 |
| T276 | [PdfRotateTests.DuplicateOrOversizedPagesQuery_Returns400_BeforeReadingBody](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L122) | 1 | page query重複/過長はbodyも読まず拒否 | 維持 |
| T277 | [PdfRotateTests.PageOutsideDocument_Returns400](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L143) | 1 | PDFの頁数を超えた指定を拒否 | 維持 |
| T278 | [PdfRotateTests.HugeOrOverflowingPageNumber_Returns400_WithoutExpandingRange](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L153) | 2 | 巨大範囲/整数overflowを展開せず拒否 | 維持 |
| T279 | [PdfRotateTests.PagesOmitted_SkipsPageCount_WhilePagesSpecifiedUsesIt](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L163) | 1 | 全頁回転では頁数process省略・指定時だけ取得 | 維持 |
| T280 | [PdfRotateTests.OversizedPageCountOutput_IsRejectedWithoutDisclosure](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L192) | 1 | 過長な頁数stdoutは安全な500 | 維持 |
| T281 | [PdfRotateTests.InvalidPdf_Returns422](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L219) | 4 | rotateの空/非PDF/破損/warning拒否 | 維持 |
| T282 | [PdfRotateTests.EncryptedPdf_Returns422](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L235) | 1 | rotateの暗号化入力拒否 | 維持 |
| T283 | [PdfRotateTests.RotateFailure_IsSanitized_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L249) | 3 | 回転stage終了code分類・出力非露出・削除 | 維持 |
| T284 | [PdfRotateTests.PasswordAndDuplicateFile_AreRejected](../../tests/Amane.Pdf.Api.Tests/PdfRotateTests.cs#L272) | 1 | rotateのfile欠落/重複・password拒否 | 維持 |

## PdfSplitCapacityTests

本番参照: [PdfSplitCapacity.cs](../../src/Amane.Pdf.Api/PdfSplitCapacity.cs), [PdfSplitProcessor.cs](../../src/Amane.Pdf.Api/PdfSplitProcessor.cs)。

層: 単体。判定: 固定byte/4KiB丸め・sentinel・順序・overflow。

重複・補完: ZipTestsは実stream書込、SplitTestsはHTTP/realFSIZE。

適用範囲: 会計上の境界。本番OSの実FSIZEは別テストで補完。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T285 | [PdfSplitCapacityTests.PartOrder_IncludesTheCopyAndAllocationBoundary](../../tests/Amane.Pdf.Api.Tests/PdfSplitCapacityTests.cs#L14) | 5 | 分割順によるpart/ZIP共存容量・4KiB境界 | 維持 |
| T286 | [PdfSplitCapacityTests.PdfAndZipLimits_AcceptEqualityAndRejectOneExtraByte](../../tests/Amane.Pdf.Api.Tests/PdfSplitCapacityTests.cs#L35) | 1 | PDF合計とZIP上限ぴったり受理・1byte超過拒否 | 維持 |
| T287 | [PdfSplitCapacityTests.JobLimit_RoundsAllThreeFilesAndReservesOverflowSentinel](../../tests/Amane.Pdf.Api.Tests/PdfSplitCapacityTests.cs#L46) | 1 | job予算がinput/part/ZIPの丸めとsentinelを予約 | 維持 |
| T288 | [PdfSplitCapacityTests.StartupValidation_IncludesZip32AndOverflow](../../tests/Amane.Pdf.Api.Tests/PdfSplitCapacityTests.cs#L60) | 1 | ZIP32・part数・設定overflowと最低予算の起動検証 | 維持 |

## PdfSplitFileNameTests

本番参照: [PdfSplitFileName.cs](../../src/Amane.Pdf.Api/PdfSplitFileName.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs), [PdfSplitZipStream.cs](../../src/Amane.Pdf.Api/PdfSplitZipStream.cs), [PdfSplitProcessor.cs](../../src/Amane.Pdf.Api/PdfSplitProcessor.cs)。

層: 単体・HTTP実ZIP。判定: 固定Unicode/UTF8値・実ZIP entry/flags・path/log。

重複・補完: SplitTestsのページ内容とZipTestsのwrite障害を補完。500分割のみコスト見直し候補LV13。

適用範囲: 最大500entryは本番WriteZipAsyncの直接検証（qpdf抽出/検査のみ固定fixture）。HTTP header→ZIPの20行列は維持。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T289 | [PdfSplitFileNameTests.BaseName_UsesPortableSafeNames](../../tests/Amane.Pdf.Api.Tests/PdfSplitFileNameTests.cs#L38) | 24 | path・危険文字・Windows予約名・空名をportableなbasenameへ変換 | 維持 |
| T290 | [PdfSplitFileNameTests.UnicodeLimit_PreservesScalarsAndSuffixAndRechecksTrailingCharacters](../../tests/Amane.Pdf.Api.Tests/PdfSplitFileNameTests.cs#L42) | 1 | UTF-8 byte上限でscalarを壊さずsuffix/予約名/末尾再検証 | 維持 |
| T291 | [PdfSplitFileNameTests.MultipartNames_AppearSafelyInActualZip](../../tests/Amane.Pdf.Api.Tests/PdfSplitFileNameTests.cs#L89) | 20 | multipart filenameの各encoding/escapeを実ZIP名へ安全反映 | 維持 |
| T292 | [PdfSplitFileNameTests.BrowserRawUtf8Filename_UsesJapaneseNamesAndUtf8ZipFlag](../../tests/Amane.Pdf.Api.Tests/PdfSplitFileNameTests.cs#L106) | 1 | browserのraw UTF-8 filenameとZIP UTF-8 flag・ログ非露出 | 維持 |
| T293 | [PdfSplitFileNameTests.MissingUsableFilename_KeepsExisting400](../../tests/Amane.Pdf.Api.Tests/PdfSplitFileNameTests.cs#L130) | 2 | 利用可能なfilenameのないpartは既存400 | 維持 |
| T294 | [PdfSplitFileNameTests.MaximumUnicodeNameAnd500Entries_FitExistingZipOverhead](../../tests/Amane.Pdf.Api.Tests/PdfSplitFileNameTests.cs#L138) | 1 | 本番WriteZipAsyncで最大Unicode名×500entryを生成し、内容・UTF8 flag・overhead・part削除を確認 | LV13：対応済み・維持（改善検証済み） |
| T295 | [PdfSplitFileNameTests.OriginalName_StaysOutOfPathsCommandsLogsAndFailures](../../tests/Amane.Pdf.Api.Tests/PdfSplitFileNameTests.cs#L191) | 1 | 元filenameを内部path/argv/log/失敗応答に出さない | 維持 |

## PdfSplitPlanTests

本番参照: [PdfSplitPlan.cs](../../src/Amane.Pdf.Api/PdfSplitPlan.cs)。

層: 単体。判定: 固定range/順序・上限/overflow・例外。

重複・補完: SplitTestsは実PDFの頁数とplan適用。

適用範囲: int.MaxValueは展開せず算術境界を確認。網羅値のHTTP反復は不要。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T296 | [PdfSplitPlanTests.Every_UsesShortFinalPartAndInvariantNames](../../tests/Amane.Pdf.Api.Tests/PdfSplitPlanTests.cs#L13) | 1 | everyの短い最終part・rangeと固定命名 | 維持 |
| T297 | [PdfSplitPlanTests.Ranges_PreservesOrderAndOmittedPages](../../tests/Amane.Pdf.Api.Tests/PdfSplitPlanTests.cs#L23) | 1 | rangesの指定順と省略pageを保持 | 維持 |
| T298 | [PdfSplitPlanTests.PartCount_IsBoundedBeforeAllocating](../../tests/Amane.Pdf.Api.Tests/PdfSplitPlanTests.cs#L35) | 7 | part数上限を巨大配列確保前に判定 | 維持 |
| T299 | [PdfSplitPlanTests.MaximumPageNumber_DoesNotOverflow](../../tests/Amane.Pdf.Api.Tests/PdfSplitPlanTests.cs#L43) | 1 | int最大page数の計算overflowと500part上限 | 維持 |
| T300 | [PdfSplitPlanTests.InvalidSyntax_IsRejected](../../tests/Amane.Pdf.Api.Tests/PdfSplitPlanTests.cs#L71) | 18 | every/rangesの互斥・重複・不正数値/範囲構文拒否 | 維持 |
| T301 | [PdfSplitPlanTests.Ranges_CountLengthAndPageBoundsAreChecked](../../tests/Amane.Pdf.Api.Tests/PdfSplitPlanTests.cs#L74) | 1 | ranges個数/長さ/頁数とeveryのpart数拒否 | 維持 |

## PdfSplitResourceTests

本番参照: [PdfSplitEndpoint.cs](../../src/Amane.Pdf.Api/PdfSplitEndpoint.cs), [PdfSplitProcessor.cs](../../src/Amane.Pdf.Api/PdfSplitProcessor.cs), [PdfSplitPlan.cs](../../src/Amane.Pdf.Api/PdfSplitPlan.cs), [PdfSplitZipStream.cs](../../src/Amane.Pdf.Api/PdfSplitZipStream.cs), [PdfSplitCapacity.cs](../../src/Amane.Pdf.Api/PdfSplitCapacity.cs)。

層: HTTP・実qpdf/ZIP・故障注入。判定: ZIP再読込・抽出page・realFSIZE/limits・deadline/permit。

重複・補完: Plan/Capacity/Zip/FileName単体は内部境界。本群は複数生成と返却の一貫性。

適用範囲: qpdf exit単独から容量超過を推定しない境界は保持。stageごとの時間共有も必要。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T302 | [PdfSplitResourceTests.Timeout_StopsProcessTreeCleansJobAndReleasesPermit](../../tests/Amane.Pdf.Api.Tests/PdfSplitResourceTests.cs#L11) | 1 | split timeoutの親子停止・job削除・permit返却 | 維持 |
| T303 | [PdfSplitResourceTests.CancellationOrStopping_CleansJobAndStopsProcessTree](../../tests/Amane.Pdf.Api.Tests/PdfSplitResourceTests.cs#L29) | 2 | split中cancel/app停止の親子停止・job削除 | 維持 |
| T304 | [PdfSplitResourceTests.Split_SharesLimiterWithCompressMergeUnlockAndKeepsHealthAvailable](../../tests/Amane.Pdf.Api.Tests/PdfSplitResourceTests.cs#L50) | 1 | splitとcompress/merge/unlock共有permit・health・復旧 | 維持 |
| T305 | [PdfSplitResourceTests.AllParts_ShareOneDeadline](../../tests/Amane.Pdf.Api.Tests/PdfSplitResourceTests.cs#L80) | 1 | 各partの処理でdeadlineを再設定しない | 維持 |

## PdfSplitTests

本番参照: [PdfSplitEndpoint.cs](../../src/Amane.Pdf.Api/PdfSplitEndpoint.cs), [PdfSplitProcessor.cs](../../src/Amane.Pdf.Api/PdfSplitProcessor.cs), [PdfSplitPlan.cs](../../src/Amane.Pdf.Api/PdfSplitPlan.cs), [PdfSplitZipStream.cs](../../src/Amane.Pdf.Api/PdfSplitZipStream.cs), [PdfSplitCapacity.cs](../../src/Amane.Pdf.Api/PdfSplitCapacity.cs)。

層: HTTP・実qpdf/ZIP・故障注入。判定: ZIP再読込・抽出page・realFSIZE/limits・deadline/permit。

重複・補完: Plan/Capacity/Zip/FileName単体は内部境界。本群は複数生成と返却の一貫性。

適用範囲: qpdf exit単独から容量超過を推定しない境界は保持。stageごとの時間共有も必要。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T306 | [PdfSplitTests.Split_ReturnsCompleteStoredZipWithRequestedPagesAndNames](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L19) | 3 | 完全な無圧縮ZIPの指定頁/順序/名・有効PDF・DOS固定時刻 | 維持 |
| T307 | [PdfSplitTests.JapaneseNames_PreserveRequestedPagesAndOrder](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L50) | 3 | 日本語ZIP entry名・UTF-8 flagと複数範囲の頁順 | 維持 |
| T308 | [PdfSplitTests.BadQuery_Returns400BeforeUploadAndQpdf](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L102) | 7 | 不正split queryの400とtool前拒否（body未読は観測していない） | 維持 |
| T309 | [PdfSplitTests.InvalidPageCount_Returns400](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L113) | 3 | 頁数取得後の分割不可/範囲外を400拒否 | 維持 |
| T310 | [PdfSplitTests.MaximumPartsIsAppliedAfterCountingPages](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L121) | 1 | 設定MaxSplitPartsを頁数確定後にも適用 | 維持 |
| T311 | [PdfSplitTests.MultipartRejectsPasswordAdditionalFieldAndMultipleFiles](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L130) | 1 | password/余分field/複数file/欠落fileを拒否 | 維持 |
| T312 | [PdfSplitTests.InputFailuresKeepCommon422WithoutReason](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L149) | 1 | 空/非PDF/破損/warning/暗号入力は共通reasonなし422 | 維持 |
| T313 | [PdfSplitTests.FileLimit_Returns413BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L163) | 1 | split入力サイズ超過はqpdf前413 | 維持 |
| T314 | [PdfSplitTests.RealFsizeAndSizeComparison_CoverExactOneByteAndLargeOverflow](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L172) | 1 | 実FSIZEでPDF合計ぴったり・1byte/大幅超過を区別 | 維持 |
| T315 | [PdfSplitTests.SplitExitWithoutOutput_IsNotMisclassifiedAsCapacity](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L197) | 4 | 出力なしexitを容量不足と誤分類しない | 維持 |
| T316 | [PdfSplitTests.RealPrlimitMissingOrNonExecutablePath_Is500](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L211) | 2 | 実prlimit下の不存在/非実行qpdfは500 | 維持 |
| T317 | [PdfSplitTests.InvalidGeneratedPart_Is500](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L230) | 3 | 生成partの破損/暗号残留/warningは500 | 維持 |
| T318 | [PdfSplitTests.JobBudgetExhaustion_DoesNotStartTheNextPart](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L252) | 1 | job予算不足で次partのtoolを開始しない | 維持 |
| T319 | [PdfSplitTests.SplitCommands_UsePrivateFilesCanonicalRangesAndRealLimits](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L268) | 1 | qpdf分割の正規range/private権限/AS/FSIZE/JPEGMEM | 維持 |
| T320 | [PdfSplitTests.SplitLaunchFailure_PrecedesEvenAnOversizedExistingOutput](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L289) | 2 | 起動不能の分類は既存過大出力より優先 | 維持 |
| T321 | [PdfSplitTests.PageCountParsing_AcceptsIntMaxValueBeforePlanRejectsTooManyParts](../../tests/Amane.Pdf.Api.Tests/PdfSplitTests.cs#L308) | 1 | 頁数stdoutのint.MaxValueを受理（plan拒否はこのmethod外） | 維持 |

## PdfSplitZipTests

本番参照: [PdfSplitZipStream.cs](../../src/Amane.Pdf.Api/PdfSplitZipStream.cs), [PdfSplitProcessor.cs](../../src/Amane.Pdf.Api/PdfSplitProcessor.cs), [PdfSplitCapacity.cs](../../src/Amane.Pdf.Api/PdfSplitCapacity.cs)。

層: 単体stream・故障注入。判定: 全write overload・fault stream・元例外・長さ。

重複・補完: HTTPの正常ZIPだけではclose/finalize失敗や上書き容量の枝を通せない。

適用範囲: FaultStreamは意図的なI/O失敗のモデル。OS/disk故障全種の再現ではない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T322 | [PdfSplitZipTests.Abort_RejectsEveryMutationWithoutTouchingUnderlyingStream](../../tests/Amane.Pdf.Api.Tests/PdfSplitZipTests.cs#L24) | 1 | abort後の全mutationを基底streamに届かせない | 維持 |
| T323 | [PdfSplitZipTests.Cancellation_PrecedesCapacityForEveryMutation](../../tests/Amane.Pdf.Api.Tests/PdfSplitZipTests.cs#L36) | 1 | 全mutationでキャンセルを容量判定より優先 | 維持 |
| T324 | [PdfSplitZipTests.EveryWriteOverloadAndSetLength_EnforcesCapacityBeforeWriting](../../tests/Amane.Pdf.Api.Tests/PdfSplitZipTests.cs#L48) | 1 | 全write overload/SetLengthで容量超過前に停止 | 維持 |
| T325 | [PdfSplitZipTests.HeaderRewrites_UseFileLengthInsteadOfCumulativeWrites](../../tests/Amane.Pdf.Api.Tests/PdfSplitZipTests.cs#L61) | 1 | ZIP header再書込は累積byteでなく実長で計上 | 維持 |
| T326 | [PdfSplitZipTests.ZipFailures_PreservePrimaryExceptionCloseHandlesAndNeverFinalizeAgain](../../tests/Amane.Pdf.Api.Tests/PdfSplitZipTests.cs#L78) | 5 | ZIP write/seek/central/close障害で一次例外保持・handle解放・二重finalize防止 | 維持 |
| T327 | [PdfSplitZipTests.ZipOverheadFailure_IsCapacityFailureAndClosesTheStream](../../tests/Amane.Pdf.Api.Tests/PdfSplitZipTests.cs#L99) | 1 | ZIP overhead超過は容量例外となりstreamも閉じる | 維持 |
| T328 | [PdfSplitZipTests.CancelledCopyOrFinalization_PreservesCancellationDespiteCloseFailure](../../tests/Amane.Pdf.Api.Tests/PdfSplitZipTests.cs#L116) | 3 | copy/finalize中cancelとclose障害が競合してもcancelを保持 | 維持 |

## PdfStampImageTests

本番参照: [PdfStampImageEndpoint.cs](../../src/Amane.Pdf.Api/PdfStampImageEndpoint.cs), [StampImageInput.cs](../../src/Amane.Pdf.Api/StampImageInput.cs), [StampPlacement.cs](../../src/Amane.Pdf.Api/StampPlacement.cs), [PdfOverlayBuilder.cs](../../src/Amane.Pdf.Api/PdfOverlayBuilder.cs), [PngDataValidator.cs](../../src/Amane.Pdf.Api/PngDataValidator.cs)。

層: HTTP・実PNG/pdfcpu/qpdf・故障注入。判定: 座標/画像構造・status/limits・process停止・削除。

重複・補完: Input/Placement単体とPython48描画ケースで分担。

適用範囲: LV01対応済み。正常PDF/PNG multipartと本文未読probeでParse削除・順序逆転を検出。配置構造だけで見た目を保証せずPythonを残す。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T329 | [PdfStampImageTests.PlacesAtDisplayedPositionForEveryRotationCropAndUserUnit_OnlyTargetPageChanges](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L21) | 1 | 各回転/Crop/UserUnitの表示座標と非対象content不変・SMask・metadata除去 | 維持 |
| T330 | [PdfStampImageTests.AcceptsAllPngDepthsAndAdam7IncludingPaletteTransparency](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L65) | 1 | 全PNG色/深度/Adam7/透明度の実pdfcpu受理・SMask | 維持 |
| T331 | [PdfStampImageTests.InvalidQueryPrecedesUploadAndTools](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L85) | 1 | 正常PDF/PNG multipartを伴う不正queryの400・本文読取0回・tool到達前の拒否 | LV01：対応済み・維持（改善検証済み） |
| T332 | [PdfStampImageTests.InvalidMultipartImageAndPlacement_ReturnExpectedProblems](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L100) | 1 | 不正multipart/PNG/配置のstatusと画像reason | 維持 |
| T333 | [PdfStampImageTests.IndividualAndTotalUploadLimits_WorkWithAndWithoutContentLength](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L136) | 1 | Content-Length有無でPDF/PNG単体とrequest全体容量 | 維持 |
| T334 | [PdfStampImageTests.MaxPdfPlusMaxPng_IsAccepted_WithoutRaisingOtherEndpointsLimit](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L163) | 1 | 50MiB PDF+1MiB PNG受理と他API上限不変 | 維持 |
| T335 | [PdfStampImageTests.SmallValidPngWithHugeLayer_ReturnsImageTooComplex](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L190) | 1 | 小さい圧縮PNGでも巨大layerはimage-too-complex | 維持 |
| T336 | [PdfStampImageTests.PdfErrorsAndToolFailuresKeepDistinctContracts](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L202) | 1 | 入力PDF・page属性・容量・tool終了codeの区別 | 維持 |
| T337 | [PdfStampImageTests.TimeoutCancelAndBusySlot_DeleteImageAndWaitForProcessTree](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L234) | 1 | stamp保持中timeout/cancel/busyで削除・親子停止・permit返却 | 維持 |
| T338 | [PdfStampImageTests.EveryStageWaitsForChildrenAndDeletesStamp](../../tests/Amane.Pdf.Api.Tests/PdfStampImageTests.cs#L266) | 9 | 入力/draw/overlay各段階で実HTTP timeout/cancel/app停止・子停止・stamp削除 | 維持 |

## PdfUnlockResourceTests

本番参照: [PdfUnlockEndpoint.cs](../../src/Amane.Pdf.Api/PdfUnlockEndpoint.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs), [PdfEndpointHelpers.cs](../../src/Amane.Pdf.Api/PdfEndpointHelpers.cs)。

層: HTTP・実暗号PDF/qpdf・故障注入。判定: 実復号/内容保持・stage別reason・安全argv・deadline/PID。

重複・補完: Protect/Restrictでは認証段階/暗号入力の分類を守れない。6stageごとの異常は固有価値がある。

適用範囲: 故障wrapperのstage名は実pipelineに対応。単純共通エラーテストへ統合しない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T339 | [PdfUnlockResourceTests.Unlock_TimeoutAtEveryStage_StopsProcessTreeCleansFilesAndReleasesPermit](../../tests/Amane.Pdf.Api.Tests/PdfUnlockResourceTests.cs#L21) | 6 | 解除6stage全てのtimeoutで停止・削除・permit返却 | 維持 |
| T340 | [PdfUnlockResourceTests.Unlock_CancellationOrApplicationStoppingAtEveryStage_StopsProcessTreeAndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfUnlockResourceTests.cs#L54) | 12 | 解除6stageのHTTP cancel/app停止で親子停止・削除 | 維持 |
| T341 | [PdfUnlockResourceTests.Unlock_TimeBudgetIsSharedAcrossPasswordAuthenticationAndInputCheck](../../tests/Amane.Pdf.Api.Tests/PdfUnlockResourceTests.cs#L81) | 1 | password認証と入力checkで共通deadlineを使う | 維持 |
| T342 | [PdfUnlockResourceTests.Unlock_OutputCheckReceivesOnlyRemainingTimeBudget](../../tests/Amane.Pdf.Api.Tests/PdfUnlockResourceTests.cs#L103) | 1 | 出力checkも残りdeadlineだけを使う | 維持 |
| T343 | [PdfUnlockResourceTests.Unlock_SharesConcurrencyLimiterWithProtect_AndKeepsHealthAvailable](../../tests/Amane.Pdf.Api.Tests/PdfUnlockResourceTests.cs#L126) | 1 | unlock/protectの共有permit・health可用性・復旧 | 維持 |
| T344 | [PdfUnlockResourceTests.Unlock_FileSizeBoundaryWithOrWithoutContentLength_IsEnforced](../../tests/Amane.Pdf.Api.Tests/PdfUnlockResourceTests.cs#L166) | 4 | Content-Length有無とファイルぴったり/1byte超過 | 維持 |
| T345 | [PdfUnlockResourceTests.Unlock_RequestEnvelopeLimitIncludesEpilogue_WithOrWithoutContentLength](../../tests/Amane.Pdf.Api.Tests/PdfUnlockResourceTests.cs#L186) | 2 | Content-Length有無でMIME epilogueを全体容量に算入 | 維持 |
| T346 | [PdfUnlockResourceTests.Unlock_CancelledOrStoppingUpload_CleansPartialFiles](../../tests/Amane.Pdf.Api.Tests/PdfUnlockResourceTests.cs#L216) | 2 | upload中cancel/app停止で部分ファイルと応答を残さない | 維持 |

## PdfUnlockTests

本番参照: [PdfUnlockEndpoint.cs](../../src/Amane.Pdf.Api/PdfUnlockEndpoint.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs), [PdfEndpointHelpers.cs](../../src/Amane.Pdf.Api/PdfEndpointHelpers.cs)。

層: HTTP・実暗号PDF/qpdf・故障注入。判定: 実復号/内容保持・stage別reason・安全argv・deadline/PID。

重複・補完: Protect/Restrictでは認証段階/暗号入力の分類を守れない。6stageごとの異常は固有価値がある。

適用範囲: 故障wrapperのstage名は実pipelineに対応。単純共通エラーテストへ統合しない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T347 | [PdfUnlockTests.Unlock_WithUserOrOwnerPassword_RemovesEncryptionAndPreservesPages](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L26) | 8 | 4暗号方式とuser/owner認証で暗号解除・頁順/回転・Encrypt除去 | 維持 |
| T348 | [PdfUnlockTests.Unlock_PreservesUnicodeSpacesAndJsonCharacters](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L56) | 4 | Unicode/空白/JSON特殊文字passwordを保持 | 維持 |
| T349 | [PdfUnlockTests.Unlock_Aes128WithUtf8PasswordBytes_AcceptsUnicodePasswordAndRejectsWrongPassword](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L69) | 1 | AES128 bytes方式Unicode互換と誤passwordのreason | 維持 |
| T350 | [PdfUnlockTests.Unlock_127Utf8Bytes_IsAcceptedWithoutTrimming](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L89) | 1 | UTF-8 127byte passwordを受理 | 維持 |
| T351 | [PdfUnlockTests.Unlock_WrongPassword_ReturnsFixedReason](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L102) | 1 | 誤passwordはwrong-passwordの固定応答 | 維持 |
| T352 | [PdfUnlockTests.Unlock_OwnerOnlyRestrictions_AreRejectedBeforePasswordAuthentication](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L113) | 2 | owner制限のみPDFは認証前にno-open-passwordで拒否 | 維持 |
| T353 | [PdfUnlockTests.Unlock_UnencryptedOrInvalidInput_ReturnsCorrectReason](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L128) | 6 | 平文/空/破損/warning/切断入力のreason区別 | 維持 |
| T354 | [PdfUnlockTests.Unlock_BrokenReferencesFailingAfterAuthentication_Return422InvalidPdf](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L147) | 2 | 認証後に分かるRoot/Pages参照破損はinvalid-pdf | 維持 |
| T355 | [PdfUnlockTests.Unlock_AuthenticatedStructureErrorOrWarning_IsRejected](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L173) | 2 | 認証可能なstream破損/構造warningも拒否 | 維持 |
| T356 | [PdfUnlockTests.Unlock_MissingDuplicateOrUnexpectedFields_Return400BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L206) | 5 | 必須/重複/未知fieldをqpdf前に拒否 | 維持 |
| T357 | [PdfUnlockTests.Unlock_InvalidPassword_Return400BeforeQpdf](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L222) | 5 | 空/制御/128byte/不正UTF-8 passwordをqpdf前に拒否 | 維持 |
| T358 | [PdfUnlockTests.Unlock_MalformedMultipart_Return400](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L240) | 3 | unlockのmalformed multipartを400拒否 | 維持 |
| T359 | [PdfUnlockTests.Unlock_ExitCodesAreMappedByStage_WithoutExposingOutput](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L264) | 14 | 解除の各stage exit codeを固有reason/422/500に分類・秘密抑止 | 維持 |
| T360 | [PdfUnlockTests.Unlock_InvalidOutput_Returns500InsteadOf422](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L282) | 5 | 壊れた/空/暗号残留/欠落出力は入力エラーでなく500 | 維持 |
| T361 | [PdfUnlockTests.Unlock_MissingExecutable_ReturnsSanitized500AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L305) | 1 | 実行ファイル消失時500・非露出・削除 | 維持 |
| T362 | [PdfUnlockTests.Unlock_AllJobsArePrivate_AndNoPasswordOrFilenameAppearsInAnyArgv](../../tests/Amane.Pdf.Api.Tests/PdfUnlockTests.cs#L313) | 1 | 3種認証jobのprivate権限・unicodeモード・全argvへの秘密非露出 | 維持 |

## PdfValidationTests

本番参照: [PdfProtectEndpoint.cs](../../src/Amane.Pdf.Api/PdfProtectEndpoint.cs), [MultipartPdfUpload.cs](../../src/Amane.Pdf.Api/MultipartPdfUpload.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs), [TemporaryPdfFiles.cs](../../src/Amane.Pdf.Api/TemporaryPdfFiles.cs)。

層: HTTP・直接processor・実qpdf。判定: 実暗号化/復号・private job・argv・固定応答。

重複・補完: PdfResourceLimitTestsで実request中のcancel/timeout。Validationは受付、Protectは暗号化結果/内部job。

適用範囲: 事前cancelの直接呼出は実処理中の取消ではない。LV06。QpdfJobFailureとFailedQpdfは入口/直接呼出が異なる。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T363 | [PdfValidationTests.MissingFields_Return400](../../tests/Amane.Pdf.Api.Tests/PdfValidationTests.cs#L14) | 2 | protect必須file/password欠落は400 | 維持 |
| T364 | [PdfValidationTests.InvalidPasswords_Return400](../../tests/Amane.Pdf.Api.Tests/PdfValidationTests.cs#L26) | 4 | protectの空/制御/過長passwordは400 | 維持 |
| T365 | [PdfValidationTests.InvalidPdf_Return422_RegardlessOfContentType](../../tests/Amane.Pdf.Api.Tests/PdfValidationTests.cs#L38) | 4 | 空/非PDF/破損/qpdf warningは422と固定安全応答 | 維持 |
| T366 | [PdfValidationTests.EncryptedPdf_Return422_EvenWithEmptyOrMatchingPassword](../../tests/Amane.Pdf.Api.Tests/PdfValidationTests.cs#L61) | 2 | 開くpasswordが空でも設定済みでも再暗号化を422拒否 | 維持 |
| T367 | [PdfValidationTests.UnicodeAndSpaces_AreUsablePasswords](../../tests/Amane.Pdf.Api.Tests/PdfValidationTests.cs#L78) | 2 | Unicode・両端空白passwordの正誤認証とログ非露出 | 維持 |
| T368 | [PdfValidationTests.ValidPdf_WithUntrustedContentType_IsAccepted](../../tests/Amane.Pdf.Api.Tests/PdfValidationTests.cs#L96) | 1 | PDF実体なら偽拡張子/MIMEでも受理 | 維持 |
| T369 | [PdfValidationTests.DuplicateFields_Return400](../../tests/Amane.Pdf.Api.Tests/PdfValidationTests.cs#L109) | 2 | protectのfile/password重複を400拒否 | 維持 |
| T370 | [PdfValidationTests.MalformedRequests_Return400](../../tests/Amane.Pdf.Api.Tests/PdfValidationTests.cs#L122) | 3 | 非multipart・boundary欠落/不一致を400拒否 | 維持 |
| T371 | [PdfValidationTests.QpdfJobFailure_IsSanitized_AndCleansFiles](../../tests/Amane.Pdf.Api.Tests/PdfValidationTests.cs#L135) | 3 | protect qpdf job終了code別422/500とstderr/秘密非露出 | 維持 |

## PdfWatermarkTests

本番参照: [PdfWatermarkEndpoint.cs](../../src/Amane.Pdf.Api/PdfWatermarkEndpoint.cs), [WatermarkText.cs](../../src/Amane.Pdf.Api/WatermarkText.cs), [PdfcpuLayer.cs](../../src/Amane.Pdf.Api/PdfcpuLayer.cs), [PdfOverlayBuilder.cs](../../src/Amane.Pdf.Api/PdfOverlayBuilder.cs)。

層: HTTP・実pdfcpu/qpdf・故障注入。判定: 構造/opacity・HTTP契約・process/permit・private text。

重複・補完: WatermarkText単体は数式、Pythonは抽出/実色/角度/描画境界。

適用範囲: LV01対応済み（正常multipart・本文読取0回・変異検出）。色行列のassert不足 LV08。HTTP受付と独立描画の両層が必要。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T372 | [PdfWatermarkTests.InvalidQueryPrecedesUploadAndTools](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L26) | 1 | 正常PDF/text multipartを伴う不正queryの400・本文読取0回・tool到達前の拒否 | LV01：対応済み・維持（改善検証済み） |
| T373 | [PdfWatermarkTests.AllStylesAndColorsPreserveGeometryAndDrawOnceOnEveryPage](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L48) | 8 | 各style/color/textで実色・CID/ToUnicode文字・opacity・一頁一描画とgeometry保持 | LV08：対応済み・維持（改善検証済み） |
| T374 | [PdfWatermarkTests.ImageIsDrawnBeforeWatermarkAndBookmarkFormAndLinkSurvive](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L91) | 1 | 画像→透かしの重ね順とbookmark/form/linkの保持 | 維持 |
| T375 | [PdfWatermarkTests.TextIsLiteralPrivateJsonAndAbsentFromArgvErrorsAndLogs](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L123) | 1 | literal textはprivate JSONへ、argv/error/logには出さない | 維持 |
| T376 | [PdfWatermarkTests.MultipartRejectsMissingDuplicateUnknownAndMisclassifiedParts](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L155) | 1 | multipartの欠落/重複/未知/part種別拒否 | 維持 |
| T377 | [PdfWatermarkTests.Utf8ByteBoundaryCharsetIgnoredAndInvalidTextPrecedesTools](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L181) | 1 | 文字数/UTF-8 byte境界・charset非依存と不正textのtool前拒否 | 維持 |
| T378 | [PdfWatermarkTests.SizeLimitsPrecedeSemanticTextValidationAndTools](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L200) | 1 | file容量がsemantic text検査より優先 | 維持 |
| T379 | [PdfWatermarkTests.ThousandPagesSucceedAndThousandAndOneIsTooComplex](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L213) | 1 | 1000頁成功と1001頁too-complex | 維持 |
| T380 | [PdfWatermarkTests.InputCheckRunsOnceAndNoSeparatePageCountProbe](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L229) | 1 | 入力check一度と頁数probe省略 | 維持 |
| T381 | [PdfWatermarkTests.PdfValidationCapacityUnsupportedAndToolFailuresKeepErrorContracts](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L245) | 1 | PDF/容量/未対応属性/tool障害のreasonと固定応答 | 維持 |
| T382 | [PdfWatermarkTests.TimeoutReleasesSharedSlotAndCancelStopsProcessTree](../../tests/Amane.Pdf.Api.Tests/PdfWatermarkTests.cs#L279) | 1 | timeoutの共有slot解放とHTTP cancelの親子停止・削除 | 維持 |

## PdfcpuProcessorTests

本番参照: [PdfcpuProcessor.cs](../../src/Amane.Pdf.Api/PdfcpuProcessor.cs), [PdfcpuLayer.cs](../../src/Amane.Pdf.Api/PdfcpuLayer.cs), [Program.cs](../../src/Amane.Pdf.Api/Program.cs)。

層: 直接processor・実pdfcpu・起動。判定: private JSON・出力検証・固定deadline・実limits/PID。

重複・補完: OverlayBuilderは組み合わせ、Docker smokeは実配置/license。30秒共有deadlineは長いが固有の起動保証。

適用範囲: 直接processorではHTTP受付を通らない。実pdfcpu起動の出力/環境/FSIZEを検証する層。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T383 | [PdfcpuProcessorTests.JapaneseText_IsPrivateJson_WithFixedEmbeddedFont_AndValidOutput](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L18) | 1 | 日本語をprivate JSON経由で固定BIZフォントに渡し埋込みフォントとToUnicodeを持つPDFを生成する | 維持 |
| T384 | [PdfcpuProcessorTests.PercentEscaping_UsesOriginalLength_AndCallerLimitsAreIndependent](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L41) | 1 | %の二重化前の文字数を使い呼出側ごとの文字数上限を独立に適用する | 維持 |
| T385 | [PdfcpuProcessorTests.JsonBudget_ExactBytesSucceeds_OneLessFailsBeforePrivateFileOrProcess](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L63) | 1 | JSON予算のちょうど上限は成功し1byte不足では生成物も外部プロセスも作らない | 維持 |
| T386 | [PdfcpuProcessorTests.InvalidText_IsRejectedWithoutCallingPdfcpu](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L92) | 11 | 不正な文字・配置・サイズ等をpdfcpu実行と中間JSON作成前に拒否する | 維持 |
| T387 | [PdfcpuProcessorTests.TextAndPerPageElementBoundaries_AreCheckedBeforeDrawing](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L116) | 1 | 文字数とページ内要素数の上限を描画前に検証する | 維持 |
| T388 | [PdfcpuProcessorTests.NoOutput_IsFixedInternalFailure](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L137) | 5 | 出力なしの各終了コードを内部情報なしの固定内部エラーにする | 維持 |
| T389 | [PdfcpuProcessorTests.RealPdfcpu_FsizeDeletesPartialOutput_AndRemainsInternalFailure](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L148) | 1 | 実pdfcpuがFSIZEに達した部分出力を削除し内部失敗として扱う | 維持 |
| T390 | [PdfcpuProcessorTests.ObservedOutputOverflow_IsCapacityFailure_EvenWithExitZero](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L158) | 1 | 終了コード0でも観測した出力超過を容量失敗に分類する | 維持 |
| T391 | [PdfcpuProcessorTests.InvalidOrWrongPageOutput_IsNeverReturned](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L172) | 4 | 空・破損・暗号化・ページ数違いのpdfcpu出力を返さない | 維持 |
| T392 | [PdfcpuProcessorTests.Request_AppliesRealLimitsAndEnvironment_AndKeepsTextOutOfArgv](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L190) | 1 | 実プロセスの環境変数・AS/CORE/FSIZE・private JSON権限・argv非漏えいを守る | 維持 |
| T393 | [PdfcpuProcessorTests.Cancellation_StopsProcessTree_AndDisposalRemovesJob](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L225) | 1 | キャンセルで子プロセスを停止し所有者によるDisposeでジョブを削除する | 維持 |
| T394 | [PdfcpuProcessorTests.Paths_RejectOriginalInputAndOutsideJob_AndPreserveExistingOutput](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L246) | 3 | 入力上書き・ジョブ外パス・既存出力を拒否し既存ファイルを保持する | 維持 |
| T395 | [PdfcpuProcessorTests.Startup_AcceptsExistingRelativeTempRoot_AndDeletesItsJob](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L269) | 1 | 相対指定した既存一時rootで起動し自己診断のジョブだけを消す | 維持 |
| T396 | [PdfcpuProcessorTests.InvalidOptions_PreventStartup](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L289) | 10 | pdfcpu等の不正設定を起動時に拒否する | 維持 |
| T397 | [PdfcpuProcessorTests.StartupFailures_HaveDedicatedExceptionAndLog_WithoutInternalDetails](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L306) | 8 | pdfcpu起動失敗を専用例外と固定ログに変換し内部情報・残存ファイルを残さない | 維持 |
| T398 | [PdfcpuProcessorTests.Program_UsesOneFixedThirtySecondDeadline_AcrossBothSelfTests](../../tests/Amane.Pdf.Api.Tests/PdfcpuProcessorTests.cs#L349) | 1 | qpdfとpdfcpuの連続自己診断を共通30秒期限で打ち切り子とジョブを回収する | 維持 |

## PngSanitizerTests

本番参照: [PngSanitizer.cs](../../src/Amane.Pdf.Api/PngSanitizer.cs), [ImageInputInspector.cs](../../src/Amane.Pdf.Api/ImageInputInspector.cs)。

層: 単体。判定: 固定working vectors・pixel chunk bytes・CRC/構造・境界。

重複・補完: StampImageInputはIDAT decode、FromImages HTTP/Pythonは実画素。

適用範囲: FromImagesFixturesはCRCを本番UpdateCrcで生成する相関あり。独立zlib/Pillow/実pdfcpuの検証を維持。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T399 | [PngSanitizerTests.WorkingEstimate_MatchesSharedVectorsAndDefaultAdmission](../../tests/Amane.Pdf.Api.Tests/PngSanitizerTests.cs#L22) | 159 | 共有vectorのPNG作業量見積りと既定320MiB受理境界 | 維持 |
| T400 | [PngSanitizerTests.Sanitizer_PreservesPixelChunksAndDropsMetadataAndTrailingBytes](../../tests/Amane.Pdf.Api.Tests/PngSanitizerTests.cs#L38) | 4 | pixel用chunkを保存しmetadata/末尾data除去・private権限 | 維持 |
| T401 | [PngSanitizerTests.Sanitizer_RejectsMalformedStructureAndCrcEvenOnDiscardedChunks](../../tests/Amane.Pdf.Api.Tests/PngSanitizerTests.cs#L55) | 1 | PNG構造・CRC・未知critical/APNG/順序違反を拒否 | 維持 |
| T402 | [PngSanitizerTests.Sanitizer_EnforcesPixelsWorkingEstimateWriteBudgetAndCancellation](../../tests/Amane.Pdf.Api.Tests/PngSanitizerTests.cs#L82) | 1 | 画素/作業量/書込予算・cancelと出力量 | 維持 |
| T403 | [PngSanitizerTests.Gray16_TransparencyRequiresTheExpandedDecoderBudget](../../tests/Amane.Pdf.Api.Tests/PngSanitizerTests.cs#L107) | 1 | 16bit gray透明度有無によるdecoder予算差 | 維持 |
| T404 | [PngSanitizerTests.Adam7_ReservesAnotherDecodedImage](../../tests/Amane.Pdf.Api.Tests/PngSanitizerTests.cs#L128) | 1 | Adam7の追加decoded画像予算（header変更のみ、実decode対象外） | 維持 |

## ProcessFileLimitTests

本番参照: [ProcessMemoryLimits.cs](../../src/Amane.Pdf.Api/ProcessMemoryLimits.cs), [ExternalProcessRunner.cs](../../src/Amane.Pdf.Api/ExternalProcessRunner.cs), [PdfCompressProcessor.cs](../../src/Amane.Pdf.Api/PdfCompressProcessor.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs)。

層: 実OS/実qpdf/JPEG。判定: /proc limits・signal・exact/one-byte境界・実output。

重複・補完: 計算のみのCapacityTestsではsignal継承/exec挙動を代替不可。

適用範囲: Linux固有の制限。Dockerのcgroupメモリ上限とは別の資源境界。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T405 | [ProcessFileLimitTests.SignalAndLimits_SurviveExec_WithAndWithoutFileLimit](../../tests/Amane.Pdf.Api.Tests/ProcessFileLimitTests.cs#L14) | 2 | CORE/AS/FSIZEと環境変数、SIGXFSZ無視がexecをまたいで維持 | 維持 |
| T406 | [ProcessFileLimitTests.RealQpdf_FileLimitKeepsExactAndOneByteOverflow_WithoutSignalTermination](../../tests/Amane.Pdf.Api.Tests/ProcessFileLimitTests.cs#L36) | 1 | 実qpdfのぴったり/1byte超過/saturated出力と正常終了の実態 | 維持 |
| T407 | [ProcessFileLimitTests.RealJpegTool_FileLimitReturnsError_AndLeavesOnlyBoundedOutput](../../tests/Amane.Pdf.Api.Tests/ProcessFileLimitTests.cs#L66) | 2 | 実cjpeg/djpegのFSIZE到達がsignal死でなくbounded出力とI/O error | 維持 |
| T408 | [ProcessFileLimitTests.SaturatedMetadata_StopsImageSelection_AndReturnsValidPdf](../../tests/Amane.Pdf.Api.Tests/ProcessFileLimitTests.cs#L94) | 2 | 切詰めmetadataでも圧縮をskipし有効PDFを返す | 維持 |
| T409 | [ProcessFileLimitTests.FinalOutputAtFileLimit_Is500_EvenWhenRealQpdfAndCheckReturnZero](../../tests/Amane.Pdf.Api.Tests/ProcessFileLimitTests.cs#L125) | 1 | 実qpdf/check両方0でも最終出力FSIZE飽和は500・安全応答 | 維持 |

## QpdfMemoryLimitTests

本番参照: [ProcessMemoryLimits.cs](../../src/Amane.Pdf.Api/ProcessMemoryLimits.cs), [QpdfProcessor.cs](../../src/Amane.Pdf.Api/QpdfProcessor.cs), [Program.cs](../../src/Amane.Pdf.Api/Program.cs)。

層: 実OS/実qpdf・起動/HTTP。判定: 実AS/JPEGMEM・同一PDF上限変更対照・process停止。

重複・補完: ResourceTestsのtimeoutとは異なるmemory admission。Docker capacityが総cgroupを補完。

適用範囲: 小ASへ設定変更して境界を発火。既定で全PDFが成功するという性能保証ではない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T410 | [QpdfMemoryLimitTests.InvalidOptions_PreventStartup](../../tests/Amane.Pdf.Api.Tests/QpdfMemoryLimitTests.cs#L34) | 15 | 不正prlimit/AS/JPEGMEM設定で起動拒否 | 維持 |
| T411 | [QpdfMemoryLimitTests.ValidJpegMemory_Starts](../../tests/Amane.Pdf.Api.Tests/QpdfMemoryLimitTests.cs#L45) | 4 | 許可するJPEGMEM数値表記で起動可能 | 維持 |
| T412 | [QpdfMemoryLimitTests.FailedSelfTest_PreventsStartup_WithoutInternalDetails](../../tests/Amane.Pdf.Api.Tests/QpdfMemoryLimitTests.cs#L57) | 5 | 自己テスト失敗の起動拒否・固定例外と内部詳細抑止 | 維持 |
| T413 | [QpdfMemoryLimitTests.RealAddressSpaceLimit_ReturnsCommon422_AndIncreasingLimitProcessesSamePdf](../../tests/Amane.Pdf.Api.Tests/QpdfMemoryLimitTests.cs#L80) | 7 | 実RLIMIT_ASを下げた7 APIの422と上げた同PDFの成功 | 維持 |
| T414 | [QpdfMemoryLimitTests.Unlock_LimitedPlainAndAuthenticatedCheck_KeepInvalidPdfReason](../../tests/Amane.Pdf.Api.Tests/QpdfMemoryLimitTests.cs#L107) | 1 | unlockの実memory上限でもinvalid-pdf reasonを維持し増枠で成功 | 維持 |
| T415 | [QpdfMemoryLimitTests.RealLimiter_AppliesEnvironmentAndAddressSpaceToBothExecutionPaths](../../tests/Amane.Pdf.Api.Tests/QpdfMemoryLimitTests.cs#L133) | 1 | 実prlimitがqpdf両実行経路にASとJPEGMEMを適用 | 維持 |
| T416 | [QpdfMemoryLimitTests.SelfTest_QpdfTooSmallAndTimeout_AreRejected_AndProcessTreeStops](../../tests/Amane.Pdf.Api.Tests/QpdfMemoryLimitTests.cs#L154) | 1 | trueだけでなくqpdf起動能力を検証し自己テストtimeoutで親子停止 | 維持 |
| T417 | [QpdfMemoryLimitTests.RealPrlimit_ExecFailureAfterStartup_Is500](../../tests/Amane.Pdf.Api.Tests/QpdfMemoryLimitTests.cs#L175) | 2 | 起動後の実prlimit exec失敗126/127はAPIの500 | 維持 |

## RedactNeutralObjectsTests

本番参照: [RedactObjectRoles.cs](../../src/Amane.Pdf.Api/RedactObjectRoles.cs), [RedactDocument.cs](../../src/Amane.Pdf.Api/RedactDocument.cs), [PdfRedactProcessor.cs](../../src/Amane.Pdf.Api/PdfRedactProcessor.cs)。

層: 単体構造・HTTP実PDF。判定: role別allowlist・内容残存禁止・rasterized集合。

重複・補完: RedactSecurityTestsは危険共有、Neutralは安全共有の限定許可。互いに削除不可。

適用範囲: 同じobjectへの複数role到達を確認する必要がある。単なる名前allowlistにしない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T418 | [RedactNeutralObjectsTests.NeutralSharing_KeepsSettingsButNotTargetPixels_WithoutExpandingR](../../tests/Amane.Pdf.Api.Tests/RedactNeutralObjectsTests.cs#L20) | 14 | 安全な共有ICC・状態・関数等は残し対象画素を除去して不要な画像化を増やさない | 維持 |
| T419 | [RedactNeutralObjectsTests.NeutralParent_DoesNotExemptBodyChildrenOrConflictingRoles](../../tests/Amane.Pdf.Api.Tests/RedactNeutralObjectsTests.cs#L42) | 9 | 中立objectの子や競合する役割にある本文参照を例外扱いせず拒否する | 維持 |
| T420 | [RedactNeutralObjectsTests.Function_RequiresSupportedTypeAndDomain_AndStreamsForSampledOrCalculatorFunctions](../../tests/Amane.Pdf.Api.Tests/RedactNeutralObjectsTests.cs#L133) | 1 | 許可FunctionTypeとDomainを必須にしsampled/calculatorにはstreamを要求する | 維持 |
| T421 | [RedactNeutralObjectsTests.Allowlist_UsesRoleAndStructure_AndRejectsUnknownOrContentBearingShapes](../../tests/Amane.Pdf.Api.Tests/RedactNeutralObjectsTests.cs#L156) | 1 | 中立object判定が役割・形状の許可リストに従い本文役割や未知形状を拒否する | 維持 |
| T422 | [RedactNeutralObjectsTests.ColorSpaceFamilies_KeepOnlyTheApprovedArrayShapes](../../tests/Amane.Pdf.Api.Tests/RedactNeutralObjectsTests.cs#L184) | 1 | 色空間配列が許可したfamilyと形状だけを受け入れる | 維持 |
| T423 | [RedactNeutralObjectsTests.EveryArrivalIsCollected_EvenWhenANeutralRoleIsVisitedFirst](../../tests/Amane.Pdf.Api.Tests/RedactNeutralObjectsTests.cs#L203) | 2 | 中立役割で先に訪れても後着の本文役割を収集して残存参照を拒否する | 維持 |

## RedactSecurityTests

本番参照: [RedactDocument.cs](../../src/Amane.Pdf.Api/RedactDocument.cs), [RedactObjectRoles.cs](../../src/Amane.Pdf.Api/RedactObjectRoles.cs), [PdfRedactEndpoint.cs](../../src/Amane.Pdf.Api/PdfRedactEndpoint.cs)。

層: HTTP・実構造変換。判定: 秘密参照からの復活拒否・rasterized対象・headers。

重複・補完: PdfRedactTestsは代表全体、Neutralは許可境界。細分graphは攻撃経路ごとに固有。

適用範囲: 人工の攻撃参照graphを実PDF化してHTTP処理。内部JSONだけの試験と区別。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T424 | [RedactSecurityTests.SharedOrPrivateTargetObjects_Are422WithoutPdfOrSuccessHeaders](../../tests/Amane.Pdf.Api.Tests/RedactSecurityTests.cs#L15) | 8 | 共有・private参照に残る対象画像/Form等を422にしてPDFや成功ヘッダを返さない | 維持 |
| T425 | [RedactSecurityTests.IndependentImageOrForm_DoesNotExpandRasterizedSet](../../tests/Amane.Pdf.Api.Tests/RedactSecurityTests.cs#L27) | 2 | 独立した画像/Formを共有と誤認して非対象ページを画像化しない | 維持 |
| T426 | [RedactSecurityTests.ExtGStateFont_SharingEitherDirection_RasterizesBothPages](../../tests/Amane.Pdf.Api.Tests/RedactSecurityTests.cs#L72) | 2 | ExtGState経由の共有fontを両方向に認識して両ページを画像化しglyph情報を消す | 維持 |
| T427 | [RedactSecurityTests.ProtectedGraph_TraversesArbitraryContainersAndInheritedResources_WithoutWalkingPageTree](../../tests/Amane.Pdf.Api.Tests/RedactSecurityTests.cs#L98) | 1 | 任意containerと継承resourceに隠れた対象本文参照を見落とさずpage tree全体には拡張しない | 維持 |

## RedactTests

本番参照: [RedactRegions.cs](../../src/Amane.Pdf.Api/RedactRegions.cs), [RedactRaster.cs](../../src/Amane.Pdf.Api/RedactRaster.cs), [RedactDocument.cs](../../src/Amane.Pdf.Api/RedactDocument.cs)。

層: 単体。判定: 固定decimal/pixel境界・graph/font/image・update許可キー。

重複・補完: 実PDF/PDFium/HTTPテストでdecode/入力到達を補完。

適用範囲: 1ファイル3TestClass。画像decoder許容値の実測はPdfRedactTestsとthresholds履歴を参照。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T428 | [RedactRegionsTests.RejectsMalformedEncodingJsonKeysNumbersAndBounds](../../tests/Amane.Pdf.Api.Tests/RedactTests.cs#L13) | 1 | regionsのUTF-8/JSON/キー/数値表現/座標/個数制限を厳格に検証する | 維持 |
| T429 | [RedactRegionsTests.AcceptsExactBoundariesDuplicatesAndTwentyPages_UsingDecimal](../../tests/Amane.Pdf.Api.Tests/RedactTests.cs#L40) | 1 | regions上限・重複・20ページ・decimalの端数を許容し実ページ数超過を拒否する | 維持 |
| T430 | [RedactRasterTests.LimitsPixelsAndAxes_WithCorrectFloorAndOutwardRounding](../../tests/Amane.Pdf.Api.Tests/RedactTests.cs#L59) | 1 | 画素数・軸長・floor・領域外向き丸め・pt文字列の境界を守る | 維持 |
| T431 | [RedactDocumentTests.Fonts_IndirectDirectProgramsAndNestedResources_WithoutTransitiveExpansion](../../tests/Amane.Pdf.Api.Tests/RedactTests.cs#L111) | 1 | 直接・間接・埋込み・入れ子フォント共有を検出し不要な推移的画像化はしない | 維持 |
| T432 | [RedactDocumentTests.Images_CheckFilterChainsMasksColorSpacesAndExactThresholds](../../tests/Amane.Pdf.Api.Tests/RedactTests.cs#L154) | 1 | 圧縮filter・色空間・maskを考慮した画像の安全閾値と1超過拒否を守る | 維持 |
| T433 | [RedactDocumentTests.DescendantArraysAndFontPrograms_AreTraversed_AndResourceDepthIsBounded](../../tests/Amane.Pdf.Api.Tests/RedactTests.cs#L187) | 1 | DescendantFontsと共有font programを走査しresource深度を制限する | 維持 |
| T434 | [RedactDocumentTests.VisitBudget_AliasedColorSpaceAndImageMask_AreEnforced](../../tests/Amane.Pdf.Api.Tests/RedactTests.cs#L211) | 1 | 走査回数上限と色空間alias・image maskの画像予算評価を守る | 維持 |
| T435 | [RedactDocumentTests.CapacityFactories_KeepCleanDefaults_AndReserveExternalSentinelsBeforeWriting](../../tests/Amane.Pdf.Api.Tests/RedactTests.cs#L233) | 1 | 黒塗り用容量例外でもclean既定値を変えずsentinel分を予約し書込み上限を守る | 維持 |
| T436 | [RedactDocumentTests.Update_UsesFreshObjectsAndOnlyAllowedPageCatalogAndRootKeys](../../tests/Amane.Pdf.Api.Tests/RedactTests.cs#L253) | 1 | 最終更新が新規objectと許可キーだけを使い元private情報を残さず不正ページ順を拒否する | 維持 |

## StampImageInputTests

本番参照: [StampImageInput.cs](../../src/Amane.Pdf.Api/StampImageInput.cs), [PngDataValidator.cs](../../src/Amane.Pdf.Api/PngDataValidator.cs), [PngSanitizer.cs](../../src/Amane.Pdf.Api/PngSanitizer.cs), [OverlayCapacity.cs](../../src/Amane.Pdf.Api/OverlayCapacity.cs)。

層: 単体・実PNG inflate。判定: 独立PNG fixture・CRC正常でも壊れたIDAT・寸法/予算。

重複・補完: PdfStampImageTestsとPythonは実合成後の画素。PngSanitizer単体はmetadata/構造が目的。

適用範囲: StampFixturesはpacked depth/Adam7を生成。from-images構造用PNGと用途を混同しない。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T437 | [StampImageInputTests.Png_ValidatesAllDepthsAndAdam7WithSmallAndOddDimensions](../../tests/Amane.Pdf.Api.Tests/StampImageInputTests.cs#L7) | 1 | 全PNG色/深度/Adam7/奇数極小寸法の検証とmetadata/upload削除 | 維持 |
| T438 | [StampImageInputTests.Png_RejectsCorruptCompressedDataDespiteValidChunkCrc](../../tests/Amane.Pdf.Api.Tests/StampImageInputTests.cs#L25) | 1 | CRC正常でもzlib/adler/行長/filter/dictionary破損を拒否 | 維持 |
| T439 | [StampImageInputTests.Png_RejectsEachSideEvenWhenPixelProductFits](../../tests/Amane.Pdf.Api.Tests/StampImageInputTests.cs#L37) | 1 | 画素積内でも各辺2000px上限を適用 | 維持 |
| T440 | [StampImageInputTests.Settings_RejectOverflowAndInsufficientTemporaryCapacity](../../tests/Amane.Pdf.Api.Tests/StampImageInputTests.cs#L48) | 1 | stamp設定のoverflow・不足一時領域・request上限 | 維持 |

## StampPlacementTests

本番参照: [StampPlacement.cs](../../src/Amane.Pdf.Api/StampPlacement.cs), [PdfStampImageEndpoint.cs](../../src/Amane.Pdf.Api/PdfStampImageEndpoint.cs)。

層: 単体。判定: 固定表示寸法/座標・ratio/数字/overflow。

重複・補完: HTTPのquery配線はLV01修正と検証削除変異で確認済み。Pythonは独立描画座標。

適用範囲: 本番と同じ計算を期待値で再計算せず固定値/境界を比較。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T441 | [StampPlacementTests.Query_RequiresAsciiBoundedNumbersAndUniqueKnownKeys](../../tests/Amane.Pdf.Api.Tests/StampPlacementTests.cs#L11) | 1 | query必須/ASCII正規数値/重複/未知key・page範囲 | 維持 |
| T442 | [StampPlacementTests.Placement_UsesDisplayedDimensionsAndKeepsAspectRatio](../../tests/Amane.Pdf.Api.Tests/StampPlacementTests.cs#L30) | 1 | 表示寸法基準の位置/縦横比・文化非依存description | 維持 |
| T443 | [StampPlacementTests.Placement_RejectsOverflowAndExtremelyTallImages](../../tests/Amane.Pdf.Api.Tests/StampPlacementTests.cs#L53) | 1 | はみ出し/極端な縦長を拒否し極小scaleを0にしない | 維持 |

## WatermarkTextTests

本番参照: [WatermarkText.cs](../../src/Amane.Pdf.Api/WatermarkText.cs), [WatermarkGlyphs.g.cs](../../src/Amane.Pdf.Api/WatermarkGlyphs.g.cs), [PdfcpuLayer.cs](../../src/Amane.Pdf.Api/PdfcpuLayer.cs)。

層: 単体。判定: 固定glyph/advance・UTF8/text element境界・幾何。

重複・補完: HTTPは実入口、Pythonは実pdfcpu字形/色/描画。

適用範囲: 生成table自己整合だけでは字体真値を保証しない。独立固定値とPython検証を維持。

| ID | テスト（定義位置） | 現在ケース数 | 守ること | 判断 |
| --- | --- | ---: | --- | --- |
| T444 | [WatermarkTextTests.GlyphAndRangeBoundaries](../../tests/Amane.Pdf.Api.Tests/WatermarkTextTests.cs#L22) | 33 | watermark文字範囲とglyph table境界 | 維持 |
| T445 | [WatermarkTextTests.InvalidTextHasFixedMessage](../../tests/Amane.Pdf.Api.Tests/WatermarkTextTests.cs#L31) | 21 | 空白のみ/未許可文字の固定拒否 | 維持 |
| T446 | [WatermarkTextTests.InvalidUtf8IsRejected](../../tests/Amane.Pdf.Api.Tests/WatermarkTextTests.cs#L40) | 6 | 不正UTF-8を厳格拒否 | 維持 |
| T447 | [WatermarkTextTests.AcceptedTextIsNotTrimmedOrNormalized](../../tests/Amane.Pdf.Api.Tests/WatermarkTextTests.cs#L46) | 5 | 許可textの空白/結合文字をtrim/normalizeしない | 維持 |
| T448 | [WatermarkTextTests.ByteAndTextElementLimitsIncludeCombiningMarks](../../tests/Amane.Pdf.Api.Tests/WatermarkTextTests.cs#L50) | 1 | 120byteと30文字要素の境界・結合文字扱い | 維持 |
| T449 | [WatermarkTextTests.WidthUsesLockedAsciiAdvancesAndCountsEveryRune](../../tests/Amane.Pdf.Api.Tests/WatermarkTextTests.cs#L66) | 1 | 固定ASCII advanceと全Runeを使う幅計算 | 維持 |
| T450 | [WatermarkTextTests.FontTableHasExpectedCoverageIncludingJisKanji](../../tests/Amane.Pdf.Api.Tests/WatermarkTextTests.cs#L75) | 1 | 固定文字集合数とJIS漢字包含 | 維持 |
| T451 | [WatermarkTextTests.DiagonalFitsRotatedAndUnrotatedBoundsUnlessClampedToOnePoint](../../tests/Amane.Pdf.Api.Tests/WatermarkTextTests.cs#L89) | 1 | 斜め描画角度・最小fontと回転前後のpage内収容 | 維持 |
| T452 | [WatermarkTextTests.CornerShrinksOnNarrowPagesAndUsesPhysicalMargin](../../tests/Amane.Pdf.Api.Tests/WatermarkTextTests.cs#L110) | 1 | cornerの物理余白と狭page縮小（期待式が実装と同形） | 維持 |
