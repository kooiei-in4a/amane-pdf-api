# qpdf共通メモリ制限の実測（Issue #42 / PR #43）

2026-10-08のHuman判断に従い、標準コンテナをmemory **1.5 GiB**、memory-swap **1.5 GiB**（swapなし）へ変更しました。初期値は `QpdfAddressSpaceLimitBytes=570425344`（544 MiB）、`QpdfJpegMemory=600M`（600,000,000 bytes）です。

## 条件と測定方法

- 着手時にfetchし、PR head `bb954c17876fa8bf55ccff883beefc1ff7654e81` とorigin/main `8ed8a5cca19cff60d1875c996e61f2fbc6475a25` を確認。同じPRのheadへ修正。
- Ubuntu 26.04 runtime image、.NET 10.0.12、qpdf 12.3.2、libjpeg-turbo8 2.1.5-4ubuntu4、util-linux 2.41.3-3ubuntu2.2。
- non-root、read-only、network none、cap-drop ALL、no-new-privileges。CPU 1、tmpfs /tmp 256 MiB、同時処理2。cgroup `memory.swap.max=0` を確認。
- 実APIへのloopback HTTP。写真/Flateはoptimize、50 MiB付近はoptimizeとmerge。標準負荷210要求はAS/JPEGMEMの環境変数を上書きせず、実装の初期値で実行。
- kernelの `memory.peak` / `memory.events`、ホストから20 msごとにAPI/qpdfの `/proc` RSS/HWM・VmSizeとtmpfs使用量を取得。短いprocessや瞬間的なpeakはサンプリングで見逃し得る。
- dotnet-counters 10.0.750501でmanaged heap、GC committed、working setを1秒ごとに測定。CSVのMBは1,000,000 bytesなので、本書ではMiBへ換算しています（[.NET RuntimeEventSourceの単位](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Diagnostics/Tracing/RuntimeEventSource.cs)）。監視・診断はコンテナ外で実行。
- qpdf単体の比較は同じnon-rootコンテナで `qpdf --check` を直接実行。JPEGMEM未指定は `env -u JPEGMEM`、指定時はenv経由。ASはprlimitで付け、5 msごとにRSS/HWM・VmSizeを観測。APIのHTTP負荷と区別して記録。
- fixtureは一時領域で合成。Pillow 12.3.0、NumPy 2.5.3、品質85、滑らかなRGB色面と細かな乱数texture。実カメラ写真コーパスではありません。JPEG headerで寸法、sampling、progressiveを確認。
- 24 MPは6000×4000、約48 MPは8064×6048。それぞれbaseline 4:2:0、baseline 4:4:4、progressive 4:2:0を明示して生成。12 MP（4000×3000）はbaseline 4:2:0だけ。
- 100 MPグレー（10000×10000）はbaseline/progressive、100 MPカラーはbaseline 4:2:0。早期拒否比較用の225 MPカラーprogressive 4:2:0（15000×15000、39,322,703 bytes）も生成。
- A4 Flateは4961×7016のRGBを595.28×841.89 ptへ配置（600dpi相当）、44,023,268 bytes。50 MiB付近は49個の固有1024×1024グレー画像streamを持つ51,389,831 bytes（約49 MiB）。大きなファイルをRepositoryへ入れません。
- 成功出力を対象コンテナ外のqpdfでcheck。stdout/stderr、画像、PDF、JSON本文は検証ログへ出さず、固定ケース名、status、時間、サイズ、resource量だけを記録。

## 確認済み：標準設定の実HTTP

各行は同時2要求。RSSはqpdf 1 processの観測HWM。cgroupにはAPI、qpdf、tmpfs、HTTP送信用の短いbash/cat等も含みます。MiBは1,048,576 bytes。

