# #134 標準JSON案からの実描画検証

標準JSON案 v0.1から検証専用スクリプトで表示文字列へ変換し、既存APIで請求書・見積書を生成した。**製品実装・公開API・既存契約・CI・共有mainは未変更。** 案の必須性・上限・HTTP仕様を確定したことにはしない。#133／#134はopenのまま。

- [検証した標準JSON案](../standard-json-draft/README.md)：固定提案commit `ffa49b4228faa24e0d5166a5eeb07c25d689a58c`
- [検証専用スクリプト](../validate-standard-json.py)、[測定・照合結果](results.json)
- API `465088ed0b575f055beb6851e98d28ed22fb9928`、site資産 `e08aed241245f9f84a7336302edcb7d6a89a57b1`。背景は採用した罫線整理案、合計12ptを使用。

## 結果

| ケース | 生成PDF | 発行入力 | JSON読込～最終PDF秒 | 最終bytes | 取引・備考 行／文字 |
| --- | --- | --- | ---: | ---: | ---: |
| 請求書1明細 | [PDF](invoice-1/final.pdf) / [PNG](invoice-1/final.png) | [JSON](invoice-1/request.json) | 0.692 | 221,148 | 2 / 26 |
| 請求書8明細 | [PDF](invoice-8/final.pdf) / [PNG](invoice-8/final.png) | [JSON](invoice-8/request.json) | 0.613 | 221,818 | 3 / 36 |
| 見積書1明細 | [PDF](estimate-1/final.pdf) / [PNG](estimate-1/final.png) | [JSON](estimate-1/request.json) | 0.602 | 220,080 | 2 / 26 |
| 見積書8明細 | [PDF](estimate-8/final.pdf) / [PNG](estimate-8/final.png) | [JSON](estimate-8/request.json) | 0.702 | 220,275 | 3 / 36 |
| 不整合な確定金額 | [PDF](provided-amounts-unchanged/final.pdf) / [PNG](provided-amounts-unchanged/final.png) | [JSON](provided-amounts-unchanged/request.json) | 0.585 | 222,042 | 2 / 26 |
| 取引日＋備考上限 | [PDF](date-notes-boundary/final.pdf) / [PNG](date-notes-boundary/final.png) | [JSON](date-notes-boundary/request.json) | 0.682 | 220,292 | 3 / 48 |
| 年またぎ期間＋備考上限 | [PDF](period-notes-boundary/final.pdf) / [PNG](period-notes-boundary/final.png) | [JSON](period-notes-boundary/request.json) | 0.573 | 221,030 | 3 / 43 |
| 取引情報なしの見積書 | [PDF](estimate-no-transaction/final.pdf) / [PNG](estimate-no-transaction/final.png) | [JSON](estimate-no-transaction/request.json) | 0.658 | 218,706 | 3 / 48 |
| 候補金額上限 | [PDF](money-boundary/final.pdf) / [PNG](money-boundary/final.png) | [JSON](money-boundary/request.json) | 0.652 | 221,112 | 2 / 26 |

全9ケースの最終PDFはqpdf検査成功・1ページ。Popplerの欄別文字抽出は変換した全fieldsと完全一致、矩形外のwordは0。数量の末尾0（2.50等）を保持。登録会社情報・発行日・取引情報・明細・金額も文字として残る。全ケースで既存の透明／半透明PNGの画素・縦横比・配置検査も成功した。144dpiで通常4帳票と取引情報／備考境界2帳票を目視確認し、欠け・線との衝突なし。

### 取引日／期間と備考

- 1明細例は `取引日 2026/10/20`＋備考1行＝2行・26文字。
- 8明細例は `取引期間`、`2026/10/01～2026/10/31`、備考1行＝3行・36文字。
- [取引日上限](date-notes-boundary/final.pdf)：取引日1行＋16文字の備考2行＝**48文字・3行ちょうど**で全文字を抽出。
- [年またぎ期間](period-notes-boundary/final.pdf)：`2026/12/31～2027/01/01`＋16文字の備考1行＝**43文字・3行**。両端の年を省かずに表示・抽出。
- [見積書で取引情報を省略](estimate-no-transaction/final.pdf)：備考3行・LF込み48文字が収まる。
- 取引日＋備考3行、期間＋備考2行は事前検証で明示拒否し、黙って行を落とさない。

