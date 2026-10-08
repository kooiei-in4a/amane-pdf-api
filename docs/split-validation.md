# PDF分割の検証記録（2026-10-08）

基準mainは`d6b8f95ea3b439a92b2e3f8e51d8c66614ab9d7d`。このPRのsplit実装をローカルで検証した記録で、本番deployの確認ではありません。

## 条件と再現手順

hostはUbuntu 26.04、x86_64、page size 4096、.NET SDK 10.0.401 / runtime 10.0.12。Dockerは29.8.2、runtime imageはaspnet:10.0-resolute、qpdf 12.3.2です。測定イメージIDは`sha256:43c6514ac864702ef7f44e3278fcd27a8d1479cdad6b5bc7535a374cfabd170d`。APIの実装コードはこのPRのものです。

全コンテナはnon-root、read-only、network none、CPU 1、memory 1.5 GiB、swapなし、tmpfs 256 MiB、共有同時処理枠2で実行しました。splitの設定は分割数100、PDF合計50 MiB、job予算124 MiBです（smokeの容量境界ケースだけPDF合計を変更）。

```bash
dotnet build --configuration Release
# hostではCIと同じ配布qpdfの一時コピーをPATHへ置く。AppArmorのraw出力制約への対応。
install -Dm755 /usr/bin/qpdf /tmp/issue-23-pdf-tools/qpdf
PATH="/tmp/issue-23-pdf-tools:$PATH" dotnet test --configuration Release
docker build -t amane-pdf-api:issue-23-split .
python3 scripts/docker-smoke.py amane-pdf-api:issue-23-split
sudo python3 scripts/split-validation.py amane-pdf-api:issue-23-split /tmp/split-metrics.json
```

容量・時間のscriptは通常のsmoke/CIと分離して一度実行しました。固定seedのraw RGB画像を持つ有効な合成PDFだけを作り、終了時にfixtureと検証コンテナを削除します。PDF・画像・password・内部argv・stderrは測定記録へ出しません。sudoは作成した検証コンテナの/proc/cgroupを読むためです。設定やcollectorを変更しません。

## build/test/smoke

- Release build: PASS、警告0・エラー0。
- Releaseの全テスト: 533 PASS、失敗0・skip 0。split関連は87件。実qpdfでのevery/ranges・順序・省略ページ・固定名、Stored/DOS時刻、query、入力拒否、生成物不正、容量、126/127、timeout/cancel/app stopping、共有limiter、handle解放・削除、Abort時のデータ/seek/central directory/close故障を含みます。
- 大きなページ番号の境界テストで、既存の数値parseが数字のASCII値を先に足すためint.MaxValue付近でoverflowする問題を確認しました。数字への変換を先に行い、ページ指定とqpdfページ数の両方でint.MaxValueを検証しました。
- Docker build / 全Docker smoke: PASS。splitの2ページ、Stored・名前・ページ、TZ=UTC/Asia/TokyoのDOS時刻1980-01-01、容量422、catによる入力検査500、各完了後のcleanup/healthを確認しました。catの500をsplit固有のZIP故障試験とは扱いません。

同じイメージ内で`docker exec prlimit`にAS 544 MiB / JPEGMEM 600MとFSIZEを与え、HTTPでは見えない終了コードを直接確認しました。1ページの通常出力長は698 bytesです。

| partBudget | FSIZE | 終了コード | 実出力長 |
| ---: | ---: | ---: | ---: |
| 698 | 699 | 0 | 698 |
| 697 | 698 | 0 | 698 |
| 128 | 129 | 153 | 129 |

全パートの通常出力長合計Tを測り、HTTPではPDF合計上限Tで200、T−1で422（0終了後の実サイズ比較）、T−最終パート長＋128で422（実FSIZE超過）を確認しました。いずれも固定reason、部分ZIP非返却、cleanup/healthを確認しました。153を全qpdf失敗の判定条件にせず、終了待ち後の実サイズを使います。

## 容量・時間の実測

近い入力上限のfixtureは50,332,752 bytes（約48.001 MiB）、raw画像30 MiB→18 MiBの2ページです。共有resourceのfixtureは27 MiBの画像を2ページで共有し、分割すると各PDFへ複製されます。100パートは小さな白紙ページ100枚です。mixedのcompressはraw画像を圧縮対象にせず、0画像置換で最終処理を行う負荷です。JPEG変換自体は既存の全テストとDocker smokeで確認しました。

| 場面 | HTTP | HTTP時間（秒） | tmpfs最大の観測値（MiB） | cgroup memory.peak（MiB） |
| --- | --- | --- | ---: | ---: |
| 上限付近のsplit×2 | 200 / 200 | 3.642 / 3.582 | 210.05 | 290.91 |
| 同じ入力でranges=2,1 | 422 | 1.724 | 94.51 | 154.89 |
| 共有resourceの複製 | 422 | 1.817 | 77.01 | 139.94 |
| 小さな100パート | 200 | 1.886 | 0.06 | 34.46 |
| split / compress | 200 / 200 | 3.573 / 3.619 | 208.67 | 319.71 |
| split / merge | 200 / 200 | 3.386 / 3.123 | 174.25 | 305.33 |
| split / unlock | 200 / 200 | 2.999 / 2.484 | 193.03 | 319.50 |

全ケースでmemory.eventsのoom/oom_killは0、ENOSPC相当の500はなく、process/job残存0、終了後のhealth 200でした。上限付近の成功ZIPは50,348,841 bytes、PDF合計50,348,607 bytesです。逆順は同じ入力でもコピー中の重複予算に達して拒否することを確認しました。共有resourceのケースはPDF合計の上限で拒否しました。

tmpfsは20 ms間隔の標本で、瞬間の最大値を保証しません。memory.peakは新しいコンテナ全体の値で、起動時自己テスト等も含みます。時間はupload/downloadを含む実HTTP時間です。この1回・このfixtureでの事実であり、任意のPDFの30秒内成功やOOM回避を保証しません。500パート、非Linux、64 KiB pageのarm64は未検証です。設定・runtime・host/page size変更時には再測定します。

## core dumpの確認と未解決事項

合成PDFを使う専用Docker probeで上記のFSIZE 129を再現しました。コンテナ内にcoreファイルは見つからず、job削除とhealthは成功しました。一方、コンテナ内API processのRLIMIT_COREはsoft/hardともunlimitedで、host shellのsoft 0とは異なりました。

core_patternはhostのapportへのpipeでした。専用probe前後でhost apport.logの「Ignoring signal 25」に対応する行が1件増えました。確認したapportのsourceにはcontainer判定とSIGXFSZを無視する処理があります。ログ本文やdump本体は記録・公開していません。これらは、PDF内容が保存されたという証拠でも、内容がコンテナ外へ渡らない証明でもありません。`RLIMIT_CORE=0`だけでpipe collectorを抑えられるとは扱いません。

内容のコンテナ外への転送抑止は未確認です。[共通対策 #46](https://github.com/kooiei-in4a/amane-pdf-api/issues/46)でcompress/splitとhost/コンテナの経路を扱います。#23では共通prlimit・Runner・launcher・host設定を変更していません。実装PRのmerge可否はHumanが判断し、API splitのdeployは#46が解決して内容が外へ渡らないことを確認するまで保留します。サイトのsplitツールもAPI deploy後に公開します。