| 入力・操作 | HTTP | cgroup peak MiB | tmpfs最大 MiB | qpdf RSS MiB | qpdf VmSize MiB | HTTP最大秒 |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 12 MP baseline 4:2:0（2,429,778 bytes） | 200 / 200 | 138.8 | 9.3 | 60.9 | 92.7 | 0.322 |
| 24 MP baseline 4:2:0（4,844,176 bytes） | 200 / 200 | 190.3 | 13.9 | 89.2 | 132.6 | 0.518 |
| 24 MP baseline 4:4:4（7,952,224 bytes） | 200 / 200 | 215.5 | 30.3 | 95.0 | 138.4 | 0.863 |
| 24 MP progressive 4:2:0（4,211,505 bytes） | 200 / 200 | 314.6 | 16.1 | 156.7 | 200.0 | 0.982 |
| 48 MP baseline 4:2:0（9,828,262 bytes） | 200 / 200 | 464.7 | 37.5 | 217.6 | 320.2 | 1.184 |
| 48 MP baseline 4:4:4（16,149,503 bytes） | 200 / 200 | 500.8 | 61.6 | 229.4 | 332.2 | 1.483 |
| 48 MP progressive 4:2:0（8,540,794 bytes） | 200 / 200 | 737.9 | 32.6 | 354.5 | 457.3 | 1.618 |
| A4・600dpiカラーFlate（44,023,268 bytes） | 200 / 200 | 404.3 | 167.9 | 179.1 | 251.7 | 17.471 |
| 約49 MiBのoptimize | 200 / 200 | 234.8 | 196.1 | 13.1 | 22.9 | 3.259 |
| 約49 MiBのmerge | 200 / 200 | 249.9 | 196.1 | 13.3 | 23.0 | 3.388 |
| 100 MPグレーbaseline（26,124,793 bytes） | 200 / 200 | 468.2 | 99.7 | 215.4 | 302.0 | 1.540 |
| 100 MPグレーprogressive（23,213,222 bytes） | 200 / 200 | 815.8 | 88.6 | 400.8 | 487.2 | 2.674 |
| 100 MPカラーbaseline 4:2:0（20,130,620 bytes） | 422 / 422 | 616.0 | 38.4 | 283.1 | 408.0 | 1.423 |
| 48 MP progressive＋225 MP progressive | 200 / 422 | 381.7 | 45.7 | 354.4 | 457.3 | 1.146 |
| 48 MP baseline 4:4:4＋100 MPカラーbaseline | 200 / 422 | 552.4 | 50.0 | 282.7 | 408.0 | 1.389 |

正常と超過の混在でも固定422 title・reasonなしを確認。破損・warning・上限超過は区別していません。422になったqpdfのVmSizeはAS設定値まで増えるとは限りません。次の大きなallocationが失敗する場合があります。

## 確認済み：tmpfs圧力下の継続負荷

各形式ごとに常駐APIで同時2要求を30回（60要求）、計180要求。2入力＋2出力と一時ファイル用の余裕を残してtmpfsを事前使用しました。各返却PDFを外側のqpdfで確認し、終了後のhealth、job directory削除、qpdf残存なしも確認。

| 48 MPの形式 | tmpfs事前使用 MiB | HTTP | cgroup peak MiB | tmpfs最大 MiB | qpdf RSS / VmSize MiB | API RSS MiB | heap / GC committed MiB | HTTP最大秒 |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline 4:2:0 | 210 | 60件すべて200 | 701.1 | 247.5 | 217.7 / 320.2 | 108.8 | 12.23 / 13.46 | 1.433 |
| baseline 4:4:4 | 186 | 60件すべて200 | 719.9 | 247.6 | 229.7 / 332.2 | 108.5 | 11.72 / 13.27 | 1.481 |
| progressive 4:2:0 | 215 | 60件すべて200 | 982.7 | 247.6 | 354.8 / 457.3 | 109.2 | 12.14 / 13.65 | 1.875 |

全標準ケース210要求で `memory.events` のmax、oom、oom_kill、oom_group_killは0。tmpfsの圧力用ファイルはコンテナ削除で破棄しました。

## 初期値の判断と余裕（見積り）

ASを制限しないqpdf単体の最大VmSizeは、対象写真では48 MP progressiveの457.3 MiB。実APIでも同じ値を観測しました。追加の100 MPグレーprogressiveを含めた標準成功ケースの最大は487.2 MiBです。観測最大へ少なくとも10%を加え、16 MiB単位で切り上げる考え方で544 MiBを選びました。対象写真へ86.7 MiB（19.0%）、追加の100 MPグレーへ56.8 MiB（11.7%）を残します。旧設定の約3.8 MiBの余裕から増やしました。AS設定値はRSSの実測値ではありません。