検証用definitionコピーの `label.notes.text` だけを追加提案の「取引・備考」にした。文字矩形・行間は変えていない。この共有枠の成立を確認したが、自由備考3行を常に必要とするなら別枠が必要で、最終採否は未確定。

### 確定金額

[不整合値ケース](provided-amounts-unchanged/request.json)は数量1.25・単価100に対し、明細98,765円、10%対象額12,345円、税額777円、合計99,999円を指定。[最終PDF](provided-amounts-unchanged/final.pdf)と[抽出した確定値](provided-amounts-unchanged/confirmed-values-extracted.json)がそのまま一致した。入力からの乗算・税計算・合算・金額間の整合性判定は行わない。

単価99,999,999円、明細・対象額・税額・合計999,999,999,999円の[候補上限ケース](money-boundary/final.pdf)も収まり、桁落ち・丸めなし。これは業務上の金額上限の承認ではなく、指定された上限案の描画確認。

## 入力エラー：ローカル事前検証

下表は**検証スクリプトのfixture用validatorが返した結果**。HTTP serverを実装していないため、statusは標準JSON案の候補値であり、siteの実HTTP応答ではない。全26ケースはPDF API呼び出し前に拒否し、PDFを作っていない。正常ケースの映像を流用して成功扱いにすることもない。各ディレクトリに再現requestと `preflight-error.json` を同梱。

| ケース | status案 | 元入力path | code |
| --- | ---: | --- | --- |
| [nine-items](errors/nine-items/preflight-error.json) | 422 | `/items/8` | `too-many-items` |
| [empty-items](errors/empty-items/preflight-error.json) | 422 | `/items` | `too-few-items` |
| [missing-amount](errors/missing-amount/preflight-error.json) | 422 | `/items/0/amount` | `required` |
| [numeric-quantity](errors/numeric-quantity/preflight-error.json) | 422 | `/items/0/quantity` | `invalid-type` |
| [long-quantity](errors/long-quantity/preflight-error.json) | 422 | `/items/0/quantity` | `quantity-format` |
| [negative-amount](errors/negative-amount/preflight-error.json) | 422 | `/items/0/amount` | `out-of-range` |
| [fractional-amount](errors/fractional-amount/preflight-error.json) | 422 | `/items/0/amount` | `invalid-integer` |
| [money-over-limit](errors/money-over-limit/preflight-error.json) | 422 | `/totalAmount` | `out-of-range` |
| [unsupported-tax](errors/unsupported-tax/preflight-error.json) | 422 | `/items/0/taxRate` | `unsupported-tax-rate` |
| [unknown-recipient-field](errors/unknown-recipient-field/preflight-error.json) | 400 | `/recipient/addressLines` | `unknown-field` |
| [unsupported-character](errors/unsupported-character/preflight-error.json) | 422 | `/recipient/name` | `unsupported-character` |
| [combining-mark](errors/combining-mark/preflight-error.json) | 422 | `/recipient/name` | `combining-mark-not-supported` |
| [placeholder](errors/placeholder/preflight-error.json) | 422 | `/recipient/name` | `forbidden-sequence` |
| [invalid-date](errors/invalid-date/preflight-error.json) | 422 | `/transaction/date` | `invalid-date` |
| [reversed-period](errors/reversed-period/preflight-error.json) | 422 | `/transaction/period/end` | `invalid-period` |
| [ambiguous-transaction](errors/ambiguous-transaction/preflight-error.json) | 422 | `/transaction` | `ambiguous-transaction` |
| [missing-invoice-transaction](errors/missing-invoice-transaction/preflight-error.json) | 422 | `/transaction` | `required` |
| [period-and-two-notes](errors/period-and-two-notes/preflight-error.json) | 422 | `/notesLines/1` | `notes-budget-exceeded` |
| [date-and-three-notes](errors/date-and-three-notes/preflight-error.json) | 422 | `/notesLines/2` | `notes-budget-exceeded` |
| [wrong-expiry](errors/wrong-expiry/preflight-error.json) | 422 | `/validUntil` | `not-applicable` |
| [invalid-company-reference](errors/invalid-company-reference/preflight-error.json) | 422 | `/companyId` | `invalid-reference` |
| [registered-company-character](errors/registered-company-character/preflight-error.json) | 422 | `/companyId` | `registered-data-invalid` |
| [narrow-template-fixture](errors/narrow-template-fixture/preflight-error.json) | 422 | `/recipient/name` | `field-overflow` |
| [invalid-json](errors/invalid-json/preflight-error.json) | 400 | `(root)` | `invalid-json` |
| [duplicate-key](errors/duplicate-key/preflight-error.json) | 400 | `(root)` | `duplicate-key` |
| [exponent-money](errors/exponent-money/preflight-error.json) | 422 | `/items/0/amount` | `invalid-integer` |

