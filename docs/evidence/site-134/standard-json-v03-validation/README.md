# #134 標準JSON v0.3 nameLinesからの通し検証

**検証スクリプトと架空データのみ。製品コード・既存API契約・CI・共有mainは未変更。#133／#134はopen・未完了。** 会社nameLines・recipient.nameLines・会社addressLinesの各20文字×最大2行、宛先の部署・担当者・敬称、数量両モード・設定解決を、JSON解析・事前検証→LF結合・表示変換→既存PDF APIで確認した。過去の表示値JSONは読まず、今回の入力から全fieldsを作っている。

## 結果

- 正常18件がすべてqpdf検査成功・1ページ、全欄の抽出が送信fieldsと完全一致、矩形外word 0。会社・宛先・住所・日付・取引／備考・数量・確定金額は、元入力から独立に作った期待値とも一致。
- 20×2境界は請求書／見積書×1／8明細×decimal／rightの8件。会社名・会社住所・宛先の3欄を同時に各20＋20＋LF＝41文字として受理・表示。各行の実幅180ptは幅202.3pt／9ptのセルに収まった。名称／住所wordと他欄のwordの非交差も全件確認。
- 長名・部署／担当者／敬称は両帳票1／8明細で4件。請求書は「営業企画部 架空花子 様」、見積書は「営業企画部 御中」を第2行へ表示。敬称の補完・短縮・自動改行・正規化なし。
- 不正66件は全件をAPIより前に拒否。実clientの全request観測とPOSTカウンタを各ケース前後で照合し、増分0（54→54）、生成PDFなし。正常・不正は同じprocess()入口を通し、PDF APIをstubには置き換えていない。

## PDF・入力・実測

通常4帳票と確定金額保持例はv0.3公開例を使用。rolesは長名会社例＋長名宛先例で、1明細は通常1明細例に同じ宛先を適用。boundaryは3欄の20文字×2行を同時に使った境界入力。

