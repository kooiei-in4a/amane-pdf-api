# セキュリティ方針

## 前提

このAPIは、利用者がアップロードしたPDFを処理します。

PDFは外部から渡される信用できない入力です。正常なPDFだけでなく、壊れたPDFや意図的に細工されたPDFが送られることを前提にします。

## 分離

`amane-pdf-api` は `amane-tools-site` と別コンテナで動かします。

このAPIには、原則として次を持たせません。

- 利用者データベース
- 認証情報の保存
- SMTP認証情報
- 決済情報
- 他サービスのAPIキー

PDF処理部分に問題が発生した場合でも、Webサイト本体への影響を小さくするためです。

## ファイルとパスワード

実装済みの対策:

- PDFを永続保存しない
- パスワードを永続保存しない
- PDF本文やパスワードをログへ出さない
- 一時ファイルを処理終了後に削除する
- 一時領域は可能であればRAM上の一時ファイルシステムを使用する
- qpdf公式QPDFJob JSONファイルでpasswordを渡し、argvにはランダムな内部JSON pathだけを載せる
- owner passwordはuser passwordと独立した暗号学的乱数32 bytesから生成する
- 処理専用ディレクトリはランダム名、Linuxでは0700、入力PDF/JSONは0600とする
- qpdfのstdout/stderrはログ・例外・HTTPレスポンスへ出さず破棄する
- qpdfのexit code 0を確認してから出力PDFをストリーム送信する
- クライアントキャンセル時はqpdfのprocess treeを終了し、終了待ちしてから一時領域を削除する

JSONの形式とCLIの対応は[qpdf公式QPDFJob文書](https://qpdf.readthedocs.io/en/stable/qpdf-job.html)に従います。

## 入力検証とエラー

- Content-Typeや利用者ファイル名を信用せず、qpdfでPDF構造を検証する
- 空ファイル、非PDF、破損PDF、qpdf warningを422で拒否する
- 既暗号化PDFは`--is-encrypted`で判定し、空のuser passwordや一致するpasswordでも拒否する
- 必須項目不足/重複、multipart形式不正、制御文字やUTF-8で127 bytesを超えるpasswordは400で拒否する
- qpdf実行失敗や未知のexit codeは500とし、HTTP response/logには内部例外を含めない
- エラーは固定文言のProblem Detailsへ統一し、password、PDF本文、内部path、stack trace、stderrを含めない

qpdfの検証はPDF構造の検証であり、PDF内のJavaScriptや添付ファイルの無害化はこのAPIの機能に含みません。

## リソース制限

実装済みの初期値:

- PDF 50 MiB、リクエスト総量はその上限 + multipart用64 KiB
- qpdfの検査・暗号化全体で30秒
- 同時PDF処理数2、待ち行列0

`Pdf` configurationにまとめ、正でない制限値などは起動時に拒否します。
Kestrelのbody上限とContent-Lengthチェックに加え、実際に読んだbytesを制限します。Content-Lengthなしでも上限超過のPDFを全量保存しません。
MultipartReaderで直接privateファイルへ書き込み、passwordのbufferも127 bytesへ制限します。

Concurrency Limiterはアップロード開始前からレスポンス送信/削除まで処理枠を保持し、超過を503で拒否します。利用者/IPでpartitionせず、healthは制限しません。
timeoutは504で返し、qpdfの検査・暗号化の各段階でprocess treeの終了と終了待ちを行ってから一時領域を削除します。クライアントキャンセルやアプリ停止でもqpdfを終了します。
サイズ超過は413、形式不正は400です。内部情報を含まないProblem Detailsで返します。

利用者/IP単位の頻度制限は入口側、CPU/メモリ/tmpfs容量の上限は実行環境側で設定します。

## ネットワーク

PDF処理コンテナから外部インターネットへ通信する必要は基本的にありません。

本番環境では、運用上必要な通信を確認した上で、不要な外向き通信を許可しない構成を目標とします。

## qpdf

PDFの暗号化処理にはqpdfを使用します。

qpdfはApache License 2.0で公開されているOSSです。

qpdfの更新状況を定期的に確認し、既知の脆弱性や重要な修正がある場合は更新します。

## 後続Issueで実装する対策

DockerのE2E CIは#5で追加します。
利用者/IP単位の頻度制限、コンテナのCPU/メモリ/ネットワーク設定は入口・実行環境の責務です。