同じ48 MP progressive・JPEGMEM 600Mのqpdf単体比較ではAS 384/448 MiBでexit 3、480/512/544/576 MiBで0。今回のfixtureで480 MiBが通ることだけを基準にせず、観測VmSizeと余裕から設定を決めています。

```text
コンテナmemory ≥ .NET API ＋ tmpfs上限 ＋ 同時処理数 × qpdfのAS上限 ＋ 余裕
1536 MiB ≥ 128 MiB ＋ 256 MiB ＋ 2 × 544 MiB ＋ 64 MiB
```

.NET APIの観測RSS最大109.2 MiBを128 MiBへ切り上げ、約18.8 MiBの余裕を含めています。managed heap最大12.23 MiB、GC committed最大13.65 MiB。RSSからGC committedを引いたnative＋共有mapping等の粗い見積りは約95.2〜95.5 MiBです。別指標・別時点のpeakなので、正確なnative allocationやmanaged residentの分離ではありません。

全標準負荷のcgroup最大982.7 MiBと1.5 GiBとの差は約553.3 MiB。tmpfsは観測247.6 MiBに対して上限256 MiBを式へ入れ、qpdfも観測RSSではなくASの設定上限を2 process分計上します。さらに64 MiBを全体の余裕として見積りました。

128 MiBと64 MiBは容量計画用の見積りで、.NETへのhard limitではありません。RSSとAS、共有ページ、各項の最大値の時刻は異なり、式とcgroup peakは一致しません。任意のPDFや将来のruntimeでの成功・OOM回避を保証する式ではありません。AS、同時処理数、tmpfs、runtimeを変える場合は再測定し、コンテナmemoryも合わせて増やしてください。

## JPEGMEMの判断とASによる拒否との比較

AS 768 MiBのqpdf単体で24/48 MP progressiveを比較しました。

| JPEGMEM | 24 MP progressive | 48 MP progressive |
| --- | ---: | ---: |
| 64M | exit 3 | exit 3 |
| 128M | 0 | 3 |
| 160M / 200M / 400M / 600M | 0 | 0 |
| 未指定（ASなしの比較） | 0 | 0 |

この写真だけなら160Mでも通ります。しかし100 MPグレーprogressiveはAS 544 MiBで処理できてもJPEGMEM 200Mではexit 3、400M/600M/未指定では0でした。したがって写真の最小成功値を既定値にせず、標準AS全体570,425,344 bytesより大きい600,000,000 bytesへ切り上げました。JPEG内部の予算でASの受付範囲を先に狭めない意図です。600MのMは十進の1,000,000 bytesで、600 MiBではありません。

225 MP progressiveカラーを単体・各条件3回測定した比較（両方ともexit 3）：

| AS | JPEGMEM | RSS MiB（範囲） | 拒否まで秒（範囲） |
| --- | --- | ---: | ---: |
| 544 MiB | 未指定 | 84.4–84.5 | 0.138–0.142 |
| 544 MiB | 600M | 84.6–84.8 | 0.136–0.137 |
| 768 MiB | 未指定 | 740.4–740.7 | 1.899–2.046 |
| 768 MiB | 600M | 84.4–84.7 | 0.158–0.161 |

標準ASでは大きな差を確認していません。ASを増やす設定ではJPEGMEMで大量の係数メモリを確保する前に拒否でき、RSSと時間を減らせるため、設定とEnvironmentでの適用を維持しました。非LinuxではASがないため同じ効果を期待する設計ですが、非Linuxでの実測はありません。JPEGMEMはbaselineやqpdf自身のbufferの全メモリを制限しないので、ASの代替にはしません（[libjpeg-turboのメモリ予算とbacking store実装](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/2.1.5/jmemnobs.c)）。

ASを増やしたときもJPEGの受付範囲をASで決める運用なら、JPEGMEMのbytes値も新しいAS以上へ見直してください。JPEGMEMを低く維持して巨大progressiveを先に拒否する運用では、ASだけなら通る入力も拒否し得ます。

