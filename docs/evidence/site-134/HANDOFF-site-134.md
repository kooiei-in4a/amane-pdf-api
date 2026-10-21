# site実装担当への引き継ぎ：帳票テンプレートと #134 の残作業

## 決定・現在の状態

ユーザーは、このチャットで提示した**罫線整理案を採用する方針**を承認した。請求書・見積書とも、宛先等の囲み枠を外し、明細は薄い横線、合計は上下線と文字サイズで整理する。

続いて、**数量欄の拡張・小数点揃えを採用**した。数量は文字列のまま整数部最大5桁・小数部最大3桁（最大9文字）とし、`2.50`／`1.000`／`0.010`の末尾0を保持する。整数は小数点基準位置に末尾を揃える。旧「全体5文字」制限は引き継がない。[標準JSON案 v0.2](standard-json-draft-v0.2/README.md)と[比較PDF・配置・実測](quantity-decimal-comparison/README.md)が最新の数量方針。

採用範囲は数量の桁数・表示と罫線方針。会社名／宛先／住所の20文字×2行や`nameLines`、取引・備考配分、その他の必須性・金額／税区分・HTTP契約は引き続き[判断資料](INITIAL-SPEC-RECOMMENDATION.md)の未確定事項。

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
| 同スクリプトの `definition` | 合計金額 `total` を9→12pt、`label.total` を9→10ptへ。quantity／heading.quantityの論理セルをx=.3800・幅=.0950へ、name／heading.nameの幅を.3200へ。数量maxLengthは5→9。数量8pt・品名9pt、他の列の位置は維持。 |
| `src/Amane.Tools.Site/wwwroot/pdf-templates/invoice/template.pdf` と `estimate/template.pdf` | generatorから再生成。手作業でPDFだけ変更してgeneratorと乖離させない。 |
| 同ディレクトリの各 `definition.json` | 合計のfontSize、数量・品名のセルと数量maxLength、生成されたtemplate.pdfの `sha256` を整合させる。 |
| 標準JSON→fieldsの製品側表示変換（未実装） | 固定フォントの送り幅で整数部の幅を求め、数量全体を1 fieldとしてleft配置する。値ごとに開始xと送信矩形幅を調整。整数部5桁／小数部3桁を別々に検査し、末尾0を保持。小数点揃えはdefinitionだけでは成立しない。 |
| `src/Amane.Tools.Site/wwwroot/pdf-templates/files.sha256` | 更新対象資産のハッシュを整合させる。 |
| 既存テンプレート検証・プレビュー期待値 | ガイド枠に依存する判定を点検し、正式罫線でも文字の欠け・配置を検証できるよう必要最小限を調整する。 |

検証見直しの入口は `scripts/tests/test-document-templates.mjs`、`scripts/verify-pdf-document-templates-browser.mjs`、`scripts/fill-template-ink-metrics.py` と `docs/evidence/pdf-fill-template-templates/`。変更が必要かを確認してから調整し、CI改善中の別作業へ広げない。

採用案の見た目は次のとおり。

- 宛先・発行者・日付・住所・税率別内訳・備考・振込先などの囲み枠を削除。
- 明細の縦線を削除し、固定8行の薄い横線を残す。見出しは薄い灰色の背景と下線。
- 合計は上下線と上記fontSizeで強調。現行APIの固定フォントのまま、太字切替は行わない。
- 罫線比較では表示文字列・金額・文字矩形・画像とその配置を維持した。後続の数量比較では上記の数量・品名セルだけを調整し、入力文字列・確定金額・他の文字位置・画像を維持した。

数量配置の入口は[compare-decimal-quantity.py](compare-decimal-quantity.py)の`decimal_fields()`。論理セルは数量x=.3800・幅=.0950（56.525pt、右端.4750維持）、品名x=.0550・幅=.3200（190.4pt）で、相互に2.975ptの間隔。検証版の小数点基準は261.469pt、API座標の小数4桁への丸め余白は0.06pt。実送信矩形は[fields.json](quantity-decimal-comparison/invoice-8/proposed/fields.json)／[quantity-placement.json](quantity-decimal-comparison/invoice-8/quantity-placement.json)を参照する。固定フォントやレイアウトを変える場合は送り幅と基準位置を再確認する。

