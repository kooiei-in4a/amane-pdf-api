# pdfcpu導入の検証記録（Issue #25・PR A）

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
