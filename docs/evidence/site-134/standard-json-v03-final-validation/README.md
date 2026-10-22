# #134 標準JSON v0.3・採用済みR1〜R8の最終検証

**検証用スクリプトのみ。正常24件＋同時2件成功、入力拒否135件はAPI呼び出し0。製品実装・既存PDF API契約・CI・共有mainは変更なし。両Issueはopenのまま。** 仕様根拠は[標準JSONv0.3](../standard-json-draft-v0.3/README.md)・[R1〜R8採用記録](../INITIAL-SPEC-ADOPTED.md)。最終検証スクリプトは[validate-standard-json-v03-final.py](../validate-standard-json-v03-final.py)。旧v0.3／v0.2のスクリプト・入力・実測・PDFは変更していない。

## 結果とF1〜F6

| 対象 | 結果・根拠 |
| --- | --- |
| F1 組合せ | 両帳票1／8明細×decimal／rightの8件で、会社・住所・宛先を同時20文字×2行。品名17文字＋軽減記号、単価8桁・他金額12桁も同時に使用。1明細はdate＋備考16×2＝48文字、8明細は年越しperiod＋16文字＝43文字。各1ページ・全欄抽出一致、数量／隣接欄・名称／他欄・画像予約領域の非交差。 |
| F1 省略・同日期間 | 見積1明細は取引・備考ともなし、見積8明細は取引なし＋16＋16＋14＋LF2＝48文字を両モードで確認。両方省略時はnotesとラベルなし。両帳票8明細rolesで部署・担当者・敬称と同日period＋1備考を保持。 |
| 数量・設定 | 整数5桁／小数3桁、99999.999・99999・0.001・2.50・1.000・123.45・0.010を保持。発行＞登録＞既定decimalの9組合せを今回も網羅。decimalの基準位置最大差0.0475pt、rightの右端差0pt。比較4組は全欄抽出と数量以外のword座標が一致。 |
| F2 ゼロ・省略 | 両帳票8明細は正数量0.001、単価・明細・対象額・税額・合計を全0として0円を表示。未使用8%区分も呼出元が0を明示し、両税率ラベル保持。任意の件名・期限・登録番号・連絡先・振込先を省いた両帳票1明細は値／ラベルなし。画像なしは文字1回、ロゴのみ／印影のみは各2回。 |
| F3 数値境界・金額保持 | 単価99,999,999、他金額999,999,999,999を描画・抽出し、0円も空欄にしない。意図的不整合（数量1.25、単価100に明細98,765円・対象12,345円・税777円・合計99,999円）をright／既定decimalの両方で保持。乗算・税計算・合算・整合性拒否なし。 |
| F4 入力拒否 | 旧66件を再利用し69件追加、計135件を同じprocess入口でAPI前に拒否。実client全method/pathとpost_countを照合し、各増分0・PDFなし。400／413／422、JSON Pointer、first-error、修正理由message、登録sourceを照合。不正登録モードを正しい発行値でも拒否。 |
| F5 品質・実測 | 正常24＋同時2のすべてqpdf成功・1ページ・矩形別抽出一致、wordの矩形外0。透過／半透過の画素・縦横比・配置・重ね順を確認し、画像予約領域とwordの交差0。単独／同時／容量とも採用基準内。終了後job・qpdf/pdfcpu子process残存なし、health200。 |
| F6 統合報告 | 全入力・PDF・比較・エラー・fields・設定解決・実測と再現資料を今回共有。#134へ報告し、親#133への要約反映は残る。Issueの自動完了は行わない。 |

既存helperの差分は検証用module内だけ：数量0はout-of-range、金額-0はトークンを失わずinvalid-integer、エラーにmessageを付加。0円が単価・金額の両列にある場合に検査側が取り違えないよう、隣接wordをyだけでなくx矩形でも識別する。過去のスクリプトファイルや製品へ変更を加えず、旧処理・画像検査・数量配置を再利用した。新基盤・依存追加なし。

## 時間・サイズと実行条件

| 単独・画像数 | 件数 | JSON→最終PDF秒 | 最終bytes |
| --- | ---: | ---: | ---: |
| 画像なし・文字1回 | 2 | 0.207〜0.214 | 208,365〜209,192 |
| ロゴまたは印影のみ・計2回 | 2 | 0.385〜0.393 | 216,706〜217,797 |
| ロゴ＋印影・計3回 | 20 | 0.568〜0.710 | 213,846〜224,521 |

同時2チェーンはbarrierで開始をそろえ、実clientの同時POST最大2・計6 POSTを観測：
- invoice-8-boundary-decimal：0.989秒／217,849 bytes、[PDF](concurrent-invoice-8-boundary-decimal/final.pdf)／[入力](concurrent-invoice-8-boundary-decimal/request.json)。
- estimate-8-boundary-right：0.989秒／215,732 bytes、[PDF](concurrent-estimate-8-boundary-right/final.pdf)／[入力](concurrent-estimate-8-boundary-right/request.json)。