設定増加の実HTTP確認：標準で422の同じ100 MPカラーbaseline 4:2:0は、memory 2 GiB・AS 768 MiB・JPEGMEM 900Mで同時2件とも200。cgroup peak 1061.5 MiB、qpdf RSS 517.0 MiB、VmSize 759.5 MiB、tmpfs 73.2 MiB、HTTP最大2.108秒、OOM 0。この設定で他のJPEG形式や任意の100 MP写真が通るとは検証していません。

## 再実行

Docker smokeはPython標準ライブラリだけで実行できます。重いfixtureは単体テストへ入れず、ホストの検証専用Pillow/NumPyで合成します。監視にhost root権限を使いますが、APIコンテナはnon-rootのままです。

```bash
python3 -m venv /tmp/qpdf-memory-venv
/tmp/qpdf-memory-venv/bin/pip install pillow==12.3.0 numpy==2.5.3
/tmp/qpdf-memory-venv/bin/python scripts/memory-fixtures.py /tmp/qpdf-memory-fixtures --include-giant
dotnet tool install dotnet-counters --version 10.0.750501 --tool-path /tmp/qpdf-memory-tools
docker build -t amane-pdf-api:memory-check .
sudo python3 scripts/docker-memory-check.py amane-pdf-api:memory-check \
  /tmp/qpdf-memory-fixtures /tmp/qpdf-memory-results \
  --counters /tmp/qpdf-memory-tools/dotnet-counters
sudo python3 scripts/docker-memory-check.py amane-pdf-api:memory-check \
  /tmp/qpdf-memory-fixtures /tmp/qpdf-memory-raised \
  --as-mib 768 --jpeg-memory 900M --memory-mib 2048 --cases 100mp-color
```

単体比較（コンテナは1.5 GiB、qpdfは1 process。768 MiBは診断用で、標準の同時2設定ではありません）：

```bash
sudo python3 scripts/docker-qpdf-memory-check.py amane-pdf-api:memory-check \
  /tmp/qpdf-memory-fixtures /tmp/qpdf-jpeg-threshold.json \
  --cases 6000x4000-progressive-420 8064x6048-progressive-420 \
  --as-mib 768 --jpeg-memory 64M 128M 160M 200M 400M 600M --repeat 1
sudo python3 scripts/docker-qpdf-memory-check.py amane-pdf-api:memory-check \
  /tmp/qpdf-memory-fixtures /tmp/qpdf-as-threshold.json \
  --cases 8064x6048-progressive-420 --as-mib 384 448 480 512 544 576 \
  --jpeg-memory 600M --repeat 1
sudo python3 scripts/docker-qpdf-memory-check.py amane-pdf-api:memory-check \
  /tmp/qpdf-memory-fixtures /tmp/qpdf-early-rejection.json \
  --cases 15000x15000-progressive-420 --as-mib 544 768 --jpeg-memory unset 600M
```

hostのdotnetがユーザーディレクトリにある環境ではsudoへDOTNET_ROOTを明示してください。AS/JPEGMEMを指定しないHTTP負荷はAPIの初期値を使います。`--cases` で個別の写真形式や `sustained-baseline` / `sustained-baseline-444` / `sustained-progressive-420` を選択できます。

成功・失敗ともfinallyでコンテナを削除します。一時fixture、結果CSV/JSON、診断ツール、venv、検証イメージは削除済みです。数値と再実行用ソースだけをRepositoryに残しました。

## 回帰と未検証

Release build / 全361テスト / Docker build / Docker smoke（25 POSTと起動失敗ケース）はPASS。Linuxのテストは実prlimitとqpdfを使用。全APIの正常・不正・warning・暗号化入力、unlockのprobe・4 reason、出力不正500、126/127の500、timeout・cancel・アプリ停止後のprocess treeと一時ファイル削除を含みます。ProcessMemoryLimits、QpdfProcessorの2メソッドでの適用、起動時自己テスト、shellなし、Runner無変更、422/unlock契約を維持しています。

非Linux、実カメラ写真コーパス、progressive 4:4:4／4:2:2・CMYK・12-bit等のJPEG、native allocationの厳密な内訳、本番・長時間運用は未検証です。サイトRepository、#22の圧縮API、deploy・公開は変更していません。PRはHuman承認なしにmergeしません。
