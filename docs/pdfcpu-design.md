# pdfcpuと日本語フォントの実装設計

2026-10-09 JST。対象はAPIリポジトリの [Issue #25](https://github.com/kooiei-in4a/amane-pdf-api/issues/25)、基準はmain `4590c8032759c521bb690b2f9692701d60fadfb5`。pdfcpuとBIZ UDPゴシックを導入し、APIが生成する描画レイヤーをqpdfで元のPDFへ合成する共通処理を用意する。新しいHTTP endpointは追加しない。ページ番号・透かし・電子印鑑・画像からのPDF作成は後続Issueで扱う。

以下はレビューを反映した実装案であり、アプリケーションへの導入はまだ行っていない。Issueには仕様と完了条件を置き、実装設計の正本はこのファイルに一本化する。Issue末尾の旧設計は削除し、このファイルの固定commitへ案内する。設計変更もこのファイルに反映して参照commitを更新する。infraとサイトは対象外とし、#46によるdeployの保留を維持する。

## 最新mainに合わせる変更

2026-10-07の初期案から、次の扱いを採用する。

| 項目 | 採用する扱い |
| --- | --- |
| 外部プロセス | `ExternalProcessRunner` はstatic。DIでrunnerを注入せず、現在の `ExternalProcessRequest` と `RunAsync` を使う |
| メモリ制限 | `ProcessMemoryLimits.CreateRequest` の環境変数dictionaryを受ける既存overloadを使う。pdfcpuのASはqpdfと別設定にする |
| 422の理由 | mainに `PdfInputException(reason)` はない。既存のunlock・cleanと同様、overlay用の型付き例外を追加する |
| ページ属性 | MediaBox・CropBox・Rotateは継承を解決する。UserUnit・TrimBoxはページ直下から読み、継承しない |
| 合成 | Boxの一時調整をoverlayと同じqpdf呼び出しで行い、合成後に実効値を復元する |
| Docker検証 | 存在確認に加え、日本語描画を#25で実行する。後続APIの追加まで延期しない |
| 読み取り専用設定 | `chmod -R a-w` だけでなく、ディレクトリ0555・ファイル0444を明示する |

既存endpointの入力判定とエラー形式は維持する。cleanの50,000 object上限はcleanに適用される値であり、その成功時間をoverlayの性能保証には使わない。

## 取得物と配置

pdfcpuは [v0.16.1の公式Release](https://github.com/pdfcpu/pdfcpu/releases/tag/v0.16.1)、フォントは [v1.051の公式Release](https://github.com/googlefonts/morisawa-biz-ud-gothic/releases/tag/v1.051) を固定する。対象platformはLinux x86_64とする。別architectureでは明示的に取得処理を失敗させる。

2026-10-09に公式配布物から確認したSHA-256は次のとおり。pdfcpu tar.xzはReleaseの `checksums.txt` とも一致した。フォントの値は取得した固定Release assetから計算したもので、独立した署名検証の結果ではない。

| 取得物 | SHA-256 |
| --- | --- |
| `pdfcpu_0.16.1_Linux_x86_64.tar.xz` | `757e57036e1da56b789d0243ba72b09be4f26b3ec88e781170896ac36ae04d51` |
| `BIZUDGothic.zip` | `30692df621b92df13b88f1360aed1ab6ae50de441bce751a396c6439045cd759` |
| `BIZUDPGothic-Regular.ttf` | `595dfefa96dcb281c40f5d560841f3fc623b0d9d12064481a7daf78044462673` |
| フォントrepoの `v1.051/OFL.txt` | `e753d7155d53c747d037a445e584c8ecfca6dd79846db610417e282a736b28bc` |

`third_party/pdfcpu.lock` にversion・完全なHTTPS URL・SHA-256・必要なarchive member名を保存する。実装時の値は上表と再照合する。チェックサムの比較対象はlockの固定値とし、更新され得るremoteのchecksumsファイルだけを信用してインストールしない。

`scripts/install-pdfcpu.sh <prefix>` をDocker・CI・開発環境で共有する。固定したキーだけを読み、`eval` は使わない。ダウンロードには失敗判定・timeout・回数を限定したretryを設け、checksum確認後に必要なmemberだけを展開する。展開先に既存の管理外ファイルがある場合は失敗させる。一時領域は成功・失敗とも削除する。

Dockerのtools stageで取得・フォント組み込みを行い、final stageへ次をroot所有でコピーする。curl・unzip・archive・元のTTFはfinal stageに残さない。

```text
/opt/amane-pdf/bin/pdfcpu
/opt/amane-pdf/config/pdfcpu/config.yml
/opt/amane-pdf/config/pdfcpu/fonts/BIZUDPGothic-Regular.gob
/opt/amane-pdf/config/pdfcpu/ ... 初期化時に作られる設定資源
/opt/amane-pdf/licenses/pdfcpu-LICENSE.txt
/opt/amane-pdf/licenses/BIZ-UDGothic-OFL.txt
/opt/amane-pdf/licenses/THIRD_PARTY_NOTICES.md
```

`pdfcpu -c <prefix>/config fonts install <ttf>` をビルド時だけ実行する。`-c` は `pdfcpu` ディレクトリの親を指す。設定の `offline` をtrueにし、実行時にも `--offline` を付ける。初期化でRobotoのfont cacheも作られるため、RobotoのApache-2.0全文と `Copyright 2011 Google Inc. All Rights Reserved.` の表示も収録する。BIZのzipにはOFL本文が入っていないため、上表の公式OFL原文をrepositoryへ保存する。原文の著作権表示は書き換えない。

noticeの対象はpdfcpu本体とfontに限定しない。[v0.16.1のgo.mod](https://github.com/pdfcpu/pdfcpu/blob/v0.16.1/go.mod) のrequire全体を、indirectを含む11 moduleの一覧として保存する。取得したbinaryの `go version -m` と照合して、実際に組み込まれたmodule、Go runtime・標準ライブラリ、font等の同梱資源のlicense全文・著作権表示を収録する。照合用Goは開発またはCIの検証toolとして使い、配布imageには追加しない。module一覧はnoticeと一緒に配布し、取得物の更新時にも再生成する。

設定ディレクトリは0555、設定とfont cacheは0444、バイナリは0555とする。実行ユーザー `app` で読み取り・実行ができ、書き込みができないことを確認する。取得やfont installをAPI起動時に行わない。

## 設定と起動時検証

Linuxではpdfcpuの導入を必須にする。#25ではpdfcpuを使うendpointがまだなくても、binary・font・設定・自己テストの不具合があれば、healthを含む全APIの待受を開始しない。この影響を受け入れ、toolが不完全なimageを提供しない方式を採用する。非Linuxの既存endpointは維持し、pdfcpu基盤の起動検証はLinuxだけで実行する。

| `PdfOptions` | 初期値案 | 検証 |
| --- | --- | --- |
| `PdfcpuPath` | `pdfcpu` | 空でない。起動時に実行して固定versionと照合 |
| `PdfcpuConfigDir` | 空。DockerとCIで明示指定 | Linuxでは絶対path必須。`pdfcpu/config.yml` と日本語font cacheが読み取り可能 |
| `PdfcpuMemoryLimit` | `200MiB` | 正の整数MiBのみ。byte換算でoverflowしないこととAS以下であることを確認 |
| `PdfcpuAddressSpaceLimitBytes` | `1,073,741,824` bytes | 正の値。実処理で起動・描画・制限適用を確認 |

`GOMEMLIMIT` はGo runtimeのsoft limitであり、RSSの厳密な上限ではない。AS 1 GiBも仮想アドレス空間の制限である。物理メモリの判定には制限付きDockerでの実測を使う。[Go GC guide](https://go.dev/doc/gc-guide#Memory_limit)

Programに起動全体の30秒deadlineを1つ作り、既存の `ProcessMemoryLimits.ValidateStartupAsync` と新しい `PdfcpuProcessor.ValidateStartupAsync` へ同じtokenを渡す。既存の自己テスト内のtimeoutも、この親deadlineを超えられないようにする。pdfcpuではversion、font一覧、固定の短い日本語を描く自己テスト、qpdf出力検査を行う。version/font一覧だけはそれぞれ4 KiB/64 KiBのbounded stdoutで比較し、上限超過は起動失敗にする。表示用にその内容をログへ出さない。

自己テストも0700のjobを使い、終了後に削除する。pdfcpuの失敗は専用の `PdfcpuStartupException` にまとめ、Programで既存のInvalidOperationExceptionより先にcatchし、「pdfcpu・日本語フォントの自己テストに失敗しました。」という別の固定メッセージとexit 1で終了する。qpdf/JPEGの既存メモリ自己テストのメッセージと、設定値不正のメッセージは維持する。stderr・内部path・inner exceptionはログに残さない。全Linuxテストでこの起動条件が必要になるため、CIだけでなくREADMEの開発手順にも同じinstallerと環境設定を示す。

起動のたびに自己テストを実行し、process全体のstatic cacheで省略しない。PR Aでは全テストの実行時間と起動回数を記録し、CIの10分timeoutに収まることを確認する。設計時に測った追加の自己テストの時間は末尾に記載する。

## 共通処理の責務

新しい型はAPI assemblyのinternalとする。外部から任意のpdfcpuコマンドやJSONを受け取る入口は作らない。

```csharp
internal sealed class PdfcpuProcessor(IOptions<PdfOptions> options)
{
    internal const string JapaneseFont = "BIZUDPGothic-Regular";

    internal Task CreateAsync(TemporaryPdfFiles files, PdfcpuLayer layer,
        string blankPath, string outputPath, long jsonBudget, long pdfBudget,
        CancellationToken token);
}

internal sealed class PdfOverlayBuilder(QpdfProcessor qpdf,
    IOptions<PdfOptions> options)
{
    internal Task BuildAsync(TemporaryPdfFiles files, int from, int to,
        Func<OverlayCanvas, CancellationToken, Task> draw,
        CancellationToken token);
}
```

`OverlayCanvas` は白紙path、レイヤー出力path、ページごとの表示寸法、JSONとPDFそれぞれの書き込み予算を持つ小さいrecordとする。描画JSONの固定pathは `TemporaryPdfFiles.PdfcpuLayerJsonPath` としてPR Aで追加する。Builderがdrawの前にJSONとPDFの両方を予約し、drawの後に実サイズを照合する。drawはDIから得たPdfcpuProcessorへcanvasのpathと予算を渡す。同じjob内の生成済みpathだけを使い、容量を制限しない任意の書き込みは許さない。レイヤーに元のPDFを使うことは禁止する。PdfcpuProcessorはBuilderやOverlayCapacityへ依存しない。

`PdfcpuLayer` はページ番号をキーとする `pages → content → text[]` の型付きmodelとする。文字の `value`、`anchor` または位置、`dx` / `dy`、fontのsize・col、`rot` だけを入力modelに持たせる。font nameは自由入力にせず、JSON writerが常に `JapaneseFont` を書く。pdfcpuの任意JSON、URL、外部ファイル参照、画像取得、フォーム要素を透過させない。schemaは [v0.16.1のTextBox](https://github.com/pdfcpu/pdfcpu/blob/v0.16.1/pkg/pdfcpu/primitives/textBox.go) と [Content](https://github.com/pdfcpu/pdfcpu/blob/v0.16.1/pkg/pdfcpu/primitives/content.go) に合わせる。

利用者の文字は0600のJSONにだけ書く。argvに載せない。`%` はpdfcpuの置換文字として扱われるため、利用者の文字に含まれる場合は生成前に拒否する。ページ番号は後続Issueで計算した数字そのものを渡す。文字数・描画要素数・サイズ・回転角・色・配置名は各APIの入力検証で制限し、共通処理でも有限値と許可した要素を確認する。画像stampやimportの利用者向け仕様は#29/#26で追加する。

pdfcpuのprocess requestは現在のfactoryで作り、`with { WorkingDirectory = files.DirectoryPath }` を設定する。引数の先頭は `-c <ConfigDir> --offline`。runnerは親の環境を引き継ぐため、pdfcpu requestで次を明示して上書きする。qpdf/JPEGや親の環境は変更しない。

| 環境変数 | pdfcpuへ渡す値 |
| --- | --- |
| `HOME` / `XDG_CONFIG_HOME` | job内の空のhomeディレクトリ |
| `GOMEMLIMIT` | 検証済みのPdfcpuMemoryLimit |
| `GOGC` | `100` |
| `GODEBUG` | 空文字。binaryの既定設定を使う |
| `GOMAXPROCS` | `1`。CPU 1の予算に合わせ、hostでも同じ条件にする |
| `GOTRACEBACK` | `none` |

`PDFCPU_CONFIG_ROOT` が親にあっても明示した `-c` が優先されることを、[v0.16.1の設定解決](https://github.com/pdfcpu/pdfcpu/blob/v0.16.1/pkg/api/configuration.go#L94) と実toolテストで確認する。存在しないHOME/XDGや異なるGo設定を親へ与えるテストを設け、job外への書き込みがないことを確認する。AS・CORE=0・SIGXFSZ無視・FSIZEを適用し、shellを介さず `ArgumentList` を使う。通常のstdout/stderrは破棄する。

pdfcpuは既存の出力pathを拒否する。JSONは `CreatePrivateFile` で作るが、pdfcpu出力を事前に空ファイルとして作らない。job内の新しい固定名にだけ出力し、既存ファイルを上書きする `--force` は使わない。pdfcpuの新規出力は0666とumaskからpermissionが決まるため、生成中は0700のjobで遮断し、成功後はLinuxの `File.SetUnixFileMode` で0600へ揃えてから検査する。失敗時はjobごと削除する。[v0.16.1の出力処理](https://github.com/pdfcpu/pdfcpu/blob/v0.16.1/pkg/api/file.go)

## ページ属性の取得

`PdfPageBoxes` はqpdf JSON v2の `pages` と `qpdf` を1回の呼び出しで取得する。`--json-stream-data=none --decode-level=none` を指定し、stream本文を.NETへ取り込まない。入力は先に `ValidateAsync` とページ数検査を通す。

JSON bytes・深さ・object数を制限し、参照はobject番号とgenerationを含む完全なkeyで索引化する。値の参照連鎖とParent連鎖に深さ上限32・循環検出を設ける。長いループでもtokenを確認する。qpdfのJSON形式が壊れている場合は500、有効なqpdf JSON内の未対応ページ属性は422とする。

MediaBoxは必須、CropBoxの省略/nullはMediaBox、Rotateの省略/nullは0。これらは各属性を独立にParentから解決する。UserUnitの省略/nullは1、TrimBoxの省略/nullはCropBoxであり、Parentの値を使わない。この継承範囲は [qpdf 12.3.2のgetAttribute](https://github.com/qpdf/qpdf/blob/v12.3.2/libqpdf/QPDFPageObjectHelper.cc) と合わせる。

Boxは4つの有限数、正の幅・高さを要求する。実効CropBoxはMediaBoxとの交差とし、交差が空ならunsupported-pdf。逆転したBoxや範囲外の数値を勝手に修正しない。UserUnitの対応範囲は0より大きく75,000以下の有限数、Rotateは整数で90の倍数とし、配置計算用には0/90/180/270へ正規化する。qpdf 12.3.2の配置処理は `/Rotate -90` や `630` をそのまま渡すと0相当で扱うため、Box調整JSONでも正規化は必須である。復元用には正規化前の整数値も保存する。物理寸法は実効CropBoxの幅・高さにUserUnitを掛け、90/270なら入れ替える。計算後の各辺が有限かつ0より大きく14,400 pt以下であることを要求し、範囲外はunsupported-pdfにする。巨大ページの対応拡張は実測後に別途判断する。

元のページ直下のMediaBox・CropBox・TrimBox・Rotateについて、キーの有無、null、解決後の値と元の実効値を保存する。UserUnit・BleedBox・ArtBoxの比較用実効値も保存するが、これらのキーは更新しない。復元時の参照先object番号は書き換えで変わるため、古い参照文字列を出力PDFへコピーしない。必要なBox値は意味が同じ直接配列へ復元する。元のバイナリやobject番号の保持は約束しない。DOMから得たJsonElementをdispose後まで保持せず、属性は小さい数値modelへ移す。ページ辞書の更新JSONはDOMが有効な間に書き終える。

MediaBox・CropBox・Rotateの復元規則は次の1つに統一する。入力に有効な直下値があった場合は、解決した元の直下値を戻す。直下値がなかったかnullだった場合は、調整で追加したキーを除いた候補辞書を作り、**合成後のParent連鎖とPDFの既定値で元の実効値が得られる場合だけ**キーを除去する。一致しなければ元の実効値を直下へ書く。元のnullやキーの有無より、元の実効値の保持を優先する。

候補の評価はMediaBox、CropBox、Rotateの順に行い、CropBoxのfallbackは復元済みのMediaBoxを使う。候補にMediaBoxが見つからない場合は入力不正として打ち切らず、「一致しない」として元の実効値を直下へ復元する。Rotateの復元値と比較値は元の整数値を使い、正規化は合成時の配置に限定する。Parent連鎖が循環・上限超過・不正な型なら、その段階の対応するエラーにする。元の継承値がParentから消えた場合にも、MediaBoxを欠落させない。

TrimBoxは継承されない。元の有効な直下値を戻し、省略/nullなら復元済みCropBoxへfallbackさせるため調整キーを除去する。BleedBox・ArtBox・UserUnitは調整も復元もしない。合成でobject番号が変わっても、それらの実効値は保持検査の対象とする。

## Boxを揃えてから合成する

qpdf 12.3.2のCLI overlayは、配置先と元ページのForm XObject生成にTrimBoxを使う。その元ページFormをMediaBoxへ配置するため、CropBoxとMediaBoxの中心が異なる場合に元の本文も移動し得る。TrimBoxがCropBoxより小さい場合は、通常の表示範囲内の内容もFormのBBoxで切れる。元本文の通常の見た目を守るため、対象ページのBoxを合成の直前に一時調整する。[qpdfの実装](https://github.com/qpdf/qpdf/blob/v12.3.2/libqpdf/QPDFJob.cc)

1. 入力を検証し、全ページ数と対象の連続範囲 `from..to` を確認する。対象ページの元の属性と実効CropBoxを保持する。同じ入力metadataのobject番号を使い、対象ページ直下のMediaBox・CropBox・TrimBoxを実効CropBoxへ揃え、Rotateを正規化する調整JSONを作る。ページツリーと非対象ページの辞書は更新しない。
2. 対象ページだけの白紙PDFをqpdf `--json-input` で作る。MediaBoxは `[0 0 表示幅 表示高さ]`、UserUnitは1、Rotateはなし、Resourcesとcontentは空とする。JSON headerには `jsonversion: 2`・`pdfversion: 1.7`、trailerにはRootと正しいSizeを入れる。
3. pdfcpu `create <layer.json> <blank.pdf> <layer.pdf>` で描く。描画レイヤーのページ数が対象数と一致することとqpdf検査の成功を確認する。
4. `qpdf input.pdf --update-from-json=adjust.json overlaid.pdf --overlay layer.pdf --to=<from>-<to> --from=1-z --` で、一時調整とoverlayを1回の読み込み・書き出しで行う。`normalized.pdf` は作らない。
5. **合成後の**qpdf JSONから対象ページの新しいobject番号・辞書・Parent連鎖を取得する。現在のContents・Resources・その他の参照を保持した辞書へ、前節の統一規則でBox・Rotateを復元する。qpdf `--update-from-json` で最終出力を作る。
6. 最終出力の非暗号化・`--check` exit 0・ページ数一致を確認する。対象ページの実効Box・UserUnit・Rotateが入力と同じことも検査する。中間物を削除してから出力を返す。

各段階で終了code 0だけを成功とする。部分更新で置換するのは対象ページ辞書であり、streamを.NETで解釈・再圧縮しない。しおり・リンク・フォームの保持は構造テストと描画テストで確認する。qpdfが元本文をFormへ包み直すため、Contents/Resourcesの構造やbytesの同一性は要求しない。

qpdfのmanualはoverlayを後段で処理することを説明しており、[12.3.2の処理順](https://github.com/qpdf/qpdf/blob/v12.3.2/libqpdf/QPDFJob.cc#L426) はJSON更新、ページ操作、overlayの順である。この順序を実toolテストで固定する。qpdf更新時にも同じテストを必須とする。直接overlayとの差は復元の書き出し1回となり、初期案から最大54 MiBの中間PDFと書き出し1回を削減する。入力サイズ・object数・対象ページ数の上限で同時2件を測り、共有30秒に収まることを実装の完了条件に含める。[qpdf 12.3 manual](https://qpdf.readthedocs.io/en/12.3/cli.html#overlay-and-underlay)

## 表示範囲外の内容

対象ページの元本文はForm XObjectに包まれ、そのBBoxは一時調整した実効CropBoxになる。通常のCropBox表示は保持するが、出力のCropBoxを後から広げたり削除したりしても、元のCropBox外にあった本文描画はFormのBBoxで切り取られ、表示されない。この制約を許容する。非対象ページへはこの合成を行わない。

切り取られた内容は元のstream内に残る。文字抽出toolによっては拾われるため、この処理を黒塗り・情報除去として扱わない。CropBox外の目印を持つfixtureで、通常表示の一致、CropBox拡張後の非表示、stream内の残存を検査する。TrimBoxがCropBoxより小さいfixtureでは、元の表示範囲内の内容が欠けないことも検査する。BleedBoxとArtBoxは変更・復元せず、元の値を保つ。

## 容量と時間の予算

次は実装時に測定する初期値案であり、性能検証済みの値ではない。

| 制限 | 初期値案 |
| --- | ---: |
| 入力PDF | 既存の50 MiB |
| qpdf metadata JSON | 各32 MiB、JSON深さ64 |
| 入力metadataの間接object | 50,000 |
| 生成後metadataの間接object | 75,000 |
| 対象ページ | 1,000 |
| 白紙・描画JSON・部分更新JSON・レイヤーPDF | 各8 MiB |
| overlaid / 最終PDF | 各54 MiB |
| job全体の割当容量 | 124 MiB |
| 処理全体 | 既存の共有30秒 |
| API同時実行 | 既存の2、待ちqueue 0 |

生成後はレイヤー由来のobjectが増えるため、入力と別のobject上限を設ける。32 MiB制限も常に適用する。上記のJSON・PDF・job上限と対象ページ数をoverlay用設定として追加し、object数・深さ・描画modelの許可範囲は共通処理の定数にまとめる。cleanやcompressの設定を流用しない。

`OverlayCapacity` とbounded managed streamを追加する。現在のCleanCapacityの割当計算を参考に、4 KiBへ切り上げた容量をpathごとに予約し、入力・JSON・生成物・外部tool出力をすべて数える。clean固有の例外を返す既存型は流用せず、全機能に及ぶcapacityの再設計も行わない。

書き出し前に `min(そのファイルの上限, job残容量から安全に使える予算)` を計算する。外部toolはFSIZEを予算+1にし、そのsentinel分の割当も予約する。実サイズが予算を超えれば、終了codeが0でもtoo-complex。managed JSONは書き込み前に上限を検査する。終了codeが126/127、出力不在、空、その他の異常終了は500。ファイル削除後に予約を解放する。

pdfcpuは書き込み失敗時に部分出力を削除する。FSIZE到達でも出力不在・exit 1になることがあり、サイズを観測できない失敗は500にする。stderrの文言だけでtoo-complexを推測しない。事前の容量予約による拒否と、実サイズで証明できる超過は422とし、この違いをfault testで固定する。FSIZEによるディスク容量の制限はどちらの場合にも有効である。

処理の順序と削除時点を固定して、複数の大きいPDFを残し続けない。

| 段階 | 残す大きいファイル | 次に削除するもの |
| --- | --- | --- |
| 入力metadata取得 | input、metadata JSON | parse後のJSON |
| 白紙と描画 | input、調整JSON、blank、layer、描画JSON | 白紙JSONはblank生成直後。描画・レイヤー検査後にblankと描画JSON |
| 調整とoverlay | input、調整JSON、layer、overlaid | input、調整JSON、layer。元属性は制限したmodelで保持 |
| 復元metadata取得 | overlaid、metadata JSON | parse後のJSON |
| Box復元 | overlaid、復元JSON、output | overlaidと復元JSON |
| 最終検査と送信 | output、検査JSON | 検査JSON。outputは送信完了後にjobごと削除 |

実効予算が不足すれば422で終了し、出力の上限を超えて書かせない。124 MiB×2は248 MiBであり、256 MiB tmpfsの残りをjobディレクトリ等に使う。予期しないpdfcpuの補助ファイルが増えないことも測定で確認する。JSON DOMとbyte bufferは必要な段階だけ保持し、外部tool起動前に大きいDOMをdisposeする。

tokenは呼び出し元から1つ受け取り、qpdf・pdfcpu・JSON生成・参照解決・後片付けへ引き継ぐ。各toolごとに30秒を与え直さない。将来のHTTP endpointは既存 `PdfEndpointHelpers.ExecuteAsync` と同じconcurrency limiterを使い、upload後に始まる共有deadline内でBuilderを呼ぶ。#25の共通処理内に別queueや追加の並列tool実行を作らない。

## エラーの扱い

`PdfOverlayInputException` に `TooComplex` / `UnsupportedPdf` のenumを持たせる。Problem Detailsへの変換は `PdfEndpointHelpers` の専用catchで固定titleとreasonを出す。後続endpointで同じ形式を使えるよう#25で用意し、既存の `PdfInputException` とcleanのcatchは変更しない。

| 原因 | 扱い |
| --- | --- |
| 既存のPDF入力検証失敗 | 従来の422 |
| JSON/object/深さ/参照循環/対象ページ/jobの予算超過、検出した生成物の容量超過 | 422 `too-complex` |
| 対応範囲外のBox・Rotate・UserUnit・物理寸法 | 422 `unsupported-pdf` |
| 不正な対象範囲、文字列・配置の入力規則違反 | 後続endpointの400。共通処理の契約違反を利用者へ公開しない |
| qpdf JSON破損、生成済み白紙に対するpdfcpu失敗、無効な生成PDF、ページ数不一致 | 固定メッセージの500 |
| 共有deadline | 将来のHTTPでは既存の504 |
| 利用者cancel・app停止 | 既存のabortとjob削除 |

PDF本文、文字列、JSON内容、ファイル名、toolのstdout/stderrをエラーやログに入れない。文字入力の`%`拒否は後続endpointの400に対応する入力検証であり、tool異常の500と混同しない。

## 変更ファイルと実装順序

| ファイル | 変更 |
| --- | --- |
| `third_party/pdfcpu.lock`、`scripts/install-pdfcpu.sh`、`third_party/licenses/` | 固定取得、検証、font組み込み、配布notice |
| `Dockerfile`、`.github/workflows/ci.yml` | 共通installer、final配置、CIの必須tool設定 |
| `PdfOptions.cs`、`Program.cs`、`ProcessMemoryLimits.cs` | 設定検証、DI、共有起動deadline、pdfcpu専用の固定エラー。request factoryの既存overloadを維持 |
| `PdfcpuProcessor.cs`、描画model | 型付きJSON、制限付き実行 |
| `PdfPageBoxes.cs` | bounded metadata読取、属性の解決、元属性の保存 |
| `PdfOverlayBuilder.cs`、`OverlayCapacity.cs`、`QpdfProcessor.cs` | Box調整・白紙・描画・overlay・復元・検査、容量付きqpdf書き出し |
| `PdfEndpointHelpers.cs` | overlay固有の422 reasonを固定形式へ変換 |
| `tests/Amane.Pdf.Api.Tests/` | runner/設定/geometry/容量/実tool/後片付けのテスト |
| `scripts/docker-smoke.py`、描画・容量検証script | 日本語描画、既存test assemblyによる実Builderの制限付きDocker検証 |
| `README.md`、`docs/security.md`、`THIRD_PARTY_NOTICES.md`、検証記録 | 導入・更新・対応範囲・制限・実測値 |

srcのファイルは `src/Amane.Pdf.Api/` 配下とする。qpdf書き出しhelperはoverlay用の型付き失敗を返し、clean用 `RunCleanWriteAsync` の流用による誤ったエラーtitleを避ける。

## PRの分割

同じIssueの対応を次の2つのPRに分ける。両方を完了してから#25を閉じ、後続の描画APIへ進む。

| PR | 範囲と検証 |
| --- | --- |
| A | 取得script・lock・license/module一覧、Docker/CI、Pdfcpu設定、共有起動deadlineと専用エラー、PdfcpuProcessorと文字model・JSON予算、固定白紙による日本語smoke、README/security/notice。Release build・全テスト・起動時間・Docker描画を確認 |
| B | Aを土台にPdfPageBoxes、Builder、復元規則、調整とoverlayの統合、OverlayCapacity、overlay用422、geometry/保持/境界/faultテスト、Poppler比較、既存test assemblyでの容量測定と記録。Release build・全テスト・既存API smoke・同時2件の上限検証を確認 |

AのprocessorはpathとJSON/PDF予算だけを受け取り、Bのcanvasやcapacityに依存させない。Aの自己テスト用白紙は固定の1ページとし、任意入力のページ属性解析はBで実装する。Aは `Refs #25`、BはAの完了を確認したうえで `Closes #25` とする。AだけではIssue全体を完了扱いにしない。merge・tag・deployはこの設計更新では行わない。

## 検証と完了条件

CIではinstallerを実行し、`Pdf__PdfcpuPath` と `Pdf__PdfcpuConfigDir` を全テストへ渡す。Linuxの.NETテストで必須となるqpdf/pdfcpu/fontの不足は失敗にし、Inconclusiveにしない。非Linuxの既存APIテストとLinux専用検証は区別する。

Popplerは.NETテストの必須toolに含めず、描画比較scriptの依存にする。通常の開発環境の.NETテストはPopplerなしでも行列と構造を検査する。描画比較はCIの専用stepで必須実行し、toolが不足すれば失敗する。開発者が描画比較を選ぶ場合はREADMEの手順でPopplerを導入し、同じscriptを実行する。未実行の描画検証をPASSに数えない。配布imageにはPopplerを追加しない。

| 検証 | 確認内容 |
| --- | --- |
| 取得 | binary/archive/TTF/licenseのchecksum不一致で非0。Dockerとhostが同じversion・fontを使用 |
| 起動 | version違い、font/config欠落、読み取り不可、不正設定、不適合ASで待受前に失敗。固定logだけが残る |
| 実行request | `-c`、offline、GOMEMLIMIT、HOME/XDG、Go環境の上書き、working directory、AS/CORE/FSIZE/SIGXFSZが実processへ適用される。親の環境は変わらない |
| 文字の受け渡し | 日本語、quote、改行、先頭 `-`、`,` がJSON経由で渡りargv/logに出ない。`%`は拒否、fontはJapaneseFont固定 |
| 属性と復元 | 継承とoverride、間接配列/数値、null/default、合成後のParentが元の値を持つ場合の除去、値が失われた場合の直下への復元、UserUnit/TrimBox非継承、負/360超のRotate、循環・深さ・型・有限値・交差 |
| geometry | portrait/landscape、Rotate 0/90/180/270、負のorigin、偏ったCropBox、TrimBoxがCropBoxと異なる場合、UserUnit 0.5/2、混在寸法 |
| 合成の順序と保持 | 調整JSONがoverlayより先に適用され、入力のobject番号で更新できること。元本文の表示位置、画像、しおり、Link、フォーム値・annotation、非対象ページ、ページ数、Box/Rotate/UserUnit、BleedBox/ArtBox |
| 表示範囲外 | CropBox外の内容のstream残存と拡張後の非表示、TrimBox外かつCropBox内の内容の通常表示保持。情報除去として扱わない |
| 境界 | object 50,000/50,001、対象ページ1,000/1,001、生成後object上限、JSON/生成物/FSIZE/jobの直前・同値・超過 |
| 故障 | exit 3を含む非0、126/127、空/破損/暗号化/warning出力、ページ数不一致、壊れたJSONを返さない |
| lifecycle | 各stageでtimeout/cancel/app停止、親子processのkill・wait、job削除。将来のHTTP接続時にも共有枠の解放を検証 |

配置行列の.NETテストに加え、専用scriptでPopplerによる描画を比較する。originalと最終出力を同じ条件で描画し、新しい文字を置いた領域以外の本文が動かないことを確認する。文字抽出とsubset埋め込みも確認する。画像の単純なPDF内bytes比較や、`qpdf --check` だけでは見た目の保持を判定しない。

Docker smokeはfinal imageをnon-root・read-only・network noneで起動し、version・font・license、固定の日本語JSONによる実描画、qpdf検査、設定への書き込み拒否を確認する。CLI smokeは配置の確認、hostの実toolテストは共通C#処理の確認として、それぞれを報告する。API endpointを検証用に追加しない。

容量測定には既存の `Amane.Pdf.Api.Tests` を使い、製品assemblyのInternalsVisibleToは増やさない。測定テストへ `OverlayCapacity` categoryとDoNotParallelizeを付け、1つのtest内でBuilderを単独または2件同時に呼ぶ。既存のテストDLL・依存物・fixture・content rootとSDKのVSTest runnerをread-only mountし、final image内のdotnetで `vstest.console.dll` を実行する。runnerもテストも配布imageには収録しない。

CPU 1・memory 1.5 GiB・swapなし・tmpfs 256 MiB・cap-drop ALL・no-new-privilegesの条件で、入力bytes、metadata bytes/object、対象ページ、描画要素数が上限に近い組合せを単独と同時2件で測る。入力配置と起動自己テストは処理timerの前に行い、Builderの処理開始から最終検査・削除までを各jobで測る。test driverのメモリもcgroupのpeakに含まれることを記録する。結果/TRXと一時的なCLI homeは/tmpへ出し、API jobの残存とは分けて数える。

初期値を確定する条件は、正常ケースが両方成功し、各処理24秒以内、OOM/oom_kill 0、tmpfs予算内、残存job/process 0とする。memory.peak、tmpfs観測最大、各stage時間、出力bytesを検証記録へ残す。超過時は対象ページ・生成物の上限や処理回数を調整して再測定し、30秒予算を延長して通さない。cleanの19.4秒という値は流用しない。

Release build、全テスト、Docker build、既存APIを含むsmoke、上記の描画・容量検証、PRのCIとセルフレビューを通して#25を完了する。手動の容量測定とCI smokeの範囲は検証記録で明記する。

## 設計時の確認結果

2026-10-09、固定Release binary `0.16.1` / commit `2f7a89a0` / Go `go1.27.1`、qpdf `12.3.2` で確認した。

- `GOMEMLIMIT=200MiB`、AS 544 MiBではversion確認がexit 2となり、Go runtimeが初期化できなかった。AS 1 GiBではversion確認と4ページの日本語 `create` が成功した。1 GiBは実装開始時の候補であり、上限規模での成功は容量測定で確認する。
- font install後の `config.yml` は0600だった。単に書き込み権限を除去すると0400となり、別UIDの `app` は読めなかった。0444・ディレクトリ0555へ設定すると描画できた。
- 日本語レイヤー出力へFSIZE 4,097 bytesを適用するとpdfcpuはexit 1となり、部分出力を削除した。stderrを破棄する実行方式では、終了後の出力サイズだけでこの容量超過を識別できない。
- 既存の `amane-pdf-api:issue-24-limit` imageに検証binary/configをread-only mountし、non-root・read-only・network none・CPU 1・memory 1.5 GiB・swapなし・tmpfs 256 MiBで、日本語描画と `qpdf --check` exit 0、font一覧の `BIZUDPGothic-Regular (13932 glyphs)` を確認した。これは新しいDockerfileのbuild/smokeの代替ではない。
- MediaBox `[0,0,600,800]`、CropBox `[100,200,500,700]`、Rotate 0の直接overlayでは、元本文のForm配置が `1 0 0 1 0 -50 cm` となった。一時調整・復元後は `1 0 0 1 0 0 cm` だった。Rotate 90/180/270、TrimBox `[150,250,450,650]`、UserUnit 2を含む4ページでも、元本文のForm Matrixと配置matrixの合成が単位行列になることを確認した。最終PDFはqpdf検査を通った。
- 初回のgeometry確認は行列と構文によるものだった。レビュー後の描画比較は次節に記載する。注釈・フォームの全保持、上限規模の性能は実装時の検証に残る。

## レビュー後の追加確認

同日のqpdf 12.3.2で、調整JSONとoverlayを同じ呼び出しへ統合した。Rotate 0/90/180/270とUserUnit 2を含む4ページがexit 0・check 0になり、元本文のForm Matrixと配置matrixの合成が単位行列になることを確認した。別の6ページfixtureではRotate 0/90/180/270/-90/630を使い、合成後に元の整数値へ復元した。

検証用imageへPoppler 26.01.0を追加して72 dpi・RGB・CropBox表示で比較すると、空の描画レイヤーを合成した6ページは元PDFとpixel単位で一致した。fixtureのTrimBoxはCropBoxより小さく、TrimBox外かつCropBox内にも矩形を置いた。直接overlayした出力では各ページ1,200 RGB bytesが異なり、調整を統合した出力では一致した。

CropBox外に置いた矩形は、元PDFのCropBoxをMediaBoxへ拡張すると黒、合成後のPDFを同様に拡張すると白になった。同じ領域の `OUTSIDE` という合成文字列はForm stream内に残り、Popplerのpdftotextでも抽出された。通常表示の保持と、後から表示範囲を広げられない制約を別々に確認した。BleedBox/ArtBoxや注釈・フォームの全保持はPR Bのテストに残る。

継承MediaBox/CropBox/Rotateを持つfixtureでは、合成後のParentが元の値を保持している場合に調整キーを除去してcheckを通した。Parentから値が失われた状況も合成fixtureへ部分更新で作り、元のMediaBox/CropBoxをページ直下へ復元してcheckと実効値一致を通した。

既存のmainと同じAPI/testコードでbuild済みのtest assemblyを、read-only・network none・CPU 1・memory 1.5 GiB・swapなし・tmpfs 256 MiBのfinal runtime imageへmountした。SDKのVSTest runnerをread-only mountして `Healthz_ReturnsHealthy` 1件を実行し、成功・skip 0を確認した。これは実Builderの容量測定の実施結果ではなく、既存test projectを利用できることの確認である。

### 起動自己テストの時間

現在のExternalProcessRunner・ProcessMemoryLimitsのrequest factory・TemporaryPdfFilesをそのまま利用した試作で、追加する自己テストを測った。1ページの固定白紙と短い日本語を使い、pdfcpu version、font一覧、qpdf白紙生成、pdfcpu create、qpdf非暗号化確認、qpdf checkの6 processを順に実行した。JSON作成とjob削除を含む。各環境の初回3回をwarmupとして除外した。

| 条件 | 測定回数 | 中央値 | p95 | 最大 | 合計 |
| --- | ---: | ---: | ---: | ---: | ---: |
| host、.NET SDK 10.0.400 | 30 | 83.05 ms | 93.79 ms | 96.15 ms | 2.524秒 |
| final runtime image、CPU 1・memory 1.5 GiB・swapなし・tmpfs 256 MiB | 194 | 83.93 ms | 99.64 ms | 181.11 ms | 16.442秒 |

全試行が成功し、残存jobは0だった。ASはqpdf 544 MiB、pdfcpu 1 GiB、FSIZEは各8 MiB、GOMEMLIMITは200MiB。HOME/XDGはjob内、GOGC 100・GODEBUG空・GOMAXPROCS 1で実行した。

追加で、親のXDG_CONFIG_HOME/PDFCPU_CONFIG_ROOTを存在しないpath、GOGC/GODEBUG/GOMAXPROCS/GOTRACEBACKを異なる値にした5回の試行も、明示したconfigとGo設定で成功し、job残存は0だった。

mainの生成箇所はPdfTestContext 189、WebApplicationFactoryの直接利用5の合計194か所だった。ただしDataRow等によって1か所が複数回実行されるため、194は全suiteの起動回数ではない。この値は追加するprocess処理の試作時間であり、現在のqpdf/JPEG自己テスト、WebApplicationFactoryの構築、.NET process開始、全suiteの所要時間を含まない。PR Aで実装後の全suiteの起動回数と前後の実時間を記録する。

この設計で着手する際は、PR Aで起動・描画の基盤を導入し、PR Bで統合した合成と復元の実toolテストを固定して、上限規模の測定で制限値を確定する。
