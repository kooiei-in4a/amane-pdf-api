# #134 標準JSON v0.2候補全体：事前検証→表示変換→PDF

**検証用スクリプトのみ。製品コード・API契約・CI・共有mainは変更していない。#133／#134はopen・未完了。** 採用済みの数量桁数／末尾0／両モードとquantityAlignment設定解決を含め、会社JSON・登録帳票設定JSON・発行JSONを解析し、全項目の事前検証→表示変換→既存PDF APIでの描画を確認した。過去のdisplay-values.jsonを読んで描画する方式から進め、今回の全表示値を入力JSONから生成した。

## 採用済みと今回だけの候補条件

| 区分 | 今回使った条件 | 採否・製品への反映 |
| --- | --- | --- |
| 採用済み | 数量はstring、整数部5桁／小数部3桁まで、末尾0保持。decimal／right共通の拡張セル。発行明示値＞登録値＞既定decimal。不正登録は正しい発行指定でも拒否。罫線整理。 | 今回の検証に反映。製品未実装。 |
| 名称・住所 | 既存v0.2例の会社name／宛先recipient.nameは1行16文字、住所は各16文字×最大2行・LF込み32文字まで。品名17文字＋表示用軽減記号。 | 検証上の仮置き。20文字×2行／nameLines、宛先住所・部署・担当者の採否は未確定。 |
| 取引・備考 | 請求書transaction必須、見積書任意。既存notesを取引・備考として3行／48文字。dateなら自由備考2行、periodなら1行。 | 候補条件を検証。専用取引欄や自由備考3行維持の判断は残る。 |
| 数値・税等 | JPY・税抜、非負JSON整数金額。単価8桁、他金額12桁。税率8／10、両taxSummary区分を明示。数量は正の値のみ。 | 数量桁数・文字列保持以外の必須・0／負数・税区分・金額上限は未確定。金額の再計算・相互整合性判定はしない。 |
| 入力・エラー | 各JSON64KiB、未知／重複key・型変換・未対応文字・結合濁点等を拒否。任意項目省略時はラベルも省く。 | 数量モード設定の規則以外は候補契約。今回のstatus／codeはfixture内の例外で、実site HTTP応答ではない。 |
| 登録・参照 | company_demo、invoice/estimate-standard-v1、logo_demo／seal_demoの架空固定カタログをローカルJSONで解決。 | DB／認証・利用者分離・画像登録・版固定は未実装・未確認。settings.jsonのcompanyId/templateIdは検証用レコードの参照情報で、登録APIを新設したものではない。 |

[標準JSON案v0.2](../standard-json-draft-v0.2/README.md)／[初版の推奨仕様と判断事項](../INITIAL-SPEC-RECOMMENDATION.md)の候補を今回まとめて確定したものではない。

## 正常系14件・最終PDF

先頭4件は共有済みv0.2の発行JSON・会社JSON・設定例をそのまま使用した。残りはそれらを基礎にした境界入力。全14件がqpdf検査成功・1ページ、送信fieldsと全欄の抽出が完全一致、矩形外word 0。入力オブジェクト・JSON・定義コピーの非破壊も照合した。

