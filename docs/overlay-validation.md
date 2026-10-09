# Issue #25 PR B 共通描画基盤の検証

2026-10-09 JST。基準は取得し直した `origin/main` の `77e8a8caf96bfb124032a331fa7eb77674fd0743`（PR A #50・CI改善 #52を含む）。専用worktreeで実装した。新しいHTTP endpoint、infra、サイトは追加しない。設計の正本は [pdfcpu-design.md](pdfcpu-design.md)、PR Aの記録は [pdfcpu-validation.md](pdfcpu-validation.md)。#46によるdeploy保留を継続する。

## 確定した上限

初期案と確定値を分ける。設定は確定値を超えて広げられず、小さい設定ではjobの実効予算を優先して拒否する。

| 項目 | 初期案 | 確定値 |
| --- | ---: | ---: |
| 入力PDF | 50 MiB | 50 MiB（MaxFileBytesとの小さい方） |
| 入力metadata | 32 MiB | 32 MiB |
| 合成後・最終metadata | 各32 MiB | 各40 MiB |
| 入力 / 生成後object | 50,000 / 75,000 | 同左 |
| JSON / 参照・Parent深さ | 64 / 32 | 同左 |
| 対象ページ | 1,000 | 同左 |
| 白紙PDF・白紙/描画/更新JSON・レイヤー | 各8 MiB | 同左 |
| overlaid / 最終PDF | 各54 MiB | 各62 MiB |
| job / deadline / 同時実行 / queue | 124 MiB / 30秒 / 2 / 0 | 同左 |
| 文字系model | 未確定 | 1要素128 UTF-16単位、1ページ4要素、合計4,000要素 |
| LF / tab | 未確定 | 各要素にそれぞれ3個まで。他の制御文字は拒否 |
| font / size | 固定font | JapaneseFont固定、1〜14,400 pt |
| 座標・offset / 文字回転 | 有限値 | ±14,400 pt / ±360度 |

不正UTF-16とpdfcpuの置換文字`%`は引き続き拒否する。文字はJSONだけで渡し、font・path・任意JSONを利用者に指定させない。画像stamp #29と画像→PDF #26は対象外であり、各Issueでbytes・画素数・生成物の上限を定め、同じ最大入力・容量・時間検証を行う。

初期54 MiBではoverlayが実際にFSIZEへ達した。生成後metadataも34,561,982 bytesとなり32 MiBを超えたため、生成後metadataだけ40 MiB、PDF出力だけ62 MiBへ変更した。124 MiB/job・256 MiB/tmpfs・30秒は延長していない。BMPの全領域を含めた初回測定では同時実行の一方が24.693秒で不合格だった。入力ページ数を同じmetadataから取得し、最終暗号化判定も同じmetadataへまとめ、PDFの読み直しを2回減らした。最終の独立した `qpdf --check` exit 0は維持して再測定した。

## 手動の上限規模測定

既存 `Amane.Pdf.Api.Tests` の実Builderを使う。製品assemblyのInternalsVisibleToは増やしていない。`OverlayCapacity` category・DoNotParallelizeの単独/同時2件テストを、製品runtime imageのdotnetとread-only mountした既存test assembly・SDK VSTest runnerで実行した。runner・Popplerを配布imageへ追加していない。

条件はLinux x86_64・4 KiB割当、CPU 1、memory 1.5 GiB、swapなし、tmpfs 256 MiB、non-root、read-only、network none、cap-drop ALL、no-new-privileges。単独と同時2件を別cgroupで実行する。入力配置・qpdf/JPEG/pdfcpuの起動自己テストはtimer前。同時2件は1つの30秒tokenを共有し、各timerはBuilder開始から最終検査・中間物削除まで。出力削除とjob全体の削除もtimer後に確認した。

入力は52,297,734 bytes（50 MiBまで131,066 bytes）、入力metadataは33,030,150 bytes（32 MiBまで524,282 bytes）、到達可能な入力objectはちょうど50,000、対象は1,000ページ。独立した文字列と圧縮しにくいstreamで、PDF bytesとmetadata bytesを同時に上限規模へ近づけた。生成後objectは53,007で、75,000の厳密な境界は別の.NETテストで検査する。

文字は4,000要素×128 UTF-16単位＝512,000単位。固定TTFのSHAをlockと照合し、cmapで各glyphへ対応する文字を選ぶ。BMPだけの11,425種類と、補助平面313 scalarを含む11,738種類の2条件を用意し、seed 25でshuffleして繰返し文字の圧縮を避ける。さらに、改行/tabなしと上限の各3個を含む4行の条件を組み合わせた。補助平面文字はsurrogate pairを切らず、128単位を満たす。font subsetの多様性と改行/tabのstream増加を両方測るための4条件である。

