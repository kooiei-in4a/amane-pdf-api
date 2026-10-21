# #134 標準JSON案 v0.2：数量の小数点揃え・整数部5桁／小数部3桁を採用

**数量の桁数・小数点揃え・末尾0保持はユーザー採用済み。標準JSON全体と製品APIは未確定・未実装。** 会社登録と発行入力を分離し、確定金額を再計算せず、固定8明細・1ページで描画する。製品コード・API契約・CI・共有mainは変更していない。

今回の採用範囲は、`items[i].quantity`を文字列として扱い、整数部最大5桁・小数部最大3桁（小数点込み最大9文字）、入力された末尾0を保持して小数点位置を揃えること。旧「全体5文字」上限を置き換える。小数点なしの整数は、同じ基準位置に末尾を揃える。

**追補：数量配置は2モードを用意する方針。** ユーザーの追加方針により、小数点揃えに加えて当初の右揃えも選択できるようにする。下記の小数点基準の計算はdecimalモードへ適用する。両モードとも整数部5桁・小数部3桁と末尾0保持を共通にし、数量の入力値・型・金額を変えない。既定モードは小数点揃えを推奨するが未確定。

罫線整理方針は既に採用済み。それ以外の必須性・金額／税区分・会社名／宛先の構造・備考配分・HTTP仕様等は継続検討の案であり、今回まとめて承認したものではない。特に会社名／住所の20文字×2行と`nameLines`への改訂は[判断資料](../INITIAL-SPEC-RECOMMENDATION.md)の未確定事項で、本書の既存名称keyをその案への対応済みと扱わない。