| ケース | 実効モード / 出所 | 成果物 | JSON→PDF秒 | bytes | 行要素数 |
| --- | --- | --- | ---: | ---: | ---: |
| invoice-1 | decimal / request | [PDF](invoice-1/final.pdf) / [発行](invoice-1/request.json) / [会社](invoice-1/company.json) / [設定](invoice-1/settings.json) | 0.679 | 221,298 | 47 |
| invoice-8 | right / templateSettings | [PDF](invoice-8/final.pdf) / [発行](invoice-8/request.json) / [会社](invoice-8/company.json) / [設定](invoice-8/settings.json) | 0.599 | 221,974 | 83 |
| estimate-1 | right / request | [PDF](estimate-1/final.pdf) / [発行](estimate-1/request.json) / [会社](estimate-1/company.json) / [設定](estimate-1/settings.json) | 0.597 | 220,166 | 44 |
| estimate-8 | decimal / templateSettings | [PDF](estimate-8/final.pdf) / [発行](estimate-8/request.json) / [会社](estimate-8/company.json) / [設定](estimate-8/settings.json) | 0.747 | 220,438 | 80 |
| provided-amounts-unchanged | right / templateSettings | [PDF](provided-amounts-unchanged/final.pdf) / [発行](provided-amounts-unchanged/request.json) / [会社](provided-amounts-unchanged/company.json) / [設定](provided-amounts-unchanged/settings.json) | 0.601 | 222,042 | 47 |
| invoice-1-roles | decimal / request | [PDF](invoice-1-roles/final.pdf) / [発行](invoice-1-roles/request.json) / [会社](invoice-1-roles/company.json) / [設定](invoice-1-roles/settings.json) | 0.583 | 224,831 | 49 |
| invoice-8-roles | right / templateSettings | [PDF](invoice-8-roles/final.pdf) / [発行](invoice-8-roles/request.json) / [会社](invoice-8-roles/company.json) / [設定](invoice-8-roles/settings.json) | 0.698 | 225,433 | 85 |
| estimate-1-roles | right / request | [PDF](estimate-1-roles/final.pdf) / [発行](estimate-1-roles/request.json) / [会社](estimate-1-roles/company.json) / [設定](estimate-1-roles/settings.json) | 0.625 | 223,541 | 46 |
| estimate-8-roles | decimal / templateSettings | [PDF](estimate-8-roles/final.pdf) / [発行](estimate-8-roles/request.json) / [会社](estimate-8-roles/company.json) / [設定](estimate-8-roles/settings.json) | 0.709 | 223,778 | 82 |
| invoice-1-boundary-decimal | decimal / request | [PDF](invoice-1-boundary-decimal/final.pdf) / [発行](invoice-1-boundary-decimal/request.json) / [会社](invoice-1-boundary-decimal/company.json) / [設定](invoice-1-boundary-decimal/settings.json) | 0.624 | 218,866 | 49 |
| invoice-1-boundary-right | right / request | [PDF](invoice-1-boundary-right/final.pdf) / [発行](invoice-1-boundary-right/request.json) / [会社](invoice-1-boundary-right/company.json) / [設定](invoice-1-boundary-right/settings.json) | 0.609 | 218,866 | 49 |
| invoice-8-boundary-decimal | decimal / templateSettings | [PDF](invoice-8-boundary-decimal/final.pdf) / [発行](invoice-8-boundary-decimal/request.json) / [会社](invoice-8-boundary-decimal/company.json) / [設定](invoice-8-boundary-decimal/settings.json) | 0.633 | 219,527 | 85 |
| invoice-8-boundary-right | right / templateSettings | [PDF](invoice-8-boundary-right/final.pdf) / [発行](invoice-8-boundary-right/request.json) / [会社](invoice-8-boundary-right/company.json) / [設定](invoice-8-boundary-right/settings.json) | 0.753 | 219,532 | 85 |
| estimate-1-boundary-decimal | decimal / request | [PDF](estimate-1-boundary-decimal/final.pdf) / [発行](estimate-1-boundary-decimal/request.json) / [会社](estimate-1-boundary-decimal/company.json) / [設定](estimate-1-boundary-decimal/settings.json) | 0.630 | 217,277 | 46 |
| estimate-1-boundary-right | right / request | [PDF](estimate-1-boundary-right/final.pdf) / [発行](estimate-1-boundary-right/request.json) / [会社](estimate-1-boundary-right/company.json) / [設定](estimate-1-boundary-right/settings.json) | 0.602 | 217,278 | 46 |
| estimate-8-boundary-decimal | decimal / request | [PDF](estimate-8-boundary-decimal/final.pdf) / [発行](estimate-8-boundary-decimal/request.json) / [会社](estimate-8-boundary-decimal/company.json) / [設定](estimate-8-boundary-decimal/settings.json) | 0.587 | 217,443 | 82 |
| estimate-8-boundary-right | right / request | [PDF](estimate-8-boundary-right/final.pdf) / [発行](estimate-8-boundary-right/request.json) / [会社](estimate-8-boundary-right/company.json) / [設定](estimate-8-boundary-right/settings.json) | 0.642 | 217,445 | 82 |
| default-decimal | decimal / default | [PDF](default-decimal/final.pdf) / [発行](default-decimal/request.json) / [会社](default-decimal/company.json) / [設定](default-decimal/settings.json) | 1.136 | 222,043 | 47 |

request＝発行明示、templateSettings＝登録値、default＝既定decimal。全18件はfill-template 1回→透過PNGロゴ→印影の3 POST。入力オブジェクト・会社／発行／設定JSON・元定義は処理前後で不変。

比較PDFは1ページ目decimal、2ページ目right。元の最終PDFはすべて1ページ：
[請求書1明細](invoice-1-comparison.pdf)／[8明細](invoice-8-comparison.pdf)、[見積書1明細](estimate-1-comparison.pdf)／[8明細](estimate-8-comparison.pdf)。

同じ帳票・明細数ではquantityAlignment以外の発行入力が一致。両モードの全欄抽出・全文raw text・数量以外のword座標が一致し、名称・住所等を移動していない。

## 設定解決・数量・確定金額

