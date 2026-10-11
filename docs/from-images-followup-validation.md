# 画像からPDFの配置前容量検証（#59）

2026-10-11 JST。[#59](https://github.com/kooiei-in4a/amane-pdf-api/issues/59)の画像処理とcompressの混在、16 bit・パレットPNG、standardの約48 MP・4:4:4 JPEGを合成データで測る。PR #58当時の測定とは分ける。実機配置・公開、[#46](https://github.com/kooiei-in4a/amane-pdf-api/issues/46)のcollector確認はこの作業に含まない。

## 測定対象と判定

変更前はmain `0c8aea1cf70f46273fe21a68be2a82edc0b51fca`、修正版は `4a24843608c8300ff0f220f276895bbaa62d8ed2`。Dockerfile・固定tool・Go soft limit・AS・並列数を維持し、16 bit PNGの見積りの画素係数だけを24から48へ見直した。

全HTTP測定は1条件ごとに新しいコンテナを使う。CPU 1、memory 1536 MiB、MemorySwap合計も1536 MiB（追加swapなし）、tmpfs 256 MiB、non-root、read-only、network none、cap-drop ALL、no-new-privileges。画像とcompressは同じ処理枠を共有する。2要求は送信前のbarrierで開始を合わせ、成功する同時2件では2 jobの重なりも観測する。通常設定は内部deadline 30秒。HTTP時間はアップロードと全応答の受信を含み、内部deadlineの残り時間に換算しない。

PNGの見積りは `E = 2×正規化PNG bytes + 画素数×(16 bitは48、8 bit以下は12) + 64 MiB`、上限320 MiB。グレー、グレー+tRNS、RGB、RGB+tRNS、グレー+alpha、RGBAの16 bitと、8 bitパレットのtRNS有無を、単色と固定seedの高複雑度に分ける。tRNSの高複雑度入力にも実際の透明画素を入れる。PNGは直接IHDR・画素・CRC・zlibを書き、Pillowによる16 bit RGBの8 bit化を避ける。PNGは非interlaceで、Adam7の大画像容量を検証した結果ではない。

Eの上限直前・直後はRGBA16と透過パレットPNGの高さを1行だけ変え、実際のencoded bytesを含めて決める。E以内の候補は200と正常PDFを要求し、超過は422 `unsupported-image`、成功ヘッダーと部分PDFなしを要求する。設計の必須合格対象である縦横12 MP・高複雑度RGBA8も両quality・単独/同時2件で再確認する。

JPEGは8064×6048（48,771,072 pixels）の4:4:4、単色RGB、quality 90、baseline/progressive、Orientation 1/6の4条件をstandardで測る。係数メモリと向き補正の条件を調べる入力であり、全実写真の時間の上界を示さない。別途、固定seedの高複雑度4:4:4 JPEG（4500×3800、quality 100、optimize）をstandard/originalで測る。同じJPEGをPDFへ埋め込み、入力約47.7 MiBのcompressをstandard/strongで画像処理と組み合わせる。各成功PDFはqpdfの構造・1ページを確認し、画像側ではA4・余白・寸法・透過を確認する。

## 16 bitの見積り見直し

変更前の高複雑度RGB16（4000×1860、入力44,677,863 bytes）とRGBA16（4000×1600、51,236,483 bytes）は旧Eの320 MiB以内だったが、pdfcpuのAS 1 GiB下で拒否された。RGB16には200/422の両方を観測した。RGBA16の制限付き単独toolでも、圧縮bufferの75,497,472 bytes確保に失敗してexit 2となった。これはtool内のメモリ確保失敗で、コンテナのOOM/oom_killとは区別する。

16 bitの係数を48へ変更し、同じ入力をimport前に拒否する。修正版の低複雑度は約5.58 MP、高複雑度は型に応じ約4.18〜5.14 MPの、Eが320 MiBに近い入力を測る。AS 1 GiBやGo soft limit 200MiBを増やさず、8 bitの係数12を維持する。48は実測に基づく受付見積りであり、RSS/ASの上界や任意入力の成功を証明しない。大きいグレー16 bitも含め、以前より受付範囲が狭くなる。

小さいPNGを使う追加4件のHTTP回帰テストでは、旧・新の見積りの間に予算を設定し、pdfcpuのpathを起動後に故意に存在しないものへ変える。16 bitはtool起動前の422、同じ寸法の8 bitは実toolで200となることとjob削除を確認する。大画像測定の代わりにはしない。

## 修正版の結果

通常設定の124コンテナ測定・194要求は、200が170要求、見積り超過による422が24要求で、すべて期待どおりだった。500/503/504、OOM/oom_kill、残存job/tool、終了後health失敗は0。全コンテナで後続の既存rotateも200。下表は単独/同時2件・両qualityを含む各群の最大で、列ごとに異なる測定が最大となることがある。

| 測定群 | 要求数・結果 | 最大HTTP秒 | cgroup peak MiB | tmpfs観測peak MiB |
| --- | --- | ---: | ---: | ---: |
| 見積り以内の16 bit PNG | 78件200 | 3.023 | 425.80 | 127.62 |
| 見積り以内のパレットPNG | 30件200 | 1.947 | 465.61 | 146.27 |
| 必須合格対象の12 MP高複雑度RGBA8 | 12件200 | 3.013 | 541.15 | 183.22 |
| 約48 MP・4:4:4 JPEG standard | 12件200 | 1.172 | 609.21 | 19.99 |
| 画像処理とcompressの混在 | 32件200 | 3.895 | 504.86 | 135.77 |
| 見積り超過PNG | 24件422 | 1.087 | 179.55 | 145.68 |

| 約48 MP JPEGの条件 | 単独HTTP秒 | 同時2件の遅い方のHTTP秒 | 同時2件cgroup peak MiB |
| --- | ---: | ---: | ---: |
| baseline・Orientation 1 | 0.297 | 0.543 | 71.04 |
| baseline・Orientation 6 | 0.330 | 0.546 | 71.05 |
| progressive・Orientation 1 | 0.611 | 1.172 | 609.21 |
| progressive・Orientation 6 | 0.617 | 1.136 | 608.66 |

この表以外に約47.7 MiBの高複雑度JPEGの単独/同時2件を6要求測り、すべて200。混在測定のcompressは全16要求でJPEGを1件再圧縮した。通常設定の全体最大HTTPは5.098秒、cgroup peak 609.21 MiB（制限1536 MiB）、tmpfs観測peak 190.625 MiB（制限256 MiB）、単一jobの観測割当量95.313 MiB（予算124 MiB）、最終PDF最大47.653 MiB（上限54 MiB）。観測した容量余裕はこの合成入力での結果であり、サンプリングで捕捉できなかった瞬間の値、全入力、異なるCPU/配置条件へ一般化しない。

短い期限の別測定は、約47.7 MiB JPEGのoriginalがHTTP 504・2.427秒で終了し、PDF/成功ヘッダーを返さず、残存job/tool 0、health/後続rotate 200、OOM/oom_kill 0だった。2.427秒はHTTP全体であり、内部の2秒予算と同一の尺度ではない。生データの`deadline_final`には通常設定の対照1要求もあり、上の194要求へ重複加算しない。

変更前の116コンテナ測定では15条件が候補の期待を満たさなかった。高複雑度16 bit RGB/RGBAのtool拒否と、tRNSを宣言していても透明画素のなかったRGB16入力のSMask期待を区別する。修正版は透明画素を持つfixtureでSMaskも確認した。変更前の数値を修正版の成功件数へ合算しない。

## 生データと再現

HTTP・cgroup・tmpfs・tool RSS/AS/FSIZE/COREの生記録、対象SHA、完全なDocker image ID、tool/driver版、fixture bytes・完全なSHA-256・seed、残存job/tool・health・既存rotateの結果は [from-images-followup-results.json](from-images-followup-results.json) に保存する。PDF/PNG/JPEG本体はリポジトリへ含めない。tmpfsとRSSは20 msのサンプリング値、cgroup peakはkernelの値。短いtoolを捕捉できなかった0を実処理時間0とは扱わない。loopback HTTPのbash/catは測定コンテナ内で動き、cgroup peakへ含まれる。

hostにPillow・NumPy・qpdfとDockerが必要。通常CIへ大画像を追加しない。

```bash
# 本PRの最終checkoutで実行。製品imageは測定対象のSHAから作る。
git worktree add --detach /tmp/issue59-measured-source 4a24843608c8300ff0f220f276895bbaa62d8ed2
docker build -t amane-pdf-api:issue59-final /tmp/issue59-measured-source
python3 scripts/from-images-followup.py --generate-only --fixtures /tmp/issue59-fixtures
sudo python3 scripts/from-images-followup.py amane-pdf-api:issue59-final \
  --target-sha 4a24843608c8300ff0f220f276895bbaa62d8ed2 \
  --fixtures /tmp/issue59-fixtures --output /tmp/issue59-results --mixed --timeout-check
```

短いdeadlineの追加確認は、有効な`QpdfTimeoutSeconds=2`と`CompressSoftTimeoutSeconds=1`を使う。`--timeout-check`で約47.7 MiB JPEGのoriginalを504にし、tool終了待ち・job削除・health 200・既存rotate 200を確認する。標準30秒の性能測定へ合算しない。再実行は新しいoutput dirを指定する。

変更前のfixture/driverはコミット `55b645e` の`--generate-only`で再現できる。測定imageのAPIコードはmain `0c8aea1`。生記録のfixture manifestのgenerator hashは予備測定のtool版取得処理修正前の値で、画素生成はこのコミットと同じ。別ディレクトリで28入力を再生成し、全入力のSHA-256一致を確認した記録もJSONへ保存した。fixtureごとの完全なハッシュを同一性の基準にする。

初回driverの1秒deadline例は、compress soft deadlineの21秒より短く起動時設定検査を通らないため、HTTP測定にはならなかった。driverを有効な2/1秒へ修正し、上記の別測定で確認した。これは製品コードの再変更ではない。124件の通常設定の生記録は保持し、起動しなかった例を504や正常な測定件数へ数えない。

## 通常検証

Release buildは警告0・エラー0。全755テストは成功、失敗/skip 0、所要3分16秒（追加の4ケースを含む）。Ubuntu hostのqpdf AppArmorによる拡張子なしのraw出力拒否は、既存CIと同じ配布バイナリの一時コピーをPATHと`Pdf__QpdfPath`へ指定して避けた。製品コードやhostのAppArmor設定は変更していない。固定pdfcpu/configは既存installerの検証用一時ディレクトリを指定した。TRXは`/tmp/issue59-test-results-20261011/issue59.trx`で、長期保存するCI成果物とは区別する。

修正版Docker buildと全smokeが成功。既存25 POST、compress両level、clean、splitのFSIZE実測、CORE/AS、read-only/non-root/network none、起動失敗・エラー非露出・job削除を確認した。大画像の124測定と通常smokeは別の検証である。

## 配置とサイトへの引き継ぎ

本測定の条件と実際のinfra条件を合わせるか、配置条件で再測定する。#46のcollector確認・CORE=0・容量条件整合とHuman承認を配置前に確認し、API配置後にサイトを公開する。

[サイト#41](https://github.com/kooiei-in4a/amane-tools-site/issues/41)には、16 bit PNGは比較的小さい画像でも制限にかかること、縮小または利用者側で8 bit PNG/JPEGへ保存し直せることを引き継ぐ。PNGは両qualityで同じ処理なので「標準」に変えるだけでは解消しない。JPEGの大きい4:4:4 originalの向き補正制約は継続する。422/504時は画像・順序・画質の選択を保持し、自動再送・画質変更・PNGのJPEG化を行わない。
