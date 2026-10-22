# #134 標準JSON案 v0.3：初版仕様R1〜R8採用済み

**初版R1〜R8の一括採用を反映した資料改訂。数量の桁数・両モード・末尾0保持・quantityAlignmentと罫線の採用決定も維持する。製品未実装。** v0.3はこの資料の版名で、公開APIの版・schemaVersionを新設したものではない。[v0.2](../standard-json-draft-v0.2/README.md)とその入力・スクリプト・PDFは過去の検証再現用に残す。 会社登録と発行入力を分離し、確定金額を再計算せず、固定8明細・1ページで描画する。製品コード・API契約・CI・共有mainは変更していない。

採用済みの数量仕様は、`items[i].quantity`を文字列として扱い、整数部最大5桁・小数部最大3桁（小数点込み最大9文字）、入力された末尾0を保持して小数点位置を揃えること。旧「全体5文字」上限を置き換える。小数点なしの整数は、同じ基準位置に末尾を揃える。

**追補：数量配置は2モードを用意する方針。** ユーザーの追加方針により、小数点揃えに加えて当初の右揃えも選択できるようにする。下記の小数点基準の計算はdecimalモードへ適用する。両モードとも整数部5桁・小数部3桁と末尾0保持を共通にし、数量の入力値・型・金額を変えない。設定仕様も採用済みで、既定decimal、発行時上書き可、発行値＞登録値＞decimalとする。登録値が不正なら正しい発行値があっても拒否する。

**今回採用した名称・宛先仕様**：会社の`nameLines`、発行の`recipient.nameLines`は必須のstring配列1〜2行・各1〜20文字（UTF-16 unit）、LF結合後最大41文字。会社`addressLines`も必須・同じ上限。部署・担当者・敬称は宛先2行に含め、独立key／追加行は設けない。宛先住所の専用key／欄と旧`name`形式の併存・自動変換は初版対象外。旧keyや対応外keyは黙って捨てず描画前に拒否する。[採用仕様・根拠](../NAME-RECIPIENT-SPEC-PROPOSAL.md)を参照。取引／備考の配分と帳票別必須性も[採用済み](../TRANSACTION-NOTES-SPEC-PROPOSAL.md)。必須性・税区分・金額上限・数量0／負数・ゼロ表示・文字制約・入力エラー形式・品質基準も[R1〜R8](../INITIAL-SPEC-ADOPTED.md)として採用済み。[最終検証](../FINAL-VALIDATION-site-134.md)は未実施。ID／版・保存・認証・運用エラーの契約は後続。