| ケース | 実効 / 出所 | 成果物 | 取引・備考の行 / 文字 | JSON→PDF秒 | bytes | POST |
| --- | --- | --- | --- | ---: | ---: | ---: |
| invoice-1 | decimal / request | [PDF](invoice-1/final.pdf) / [JSON](invoice-1/request.json) / [会社](invoice-1/company.json) / [設定](invoice-1/settings.json) | 2 / 26 | 0.725 | 221,298 | 3 |
| invoice-8 | right / templateSettings | [PDF](invoice-8/final.pdf) / [JSON](invoice-8/request.json) / [会社](invoice-8/company.json) / [設定](invoice-8/settings.json) | 3 / 36 | 0.612 | 221,974 | 3 |
| estimate-1 | right / request | [PDF](estimate-1/final.pdf) / [JSON](estimate-1/request.json) / [会社](estimate-1/company.json) / [設定](estimate-1/settings.json) | 2 / 26 | 0.642 | 220,166 | 3 |
| estimate-8 | decimal / templateSettings | [PDF](estimate-8/final.pdf) / [JSON](estimate-8/request.json) / [会社](estimate-8/company.json) / [設定](estimate-8/settings.json) | 3 / 36 | 0.629 | 220,438 | 3 |
| provided-amounts-unchanged | right / templateSettings | [PDF](provided-amounts-unchanged/final.pdf) / [JSON](provided-amounts-unchanged/request.json) / [会社](provided-amounts-unchanged/company.json) / [設定](provided-amounts-unchanged/settings.json) | 2 / 26 | 0.631 | 222,042 | 3 |
| default-money-boundary | decimal / default | [PDF](default-money-boundary/final.pdf) / [JSON](default-money-boundary/request.json) / [会社](default-money-boundary/company.json) / [設定](default-money-boundary/settings.json) | 2 / 26 | 0.582 | 221,149 | 3 |
| quantity-boundary-decimal | decimal / request | [PDF](quantity-boundary-decimal/final.pdf) / [JSON](quantity-boundary-decimal/request.json) / [会社](quantity-boundary-decimal/company.json) / [設定](quantity-boundary-decimal/settings.json) | 3 / 36 | 0.584 | 222,279 | 3 |
| quantity-boundary-right | right / request | [PDF](quantity-boundary-right/final.pdf) / [JSON](quantity-boundary-right/request.json) / [会社](quantity-boundary-right/company.json) / [設定](quantity-boundary-right/settings.json) | 3 / 36 | 0.663 | 222,285 | 3 |
| date-notes-boundary | decimal / request | [PDF](date-notes-boundary/final.pdf) / [JSON](date-notes-boundary/request.json) / [会社](date-notes-boundary/company.json) / [設定](date-notes-boundary/settings.json) | 3 / 48 | 0.711 | 220,440 | 3 |
| period-notes-boundary | decimal / templateSettings | [PDF](period-notes-boundary/final.pdf) / [JSON](period-notes-boundary/request.json) / [会社](period-notes-boundary/company.json) / [設定](period-notes-boundary/settings.json) | 3 / 43 | 0.701 | 219,674 | 3 |
| estimate-no-transaction | right / request | [PDF](estimate-no-transaction/final.pdf) / [JSON](estimate-no-transaction/request.json) / [会社](estimate-no-transaction/company.json) / [設定](estimate-no-transaction/settings.json) | 3 / 48 | 0.704 | 218,791 | 3 |
| minimal-no-images | decimal / templateSettings | [PDF](minimal-no-images/final.pdf) / [JSON](minimal-no-images/request.json) / [会社](minimal-no-images/company.json) / [設定](minimal-no-images/settings.json) | 0 / 0 | 0.204 | 208,366 | 1 |
| logo-only | decimal / request | [PDF](logo-only/final.pdf) / [JSON](logo-only/request.json) / [会社](logo-only/company.json) / [設定](logo-only/settings.json) | 2 / 26 | 0.404 | 215,427 | 2 |
| seal-only | right / request | [PDF](seal-only/final.pdf) / [JSON](seal-only/request.json) / [会社](seal-only/company.json) / [設定](seal-only/settings.json) | 2 / 26 | 0.394 | 215,467 | 2 |

request＝発行明示、templateSettings＝登録、default＝既定値。先頭4件で登録からの両モードと発行時の両方向上書き、default-money-boundaryで登録／発行とも省略時のdecimalを確認した。[設定解決単体の全9組合せ・不正40件](../quantity-resolution-validation/README.md)も引き続き根拠とする。

### 表示変換・数量・金額

