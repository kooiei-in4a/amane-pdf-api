# qpdf共通メモリ制限の実測（Issue #42）

2026-10-08のローカル検証記録です。初期値は `QpdfAddressSpaceLimitBytes=339738624`（324 MiB）、`QpdfJpegMemory=64M` としました。

## 条件と測定方法

- 最新origin/main `8ed8a5cca19cff60d1875c996e61f2fbc6475a25` から新規worktreeを作成し、制限を実装したAPIを使用。
- Ubuntu 26.04 runtime image、.NET 10.0.12、qpdf 12.3.2、libjpeg-turbo8 2.1.5-4ubuntu4、util-linux 2.41.3-3ubuntu2.2。
- non-root、read-only、network none、cap-drop ALL、no-new-privileges。
- memory 1 GiB、memory-swap 1 GiB（cgroup `memory.swap.max=0`）、CPU 1、tmpfs /tmp 256 MiB、同時処理2。
- 実HTTPをコンテナ内loopbackへ送信。JPEG/Flateはoptimize、50 MiB付近はoptimizeとmergeを使用。標準条件の最終実行は合計84要求。
- `memory.peak` / `memory.events` はkernelの値。ホストの監視処理が20 msごとにAPIとqpdfの `/proc` RSS/HWM・VmSize、tmpfs使用量を取得。短いprocessや瞬間的なpeakはサンプリングで見逃し得る。
- ホストのdotnet-counters 10.0.750501でmanaged heapとGC committedを1秒ごとに測定。監視・診断ツール自体は対象コンテナ外で実行。
- fixtureは一時領域で合成。カラー写真相当の滑らかなRGB色面＋細かなランダムtextureをJPEG化（品質85、baseline）。実際のカメラ写真コーパスではない。Flateは4961×7016のRGB合成データをA4の595.28×841.89 ptへ配置し、サイズ44,023,268 bytes。
- 50 MiB付近は49個の固有1024×1024グレー画像streamを持つ51,389,831 bytesのPDF（約49 MiB）。mergeはこれと通常PDFを結合。大きなファイルをRepositoryへ入れない。
- 成功した返却PDFは対象コンテナ外のqpdfでcheck。stdout/stderr、画像、PDF、JSON本文を検証ログへ出さない。記録するのは固定ケース名、status、時間、サイズ、resource量だけ。

## 確認済みの数値

各行は同時2要求。RSSはqpdf 1 processの観測HWM。cgroupにはAPI、qpdf、tmpfs、HTTP送信用の短いbash/cat等も含みます。MiBは1,048,576 bytes。

| 入力・操作 | HTTP | cgroup peak MiB | tmpfs最大 MiB | qpdf RSS MiB | qpdf AS MiB | HTTP最大秒 |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 12 MPカラーJPEG（2,429,778 bytes） | 200 / 200 | 135.5 | 9.3 | 61.1 | 92.7 | 0.365 |
| 24 MPカラーJPEG（4,844,176 bytes） | 200 / 200 | 195.7 | 18.5 | 89.0 | 132.6 | 0.491 |
| 48 MPカラーJPEG（8064×6048、9,828,262 bytes） | 200 / 200 | 466.0 | 37.5 | 217.6 | 320.2 | 1.039 |
| A4・600dpiカラーFlate（44,023,268 bytes） | 200 / 200 | 452.4 | 167.9 | 179.0 | 251.7 | 16.892 |
| 約49 MiBのoptimize | 200 / 200 | 233.2 | 196.1 | 12.8 | 22.9 | 2.965 |
| 約49 MiBのmerge | 200 / 200 | 238.3 | 196.1 | 13.3 | 23.0 | 2.960 |
| 100 MPグレーbaseline（26,124,793 bytes） | 200 / 200 | 481.5 | 99.7 | 215.4 | 301.9 | 1.507 |
| 同progressive（23,213,222 bytes） | 422 / 422 | 161.2 | 44.3 | 54.0 | 61.9 | 0.457 |
| 100 MPカラーbaseline（20,130,620 bytes） | 422 / 422 | 378.7 | 38.4 | 165.8 | 232.2 | 0.913 |
| 通常48 MP＋100 MPグレーprogressive | 200 / 422 | 246.3 | 31.5 | 217.4 | 320.2 | 0.791 |
| 通常48 MP＋100 MPカラーbaseline | 200 / 422 | 400.4 | 28.6 | 217.5 | 320.2 | 1.075 |
| tmpfsを事前に210 MiB使用＋48 MP | 200 / 200 | 677.6 | 247.5 | 217.3 | 320.2 | 1.001 |
| 同条件の常駐APIで30回繰り返し（60要求） | 全200 | 710.1 | 247.5 | 217.7 | 320.2 | 1.331 |

全ケースで `memory.events` のmax、oom、oom_kill、oom_group_killは0。終了後のhealthは200、qpdf processとAPI job directoryの残存なし。tmpfsの圧力用ファイルはコンテナ削除で破棄しました。

