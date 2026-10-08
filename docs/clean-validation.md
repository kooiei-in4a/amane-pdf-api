# PDF情報除去の検証記録

2026-10-08 UTC、main `51b1c7f` を起点とする #24 の実装を検証した。設計時のqpdf単体実験と区別し、ここはHTTP API・.NETテスト・Dockerの結果を記録する。合成fixtureの結果であり、任意のPDFの処理成功や本番でのOOM回避を保証しない。

## 実行条件

- host: .NET SDK 10.0.401、qpdf 12.3.2。
- Docker: Dockerfileの .NET 10 / Ubuntu 26.04、qpdf 12.3.2。
- non-root、read-only、network none、CPU 1、memory 1.5 GiB、memory-swapも1.5 GiB（swapなし）、tmpfs /tmp 256 MiB、cap-drop ALL、no-new-privileges。
- API設定は初期値。共有deadline 30秒、concurrency 2、qpdf AS 544 MiB、JPEGMEM 600M、SIGXFSZ無視・CORE=0・FSIZEあり。
- JSON各32 MiB、PDF出力54 MiB、1 job 124 MiB。JSON深さ・参照連鎖64、参照循環はtoo-complex、間接object 100,000。

## 機能と残存検査

`Fixtures/clean-source.json` から実qpdfで作ったPDFをHTTPへ送信した。入力には文書Info、文書XMP、Names添付、FileAttachment、ParentなしのPopup、Parentだけで関連付けたPopup、IRT返信、OBJR、AF、EF、RF、RichMedia assets、GoToEを含めた。

200の出力を `qpdf --qdf --object-streams=disable --decode-level=all` で展開し、文書情報・XMP・添付payload・添付用filespec名・添付注釈のContents / RC / T / Subj / NM・両方の関連Popupの目印が消えることを確認した。RichMedia / GoToEの中身は消え、それらが参照する空のfilespec名は残った。

本文と画像のデコード後SHA-256、ページ数・順序・回転・Box、通常コメント・Popup・返信、OBJR、ページXMP、Dests / JavaScript、フォーム値、しおり、Collectionを保持した。InfoはModDateだけが残り、Producerは追加されなかった。trailer IDの1要素目を保持し、再度cleanしても200だった。増分更新で古いInfoのAuthorが入力bytesに残るfixtureも、出力には残らなかった。

`PdfEmbeddedFileStripperTests.cs` は入力JSON → 部分更新JSON → 更新後JSON → Verifyを検査する。直接・間接・別名・共有Annots、Parentなし・Parentだけ・両方のPopup、間接Subtype、許可リスト、非対象のnull / 数値 / 文字列 / 辞書でないNames、stream辞書、深さ・object数・参照循環を確認した。入力と出力のPopup数は同じ関数で数え、対象の間接objectだけをnullにする。

HTTPの異常系は既存422（空・非PDF・warning・user/owner暗号化）、400（query・password・重複/不足file）、413、too-complex、壊れた入力/出力JSON、残存、空/破損/warning出力、ページ数不一致、126/127等の500を確認した。出力JSON/PDFの実FSIZE境界は、予算が実サイズ−1と同値なら422、実サイズ＋1なら200だった。timeout・キャンセル・アプリ停止で親子processの終了、一時領域の削除、共有枠の解放、healthを確認した。

正常経路は9回のqpdf起動だった。入力JSONはqpdfのみ、出力JSONはqpdf + attachmentsを1回で取得し、最終QPDFJobは1回。job / update JSONとargv、0600ファイル・0700ディレクトリ、AS / FSIZE、出力検査前の入力・中間物削除を確認した。ログ・Problem DetailsへPDF、添付key/名前、作成者、内部path、stderrは出ない。

## 容量・時間の測定

`scripts/clean-validation.py` は50 MiBに近いPDFを生成する。31.9 MiBの保持対象文字列を独立objectへ置き、非圧縮の固定seedバイナリstreamと添付を含める。入力JSONはstream dataなしで32 MiBに近づける。大きいscalarを変更objectへ含めず、実測はこの構成を対象とする。大きい変更objectの更新JSONが容量上限へ達する場合は422で拒否する。

| 条件 | 単独 | 同時2件 |
| --- | ---: | ---: |
| 各入力PDF bytes | 52,425,553 | 52,425,553 |
| 各入力objects JSON bytes | 33,425,084 | 33,425,084 |
| HTTP結果 | 200 | 200 / 200 |
| HTTP秒数（upload・download込み） | 6.755 | 12.481 / 12.555 |
| 各出力PDF bytes | 52,431,250 | 52,431,250 |
| cgroup memory.peak bytes | 351,969,280 | 668,635,136 |
| tmpfs使用量の観測最大 bytes | 85,860,352 | 171,712,512 |
| 同時job数の観測最大 | 1 | 2 |
| memory.events max / oom / oom_kill | 0 / 0 / 0 | 0 / 0 / 0 |

private binary streamのデコード後SHA-256は両出力とも入力と一致した。HTTP後にはjobもqpdfも残らなかった。tmpfsはdocker execによる定期サンプリングで、瞬間的な最大の保証ではない。memory.peakはcgroupの値で、tmpfsを含むコンテナ全体のピークである。

## 再実行と完了確認

```bash
dotnet build --configuration Release
dotnet test --configuration Release
docker build -t amane-pdf-api:ci .
python3 scripts/docker-smoke.py amane-pdf-api:ci
python3 scripts/clean-validation.py amane-pdf-api:ci --concurrency 1
python3 scripts/clean-validation.py amane-pdf-api:ci
```

通常CIのDocker smokeへ代表的なclean・RichMedia・GoToE・目印検査と上限422・不正設定での起動拒否を追加し、既存APIのsmokeも成功した。容量計測は通常CIと分ける。hostではCIと同様、パス依存のAppArmor制限を避けるため同じqpdfバイナリを別パスへコピーして実行した。制限付きworkspaceではMSBuildを `-m:1` で実行した。

Release build、全体593件、セルフレビューの最後の修正後のclean関連53件が成功した。最後の修正は、入力検証後のページ数取得warningも500に揃えたもので、追加したテストで確認した。Docker buildと既存APIを含む全smoke、その修正後のclean smokeも成功した。ローカルrestoreにはNuGet監査endpointを取得できないNU1900警告があったが、依存解決・build・test自体は成功した。GitHub Actionsの結果はPR側で確認し、ローカルPASSと区別する。

この結果は #46 の隔離VM・core collector・infra容量整合の確認を代替しない。merge、tag、Release、deployは別途Human承認の対象で、公開条件の保留も維持する。