- 会社名・住所・登録番号・連絡先、宛先・発行日・番号・件名・帳票別期限、取引情報・備考、全明細・税率別対象額／税額・合計を、入力から独立に作った期待値と抽出結果で照合した。日付の書式化、金額のカンマ／円、8%明細の※付加だけを候補表示規則に従い加えた。元入力の削除・丸め・補完・数量の数値化はない。
- 数量境界の2ケースは99999.999／99999／0.001／2.50／1.000／3／123.45／0.010、品名17文字＋軽減記号・単価99,999,999円。**この入力を事前検証してから**両モードのfieldsへ変換した。旧全体5文字制限を使わず、123.45／12345.000も代表例で受理。
- [数量の2ページ比較](quantity-boundary-comparison.pdf)は1ページ目decimal、2ページ目right。モード以外の発行JSON、抽出内容・全文raw text、数量以外の文字座標が一致。decimalの小数点画素中心は144dpiで全行一致、整数末尾を含む基準の最大差0.0475pt。rightの右端の最大差0pt。隣接欄との最小間隔は品名側35.9065pt／単価側36.639pt。
- provided-amounts-unchangedで数量1.25・単価100に対し明細98,765円、10%対象12,345円・税777円、合計99,999円をそのまま表示。default-money-boundaryは99999.999・単価99,999,999円、明細／両対象額／両税額／合計を各999,999,999,999円として受理・描画。乗算・税計算・合算・金額間不整合による拒否はしていない。すべて架空の検証値。

### 取引日／期間・備考と任意項目

- date-notes-boundary：取引日1行＋自由備考16文字×2行＝3行48文字を保持。
- period-notes-boundary：発行日2027-01-02とは別に取引期間2026-12-31〜2027-01-01を両端の年付きで表示。期間2行＋自由備考16文字＝3行43文字を保持。
- estimate-no-transaction：見積書で取引省略、自由備考16／16／14文字＝3行48文字を表示。
- minimal-no-images：会社の登録番号／連絡先／銀行／画像、発行の件名／期限／取引・備考を省略。値・該当ラベルが抽出に出ないこと、未使用row1〜row7のfieldsがないことを確認。画像合成を呼ばずfill-templateの1 POSTだけで完了し、最終PDFは文字PDFとbyte一致。
- logo-only／seal-onlyはそれぞれ指定画像だけを合成して各2 POST。通常11件は文字→ロゴ→印影の3 POST。透明／半透明・比率・配置の画素検査成功。銀行情報は請求書のみへ表示する候補規則を明示し、見積書ではラベルごと非適用とした。

請求書8明細、年越し期間の見積書8明細、最大金額の請求書を144dpiで目視し、文字・罫線・画像の衝突／欠けなし。全ケースのfinal.pngで確認可能。Acrobat/pdf.js・実印刷での確認ではない。

## 代表的な入力エラー65件

[エラー一覧・元入力・期待path/code](ERROR-CASES.md)／[実測結果](results.json)。全件を正常系と同じprocess()入口へ通した。実API clientの全request観測件数と既存POSTカウンタを各ケース前後で照合し、全65件が増分0（38→38）、PDF生成なし。呼び出し先をstubに置き換えていない。

代表：必須欠落、0／9明細、数量の数値型・null・整数部6桁・小数部4桁、金額の負数・小数・文字列・boolean・指数・上限超過、税率／税区分不足、未知項目、未対応文字・結合濁点・禁止文字列、存在しない日付・期間逆転／不完全・取引両形式、取引／備考行数超過、不正会社参照・登録会社データ、不正数量モード、壊れたJSON・重複key・64KiB超過。

不正登録モードはnull／leftを、正しい発行decimalでも拒否し、`/templateId`とtemplateSettingsのsource.path/codeを照合。登録会社の不正は`/companyId`と会社登録元sourceを示す。幅超過2件は、入力文字数と実矩形検査を分けるため、**検証用definitionだけを意図的に狭めた再現例**。製品テンプレートを狭めたものではない。

数量0／負数の拒否、会社／宛先16文字制限、備考配分、数量モード以外のstatus/code等は上記の候補条件に基づく結果で、採用済み仕様へ昇格させない。最初のエラーを返すfixtureで、複数エラーの集約・優先順は正式契約未確定。

## 固定版・環境・測定条件

API SHA `465088ed0b575f055beb6851e98d28ed22fb9928`／site資産 SHA `e08aed241245f9f84a7336302edcb7d6a89a57b1`、前回成果物commit `fcae4f1942eeb52b0dd1298a189d0bfcc2d6059e`。背景・定義は固定資産から作った採用罫線／拡張数量欄の検証用コピー。動くsite mainを参照していない。

