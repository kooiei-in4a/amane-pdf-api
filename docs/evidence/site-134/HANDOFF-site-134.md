# site実装担当への引き継ぎ：帳票テンプレートと #134 の残作業

## 決定・現在の状態

ユーザーは、このチャットで提示した**罫線整理案を採用する方針**を承認した。請求書・見積書とも、宛先等の囲み枠を外し、明細は薄い横線、合計は上下線と文字サイズで整理する。

続いて、**数量欄の拡張・小数点揃えを採用**した。数量は文字列のまま整数部最大5桁・小数部最大3桁（最大9文字）とし、`2.50`／`1.000`／`0.010`の末尾0を保持する。整数は小数点基準位置に末尾を揃える。旧「全体5文字」制限は引き継がない。[最新の標準JSON案 v0.3](standard-json-draft-v0.3/README.md)と[比較PDF・配置・実測](quantity-decimal-comparison/README.md)が最新の数量方針。

**追加方針：小数点揃えと当初の右揃えの両モードを用意する。** 数量規則・拡張セルは共通とし、配置だけを切り替える。小数点揃えでは上記の整数末尾基準、右揃えでは数量文字列全体の右端を揃える。設定仕様も採用済み。登録帳票設定直下と発行JSONルートの`quantityAlignment`で選び、値は`decimal`／`right`、既定decimal、発行時上書き可、発行値＞登録値＞decimal。登録値不正は正しい発行値があっても拒否する。PDF APIへ渡す新しいalign値ではない。

**採用済みの設定仕様（製品未実装）**：[数量モード設定](QUANTITY-MODE-SETTINGS-DRAFT.md)。発行値と登録値を検証してから優先順位を適用し、登録値が不正なら上書きで救済しない。上書きはその発行だけに適用し登録値を変更しない。省略とnullを区別し、発行エラーは`/quantityAlignment`、登録値不正は`/templateId`と登録元source.path=`/quantityAlignment`を示す。採否判断は完了。[検証用スクリプトの結果](quantity-resolution-validation/README.md)で正常9件の優先順位と実効モードPDF、不正40件の描画前拒否・API呼び出し0を確認済み。製品保存・HTTPの確認は別途。

**名称・宛先仕様も採用済み（製品未実装）**：[採用仕様](NAME-RECIPIENT-SPEC-PROPOSAL.md)／[最新の標準JSON案v0.3](standard-json-draft-v0.3/README.md)。登録会社nameLines、発行recipient.nameLinesは必須のstring配列1〜2行・各20文字、LF込み41文字。会社addressLinesも必須・同じ上限。部署・担当者・敬称は宛先2行に含める。宛先住所の専用key／欄、部署／担当者の独立key、旧name形式の併存・自動移行は初版対象外。受けて捨てず事前拒否する。

名称のfields描画に続き、[nameLinesからの通し検証](standard-json-v03-validation/README.md)も正常18件・拒否66件で確認済み。旧v0.2のJSON・スクリプト・正常14件／拒否65件・PDFは再現用に残す。新しい資料例と名称／数量の定義コピーはv0.3へ分けた。取引・備考配分と帳票別必須性も採用済み。残る必須性・税区分・金額上限・数量0／負数・ゼロ表示・文字制約・入力エラー形式・品質基準も[R1〜R8](INITIAL-SPEC-ADOPTED.md)として一括採用済み。ID／版・認証／運用等の後続設計は別範囲。

**取引情報・備考も採用済み（製品未実装）**：[採用仕様](TRANSACTION-NOTES-SPEC-PROPOSAL.md)／[標準JSON v0.3](standard-json-draft-v0.3/README.md)。共有最大3行・48文字（LF込み）、自由備考は各行16文字でdate時最大2行、period時最大1行、取引省略時最大3行・LF込み48文字。専用欄は初版対象外。transactionは請求書必須／見積書任意。取引情報を先頭、自由備考を後ろへLF結合し、固定ラベル「取引・備考」。見積書で両方省略なら値・ラベルなし。発行日等から補完せず、超過分を削除・縮小しない。

**初版R1〜R8も一括採用済み**：[採用記録](INITIAL-SPEC-ADOPTED.md)。標準JSONv0.3の必須表、JPY・税抜・軽減8%／10%、単価8桁／他金額12桁の非負整数、正数量のみ、両税率の確定値と0円表示、未対応文字・NFD結合濁点拒否、400／413／422＋first-error＋JSON Pointer／source、単独10秒／同時各20秒／2MiB等を採用した。元の名称・数量・設定・取引／備考・罫線は維持。新規描画・validator修正は今回なし。