ページは物理24×32 ptと14,400×14,400 ptを交互に配置し、UserUnit 0.5/1/2、Rotate 0/90/180/270/-90/630を混在させた。各ページにfont size 1/12/72/14,400、座標0/14,400、offset -14,400/+14,400、文字回転-360/-45/90/360を組み合わせる。小さいページ・大きいページ・長い文字列・多様なglyph・大きい配置数値を含めることで、endpoint未追加でも最大文字入力のレイヤーを実際に生成する。

実測の生データ（各処理のstage時間・bytes・object数・実効予算・cgroup events・残存物）は [overlay-capacity-results.json](overlay-capacity-results.json)。レイヤーの予約実効PDF予算は全処理8,388,608 bytesであり、file上限とjob残予算の両方へ余裕を持つ。

| 文字条件 | 単独秒 | 同時2件の各秒 | 最大レイヤーbytes | 最小残りbytes |
| --- | ---: | ---: | ---: | ---: |
| dense BMP | 11.602 | 21.676 / 21.753 | 4,193,554 | 4,195,054 |
| dense 補助平面込み | 10.623 | 21.342 / 22.114 | 4,241,093 | 4,147,515 |
| LF/tab BMP | 10.630 | 22.683 / 22.506 | 4,248,401 | 4,140,207 |
| LF/tab 補助平面込み | 11.323 | 22.326 / 22.541 | 4,295,096 | 4,093,512 |

最大レイヤーは実効予算の51.202%で、残りは48.798%。最長22.6827033秒は24秒に1.3172967秒、共有30秒に7.3172967秒の余裕がある。すべて成功し、FSIZE失敗・OOM・oom_kill・残存job・残存処理processは0。

| 文字条件 | memory.peak 単独 / 同時2件 bytes | tmpfs観測最大 単独 / 同時2件 bytes |
| --- | ---: | ---: |
| dense BMP | 443,932,672 / 840,994,816 | 114,196,480 / 223,989,760 |
| dense 補助平面込み | 444,506,112 / 864,555,008 | 114,294,784 / 227,614,720 |
| LF/tab BMP | 440,434,688 / 853,401,600 | 113,823,744 / 222,457,856 |
| LF/tab 補助平面込み | 444,973,056 / 819,040,256 | 114,405,376 / 227,643,392 |

memory.peakはtest driver/VSTestとmanaged heap、子tool、tmpfsも含むcgroup全体の値。最大864,555,008 bytesはmemory上限に746,057,728 bytesの余裕がある。tmpfsは約0.1秒ごとに採取した観測値で、瞬間最大の証明ではない。最大227,643,392 bytesは256 MiBに40,792,064 bytesの余裕がある。別途jobの4 KiB予約ピークは最大122,359,808 bytesで124 MiBに7,663,616 bytesの余裕があり、FSIZE sentinelを含む。job台帳と観測を併用した。

最大レイヤー条件のoverlaidは57,133,009 bytes、最終PDFは57,060,673 bytes（62 MiBまで7,951,039 bytes）。合成後metadataは34,561,982 bytes、最終metadataは34,321,646 bytes（各40 MiB以内）。白紙PDFは121,125 bytes。最長処理（LF/tab BMP、同時2件のindex 0）の段階別時間は次のとおり。

| 段階 | 秒 |
| --- | ---: |
| 入力検査 | 2.234 |
| 入力metadata | 3.002 |
| 入力属性・調整JSON（metadataを除く） | 0.033 |
| 白紙JSON/PDF | 0.072 |
| 描画JSON/pdfcpu/レイヤー検査 | 3.097 |
| 調整とoverlay | 3.695 |
| 合成後metadata | 3.014 |
| 復元JSON（metadataを除く） | 0.012 |
| 復元PDF書き出し・中間削除 | 2.474 |
| 最終qpdf check | 2.239 |
| 最終metadata | 2.790 |
| 最終属性比較・検査JSON削除（metadataを除く） | 0.012 |

生データの`input-attributes`・`restore-attributes`・`validate-output`は直前のmetadata取得を含む区間で、上表では内数のmetadataを差し引いた。総timerは二重計上しない。最終出力だけがjobへ残ること、出力削除後のfile数0、job削除後のディレクトリ不在、test終了後のqpdf/pdfcpu/prlimit/JPEG/dotnet/testhost不在を検査した。測定fixtureの結果を任意PDFの性能保証へ広げず、cleanの19.4秒もoverlayの保証に使わない。

## geometry・保持・描画比較

実toolテストは一時調整JSONとoverlayを同じqpdf呼出しで行う。Rotate 0/90/180/270/-90/630、負のorigin、偏ったCropBox、CropBoxより小さいTrimBox、UserUnit 0.5/2、混在寸法、対象外ページを含む7ページを使い、合成後の元の整数Rotate・全Box・UserUnitを検査した。継承/null/間接値、新しいParentでの継承一致/欠落/変更、入力の古い参照を新しいobjectへコピーしない復元は.NETテストで固定する。