既存Docker `amane-pdf-api:site134-465088e`、image ID `sha256:67d204ed4e10d643a41ffb248a621ed0a2376660f26f5051d0952c65cc10d0e3`。CPU1／RAM1536MiB（追加swapなし）／tmpfs256MiB／read-only／network none、共有2枠・待ち0。docker exec loopback HTTP、直列、各1回。既存pdfcpu0.16.2・BIZ UDP Gothic1.051・.NET10.0.12、Python3.14.4／Pillow12.1.1／numpy2.3.5／Poppler26.01.0／qpdf12.3.2。新しい基盤・依存なし。

時間はJSON解析・全項目事前検証・設定解決・表示変換の開始から最終PDF本文受信まで。保存・描画／抽出検査・起動時間を除く。通常の画像2枚付き11件は0.582〜0.725秒、全14件は0.204〜0.725秒／208,366〜222,285 bytes。事前検証は約1.3〜1.8msだが、各1回で性能差や保証を判断する試験ではない。全38 POST、health200、成功ごとと終了時にjob・子process残存なしを確認。site経由・同時負荷は今回追加測定していない。

## 再現とコードの根拠

既存[環境準備](../README.md)に従い固定APIソース／Docker imageを用意し、成果物基点で未存在の/tmp出力先を指定する。

```bash
python3 validate-standard-json-v02.py --output /tmp/amane-site134-standard-json-v02-rerun
```

- [改訂スクリプト](../validate-standard-json-v02.py)：[全項目検証・表示変換](../validate-standard-json-v02.py#L27)、[JSON解析と入力不変確認](../validate-standard-json-v02.py#L145)、[共通入口と既存API呼び出し](../validate-standard-json-v02.py#L160)、[エラー入力生成](../validate-standard-json-v02.py#L311)、[実client計数](../validate-standard-json-v02.py#L393)。
- [採用済みresolver](../validate-quantity-resolution.py#L31)、[数量配置](../compare-quantity-modes.py)、[フォント幅と既存検証helper](../validate-standard-json.py)。旧v0.1のmap_fields()は使用せず、helperと新しいmap_fields()を組み合わせる。旧スクリプト・旧測定は変更していない。siteのブラウザ用計算処理は未使用。
- 各成功ケース：company.json／settings.json／request.json、生成display-values・fields・field-index・field-sources・definition・effective・quantity-placement、入力から独立照合したexpected-from-input、final.pdf/png/txt/extracted.json/bbox.html。各失敗ケース：同3入力・error.json、意図的な狭幅時のみdefinitions.json。results.jsonに全caseの実測、script／背景のSHAを保存。[SHA256SUMS](../SHA256SUMS)で共有全ファイルを照合できる。

## A/B/Cと#134に残る判断

A：既存fill-template＋必要なstamp-imageで、採用数量仕様・設定解決を含むv0.2候補の全項目を1ページへ描画できた。画像の有無に応じ1／2／3呼び出しで成立。

B：site側で今回の候補条件を正式仕様へ確定し、事前検証・元入力path/source・表示変換・設定解決・テンプレートへ反映する。今回のスクリプトは製品への組込みではない。

C：今回、具体的なPDF API変更の必要性は確認なし。新規API Issueは作成しない。

**残る判断**：名称20文字×2行／nameLinesと宛先住所・部署・担当者、取引／自由備考配分、その他の必須性・JPY／金額上限／税区分・数量0／負数・任意ラベル等、数量モード以外の入力／エラー契約・標準JSONの版識別、正式な品質・時間・容量基準。候補JSONの通し確認は今回実施済みだが、最終採用した契約へ合わせる再確認と親Issueへの統合を残す。

**未確認／後続**：製品の会社／設定／画像の保存・読込、登録時の省略明示化／更新時省略維持、認証・利用者スコープ・参照解決／版固定、実site HTTP応答・site経由の時間、ブラウザ閲覧・実印刷。これらの製品実装は今回開始せず、#134全体の完了扱いにも含めない。