合計72 POST（単独66＋同時6）。単独10秒／同時各20秒／最終2MiBの採用基準を満たした。JSON解析・事前検証・表示変換開始〜最終PDF受信を計測し、起動・保存・PDF抽出／画像検査は除外。各1回、同時は1組のみ。速度差や本番SLA・最大画像・site経由・他機能競合の保証ではない。

- 固定API SHA `465088ed0b575f055beb6851e98d28ed22fb9928`、site資産SHA `e08aed241245f9f84a7336302edcb7d6a89a57b1`。採用資料commit `0e76a4f19ee732221d581901310bbbaddf6f2d1f`。
- 既存Docker `amane-pdf-api:site134-465088e`、image ID `sha256:67d204ed4e10d643a41ffb248a621ed0a2376660f26f5051d0952c65cc10d0e3`。
- CPU1／RAM1536MiB（追加swapなし）／tmpfs256MiB／read-only／network none／既存共有2枠・待ち0。docker execのloopback HTTP。ホストLinux7.0.0-38-generic、Python3.14.4、Pillow12.1.1、numpy2.3.5、Poppler26.01.0、qpdf12.3.2。固定APIの.NET10.0.12／pdfcpu0.16.2／BIZ UDP Gothic1.051を使用。

## PDF・入力・全実測

比較は1ページ目decimal、2ページ目right。元の最終PDFはすべて1ページ：
[請求書1明細](invoice-1-comparison.pdf)／[8明細](invoice-8-comparison.pdf)、[見積書1明細](estimate-1-comparison.pdf)／[8明細](estimate-8-comparison.pdf)。

| ケース | 実効mode / source | 取引・備考 行 / 文字 | POST | 秒 | bytes | 資料 |
| --- | --- | --- | ---: | ---: | ---: | --- |
| invoice-1-boundary-decimal | decimal / request | 3 / 48 | 3 | 0.710 | 216,986 | [PDF](invoice-1-boundary-decimal/final.pdf) / [入力](invoice-1-boundary-decimal/request.json) |
| invoice-1-boundary-right | right / request | 3 / 48 | 3 | 0.601 | 216,986 | [PDF](invoice-1-boundary-right/final.pdf) / [入力](invoice-1-boundary-right/request.json) |
| invoice-8-boundary-decimal | decimal / templateSettings | 3 / 43 | 3 | 0.641 | 217,849 | [PDF](invoice-8-boundary-decimal/final.pdf) / [入力](invoice-8-boundary-decimal/request.json) |
| invoice-8-boundary-right | right / templateSettings | 3 / 43 | 3 | 0.592 | 217,854 | [PDF](invoice-8-boundary-right/final.pdf) / [入力](invoice-8-boundary-right/request.json) |
| estimate-1-boundary-decimal | decimal / request | 3 / 48 | 3 | 0.640 | 214,884 | [PDF](estimate-1-boundary-decimal/final.pdf) / [入力](estimate-1-boundary-decimal/request.json) |
| estimate-1-boundary-right | right / request | 3 / 48 | 3 | 0.614 | 214,884 | [PDF](estimate-1-boundary-right/final.pdf) / [入力](estimate-1-boundary-right/request.json) |
| estimate-8-boundary-decimal | decimal / request | 3 / 43 | 3 | 0.584 | 215,728 | [PDF](estimate-8-boundary-decimal/final.pdf) / [入力](estimate-8-boundary-decimal/request.json) |
| estimate-8-boundary-right | right / request | 3 / 43 | 3 | 0.578 | 215,732 | [PDF](estimate-8-boundary-right/final.pdf) / [入力](estimate-8-boundary-right/request.json) |
| default-decimal | decimal / default | 2 / 26 | 3 | 0.652 | 222,043 | [PDF](default-decimal/final.pdf) / [入力](default-decimal/request.json) |
| provided-amounts-unchanged | right / templateSettings | 2 / 26 | 3 | 0.646 | 222,042 | [PDF](provided-amounts-unchanged/final.pdf) / [入力](provided-amounts-unchanged/request.json) |
| invoice-8-roles | right / templateSettings | 3 / 43 | 3 | 0.644 | 224,521 | [PDF](invoice-8-roles/final.pdf) / [入力](invoice-8-roles/request.json) |
| estimate-8-roles | decimal / templateSettings | 3 / 43 | 3 | 0.574 | 222,827 | [PDF](estimate-8-roles/final.pdf) / [入力](estimate-8-roles/request.json) |
| estimate-1-omitted-decimal | decimal / request | 0 / 0 | 3 | 0.616 | 213,846 | [PDF](estimate-1-omitted-decimal/final.pdf) / [入力](estimate-1-omitted-decimal/request.json) |
| estimate-1-omitted-right | right / request | 0 / 0 | 3 | 0.568 | 213,846 | [PDF](estimate-1-omitted-right/final.pdf) / [入力](estimate-1-omitted-right/request.json) |
| estimate-8-omitted-decimal | decimal / request | 3 / 48 | 3 | 0.589 | 215,381 | [PDF](estimate-8-omitted-decimal/final.pdf) / [入力](estimate-8-omitted-decimal/request.json) |
| estimate-8-omitted-right | right / request | 3 / 48 | 3 | 0.653 | 215,393 | [PDF](estimate-8-omitted-right/final.pdf) / [入力](estimate-8-omitted-right/request.json) |
| invoice-zero | right / templateSettings | 3 / 36 | 3 | 0.587 | 221,645 | [PDF](invoice-zero/final.pdf) / [入力](invoice-zero/request.json) |
| invoice-minimal-no-images | decimal / request | 1 / 14 | 1 | 0.207 | 209,192 | [PDF](invoice-minimal-no-images/final.pdf) / [入力](invoice-minimal-no-images/request.json) |
| estimate-zero | decimal / templateSettings | 3 / 36 | 3 | 0.571 | 220,112 | [PDF](estimate-zero/final.pdf) / [入力](estimate-zero/request.json) |
| estimate-minimal-no-images | right / request | 0 / 0 | 1 | 0.214 | 208,365 | [PDF](estimate-minimal-no-images/final.pdf) / [入力](estimate-minimal-no-images/request.json) |
| invoice-single-image | decimal / request | 2 / 26 | 2 | 0.385 | 217,797 | [PDF](invoice-single-image/final.pdf) / [入力](invoice-single-image/request.json) |
| estimate-single-image | right / request | 2 / 26 | 2 | 0.393 | 216,706 | [PDF](estimate-single-image/final.pdf) / [入力](estimate-single-image/request.json) |
| ordinary-percent | decimal / request | 2 / 26 | 3 | 0.647 | 221,716 | [PDF](ordinary-percent/final.pdf) / [入力](ordinary-percent/request.json) |
| body-exact-65536 | decimal / request | 2 / 26 | 3 | 0.571 | 221,298 | [PDF](body-exact-65536/final.pdf) / [入力](body-exact-65536/request.json) |

