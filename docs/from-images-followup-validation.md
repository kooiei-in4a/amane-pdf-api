# 画像からPDFの配置前容量検証（#59）

> **訂正・再レビュー中:** 観測VmSizeとAS上限の差を「AS余裕2.89%」とした説明を撤回する。VmSizeはGoの予約とサンプリングの影響を受け、実際の必要ASを示さない。12 MPのAdam7 PNGでは、見積り内でも非決定的な422が報告された。下記は非interlaceの履歴であり、#59の完了・配置判断には使わない。[AS別の反復検証](from-images-as-validation.md)の結果を受け、Kooの判断で取込専用ASは採用せず、共通AS 1 GiBのままAdam7を見積りで扱う（案A）。#46・サイト#41へ更新済みとした記載も誤りだったため、再測定前に注意書きを反映した。

2026-10-11 JST。[#59](https://github.com/kooiei-in4a/amane-pdf-api/issues/59)の画像処理とcompressの混在、16 bit・パレットPNG、standardの約48 MP・4:4:4 JPEGを合成データで測る。PR #61のレビューを受け、8 bit RGB/RGBAの境界も追加した。実機配置・公開と[#46](https://github.com/kooiei-in4a/amane-pdf-api/issues/46)のcollector確認は別作業である。

## レビューを受けた変更と根拠

初回提案 `4a24843608c8300ff0f220f276895bbaa62d8ed2` は16 bitの画素係数を一律48、8 bit以下を12としていた。追加測定では、この見積り以内のRGB8（14.8 MP）・RGB8+tRNS（14.8 MP）・RGBA8（13 MP）で変換中の422を再現した。8コンテナ・12要求中、200は4、422は8だった。期待200に対する失敗として保存し、成功した測定に合算しない。コンテナOOMとは別の失敗である。この3入力は修正版generatorの`oversized-rgb8-high`、`oversized-rgb8-trns-high`、`oversized-rgba8-high`と完全なSHA-256が一致する。追加の低複雑度22.3 MP RGBA8は`png(path, 4000, 5575, 8, 6, True, False, 6100)`で再生成できる。

固定pdfcpuの単独試験でも、13.4 MPのRGB8/tRNSで75,497,472 bytesの圧縮buffer確保失敗を観測した。RGB8は同じ入力で成功/失敗の両方を観測した。一方、12.192 MPのRGBA8、16.76 MPの高複雑度グレー16、22.3 MPの低複雑度グレー16は各2回成功した。この予備試験はHTTP測定の代わりにしない。

修正版の製品コードは `c3b4c9d3d0c77a3c1a0ba53e0fdfe2bf2a040259`。AS、Go soft limit、同時実行数、Dockerfile、tool版を維持し、型別の受付見積りを使う。入力のチャネル数だけでなく、[GoのPNGデコーダ](https://github.com/golang/go/blob/go1.26.0/src/image/png/reader.go)が実際に作る画素を基準にする。透過なしグレー16は1チャネル×2 bytes、RGB16/RGBA16・グレー16+tRNS/alphaは4チャネル×2 bytesに展開される。[固定pdfcpuの画像処理](https://github.com/pdfcpu/pdfcpu/blob/v0.16.1/pkg/pdfcpu/model/image.go)は展開画素とPDF用buffer・圧縮bufferを持つ。

正規化PNGの実サイズをB、画素数をPとし、上限は320 MiB。係数は実測から決めた受付用の値で、RSS/仮想メモリの上界ではない。

| PNGの型 | 見積りE |
| --- | --- |
| 16 bit・透過なしグレー | `64 MiB + 2B + 12P` |
| その他の16 bit | `64 MiB + 2B + 48P` |
| 8 bit以下・非パレットのカラー/透過付きグレー | `64 MiB + max(2B + 14P, 22P)` |
| その他の8 bit以下（透過なしグレー、パレット） | `64 MiB + 2B + 12P` |

16 bitの画素係数は展開チャネル数×サンプル幅2 bytes×6である。8 bitカラーは圧縮PNGが小さくても、画素bufferを過大に受け付けない下限を設ける。高複雑度RGB8の失敗を防ぐには、係数12→14だけでは足りなかった。透過なしグレー8とパレットは従来の保守的な係数を維持する。16 bitグレー+tRNS/alphaは4チャネルへ展開されるため、透過なしグレーの緩和を適用しない。

高さが1行違う境界入力を実サイズで確認した。幅4000 pxの場合、透過なしグレー16の低複雑度は22.360/22.364 MP、高複雑度は16.772/16.776 MP、RGB8/tRNSと低複雑度RGBA8は12.200/12.204 MP、高複雑度RGBA8は12.196/12.200 MPが受付直前/直後である。RGBA16の低複雑度5.588/5.592 MPと透過パレット19.168/19.172 MPも確認する。型・encoded bytesによって境界は変わり、寸法だけの成功保証ではない。

## 測定条件とdriverの検証

1条件ごとに新しいコンテナを使う。CPU 1、memory 1536 MiB、MemorySwap合計1536 MiB（追加swapなし）、tmpfs 256 MiB、non-root、read-only、network none、cap-drop ALL、no-new-privileges。pdfcpuはAS 1024 MiB、GOMEMLIMIT 200MiB、GOGC 100、GOMAXPROCS 1、CORE 0を維持する。APIの同時2枠を既存PDF処理と共有し、送信前のbarrierで開始を合わせる。成功する同時2件では2 jobの重なりも要求する。HTTP時間はアップロード・全応答受信を含み、内部30秒deadlineの残り時間に換算しない。

PNGは16 bitの型を保ってIHDR・画素・CRC・zlibを直接生成する。グレー/RGBのtRNS、グレー+alpha、RGBA、パレットの透過有無、低/高複雑度、縦横12 MP RGBA8を含む。非interlaceの測定であり、Adam7大画像の容量検証ではない。境界探索は1本の圧縮streamのprefixとcompressorのコピーを使い、PNG全体を行ごとに再生成しない。確定した隣接2入力だけを保存し、その実サイズでも境界をassertする。

JPEGは8064×6048（48,771,072 pixels）の単色4:4:4・quality 90、baseline/progressive、Orientation 1/6をstandardで測る。全実写真の処理時間の上界ではない。別途、固定seedの高複雑度4:4:4 JPEG（4500×3800、quality 100、optimize、約47.7 MiB）を両qualityで測り、同じJPEGを埋め込んだPDFのcompress standard/strongと画像を同時に送る。混在には、受付超過RGBA8の422とcompressの200の組も含める。

driverは次も確認する。

- 測定専用のread-only shell wrapperがpdfcpuのexec直前に固定1 byteを追記する。起動時自己テスト分を消してから数え、PNG見積りによる422は起動0回、成功画像は要求数と同じ起動回数をassertする。20 msのprocess観測だけで「起動しなかった」と判断しない。wrapperは測定コンテナだけにbind mountし、製品imageへ追加しない。
- 混在の期待statusはfixture manifestから取得し、画像側を200へ固定しない。
- JSONでないエラー本文でもHTTP status・Content-Type・応答bytes/hashを先に保存する。JSON不正は検証エラーとして残し、通信エラーに置き換えない。本文そのものは保存しない。
- 同時toolのVmSize/RSSは各processのサンプリング最大を記録する。旧生データの`tool_address_space_headroom`は観測値と上限との差であり、必要ASや実際の余裕として解釈しない。AS別の反復失敗率で評価する。
- 成功PDFはqpdf構造・1ページ・A4/余白・PNG寸法/SMask・JPEG向きを検査する。拒否/504は成功ヘッダー・部分PDFなし、全条件でprocess/job削除・health・後続rotateを確認する。

## 修正版の結果

通常設定は188コンテナ・292要求で、210件200、82件422 `unsupported-image`、全条件が期待どおりだった。500/503/504、OOM/oom_kill、残存job/tool、health失敗は0。全コンテナで後続rotateが200だった。

| 測定群（単独/同時2件・両quality） | 要求数・結果 | 最大HTTP秒 | cgroup peak MiB | tmpfs観測peak MiB | pdfcpu観測最大VmSize MiB | 上限と観測値の差（余裕ではない） |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 透過なしグレー16 | 24件200 | 2.251 | 439.08 | 128.02 | 929.77 | 9.20% |
| その他の16 bit PNG | 66件200 | 2.774 | 427.84 | 127.62 | 929.83 | 9.20% |
| パレットPNG | 30件200 | 2.039 | 462.32 | 146.27 | 992.65 | 3.06% |
| 8 bitカラーPNG（12 MPと境界） | 36件200 | 2.949 | 542.31 | 186.18 | 994.40 | 2.89% |
| 約48 MP・4:4:4 JPEG standard | 12件200 | 1.240 | 609.31 | 19.99 | 734.99 | 28.22% |
| 画像とcompressの混在 | 36件200・4件422 | 3.917 | 465.48 | 146.95 | 800.44 | 21.83% |
| PNG見積り超過（混在を除く） | 78件422 | 1.275 | 196.19 | 162.06 | 起動0回 | — |

この表以外に約47.7 MiB JPEGの単独/同時2件を両qualityで6要求測り、すべて200だった。表の各列の最大は同じ測定とは限らない。混在のcompressは20要求すべてでJPEGを1件再圧縮した。PNGの見積り超過は単独/同時2件の78要求と混在の4要求すべてでpdfcpuの起動0回をassertした。成功画像190要求では起動190回だった。

| 約48 MP JPEGの条件 | 単独HTTP秒 | 同時2件の遅い方のHTTP秒 | 同時2件cgroup peak MiB |
| --- | ---: | ---: | ---: |
| baseline・Orientation 1 | 0.306 | 0.477 | 70.74 |
| baseline・Orientation 6 | 0.297 | 0.508 | 67.76 |
| progressive・Orientation 1 | 0.625 | 1.168 | 607.04 |
| progressive・Orientation 6 | 0.624 | 1.240 | 609.31 |

全体のHTTP最大は5.199秒、cgroup最大609.31/1536 MiB、tmpfs観測190.629/256 MiB、単一job観測98.457/124 MiB、出力最大47.653/54 MiB。pdfcpuの観測最大VmSizeは994.402 MiBだったが、AS上限1024 MiBとの差29.598 MiB（2.89%）を実際の余裕として扱えない。失敗する割当て自体はVmSizeへ反映されず、Goは上限に応じて予約を変えるためである。必要ASを少数回の二分探索で確定せず、ASごとの反復回数・失敗数を残す。コンテナの物理メモリ（cgroup）と仮想アドレス空間（AS）の上限を区別し、どちらか一方だけを配置可の根拠にしない。

短い期限は別の1コンテナ・1要求で504、HTTP 2.337秒、成功ヘッダー/部分PDFなし、OOM/oom_kill 0、残存job/tool 0、health/後続rotate 200だった。HTTP全体の2.337秒を内部2秒予算と同じ尺度にしない。通常292要求には合算しない。

## 生データと再現

[生データ](from-images-followup-results.json)の `review_final` が型別見積りの今回の測定、`review_baseline` が8 bit境界の失敗再現、`review_probes` が固定toolの予備試験である。`baseline` はmain `0c8aea1` の初回測定、`final` / `deadline_final` は一律48の初回提案の記録であり、今回の成功件数へ合算しない。初回提案は通常124条件・194要求（170件200、24件422）、cgroup最大609.21 MiBだったが、8 bitの受付境界が不足していた。

対象SHA、完全なDocker image ID、tool/driver版、script hash、fixture bytes・完全なSHA-256・seed、HTTP・cgroup/tmpfs・tool RSS/VmSize/AS/FSIZE/CORE、残存job/toolとhealth/rotateを保存する。PNG/JPEG/PDF本体はリポジトリに含めない。tmpfsとprocessメモリは20 msのサンプリング値、cgroup peakはkernel値。捕捉できなかった瞬間の値や短いtoolの処理時間を保証しない。loopback HTTPのbash/catもcgroup peakに含まれる。

hostにPillow・NumPy・qpdfとDockerが必要。大画像は通常CIへ追加せず、driverの小さい回帰テスト2件をCIで実行する。

```bash
git worktree add --detach /tmp/issue59-measured-source c3b4c9d3d0c77a3c1a0ba53e0fdfe2bf2a040259
docker build -t amane-pdf-api:issue59-review /tmp/issue59-measured-source
python3 /tmp/issue59-measured-source/scripts/from-images-followup-test.py
python3 /tmp/issue59-measured-source/scripts/from-images-followup.py \
  --generate-only --fixtures /tmp/issue59-review-fixtures
sudo python3 /tmp/issue59-measured-source/scripts/from-images-followup.py amane-pdf-api:issue59-review \
  --target-sha c3b4c9d3d0c77a3c1a0ba53e0fdfe2bf2a040259 \
  --fixtures /tmp/issue59-review-fixtures --output /tmp/issue59-review-results --mixed --timeout-check
```

output dirは再実行ごとに新しくする。短いdeadlineは有効な `QpdfTimeoutSeconds=2` / `CompressSoftTimeoutSeconds=1` を使い、通常設定とは別に記録する。初回driverの1秒例は起動時設定検査を通らず、HTTP測定ではなかった。初回fixtureはコミット `55b645e` から28入力を再生成し、すべてSHA-256が一致した記録も保存した。旧PR #58の30枚測定の完全な入力hashと生ログは未回収であり、回収済みとは扱わない。

## 通常検証と配置への引き継ぎ

Release buildは警告0・エラー0。全760テストが成功、失敗/skip 0、所要3分15秒。追加のHTTP4ケースは、8 bitカラーが存在しないpdfcpuのpathへ到達する前に422となり、同じ予算で透過なしグレー16が実toolで200になることを確認する。sanitizerの追加1ケースはグレー16のtRNS/alphaに4チャネル分の予算が必要なことを検証する。従来の16 bit import前拒否4ケースも成功した。driver回帰2件は非JSON時のHTTP証拠保存と、prefix探索の境界と実PNGサイズの一致を確認する。

hostのqpdf AppArmorによる拡張子なしのraw出力拒否は、既存CIと同じ配布バイナリの一時コピーをPATHと`Pdf__QpdfPath`へ指定して避けた。製品コードやhostのAppArmor設定を変更していない。TRXは`/tmp/issue59-review-test-results/issue59-review-all.trx`で、CI成果物とは別のローカル記録である。

修正版Docker buildと全smokeが成功した。既存25 POST、compress両level、clean、splitのFSIZE実測、AS/CORE、read-only/non-root/network none、起動失敗・エラー非露出・job削除を確認した。大画像の189条件と通常smokeは別の検証である。

製品AS/Go soft limit、時間、入力/出力容量、同時2枠、cleanupは変更していない。Adam7は見積りに展開画像1枚分を加えてimport前に422とし、取込専用ASは採用しない（[AS別の反復検証](from-images-as-validation.md)）。合成入力の成功を任意入力・別infra条件へ一般化しない。collector確認、infraのCORE=0/容量条件整合とHumanの配置承認は引き続き必要である。

[サイト#41](https://github.com/kooiei-in4a/amane-tools-site/issues/41)と#46へ、旧結果を最新仕様として使わない注意書きを先に反映した。案Aの結果を両Issueへ引き継ぐ。READMEは8 bitのカラー・透過付きグレー、低bitグレー+tRNS、Adam7の受付範囲を明記し、縮小または利用者側でJPEGへ保存する案内とした。8 bit化だけで解消するとは案内しない。PNGでは「標準」に変えるだけでは解消しない。422/504でも画像・順序・画質の選択を保持し、自動再送・自動画質変更・PNGのJPEG化を行わない。採用API版の配置後にサイトを公開する。
