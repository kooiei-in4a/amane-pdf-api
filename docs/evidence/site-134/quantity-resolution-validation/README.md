# #134 quantityAlignment設定解決の検証結果

**検証用スクリプトのみ。製品実装・既存API契約・CI・共有mainは変更していない。#133／#134は未完了。** 採用済みの[設定仕様](../QUANTITY-MODE-SETTINGS-DRAFT.md)を、登録設定JSON／発行JSON→解決→fields→既存PDF APIの共通経路で確認した。実サイトの設定保存・読込やHTTPエラー応答を試験したものではない。

## 結果

- 正常9件：登録値と発行値の「省略／decimal／right」の全組合せが期待値どおり。発行明示値＞登録値＞decimal、同値の明示指定も発行値として解決。全ケースで入力オブジェクトと保存した登録／発行JSONは不変。
- 不正40件：全件がfields作成・PDF API呼び出しより前に拒否。各ケースの実client観測件数0、POSTカウンタ差0（全件27→27）、生成PDFなし。正常系・不正系は同じprocess()入口を通す。PDF APIをstubには置き換えていない。
- 不正登録は27件：9種類×発行省略／正しいdecimal／正しいright。**正しい上書きがある18件もすべて拒否。** `/templateId`のregistered-data-invalidと登録元source.path=`/quantityAlignment`、source.codeを照合。
- 不正発行9件：正常な登録rightがあっても、null・不正値を省略扱いにせず拒否。`/quantityAlignment`を示す。
- 残り4件：壊れたJSON、重複key、未知quantityAlign、明細内quantityAlignmentを400相当の構造付きエラーとして拒否。重複／構文不正はroot path。他は元入力pathを照合。

不正値9種類はnull／数値42／boolean true／配列／object／空文字／left／DECIMAL／前後空白付き` right `。先頭5種はinvalid-type、後4種はunsupported-quantity-alignment。trim・型変換・補正・黙ったフォールバックなし。エラーのstatusとcodeは**採用設計をfixture内の例外として検証した値**で、siteの実HTTP応答ではない。[全結果](results.json)に各ケースのerrorと呼び出し数を収録。

## 実効モードから生成したPDF

期待モードは独立した明示表と照合し、描画側にはresolverの返値だけを渡す。各PDFは文字1回＋透過PNGロゴ1回＋印影1回。全9件がqpdf検査成功・1ページ。請求書／見積書の1・8明細を両モードでカバーした。

| ケース | 登録 | 発行 | 実効 / 出所 | 帳票 | 成果物 | 秒 | bytes |
| --- | --- | --- | --- | --- | --- | ---: | ---: |
| 01-default | 省略 | 省略 | decimal / default | invoice-1 | [PDF](01-default/final.pdf) / [入力](01-default/request.json) / [登録](01-default/settings.json) | 0.869 | 221,511 |
| 02-registered-decimal | decimal | 省略 | decimal / templateSettings | estimate-1 | [PDF](02-registered-decimal/final.pdf) / [入力](02-registered-decimal/request.json) / [登録](02-registered-decimal/settings.json) | 0.717 | 220,431 |
| 03-registered-right | right | 省略 | right / templateSettings | invoice-8 | [PDF](03-registered-right/final.pdf) / [入力](03-registered-right/request.json) / [登録](03-registered-right/settings.json) | 0.643 | 222,285 |
| 04-request-decimal | 省略 | decimal | decimal / request | estimate-8 | [PDF](04-request-decimal/final.pdf) / [入力](04-request-decimal/request.json) / [登録](04-request-decimal/settings.json) | 0.692 | 220,748 |
| 05-request-right | 省略 | right | right / request | invoice-1 | [PDF](05-request-right/final.pdf) / [入力](05-request-right/request.json) / [登録](05-request-right/settings.json) | 0.667 | 221,512 |
| 06-same-decimal | decimal | decimal | decimal / request | invoice-8 | [PDF](06-same-decimal/final.pdf) / [入力](06-same-decimal/request.json) / [登録](06-same-decimal/settings.json) | 0.683 | 222,279 |
| 07-override-right | decimal | right | right / request | estimate-8 | [PDF](07-override-right/final.pdf) / [入力](07-override-right/request.json) / [登録](07-override-right/settings.json) | 0.607 | 220,753 |
| 08-override-decimal | right | decimal | decimal / request | estimate-1 | [PDF](08-override-decimal/final.pdf) / [入力](08-override-decimal/request.json) / [登録](08-override-decimal/settings.json) | 0.588 | 220,431 |
| 09-same-right | right | right | right / request | estimate-1 | [PDF](09-same-right/final.pdf) / [入力](09-same-right/request.json) / [登録](09-same-right/settings.json) | 0.685 | 220,432 |