各ケースにはcompany.json・settings.json・request.json、display-values・fields・field-index・field-sources・definition・effective・quantity-placement、独立に作ったexpected-from-input、final.pdf／txt／extracted.json／bbox.html、measure.jsonを併置。代表6ケースはPNGプレビューも共有。[実測JSON](results.json)と[入力拒否全135件](ERROR-CASES.md)を参照。

拒否ケースは請求書transaction欠落・null／空object／date+period・実在しない日付／期間片側・逆転、date＋3備考／period＋2備考／LF込み50文字、必須不足・9／0明細、税率0等／外貨・両税率内訳不足、金額小数／指数／-0／負数／文字列／bool／上限超過、数量0／0.000／負数／表記違反・桁数、名称21文字／3行／旧key／宛先住所、未知／重複key、不正登録＋正しい発行、未対応文字・NFD濁点／半濁点・禁止文字列・行内改行・空白等、実幅超過、本文64KiB超過を含む。65,536 bytesの正しい発行JSONは空白パディング付きで実PDF生成成功、65,537 bytesは拒否。会社・登録設定本文の超過も拒否。

## 再現

[既存環境準備](../README.md#取得・再現)に従い固定ソースをapi-sourceへ展開し、既存Docker image・qpdf／Poppler／Python依存を用意する。成果物基点で未存在の出力先を指定：

~~~bash
python3 validate-standard-json-v03-final.py --output /tmp/amane-site134-v03-final-rerun
~~~

スクリプトは旧v0.3／v0.2・数量resolver／配置・reproduce.pyを読み込み、今回の完全な入力から表示値を作る。過去のdisplay-valuesを正解として流用しない。シナリオ名でmodeを選ばずresolverの実効値を使用。入力オブジェクト・定義・原本JSONは不変。スクリプト・背景のSHAはresults.json、共有全ハッシュは[SHA256SUMS](../SHA256SUMS)。初回の検証側word識別修正後、上記24＋2＋135件を同じ最終スクリプトで再実行した結果を共有。途中失敗の出力は共有版に含めない。

## 未確認・後続とA／B／C

- 前後空白は事前検証→fieldsで保持することを確認したが、この追加ケースではPDF抽出まで照合していない（results.jsonのpreflight_only）。通常の%は別のPDFケースで確認済み。この範囲を全欄抽出成功26件へ含めない。
- 今回はfixture内の入力形式・参照ID。製品の保存／登録更新・認証／利用者スコープ・実参照解決／版固定・正式ID／公開経路・運用HTTP契約・実site HTTP・64KiB入口制御は未実装／未確認。site経由時間・資源競合、既存ブラウザプレビュー・Acrobat／実印刷も未確認。
- 製品反映時はgenerator・PDF・definition・hashをそろえ、既存一般向け機能への回帰とブラウザ代表4帳票の確認を行う。比較元の固定site資産から現mainへの差分確認も必要。
- A：既存fill-template＋任意のstamp-imageで代表ケースが成立。B：siteのテンプレート整合・事前検証・表示変換・設定解決を製品へ反映する作業。C：今回必要なPDF API変更は確認なし、新規API Issueなし。一括合成・自動改ページ・可変明細は先行実装しない。

#134の技術検証F1〜F5は今回の代表ケースで確認した。親への要約と後続の引き継ぎを残し、両Issueはopen。製品実装は開始していない。