管理先：[site #134](https://github.com/kooiei-in4a/amane-tools-site/issues/134)、親：[site #133](https://github.com/kooiei-in4a/amane-tools-site/issues/133)。根拠の固定版：API `465088ed0b575f055beb6851e98d28ed22fb9928` / site資産 `e08aed241245f9f84a7336302edcb7d6a89a57b1`。採用罫線の共有資料は [既存の成果物一覧](../README.md)。

**根拠と版の区別**：[小数点揃えの比較PDF・定義・実測](../quantity-decimal-comparison/README.md)では、既存APIで`99999.999`まで表示・抽出できた。[v0.1のJSON描画検証](../standard-json-validation/README.md)と既存`validate-standard-json.py`は全体5文字の過去の再現資料として残す。今回、それらのvalidator・変換をv0.2へ実装し直したわけではない。以下の新しいJSON例は構文・数量規則・確定金額の不変だけを机上確認し、v0.2全体のAPI描画を追加実行していない。

## 1. 登録時と発行時の分離

| 時点 | 送る内容 | 扱い |
| --- | --- | --- |
| 会社登録／更新 | name、addressLines、registrationNumber、contact、bankTransferLines、logoAssetId、sealAssetId | サービスが発行するcompanyIdに関連付けて再利用。ID・保存方式・登録APIの経路は未設計。 |
| 帳票設定の登録 | サービスが用意するtemplateIdと版、固定文言・位置定義・画像予約領域 | 初期の標準JSONにはレイアウト座標・任意PDF・独自schemaを含めない。今回のJSONでは既存の標準設定をtemplateIdで選ぶ。登録APIやカスタマイズ項目の本設計は後続。 |
| 発行 | companyId、templateId、宛先、発行日、番号、件名、期限、取引日／期間、明細、確定金額、備考 | 会社名・住所・画像を毎回送り直さない。会社情報の発行時上書きは初版候補では認めず、未知keyとして拒否。 |

`companyId`・画像IDは利用者のスコープで解決する前提。例の `company_demo` / `logo_demo` / `seal_demo` は架空の参照IDで、登録済みの実IDではない。画像binary・URLはこの発行JSONに含めない。画像登録・保存・所有関係・認証の製品実装は後続Issueの範囲。

会社情報は生成開始時に解決した1つのスナップショットを3回のPDF処理で使う案とする。過去版の指定や永続版管理は未確定であり、今回追加しない。

## 2. JSON例

- [会社の登録情報](examples/company-registration.json)
- [請求書1明細](examples/invoice-1.json) / [請求書8明細・税率混在・取引期間](examples/invoice-8.json)
- [見積書1明細](examples/estimate-1.json) / [見積書8明細](examples/estimate-8.json)
- [確定金額を変えないことの確認例](examples/provided-amounts-unchanged.json)：意図的に不整合な試験値。実取引用ではない。

発行例の構造（請求書1明細）：

```json
{
  "companyId": "company_demo",
  "templateId": "invoice-standard-v1",
  "currency": "JPY",
  "recipient": { "name": "架空取引先株式会社 御中" },
  "issueDate": "2026-10-22",
  "number": "INV-2026-0001",
  "subject": "帳票サンプル",
  "paymentDueDate": "2026-11-30",
  "transaction": { "date": "2026-10-20" },
  "items": [
    { "name": "試験品目1", "quantity": "99999.999", "unitPrice": 100, "taxRate": 10, "amount": 125 }
  ],
  "taxSummary": {
    "10": { "taxableAmount": 125, "taxAmount": 12 },
    "8": { "taxableAmount": 0, "taxAmount": 0 }
  },
  "totalAmount": 137,
  "notesLines": ["試験入力につき振込不要"]
}
```

## 3. 共通の入力規則案

- UTF-8 JSON object、登録／発行それぞれ64 KiBまでを候補にする。未知key、同名key重複、null、型の自動変換は認めない。任意項目は原則keyを省略し、空文字は使わない。`notesLines: []` は備考なしとして許可する。
- 文字数はUTF-16 code unitで数える。文字表にあるBMP文字だけを使い、無断trim・Unicode正規化・折り返し・縮小・切り捨てをしない。前後空白も表示幅・文字数に含め、全空白の文字列は拒否する。
- 各文字列の中の改行・制御文字は不可。複数行は明示した文字列配列をLFで結合する。配列の各行は空白のみ不可。
- 文字数と行数を満たしても、**最終文字列を固定フォント・実際の矩形・fontSizeで検査する**。APIの1項目200文字・5行を、そのまま帳票の許容値にしない。
- 未対応文字（例：𠮷、絵文字、異体字セレクタ）、`%p/%P/%t/%v`、バックスラッシュ直後のnは拒否する。通常の%は許可。結合濁点・半濁点U+3099/U+309Aは表示の差を避けるため初版候補では明示拒否し、合成済み文字への修正を案内する（API自体は受理し独立幅で描く。これはsiteに追加する候補制約）。
- 日付は実在する `YYYY-MM-DD`（年0001〜9999、時刻・timezoneなし）。期間はstart≦end。発行日と取引日を相互補完しない。期限と発行日の前後関係は業務上の追加制約を課さない案とし、必要性は別途確認する。

## 4. 会社登録情報：必須・型・上限案

| key | 必須 | 型・候補上限 | 表示先／注意 |
| --- | --- | --- | --- |
| name | 必須 | string、1〜16文字、1行 | issuer。法人名として短すぎないかは未確定事項。 |
| addressLines | 必須 | string配列1〜2行、各16文字以下、LF結合後32文字以下 | address。2行各16文字だとLF込み33文字になるため不可。 |
| registrationNumber | 任意 | string、`T`＋ASCII数字13桁 | registration。形式検査のみで登録の実在性・帳票の法的充足を判定するものではない。 |
| contact | 任意 | string、1〜16文字、1行 | contact。電話／メールを分ける新しい欄は初版候補に含めない。 |
| bankTransferLines | 任意 | string配列1〜3行、各16文字以下、LF結合後48文字以下 | 請求書bank。見積書には適用しない登録設定であることを明示する。 |
| logoAssetId / sealAssetId | 任意 | string、ASCII英数字・`_`・`-`、1〜64文字を仮置き | 登録済みPNGの参照。省略時は画像なし。ID形式は後続の保存・認証設計で確定。 |

登録されている任意項目が省略された場合は、値とそのラベルを省く案。表示する登録情報に問題があれば発行時も拒否し、会社登録のどの項目を直すか示す。登録時の文字検査に加えて、選んだテンプレートの矩形検査も発行時に行う。

PNGは既存APIの1 MiB・各辺2000px以内、幅比0.02〜0.5、縦横比保持・ページ内配置が前提。正式な画像予約領域との衝突検査は帳票設定側で決める。ここでは画像サイズ・保存APIを追加実装しない。

## 5. 発行時データ：必須・型・上限案

| key | 必須 | 型・候補上限 | 条件 |
| --- | --- | --- | --- |
| companyId | 必須 | string、ASCII英数字・`_`・`-`、1〜64文字を仮置き | 登録会社の参照。形式とIDの版指定方法は未確定。 |
| templateId | 必須 | string、初版候補は `invoice-standard-v1` / `estimate-standard-v1` | これで帳票種別を決め、重複するdocumentTypeは送らない。値・版管理方法は未確定。 |
| quantityAlignment | 任意・今回の設定提案 | string、`decimal`／`right` | 発行時の上書き案。発行値＞登録値＞decimal。設定契約として未採用・未実装。 |
| currency | 必須 | string、`JPY`のみ | 初版候補はJPY・税抜。税込入力・外貨は対象外候補。 |
| recipient.name | 必須 | string、1〜16文字、1行 | 御中／様を含む完成した表示名。敬称は自動追加しない。 |
| issueDate | 必須 | 日付string | date（請求日／見積日）。 |
| number | 必須 | string、1〜16文字、1行 | 呼び出し元の確定番号。採番・一意性検査はしない。 |
| subject | 任意 | string、1〜16文字、1行 | subject。未指定時の件名ラベルも省く案。 |
| paymentDueDate | 請求書で任意 | 日付string | 請求書expiry。見積書で指定したらエラー。 |
| validUntil | 見積書で任意 | 日付string | 見積書expiry。請求書で指定したらエラー。 |
| transaction | 請求書で必須、見積書で任意を推奨 | 下記2形のどちらか1つ | 必須性と表示方法は未確定。見積書でも送れば必ず表示する。 |
| items | 必須 | object配列、1〜8件 | 順序維持。空行・9行目の切り捨て・自動改ページなし。未使用行はfieldsを送らない。 |
| items[i].name | 必須 | string、1〜17文字、1行 | 軽減記号を付けた描画文字列は最大18文字。 |
| items[i].quantity | 必須を継続提案 | string。整数部1〜5桁・任意の小数部1〜3桁、最大9文字。桁数と表示方針は採用済み | 数値型へ変換せず末尾0を保持。小数点揃え／右揃えを選択。 |
| items[i].unitPrice | 必須 | JSON整数、0〜99,999,999 | 確定した税抜単価。丸めなし。 |
| items[i].taxRate | 必須 | JSON整数、8または10 | 非課税・免税・対象外・0%等は初版対象外候補。 |
| items[i].amount | 必須 | JSON整数、0〜999,999,999,999 | 呼び出し元の確定した税抜明細金額。 |
| taxSummary | 必須 | `10` / `8` の2キーを持つobject | 両方を明示する案。該当しない税率にも呼び出し元が0を渡す。サービスは0補完しない。 |
| taxSummary[rate].taxableAmount | 必須 | JSON整数、0〜999,999,999,999 | 税率別の確定した税抜対象額。 |
| taxSummary[rate].taxAmount | 必須 | 同上 | 確定税額。税率から計算しない。 |
| totalAmount | 必須 | 同上 | 確定合計。明細・対象額・税額から合算しない。 |
| notesLines | 任意 | string配列0〜3行、各16文字以下。実際の行数はtransactionで減る | 次節の結合後48文字・3行・実矩形の範囲内。 |

金額は小数部・指数表記のないJSON整数を推奨（カンマ・円・引用符なし）。上限は安全整数の範囲内で、型・範囲検査だけを行う。`"125"`、`125.5`、`-1`、`1e3`は候補契約では不可。数量はASCIIの十進文字列で、`(0|[1-9][0-9]{0,4})(\.[0-9]{1,3})?`への**全体一致**で整数部／小数部の桁をそれぞれ検査する。単に全体9文字だけで判定しない。型の自動変換・カンマ付加・丸め・末尾0の削除／補完は行わない。

| 例 | 扱い |
| --- | --- |
| `"99999"`、`"99999.999"` | 整数部5桁、小数部最大3桁として受理。 |
| `"123.45"`、`"12345.000"` | 旧5文字制限で拒否していた例も受理。末尾0を保持。 |
| `"0.001"`、`"2.50"`、`"1.000"`、`"0.010"` | 表記をそのまま表示・抽出する。 |
| `"100000"`、`"100000.001"` | 整数部6桁のため`/items/0/quantity`のquantity-format。 |
| `"1.0000"` | 小数部4桁のため同じエラー。 |
| 数値`1.25`、null | invalid-type。数値から文字列へ変換しない。 |
| `"01.5"`、`"1e3"`、`"+1"`、`"1,000"`、`"1."`、空白付き | 従来の形式案ではquantity-format。 |

数量0（`"0"`／`"0.000"`）・負数を対象外とする正の数量案は継続するが、今回の桁数・表示の採用で業務上の必須性や0／負数対応まで新たに確定したものではない。従来どおり対象外とするかは残る業務判断へ含める。

宛先住所・部署・担当者、明細の単位、値引き専用欄、税込金額、任意の税区分、自由表題・色指定、会社情報の発行時overrideは今回のJSON候補に含めていない。入力されたら未知keyとして拒否し、黙って捨てない。必要な項目を除外したまま確定しないよう、初版に含めるかを未確定事項で確認する。

## 6. 取引日／期間と備考：1ページを保つ推奨案

`transaction` の形は次のどちらか。両方・空object・片側だけの期間は不可。

```json
{ "transaction": { "date": "2026-10-20" } }
```

```json
{ "transaction": { "period": { "start": "2026-10-01", "end": "2026-10-31" } } }
```

**推奨案：既存notesの3行枠を正式に「取引・備考」として使い、生成行数を入力契約で明示する。** 日付／期間をexpiryや発行日に混ぜない。表示先を増やさず固定8行を維持できる。ラベル変更は罫線整理だけの承認には含まれず、今回の追加提案である。

| transaction | notesへ生成する先頭行 | 呼び出し元のnotesLines上限 |
| --- | --- | --- |
| date | `取引日 2026/10/20` の1行 | 2行 |
| period | `取引期間` と `2026/10/01～2026/10/31` の2行 | 1行 |
| 省略（見積書のみを提案） | なし | 3行 |

結合後48文字・3行と実際の幅／高さの両方で判定する。期間は両端の年を省略しない。備考が多すぎれば `/notesLines` 等をエラーにし、取引情報・備考を切り捨てない。登録の振込先は別のbank枠なのでこの行数を消費しない。

独立した取引日／期間欄を作る案も残る。自由備考の3行を維持したい場合や宛先住所等を追加する場合は、正式欄を含む1ページレイアウトを小さく再検証してから確定する。現時点で新しい矩形を製品へ追加していない。

## 7. 標準JSONから帳票への項目対応

| 標準データ／設定 | 既存definition key | 表示変換 |
| --- | --- | --- |
| templateId | title | 固定「請求書」／「見積書」。任意表題は受け付けない案。 |
| 登録name / addressLines / registrationNumber / contact | issuer / address / registration / contact | 名前等はそのまま。住所行はLF結合。 |
| 登録bankTransferLines | bank（請求書のみ） | LF結合。見積書には適用しない会社設定。 |
| 登録logoAssetId / sealAssetId | stamp-imageへ渡す画像 | templateが決めた配置、文字→ロゴ→印影。画像なしなら呼び出しを省く。 |
| recipient.name / issueDate / number / subject | recipient / date / number / subject | 日付のみ `YYYY年MM月DD日`。敬称・採番なし。 |
| paymentDueDate / validUntil | 帳票種別に対応するexpiry | 同じ日付表記。違う帳票用のkeyは拒否。 |
| items[i] の name / quantity / unitPrice / taxRate / amount | row0〜row7.name / quantity / price / rate / amount | 8%のname末尾に表示用の※を1個付加する案（入力文字は削除しない）。数量は文字列を保って選択した小数点揃え／右揃え、単価・金額は3桁カンマ＋円、税率は8%／10%。 |
| taxSummary[10] / taxSummary[8] | subtotal10 / tax10 / subtotal8 / tax8 | それぞれtaxableAmount / taxAmountを表示する。 |
| totalAmount | total | 3桁カンマ＋円。採用案の12pt。 |
| transaction ＋ notesLines | notes ＋ label.notes | 前節に従いLF結合。label.notesを「取引・備考」へ変更する追加提案。 |
| companyId / templateId / currency | 参照解決・許可設定 | 参照等の制御値であり、その文字列自体は帳票へ印字しない。 |
| quantityAlignment（未採用の設定案） | 数量の配置分岐 | 発行値・登録値・既定値から実効モードを解決。文字列自体は印字せず、入力数量の表記も変えない。 |

表示規則（※、カンマ、円、日付・ラベル）はあらかじめ公開し、入力値の省略や隠れた修正にしない。品名に元から※があっても勝手に取り除かない。表示用fieldsが増えても、1ページ100行要素・全体200項目・fields JSON64KiB以内を検査する。

### 採用した数量セル・表示変換

| モード（設定値名は候補） | 数量の配置 | 既存PDF APIへの変換 |
| --- | --- | --- |
| `decimal` | 小数点開始位置を共通化。整数は同じ位置に末尾を揃える。 | 整数部の文字幅からxを調整し、数量文字列全体を1 fieldでleft配置。 |
| `right` | 小数点の位置にかかわらず文字列全体の右端を揃える。当初の表示方式。 | 共通の拡張済み数量セルに文字列全体を送り、既存のalign=rightを使用。 |

**今回の設定推奨案（未採用・未実装）**：登録帳票設定の直下と発行JSONルートに`quantityAlignment`を置き、stringの`decimal`／`right`を許可する。省略時はdecimal、発行時上書きを許可し、優先順位は発行明示値＞登録値＞decimal。指定された不正値は補正せず、元入力path付きで拒否する。設定場所・key・既定値・上書き可否・エラーはまだ採用済みではない。[推奨案・解決表・エラー例・判断点](../QUANTITY-MODE-SETTINGS-DRAFT.md)を参照。帳票全体で1つの実効モードを使う。

invoice／estimateともquantity／heading.quantityの論理セルをx=.3800・幅=.0950（56.525pt、右端.4750は維持）へ広げる。品名／見出しの幅は.3400→.3200とし、品名17文字＋軽減記号を保持する。数量8pt・品名9pt、他の列の位置は維持。数量のmaxLengthは5→9とし、整数部・小数部の個別上限も入力で検査する。

セルは両モード共通。rightを選んでも旧幅44.625pt・全体5文字制限へ戻さない。前回の比較PDFの「現行」は旧幅の右揃え。[拡張セルでの同一入力・両モード比較](../quantity-mode-comparison/README.md)を追加し、請求書／見積書の1・8明細すべてで`99999.999`・末尾0・隣接欄の非交差を確認した。これはローカルfixtureでの切替検証であり、製品設定やv0.2全体のvalidatorは未実装。

小数点基準位置は固定フォントのadvanceから計算し、APIの小数4桁座標丸めを見込む余白を設ける。検証版では261.469pt、丸め余白0.06pt。実際のAPIには数量全体を1 fieldでleft配置し、整数部の幅に応じてxを調整する。definitionのセル幅だけの変更では成立しない。[数量配置スクリプト](../compare-decimal-quantity.py)の`decimal_fields()`、[実送信fields](../quantity-decimal-comparison/invoice-8/proposed/fields.json)、[位置対応表](../quantity-decimal-comparison/invoice-8/quantity-placement.json)を参照する。

数量maxLengthを9へ合わせた検証用の論理セル定義：[請求書](quantity-layout/invoice.definition.json)／[見積書](quantity-layout/estimate.definition.json)。数量以外は比較検証時点の候補を保持しており、名称・備考等の未確定事項を新たに確定したコピーではない。このコピーからPDFを追加生成した実績はなく、過去の比較PDFと区別する。

これは入力／表示仕様の採用で、標準JSONへ座標や新しいalign値を加える変更ではない。製品テンプレート・製品変換は未実装。既存のブラウザ用整数数量・税額計算を流用しない。

## 8. 再計算しない責任分担

呼び出し元が、単価、明細金額、税率別対象額・税額、合計を確定して送る。siteは型・範囲・文字・表示枠・参照・構造を検証して表示文字列へ変換し、PDF APIは描画する。

`quantity × unitPrice = amount`、明細合計＝税率別対象額、税額＝対象額×税率、合計＝対象額＋税額、という**金額間整合性の再計算・補正・自動拒否は行わない推奨案**。丸め方・税計算単位・discount計算等のパラメータは持たない。数量は計算せず表示だけに使う。

[確定値保持例](examples/provided-amounts-unchanged.json) は `quantity="1.25"` / `unitPrice=100` に対して `amount=98765`、10%対象額12345、税額777、合計99999を送る。候補契約では金額間の不一致を理由にエラーにせず、`98,765円`、`12,345円`、`777円`、`99,999円`を表示する。これは実取引の正しさの保証ではない。

## 9. エラー仕様案と代表例

以下は**siteの将来の標準入力エラー案**であり、既存PDF APIの400/422応答形式を変更するものではない。`path` は0始まりの配列indexを含むJSON Pointerを使う案。本文・入力値・PDF内部pathは応答やログへ埋め込まない。

```json
{
  "status": 422,
  "code": "validation-failed",
  "errors": [
    { "path": "/items/8", "code": "too-many-items", "message": "明細は8件以内にしてください。", "limit": 8 }
  ]
}
```

| 入力例 | HTTP案 / code | path例／扱い |
| --- | --- | --- |
| 壊れたJSON・重複key | 400 / invalid-json、duplicate-key | 解析できればpath、特定できなければ空文字（root） |
| 未知の `recipient.addressLines` 等 | 400 / unknown-field | `/recipient/addressLines`。表示しないまま成功させない。 |
| amount欠落、quantityが数値 | 422 / required、invalid-type | `/items/0/amount`、`/items/0/quantity` |
| 金額-1・小数・上限超過 | 422 / out-of-range、invalid-integer | `/items/0/amount`、`/totalAmount` |
| quantity `100000`（整数部6桁）・`1.0000`（小数部4桁） | 422 / quantity-format | `/items/0/quantity`。丸め・短縮しない。`123.45`は有効。 |
| taxRate=0・currency=USD | 422 / unsupported-tax-rate、unsupported-currency | `/items/0/taxRate`、`/currency` |
| 9明細・0明細 | 422 / too-many-items、too-few-items | `/items/8`、`/items`。部分PDFを返さない。 |
| 氏名に𠮷・結合濁点・禁止文字列 | 422 / unsupported-character、combining-mark-not-supported、forbidden-sequence | `/recipient/name`。コードポイント等を案内、代替・正規化なし。 |
| 2026-02-30、期間の逆転・両形式指定 | 422 / invalid-date、invalid-period、ambiguous-transaction | `/transaction/date`、`/transaction/period/end`、`/transaction` |
| 期間＋備考2行 | 422 / notes-budget-exceeded | `/notesLines/1`。periodは備考最大1行、入力全体を拒否。 |
| 文字数・行数以内でも幅超過 | 422 / field-overflow | 元の入力path。自由縮小・折り返しなし。 |
| 参照先が存在しない／利用できない | 422 / invalid-reference を仮置き | `/companyId`、登録時の `/logoAssetId` 等。認証・参照エラーとのHTTP整理は未確定。 |
| 登録会社名が選択templateに収まらない | 422 / registered-data-invalid | 発行payloadの `/companyId` に加え、`source: {"kind":"company","path":"/name","code":"field-overflow"}` として直す登録項目を示す案。 |
| request UTF-8 64KiB超過 | 413 / request-too-large | root。具体的な入口上限は後続API設計で整合。 |
| 金額間だけが不整合 | エラーにしない案 | 再計算しない。型・範囲に合う確定値をそのまま表示。 |

構造→型／候補上限→参照解決→全文字と矩形→全体予算の順で事前検証する案。APIが返す一般的な400／field-overflowしかない場合も、推測した別項目へ付け替えない。siteで事前検証できなかった失敗は帳票全体の生成失敗として扱い、内部のfields index・本文をそのまま返さない。認証・503/504・再試行等の運用契約は今回確定しない。

## 10. 上限の根拠と確認範囲

元のsite定義の数量欄は5文字・44.625ptで、`99999.999`を直接fieldsで送ると実APIは422 field-overflowになる。[幅拡張＋小数点揃えの検証](../quantity-decimal-comparison/README.md)では、56.525ptのセルで同時最大桁数を表示・抽出し、品名・単価との非交差を確認した。8明細の小数点画素中心は144dpiで一致、整数末尾も含む基準位置の最大差0.0475pt。末尾0を保持し、確定金額を変更していない。

v0.1からの描画検証と、位置付きfieldsでの数量拡張検証は実施済み。ただし本v0.2の全JSON例から採用配置までの一連の変換と境界エラーは、改訂した**検証専用スクリプト**での確認が残る。今回、新しいPDF・性能測定は追加していない。旧例・旧測定・旧validatorをv0.2対応済みとして流用しない。

[例ファイルの机上確認記録](example-check.json)はJSON構文、改訂した数量の型・桁数、元例からの確定金額不変を確認したもの。汎用validatorやHTTP試験ではない。旧[example-audit.json](../standard-json-draft/example-audit.json)はv0.1の記録として残す。

## 11. 未確定事項と次の判断

| 未確定事項 | 推奨する出発点／確定前に確認すること |
| --- | --- |
| 型・税／通貨・金額上限 | JPY・税抜・非負整数、単価8桁／他金額12桁、数量の文字列保持・整数部5桁／小数部3桁・小数点揃えは採用済み。税率8/10等の他の範囲は用途上妥当か確認する。 |
| 取引情報と備考 | notes3行を正式に「取引・備考」とする本案を推奨。自由備考の3行を維持する要件があれば専用欄を再検証。請求書でtransaction必須、見積書では任意とするかも確定する。 |
| 長い法人名・住所、宛先住所・部署等 | 16文字の法人名／宛先は用途上短い可能性がある。必要項目を洗い出し、枠拡張・明示複数行・専用欄を決める。現行に表示先がない情報を受けて捨てない。 |
| 必須・ゼロ表示・任意ラベル | number／住所等の必須、税率別2区分を常に必須・表示とするか、任意ラベルを省く方針、数量0／負数等を対応外にしてよいか確認する。 |
| ID・版と会社更新、配置モード設定 | companyId／templateId／asset IDの命名、参照できない時のエラー、生成時の会社・画像・templateの版の固定方法は後続の登録・認証設計と整合させる。数量モードは[設定推奨案](../QUANTITY-MODE-SETTINGS-DRAFT.md)の採否を決める。quantityAlignment、既定decimal、発行上書き許可・発行値優先、不正値の明示拒否を推奨。過去版管理は今回実装しない。 |
| エラー・初版契約 | HTTP・code・JSON Pointer、NFD拒否、64KiB候補、未知key拒否、金額間不一致をエラーにしない点を確定。標準JSONの版識別key／API経路は今回未設計。 |

これらを決め、代表JSON・表示先・入力上限／エラー例・正式な品質／時間／容量基準をそろえてから #134 の完了を判断する。提案を作成・共有しただけで #133／#134を閉じない。認証・会社保存・画像保存・可変明細・一括合成の製品実装は開始しない。