数量は全1明細で99999.999、8明細で99999.999／99999／0.001／2.50／1.000／3／123.45／0.010。全欄の抽出が送信fieldsと完全一致、矩形外word 0。数量・末尾0・単価・税率別対象額／税額・明細金額・合計が入力の確定値と一致し、乗算・税計算・合算はしない。数量と単価・金額は意図的な不整合を含む架空例。

[前回の同モード出力](../quantity-mode-comparison/README.md)とfields・全欄抽出・全文raw textが一致し、数量以外の文字と座標も一致。decimalの小数点画素中心は144dpiで全行一致、整数末尾を含む基準の最大差0.0475pt。rightの文字列右端の最大差0pt。品名17文字＋軽減記号・単価99,999,999円の境界入力でも非交差、最小間隔は品名側35.9065pt／単価側36.639pt。全9件で透明／半透明PNGの透過・比率・配置の画素検査成功。請求書8明細decimalと見積書8明細rightを144dpiで目視し、文字・線・画像の欠けや衝突なし。

[請求書8明細decimalのプレビュー](06-same-decimal/final.png) / [見積書8明細rightのプレビュー](07-override-right/final.png)。各ケースに実効値effective.json、送信fields、field-index、quantity-placement、final.pdf/png/txt/extracted.json/bbox.html、独立したconfirmed-valuesを同梱。不正ケースはrejected配下にsettings.json／request.json／error.json（malformedケースは意図した不正JSON）を収録。

## 環境・時間と呼び出し

API固定SHA `465088ed0b575f055beb6851e98d28ed22fb9928`、site資産固定SHA `e08aed241245f9f84a7336302edcb7d6a89a57b1`。元成果物commit `8a7ae693d23c70e96e8c217de6f7e837f9a71a5f`。API Docker image `sha256:67d204ed4e10d643a41ffb248a621ed0a2376660f26f5051d0952c65cc10d0e3`。

CPU1／RAM1536MiB（追加swapなし）／tmpfs256MiB／read-only／network none、既存共有2枠・待ち0。docker execからloopback HTTP、直列、各1回。既存pdfcpu0.16.2・BIZ UDP Gothic1.051・.NET10.0.12、Python3.14.4／Pillow12.1.1／numpy2.3.5／Poppler26.01.0／qpdf12.3.2。新しい依存なし。

3回連続呼び出しは0.588〜0.869秒、最終220,431〜222,285 bytes。時間はfill-template開始から最後のPDF本文受信までで、設定解決・ファイルI/O・文字抽出／画像検査は除く。設定解決の性能や両モードの速度差を評価する測定ではない。成功9チェーン＝27 POST、全40拒否＝0リクエスト、health200。各チェーン後と終了時のassert_cleanでjob・子process残存なし。startup／終了時healthはケース観測外。

## 再現

既存[環境準備](../README.md)で同じ固定APIソース／imageを用意し、成果物基点で実行する。出力先は未存在の/tmp配下を指定する。

```bash
python3 validate-quantity-resolution.py --output /tmp/amane-site134-quantity-resolution-rerun
```

[検証スクリプト](../validate-quantity-resolution.py)の[resolve()](../validate-quantity-resolution.py#L31)が全指定値の型・値検証→優先順位、[process()](../validate-quantity-resolution.py#L57)が解決後だけ描画、main()が独立した9件の期待表と40件のエラー・client呼び出し数を照合する。既存read_json／モード配置／実API連続呼び出し／抽出・画素検査を再利用。[実測・SHA](results.json)と[全成果物ハッシュ](../SHA256SUMS)を参照。中間の文字のみ／ロゴ後PDF・PNGはローカル検証先に保持し、共有版には最終出力を掲載する。

## A/B/Cと残る範囲

A：既存APIで、解決したdecimal／rightを両帳票1／8明細へ反映できる。新しいAPI align値は不要。

B：設定解決と描画前の拒否、decimalの文字幅による配置はsite側。今回のfixtureで規則と描画接続は確認できたが、製品へ反映していない。

C：今回、具体的なPDF API変更の必要性は確認なし。新規API Issueは作成しない。

残作業：名称・宛先情報、取引／備考配分、その他の必須・型・金額上限・税区分・品質／性能基準の採否、v0.2全体の事前検証→変換→PDFの一連確認。今回のresolverは数量設定だけのfixtureで、他項目は前回検証の表示値を再利用した。汎用v0.2 validatorを実装／検証したものではない。会社／帳票設定のDB保存、登録時の省略明示化、更新時省略保持、認証・参照解決／版固定、実製品HTTPエラー、site経由測定・Acrobat/pdf.js・印刷は未確認。製品実装は後続の承認された作業として扱い、今回開始しない。

POSTカウンタの根拠は固定版の[既存Api.request](https://github.com/kooiei-in4a/amane-pdf-api/blob/465088ed0b575f055beb6851e98d28ed22fb9928/scripts/docker-smoke.py#L117)。このclientを包み、GETを含む全requestも別に観測して拒否ケースで両方の増分0を確認した。