独立した期待表により、登録／発行それぞれ省略・decimal・rightの全9組合せを確認。描画へ渡すmodeはresolverの返値だけで、期待表から選んでいない。

| 登録 | 発行 | 実効 / 出所 | ケース |
| --- | --- | --- | --- |
| 省略 | 省略 | decimal / default | default-decimal |
| 省略 | decimal | decimal / request | invoice-1-boundary-decimal |
| 省略 | right | right / request | invoice-1-boundary-right |
| decimal | 省略 | decimal / templateSettings | invoice-8-boundary-decimal |
| right | 省略 | right / templateSettings | invoice-8-boundary-right |
| decimal | decimal | decimal / request | estimate-1-boundary-decimal |
| right | right | right / request | estimate-1-boundary-right |
| right | decimal | decimal / request | estimate-8-boundary-decimal |
| decimal | right | right / request | estimate-8-boundary-right |

数量境界は1明細で99999.999、8明細で99999.999／99999／0.001／2.50／1.000／3／123.45／0.010。stringと末尾0を保持し、通常見積書1明細の12345.000も表示。decimalは整数末尾を含む基準の最大差0.0475pt、小数点画素中心は144dpiで全行一致。rightは文字列右端の最大差0pt。数量と品名・単価は全件非交差。今回は通常の品名・単価であり、17文字品名等の同時境界は過去の[数量比較](../quantity-mode-comparison/README.md)と区別する。

provided-amounts-unchanged／default-decimalは、数量1.25・単価100に対し明細98,765円、10%対象12,345円／税777円、合計99,999円という意図的な不整合を、right／既定decimalそれぞれでそのまま表示。全18件で明細金額・税率別対象額／税額・合計を元入力と照合。乗算・税計算・合算・相互整合性判定なし。

全18件で透過／半透過PNGの画素・比率・配置を既存検査で確認。請求書8明細境界decimal、見積書1明細境界right、両帳票8明細rolesを144dpiで目視し、名称・欄・罫線・画像の欠け／衝突なし。

## エラー66件・API呼び出し0

[全エラー・入力・path/code/source](ERROR-CASES.md)／[実測JSON](results.json)。全件でclient.requestの全method・pathと既存post_countを観測し、リクエスト増分0・POST増分0・生成PDFなし。

| 件数 | 入力 |
| ---: | --- |
| 36 | 宛先nameLines／会社nameLines／会社addressLines×12種：21文字、3行、空配列、空行、全空白行、null、配列でなくstring、要素null／数値、要素内LF／CR、未対応文字。 |
| 4 | 会社／宛先の旧nameのみ、および旧新同時指定。変換・併合せず拒否。 |
| 4 | recipient.addressLines／department／contactName／honorificの独立key。unknown-field。 |
| 3 | 宛先名・会社名・会社住所の必須欠落。 |
| 3 | 実幅超過。検証用definitionだけを意図的に狭め、宛先nameLines/0／会社sourceのnameLines/0・addressLines/0を示す。 |
| 7 | 9明細、amount欠落、数量整数部6桁／小数部4桁／数値型、金額小数、期間＋備考2行。 |
| 6 | 発行モードnull／left、不正登録null／left×正しい発行decimal／right。不正登録を正しい発行値でも拒否。 |
| 3 | 壊れたJSON、nameLines重複key、64KiB超過。 |

会社不正は発行時422相当のregistered-data-invalidと/companyId、およびsource.kind=company／元登録pathを示す。旧登録nameのunknown-fieldもsourceに含む。発行の未知keyは400相当。不正登録モードは/templateId＋templateSettingsのsource.path/codeを照合した。

21文字はtoo-long、3行はline-count、不正型はinvalid-type、要素内CR/LFはinvalid-line-break。**数量モード以外のstatus/codeは候補契約としてfixture内で確認した値で、正式HTTP契約・実site応答ではない。** first-error方式で、複数エラー集約順も未確定。

## 固定版・測定条件