既存のブラウザ用整数数量・税額計算をB2Bの表示変換へ流用しない。共通テンプレート資産の更新時も、一般向け機能の入力・計算契約を無断で変更しない。

背景の線の座標・濃さ・太さは [compare-lines.py](compare-lines.py) の `sample_template()`、fontSizeは比較用 `invoice.definition.json` / `estimate.definition.json` を参照する。文字位置定義と背景罫線は別の役割であり、文字矩形まで囲み枠として出力する必要はない。

## #134 に残っている作業

1. **標準JSONの残る確定**：数量の文字列保持・整数部5桁／小数部3桁・小数点揃えは採用済み。その他の登録情報／発行データ、必須・型・金額上限・税区分・対応外を決める。数量0／負数を対象外とする案、JPY・非負整数金額・税抜・10%／軽減8%等をまとめて確定したわけではない。
2. **標準項目の表示先を確定**：取引日／期間はサンプルでは備考へ仮配置している。正式欄・ラベル、自由備考との共存、宛先住所／部署／担当者の扱い、長い会社名・住所・品名、ロゴ／印影の予約領域を決める。罫線案の採用だけでは、これらの表示先は確定しない。
3. **変換・入力エラーの仕様を決定**：金額を再計算しない変換と、元入力パスを示すサーバー側事前検証を整理する。ブラウザ用 `pdf-document-templates-rules.js` の整数数量・税額計算は流用しない。9明細、必須不足、未対応文字、枠超過、画像上限等のエラー例を決める。無断の削除・置換・正規化・縮小で成功させない。
4. **採用条件を確定**：既存fill-template 1回→stamp-image 2回を初期方式とし、siteの共有実行枠を踏まえた呼び出し条件、品質・時間・最終サイズの基準を決める。検証時の単独10秒／同時各20秒／2MiBは仮目安。site経由の測定とAPI単体の実測を区別する。長時間の負荷試験は追加しない。
5. **結果を管理先へ統合**：標準JSON例・項目対応・エラー例・固定SHA・代表PDF・測定条件・採用方式・制限を #134 にまとめ、親 #133 に判断と後続作業を反映する。
6. **v0.2の検証用変換を確認**：過去の`validate-standard-json.py`はv0.1の全体5文字制限のまま。旧例・測定の再現用として残し、別の改訂用コピーで新しい数量規則と配置をつなぎ、v0.2の両帳票1／8明細を確認する。`99999.999`等のfields描画は確認済みだが、v0.2全体の事前検証→表示変換→API描画を完了扱いにしない。

数量のエラー例は`/items/0/quantity`：`100000`（整数部6桁）・`1.0000`（小数部4桁）はquantity-format、数値型`1.25`はinvalid-type。`123.45`／`99999.999`を旧5文字制限で拒否しない。単なる9文字制限へ置き換えるだけでは整数部／小数部の上限を守れない。HTTP／codeの正式契約は未確定。

既存APIは業務上の8明細制限や必須項目を知らない。9行目を直接座標指定すると200でも注記と重なるため、siteで `items[8]` 等を明示拒否する。APIの400/422には元入力パスがないので、siteで入力パスとfields配列indexを対応させる。

文字制約の例：𠮷・絵文字・異体字セレクタは400、長文の枠超過は422 `field-overflow`。NFD結合濁点は入力コードを保持するが独立幅で描かれるため、標準入力での扱いと明示修正案内を決める。PNGは1MiB・各辺2000px、配置幅はページ幅比0.02〜0.5。

## 完成条件

**テンプレート調整部分の完成条件（将来の実装時）**

- 採用した罫線・合計強調を、請求書／見積書の1明細・8明細に反映し、各1ページで線と文字の衝突・欠けがない。
- 数量欄の拡張・小数点揃えも反映し、`99999`、`99999.999`、`0.001`、`123.45`、`2.50`、`1.000`、`0.010`を入力文字列のまま表示・抽出できる。整数末尾も共通基準に揃い、品名17文字＋軽減記号・単価上限との非交差を確認する。
- generator、PDF、definition、ハッシュが一致し、再生成して同じ資産になる。証明書等の対象外テンプレートや既存一般向け機能への意図しない変更がない。
- 同じ入力の文字・金額がそのまま抽出できる。画像なし／ロゴ・印影合成後の位置・縦横比・透過・重ね順を確認できる。
- 既存のテンプレート・表示・API接続に関係する必要な検証が通り、差分と結果がレビュー可能である。

