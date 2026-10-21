# site #134：採用した罫線整理案・実装引き継ぎ

**成果物共有専用。製品実装・API契約・CI・共有mainは変更していません。** ユーザーは罫線整理案、数量の整数部5桁／小数部3桁・末尾0保持、小数点揃え／右揃えの両モードを用意する方針を採用しました。既定モードは小数点揃えを推奨し、未確定です。#134全体は未完了です。

- [site担当向け引き継ぎ文](HANDOFF-site-134.md)：変更対象、#134の残作業、テンプレート調整部分／Issue全体の完成条件。
- [最新の標準JSON案 v0.2](standard-json-draft-v0.2/README.md)：採用した数量規則・表示変換、登録／発行データ、エラー例・改訂入力例。数量以外には未確定事項が残り、製品未実装。
- [旧標準JSON案 v0.1](standard-json-draft/README.md)：過去の検証再現用。旧全体5文字制限を現行案へ引き継がない。
- [数量欄拡張・小数点揃えの比較](quantity-decimal-comparison/README.md)：請求書／見積書の1・8明細、現行数量配置との2ページ比較PDF、末尾0保持・隣接欄・99999.999の追加検証。**数量の桁数・表示は採用済み、製品未反映。検証時点の未確定という記述は過去の記録。**
- [初版の推奨仕様と判断事項](INITIAL-SPEC-RECOMMENDATION.md)：必須・型・上限、取引情報／備考の配分、名称20文字×2行の推奨と宛先住所等の判断点。**数量・罫線以外は未確定、製品未実装。** [名称・住所の追加検証](header-lines-probe/README.md)も参照。
- [標準JSON案からの実描画検証](standard-json-validation/README.md)：請求書／見積書の1・8明細、取引日／期間と備考、確定金額保持、候補入力エラーの再現と実PDF API応答。
- [作業Issue #134](https://github.com/kooiei-in4a/amane-tools-site/issues/134) / [親 #133](https://github.com/kooiei-in4a/amane-tools-site/issues/133)

## PDF・定義ファイル

比較PDFは**1ページ目が現行、2ページ目が整理案**。入力・金額・画像は同じです。

| ケース | 現行PDF | 整理案PDF | 比較PDF | 入力 |
| --- | --- | --- | --- | --- |
| 請求書1明細 | [現行](line-comparison/invoice-1/current.pdf) | [整理案](line-comparison/invoice-1/final.pdf) | [2ページ比較](line-comparison/invoice-1/comparison.pdf) | [表示値JSON](line-comparison/invoice-1/input.display-values.json) |
| 請求書8明細 | [現行](line-comparison/invoice-8/current.pdf) | [整理案](line-comparison/invoice-8/final.pdf) | [2ページ比較](line-comparison/invoice-8/comparison.pdf) | [表示値JSON](line-comparison/invoice-8/input.display-values.json) |
| 見積書1明細 | [現行](line-comparison/estimate-1/current.pdf) | [整理案](line-comparison/estimate-1/final.pdf) | [2ページ比較](line-comparison/estimate-1/comparison.pdf) | [表示値JSON](line-comparison/estimate-1/input.display-values.json) |
| 見積書8明細 | [現行](line-comparison/estimate-8/current.pdf) | [整理案](line-comparison/estimate-8/final.pdf) | [2ページ比較](line-comparison/estimate-8/comparison.pdf) | [表示値JSON](line-comparison/estimate-8/input.display-values.json) |

- [請求書の比較用definition.json](line-comparison/invoice.definition.json)
- [見積書の比較用definition.json](line-comparison/estimate.definition.json)
- [共通背景の整理案PDF](line-comparison/refined-template.pdf)
- 固定版の元資産：[請求書](site-assets/src/Amane.Tools.Site/wwwroot/pdf-templates/invoice/definition.json) / [見積書](site-assets/src/Amane.Tools.Site/wwwroot/pdf-templates/estimate/definition.json)。同ディレクトリに元template.pdfも同梱。
- [比較生成スクリプト](compare-lines.py) / [描画検証スクリプト](reproduce.py)
- [罫線整理案の測定・照合結果](line-comparison/results.json) / [初期描画検証の測定結果](results/results.json)

比較用definitionは背景PDFのSHA256と合計のfontSizeだけを変更した検討用コピー。製品資産への反映は別作業です。GitHubでPDFを表示できない場合はファイル画面のDownload/Rawから取得してください。

## 見た目（8明細）

| 現行 | 整理案 |
| --- | --- |
| ![請求書 現行](line-comparison/invoice-8/current.png) | ![請求書 整理案](line-comparison/invoice-8/final.png) |
| ![見積書 現行](line-comparison/estimate-8/current.png) | ![見積書 整理案](line-comparison/estimate-8/final.png) |

[index.html](line-comparison/index.html) は取得後、ローカルのブラウザで4ケースを左右比較できます。

## 固定版・確認済み事項

API: `465088ed0b575f055beb6851e98d28ed22fb9928` / site資産: `e08aed241245f9f84a7336302edcb7d6a89a57b1`。

4整理案は各1ページ、qpdf検査成功、欄別抽出が入力・現行版と完全一致。画像の透過・縦横比・配置も確認済み。3回呼び出しは0.574〜0.690秒、最終219,544〜221,533 bytes（CPU 1 / RAM 1536MiBのローカルDocker、各1回）。性能差の評価やsite経由の保証ではありません。Acrobat/pdf.js・実印刷は未確認です。

サンプルは「確定金額を再計算しない」検証用に、数量×単価・税計算と意図的に一致しない金額を使用。実取引用ではありません。全会社情報・画像は架空です。

## 取得・再現

このブランチを既存APIリポジトリの作業コピーで参照するか、ファイルを取得してください。必要な代表入力と元資産を同梱し、比較生成スクリプトを単独で再実行できる配置にしています。

共有ディレクトリで、既存APIソースの固定版を検証用の `api-source` に展開します（共有mainを変更しません）。

```bash
cd docs/evidence/site-134
mkdir -p api-source
git -C ../../.. archive 465088ed0b575f055beb6851e98d28ed22fb9928 | tar -x -C api-source
docker build -t amane-pdf-api:site134-465088e api-source
python3 compare-lines.py --output /tmp/amane-site134-line-comparison-rerun
```

既存依存のDocker、qpdf、Poppler、Python/Pillow/numpyを使用。環境による制約を避けるため検査先は `/tmp` としています。初期描画検証の全ケースを再実行する場合は `reproduce.py --output /tmp/amane-site134-rerun` を使用してください。大規模な検証基盤の追加はありません。

## 引き継ぐ判断

A：既存fill-template 1回＋stamp-image 2回を初期方式として採用可能。

B：罫線・合計強調はsite側のgenerator/PDF/definition/ハッシュ/関連検証を整合させて反映。標準JSON、取引日／期間・宛先情報・長文・画像領域の表示先、8明細と必須・文字・枠の事前検証、項目パス付きエラー、正式な性能基準は #134 の残作業。

C：今回必須のAPI変更は確認なし。可変明細・自動改ページ・一括合成を先行実装しない。テンプレート反映だけで #134 全体を閉じない。
