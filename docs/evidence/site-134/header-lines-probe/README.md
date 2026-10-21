# #134 長い名称・住所の表示枠：追加検証

**位置付きfieldsの小さな検証。標準JSON改訂版全体の受理・変換試験ではない。** 製品・API契約・CI・共有mainは変更していない。採否は[初版推奨仕様](../INITIAL-SPEC-RECOMMENDATION.md)で判断する。

固定API `465088ed0b575f055beb6851e98d28ed22fb9928` / site資産 `e08aed241245f9f84a7336302edcb7d6a89a57b1`。[前回の標準JSON検証](../standard-json-validation/README.md)の請求書／見積書8明細のfields・入力由来表示値を基にした。APIイメージIDは[results.json](results.json)を参照。

## 変更した検証用コピー

- issuer・recipient・addressの矩形幅だけ `.29→.34`（172.55→202.3pt）。x/y/高さ、9pt、ラベル矩形は維持。
- issuer・recipientはmultiline、2行、maxLength41へ。addressは2行のままmaxLength32→41。
- `header-lines.json`に印字する名称・住所の行を明示。nameLinesは提案keyの説明用であり、既存v0.1 validatorはこれを受理しない。
- 既存の罫線整理背景、8明細、金額、取引・備考、ロゴ・印影の位置は維持する。

## 結果

| ケース（すべて8明細） | 成果物 | 既存3回呼び出し秒 | 最終bytes | 行要素数 |
| --- | --- | ---: | ---: | ---: |
| invoice-single | [PDF](invoice-single/final.pdf) / [PNG](invoice-single/final.png) | 0.723 | 221,818 | 83 |
| invoice-example | [PDF](invoice-example/final.pdf) / [PNG](invoice-example/final.png) | 0.631 | 225,000 | 85 |
| invoice-boundary | [PDF](invoice-boundary/final.pdf) / [PNG](invoice-boundary/final.png) | 0.612 | 219,383 | 85 |
| estimate-single | [PDF](estimate-single/final.pdf) / [PNG](estimate-single/final.png) | 0.630 | 220,275 | 80 |
| estimate-example | [PDF](estimate-example/final.pdf) / [PNG](estimate-example/final.png) | 0.602 | 223,621 | 82 |
| estimate-boundary | [PDF](estimate-boundary/final.pdf) / [PNG](estimate-boundary/final.png) | 0.574 | 217,282 | 82 |

6 PDFはすべてqpdf検査成功・1ページ。欄別の抽出文字列は各fieldsと完全一致、矩形外word 0。3欄以外の抽出結果は前回と完全一致し、明細・確定金額・日付等は変えていない。透過／半透過画像の画素・縦横比・配置の既存検査も全件成功。144dpiで請求書の長い名称例、見積書の1行名・20文字×2行境界を目視し、欠け・隣接欄との重なりなし。

`single`は既存の1行会社名・宛先名。`example`は会社名＋拠点名25文字、宛先名＋部署／敬称24文字（結合LF込み25文字）、会社住所32文字（結合LFで33）をそれぞれ2行表示。`boundary`は会社名・宛先名・住所の3欄に各20文字×2行（LF込み41文字）。すべて架空データ。

同じ20文字×2行の宛先を元の幅.29・高さ.026の矩形へ直接送ると、実APIはHTTP422／field-overflowでPDFを返さなかった。[送信fields](original-recipient.fields.json) / [応答](original-recipient.problem.json)。文字を削って成功させる必要はなく、siteの欄幅・定義調整で対応できる。

## 実行条件・限界

既存のreproduce.pyとDocker smoke起動処理を再利用。CPU1、RAM1536MiB（追加swapなし）、tmpfs256MiB、read-only、network none、共有2枠／待ち0。既存イメージ・Pillow/numpy/Poppler/qpdfのみで新規依存なし。時間はfill-template→stamp-image→stamp-imageの最終本文受信までで、会社JSON解決・事前検証・描画／抽出検査は含まない。各1回のdocker exec loopback HTTP測定で、本番・site中継の測定ではない。

19 POST（6×3＋元枠エラー1）、health200、成功／失敗の都度assert_cleanでjob・子process残存なし。改訂上限の21文字・3行を拒否するvalidatorは今回実装していない。会社名と宛先の十分な業務範囲を証明する試験でもない。宛先住所の専用欄、全glyphの組み合わせ、ブラウザ表示・実印刷は未確認。

## 再現・ファイル

[検証スクリプト](../probe-header-lines.py)。既存の[成果物環境](../README.md)を用意し、既存PDF・定義コピーをそろえてから実行する。outputは未存在の一時ディレクトリを指定する。

```bash
python3 probe-header-lines.py --output /tmp/amane-site134-header-lines-rerun
```

各ケースにheader-lines.json、definition.json、display-values.json、fields.json、field-index.json、final.pdf／png／txt／extracted.jsonを共有。入力の金額等は基になったstandard-json-validation/{invoice,estimate}-8のcompany.json／request.jsonで追跡できる。ローカルには文字のみ／ロゴ後の中間PDF・PNGも保存する。過去の検討用コピーは公開成果物に混ぜず、この最小幅変更版だけを共有する。