管理先：[site #134](https://github.com/kooiei-in4a/amane-tools-site/issues/134)、親：[site #133](https://github.com/kooiei-in4a/amane-tools-site/issues/133)。根拠の固定版：API `465088ed0b575f055beb6851e98d28ed22fb9928` / site資産 `e08aed241245f9f84a7336302edcb7d6a89a57b1`。採用罫線の共有資料は [既存の成果物一覧](../README.md)。

**根拠と未確認の区別**：[名称・住所のfields描画](../header-lines-probe/README.md)は両帳票8明細・6 PDFで20文字×2行を確認。[v0.2の通し検証](../standard-json-v02-validation/README.md)は旧name形式で正常14件・拒否65件。**本v0.3のnameLines→事前検証→表示変換→PDFと、改訂境界の拒否は後続の[通し検証](../standard-json-v03-validation/README.md)で確認済み。** 資料採用時の机上確認に続き、後続で検証専用スクリプト・正常18 PDF・拒否66件を確認した。製品未実装。

## 1. 登録時と発行時の分離

| 時点 | 送る内容 | 扱い |
| --- | --- | --- |
| 会社登録／更新 | nameLines、addressLines、registrationNumber、contact、bankTransferLines、logoAssetId、sealAssetId | サービスが発行するcompanyIdに関連付けて再利用。ID・保存方式・登録APIの経路は未設計。 |
| 帳票設定の登録 | サービスが用意するtemplateIdと版、quantityAlignment、固定文言・位置定義・画像予約領域 | 初期の標準JSONにはレイアウト座標・任意PDF・独自schemaを含めない。今回のJSONでは既存の標準設定をtemplateIdで選ぶ。登録APIやカスタマイズ項目の本設計は後続。 |
| 発行 | companyId、templateId、任意のquantityAlignment、宛先、発行日、番号、件名、期限、取引日／期間、明細、確定金額、備考 | 会社名・住所・画像を毎回送り直さない。会社情報の発行時上書きは初版では認めず、未知keyとして拒否。 |

`companyId`・画像IDは利用者のスコープで解決する前提。例の `company_demo` / `logo_demo` / `seal_demo` は架空の参照IDで、登録済みの実IDではない。画像binary・URLはこの発行JSONに含めない。画像登録・保存・所有関係・認証の製品実装は後続Issueの範囲。

会社情報は生成開始時に解決した1つのスナップショットを3回のPDF処理で使う案とする。過去版の指定や永続版管理は未確定であり、今回追加しない。

## 2. JSON例

通常例は旧例の表示文字・確定金額・数量・quantityAlignmentを保持してnameを配列へ改訂。[長い登録会社例](examples/company-registration-long.json)、[請求書8明細・部署／担当者](examples/invoice-8-long.json)、[見積書8明細・部署／敬称](examples/estimate-8-long.json)は長名の追加例。[rolesのPDF](../standard-json-v03-validation/README.md)も生成済み。

- [会社の登録情報](examples/company-registration.json)
- 帳票設定の部分例：[請求書・登録right](settings-examples/invoice.json)／[見積書・登録decimal](settings-examples/estimate.json)。登録APIの完全なpayloadではない。
- [請求書1明細](examples/invoice-1.json) / [請求書8明細・税率混在・取引期間](examples/invoice-8.json)
- [見積書1明細](examples/estimate-1.json) / [見積書8明細](examples/estimate-8.json)
- [確定金額を変えないことの確認例](examples/provided-amounts-unchanged.json)：意図的に不整合な試験値。実取引用ではない。

発行例の構造（請求書1明細、登録rightを発行decimalで上書き。8明細例は上書きkeyを省略する。見積書1明細例は登録decimalをrightへ上書きする。モードの期待値は旧形式の[通し検証](../standard-json-v02-validation/README.md)で確認済みだが、この改訂例も[v0.3結果](../standard-json-v03-validation/README.md)で描画済み）：

```json
{
  "companyId": "company_demo",
  "templateId": "invoice-standard-v1",
  "quantityAlignment": "decimal",
  "currency": "JPY",
  "recipient": { "nameLines": ["架空取引先株式会社 御中"] },
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

## 3. 共通の入力規則（R6採用済み）

- UTF-8 JSON object、登録／発行それぞれ64 KiB（65,536 bytes）までとする。未知key、同名key重複、null、型の自動変換は認めない。任意項目は原則keyを省略し、空文字は使わない。`notesLines: []` は備考なしとして許可する。
- 文字数はUTF-16 code unitで数える。文字表にあるBMP文字だけを使い、無断trim・Unicode正規化・折り返し・縮小・切り捨てをしない。前後空白も表示幅・文字数に含め、全空白の文字列は拒否する。
- 各文字列の中の改行・制御文字は不可。複数行は明示した文字列配列をLFで結合する。配列の各行は空白のみ不可。
- 文字数と行数を満たしても、**最終文字列を固定フォント・実際の矩形・fontSizeで検査する**。APIの1項目200文字・5行を、そのまま帳票の許容値にしない。
- 未対応文字（例：𠮷、絵文字、異体字セレクタ）、`%p/%P/%t/%v`、バックスラッシュ直後のnは拒否する。通常の%は許可。結合濁点・半濁点U+3099/U+309Aは表示の差を避けるため初版では明示拒否し、合成済み文字への修正を案内する（API自体は受理し独立幅で描く。これはsiteに追加する採用制約）。
- 日付は実在する `YYYY-MM-DD`（年0001〜9999、時刻・timezoneなし）。期間はstart≦end。発行日と取引日を相互補完しない。期限と発行日の前後関係は業務上の追加制約を課さない。

## 4. 会社登録情報：必須・型・上限（R1採用済み）

| key | 必須 | 型・上限 | 表示先／注意 |
| --- | --- | --- | --- |
| nameLines | 必須・採用済み | string配列1〜2行、各1〜20文字、LF結合後41文字以内 | issuerへLF結合。1行でも配列。旧nameとの併存なし。 |
| addressLines | 必須・上限採用済み | string配列1〜2行、各1〜20文字、LF結合後41文字以内 | addressへLF結合。会社住所専用。20＋20＋LF＝41。 |
| registrationNumber | 任意 | string、`T`＋ASCII数字13桁 | registration。形式検査のみで登録の実在性・帳票の法的充足を判定するものではない。 |
| contact | 任意 | string、1〜16文字、1行 | contact。電話／メールを分ける新しい欄は初版に含めない。 |
| bankTransferLines | 任意 | string配列1〜3行、各16文字以下、LF結合後48文字以下 | 請求書bank。見積書には適用しない登録設定であることを明示する。 |
| logoAssetId / sealAssetId | 任意 | string、ASCII英数字・`_`・`-`、1〜64文字を仮置き | 登録済みPNGの参照。省略時は画像なし。ID形式は後続の保存・認証設計で確定。 |

登録されている任意項目が省略された場合は、値とそのラベルを省く。表示する登録情報に問題があれば発行時も拒否し、会社登録のどの項目を直すか示す。登録時の文字検査に加えて、選んだテンプレートの矩形検査も発行時に行う。

PNGは既存APIの1 MiB・各辺2000px以内、幅比0.02〜0.5、縦横比保持・ページ内配置が前提。正式な画像予約領域との衝突検査は帳票設定側で決める。ここでは画像サイズ・保存APIを追加実装しない。

## 5. 発行時データ：必須・型・上限（R1〜R5採用済み）

| key | 必須 | 型・上限 | 条件 |
| --- | --- | --- | --- |
| companyId | 必須 | string、ASCII英数字・`_`・`-`、1〜64文字を仮置き | 登録会社の参照。形式とIDの版指定方法は未確定。 |
| templateId | 必須 | string、初版候補は `invoice-standard-v1` / `estimate-standard-v1` | これで帳票種別を決め、重複するdocumentTypeは送らない。値・版管理方法は未確定。 |
| quantityAlignment | 任意・採用済み | string、`decimal`／`right`完全一致 | 発行時上書きを許可。発行値＞登録値＞decimal。登録値不正は上書きで救済しない。製品未実装。 |
| currency | 必須 | string、`JPY`のみ | 初版はJPY・税抜。税込入力・外貨は対象外。 |
| recipient.nameLines | 必須・採用済み | string配列1〜2行、各1〜20文字、LF結合後41文字以内 | 部署・担当者・敬称も含む完成した表示行。敬称を補完しない。旧recipient.nameとの併存なし。 |
| issueDate | 必須 | 日付string | date（請求日／見積日）。 |
| number | 必須 | string、1〜16文字、1行 | 呼び出し元の確定番号。採番・一意性検査はしない。 |
| subject | 任意 | string、1〜16文字、1行 | subject。未指定時の件名ラベルも省く。 |
| paymentDueDate | 請求書で任意 | 日付string | 請求書expiry。見積書で指定したらエラー。 |
| validUntil | 見積書で任意 | 日付string | 見積書expiry。請求書で指定したらエラー。 |
| transaction | 請求書で必須、見積書で任意・採用済み | date／periodのどちらか1つ | 共有notesへ表示。発行日等から補完しない。 |
| items | 必須 | object配列、1〜8件 | 順序維持。空行・9行目の切り捨て・自動改ページなし。未使用行はfieldsを送らない。 |
| items[i].name | 必須 | string、1〜17文字、1行 | 軽減記号を付けた描画文字列は最大18文字。 |
| items[i].quantity | 必須・採用済み | string。整数部1〜5桁・任意の小数部1〜3桁、最大9文字。桁数と表示方針は採用済み | 数値型へ変換せず末尾0を保持。小数点揃え／右揃えを選択。 |
| items[i].unitPrice | 必須 | JSON整数、0〜99,999,999 | 確定した税抜単価。丸めなし。 |
| items[i].taxRate | 必須 | JSON整数、8または10 | 非課税・免税・不課税・0%等は初版対象外として拒否。 |
| items[i].amount | 必須 | JSON整数、0〜999,999,999,999 | 呼び出し元の確定した税抜明細金額。 |
| taxSummary | 必須 | `10` / `8` の2キーを持つobject | 両方を明示する。該当しない税率にも呼び出し元が0を渡す。サービスは0補完しない。 |
| taxSummary[rate].taxableAmount | 必須 | JSON整数、0〜999,999,999,999 | 税率別の確定した税抜対象額。 |
| taxSummary[rate].taxAmount | 必須 | 同上 | 確定税額。税率から計算しない。 |
| totalAmount | 必須 | 同上 | 確定合計。明細・対象額・税額から合算しない。 |
| notesLines | 任意・配分採用済み | string配列、各行1〜16文字。date時0〜2行／period時0〜1行／取引省略時0〜3行 | LF結合後48文字・最大3行・実矩形内。省略／[]は自由備考なし。 |

金額は小数部・指数表記・符号のないJSON整数とする（カンマ・円・引用符なし）。符号表記（`-0`を含む）も拒否する。金額0は単価・明細・対象額・税額・合計のすべてで0円と表示し、空欄にしない。上限は安全整数の範囲内で、型・範囲検査だけを行う。`"125"`、`125.5`、`-1`、`1e3`は不可。数量はASCIIの十進文字列で、`(0|[1-9][0-9]{0,4})(\.[0-9]{1,3})?`への**全体一致**で整数部／小数部の桁をそれぞれ検査する。単に全体9文字だけで判定しない。型の自動変換・カンマ付加・丸め・末尾0の削除／補完は行わない。

| 例 | 扱い |
| --- | --- |
| `"99999"`、`"99999.999"` | 整数部5桁、小数部最大3桁として受理。 |
| `"123.45"`、`"12345.000"` | 旧5文字制限で拒否していた例も受理。末尾0を保持。 |
| `"0.001"`、`"2.50"`、`"1.000"`、`"0.010"` | 表記をそのまま表示・抽出する。 |
| `"100000"`、`"100000.001"` | 整数部6桁のため`/items/0/quantity`のquantity-format。 |
| `"1.0000"` | 小数部4桁のため同じエラー。 |
| 数値`1.25`、null | invalid-type。数値から文字列へ変換しない。 |
| `"01.5"`、`"1e3"`、`"+1"`、`"1,000"`、`"1."`、空白付き | 採用形式ではquantity-format。 |

数量は正数のみ（R4採用済み）。`"0"`／`"0.000"`・負数は拒否し、`"0.001"`は受理。上記の形式への全体一致に加えて、ゼロ表現を検査する。数値型への変換はせず末尾0を保持する。金額0の許可とは独立した条件。

**採用済みの初版対象外**：宛先住所の専用key／欄、部署・担当者の独立key、旧会社name／recipient.name。部署・担当者・敬称はrecipient.nameLinesの2行予算内で表示する。旧新同時指定を含め対象外keyは描画前に明示拒否し、nameLinesへ自動移行しない。

R1〜R8は上記項目表を初版基準として採用。税抜・JPY・軽減8%と10%に限定し、負数金額・外貨・税込入力・非課税等は拒否。単位・値引き専用欄・自由表題／色・発行時会社overrideはこの標準JSONにkeyや表示先を設けず、未知keyを拒否する。将来必要になった場合は範囲変更として別途検討する。

## 6. 取引情報と自由備考：採用仕様

**ユーザー採用済み・製品未実装。** [採用仕様・根拠](../TRANSACTION-NOTES-SPEC-PROPOSAL.md)。既存notesを固定「取引・備考」の共有枠として使い、取引情報を先頭、自由備考を後ろへLF結合する。合成後は最大3行・48文字（UTF-16 unit、LF込み）。自由備考は各行1〜16文字。専用取引欄は初版対象外で、名称の20文字上限を備考へ流用しない。

| transaction | 生成する取引情報 | 自由備考の配分 | 合成境界 |
| --- | --- | --- | --- |
| date | 取引日 YYYY/MM/DD（1行、14文字） | 0〜2行、各16文字以内 | 14＋16＋16＋LF2＝48文字／3行 |
| period | 取引期間（4文字）＋YYYY/MM/DD～YYYY/MM/DD（21文字）の2行 | 0〜1行、16文字以内 | 4＋21＋16＋LF2＝43文字／3行 |
| 省略（見積書のみ） | なし | 0〜3行、各16文字以内、LF込み48文字 | 16＋16＋14＋LF2＝48文字。16×3＋LF2＝50文字は不可 |

請求書はtransaction必須、見積書は任意。date／periodの片方だけを指定し、両指定・空object・null・period片側欠落・未知keyは拒否。日付は実在するYYYY-MM-DD、periodはstart≦end。同日periodも2行のまま、期間の両端の年を省略しない。発行日・期限は独立欄へ表示し、transactionへ補完しない。発行日・取引日・期限の前後関係を新たな制約にしない。

~~~json
{"transaction":{"date":"2026-10-20"},"notesLines":["自由備考の一行目","自由備考の二行目"]}
~~~

~~~json
{"transaction":{"period":{"start":"2026-12-31","end":"2027-01-01"}},"notesLines":["期間後の自由備考"]}
~~~

notesLines省略／[]は自由備考なし。見積書で取引・備考とも省略したら値・ラベルも省き、内容があればラベルは固定「取引・備考」。空行・全空白・null・非string・要素内CR/LF等は拒否する。行数・文字数と実幅／高さの両方を検査し、自動分割・trim・切捨て・短縮・縮小はしない。会社振込先は別bank枠で共有行数を消費しない。超過時は元入力path付きで描画前に拒否する。

追加の全発行JSON例（資料のみ、未描画）：
- [請求書date＋2備考／48文字](transaction-examples/invoice-date-notes-boundary.json)
- [請求書・年越しperiod＋1備考／43文字](transaction-examples/invoice-period-notes-boundary.json)
- [見積書・取引省略＋3備考／48文字](transaction-examples/estimate-no-transaction-notes-boundary.json)
- [見積書・取引／備考とも省略](transaction-examples/estimate-no-transaction-no-notes.json)
- [見積書・同日period](transaction-examples/estimate-same-day-period.json)
- [構文・期待配分・他データ保持の机上確認](transaction-example-check.json)

旧v0.2の備考境界と、v0.3のnameLines＋通常備考は描画済み。**上記追加例と、名称／数量設定＋備考境界／省略／入力エラーを組み合わせた追加実行は未実施。** 旧入力・validator・PDF・実測は維持し、採用済みと未確認の範囲を分ける。

## 7. 標準JSONから帳票への項目対応

| 標準データ／設定 | 既存definition key | 表示変換 |
| --- | --- | --- |
| templateId | title | 固定「請求書」／「見積書」。任意表題は受け付けない。 |
| 登録nameLines / addressLines / registrationNumber / contact | issuer / address / registration / contact | 名称・住所は配列順にLF結合。登録番号・連絡先はそのまま。 |
| 登録bankTransferLines | bank（請求書のみ） | LF結合。見積書には適用しない会社設定。 |
| 登録logoAssetId / sealAssetId | stamp-imageへ渡す画像 | templateが決めた配置、文字→ロゴ→印影。画像なしなら呼び出しを省く。 |
| recipient.nameLines / issueDate / number / subject | recipient / date / number / subject | 宛先はLF結合。日付のみ `YYYY年MM月DD日`。敬称補完・採番なし。 |
| paymentDueDate / validUntil | 帳票種別に対応するexpiry | 同じ日付表記。違う帳票用のkeyは拒否。 |
| items[i] の name / quantity / unitPrice / taxRate / amount | row0〜row7.name / quantity / price / rate / amount | 8%のname末尾に表示用の※を1個付加する（入力文字は削除しない）。数量は文字列を保って選択した小数点揃え／右揃え、単価・金額は3桁カンマ＋円、税率は8%／10%。 |
| taxSummary[10] / taxSummary[8] | subtotal10 / tax10 / subtotal8 / tax8 | 両区分と各taxableAmount / taxAmountは必須。0も0円と表示し、固定ラベルを残す。省略からの0補完はしない。 |
| totalAmount | total | 3桁カンマ＋円。採用案の12pt。 |
| transaction ＋ notesLines | notes ＋ label.notes | 前節に従いLF結合。label.notesは採用済みの固定「取引・備考」。見積で両方省略なら値・ラベルなし。 |
| companyId / templateId / currency | 参照解決・許可設定 | 参照等の制御値であり、その文字列自体は帳票へ印字しない。 |
| quantityAlignment（設定仕様は採用済み） | 数量の配置分岐 | 発行値・登録値・既定値から実効モードを解決。文字列自体は印字せず、入力数量の表記も変えない。 |

表示規則（※、カンマ、円、日付・ラベル）はあらかじめ公開し、入力値の省略や隠れた修正にしない。品名に元から※があっても勝手に取り除かない。表示用fieldsが増えても、1ページ100行要素・全体200項目・fields JSON64KiB以内を検査する。

### 採用した数量セル・表示変換

| モード（採用値） | 数量の配置 | 既存PDF APIへの変換 |
| --- | --- | --- |
| `decimal` | 小数点開始位置を共通化。整数は同じ位置に末尾を揃える。 | 整数部の文字幅からxを調整し、数量文字列全体を1 fieldでleft配置。 |
| `right` | 小数点の位置にかかわらず文字列全体の右端を揃える。当初の表示方式。 | 共通の拡張済み数量セルに文字列全体を送り、既存のalign=rightを使用。 |

**採用した設定仕様（製品未実装）**：登録帳票設定の直下と発行JSONルートに`quantityAlignment`を置き、stringの`decimal`／`right`を許可する。既定decimal、発行時上書き可、発行明示値＞登録値＞decimal。発行値と登録値を検証してから選び、登録値が不正なら正しい発行値があっても拒否する。省略だけを未指定とし、null・空文字・不正値は補正せずエラー。発行上書きは登録値を変更しない。[採用仕様・解決表・エラー例・残る確認](../QUANTITY-MODE-SETTINGS-DRAFT.md)を参照。帳票全体で1つの実効モードを使う。

invoice／estimateともquantity／heading.quantityの論理セルをx=.3800・幅=.0950（56.525pt、右端.4750は維持）へ広げる。品名／見出しの幅は.3400→.3200とし、品名17文字＋軽減記号を保持する。数量8pt・品名9pt、他の列の位置は維持。数量のmaxLengthは5→9とし、整数部・小数部の個別上限も入力で検査する。

セルは両モード共通。rightを選んでも旧幅44.625pt・全体5文字制限へ戻さない。前回の比較PDFの「現行」は旧幅の右揃え。[拡張セルでの同一入力・両モード比較](../quantity-mode-comparison/README.md)を追加し、請求書／見積書の1・8明細すべてで`99999.999`・末尾0・隣接欄の非交差を確認した。これはローカルfixtureでの切替検証。後続の[v0.2候補全体の検証](../standard-json-v02-validation/README.md)も完了したが、製品設定・製品validatorは未実装。

小数点基準位置は固定フォントのadvanceから計算し、APIの小数4桁座標丸めを見込む余白を設ける。検証版では261.469pt、丸め余白0.06pt。実際のAPIには数量全体を1 fieldでleft配置し、整数部の幅に応じてxを調整する。definitionのセル幅だけの変更では成立しない。[数量配置スクリプト](../compare-decimal-quantity.py)の`decimal_fields()`、[実送信fields](../quantity-decimal-comparison/invoice-8/proposed/fields.json)、[位置対応表](../quantity-decimal-comparison/invoice-8/quantity-placement.json)を参照する。

名称と数量を組み合わせた検証用定義：[請求書](layout/invoice.definition.json)／[見積書](layout/estimate.definition.json)。v0.2の数量／品名セル・合計fontSizeを継承し、issuer／recipient／addressの幅を.34、multiline・maxLines2・maxLength41へ変更した。各行20文字はJSONの事前検証で別に判定する。x/y/高さ・9pt・背景SHAは維持。**この合成コピーからのPDFは[v0.3結果](../standard-json-v03-validation/README.md)で生成済み**で、個別の名称検証・数量検証の実績とは区別する。

名称配列は空配列・3行・空行／全空白・null・非string・行内CR/LF／制御文字を拒否し、前後空白を文字数と幅へ含める。自動分割・短縮・trim・正規化・縮小は行わない。会社名が2行を使えば部署／担当者の追加行はなく、3行目を黙って落とさない。

これは入力／表示仕様の採用で、標準JSONへ座標や新しいalign値を加える変更ではない。製品テンプレート・製品変換は未実装。既存のブラウザ用整数数量・税額計算を流用しない。

## 8. 再計算しない責任分担

呼び出し元が、単価、明細金額、税率別対象額・税額、合計を確定して送る。siteは型・範囲・文字・表示枠・参照・構造を検証して表示文字列へ変換し、PDF APIは描画する。

`quantity × unitPrice = amount`、明細合計＝税率別対象額、税額＝対象額×税率、合計＝対象額＋税額、という**金額間整合性の再計算・補正・自動拒否は行わない（採用済み）**。丸め方・税計算単位・discount計算等のパラメータは持たない。数量は計算せず表示だけに使う。

[確定値保持例](examples/provided-amounts-unchanged.json) は `quantity="1.25"` / `unitPrice=100` に対して `amount=98765`、10%対象額12345、税額777、合計99999を送る。採用仕様では金額間の不一致を理由にエラーにせず、`98,765円`、`12,345円`、`777円`、`99,999円`を表示する。これは実取引の正しさの保証ではない。

## 9. 入力エラー仕様と代表例（R7採用済み・製品未実装）

以下は**siteで実装する採用済み標準入力エラー形式**であり、既存PDF APIの400/422応答形式を変更するものではない。`path` は0始まりの配列indexを含むJSON Pointerを使う。本文・入力値・PDF内部pathは応答やログへ埋め込まない。

```json
{
  "status": 422,
  "code": "validation-failed",
  "errors": [
    { "path": "/items/8", "code": "too-many-items", "message": "明細は8件以内にしてください。", "limit": 8 }
  ]
}
```

| 入力例 | HTTP / code | path例／扱い |
| --- | --- | --- |
| nameLinesの各行21文字／3行 | 422 / too-long、line-count（採用済み） | `/recipient/nameLines/1`、`/recipient/nameLines`等。登録会社は`/companyId`と登録元sourceを示す。 |
| 旧name／recipient.name、宛先住所、独立した部署／担当者key | 400 / unknown-field（採用済み） | `/name`、`/recipient/name`、`/recipient/addressLines`等。旧新同時指定も拒否。 |
| 壊れたJSON・重複key | 400 / invalid-json、duplicate-key | 解析できればpath、特定できなければ空文字（root） |
| 未知の `recipient.addressLines` 等 | 400 / unknown-field | `/recipient/addressLines`。表示しないまま成功させない。 |
| amount欠落、quantityが数値 | 422 / required、invalid-type | `/items/0/amount`、`/items/0/quantity` |
| quantityAlignmentがnull／型不正、未対応値 | 422 / invalid-type、unsupported-quantity-alignment（数量設定は採用済み） | `/quantityAlignment`。省略扱い・trim・型変換なし。 |
| 登録済みquantityAlignmentが不正 | 422 / registered-data-invalid（採用済み） | `/templateId`と登録元source.path=`/quantityAlignment`。正しい発行上書きがあっても拒否。 |
| 金額-1・小数・上限超過 | 422 / out-of-range、invalid-integer | `/items/0/amount`、`/totalAmount` |
| quantity `"0"`／`"0.000"` | 422 / out-of-range | `/items/0/quantity`。正数のみ。負数は形式違反のquantity-format。 |
| quantity `100000`（整数部6桁）・`1.0000`（小数部4桁） | 422 / quantity-format | `/items/0/quantity`。丸め・短縮しない。`123.45`は有効。 |
| taxRate=0・currency=USD | 422 / unsupported-tax-rate、unsupported-currency | `/items/0/taxRate`、`/currency` |
| 9明細・0明細 | 422 / too-many-items、too-few-items | `/items/8`、`/items`。部分PDFを返さない。 |
| 氏名に𠮷・結合濁点・禁止文字列 | 422 / unsupported-character、combining-mark-not-supported、forbidden-sequence | `/recipient/nameLines/0`。コードポイント等を案内、代替・正規化なし。 |
| 2026-02-30、期間の逆転・両形式指定 | 422 / invalid-date、invalid-period、ambiguous-transaction | `/transaction/date`、`/transaction/period/end`、`/transaction` |
| 請求書transaction欠落 | 422 / required（採用済み） | /transaction。発行日等から補完しない。 |
| date＋備考3行 | 422 / notes-budget-exceeded（採用済み） | /notesLines/2。切捨てず拒否。 |
| 備考3行各16文字 | 422 / too-long（採用済み） | /notesLines。LF込み50文字は48文字超過。 |
| 期間＋備考2行 | 422 / notes-budget-exceeded | `/notesLines/1`。periodは備考最大1行、入力全体を拒否。 |
| 文字数・行数以内でも幅超過 | 422 / field-overflow | 元の入力path。自由縮小・折り返しなし。 |
| 参照先が存在しない／利用できない | 422 / invalid-reference を仮置き | `/companyId`、登録時の `/logoAssetId` 等。認証・参照エラーとのHTTP整理は未確定。 |
| 登録会社名が選択templateに収まらない | 422 / registered-data-invalid | 発行payloadの `/companyId` に加え、`source: {"kind":"company","path":"/nameLines/0","code":"field-overflow"}` として直す登録項目を示す。 |
| request UTF-8 64KiB超過 | 413 / request-too-large | root。具体的な入口上限は後続API設計で整合。 |
| 金額間だけが不整合 | エラーにしない | 再計算しない。型・範囲に合う確定値をそのまま表示。 |

400はJSON構文・重複／未知key、413は本文上限超過、422は必須・型・値・文字・矩形・不正登録値。本文はstatus／code／errorsで、errorsは**最初の1件のみ**。要素はpath／code／修正理由messageと必要なlimit／source。rootは空文字、複数違反の優先順位は公開契約にしない。数量モードの既存採用code・sourceは変えない。事前拒否は全体失敗・実PDF API呼び出し0・部分PDFなし。後段の描画／通信障害を入力エラーへ付け替えない。

構造→型／上限→参照解決→全文字と矩形→全体予算の順で事前検証する。APIが返す一般的な400／field-overflowしかない場合も、推測した別項目へ付け替えない。siteで事前検証できなかった失敗は帳票全体の生成失敗として扱い、内部のfields index・本文をそのまま返さない。認証・503/504・再試行等の運用契約は今回確定しない。

## 10. 上限の根拠と確認範囲

- [名称・住所の描画](../header-lines-probe/README.md)：固定版の両帳票8明細・6 PDFで20文字×2行、元の宛先幅では422、新しい幅.34で成功。nameLinesの標準JSON validatorの試験ではない。
- [数量の両モード比較](../quantity-mode-comparison/README.md)と[設定解決の検証](../quantity-resolution-validation/README.md)：採用済み桁数・末尾0・decimal/rightと優先順位／不正登録拒否を確認。
- [v0.2全体の通し検証](../standard-json-v02-validation/README.md)：旧name形式・正常14件／拒否65件の履歴。入力・スクリプト・PDF・測定を変更せず残す。
- 本v0.3は新しいJSON例と定義コピーの資料整備。[例の机上確認](example-check.json)は構文・名称の行数／文字数、数量形式、元例からの確定金額・設定不変の確認のみ。この机上確認ではresolver・全面validator・API・PDFは実行していない。後続の[v0.3通し検証](../standard-json-v03-validation/README.md)で解析→全項目検証→変換→実APIと拒否時API0を確認済み。

採用したnameLinesと数量／設定を同じ経路で接続し、両帳票1／8明細・1行／2行／20×2境界とエラー拒否は[改訂用スクリプト](../validate-standard-json-v03.py)で確認済み。既存のブラウザ用金額計算は使わない。

## 11. 品質・時間・容量の受入基準（R8採用済み）

- 両帳票1／8明細・数量両モードは各1ページ、qpdf成功、入力から独立に作る期待値と全欄抽出一致。数量・末尾0・確定金額保持、非意図的な欠け／重なりなし。decimal基準位置誤差は0.1pt以内。
- ロゴ／印影の透過・半透過・比率・配置・重ね順・予約領域の非衝突、画像なしも確認。画像上限は既存APIの1MiB・各辺2000px等を維持。
- CPU1／RAM1536MiB等の固定検証条件で、入力JSON事前検証開始〜最終PDF受信まで単独10秒以内、既存共有2枠の同時2チェーンは各20秒以内。起動・保存・抽出検査を除外、画像あり／なしを分けて記録。
- 最終1ページPDFは2MiB以内。終了後のjob・子process残存なし。
- Popplerで抽出・画像を検査。製品反映時には既存ブラウザプレビューで代表4帳票を確認する。Acrobat・実印刷・site経由測定・他機能との競合は未確認として引き継ぐ。実印刷は#134の必須条件に追加しない。

既存v0.3実測の0.583〜1.136秒／217,277〜225,433 bytesと、過去の同時2チェーン約1秒は根拠として維持する。採用後の最終組合せ・同時試験は未実施。基準は条件付き検証の受入値で、本番SLA・最大画像・site経由を保証しない。

## 12. 採用後の残作業と、後続設計

R1〜R8と既存の名称／数量／設定／取引／備考は採用済み。残るのは[最終検証F1〜F6](../FINAL-VALIDATION-site-134.md)で、仕様採否を未確定へ戻さない。取引と備考の境界／省略＋nameLines／数量設定の組合せ、0金額・両税率・任意ラベル／画像省略、数値上限、事前拒否の正式形式とAPI0、採用基準に対する単独／共有枠内同時測定を、小さな追加検証でそろえる。

後続に残す設計はcompanyId／templateId／asset IDの正式値、版識別・参照／権限・生成時スナップショットの製品設計、公開API経路、保存／更新／認証・混雑／timeout等のHTTP契約。これらはR1〜R8で公開APIとして確定したものではない。例のIDは架空・仮置きのまま。登録／発行本文64KiBと保存／HTTP入口の整合、実HTTP応答、製品テンプレート・ブラウザの確認は後続実装で行う。

今回の採用更新ではvalidator・製品・PDF API・新規PDFを実行していない。過去の成功を採用後の追加確認済みへ読み替えず、F1〜F6を残して両Issueをopenとする。認証・会社保存・画像保存・可変明細・一括合成は先行実装しない。