API SHA 465088ed0b575f055beb6851e98d28ed22fb9928／site資産 SHA e08aed241245f9f84a7336302edcb7d6a89a57b1。v0.3・定義の元成果物commit ad6c7b1ea67cf719b843c7507691e85dbb79f6eb。固定資産からの検証用コピーで、issuer／recipient／addressの幅.34・multiline・2行・maxLength41と、採用済み数量／品名セルを使用。動くmainは参照しない。

既存Docker amane-pdf-api:site134-465088e、image ID sha256:67d204ed4e10d643a41ffb248a621ed0a2376660f26f5051d0952c65cc10d0e3。CPU1／RAM1536MiB（追加swapなし）／tmpfs256MiB／read-only／network none、共有2枠・待ち0。docker exec loopback HTTP、直列、各1回。pdfcpu0.16.2／BIZ UDP Gothic1.051／.NET10.0.12、Python3.14.4／Pillow12.1.1／numpy2.3.5／Poppler26.01.0／qpdf12.3.2。新しい依存・基盤なし。

JSON解析・事前検証・表示変換の開始から最終PDF本文受信まで0.583〜1.136秒、最終217,277〜225,433 bytes。保存・描画／抽出検査・起動時間は除く。54 POST（18×3）、health200、各成功後と終了時のassert_cleanでjob・子process残存なし。各1回の条件付き測定で、速度差・SLA・site経由の性能を判断する試験ではない。同時負荷は追加測定していない。

## 再現とコード根拠

既存[環境準備](../README.md)で固定APIソース／Docker imageを用意し、成果物基点で未存在の/tmp出力先を指定する。

~~~bash
python3 validate-standard-json-v03.py --output /tmp/amane-site134-standard-json-v03-rerun
~~~

[スクリプト](../validate-standard-json-v03.py)：[名称配列](../validate-standard-json-v03.py#L28)、[会社情報](../validate-standard-json-v03.py#L40)、[全項目検証／変換](../validate-standard-json-v03.py#L55)、[共通入口とAPI](../validate-standard-json-v03.py#L191)、[名称と他欄の非交差](../validate-standard-json-v03.py#L270)、[実client計数](../validate-standard-json-v03.py#L394)。

旧v0.2の解析・文字／金額検査・画像検査、[採用済みresolver](../validate-quantity-resolution.py#L31)と数量配置、既存smoke／reproduce.pyを再利用。旧スクリプト・製品・ブラウザ計算処理は変更していない。

成功ケースはcompany.json／settings.json／request.json、変換したdisplay-values／fields／field-index／field-sources／definition／effective／quantity-placement、独立照合のexpected-from-input、final.pdf/png/txt/extracted.json/bbox.html。不正ケースは3入力＋error.json、狭幅時のみdefinitions.json。results.jsonに全測定とscript／背景SHA、[SHA256SUMS](../SHA256SUMS)に共有全ハッシュを記録。中間PDF・PNGはローカルに保持し、共有版は最終出力を掲載。

## A/B/C・残作業

A：既存APIで、採用済みnameLines・名称／会社住所20×2・数量両モード／設定を組み合わせ、両帳票1／8明細を1ページへ表示できた。文字1回＋画像2回で成立。

B：site側の事前検証・元入力path/source・表示変換・設定解決・テンプレート整合の製品反映は別作業。検証用コピーを製品実装済みと扱わない。

C：今回のための具体的なPDF API変更は確認なし。新規API Issueは作成しない。

採用済み名称・数量・設定のfixture通し検証は今回実施済み。#134には取引／備考配分、名称以外の必須性・金額／税区分・数量0／負数・任意ラベル、数量モード以外のHTTP契約・版識別、正式な品質／時間／容量基準などの採否と、最終条件に合わせる確認を残す。

その他の未確定仕様は候補条件（JPY・非負整数金額・税率8/10・請求書transaction必須／見積書任意・取引／備考3行等）で検証しただけで、採用決定へ昇格させない。製品保存／読込・登録／更新・認証／利用者スコープ・参照解決／版固定、実site HTTP・site経由時間、Acrobat/pdf.js・実印刷は未確認。製品実装は開始せず、両Issueは未完了を維持する。
