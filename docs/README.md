# 文書索引

現行のAPI契約と利用例はRepository rootの[README.md](../README.md)を参照してください。セキュリティ上の境界は[security.md](security.md)、依存ソフトウェアのライセンスはrootの[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)にまとめています。

## 設計・方針

| 文書 | 内容 |
| --- | --- |
| [roadmap.md](roadmap.md) | PDF機能の方針と判断の経緯。優先度表は2026-10-07時点の計画記録で、現在の未完了項目を表しません。 |
| [pdfcpu-design.md](pdfcpu-design.md) | pdfcpu導入と共通描画・合成基盤の設計記録。 |
| [fill-template-design.md](fill-template-design.md) | PDFテンプレート差し込みの確定基本設計（2026-10-19 Human承認）。 |
| [stamp-image-design.md](stamp-image-design.md) | 透過PNG押印APIの設計と採用判断。現行の検証結果は対応するvalidation記録を参照してください。 |
| [redact-design.md](redact-design.md) | 画像化方式の黒塗りAPIの設計、安全境界、判断の経緯。 |

## 実装・検証記録

以下は特定の日付、commit、tool version、fixture、実行環境で得た結果です。任意のPDFや異なる環境での成功保証ではありません。古い記録に書かれた「未実装」「未確認」「次のPRで実施」は、その記録時点の状態です。最新の挙動と制約はREADMEおよびsecurity.mdを優先してください。

| 文書 | 対象 |
| --- | --- |
| [test-value-audit-2026-10-21/README.md](test-value-audit-2026-10-21/README.md) | commit 465088e時点の全452テストメソッド・Python検証・補助資材の価値監査。13改善候補、重複と本番経路の照合、限定変異実験の台帳。13項目すべて改善・検証済み。 |
| [clean-validation.md](clean-validation.md) | PDF情報除去APIの構造・容量検証 |
| [compress-validation.md](compress-validation.md) | JPEG画像圧縮の画質・構造・容量検証 |
| [fill-template-validation.md](fill-template-validation.md) | PDFテンプレート差し込みAPIのHTTP・配置・抽出・実署名・容量検証、および固定フォント文字表 |
| [from-images-validation.md](from-images-validation.md) | 画像からPDFを作るAPIの初期検証と運用への引き継ぎ |
| [from-images-followup-validation.md](from-images-followup-validation.md) | PNG見積りと画像処理の追加容量検証 |
| [from-images-as-validation.md](from-images-as-validation.md) | PNG取込時の仮想アドレス空間と同時処理の判断・検証 |
| [fsize-core-validation.md](fsize-core-validation.md) | LinuxのFSIZE上限とcore dumpに関するAPI側検証・未確認事項 |
| [overlay-validation.md](overlay-validation.md) | pdfcpu共通描画基盤のHTTP・描画・容量検証 |
| [pdfcpu-validation.md](pdfcpu-validation.md) | pdfcpu導入時の起動・フォント・CI検証（PR A）と、0.16.2更新時の全機能・ToUnicode比較。 |
| [qpdf-memory-validation.md](qpdf-memory-validation.md) | qpdfのメモリ制限と負荷測定 |
| [renderer-validation.md](renderer-validation.md) | PDFium別プロセス描画基盤（PR A）の検証。黒塗りAPIの後続検証はredact-validation.mdを参照 |
| [redact-validation.md](redact-validation.md) | 黒塗りAPIのHTTP契約、安全性、描画・容量検証 |
| [split-validation.md](split-validation.md) | PDF分割とZIP返却の容量・時間・core対策検証 |
| [stamp-image-validation.md](stamp-image-validation.md) | 透過PNG押印APIの自動・描画・容量検証 |
| [watermark-validation.md](watermark-validation.md) | 透かしAPIの描画、文字、容量検証 |

## 測定データと再現用資材

`docs/`内のJSON・JSONLは、本文で参照される容量測定値、試行結果、事前計画です。`redact-validation/`、`renderer-validation/`、`redact-design-probe/`、`stamp-image-design-probe/`には、対応する文書で説明する画像、fixture生成・確認スクリプト、測定結果があります。これらのデータは対象文書の条件・説明と合わせて読んでください。

## この索引の更新

新しい設計書・検証記録を追加したときは、対象機能、記録の時点、現行仕様を示す参照先をここに追記してください。実装後も価値のある数値や判断履歴は消さず、当時の記録であることと後続資料への導線を更新します。