`registered-company-character`は発行データの `/companyId` に加え、登録会社の `/name` と原因codeをsourceに返す。`narrow-template-fixture`は文字数内でも幾何検査が必要なことを示すため、検証用コピーの宛先幅だけを狭めたケース。製品テンプレートは変更していない。

## 入力エラー：実際のPDF API

事前検証を意図的に通さず、位置付きfieldsを直接送った3件を別途確認した。これらは既存APIの**実HTTP応答**。

| ケース | 実HTTP | reason | 応答 |
| --- | ---: | --- | --- |
| 40文字の宛先 | 422 | field-overflow | [Problem Details](raw-overflow.problem.json) |
| 𠮷を含む宛先 | 400 | なし | [Problem Details](raw-unsupported.problem.json) |
| 空文字の宛先 | 400 | なし | [Problem Details](raw-empty.problem.json) |

すべてPDFを返さず、APIの一般的なエラーには元入力pathがない。site側の事前検証・path対応が引き続き必要。9明細の業務上限や請求書の取引情報必須などは、既存描画APIの責務ではない。

## 実行条件・範囲

既存 `reproduce.py` の起動・multipart・文字抽出・画像照合・cleanup処理を再利用。Python標準ライブラリと既存Pillow/numpy、qpdf/Popplerを使用し、新規依存・基盤は追加していない。

CPU 1・RAM1536MiB（追加swapなし）・tmpfs256MiB・read-only・network none、既存共有2枠／待ち0の1コンテナ。既存と同じイメージIDをresults.jsonへ記録。経過時間は会社／発行JSONの読み取り・検証・マッピング開始から、fill-template 1回→stamp-image 2回の最終本文受信まで。起動、成果物保存、PNG描画・文字抽出の検査時間は除外。各1回のローカルdocker exec loopback HTTP測定であり、site中継や本番性能の測定ではない。

計30 POST（成功9×3＋実APIエラー3）、health 200、memory.peak 87,707,648 bytes、OOM/oom_kill 0、各成功／失敗後に既存assert_cleanでjob・子process残存0を確認。長時間負荷や同時2件の新たな測定はしていない。

`company_demo`と画像2件はfixtureへの固定参照。DB・認証・保存・テナント分離・画像更新競合・製品HTTPエラー応答の試験ではない。スクリプトは今回の入力・候補仕様を確かめるためのもので、完全な製品validatorではない。画像なし／片方のみ、64KiB境界、任意項目の全組み合わせ、pdf.js/Acrobat・実印刷・site経由・キャンセル／期限超過は今回未検証。

## 成果物・再現

各ケース：`company.json` / `request.json`、変換した `display-values.json` / `fields.json` / `field-index.json` / `field-sources.json`、検証用 `definition.json`、`final.pdf` / `final.png` / `final.txt` / `final.extracted.json` / `confirmed-values-extracted.json`。ローカルには文字だけ／ロゴ後の中間PDF・PNGも保存。共有版には最終PDF・PNGと再現に必要なJSONを掲載する。

既存の検証ディレクトリ（または成果物専用ブランチの `docs/evidence/site-134`）で実行：

```bash
python3 validate-standard-json.py --output /tmp/amane-site134-standard-json-rerun
```

outputは未存在のディレクトリを指定する。Dockerイメージ・api-sourceの準備は[成果物一覧](../README.md)の既存手順を使用。スクリプトは製品ファイルを変更しない。

## 判断

- **A：既存APIで成立**。標準JSON案からの固定8明細・1ページ、取引情報／備考の共有3行、確定金額保持、文字＋透過PNG2枚を実測で確認。
- **B：site側**。案を確定した後、入力検証・元入力path対応、金額を再計算しない変換、取引情報と備考の配分・ラベル、任意欄の扱いを製品側へ反映する作業が残る。会社名16文字等の業務上の妥当性・宛先住所の追加・ID／版・正式な品質／性能基準は未確定。
- **C：API変更の必要性は今回も確認なし**。新規API Issueなし。#134を完了扱いにせず、候補契約の判断とsite側の残作業を維持する。製品実装は開始しない。
