# site実装準備：標準JSON v0.3の作業順序・分割・完成条件

**実装準備のみ。以下は今後の作業案で、製品実装・新規公開経路・保存・CI・共有mainは変更していない。#133／#134はopen。** 採用済みR1〜R8と名称・数量・設定・取引／備考・罫線は維持する。[標準JSONv0.3](standard-json-draft-v0.3/README.md)が入力仕様、[採用記録](INITIAL-SPEC-ADOPTED.md)が判断の根拠、[最終検証](standard-json-v03-final-validation/README.md)が代表入力・PDF・測定の根拠。[テンプレート変更対象](HANDOFF-site-134.md#テンプレートの変更対象)を併読する。

## 現在の到達点と読む順序

- #134の検証は正常24件＋同時2件、拒否135件は実PDF API呼び出し0。単独0.207〜0.710秒／同時各約0.989秒／208,365〜224,521 bytesで条件付き受入基準内。製品HTTP・保存・認証の検証ではない。
- [#134最終報告](https://github.com/kooiei-in4a/amane-tools-site/issues/134#issuecomment-6353165370)と[親#133への要約](https://github.com/kooiei-in4a/amane-tools-site/issues/133#issuecomment-6353182632)は反映済み。F6の「親未反映」は作成時の履歴で、現在の残作業ではない。両Issueの完了チェック／closeは今回は行わない。
- 最初に標準JSONの必須／型／上限・表示表を読み、次にこの作業分割、具体的なテンプレート変更表、最後に最終結果と代表入力を確認する。Python fixtureは移植元の製品コードではなく、C#等の製品実装を比較するための実行例・期待結果である。

## 着手基点と現mainの確認

| 用途 | 固定版・確認内容 |
| --- | --- |
| PDF APIの描画基準 | 465088ed0b575f055beb6851e98d28ed22fb9928。既存fill-template／stamp-imageを使用。 |
| PDF比較に使ったsite資産 | e08aed241245f9f84a7336302edcb7d6a89a57b1。比較元は固定し、動くmainへ読み替えない。 |
| 今回読み取り確認したsite main | 03771f95851e79fe6e03a823ca2c1bc8c91dfd34。比較元より6コミット先。generator、invoice／estimateのPDF・definition、ブラウザ計算処理、PdfRelayは差分なし。CI／ブラウザrunner／テスト／検証資料には変更がある。 |
| 最終検証の成果物commit | d6aed05411678cd757cef04cda45a4bb111ad9b0。既存資料はこの固定版から参照可能。 |

比較は[固定版→確認時main](https://github.com/kooiei-in4a/amane-tools-site/compare/e08aed241245f9f84a7336302edcb7d6a89a57b1...03771f95851e79fe6e03a823ca2c1bc8c91dfd34)。実装開始時は担当の作業ブランチで、その時点のmainと再比較する。現mainのCI・検証更新を巻き戻さず、現在の検証runner・テスト一覧を使う。site現mainにAGENTS.mdは見つからなかったが、着手時は再確認する。

## 作業順序・分割

小さな実装Issueを親#133の「4：標準JSON→PDF」に関連付け、**目安は3本のPR**とする。IssueとPRを1対1にせず、各PRがレビュー・検証できる大きさにする。今は起票・PR作成・実装を行わない。下表のW番号は新規Issue番号や正式なクラス名ではない。

| 順序 | 作業単位・PR目安 | 対象・責任 | 依存／次へ渡すもの | 完成条件 |
| --- | --- | --- | --- | --- |
| W0 着手整理 | 実装Issue内の短い整理 | mainとの差分、対象資産、採用JSON・失敗例を固定し、既存CI検証コマンドを確認。公開経路／ID／保存等の未決事項は後続へ分ける。別の設計基盤は作らない。 | 下表の共通基準。認証／会社保存の完成待ちにせずW1〜W4の内部処理は準備できる。 | 変更対象と比較元SHA、対象外、担当PRの終了点が明確。 |
| W1 テンプレート調整 | PR-A | generatorのinvoice／estimate背景・定義、生成PDF・definition・hash、必要な既存テンプレート検証を整合。certificateは対象外。 | W0。W2／W3の構造検証は並行可能だが、実矩形の確定はW1に依存。 | 再生成がbyte一致。採用罫線・合計強調・名称20×2・拡張数量セル・notes3行が成立し、両帳票1／8明細と一般向け既存機能で欠け／衝突なし。 |
| W2 事前検証・入力エラー | PR-Bの前半 | siteサーバー側で厳密なJSON読取・必須／型／値／上限／文字／行予算を検査し、採用first-error形式へ対応。登録／発行を区別する。ブラウザ計算コードは使わない。 | W0・採用JSON。DB／HTTP／PDF呼び出しを含めない入力検証から始める。W1の矩形を用いる最終幅検査はW4で接続。 | 代表正常入力を保持し、135拒否例を規則ごとに再利用・必要分を製品テストへ移す。元path／登録source・修正理由が一致。拒否で描画client呼び出し0。 |
| W3 数量モードの設定解決 | PR-Bの後半 | quantityAlignmentの登録値・発行値を検査して実効modeを選ぶ。保存／UIは追加しない。 | W2のエラー形式。登録情報は参照解決済みsnapshotとして受け取る。 | 省略／decimal／rightの9組合せ、既定decimal、発行＞登録＞既定、不正登録を正しい発行でも拒否。null・空・trim／型変換なし。入力・登録値を変更しない。 |
| W4 表示変換・最終描画前検査 | PR-Cの前半 | 検証済み入力＋解決mode＋固定定義から表示文字列・fields・元path対応を作り、実矩形／全体予算／画像配置を検査する。 | W1の定義＋W2＋W3。ここまで成功して初めて描画へ渡す。 | 8基本形と省略／0円／取引・備考・金額上限・末尾0を既存期待結果に照合。数量モード以外の値・配置は不変。幅超過でも実API0。前後空白は製品PDFで追加確認し、無断trimしない。 |
| W5 描画接続・回帰 | PR-Cの後半または後続PR | W4の出力を既存PDF API clientで文字1回→存在する画像各1回へ接続。最終PDFだけ返し、既存共有資源・キャンセル・後始末に合わせる。 | 内部処理の試験はfixture／テスト用参照で可能。外部の登録→生成は#133の認証・会社登録（順序2／3）、画像を使う場合は所属確認済み画像参照に依存。 | 事前拒否API0、途中失敗で部分PDFなし、画像0／1／2枚で1／2／3呼び出し。既存回帰＋実API代表確認＋ブラウザ4帳票。公開接続の未完成を内部処理の完成と区別する。 |

PR-Aを先に独立してレビューできる。PR-Bはテンプレートとは別の入力処理なので進められるが、PR-Cの実幅・配置の確認は採用済み定義にそろえる。W5を公開する前に認証・利用者スコープ・参照権限・資源制御を接続する。未整備の間に公開経路や認証迂回を追加しない。

## 実行時の順序と呼出境界

1. 登録時・発行時それぞれの入口で生JSONを上限内で読み、構文・重複／未知key・必須・型・字句・行／文字予算を確認する。金額の指数・小数・符号（-0含む）を型変換で失う前に判定する。単なるDTO deserializeやint化で仕様を満たしたことにしない。
2. 後続の保存／認証側が利用者内の会社・テンプレート・画像参照を解決し、その生成で使うsnapshotを渡す。参照情報を3回の処理中に読み替えない。ID命名や永続版管理の新設は別判断。
3. 登録情報と発行値の採用条件を検査し、quantityAlignmentは両方の指定を検証後に解決する。正しい発行値で不正登録を隠さない。
4. 文字列へ表示書式だけを付け、notesの取引情報→自由備考のLF結合、任意値／ラベル省略、数量配置を決める。固定フォントの対応文字・送り幅・行高・実矩形、200項目／1ページ100行要素／fields64KiB等の既存API予算、画像予約領域も検査する。
5. 成功した結果だけを描画clientへ送る。文字→ロゴ→印影、画像省略時は呼び出しも省略。PDF APIの400／422をsite入力エラーと同一視せず、元pathが分からない後段失敗を推測で別項目へ付け替えない。

上記は処理の依存順を示す。first-errorで複数違反の優先順を公開契約にはしない。入力構造と登録参照の検査を適切な境界に置き、部分的に描画してから別入力の不正を発見する経路を作らない。既存3回呼び出しは内部方式であり、最終的な外部API1回という親Issueの目的と両立する。

## ファイル・コードの入口

既存ファイルは確認時mainへの固定リンク。新設する製品ファイルの配置／名前は着手Issueで既存C#構成に合わせ、汎用マッピング基盤・新ライブラリ・新リポジトリを導入しない。

| 入口 | 使い方・注意 |
| --- | --- |
| [make-pdf-templates.mjs](https://github.com/kooiei-in4a/amane-tools-site/blob/03771f95851e79fe6e03a823ca2c1bc8c91dfd34/scripts/make-pdf-templates.mjs)とwwwroot/pdf-templates | 正式帳票の背景と定義の生成元。採用座標は既存[変更表](HANDOFF-site-134.md#テンプレートの変更対象)。PDFやdefinitionだけを手編集してgeneratorと乖離させない。 |
| [pdf-document-templates-rules.js](https://github.com/kooiei-in4a/amane-tools-site/blob/03771f95851e79fe6e03a823ca2c1bc8c91dfd34/src/Amane.Tools.Site/wwwroot/pdf-document-templates-rules.js) | 一般向けブラウザ用の整数数量・金額／税額計算。B2Bでは参照用にとどめ、calculate／values／buildを呼んで確定金額を上書きしない。既存一般向け計算は回帰対象。 |
| [PdfRelay.cs](https://github.com/kooiei-in4a/amane-tools-site/blob/03771f95851e79fe6e03a823ca2c1bc8c91dfd34/src/Amane.Tools.Site/Pdf/PdfRelay.cs)・[PdfTools.cs](https://github.com/kooiei-in4a/amane-tools-site/blob/03771f95851e79fe6e03a823ca2c1bc8c91dfd34/src/Amane.Tools.Site/Pdf/PdfTools.cs) | 現行はHttpContext・antiforgery・一般向け利用制限を伴うPDF中継。新JSONの事前検証・会社登録やB2B認証を備える経路ではない。既存HTTP経路を変更して新JSONを押し込まず、HttpClient設定・キャンセル・資源処理の責務を確認して再利用範囲を決める。内部合成をブラウザ向け中継3回として一般向け利用回数を3回消費する構成には先回りして決めない。R8の10／20秒は検証受入値で、現行PdfApi:TimeoutSeconds（60〜3600秒）の変更指示ではない。新経路のdeadlineは後続設計で整合させる。 |
| [PdfFillTemplateQuery.cs](https://github.com/kooiei-in4a/amane-tools-site/blob/03771f95851e79fe6e03a823ca2c1bc8c91dfd34/src/Amane.Tools.Site/Pdf/PdfFillTemplateQuery.cs) | 現在はquery無しを検査するだけ。標準JSONのvalidatorとして扱わない。 |
| [FillTemplateLayoutContractTests.cs](https://github.com/kooiei-in4a/amane-tools-site/blob/03771f95851e79fe6e03a823ca2c1bc8c91dfd34/tests/Amane.Tools.Site.Tests/FillTemplateLayoutContractTests.cs)・[固定glyph/fieldsケース](https://github.com/kooiei-in4a/amane-tools-site/blob/03771f95851e79fe6e03a823ca2c1bc8c91dfd34/scripts/tests/fill-template-layout-cases.json) | 実幅・高さ・座標丸めの既存契約を確認する。ブラウザの警告とサーバーの拒否を混同しない。APIと同じフォント・丸め／余白を使う。 |
| [最終検証スクリプト](validate-standard-json-v03-final.py) | 旧v0.3等をimportしているfixture用Python。製品へ丸ごとコピーせず、[入力・expected-from-input・fields・quantity-placement・error.json](standard-json-v03-final-validation/README.md)を期待値として移す。 |

## 採用済み条件の移し漏れを防ぐ確認

- W1はquantityの論理セルを拡張してalign=rightを維持する。これが一般向けの既存配置。B2Bの既定decimalはW3→W4で実効modeを使ってleft配置とx／幅を調整する。definitionへ新align値を入れず、単にセル幅やmaxLengthを変えただけで小数点揃え済みと扱わない。
- 名称と会社住所は各20文字×最大2行。部署／担当者／敬称は宛先2行内で、旧name・宛先住所・独立部署keyは拒否。quantityは整数5桁／小数3桁の正の文字列、0／負数拒否・末尾0保持。
- 共有notesは合成後3行／48 UTF-16 unit（LF込み）。date時は自由2行、period時1行、省略時3行かつ合計48。invoiceのtransaction必須／estimate任意、両方省略時ラベルなし。会社銀行情報は別枠で見積には適用しない。
- JPY・税抜・8／10%、単価0〜99,999,999、その他0〜999,999,999,999。両taxSummaryは必須、0円と両税率ラベルを表示。計算・金額間不一致拒否・0補完なし。登録／発行JSONは各64KiB、UTF-16上限とUTF-8 bytesを混同しない。
- 入力失敗は400／413／422＋errors1件、JSON Pointerと必要なsource・修正理由。無断trim・正規化・置換・折返し・縮小なし。登録不正のpathを発行payloadに存在しない/nameLinesへ直接付けず、/companyIdや/templateIdと登録sourceを示す。

## 各PRの検証と全体の完成条件

| PR | 必要な検証の入口・終了点 |
| --- | --- |
| A | node scripts/make-pdf-templates.mjs --check、[既存テンプレートテスト](https://github.com/kooiei-in4a/amane-tools-site/blob/03771f95851e79fe6e03a823ca2c1bc8c91dfd34/scripts/tests/test-document-templates.mjs)、資産SHA・再生成。一般向けinvoice／estimate／certificateの既存入力と計算・出力を点検し、certificate資産が不変。型がmultilineになる既存UIの入力・プレビューも確認。背景罫線の整理に合わせink計測を調整する必要があれば、欠け検査を維持した最小差分とする。 |
| B | C#側の厳密JSONとfirst-error、規則を代表する正常／拒否ケース、設定9組合せ、不正登録＋正しい発行、入力不変。DBや実APIなしで確認できる部分はそこで確認。135件のPython試験を別基盤として二重構築せず、重複を避けて境界・失敗理由を移す。 |
| C | 表示値・元path・fields・mode別座標、最終実幅と画像予約領域の検査、入力拒否でclient0、画像数で1／2／3呼び出し、途中失敗／キャンセルの後始末。既存APIの固定版で両帳票1／8明細×両モードと少数の0／省略／境界を比較。 |

全PRでsite現行mainの必須チェックを使用する。C#変更時のbuild／test、影響するnpm test:unitと既存ブラウザmock／real検証は現在のREADME・package.json・runnerに従い、CI設定や必須テスト一覧を今回の都合で弱めない。[ブラウザrunner](https://github.com/kooiei-in4a/amane-tools-site/blob/03771f95851e79fe6e03a823ca2c1bc8c91dfd34/scripts/verify-pdf-document-templates-browser.mjs)は現mainで更新されているので、固定検証版の古いrunnerをコピーしない。

製品反映の最終受入は以下を確認できること。新しい長時間負荷試験や常設検証基盤は追加しない。

1. 採用JSONから両帳票1／8明細・数量両モードを1ページ生成し、全入力由来文字・確定金額・末尾0を保持、qpdf成功・欠け／非意図的な衝突なし。decimal誤差0.1pt以内。
2. 取引／備考境界・ゼロ金額・両税率・任意ラベルと画像省略が採用仕様どおり。不正登録モードを正しい発行でも拒否し、代表入力エラーは実PDF API0で採用形式へ対応する。
3. generator・PDF・definition・hashとサーバーの固定フォント／座標が整合し、既存一般向けの入力／計算／API契約を維持。ブラウザで代表4帳票を確認する。
4. 既存条件では単独10秒／同時各20秒／2MiB内と後始末。site経由は受付・参照解決・待ち・内部API・送信を含む別計測として条件を明記し、ローカルAPI単体の約0.6秒をそのまま保証値にしない。
5. 外部から登録→生成する機能の完成は、親#133の認証・登録・所属確認と必要なHTTP契約を接続後に判断。純粋処理やtemplateだけのPRで公開機能完成としない。

## 残る未確認事項と作業の境界

| 項目 | 扱うタイミング |
| --- | --- |
| 前後空白の最終PDF | 現fixtureは表示用fieldsまで。W4／W5でPDF文字抽出と位置の保持を追加確認し、抽出器の空白表現も分けて記録する。無断trimで通さない。 |
| 正式ID・版識別・公開経路・登録更新・保存／認証／参照権限 | 親#133の順序2／3／4／5の実装Issueで整合。採用JSONのデータ規則を未確定に戻すものではない。 |
| 生JSON64KiBの実HTTP・取消／失敗時応答 | W5と公開接続時。Content-Lengthだけでなく実読込の上限、-0等の字句、部分PDFなし、認証・混雑／timeoutのHTTP整理を確認。 |
| ブラウザ・実印刷・site経由性能／他機能競合 | ブラウザ代表4帳票は製品反映時の必要確認。Acrobat・実印刷は未確認として残し、#134へ新たな必須工程を追加しない。site経由は条件付きの少数測定を行う。 |
| API追加・可変明細・自動改ページ・一括合成 | 今回必要なAPI変更はなし。具体的不足が出た場合のみ重複Issueを確認して起票。先行実装しない。 |

この引き継ぎは実装順序の整理であり、今から製品実装を始める指示ではない。site CI改善・共有main・公開／マージ／リリースには手を加えていない。後続Issueの起票／作業開始は別の指示に従う。
