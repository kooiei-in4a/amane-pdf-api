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

qpdfの`--check`はPDFの完全な適合性や無害性を保証しません。cleanでは文書添付・添付注釈・AF / EF / RF経由の埋め込みファイルを除去し、出力検査で残存した場合は成功として返しません。filespec名やprivate application dataが残る場合があり、JavaScript等の無害化は含みません。他のAPIを情報除去として案内しません。

## リソース制限

実装済みの初期値:

- PDF 50 MiB、リクエスト総量はその上限 + multipart用64 KiB
- 結合は最大10ファイル、入力合計50 MiB、リクエスト総量は入力合計上限 + 64 KiB。各入力にも既存の単一PDF上限を適用する
- qpdfの検査と各PDF処理、または検査・ページ数取得・ページ操作の全体で30秒
- 結合では全入力検証と最終結合を1つの30秒予算で制御し、qpdfをrequest内で並列起動しない
- unlockはJSON作成・対象判定・認証・入力検査・解除・出力検証で1つの30秒予算を共有し、qpdfを逐次実行する
- 同時PDF処理数2、待ち行列0
- Linuxではすべてのqpdf実行をenv → prlimit → qpdfのexec経路で起動し、SIGXFSZ無視、RLIMIT_CORE=0、RLIMIT_AS 544 MiB（570425344 bytes）を適用。shellは挟まない
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

既存ExternalProcessRunnerとProcessMemoryLimitsを使用し、OS標準の`/usr/bin/env --ignore-signal=XFSZ --`からprlimitとtoolへexecします。RunnerのDIや独自kill／waitは追加しません。qpdfのJPEGMEM／ASは共通設定を維持します。djpeg／cjpegもSIGXFSZ無視とCORE=0、AS 64 MiB、必要な出力にはRLIMIT_FSIZEを適用し、-maxmemory 64M／-strict、djpegには-maxscans 100を付けます。Linux起動時には同じ経路で小さな画像を変換して必要オプションの実動作を確認し、失敗・timeoutは固定ログだけで起動を止めます。自己テストを省略する本番設定はありません。偽コマンドと短いrequest予算による異常系テストは、実コマンド・有効な設定で起動した後にテスト側だけで差し替えます。

pages／metadata JSONはfsize付きファイル出力、初期値16 MiB・深さ64です。候補辞書は最大500参照ずつ取得してprivate spoolへ保存し、spool合計16 MiBを上限とします。全候補のDOMを同時保持しません。ページ、Parent、Resources、XObject、画像辞書、SMask／Mask／ICCの参照を階層ごとにまとめて先読みし、ページ数に比例した1参照ずつの外部起動を避けます。ページ側は最大500ページずつ（低いspool設定ではさらに少なく）取得→画像参照収集→削除し、別名の連鎖を含めてspoolと容量台帳から解放します。画像／ICC／Mask／SMask辞書は画像の判定・変換に必要な間保持します。深い継承・別名や大きな辞書で一単位が上限へ達した場合も、上限を緩めず既知のmetadata上限として扱います。間接参照はmetadataだけで解決し、Length降順の固有objectを最大500個選びます。生JPEGは最大50画像ずつ、S=round4KiB(最大Length+4KiB)、n×Sを予約し、Length合計とn×Sをともに残り領域へ収めます。抽出fsizeはS、JSONはファイル出力なし・既存Runnerのstdout上限1 MiBです。サイズ・objectとdatafileの対応・private batch directory内のpath・symlinkなしを確認します。stdout超過や部分抽出はファイルを削除し、複数画像バッチから各画像一回だけの抽出へ縮小します。無制限retryはしません。

job台帳は4 KiB単位、初期値124 MiBです。入力、metadata、raw、PNM、新JPEG、採用JPEG、JSONの同時存在を予約します。PNMは縮小後のceil寸法×成分数+headerから求め、512 bytesの余裕を足したfsizeを使い、初期値24 MiBを超える画像を採用しません。新JPEGのfsizeは元の90%と残り容量から求めます。採用条件はI+A+J+F≤L、F=I−R+A+4 MiBとし、実測R／A、実サイズJ、各ファイルの割当量も確認します。結合時にはentry断片群とupdate.jsonが同時に存在するため、I＋採用JPEG割当＋entry割当合計＋update.json割当も採用前に確認します。結合時と出力書き出し時の容量をそれぞれ判定します。共有objectは一度だけ数えます。採用JPEGは保持、不採用raw／PNM／新JPEGは削除します。最終処理前には不要な中間物を削除します。jobは0700、.NETで作成するファイルは0600、qpdfの自動生成rawは0700のbatch directoryを境界とします。

