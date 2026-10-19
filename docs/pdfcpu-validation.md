# pdfcpu導入の検証記録（Issue #25・PR A）

現行の固定版は0.16.2。更新時の結果は末尾の「0.16.2 への更新の検証（Issue #82）」を参照。以下の#25導入時の版と測定値は当時の記録として保持する。

この文書はPR A時点（2026-10-09）の基盤導入記録です。下記にある「PR Bに残る」検証は記載時点の予定で、後続PRで実施済みです。共通描画基盤の実装・容量測定は[overlay-validation.md](overlay-validation.md)、現在の文書一覧は[docs/README.md](README.md)を参照してください。

2026-10-09 JST。基準mainは `4590c8032759c521bb690b2f9692701d60fadfb5`。
対象は取得・license・Docker/CI・起動検証・`PdfcpuProcessor`・日本語smokeである。
新しいHTTP endpoint、ページ属性の解析、overlayとBox復元、容量予約はPR B以降で実装する。
実装設計は [pdfcpu-design.md](pdfcpu-design.md) を参照する。

## 環境と取得物

hostはLinux x86_64、.NET SDK 10.0.400、qpdf 12.3.2。
hostのqpdfはUbuntuのAppArmorによる出力名制限を避けるため、CIと同じく配布binaryの一時コピーを使った。
pdfcpuはv0.16.1のLinux x86_64 Release、BIZ UDPゴシックはv1.051のRegular。
取得URL、archive・font・OFLのsha256は `third_party/pdfcpu.lock` に固定した。
Docker buildはcache optionを使わず、固定した公式URLから取得して成功した。

検証imageは `amane-pdf-api:issue-25-a`、local image IDは
`sha256:dd062314338cf915a5318ff0c63fd057a79e8807e1ec606f97e2fb5e8bbbc736`。
APIコンテナはCPU 1、memory 1.5 GiB、swapなし、tmpfs 256 MiB、non-root、read-only、cap-drop ALL、no-new-privilegesで実行した。
network noneのsmokeも成功した。このimageはregistryへ公開していない。

## 取得・licenseの確認

Docker・CI・開発環境は `scripts/install-pdfcpu.sh` を共有する。
同じinstallerへ、pdfcpu archive、font archive、OFLをそれぞれ改変したcacheを渡すと、すべて非0終了した。
展開したTTFの期待sha256と、収録licenseの内容をそれぞれ改変した場合も非0終了した。
これら5ケースでは導入先も一時取得ディレクトリも残らなかった。

`go version -m` はGo 1.27.1、pdfcpu v0.16.1、Linux binaryに含まれる9 moduleと固定version・Go checksumを返した。
`scripts/verify-pdfcpu-modules.py` でv0.16.1のgo.modにある11 moduleと照合した。
Linux binaryに含まれないjsonschema-goとWindows用mousetrapも、一覧とlicense収録の対象にした。
pdfcpu、BIZ、初期化で組み込まれるRoboto、Go module、Go runtime・標準ライブラリ・同梱資源について、license全文・著作権表示・必要なNOTICE/PATENTSを収録した。
final image内の全licenseのsha256と `THIRD_PARTY_NOTICES.md` がrepositoryと一致することをDocker smokeで確認した。

final imageの設定・font cacheはroot所有の0444、ディレクトリは0555、binaryは0555。
実行ユーザーappが設定とfont cacheを読み取れ、書き込めないことを確認した。
取得用Python/curl、検証用Go、取得archive、元TTFをpdfcpuのruntime追加物には含めていない。

## 共通処理と起動検証

`PdfcpuProcessor` はAPIが生成した白紙PDFと型付き文字modelを受け取る。
JSON/PDFの初期上限はそれぞれ8 MiB、描画fontは `BIZUDPGothic-Regular` に固定した。
文字をargvへ載せず、0600のJSONで渡す。JSONは予算内で生成できた場合だけfileへ書く。
任意JSON、font名、外部file参照、画像入力はmodelに含めていない。