最終継続試験ではAPI RSS 107.5 MiB、managed heap 11.9 MiB、GC committed 13.4 MiB。324 MiBの反復検証を合わせた最大working setは約110.0 MiB、heap約12.2 MiB、committed約13.7 MiBでした。committedは直近GCの情報です。

native＋共有mapping等は、RSSからGC committedを差し引く粗い見積りで約94〜97 MiB。RSSとGC committedは別の指標・別時点のpeakであり、正確なnative allocationやmanaged residentの分離ではありません。native anonymous、runtime、共有library、mapped assembly等の厳密な内訳は未測定です。

## 初期値と容量見積り

48 MPの同じJPEG PDFはAS 288 MiBと320 MiBで422、324 MiBと328 MiBで200でした。AS 320 MiBではqpdfの実測VmSize 320.2 MiBに届きません。実測成功値と標準コンテナの容量から324 MiBを選択しました。qpdfの観測ASに約3.8 MiBを残します。AS設定値はRSSの実測値ではありません。

```text
コンテナmemory ≥ .NET API ＋ tmpfs上限 ＋ 同時処理数 × qpdfのAS上限 ＋ 余裕
1024 MiB ≥ 112 MiB ＋ 256 MiB ＋ 2 × 324 MiB ＋ 8 MiB
```

.NET実測約110 MiBを112 MiBへ切り上げ、tmpfsは観測247.5 MiBに対して上限256 MiB、qpdfは実測320.2 MiBに対して上限324 MiB、余裕は8 MiBを見積りました。実際のcgroup最大710.1 MiBに対して1 GiBとの差は313.9 MiBです。RSSとAS、共有ページ、各項の最大値の時刻が異なるため、式の合計とcgroup peakは一致しません。

112 MiBと8 MiBは容量計画用の見積りであり、.NETへのhard limitではありません。任意のPDFや将来のruntimeでの成功・OOM回避の数学的な保証ではありません。ASを増やす場合、同時実行数やtmpfsを増やす場合、runtimeを更新する場合は同じ測定を行い、コンテナmemoryも合わせて増やしてください。

設定増加の確認：標準AS 324 MiBでは422の同じ100 MPカラーbaselineを、memory 2 GiB・AS 768 MiBにすると同時2件とも200。cgroup peak 1085.9 MiB、qpdf RSS 516.9 MiB、AS 759.5 MiB、tmpfs 76.8 MiB、HTTP最大2.040秒、OOM 0でした。AS 640 MiBでは422のままでした。JPEGMEMは64Mのままです。progressiveのJPEGMEM増加による成功閾値はこの検証の対象外です。

## 再実行

Docker smokeはPython標準ライブラリのみで実行できます。重いfixtureと負荷検証はCIの単体テストへ入れず、次のスクリプトで再実行します。Pillow/NumPyとdotnet-countersはホストの検証用で、runtime imageへ追加しません。

```bash
python3 -m venv /tmp/qpdf-memory-venv
/tmp/qpdf-memory-venv/bin/pip install pillow==12.3.0 numpy==2.5.3
/tmp/qpdf-memory-venv/bin/python scripts/memory-fixtures.py /tmp/qpdf-memory-fixtures
dotnet tool install dotnet-counters --version 10.0.750501 --tool-path /tmp/qpdf-memory-tools
docker build -t amane-pdf-api:memory-check .
sudo python3 scripts/docker-memory-check.py amane-pdf-api:memory-check \
  /tmp/qpdf-memory-fixtures /tmp/qpdf-memory-results
sudo python3 scripts/docker-memory-check.py amane-pdf-api:memory-check \
  /tmp/qpdf-memory-fixtures /tmp/qpdf-memory-results \
  --cases sustained --counters /tmp/qpdf-memory-tools/dotnet-counters
sudo python3 scripts/docker-memory-check.py amane-pdf-api:memory-check \
  /tmp/qpdf-memory-fixtures /tmp/qpdf-memory-raised \
  --as-mib 768 --memory-mib 2048 --cases 100mp-color
```

hostのdotnetがユーザーディレクトリにある環境では、sudoへDOTNET_ROOTを明示して診断ツールを起動してください。/procと診断socketの読み取りにhost root権限を使います。APIコンテナはnon-rootのままです。

スクリプトは成功・失敗ともfinallyでコンテナを削除します。生成したfixture、診断ツール、venv、結果、一時検証イメージは検証後に削除してください。本作業のfixture・コンテナ・検証イメージも削除済みです。数値だけを本書へ残します。

## その他の回帰と未検証

Release build / 全361テスト / Docker build / Docker smoke（25 POSTと起動失敗ケース）はPASS。Linuxのテストは実prlimitとqpdfを使用します。全APIの正常・不正・warning・暗号化入力、unlockのprobe・4 reasonと出力不正500、126/127の500、timeout・cancel・アプリ停止後のprocess treeと一時ファイル削除を含みます。

非LinuxのASなし経路、実カメラ写真のコーパス、native allocationの厳密な内訳、本番・長時間連続運用は未検証です。サイトRepositoryの変更、#22の圧縮API、deploy・公開は行っていません。
