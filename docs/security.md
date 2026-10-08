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
- protectのowner passwordはuser passwordと独立した暗号学的乱数32 bytesから生成する
- 処理専用ディレクトリはランダム名、Linuxでは0700、入力PDF/JSONは0600とする
- qpdfのstdout/stderrはログ・例外・HTTPレスポンスへ出さず破棄する。unlockの認証後の`--check`はpasswordをstdoutに含む場合があるため、stdoutも保持しない
- lossless最適化では固定したobject stream生成・Flate再圧縮optionだけをqpdfの`ArgumentList`へ渡し、画像最適化やdownsamplingは行わない
- 回転角度とページ指定は厳密に解析し、生のqueryではなく再構築した値をqpdfの`ArgumentList`へ渡す。ページ削除の補集合も連続rangeへまとめて再構築する
- 結合ではランダムjob directory内のAPI生成path（`input-0001.pdf`等）だけをqpdfの`ArgumentList`へ渡し、利用者ファイル名を使用しない
- ページ数取得のstdoutは64 bytesを上限として保持し、正のASCII整数だけを受け付ける
- qpdfのexit code 0を確認してから出力PDFをストリーム送信する
- クライアントキャンセル時はqpdfのprocess treeを終了し、終了待ちしてから一時領域を削除する

JSONの形式とCLIの対応は[qpdf公式QPDFJob文書](https://qpdf.readthedocs.io/en/stable/qpdf-job.html)に従います。

## 入力検証とエラー

- Content-Typeや利用者ファイル名を信用せず、qpdfでPDF構造を検証する
- 空ファイル、非PDF、破損PDF、qpdf warning、入力検査での処理上限超過を422で拒否する
- unlock以外の既暗号化PDFは`--is-encrypted`で判定し、空のuser passwordや一致するpasswordでも拒否する
- unlockはpasswordなしの`--requires-password`で対象を判定し、開くpasswordが不要なPDFは正しいowner passwordを渡しても拒否する
- unlockはprivate JSONで認証と構造検査を行い、解除後の非空・未暗号化・構造検査成功を確認してから送信する。出力検証の失敗は422ではなく500とする
- unlockの422だけに固定の`not-encrypted` / `no-open-password` / `wrong-password` / `invalid-pdf`をreasonとして付け、unlock専用のtitleとreasonを維持する。共通422はreasonなしとし、破損・パスワード設定・大きすぎる画像の確認を促す固定titleを返す
- 必須項目不足/重複、multipart形式不正、制御文字やUTF-8で127 bytesを超えるpassword、不正なangle/pagesは400で拒否する
- 結合は2〜10個の同名`file` fieldだけを受け付け、全入力を逐次検証する。不正入力が1件でもあれば結合全体を拒否する
- qpdf実行失敗や未知のexit codeは500とし、HTTP response/logには内部例外を含めない
- エラーは固定文言のProblem Detailsへ統一し、password、PDF本文、内部path、stack trace、stderrを含めない

qpdfの`--check`はPDFの完全な適合性や無害性を保証せず、PDF内のJavaScriptや添付ファイルの無害化はこのAPIの機能に含みません。

## リソース制限

実装済みの初期値:

- PDF 50 MiB、リクエスト総量はその上限 + multipart用64 KiB
- 結合は最大10ファイル、入力合計50 MiB、リクエスト総量は入力合計上限 + 64 KiB。各入力にも既存の単一PDF上限を適用する
- qpdfの検査と各PDF処理、または検査・ページ数取得・ページ操作の全体で30秒
- 結合では全入力検証と最終結合を1つの30秒予算で制御し、qpdfをrequest内で並列起動しない
- unlockはJSON作成・対象判定・認証・入力検査・解除・出力検証で1つの30秒予算を共有し、qpdfを逐次実行する
- 同時PDF処理数2、待ち行列0
- Linuxではすべてのqpdf実行にprlimitのRLIMIT_AS 544 MiB（570425344 bytes）を適用。shellを挟まず直接起動する
- 全OSのqpdfにJPEGMEM=600M（600,000,000 bytes）を既存RunnerのEnvironmentで渡す。非LinuxにはAS制限を適用しない
- Linuxの起動時は同じASで/bin/trueとqpdf --versionを確認し、失敗時は固定ログだけで非0終了する

`Pdf` configurationにまとめ、正でない制限値などは起動時に拒否します。
Kestrelのbody上限とContent-Lengthチェックに加え、実際に読んだbytesを制限します。Content-Lengthなしでも上限超過のPDFを全量保存しません。
MultipartReaderで直接privateファイルへ書き込み、passwordのbufferも127 bytesへ制限します。
結合でも複数PDFを全量bufferせず、単一・合計の残りbytesを共有のbounded copyへ渡します。ファイル数上限の次のpartは、新しいファイルを作成・書き込みする前に拒否します。

Concurrency Limiterは暗号化、解除、最適化、結合、回転、抽出、削除、並べ替えで同じ処理枠を共有し、アップロード開始前からレスポンス送信/削除まで保持して、超過を503で拒否します。結合1 requestは入力数によらず1枠です。利用者/IPでpartitionせず、healthは制限しません。
timeoutは504で返し、qpdfの検査と各PDF処理の段階でprocess treeの終了と終了待ちを行ってから一時領域を削除します。クライアントキャンセルやアプリ停止でもqpdfを終了します。
サイズ超過は413、形式不正は400です。内部情報を含まないProblem Detailsで返します。
成功、途中入力の拒否、timeout、クライアントキャンセル、アプリ停止のすべてでjob directory全体を再帰削除します。アプリ停止のtokenはアップロード・PDF送信にも適用します。

結合の出力PDFは入力合計より大きくなる可能性があります。入力合計50 MiB、同時2 requestはtmpfs 256 MiBへ収めやすくする初期値であり、出力サイズを保証しません。tmpfs / memory limitを実行環境側の安全境界として維持します。`--empty --pages`による結合は文書レベルmetadata / outlineの保持・統合を保証しません。

qpdf自身も`--check`でJPEG streamをプロセス内でデコードします。JPEGMEMはlibjpegの内部メモリ管理へ作用しますが、baseline JPEGの全メモリやqpdf自身のデコード済みbufferを制限できません。Flate画像にもJPEGMEMだけでは十分ではありません。標準JPEGMEMは標準AS全体のbytes値より大きくし、ASで処理できる範囲を先に狭めない値にしています。標準ASでは巨大progressiveの拒否に大きな差はありませんでしたが、ASを768 MiBへ増やした比較ではJPEGMEM 600Mにより拒否までの時間とRSSを減らせました。ASを増やすときは、JPEGMEMによる受付範囲の変化も測定してください。AS、同時実行数、tmpfs、コンテナmemory 1.5 GiB（swapなし）を合わせて管理します。制限は設定値で、実行時の空きメモリに応じた自動調整はしません。

入力checkの2/3は破損、警告、上限超過を区別せず共通422とし、stderr解析や新しいreasonを追加しません。unlockのprobeの3は段階によって判定結果を表すため既存の対応を維持します。出力検証の失敗、126/127等の起動失敗、想定外の終了は500です。

6000×4000／8064×6048のカラーJPEGはbaseline 4:2:0、baseline 4:4:4、progressive 4:2:0を、標準設定・同時2件で確認しました。tmpfs圧力下では8064×6048の各形式を60要求ずつ実行しています。合成fixtureの測定であり、他形式・任意のPDFの処理成功や本番でのOOM回避を保証しません。

[READMEの容量式](../README.md#リソース制限と設定)と[実測記録](qpdf-memory-validation.md)を運用時の見直しに使用してください。式の.NET値と余裕は見積りであり、AS以外のAPIメモリや出力サイズのhard limitを追加するものではありません。

利用者/IP単位の頻度制限は入口側、CPU/メモリ/tmpfs容量の上限は実行環境側で設定します。

API単体ではパスワードを試す回数を制限しません。総当たり対策は入口側（`amane-tools-site`）の日次回数・分単位の制限を前提とします。Issue #21の前提は未ログイン3回・無料10回の日次制限と分単位のレート制限ですが、その実装・適用状況はこのリポジトリでは確認できません。既存のConcurrency Limiterは共有処理枠の制限であり、利用者ごとの試行回数制限ではありません。

## コンテナとネットワーク

実コンテナsmoke testで次を確認しています。

- appユーザー（non-root）で実行する
- read-only root filesystem + tmpfs /tmpでPDF暗号化・入力拒否・削除が成立する
- CPU 1 / memory 1.5 GiB（memory-swapも1.5 GiB、swapなし） / tmpfs 256 MiBを外側から指定できる
- capabilityをdropし、no-new-privilegesで実行できる
- DB、Secret、永続Volumeなしで動作し、終了時にはコンテナを削除する
- 外部networkなし（--network none）のコンテナでもloopback HTTPで実PDF処理が成立する

PDF処理時に外向き通信は必要ありません。サービス間通信や不要な外向き通信の制御は、本番実行環境で設定する責務です。
SIGKILLやコンテナ強制終了ではfinallyは実行されません。tmpfsを用いることで、コンテナを削除した後にPDFやpasswordファイルを永続Volumeへ残さない構成にできます。

## qpdf

PDFの暗号化・パスワード解除、lossless構造最適化、結合、ページ回転、ページ抽出・削除・並べ替え処理にはqpdfを使用します。

qpdfはApache License 2.0で公開されているOSSです。

runtime stageはUbuntu 26.04ベースの `mcr.microsoft.com/dotnet/aspnet:10.0-resolute` を使用し、qpdfをaptから導入します。確認した版は12.3.2で、完全なversion pinは行わず、CIでhost/containerのqpdfが12系以降であることと必要機能を検証します。
2026-10時点、commit `f847c68`でqpdf 11.9.0のunlock動作を手動確認済みです。CIの対象は12系以降です。

qpdfの更新状況を定期的に確認し、既知の脆弱性や重要な修正がある場合は更新します。

## 検証と責務の境界

実qpdfの暗号化/解除（AES-256・AES-128・RC4のuser/owner password）/正誤password/Unicode/lossless最適化/複数PDF結合と入力順/相対回転/ページ抽出・削除・並べ替え/不正入力、一時ファイル削除、argv非露出を.NETテストで確認します。
timeoutとキャンセルは実process shimで遅延を再現し、親/子PIDの終了と処理枠の再利用を確認します。
CIでは実コンテナでhealth、QPDFJob JSONと必要機能、`--remove-info` / `--remove-metadata` の存在、暗号化、解除成功とwrong-password、lossless最適化、代表的なページ選択、異常入力、413、情報非露出、一時領域の削除を確認し、host/containerのqpdf versionを記録します。

API自身のサイズ・処理時間・同時実行制限は実装済みです。利用者/IP単位の頻度制限、CPU/メモリ/ネットワーク設定、公開・deployは入口と実行環境の責務です。

## JPEG画像圧縮（Linux）

compressはページResourcesの直接の画像XObjectだけを選び、Form内・inline imageをたどりません。DCTDecode単独、DecodeParmsなし、8 bit、実JPEGと辞書の寸法・成分数一致、生JPEG 32 KiB以上、初期値1億画素以下を検査します。DeviceGray／DeviceRGB、Nが一致するICCBased、辞書を持つCalGray／CalRGBが対象です。ImageMask=true、Decode、color key Mask配列、Matte付きSMask、対象外ColorSpace、未解決・循環参照は除外します。ColorSpace・profileと保持対象のMask／SMask・Interpolateを維持し、ICC header／acsp、Range／Alternate、WhitePoint等の値域の検査は追加しません。

JPEGはサイズ上限付きstreamで最初のEOIまで解析します。2026-10-08のHuman承認により、MPFのHDR gain mapなどEOI以降のデータを無視し、置換時には新JPEGへ引き継ぎません。SOF0／SOF1／SOF2のprecision 8・成分数1／3だけを許容し、EOIより前のheight 0／DNL、複数SOF、対応外SOF、切れたmarker、不正なsegment長、SOSなしを拒否します。最初のSOFで検査を終えません。エントロピー等の検査は制限付きdjpegの-strictにも任せます。縮小は整数の切り上げM/8、拡大なしです。新JPEGも同じparserで本体と実寸・成分数を再検査し、実寸でWidth／Heightを更新します。後続データの有無は検査対象外ですが、EOI以前の検査と期待する寸法・成分数の一致は維持します。Lengthはqpdfに生成させ、採用はchecked整数の10×新サイズ≤9×元サイズで決めます。

既存ExternalProcessRunnerとProcessMemoryLimitsを使用し、prlimitをshellやlauncherなしで直接起動します。RunnerのDIや独自kill／waitは追加しません。qpdfのJPEGMEM／ASは共通設定を維持します。djpeg／cjpegはAS 64 MiB、必要な出力にはRLIMIT_FSIZEを適用し、-maxmemory 64M／-strict、djpegには-maxscans 100を付けます。Linux起動時には小さな画像で必要オプションの実動作を確認し、失敗・timeoutは固定ログだけで起動を止めます。自己テストを省略する本番設定はありません。偽コマンドと短いrequest予算による異常系テストは、実コマンド・有効な設定で起動した後にテスト側だけで差し替えます。

pages／metadata JSONはfsize付きファイル出力、初期値16 MiB・深さ64です。候補辞書は最大500参照ずつ取得してprivate spoolへ保存し、spool合計16 MiBを上限とします。全候補のDOMを同時保持しません。ページ、Parent、Resources、XObject、画像辞書、SMask／Mask／ICCの参照を階層ごとにまとめて先読みし、ページ数に比例した1参照ずつの外部起動を避けます。ページ側は最大500ページずつ（低いspool設定ではさらに少なく）取得→画像参照収集→削除し、別名の連鎖を含めてspoolと容量台帳から解放します。画像／ICC／Mask／SMask辞書は画像の判定・変換に必要な間保持します。深い継承・別名や大きな辞書で一単位が上限へ達した場合も、上限を緩めず既知のmetadata上限として扱います。間接参照はmetadataだけで解決し、Length降順の固有objectを最大500個選びます。生JPEGは最大50画像ずつ、S=round4KiB(最大Length+4KiB)、n×Sを予約し、Length合計とn×Sをともに残り領域へ収めます。抽出fsizeはS、JSONはファイル出力なし・既存Runnerのstdout上限1 MiBです。サイズ・objectとdatafileの対応・private batch directory内のpath・symlinkなしを確認します。stdout超過や部分抽出はファイルを削除し、複数画像バッチから各画像一回だけの抽出へ縮小します。無制限retryはしません。

job台帳は4 KiB単位、初期値124 MiBです。入力、metadata、raw、PNM、新JPEG、採用JPEG、JSONの同時存在を予約します。PNMは縮小後のceil寸法×成分数+headerから求め、512 bytesの余裕を足したfsizeを使い、初期値24 MiBを超える画像を採用しません。新JPEGのfsizeは元の90%と残り容量から求めます。採用条件はI+A+J+F≤L、F=I−R+A+4 MiBとし、実測R／A、実サイズJ、各ファイルの割当量も確認します。結合時にはentry断片群とupdate.jsonが同時に存在するため、I＋採用JPEG割当＋entry割当合計＋update.json割当も採用前に確認します。結合時と出力書き出し時の容量をそれぞれ判定します。共有objectは一度だけ数えます。採用JPEGは保持、不採用raw／PNM／新JPEGは削除します。最終処理前には不要な中間物を削除します。jobは0700、.NETで作成するファイルは0600、qpdfの自動生成rawは0700のbatch directoryを境界とします。

アップロード完了後・入力検証前から30秒、同じ開始時刻から21秒で画像処理を終了します。CompressSoftTimeoutSecondsはQpdfTimeoutSeconds未満を必須とし、起動時に検証します。実行中の画像・バッチもsoft deadlineで中断し、一枚のdjpeg＋cjpegは共有2秒、バッチ抽出も2秒です。request中断・アプリ停止・hard timeoutを画像失敗として握りつぶしません。cancel時のkill tree／wait／stdout・stderr drainはRunnerが行います。残り9秒で書き出し・出力検証を行いますが、hard deadlineに達すれば504となります。

画像単位の非0終了（126／127以外、SIGXFSZの153も含む）はその画像を保持します。126／127・起動失敗は500です。既知のmetadata・時間・容量上限は画像処理を終了し、採用済み結果で最終処理を試みます。候補判定中のmetadata上限では未採用の候補を新たに変換せず、採用済みJPEGがなければ置換0になります。想定外のmetadata異常、最終書き出し／出力検証の失敗（2／3も含む）は500、入力検査の上限超過は従来のreasonなし422です。出力見積りは上界の保証ではなく、出力fsize超過の部分ファイルは削除して500です。最終検証とファイルopen後に成功ヘッダーを付け、エラーには付けません。PDF・画像・JSON・stdout／stderr・内部pathを本番ログやエラーへ出しません。

最終qpdfにはobject-streams=generateとcompression-level=9だけを指定します。stream-data、recompress-flate、decode-levelは指定しません。単独DCT／Flate／RunLength／JPX／JBIG2／CCITTの生データ、LZW／ASCIIHex／ASCII85・複合filterのデコード後データと表示上の意味を検証します。qpdf --checkだけを表示保持の根拠とせず、CIの構造・データ比較と描画比較を行います。描画ツールはruntimeに含めません。

標準条件はmemory 1.5 GiB・swapなし・CPU 1・tmpfs 256 MiB・同時2・non-root・read-only・network noneです。2×124 MiBのjobに8 MiBのtmpfs余裕を残します。tmpfsもcgroup memoryへ含まれるので、ファイルの容量式だけで成立を判断しません。上限は設定可能な安全境界であり、拡大するときはREADMEのメモリ式・tmpfs式と実測を見直します。Webシステムのプラン別サイズ・回数制限はサイト側の責務です。[圧縮の実測記録](compress-validation.md)は対象fixtureでの確認であり、任意の入力の成功を保証しません。