アップロード完了後・入力検証前から30秒、同じ開始時刻から21秒で画像処理を終了します。CompressSoftTimeoutSecondsはQpdfTimeoutSeconds未満を必須とし、起動時に検証します。実行中の画像・バッチもsoft deadlineで中断し、一枚のdjpeg＋cjpegは共有2秒、バッチ抽出も2秒です。request中断・アプリ停止・hard timeoutを画像失敗として握りつぶしません。cancel時のkill tree／wait／stdout・stderr drainはRunnerが行います。残り9秒で書き出し・出力検証を行いますが、hard deadlineに達すれば504となります。

画像単位の非0終了（126／127以外、SIGXFSZの153も含む）はその画像を保持します。126／127・起動失敗は500です。既知のmetadata・時間・容量上限は画像処理を終了し、採用済み結果で最終処理を試みます。候補判定中のmetadata上限では未採用の候補を新たに変換せず、採用済みJPEGがなければ置換0になります。想定外のmetadata異常、最終書き出し／出力検証の失敗（2／3も含む）は500、入力検査の上限超過は従来のreasonなし422です。出力見積りは上界の保証ではなく、最終出力の実サイズがfsize上限以上なら、exit 0やqpdf --check成功でも削除して500です。上限同値の正常PDFも安全側に拒否します。最終検証とファイルopen後に成功ヘッダーを付け、エラーには付けません。PDF・画像・JSON・stdout／stderr・内部pathを本番ログやエラーへ出しません。

最終qpdfにはobject-streams=generateとcompression-level=9だけを指定します。stream-data、recompress-flate、decode-levelは指定しません。単独DCT／Flate／RunLength／JPX／JBIG2／CCITTの生データ、LZW／ASCIIHex／ASCII85・複合filterのデコード後データと表示上の意味を検証します。qpdf --checkだけを表示保持の根拠とせず、CIの構造・データ比較と描画比較を行います。描画ツールはruntimeに含めません。

標準条件はmemory 1.5 GiB・swapなし・CPU 1・tmpfs 256 MiB・同時2・non-root・read-only・network noneです。2×124 MiBのjobに8 MiBのtmpfs余裕を残します。tmpfsもcgroup memoryへ含まれるので、ファイルの容量式だけで成立を判断しません。上限は設定可能な安全境界であり、拡大するときはREADMEのメモリ式・tmpfs式と実測を見直します。Webシステムのプラン別サイズ・回数制限はサイト側の責務です。[圧縮の実測記録](compress-validation.md)は対象fixtureでの確認であり、任意の入力の成功を保証しません。

## PDF分割とZIPの破棄

splitは入力検査・ページ数取得後に2〜設定上限の計画を確定し、元PDFから1パートずつ生成・検査します。qpdfの出力先はAPIが作る0700のjob内の0600ファイルです。ZIPへ128 KiB bufferでコピーし、パートのhandleを閉じて削除できた後に容量を解放します。入力・作成中ZIP・パートの同時存在を4 KiB単位で管理します。最初にZIP余裕1 MiBとFSIZEの1 byte検出用4 KiBを予約し、パートとZIPコピーの重複に残予算の半分を使います。Linuxでは共通AS/JPEGMEMに加えてpartBudget+1のFSIZEを適用し、実出力がpartBudgetを超えればexit 0でも専用422とします。126/127は常に500、予算以内の3は共通422、その他の非0は500です。生成PDFの検証失敗は500です。stderrの解析でENOSPCを422へ変換しません。

seek可能なwrite wrapperは、ZIPの最終長が採用PDF合計＋1 MiB以内で、入力＋現在のパート＋ZIPの割当量がjob予算以内であることを検査します。全write overload・WriteByte・SetLengthを共通検査に通し、cancelを容量超過より先に判定します。Stored、固定名とDOS時刻、最大500entry、完成ZIPはuint.MaxValue未満に制限し、ZIP64を対象外とします。元PDFの文書情報・添付・しおりが残り得るため、情報除去として案内しません。

ZipArchive/entry streamをusingで囲まず、コピー成功時だけentryを閉じ、全パート成功時だけarchiveを最終化します。作成・コピー・entry/archiveのDispose・パート削除・基底streamのcloseのいずれかで失敗したら、wrapperをAbortし、ZIPの最終化を再試行しません。Abort後のwrite/seek/length変更/flushを拒否します。所有する実FileStreamは必ず閉じます。主処理失敗後のclose障害だけを抑えて元の例外を維持し、成功時のclose障害は500へ伝播します。完成してcloseしたZIPだけを既存helperから送信し、部分ZIPはjob全体とともに削除します。request中断・アプリ停止・全処理timeoutは既存Runnerのprocess tree終了待ちを維持します。

