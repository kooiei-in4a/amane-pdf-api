# #134 数量モード設定：採用した初版仕様（製品未実装）

**ユーザー採用済みの設計仕様。製品未実装。** quantityAlignmentは既定decimal、発行時上書きを許可し、発行明示値＞登録値＞既定decimalで解決する。選択した登録値が不正なら、正しい発行時指定があっても拒否する。設定場所・入力・省略とnullの区別・エラーは以下の採用案にそろえる。数量の両モード・整数部5桁／小数部3桁・文字列と末尾0保持も維持する。製品コード・API契約・CI・共有mainは未変更。#133／#134は未完了。

根拠：[拡張セルの両モード比較](quantity-mode-comparison/README.md)。請求書／見積書の1・8明細×2モード＝8 PDFで、同じ入力・拡張幅56.525ptを使用し、99999.999・末尾0・確定金額・透過画像を保持した。数量以外の文字位置は同一、隣接欄と非交差。**描画の成立は実測したが、以下の設定解決・保存・エラーを試験したものではない。** 既定値・上書き・拒否方針は、この結果を踏まえた今回のユーザー判断で採用した。

## 採用した契約

| 項目 | 採用内容 | 理由・境界 |
| --- | --- | --- |
| 設定場所 | companyId／templateIdから解決する、利用者の登録済み帳票設定の直下 | 会社基本情報とは分け、請求書と見積書で別に選べる。共通の物理PDF・座標定義は両モードで使う。DB構造・設定登録APIの経路は今回設計しない。 |
| key | `quantityAlignment` | 登録帳票設定と発行JSONで同じ名称。発行JSONではルート直下の任意key。明細ごとのkeyは設けず、帳票全体へ1モードを適用。 |
| 型・値 | string、`decimal`または`right`の完全一致 | decimal＝小数点開始位置／整数末尾を揃える。right＝数量文字列全体の右端を揃える。大文字小文字変換・trim・型変換なし。 |
| 既定値 | `decimal` | 新しい帳票生成機能の初版に適用。右揃えを標準にしたい帳票は登録値をrightにする。両モードの性能差を理由に選んだものではない。 |
| 登録時の省略 | decimalを実効既定値とし、登録結果にも明示する | 省略した利用者も登録後の配置を把握できる。保存方法・取得応答の全体設計は後続。 |
| 更新時の省略 | このkeyは現値を維持する。変更はdecimal／rightを明示 | nullによる削除・リセットは認めず、decimalに戻すときはdecimalを指定。更新APIのHTTP method等は今回決めない。 |
| 発行時の上書き | 任意に許可。省略したら登録設定を使う | 1回の表示変更のために帳票設定を複製しなくて済む。上書きはその発行だけで、登録設定を更新しない。 |
| 優先順位 | **発行JSONの明示値 ＞ 登録帳票設定の値 ＞ 標準初期値decimal** | keyの省略だけを未指定とする。null・空文字・不正値を省略扱いにしない。会社既定等の別の継承階層は初版には加えない。 |
| 解決タイミング | 参照した会社・帳票設定を解決した時点で1つの実効モードを確定 | 文字→ロゴ→印影の3回呼び出し全体で同じ設定を使う。履歴DB・過去版管理の実装を要求するものではない。 |

登録帳票設定の部分例（他の登録項目・API経路は省略）：

```json
{
  "quantityAlignment": "right"
}
```

発行JSONの部分例（宛先・明細・確定金額等は省略）：

```json
{
  "companyId": "company_demo",
  "templateId": "invoice-standard-v1",
  "quantityAlignment": "decimal"
}
```

この例では登録値rightより発行値decimalが優先され、登録値はrightのまま。このkeyは数量モードだけを選ぶ。v0.2のJSON例にはこのkeyを反映したが、旧validatorや製品変換には実装していない。

入力例：[登録rightの請求書設定](standard-json-draft-v0.2/settings-examples/invoice.json)／[登録decimalの見積書設定](standard-json-draft-v0.2/settings-examples/estimate.json)。[請求書1明細](standard-json-draft-v0.2/examples/invoice-1.json)は発行decimalで上書き、[見積書1明細](standard-json-draft-v0.2/examples/estimate-1.json)は発行rightで上書きする。[請求書8明細](standard-json-draft-v0.2/examples/invoice-8.json)／[見積書8明細](standard-json-draft-v0.2/examples/estimate-8.json)はkey省略で登録値を使う。これは仕様上の期待値であり、resolverの実行結果ではない。

## 解決ルール

発行値と、選択した登録設定に値がある場合は、**両方の指定値を検証してから**優先順位を適用する。登録設定に不正値があれば、発行値が正しくても登録元のエラーを返す。既定値や発行上書きで不正な登録値を隠さない。設定が存在しない・参照権限がない場合の契約は、参照解決側の別論点として残す。