pdfcpuにはoffline、明示した設定dir、job内の0700のHOME/XDG_CONFIG_HOME、固定のGo環境変数を渡す。
初期値はGOMEMLIMIT 200MiB、AS 1 GiB、CORE 0、FSIZEは実効PDF予算+1 byte。
出力の実サイズ、暗号化なし、qpdf検査、ページ数を確認する。
観測できる予算超過は専用例外とし、PR Bでoverlay用422へ変換する。
実pdfcpuがFSIZEで部分出力を削除しexit 1になるケースは、サイズ超過を証明できない内部障害として扱う。
文字系の最大入力でもこの失敗に届かないことの保証は、PR Bの必須検証である。

Linuxではhealthを含む全APIの待受前にpdfcpuと日本語fontの自己テストを実行する。
既存のメモリ自己テストと合わせた固定30秒のdeadlineを使い、HTTP用 `QpdfTimeoutSeconds` から独立させた。
pdfcpuの自己テストはversion、font一覧、白紙生成、短い日本語の描画、暗号化・構造・ページ数の検査の計7 processである。
設計時の6 processの試作測定にはページ数検査が含まれていないため、その値を本実装の起動時間とは扱わない。

新規43ケースを含む単体・統合テストで、次を確認した。

- 日本語、quote、改行を含むJSON、font固定、subset埋め込み、ToUnicode、JSONの同値/1 byte超過境界、0600/0700。
- `%`、不正UTF-16、配置・数値・色の不正入力、job外path、原本path、既存出力の拒否。
- 非0終了、出力なし、空/破損/暗号化/ページ数不一致の出力、実FSIZE失敗、実サイズで観測できる超過。
- 親の環境を変えないGo環境の上書き、AS/CORE/FSIZE/SIGXFSZの適用、不要なHOME内fileを生成しないこと。
- binary/config/font欠落、不適合AS、version違い、bounded stdout超過、描画失敗で専用例外を返し、内部情報をログへ出さないこと。
- Programのログ捕捉による専用固定メッセージの確認、メモリ自己テストとの分類、待受開始前の終了、cancel後のprocess treeとjobの削除。
- 先行するメモリ自己テストで約20秒使った場合も、共有deadlineにより起動全体が約30秒で終了すること。`CreateClient()` の例外だけでは判定していない。

## 全テストの時間と起動回数

同じhostとRelease構成で、Programが既存のメモリ自己テストへ進む直前の回数と、pdfcpu自己テストへ進む直前の回数を一時的に計数した。
直接呼び出す自己テストはこの起動回数に含めない。計数用の変更はrepositoryへ収録していない。

| 対象 | 全テスト | wall time | メモリ自己テスト開始 | pdfcpu自己テスト開始 |
| --- | --- | --- | --- | --- |
| 導入前（mainと既存設計書） | 600 PASS / 0 skip | 133.4秒 | 507回 | — |
| PR A | 643 PASS / 0 skip | 205.4秒 | 516回 | 508回 |

差の72.0秒には新規43ケースと30秒deadlineの故障テストが含まれ、pdfcpu自己テストだけの増加時間を表さない。
hostの全テストはCPU 1のDocker容量測定ではなく、任意入力の同時2件の時間を保証するものでもない。
計数を除去したRelease buildも、warning/errorともに0で成功した。

## Dockerと描画の確認

`scripts/docker-smoke.py` で既存のhealth/encrypt/unlock/optimize/merge/rotate/extract/split/compress/cleanと、各種失敗・後片付けのsmokeが成功した。
起動時のqpdf利用が増えたため、request故障用のqpdfは自己テストを通し、HTTP処理だけを失敗させるfixtureへ更新した。
新しいpdfcpu smokeでは固定日本語JSONを実描画し、qpdf検査、暗号化なし、1ページ、subset font・ToUnicode、設定書込み拒否、license一致を確認した。
pdfcpu binary/config欠落と小さいASでは、専用の固定ログを残して待受前に終了した。