標準容量の前提は4 KiB pageのx86_64 Linux Docker、同時2、tmpfs 256 MiBです。非Linuxにはqpdfの書込み中のFSIZE/AS hard limitがなく、他page sizeも同じピーク容量を保証しません。容量・時間の実測は通常のsmokeと分離した`scripts/split-validation.py`で行い、[測定記録](split-validation.md)に残します。

## PDF情報除去（Linux）

cleanも共有のアップロード・30秒のdeadline・concurrency枠・送信・job削除を使用します。入力の検証後、qpdfのJSON v2をstreamデータなしで取得し、元object番号とheaderを保持した部分更新をQPDFJobへ渡します。removeInfo / removeMetadataと合わせて一度だけPDFを生成し、preserve-unreferencedは指定しません。catalogのNamesからEmbeddedFilesだけを切り離し、他のname treeを保持します。

全辞書からAF / EF / RFを除去し、FileAttachmentはType / Subtype / Rectの許可リストに縮めます。PopupはFileAttachmentのPopup参照とPopupのParent参照の和集合を変更前に分類します。対象の間接objectはnullにし、直接PopupはAnnots配列から外します。直接・間接・別名・共有配列を同じresolverで扱い、対象外のnull・数値・文字列・不正な型は保持します。構造だけを理由に新しい422を設けません。

出力では正常性、非暗号化、同じページ数、InfoのModDate以外、catalog Metadata、EmbeddedFiles、空のattachments、全AF / EF / RF、EmbeddedFile stream、許可リスト以外の添付注釈キー、Annots内の対象注釈を検査します。入力と出力で同じ関数により直接辞書・間接object・間接Subtypeを含めたPopup件数を数え、出力が入力の通常Popup数を超えないことも確認します。入力のobject番号は出力へ持ち越しません。残存と検査不能は500です。

JSONは各32 MiB・深さ64、参照連鎖64、間接object 100,000、PDF出力54 MiB、job 124 MiBが初期値です。1 jobの入力から出力検査までを4096 bytes単位で予約し、実サイズへ縮め、削除後に解放します。.NETの更新・job JSONもwrite前に上限を検査します。入力DOMと不要ファイルを破棄してから次のqpdfを起動し、複数DOMを同時保持しません。2 job 248 MiBだけでメモリ成立を判断せず、tmpfsも含めたcgroupの実測を[検証記録](clean-validation.md)に残します。

qpdfのファイル書込みは共通のSIGXFSZ無視・CORE=0・AS・JPEGMEMにFSIZEを加えます。cancel、126/127、実サイズがFSIZE以上、その他exit、JSONの順で判定し、exit 0でも切れたJSON/PDFや上限同値を専用422（too-complex）にします。入力checkの2/3は従来422、入力検証後の想定外exitは500です。stderrやJSONの内容から利用者向けエラーを作りません。ファイル名・PDF・JSON・プロセス出力をログやProblem Detailsへ記録しません。

## core dumpと公開条件

FSIZE超過時のSIGXFSZの既定動作はcoreを伴う終了です。Linuxの共通起動経路でSIGXFSZをSIG_IGNへ設定してtoolへexecし、この終了を防ぎます。書込み上限はprlimitで維持します。qpdfは切れたJSONでもexit 0となり得るため、pages／metadataの実サイズが上限以上なら、同値を含めて既知のmetadata上限として画像処理を終了します。splitは従来どおりpartBudget+1のFSIZEと実サイズの比較で専用422を返します。

CORE soft/hard=0も共通適用しますが、pipe方式のcore collectorはRLIMIT_COREを無視するため、CORE=0だけを受渡し防止策とは扱いません。SIGSEGV／ABRT等の別signalによるcoreは今回の対策対象外です。qpdfのメモリにはPDF内容が含まれ得るため、コンテナ内にcoreファイルがないことだけではhostのcollectorへ内容が渡らない証明になりません。[Linuxのcore仕様](https://man7.org/linux/man-pages/man5/core.5.html)と[signalのexec時の継承](https://man7.org/linux/man-pages/man7/signal.7.html)を参照してください。

合成データによるAPIの検証結果は[FSIZE core対策の検証記録](fsize-core-validation.md)に残します。本番に近い隔離VMでのcollector起動数・受信bytes・保存数の確認とinfraの容量整合は[共通対策 #46](https://github.com/kooiei-in4a/amane-pdf-api/issues/46)で追跡し、完了までAPI splitのdeployを保留します。サイトのsplitツールもAPI deploy後に行います。PRのmergeとdeployはそれぞれHumanの承認が必要です。
