# FSIZE core対策の検証記録（2026-10-09 JST）

対象は[共通対策 #46](https://github.com/kooiei-in4a/amane-pdf-api/issues/46)。基準mainは`06f10130356cad998b7cdcdce76c7a0c47ad124b`。当初のFSIZE超過に絞ったAPI側の実装と検証であり、本番collectorでの確認ではありません。

## 最小対策とHTTPへの影響

Linuxの共通ProcessMemoryLimitsで次の経路を直接起動します。

```text
/usr/bin/env --ignore-signal=XFSZ -- prlimit --core=0:0 --as=... [--fsize=...] -- tool args...
```

OS標準のenvがSIGXFSZを無視し、prlimitがCORE soft/hard=0と既存のAS／FSIZEを設定してtoolへexecします。専用launcher、shell、新しい設定flag、Runnerの変更は追加していません。起動時自己テストも同じ経路を使い、失敗はHTTP待受前に固定ログで終了します。

SIGXFSZを無視しても書込み上限は維持されます。今回のqpdfは上限で切れたPDFでもexit 0となりました。splitは既存の実サイズ判定で専用422を返します。compressのpages／metadata JSONは、上限に達した実サイズをexit 0でも既知のmetadata上限として扱うよう補いました。同値も安全側に画像選択を打ち切り、既に採用した画像があれば保持して最終処理を行います。JSON予算は増やしません。最終出力も実サイズがFSIZE以上ならexit 0でも500とし、上限同値の正常PDFも安全側に拒否します。末尾が切れたPDFはqpdf --checkを通る場合があるため、その検証だけに依存しません。JPEG変換の通常エラーは画像不採用、起動失敗126／127と最終出力の失敗は500という既存契約を維持します。

無視設定はexecをまたいで継承されますが、tool自体が戻すことは可能です。そのためtool更新時は実FSIZE超過を再確認します。根拠は[Linux signal仕様](https://man7.org/linux/man-pages/man7/signal.7.html)と[envのオプション](https://uutils.org/coreutils/docs/utils/env.html)です。CORE=0だけではpipeへの受渡しを止めません（[Linux core仕様](https://man7.org/linux/man-pages/man5/core.5.html)）。SIGSEGV／ABRT等の別signalによるcoreは今回の対象外です。

## 環境と再現

host／Docker内のkernelは`7.0.0-38-generic`、x86_64、page size 4096。SDK 10.0.401、runtime imageはaspnet:10.0-resolute（Ubuntu 26.04.1）。Docker内はenv uutils 0.10.0、prlimit util-linux 2.41.3、qpdf 12.3.2-1、libjpeg-turbo-progs 2.1.5-4ubuntu4です。検証イメージIDは`sha256:fbe4d60299b072f7ab0b023b62778745e61489fda0e715a70ac605544db1409e`。

smokeは合成PDF／画像のみを使用し、non-root、read-only、network none、CPU 1、memory 1.5 GiB、swapなし、tmpfs 256 MiB、同時処理枠2で実行しました。

```bash
dotnet build --configuration Release
# CIと同じ配布qpdfのコピー。hostのAppArmorによるraw出力制約への対応。
install -Dm755 /usr/bin/qpdf /tmp/issue-46-pdf-tools/qpdf
PATH="/tmp/issue-46-pdf-tools:$PATH" dotnet test --configuration Release
docker build -t amane-pdf-api:issue-46-fsize-core .
python3 scripts/docker-smoke.py amane-pdf-api:issue-46-fsize-core
```

## 結果

- Release build：warning／error 0。Release test：541件PASS、失敗／skip 0。
- 追加8件：両factory overloadのCORE／AS／FSIZE・JPEGMEM継承、exec後のSIGXFSZ無視、実qpdfの境界、実cjpeg／djpegの上限、pages／objects JSONの容量打切りとHTTP 200・置換0・有効PDF・cleanup、最終出力の上限同値でHTTP 500・成功ヘッダーなし。
- Docker build／全smoke PASS。既存のtimeout／cancel、126／127、入力検査、ログ非露出とjob削除は全テスト・smokeで確認。
- Docker内で共通起動経路のCORE=0、AS=64 MiB、FSIZE=4096と、exec後のSIGXFSZ送信で終了しないことを確認。
- カラー／グレーのcjpegとdjpegはFSIZE=64でexit 1、実出力64 bytes。ddはFSIZE=4096でexit 1、実出力4096 bytes。従来のsignalによる153固定期待を使いません。
- compressの両level・カラー／グレーでHTTP 200、置換1、出力縮小。splitのStored／固定名／固定DOS時刻、UTC／Asia/Tokyo、部分ZIP非返却、cleanup／healthを確認。

同じイメージ内でAPIと同じenv／prlimit経路を使い、1ページの通常出力698 bytesについて測定しました。

| partBudget | FSIZE | 終了コード | 実出力長 |
| ---: | ---: | ---: | ---: |
| 698 | 699 | 0 | 698 |
| 697 | 698 | 0 | 698 |
| 128 | 129 | 0 | 129 |

HTTPでは全パート合計Tで200、T−1で422、T−最終パート長＋128で422でした。最後のケースもexit 0後の実サイズ判定で専用422になります。[旧記録](split-validation.md)の153はSIGXFSZ無視を導入する前の観測です。

### レビュー後の最終出力境界の修正

最終出力の旧判定`実サイズ > fsize`は、SIGXFSZ無視でexit 0・実サイズ=fsizeとなる切れた出力を見逃していました。`>= fsize`へ修正しました。153の既存判定には防御目的で残すことをコメントしています。

追加テストでは、本番用の上限設定や4 MiBの余裕を変えず、最終起動だけ合成fixtureを使う実qpdfへ差し替えました。Catalogの非圧縮文字列で通常出力を4,194,893 bytesへ校正し、APIが計算するFSIZE 4,194,888で末尾5 bytesが切れることを確認しています。同じSIGXFSZ無視・CORE=0・AS／FSIZEの共通経路を使用しています。

| 出力 | qpdf終了 | 実サイズ | qpdf --check | HTTP |
| --- | ---: | ---: | ---: | ---: |
| 上限なしの校正 | 0 | 4,194,893 | 0 | 対象外 |
| 修正前・FSIZEで末尾切断 | 0 | 4,194,888 | 0 | 200（不具合を再現） |
| 修正後・同じ末尾切断 | 0 | 4,194,888 | 0 | 500 |

修正後は成功ヘッダーとPDFを返さず、内部path非露出・job削除も確認しました。正常出力であっても上限同値は拒否する扱いです。

## 未確認とinfraへの引き継ぎ

この試験で本番hostの設定変更・再起動・core生成は行っていません。hostのcore_pattern変更やcollectorの受信計測も行っていません。Dockerでsignal終了が起きない結果を、本番collectorの起動数／受信bytes／保存数を実測済みとは扱いません。非Linuxと全core抑止は未検証です。

amane-infraでは本番に近い専用の使い捨てVMで、報告書の隔離手順に沿って次を確認します。

1. kernel、collectorと版、core_pattern、image digest、実効limits、cgroupと容量条件を記録する。合成データだけを使い、hostも外部転送とbackupから隔離する。
2. 無対策、CORE=0だけ、SIGXFSZ無視＋CORE=0を比較する。無対策の比較で観測方法が働くことを確かめ、collector起動数・受信bytes・保存数を別々に測る。同版apportと試験collectorの結果を区別する。
3. 実qpdf／djpeg／cjpegのFSIZE超過、HTTP、timeout／cancel後の終了待ち・cleanupを確認する。CORE=0だけをpipe停止の根拠にしない。
4. API限定のCompose `ulimits.core.soft/hard: 0`を補助対策として用意する。本番の旧条件（memory 512 MiB、MemorySwap合計1 GiB）は新版の検証条件と異なるため、1.5 GiB・swapなし・tmpfs 256 MiB・同時2へ合わせるか、本番条件で容量を再検証する。

#46はopenのままとし、API splitとサイトsplitのdeployは確認完了まで保留します。実装PRは#46を参照し、自動closeしません。mergeとdeployはそれぞれHuman承認後に行います。