hostにPoppler/Pillow/NumPyがないため、これらのtoolを持つ一時的な検証コンテナからCI用scriptを実行した。
製品imageにこれらのtoolは追加していない。検証driverから起動するAPIコンテナには上記の制限を適用した。

- `scripts/pdfcpu-validation.py`：日本語の文字抽出と、fontの埋込み/subset/Unicode対応がPASS。
- `scripts/compress-validation.py --tools`：実JPEG変換と必要optionがPASS。
- `scripts/compress-validation.py --render`：既存の構造・stream・Poppler描画比較が両圧縮levelでPASS。

日本語文字の位置・向きのpixel比較はPR Bで行う。設計時の空レイヤー6ページのpixel一致は、描画した文字の比較には数えない。
Box復元、文字系の最大入力で8 MiBと実効予算に届かないこと、上限規模の同時2件・各24秒以内・OOM 0・tmpfs予算・残存job/processの容量測定もPR Bに残る。
画像stamp（#29）と画像→PDF（#26）は各Issueで画像のbytes/画素数/生成物の予算を定め、同じ最大入力・容量・時間検証を行う。

## 再現手順とCIの範囲

READMEの開発手順でlocked toolを導入し、pdfcpu設定をexportしたうえで次を実行する。
Goはmodule照合に、Poppler/Pillow/NumPyはCI用描画比較にだけ必要である。

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build
python3 scripts/verify-pdfcpu-modules.py "$Pdf__PdfcpuPath"
docker build -t amane-pdf-api:ci .
python3 scripts/docker-smoke.py amane-pdf-api:ci
python3 scripts/compress-validation.py --tools
python3 scripts/compress-validation.py --render amane-pdf-api:ci
python3 scripts/pdfcpu-validation.py amane-pdf-api:ci
```

通常CIには取得物とmoduleの照合、Release build、全テスト、Docker build、既存APIを含むsmoke、日本語の文字抽出・font検査、既存の描画比較を含める。
CIのtimeoutは10分を維持する。ここに記録した205.4秒はhostの全テストの時間で、CI job全体の時間ではない。
remote CIは公開承認後にPRで確認する。PR Bの上限規模の容量測定は手動の別検証とし、通常CIとの範囲を区別する。
#46によるdeployの保留を継続する。


## 0.16.2 への更新の検証（Issue #82）

2026-10-19 JST。比較元はmain `87acad7`、更新後の製品コード・テストは `7f39030`。後続の文書commitは実行コードを変えない。[測定JSON](pdfcpu-0.16.2-results.json)にコマンド全体の秒数、module差分、CMap・描画ハッシュ、最大負荷の全stageを保存した。既存の測定JSONは書き換えていない。

### 取得物・依存物・CIのGo

公式 [v0.16.2](https://github.com/pdfcpu/pdfcpu/releases/tag/v0.16.2) のLinux x86_64 archiveを自分でダウンロードしてSHA-256を計算し、公式 [checksums.txt](https://github.com/pdfcpu/pdfcpu/releases/download/v0.16.2/checksums.txt) と一致した。

```text
d86bfdbbb4ed1d50ba5bafc923b42813154a64dada33595f9c136261cd108632  pdfcpu_0.16.2_Linux_x86_64.tar.xz
```

lockはversion・URL・member・sha256・go_versionを更新。font archive、OFL、font_sha256は変更なし。導入後TTFのsha256も `595dfefa96dcb281c40f5d560841f3fc623b0d9d12064481a7daf78044462673` に一致した。

`go version -m` はGo 1.27.1→1.27.2、pdfcpu v0.16.1→v0.16.2。既存11 moduleのversion・sum・組込み区分は同じで、`github.com/xdg-go/stringprep v1.0.4`（`h1:XLI/Ng3O1Atzq0oBs3TWm+5ZVgkq2aqdlvP9JtoZ6c8=`）だけが追加。go.modは11→12 module、Linux binaryは9→10 module。jsonschema-goとmousetrapは引き続き非組込み。新依存のApache-2.0原文とREADMEの著作権表示を収録し、go.mod・module一覧・追加licenseのchecksumも更新した。Go LICENSE/PATENTS・runtime vendor、pdfcpu本体・内部pkcs7/lzwのlicense原文は新しい公式sourceと一致。Robotoのname tableの著作権・ライセンス表示も同じだった。

CIの`setup-go: go-version: 1.26.0`は変更不要。実際にGo 1.26.0で両binaryのmetadataを読み、更新後のmodule検証もPASSした。Goは照合専用で、pdfcpuをコンパイルしない。上流go.modの最低版も1.26.0のまま。runtime imageへGoを追加していない。

上流の[全91 commit](https://github.com/pdfcpu/pdfcpu/compare/v0.16.1...v0.16.2)を確認し、重点は[ToUnicodeの修正](https://github.com/pdfcpu/pdfcpu/commit/71d4ddf60da8cbc47f3ff5e0d0352bbe31ee2664)、[cmap重複拒否](https://github.com/pdfcpu/pdfcpu/commit/817e882e44a6580d1ce84bcccb7a9ba3e442e535)、[WinAnsi幅](https://github.com/pdfcpu/pdfcpu/commit/079e87f38eaba25e3cf68557523ae0b082e3b5b0)。APIは自分で生成した白紙・画像をpdfcpuへ渡し、利用者PDFの検査・合成には既存qpdfを使う構成を維持する。

### 既存挙動の比較

| 確認対象 | 0.16.1 | 0.16.2 | 判定 |
| --- | --- | --- | --- |
| 固定BIZ UDPゴシック v1.051のinstall・読込み | 成功 | 成功。cmap format 4/12の重複拒否に当たらない | 変化なし |
| 差し込み文字表 | 11,425字、欠落・GID・幅不一致0 | 同左。BMP 63,423字の照合も一致（描画11,427字＝表＋共有GIDの2字） | 再生成不要 |
| 透かし文字表 | 21,498 scalar検査、10,051字描画、幅の過小評価0 | 同左 | 再生成不要 |
| 字形がない文字 | U+1F600・U+0378を黙って落とす | 同左（「日＋未収録2字＋本」が「日本」） | APIの文字表による事前拒否を維持 |
| `%p` / `%P` | 物理ページ番号 / 全ページ数へ置換 | 同左 | 禁止した並びを維持 |
| `%t` / `%v` | 時刻 / 0.16.1へ置換 | 時刻 / 0.16.2へ置換 | 版の値以外は変化なし |
| U+005Cの直後の`n` | 2行へ分割 | 同左 | 禁止した並びを維持 |
| 単独`%` | 要素とTjが消える | 同左 | 書換えを維持 |
| n個の`%`をn+1個で渡す | n=1,2,3,10,199で元どおり | 同左。APIの`%a`×100も抽出一致 | 変化なし |
| 負のpos.y | ページ中央扱い | 同左。12 ptで直接指定はbaseline 502.0 pt、pos.y=0＋負dyは1.44141 pt | #78の回避を維持 |
| baseline / pos.y | 上端−1802/2048 s / baseline−ceil(246/2048 s) | 同左。66 baseline＋下端6/6.1/12/72 pt×3揃え・最小ページで誤差0.00002 pt未満 | 変化なし |
| 送り幅 | hmtx×1000/2048の切捨て | 同左。Type0/Identity-HとCIDのWを照合 | WinAnsi変更による影響なし |
| create / import / stamp | 全既存検証PASS | 透かし・ページ番号・差し込み / from-images / 電子印鑑の全既存検証PASS | 利用者向けの修正不要 |

`fill-template-glyphs.py --check`は11,425 entries / 68,550 bytes一致、`watermark-glyphs.py --check`も成功（開発専用fontTools 4.67.0）。両生成表のgit差分は0。直接CLIの文字内容はToUnicodeの欠落に影響されないようTjのGIDから独立に復号した。

### ToUnicode（回避処理は保持）

追加した `scripts/pdfcpu-update-validation.py` が、製品imageのpdfcpuを直接呼んだ未修正PDFと、実HTTP APIで作った修正後PDFを比較する。本番コードへ切替を残していない。抽出はMuPDF（PyMuPDF 1.26.3）とPoppler 26.01.0の両方で、ページ内のUnicode正規化や空白除去をせず照合する。

| fixture | 0.16.1・回避なし | 0.16.2・回避なし | APIの回避あり |
| --- | --- | --- | --- |
| 99（99種、1ページ） | 全文字一致、宣言99 | 全文字一致、99 | 両版とも全文字一致・Type0=1 |
| 100（100種、1ページ） | 全文字一致、宣言100/1 | 全文字一致、100 | 両版とも全文字一致・Type0=1 |
| 101（101種、1ページ） | 100字形で打切り・不一致、宣言100/1 | 全文字一致、100/1 | 両版とも全文字一致・Type0=1 |
| 200（200種、1ページ） | 100字形で打切り・不一致、宣言100/1 | 全文字一致、100/100 | 両版とも全文字一致・Type0=1 |
| multiple-pages（338種、4ページ） | 100字形で打切り・不一致、宣言100/1 | 全文字一致、100/100/100/38 | 両版とも全文字一致・Type0=1 |

複数ページの338種は320種の漢字、結合濁点、`%`の末尾・連続・数字前、U+005C、①㈱￥※〒、`%a`×100を含む。旧版の宣言100/1は第2ブロックの実エントリが0という不正な余分のブロック。新版は100件ちょうどでも空ブロックを出さない。

新版のCMapをAPIが差し替えた後も、全fixtureでType0はちょうど1つ、bfcharの宣言数と実件数が一致、各1〜100件。旧版・新版の回避ありCMapの対応表ハッシュとMuPDF描画pixelハッシュは全ページで同じ。各版内の差し替え前後の描画も全ページ同じだった。APIのType0 0個/2個の拒否、毎回の参照番号取得、故障500・cancel/timeout・削除も既存テストでPASS。

最大負荷の所要時間・予約率と、削除案の影響範囲は [fill-template-validation.md](fill-template-validation.md) を参照。判断案は、Humanの承認後に別依頼で回避処理を外すこと。本PRでは保持する。

### 全コマンドの結果と時間比較

Linux x86_64、.NET SDK 10.0.400、qpdf 12.3.2、Python 3.14.4。同一ホストで比較元・更新後を順に実行した各1回のコマンド全体の秒数。比較元image名は`amane-pdf-api:issue-82-baseline`。ホストqpdfはCI同様の一時コピー。DockerのAPI・BuilderはCPU 1、1.5 GiB、swapなし、tmpfs 256 MiB、non-root、read-only、cap-drop ALL、no-new-privilegesを維持。

| コマンド | 0.16.1 秒 | 0.16.2 秒 | 更新後の結果 |
| --- | ---: | ---: | --- |
| `scripts/install-pdfcpu.sh <一時ディレクトリ>` | — | 2.775 | PASS |
| `python3 scripts/verify-pdfcpu-modules.py <一時ディレクトリ>/bin/pdfcpu` | 0.032 | 0.032 | PASS |
| `dotnet build --configuration Release` | 1.921 | 8.149 | PASS |
| `dotnet test --configuration Release` | 321.816 | 320.701 | PASS、1355件・skip 0 |
| `docker build -t amane-pdf-api:ci .` | 1.971 | 10.592 | PASS |
| `python3 scripts/from-images-followup-test.py` | 0.265 | 0.214 | PASS |
| `python3 scripts/docker-smoke.py amane-pdf-api:ci` | 52.081 | 52.024 | PASS |
| `python3 scripts/renderer-validation.py amane-pdf-api:ci` | 4.122 | 4.122 | PASS |
| `python3 scripts/redact-validation.py amane-pdf-api:ci` | 2.419 | 2.419 | PASS |
| `python3 scripts/compress-validation.py --render amane-pdf-api:ci` | 31.234 | 29.080 | PASS |
| `python3 scripts/pdfcpu-validation.py amane-pdf-api:ci` | 2.470 | 2.469 | PASS |
| `python3 scripts/overlay-validation.py amane-pdf-api:ci --render` | 4.125 | 4.126 | PASS |
| `python3 scripts/page-numbers-validation.py amane-pdf-api:ci` | 10.485 | 10.585 | PASS |
| `python3 scripts/watermark-validation.py amane-pdf-api:ci` | 46.132 | 45.080 | PASS |
| `python3 scripts/fill-template-glyphs-validation.py amane-pdf-api:ci` | 9.635 | 8.780 | PASS |
| `<venv>/bin/python scripts/fill-template-validation.py amane-pdf-api:ci` | 20.970 | 20.366 | PASS |
| `python3 scripts/from-images-validation.py amane-pdf-api:ci` | 4.727 | 4.774 | PASS |
| `python3 scripts/stamp-image-validation.py amane-pdf-api:ci` | 15.997 | 15.996 | PASS |
| `<venv>/bin/python scripts/pdfcpu-update-validation.py amane-pdf-api:ci` | — | 6.179 | PASS |

`fill-template-validation`と`pdfcpu-update-validation`は、system-site-packagesを参照しない新規venvへ `pip install --require-hashes --only-binary=:all: -r scripts/fill-template-validation-requirements.txt` で固定した全19依存を導入して実行した。`pip check`もPASS。新規の更新検証は同じCI stepへ追加。上表はCI docker jobの全検証stepを含む。通常のRelease testは両版とも1355件、skip 0。

installerは比較元も実行成功（時間未採取）。`--check`による両文字表の再生成照合もPASS。build・Docker buildのcache状態やホスト負荷が異なるので、時間差を更新だけの速度差とは解釈しない。比較driverの初回compress呼出しは引数順の誤りでexit 2だったが、CIと同じ`--render IMAGE`で再実行しPASSした（表は再実行値、JSONに初回も区別して記録）。

### 版表記の更新と過去記録の保持

| 扱い | 対象 |
| --- | --- |
| 現行版を更新 | README、docs/README、THIRD_PARTY_NOTICES、security.md、pdfcpu-design.mdの取得物・schema参照・module件数、fill-template-design.mdの現行構成と#82の運用 |
| 新しい結果を追記 | 本文書とfill-template-validation.mdの本節、新規pdfcpu-0.16.2-results.json |
| 既存節を当時の記録として保持 | pdfcpu-design.md末尾の設計時測定、本文書の#25、fill-template-validation.mdの#77/#78、fill-template-design.mdの判断履歴 |
| 日付・測定commitのある設計/検証を保持 | watermark-validation.md、from-images-validation.md、from-images-followup-validation.md、from-images-as-validation.md、stamp-image-design.md、stamp-image-validation.md、redact-design.md、redact-validation.md、roadmap.md |
| 過去のraw測定を保持 | 既存のdocs配下の測定JSONすべて（fill-template-capacity.json、*-results.json、redact-validation/*.json等）、stamp-image-design-probe/reproduce.sh、redact-design-probe/reproduce.sh |
| 同梱資源の取得元記録を保持 | Roboto-NOTICE.txtのv0.16.1由来という記載（著作権・ライセンス表示は新版でも一致） |

フォント・qpdf・.NET・APIの契約・各容量制限は変更なし。サイト、本家への投稿、回避処理の削除、merge・tag・Release・deployは対象外。