pdfcpu createの配置計算は入力白紙のMediaBoxだけではA4のままになることを実描画で確認した。canvasの物理寸法を型付き`PdfcpuLayer.PageSizes`へ渡し、APIが固定形式のcropをJSONに生成する修正を加えた。

`scripts/overlay-validation.py --render`では実Builderが描いた赤い日本語・Latin文字と独立したレイヤー画像を比較する。Poppler 26.01.0、RGB・CropBox、元PDF/出力は72 dpi。Popplerのpixel寸法にはUserUnitが反映されないため、物理レイヤーだけ72/UserUnit dpiで描き、画像のresizeはしない。文字の位置・向きは最大RGB差2以内、実際の文字maskへ1 pixelの余裕を加えた外側の元本文は完全一致、非対象ページも完全一致した。

別の実fixtureでは画像・しおり・Link・Widget/フォーム値を保持した。画像のdecoded bytes SHA/寸法/ColorSpace、しおりの新しいページ参照、フォームfield数/name/valueとLink URIを検査し、画像・注釈・フォームを含む元の表示は追加文字領域以外で完全一致、赤文字のalpha合成は最大RGB差2以内だった。注釈が文字より前面に表示される通常のPDF挙動も保持する。

CropBox外の矩形は出力のCropBoxを広げてもForm BBoxで切られて表示されず、元PDFを広げた場合との差を検査した。同領域の`OUTSIDE`はstreamとPopplerの文字抽出に残る。通常のCropBox内でTrimBox外の本文は保持する。日本語抽出とembedded/subset/Unicode fontも成功した。この処理は情報除去ではない。

## 境界・故障・lifecycle

JSONのbyte上限・深さ64、object 50,000/50,001・75,000/75,001、参照とParentの循環/深さ、有限値・交差・属性型・物理寸法、文字128/129・要素4/5、対象ページ1,001の拒否、managed write/job予約/FSIZE sentinelを検査した。最大1,000ページの成功は手動測定に含む。

qpdfの非0/126/127・出力不在/空/破損/暗号化/warning・ページ数不一致・壊れたmetadata・生成後の不正属性を返さない。入力の上限超過は422 `too-complex`、入力の未対応属性は422 `unsupported-pdf`、tool異常と無効な生成物は固定500。pdfcpuのexit 1・出力不在をstderrの文字列だけで容量超過と推測しない。既存helperのreason/title/内部情報非出力をendpoint追加なしで検査した。

入力metadata、白紙、描画、overlay、復元、最終検査の6段階でcancel/timeout/app停止を注入した18ケースで、親子processの終了待ちとjob削除を検査した。Linuxの必須tool不足をInconclusiveへ変換しない。Popplerは専用scriptとdocker jobだけの依存とし、通常の.NETテストには要求しない。

## 通常CI・ローカル検証・公開待ち

通常CIは独立した`build-test`と`docker`の2 job、各10分timeoutを維持する。build-testはRelease全テストと小さいfixtureのOverlayCapacity回帰検証。dockerは製品image build、既存API smoke、圧縮描画比較、日本語抽出/font、実Builderの文字・元本文の描画比較を行う。上限規模4条件の手動cgroup測定は通常CIへ入れない。

ローカルの最終Release buildは警告0・エラー0で成功。全テスト、Docker build、既存APIを含む25 POST smoke、圧縮の構造/stream/描画比較、日本語抽出/font、実Builderの描画比較を実行した。最終の全716テストが成功（失敗0・skip 0、3分30秒）。単独Release buildは0.86秒だった。検証imageのlocal digestは `sha256:2aa44984e48e33781de332810634b8809ac46ee0edc40290d19574af1e3aad1d`。Docker buildはcacheを利用したため、GitHub CIの実時間の代用にはしない。

PR BのGitHub CIは公開承認前のため未実行。PR A/#52後のmainのCI成功は基準の確認であり、PR BのCI成功とは区別する。公開後に両jobの成功・実時間・10分timeoutの余裕を読み戻して記録し、A/Bの全完了条件を満たしてからだけ`Closes #25`を使う。ローカルの実装・制限付き測定・セルフレビューを終えても、PR B全体の完了扱いは両CI成功まで保留する。merge・tag・deployは行わない。

セルフレビューでは範囲、継承/復元、生成後の参照、容量予約/FSIZE、token/終了待ち/削除、固定エラー、CI依存と公開条件を確認した。生成後の不正属性が入力用422へ分類され得る点を固定500へ修正し、fault testを追加した。重大な未解決のローカル問題はなく、残る完了条件は公開後の両CIの確認と記録である。