| 発行JSONの値 | 登録値 | 結果 |
| --- | --- | --- |
| 省略 | 省略 | decimal（初期値） |
| 省略 | right | right |
| 省略 | decimal | decimal |
| decimal | right | decimal、登録値は不変 |
| right | decimal | right、登録値は不変 |
| right | right | right |
| null／不正値 | 正常値または省略 | 発行入力のエラー。登録値へ置換しない。 |
| 正常値または省略 | null／不正値 | 登録設定のエラー。発行値や既定値へ置換しない。 |

登録時の省略をdecimalとして明示化し、登録設定にkeyがない場合の初期値解決は上表のとおり定義する。登録時／更新時／発行時のいずれも、**省略とnullは別**とする。

## 採用した不正値のエラー仕様（応答未実装）

以下は採用した数量モードのsite側エラー仕様。既存PDF APIの応答を変更するものではなく、**HTTP応答の実測結果ではない**。失敗時は描画前に拒否し、PDF APIを呼ばずPDFを返さない方針。

| 入力・状況 | HTTP / code（設計値） | 修正先 |
| --- | --- | --- |
| 数値・boolean・配列・object・null | 422 / invalid-type | 登録／発行payloadの`/quantityAlignment` |
| `""`、`"left"`、`"DECIMAL"`、`" right "` | 422 / unsupported-quantity-alignment | 同上。許可値decimal／rightを案内し、補正しない。 |
| `quantityAlign`等の未知key、明細内のquantityAlignment | 400 / unknown-field | `/quantityAlign`または`/items/0/quantityAlignment`等の元入力path |
| quantityAlignmentの重複key・壊れたJSON | 400 / duplicate-key、invalid-json | 特定できれば該当path、不明ならroot。最後の値を採用しない。 |
| 発行時に解決した登録モードが不正 | 422 / registered-data-invalid | 発行payloadの`/templateId`と、登録元source.path=`/quantityAlignment`を併記 |

発行時の不正な列挙値に対する応答例：

```json
{
  "status": 422,
  "code": "validation-failed",
  "errors": [
    {
      "path": "/quantityAlignment",
      "code": "unsupported-quantity-alignment",
      "message": "decimalまたはrightを指定してください。"
    }
  ]
}
```

登録設定が不正だった場合の応答例：

```json
{
  "status": 422,
  "code": "validation-failed",
  "errors": [
    {
      "path": "/templateId",
      "code": "registered-data-invalid",
      "source": {
        "kind": "templateSettings",
        "path": "/quantityAlignment",
        "code": "unsupported-quantity-alignment"
      }
    }
  ]
}
```

sourceは修正すべき登録項目を指す。登録元が型不正ならsource.codeはinvalid-type。認証／参照エラー、DB等の障害のHTTP設計までは今回決めない。

## 採用決定と未確認事項

| 決定 | 採用した内容 |
| --- | --- |
| 設定場所・キー・既定値 | 登録帳票設定と発行JSONの直下にquantityAlignment、既定decimal。 |
| 発行時上書き | 許可、その発行だけに適用。発行値＞登録値＞decimal。 |
| 不正値・登録不整合 | 全指定値を検証し、正しい発行値があっても不正な登録値を拒否。元入力path／登録元sourceを示す。 |

これらの採否判断は完了。残るのは設定解決・不正値拒否の検証と、後続での製品登録／保存／HTTPの実装・確認。名称・宛先情報、取引／備考配分等の別の未確定事項は引き続き#134に残す。登録APIの経路・HTTP method・DB構造・認証／参照エラーまで今回設計・実装したわけではない。

## 残る小さな確認（製品実装前）

1. 解決表の省略・一致・上書きを、検証用の登録設定JSONと発行JSONから解決し、実効値と登録値不変を確認する。
2. 解決したdecimal／rightを既存の[両モード比較スクリプト](compare-quantity-modes.py)の配置へつなぐ。両帳票1／8明細で数量表記・確定金額・隣接欄・透過画像を確認する。既存の描画単体検証と設定解決の検証を区別する。
3. 発行値の型／列挙値不正、登録値不正、未知／重複keyを、元入力path付きで描画前に拒否できることを確認する。正しい上書きがあっても不正登録を拒否し、PDF API呼び出し0であることを示す。
4. 採用した規則を標準JSON例・項目表・引き継ぎ・エラー例へそろえる。登録保存／認証／製品HTTPの実装と確認は後続作業として扱う。

今回は資料の整理だけで、新しいvalidator・設定保存・公開API・PDF・性能測定を追加していない。金額の再計算も加えない。**A：既存描画APIで両モードに対応。B：設定解決と表示変換はsite側。C：この設定仕様のためのPDF API変更は必要ない。** 新規API Issueは作成せず、製品実装と共有mainの変更は開始しない。