**#134 全体の完成条件**

- 標準JSON例、登録情報／発行時データ、必須・型・上限・対応外、金額を再計算しない責任分担が確定している。
- 標準項目すべての表示先と必要なレイアウト調整が明確で、1／8明細の両帳票と文字・PNG合成を代表入力で確認している。
- 必須不足・9明細・未対応文字・枠超過等について、修正対象の項目パスと理由を示すエラー例が決まっている。
- v0.2の数量の正常／桁数超過／型エラーを検証用スクリプトで示し、旧全体5文字制限が残っていないことを確認する。製品HTTP応答の試験と混同しない。
- 合意した品質・時間・サイズ基準に対し、条件付きの単独／既存共有枠内の同時実行結果を記録し、未確認事項を区別している。
- 上記の根拠・成果物・採否を #134 に残し、親 #133 に判断と後続を反映している。罫線反映だけで #134 を閉じない。

#134は入力契約・描画方針を決めるための小さな検証Issue。認証、DB、会社登録、画像保存、新公開API、独自スキーマ変換、可変明細・自動改ページ・一括合成の製品実装を完了条件へ追加しない。今回必須のAPI変更は確認されておらず、API新規Issueは未起票。

## 確認済みの根拠と成果物

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
| 最新の標準JSON案・数量例 | [v0.2](standard-json-draft-v0.2/README.md)、[請求書8明細](standard-json-draft-v0.2/examples/invoice-8.json)、[見積書8明細](standard-json-draft-v0.2/examples/estimate-8.json) |
| 採用した数量配置の比較PDF | [請求書1明細](quantity-decimal-comparison/invoice-1/comparison.pdf)／[8明細](quantity-decimal-comparison/invoice-8/comparison.pdf)、[見積書1明細](quantity-decimal-comparison/estimate-1/comparison.pdf)／[8明細](quantity-decimal-comparison/estimate-8/comparison.pdf)。1ページ目＝現行数量配置、2ページ目＝採用案。 |
| 整数5桁＋小数3桁の同時上限 | [請求書99999.999](quantity-decimal-comparison/extended/invoice/final.pdf)／[見積書99999.999](quantity-decimal-comparison/extended/estimate/final.pdf) |
| 数量の定義・実配置 | [v0.2の請求書定義](standard-json-draft-v0.2/quantity-layout/invoice.definition.json)／[見積書定義](standard-json-draft-v0.2/quantity-layout/estimate.definition.json)は数量maxLength9へ整合した論理セルコピー。数量以外は検証時点の候補のまま。実描画済みのfieldsとquantity-placementは数量比較ディレクトリを参照。旧比較定義のmaxLength5は当時の記録であり、v0.2へ引き継がない。 |
| 数量比較の根拠・再現 | [結果・手順](quantity-decimal-comparison/README.md)、[results.json](quantity-decimal-comparison/results.json)、[compare-decimal-quantity.py](compare-decimal-quantity.py) |
| 今回の比較成果物一式 | [共有ディレクトリ](README.md)（取得・再現手順を記載） |
| 初期描画検証全体 | [README.md](README.md)、`reproduce.py`、`results/`、`site-assets/`、共有する代表入力・PDFと測定JSON（制限超過ケースの詳細は既報Issueコメントを参照） |

比較案4ケースは、各1ページ、入力・現行版と欄別文字が完全一致、qpdf検査成功、画像検査成功。既存3回呼び出しは0.574〜0.690秒、最終219,544〜221,533 bytes。前回の同時2チェーンは同じ共有2枠で各約1秒。条件はCPU 1、RAM 1536MiB、tmpfs 256MiBのローカルDockerで、site経由や本番の性能保証ではない。

サンプルは確定金額保持を確かめるため、数量×単価・税計算と意図的に一致しない金額を使用している。そのまま実取引用の計算例にしない。pdf.js／Acrobat・実印刷・site経由時間・他機能との資源競合は今回未確認。表示・抽出はPopplerで確認した。