現在は検証用サンプルまで。**製品実装はまだ行わないという依頼に従い、製品コード・API契約・CI・共有mainは未変更。** 本書は、次の担当が変更対象と未確定事項を把握するための引き継ぎである。#134 全体は未完了、Issueはopenのまま。

- 親：[site #133](https://github.com/kooiei-in4a/amane-tools-site/issues/133)
- 作業：[site #134](https://github.com/kooiei-in4a/amane-tools-site/issues/134)
- [描画検証結果](https://github.com/kooiei-in4a/amane-tools-site/issues/134#issuecomment-6341176958)、[罫線整理案の追加報告](https://github.com/kooiei-in4a/amane-tools-site/issues/134#issuecomment-6341433581)
- 固定検証版：API `465088ed0b575f055beb6851e98d28ed22fb9928`、site資産 `e08aed241245f9f84a7336302edcb7d6a89a57b1`。実装着手時のmainとの差分は担当側で確認する。比較元を動くmainへ置き換えない。

## テンプレートの変更対象

以下は **siteリポジトリ内の変更対象**。今回の作業では未変更。

| 対象 | 反映する内容 |
| --- | --- |
| `scripts/make-pdf-templates.mjs` の `pdfBytes` | 現行は全非fixed項目に灰色の検査用矩形を描く。検査用ガイドと正式帳票の罫線を分け、invoice／estimateの背景を採用案へ変更する。 |
| 同スクリプトの `definition` | 合計金額 `total` を9→12pt、`label.total` を9→10ptへ。quantity／heading.quantityの論理セルをx=.3800・幅=.0950へ、name／heading.nameの幅を.3200へ。数量maxLengthは5→9。数量8pt・品名9pt。issuer／recipient／addressは幅.29→.34、multiline・maxLines2・maxLength41へ。3欄のx/y/高さ・9ptは維持。notesは共有3行／48文字、label.notesは固定「取引・備考」。専用欄なし。 |
| `src/Amane.Tools.Site/wwwroot/pdf-templates/invoice/template.pdf` と `estimate/template.pdf` | generatorから再生成。手作業でPDFだけ変更してgeneratorと乖離させない。 |
| 同ディレクトリの各 `definition.json` | 合計のfontSize、数量・品名セル、名称／住所の幅・型・maxLines/maxLength、生成されたtemplate.pdfの `sha256` を整合させる。 |
| 標準JSON→fieldsの製品側表示変換（未実装） | decimalモードでは固定フォントの送り幅で整数部の幅を求め、数量全体を1 fieldとしてleft配置する。値ごとに開始xと送信矩形幅を調整。整数部5桁／小数部3桁を別々に検査し、末尾0を保持。小数点揃えはdefinitionだけでは成立しない。名称／会社住所は各行20文字・2行を事前検証してLF結合し、実矩形を検査。宛先の部署・担当者・敬称も同じ2行内。旧key／宛先住所等は拒否。取引・備考の合成前後の上限を検査してから描画。見積で両方省略ならnotes値・ラベルなし。 |
| `src/Amane.Tools.Site/wwwroot/pdf-templates/files.sha256` | 更新対象資産のハッシュを整合させる。 |
| 既存テンプレート検証・プレビュー期待値 | ガイド枠に依存する判定を点検し、正式罫線でも文字の欠け・配置を検証できるよう必要最小限を調整する。 |

検証見直しの入口は `scripts/tests/test-document-templates.mjs`、`scripts/verify-pdf-document-templates-browser.mjs`、`scripts/fill-template-ink-metrics.py` と `docs/evidence/pdf-fill-template-templates/`。変更が必要かを確認してから調整し、CI改善中の別作業へ広げない。

採用案の見た目は次のとおり。

- 宛先・発行者・日付・住所・税率別内訳・備考・振込先などの囲み枠を削除。
- 明細の縦線を削除し、固定8行の薄い横線を残す。見出しは薄い灰色の背景と下線。
- 合計は上下線と上記fontSizeで強調。現行APIの固定フォントのまま、太字切替は行わない。
- 罫線比較では表示文字列・金額・文字矩形・画像とその配置を維持した。後続の数量比較では上記の数量・品名セルだけを調整し、入力文字列・確定金額・他の文字位置・画像を維持した。

数量配置の入口は[compare-decimal-quantity.py](compare-decimal-quantity.py)の`decimal_fields()`。論理セルは数量x=.3800・幅=.0950（56.525pt、右端.4750維持）、品名x=.0550・幅=.3200（190.4pt）で、相互に2.975ptの間隔。検証版の小数点基準は261.469pt、API座標の小数4桁への丸め余白は0.06pt。実送信矩形は[fields.json](quantity-decimal-comparison/invoice-8/proposed/fields.json)／[quantity-placement.json](quantity-decimal-comparison/invoice-8/quantity-placement.json)を参照する。固定フォントやレイアウトを変える場合は送り幅と基準位置を再確認する。

上記のx調整はdecimalモードのみ。rightモードは同じ拡張セルで既存APIのalign=rightを使う。旧幅・5文字制限へ戻さず、末尾0や確定金額も変えない。以前の比較PDFの現行側は旧幅の右揃え。[今回の同一入力比較](quantity-mode-comparison/README.md)では、拡張セルの両モード・最大9文字・末尾0・隣接欄への影響を両帳票1／8明細で確認済み。切替はローカルfixtureであり、製品設定の保存／読込・製品HTTP応答は未確認。既定値・優先順位・拒否規則は[後続のfixture検証](quantity-resolution-validation/README.md)で確認済み。

既存のブラウザ用整数数量・税額計算をB2Bの表示変換へ流用しない。共通テンプレート資産の更新時も、一般向け機能の入力・計算契約を無断で変更しない。

背景の線の座標・濃さ・太さは [compare-lines.py](compare-lines.py) の `sample_template()`、fontSizeは比較用 `invoice.definition.json` / `estimate.definition.json` を参照する。文字位置定義と背景罫線は別の役割であり、文字矩形まで囲み枠として出力する必要はない。

## #134 に残っている作業

**仕様の採否はR1〜R8まで完了。残るのは最終仕様にそろえる検証と報告。** 詳細・入力・証拠・後続との境界は[最終検証F1〜F6](FINAL-VALIDATION-site-134.md)を参照する。

1. **F1 最終組合せ描画**：両帳票1／8明細×数量両モードを基本に、nameLines20×2、備考48文字／期間43文字、取引／備考省略、年越し／同日periodを少数ケースで網羅し、設定解決・確定金額・画像と同じ経路で確認する。
2. **F2 ゼロ・任意・画像**：正数量のまま金額0と両税率の0円／ラベルを表示。任意項目の値／ラベル省略、画像なし／片方のみを確認する。
3. **F3 数値境界**：単価8桁／他金額12桁の上限と実幅、0.001〜99999.999・末尾0・両配置を確認する。相互計算はしない。
4. **F4 事前拒否**：新しい採用条件と既存66拒否例等を差分補完し、元path／登録source・first-error形式・事前拒否時実API0を示す。実HTTPと区別する。
5. **F5 受入基準**：最終入力で単独と既存共有2枠内の同時2チェーンを少数測定し、10秒／各20秒／2MiBと品質・後始末を確認する。長時間負荷試験は追加しない。
6. **F6 統合報告**：固定SHA・入力・PDF・再現手順・検査／測定結果・制限を#134にまとめ、親#133へ判断と後続を反映する。

[v0.3正常18／拒否66](standard-json-v03-validation/README.md)、[数量解決正常9／拒否40](quantity-resolution-validation/README.md)、[旧v0.2正常14／拒否65](standard-json-v02-validation/README.md)は既存の根拠。入力・PDF・スクリプト・実測は過去の条件のまま残し、今回の一括採用だけで最終検証済みにはしない。


数量のエラー例は`/items/0/quantity`：`100000`（整数部6桁）・`1.0000`（小数部4桁）はquantity-format、数値型`1.25`はinvalid-type。`123.45`／`99999.999`を旧5文字制限で拒否しない。単なる9文字制限へ置き換えるだけでは整数部／小数部の上限を守れない。数量形式は422 quantity-format、数値型は422 invalid-type、ゼロは422 out-of-range、負数はquantity-formatとする採用形式。製品HTTPは未確認。quantityAlignmentの設定エラーは採用済み仕様を参照する。

既存APIは業務上の8明細制限や必須項目を知らない。9行目を直接座標指定すると200でも注記と重なるため、siteで `items[8]` 等を明示拒否する。APIの400/422には元入力パスがないので、siteで入力パスとfields配列indexを対応させる。

既存PDF API単体の文字制約の例：𠮷・絵文字・異体字セレクタは400（siteの採用事前検証は422）、長文の枠超過は422 `field-overflow`。NFD結合濁点はAPIが独立幅で描くため、採用したsite側事前検証では拒否し合成済み文字への修正を案内する。PNGは1MiB・各辺2000px、配置幅はページ幅比0.02〜0.5。

## 完成条件

**テンプレート調整部分の完成条件（将来の実装時）**

- 採用した罫線・合計強調を、請求書／見積書の1明細・8明細に反映し、各1ページで線と文字の衝突・欠けがない。
- 数量欄の拡張・小数点揃えも反映し、`99999`、`99999.999`、`0.001`、`123.45`、`2.50`、`1.000`、`0.010`を入力文字列のまま表示・抽出できる。整数末尾も共通基準に揃い、品名17文字＋軽減記号・単価上限との非交差を確認する。
- 同じ拡張セル・数量規則で右揃えも選択できる。モードを切り替えても入力文字列・末尾0・確定金額は不変で、選択した配置と定めた既定値・不正値時の挙動が確認できる。採用した設定解決・エラー規則と一致することを検証用コピーで確認する。
- 名称・会社住所の各20文字×2行を欠けず表示・抽出し、部署・担当者・敬称も宛先2行内。旧name形式や宛先住所等の対象外項目を描画前に拒否できる。新形式の検証結果と過去のfields描画を区別する。
- 取引／備考を採用配分で表示・抽出し、共有3行／48文字と各備考行16文字を守る。請求書transaction必須／見積書任意、両方省略時ラベルなし、上限超過時の描画前拒否を確認する。
- generator、PDF、definition、ハッシュが一致し、再生成して同じ資産になる。証明書等の対象外テンプレートや既存一般向け機能への意図しない変更がない。
- 同じ入力の文字・金額がそのまま抽出できる。画像なし／ロゴ・印影合成後の位置・縦横比・透過・重ね順を確認できる。
- 製品反映時は既存ブラウザプレビューで代表4帳票を確認し、既存のテンプレート・表示・API接続に関係する必要な検証が通り、差分と結果がレビュー可能である。

**#134 全体の完成条件**

- 標準JSON例、登録情報／発行時データ、必須・型・上限・対応外、金額を再計算しない責任分担が確定している。
- 標準項目すべての表示先と必要なレイアウト調整が明確で、1／8明細の両帳票と文字・PNG合成を代表入力で確認している。
- 必須不足・9明細・未対応文字・枠超過等について、修正対象の項目パスと理由を示すエラー例が決まっている。
- v0.2の数量の正常／桁数超過／型エラーを検証用スクリプトで示し、旧全体5文字制限が残っていないことを確認する。製品HTTP応答の試験と混同しない。
- R8の単独10秒／同時各20秒／2MiBと品質基準に対し、条件付きの単独／既存共有枠内の同時実行結果を記録し、未確認事項を区別している。
- 上記の根拠・成果物・採否を #134 に残し、親 #133 に判断と後続を反映している。罫線反映だけで #134 を閉じない。

#134は入力契約・描画方針を決めるための小さな検証Issue。認証、DB、会社登録、画像保存、新公開API、独自スキーマ変換、可変明細・自動改ページ・一括合成の製品実装を完了条件へ追加しない。今回必須のAPI変更は確認されておらず、API新規Issueは未起票。ID／版・保存・認証・参照権限・運用HTTP契約、実HTTP／site経由測定は後続範囲。詳細は[最終検証と後続の境界](FINAL-VALIDATION-site-134.md)に記す。

## 確認済みの根拠と成果物

- [初版R1〜R8の採用記録](INITIAL-SPEC-ADOPTED.md)
- [採用後の最終検証F1〜F6・後続作業・完了条件](FINAL-VALIDATION-site-134.md)

共有成果物の基点は、このファイルと同じ `docs/evidence/site-134/`。このブランチは成果物の共有専用で、製品実装は含まない。比較PDF・定義ファイル・入力例は相対リンクから参照できる。元の検証環境には追加の制限超過ケース等の成果物が残っている。

| 内容 | 場所 |
| --- | --- |
| 罫線比較の説明・検証結果 | [比較資料の案内](README.md) |
| 4ケースの左右比較 | [line-comparison/index.html](line-comparison/index.html) |
| 比較PDF | `line-comparison/{invoice,estimate}-{1,8}/comparison.pdf`。1ページ目＝現行、2ページ目＝整理案。 |
| 整理案／現行の単独PDF | 同ケースディレクトリの `final.pdf` / `current.pdf`。PNGプレビューも併置。 |
| 文字・画像・サイズ等の照合 | [line-comparison/results.json](line-comparison/results.json) |
| 背景PDF／位置定義の比較用コピー | `line-comparison/refined-template.pdf`、`line-comparison/invoice.definition.json`、`line-comparison/estimate.definition.json` |
| 比較生成スクリプト | [compare-lines.py](compare-lines.py) |
| 採用した数量設定・登録例 | [設定仕様と解決表](QUANTITY-MODE-SETTINGS-DRAFT.md)、[登録rightの請求書](standard-json-draft-v0.2/settings-examples/invoice.json)／[登録decimalの見積書](standard-json-draft-v0.2/settings-examples/estimate.json)。発行1明細例で逆モードへ上書き、8明細例では上書きを省略。 |
| v0.3 nameLines通し検証 | [結果・全PDF・入力・エラー・手順](standard-json-v03-validation/README.md)、[スクリプト](validate-standard-json-v03.py)。正常18・拒否66・拒否時API0。 |
| v0.2候補全体の通し検証 | [結果・PDF・入力・代表エラー・再現手順](standard-json-v02-validation/README.md)、[改訂スクリプト](validate-standard-json-v02.py)。正常14件・拒否65件、拒否時API0。当時の候補条件による履歴。現在の採否はR1〜R8を参照。 |
| 最新の標準JSON案・名称／数量例 | [v0.3](standard-json-draft-v0.3/README.md)、[会社長名](standard-json-draft-v0.3/examples/company-registration-long.json)、[請求書8明細・担当者](standard-json-draft-v0.3/examples/invoice-8-long.json)、[見積書8明細・部署](standard-json-draft-v0.3/examples/estimate-8-long.json)。新形式PDFは[v0.3結果](standard-json-v03-validation/README.md)へ。 |
| 採用名称・数量を組み合わせた定義コピー | [請求書](standard-json-draft-v0.3/layout/invoice.definition.json)／[見積書](standard-json-draft-v0.3/layout/estimate.definition.json)。検証用コピーで、[v0.3結果](standard-json-v03-validation/README.md)から描画済み。 |
| 採用した数量配置の比較PDF | [請求書1明細](quantity-decimal-comparison/invoice-1/comparison.pdf)／[8明細](quantity-decimal-comparison/invoice-8/comparison.pdf)、[見積書1明細](quantity-decimal-comparison/estimate-1/comparison.pdf)／[8明細](quantity-decimal-comparison/estimate-8/comparison.pdf)。1ページ目＝現行数量配置、2ページ目＝採用案。 |
| 整数5桁＋小数3桁の同時上限 | [請求書99999.999](quantity-decimal-comparison/extended/invoice/final.pdf)／[見積書99999.999](quantity-decimal-comparison/extended/estimate/final.pdf) |
| 数量の定義・実配置 | [v0.2の請求書定義](standard-json-draft-v0.2/quantity-layout/invoice.definition.json)／[見積書定義](standard-json-draft-v0.2/quantity-layout/estimate.definition.json)は数量maxLength9へ整合した論理セルコピー。数量以外は検証時点の候補のまま。実描画済みのfieldsとquantity-placementは数量比較ディレクトリを参照。旧比較定義のmaxLength5は当時の記録であり、v0.2へ引き継がない。 |
| 拡張セルの両モード比較 | [結果・全PDF・入力・共通定義](quantity-mode-comparison/README.md)、[compare-quantity-modes.py](compare-quantity-modes.py)。比較PDFは1ページ目decimal、2ページ目right。 |
| 数量比較の根拠・再現 | [結果・手順](quantity-decimal-comparison/README.md)、[results.json](quantity-decimal-comparison/results.json)、[compare-decimal-quantity.py](compare-decimal-quantity.py) |
| 今回の比較成果物一式 | [共有ディレクトリ](README.md)（取得・再現手順を記載） |
| 初期描画検証全体 | [README.md](README.md)、`reproduce.py`、`results/`、`site-assets/`、共有する代表入力・PDFと測定JSON（制限超過ケースの詳細は既報Issueコメントを参照） |

比較案4ケースは、各1ページ、入力・現行版と欄別文字が完全一致、qpdf検査成功、画像検査成功。既存3回呼び出しは0.574〜0.690秒、最終219,544〜221,533 bytes。前回の同時2チェーンは同じ共有2枠で各約1秒。条件はCPU 1、RAM 1536MiB、tmpfs 256MiBのローカルDockerで、site経由や本番の性能保証ではない。

サンプルは確定金額保持を確かめるため、数量×単価・税計算と意図的に一致しない金額を使用している。そのまま実取引用の計算例にしない。pdf.js／Acrobat・実印刷・site経由時間・他機能との資源競合は今回未確認。表示・抽出はPopplerで確認した。
