# Python・CI・測定・設計probe台帳

Pythonはscripts配下25本、docsの設計probe配下13本、合計38本。assertおよびassert helper呼出を持つ126関数/トップレベルscopeを[script-checks.csv](script-checks.csv)へ列挙し、目的・実行条件・判定式と行番号を記載。これはMSTestの実行ケース数と加算できる数ではない。

assertのないgenerator/表示用probeも下表に含める。fixtureを作る処理、測定の前提、回帰の合否判定を区別する。Pythonのassertは`python -O`で消えるが現行CIは-Oを使わない。

CI分類は対象commitの[ci.yml](../../.github/workflows/ci.yml)のコマンドと各mainの分岐から確認した。現在のGitHub Actions実行結果を照会したという意味ではない。

## [check_geom.py](../../docs/redact-design-probe/check_geom.py)

実行: 歴史的geometry確認 / 今回未実行。経路: 設計probeまたはfixture。

prototype出力のsample位置/色を表示する。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: しきい値合否をassertしないため現行の回帰検証としては数えない。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [checks.py](../../docs/redact-design-probe/checks.py)

実行: 歴史的marker確認 / 今回未実行。経路: 設計probeまたはfixture。

展開PDFのmarker/字形等の観測値を表示する。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: ToUnicode処理が簡略版。現行の秘匿保証はRedactTests群。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [fontshare.py](../../docs/redact-design-probe/fontshare.py)

実行: 歴史的graph prototype / 今回未実行。経路: 設計probeまたはfixture。

font/form/pattern共有による対象拡張の初期方式を試す。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: 現行RedactDocumentのinherited resource/role graphを再現しない。設計史として保管。

| 検証scope | 守ること |
| --- | --- |
| S001 [resolve](../../docs/redact-design-probe/fontshare.py#L15) | prototype参照解決の既知前提。現行本番のgraph安全境界ではない |

## [gen_geometry.py](../../docs/redact-design-probe/gen_geometry.py)

実行: 現役CI fixture生成にも使用 / 今回未実行。経路: 設計probeまたはfixture。

回転/crop/UserUnit混在PDFを生成する。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: renderer-validation.py/redact-validation.pyの入力。probeディレクトリという理由で削除しない。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [gen_markers.py](../../docs/redact-design-probe/gen_markers.py)

実行: 歴史的fixture生成 / 今回未実行。経路: 設計probeまたはfixture。

page/annotation/attachment/font等の追跡markerを持つPDFを作る。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: prototypeと現行fixtureの由来。生成だけを合否テストに数えない。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [gen_misc.py](../../docs/redact-design-probe/gen_misc.py)

実行: 歴史的fixture生成 / 今回未実行。経路: 設計probeまたはfixture。

form/暗号/各種page条件を生成する。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: 現行の網羅性はMSTest/CIで判断。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [gen_perf.py](../../docs/redact-design-probe/gen_perf.py)

実行: 手動性能fixture生成 / 今回未実行。経路: 設計probeまたはfixture。

大きな多頁PDF・共有資源を作る。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: 性能計測用入力であり単独gateではない。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [mkpage.py](../../docs/redact-design-probe/mkpage.py)

実行: 手動fixture生成補助 / 今回未実行。経路: 設計probeまたはfixture。

既知contentとpageを組み立てる。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: 呼出元の生成条件と合わせて読む。

| 検証scope | 守ること |
| --- | --- |
| S002 [<module>](../../docs/redact-design-probe/mkpage.py#L1) | モジュールのトップレベルで判定：既知contentとpageを組み立てる。 |

## [ppm.py](../../docs/redact-design-probe/ppm.py)

実行: probe画像読込補助 / 今回未実行。経路: 設計probeまたはfixture。

PPM header/pixelの簡易読込。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: 本番decoderではなくprobeのoracle補助。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [updates.py](../../docs/redact-design-probe/updates.py)

実行: 歴史的JSON更新prototype / 今回未実行。経路: 設計probeまたはfixture。

prune/final更新の初期方式を再現する。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: 現行のallowlist/neutral role安全境界を含む実装ではない。移植しない。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [analyze.py](../../docs/stamp-image-design-probe/analyze.py)

実行: 歴史的描画比較 / 今回未実行。経路: 設計probeまたはfixture。

PNG領域の位置/alpha/色を観測して最悪差を表示する。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: pixel誤差の全てを失敗条件にしていない。現行CI stamp-image-validation.pyを回帰gateとする。

| 検証scope | 守ること |
| --- | --- |
| S003 [main](../../docs/stamp-image-design-probe/analyze.py#L86) | 比較fixtureの前提をassertするがpixel最悪差は観測として表示 |

## [make_cases.py](../../docs/stamp-image-design-probe/make_cases.py)

実行: 歴史的fixture生成 / 今回未実行。経路: 設計probeまたはfixture。

回転/crop/UserUnitのmatrix PDFを生成する。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: 生成と結果判定を区別。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [make_png.py](../../docs/stamp-image-design-probe/make_png.py)

実行: 歴史的fixture生成 / 今回未実行。経路: 設計probeまたはfixture。

depth/color/alpha/Adam7 PNGを合成する。

判定根拠: 固定fixture/観測値。呼出側で判定。

本番との差・重複判断: 現役StampFixtures/Python validationとの用途を区別。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [clean-validation.py](../../scripts/clean-validation.py)

実行: 手動容量・構造 / 今回未実行。経路: HTTP clean。

50MiB/32MiB JSON/5万object/多頁境界の単独/2同時、metadata/attachment消去、binary content保持、24秒以内・cleanup・OOMなしを確認。

判定根拠: binary stream hash・実構造・cgroup/proc・jobs重なり。

本番との差・重複判断: 現行CIに直接呼出がない。.NETのUTF8 hashの弱点を補うが通常CIの代わりとは書かない。

| 検証scope | 守ること |
| --- | --- |
| S004 [Monitor.__init__](../../scripts/clean-validation.py#L33) | 計測環境の前提を確認：50MiB/32MiB JSON/5万object/多頁境界の単独/2同時、metadata/attachment消去、binary content保持、24秒以内・cleanup・OOMなしを確認。 |
| S005 [Monitor.finish](../../scripts/clean-validation.py#L53) | 終了時の監視結果/資源条件を確認：50MiB/32MiB JSON/5万object/多頁境界の単独/2同時、metadata/attachment消去、binary content保持、24秒以内・cleanup・OOMなしを確認。 |
| S006 [Monitor.assert_stopped](../../scripts/clean-validation.py#L64) | 計測後の子プロセス不在 |
| S007 [many_objects](../../scripts/clean-validation.py#L86) | object数/JSON等の負荷前提を検査：50MiB/32MiB JSON/5万object/多頁境界の単独/2同時、metadata/attachment消去、binary content保持、24秒以内・cleanup・OOMなしを確認。 |
| S008 [request](../../scripts/clean-validation.py#L114) | HTTP応答/構造/後始末を判定：50MiB/32MiB JSON/5万object/多頁境界の単独/2同時、metadata/attachment消去、binary content保持、24秒以内・cleanup・OOMなしを確認。 |
| S009 [case](../../scripts/clean-validation.py#L160) | 1測定条件の応答/資源を判定：50MiB/32MiB JSON/5万object/多頁境界の単独/2同時、metadata/attachment消去、binary content保持、24秒以内・cleanup・OOMなしを確認。 |

## [compress-validation.py](../../scripts/compress-validation.py)

実行: CI host --tools は今回成功 / CI Docker --render は今回未実行。経路: 実JPEG CLI、HTTP compress→独立Poppler。

JPEG引数の実変換互換、standard/strongの候補/除外、valid ICC・SMask・色空間保存、対象外stream保存、text/vector保持、画質PSNRを確認。

判定根拠: 実binary stream hash・Pillow/Poppler・PSNR>22。

本番との差・重複判断: shapeのみの.NET色空間試験を補完。--toolsの成功を--render成功と混同しない。

| 検証scope | 守ること |
| --- | --- |
| S010 [tool_check](../../scripts/compress-validation.py#L21) | cjpeg/djpegの本番引数で実変換が成立すること |
| S011 [render](../../scripts/compress-validation.py#L67) | 以下の描画比較を判定：JPEG引数の実変換互換、standard/strongの候補/除外、valid ICC・SMask・色空間保存、対象外stream保存、text/vector保持、画質PSNRを確認。 |
| S012 [render_check](../../scripts/compress-validation.py#L90) | 候補/除外stream・有効ICC/SMask・独立Popplerと画質閾値 |

## [docker-memory-check.py](../../scripts/docker-memory-check.py)

実行: 手動負荷測定 / 今回未実行。経路: HTTP optimize/compress・混在/継続。

近50MiB/巨大decode/30回×2等のexpected status、privacy、job cleanup、実cgroup/tool phaseを確認し結果を保存。

判定根拠: 実status/limits/RSS/time・品質PSNR記録。

本番との差・重複判断: qualityのPSNRは記録のみで閾値gateではない。CPU1/no-swap等の測定条件とセット。

| 検証scope | 守ること |
| --- | --- |
| S013 [Monitor.__init__](../../scripts/docker-memory-check.py#L67) | 計測環境の前提を確認：近50MiB/巨大decode/30回×2等のexpected status、privacy、job cleanup、実cgroup/tool phaseを確認し結果を保存。 |
| S014 [request](../../scripts/docker-memory-check.py#L176) | HTTP応答/構造/後始末を判定：近50MiB/巨大decode/30回×2等のexpected status、privacy、job cleanup、実cgroup/tool phaseを確認し結果を保存。 |
| S015 [quality](../../scripts/docker-memory-check.py#L216) | 出力画像寸法/構造を確認して画質を計測（PSNR閾値は無し） |
| S016 [run](../../scripts/docker-memory-check.py#L244) | 負荷シナリオの結果/資源を判定：近50MiB/巨大decode/30回×2等のexpected status、privacy、job cleanup、実cgroup/tool phaseを確認し結果を保存。 |

## [docker-qpdf-memory-check.py](../../scripts/docker-qpdf-memory-check.py)

実行: 手動qpdf較正 / 今回未実行。経路: コンテナ内qpdf直接実行。

native qpdfのAS/JPEGMEMごとのexit/RSS/timeを記録する。

判定根拠: 実tool metrics・fixture/version。

本番との差・重複判断: API受付/HTTP timeout/共有permitを通らず、期待HTTP outcomeを判定するテストではない。

| 検証scope | 守ること |
| --- | --- |
| S017 [main](../../scripts/docker-qpdf-memory-check.py#L86) | 較正環境/入力/コンテナ条件の成立（全tool試行の成功を要求しない） |

## [docker-smoke.py](../../scripts/docker-smoke.py)

実行: CI Docker / 今回未実行。経路: 実Kestrel HTTP・起動・直接CLI。

non-root/read-only/CPU1/1.5GiB/no-swap/tmpfs256MiB下でprotect/restrict/unlock/rotate/optimize/merge/extract/split/clean/compressの契約、実暗号/復号、出力妥当性、private job/log、startup失敗、FSIZE/CORE、pdfcpu/PDFium/font/license配置を確認。

判定根拠: Docker inspect・qpdf再読込/復号・/proc/limits・固定problem/headers。

本番との差・重複判断: 全18APIをこの1本だけで網羅するわけではない。overlay系等は個別スクリプト。pdfcpu_smokeはpdfcpu-validation.pyからも呼ばれ一部重複するが追加抽出検証の前提でもある。

| 検証scope | 守ること |
| --- | --- |
| S018 [ApiContainer.start](../../scripts/docker-smoke.py#L74) | コンテナの実メモリ/CPU/no-swap/read-only/tmpfs/非root/capability設定とhealth到達性 |
| S019 [ApiContainer.protect](../../scripts/docker-smoke.py#L150) | 暗号PDF・固定filename/headers・失敗時problemとcleanup |
| S020 [ApiContainer.restrict](../../scripts/docker-smoke.py#L165) | 権限指定/owner・user passwordと固定応答 |
| S021 [ApiContainer.unlock](../../scripts/docker-smoke.py#L189) | 復号結果・暗号解除と固定応答 |
| S022 [ApiContainer.rotate](../../scripts/docker-smoke.py#L207) | page回転と固定応答 |
| S023 [ApiContainer.optimize](../../scripts/docker-smoke.py#L220) | 最適化後page/固定応答 |
| S024 [ApiContainer.merge](../../scripts/docker-smoke.py#L230) | 全入力page数/順序と固定応答 |
| S025 [ApiContainer.extract](../../scripts/docker-smoke.py#L240) | 抽出page数/順序と固定応答 |
| S026 [ApiContainer.assert_clean](../../scripts/docker-smoke.py#L250) | request完了後のjob残留なしと秘密情報のlog非露出 |
| S027 [ApiContainer.verify_encryption](../../scripts/docker-smoke.py#L260) | 実qpdfによる暗号種別/鍵長/権限/認証の確認 |
| S028 [ApiContainer.verify_decryption](../../scripts/docker-smoke.py#L297) | 実復号とpage保存 |
| S029 [pdfcpu_smoke](../../scripts/docker-smoke.py#L343) | 実配置pdfcpu/PDFium/font・license/permission・limitsと日本語描画 |
| S030 [assert_startup_failure](../../scripts/docker-smoke.py#L424) | 不正依存/設定で起動拒否し固定log以外を漏らさない |
| S031 [split_request](../../scripts/docker-smoke.py#L449) | ZIP/固定headers・page合計・エラー形式とcleanup |
| S032 [split_calibration](../../scripts/docker-smoke.py#L469) | 出力FSIZE境界と固定エラー |
| S033 [split_smoke](../../scripts/docker-smoke.py#L501) | TZ/ZIP timestamp・正常/異常分割・依存異常 |
| S034 [compress_smoke](../../scripts/docker-smoke.py#L525) | 両levelの実再圧縮数/limits/結果構造・copyright配置 |
| S035 [main](../../scripts/docker-smoke.py#L579) | 入口として以下の検証を束ねる：non-root/read-only/CPU1/1.5GiB/no-swap/tmpfs256MiB下でprotect/restrict/unlock/rotate/optimize/merge/extract/split/clean/compressの契約、実暗号/復号、出力妥当性、private job/log、startup失敗、FSIZE/CORE、pdfcpu/PDFium/font/license配置を確認。 |
| S036 [clean_request](../../scripts/docker-smoke.py#L712) | metadata/attachment除去と内容保存・応答形式 |
| S037 [clean_smoke](../../scripts/docker-smoke.py#L732) | 実clean正常/複雑性/上限・private data非露出 |

## [fill-template-glyphs-validation.py](../../scripts/fill-template-glyphs-validation.py)

実行: CI Docker / 今回未実行。経路: 実pdfcpu描画・固定TTF解析。

11,425 GIDの描画幅/ToUnicode、BMP候補63,423 scalarの対応/除外、結合文字とpercent等を確認。

判定根拠: 独立fontTools TTF・実出力GID/CMap・generated table hash。

本番との差・重複判断: テーブル全lookupの内部一致だけでなく実pdfcpu挙動を守る。依存version固定の検証。

| 検証scope | 守ること |
| --- | --- |
| S038 [table](../../scripts/fill-template-glyphs-validation.py#L20) | 固定TTF/hashと生成glyph表の整合 |
| S039 [cid_widths](../../scripts/fill-template-glyphs-validation.py#L69) | 実CID font幅の構造と値 |
| S040 [verify_tounicode](../../scripts/fill-template-glyphs-validation.py#L92) | 実CMapのGID→Unicode対応・block/重複/欠落 |
| S041 [draw](../../scripts/fill-template-glyphs-validation.py#L122) | 実pdfcpu描画とbyte/字形条件 |
| S042 [verify](../../scripts/fill-template-glyphs-validation.py#L176) | 実pdfcpuのGID/advance/ToUnicodeと固定TTFの全件一致 |
| S043 [main](../../scripts/fill-template-glyphs-validation.py#L203) | 入口として以下の検証を束ねる：11,425 GIDの描画幅/ToUnicode、BMP候補63,423 scalarの対応/除外、結合文字とpercent等を確認。 |

## [fill-template-glyphs.py](../../scripts/fill-template-glyphs.py)

実行: 開発時生成・--check / 今回未実行。経路: 固定TTF→生成C#。

TTF hash/GID/advance・対象scalar・決定的出力を固定し、生成物の再現性を確認。

判定根拠: fontToolsとlock hash・生成text比較。

本番との差・重複判断: 生成側の自己整合だけではpdfcpuの解釈を保証しない。別の実glyph validationを維持。

| 検証scope | 守ること |
| --- | --- |
| S044 [generate](../../scripts/fill-template-glyphs.py#L20) | 再現fixture/生成物の前提と整合：TTF hash/GID/advance・対象scalar・決定的出力を固定し、生成物の再現性を確認。 |

## [fill-template-validation.py](../../scripts/fill-template-validation.py)

実行: CI Docker（HTTP/実署名/容量/builderを含む）/ 今回未実行。経路: HTTP fill-template・一部builder直呼出。

11page/66baselineの配置、320 glyph抽出/percent/結合文字、ぴったりfitと下端、pyHanko実署名/フォーム16形、50MiB/1000page/200field/1000lineの単独/同時、builder予約上限を確認。

判定根拠: 独立Poppler/MuPDF・pyHanko実署名・実予約/cgroup。

本番との差・重複判断: builder_capacityがLV03の条件付き2テストを有効化する。HTTP capacityと内部会計は重複削除しない。

| 検証scope | 守ること |
| --- | --- |
| S045 [request](../../scripts/fill-template-validation.py#L48) | HTTP応答/構造/後始末を判定：11page/66baselineの配置、320 glyph抽出/percent/結合文字、ぴったりfitと下端、pyHanko実署名/フォーム16形、50MiB/1000page/200field/1000lineの単独/同時、builder予約上限を確認。 |
| S046 [geometry](../../scripts/fill-template-validation.py#L103) | 固定基準66baseline・回転/UserUnit・左右中央配置・元内容保持 |
| S047 [extraction](../../scripts/fill-template-validation.py#L171) | 320 glyph/percent/結合文字のPoppler/MuPDF一致 |
| S048 [verify_bottom_baseline](../../scripts/fill-template-validation.py#L201) | 下端ぴったりの配置を独立座標で確認 |
| S049 [signed_fixtures](../../scripts/fill-template-validation.py#L220) | pyHanko実署名/フォームfixtureが成立すること |
| S050 [forms](../../scripts/fill-template-validation.py#L265) | 実署名/フォーム入力がform-fieldsで拒否されること |
| S051 [capacity_fixture](../../scripts/fill-template-validation.py#L299) | 規定入力サイズ/page/object/field等の負荷条件が実際に成立すること |
| S052 [capacity](../../scripts/fill-template-validation.py#L334) | 以下の容量/資源条件を判定：11page/66baselineの配置、320 glyph抽出/percent/結合文字、ぴったりfitと下端、pyHanko実署名/フォーム16形、50MiB/1000page/200field/1000lineの単独/同時、builder予約上限を確認。 |
| S053 [builder_capacity](../../scripts/fill-template-validation.py#L376) | 環境変数付き.NET上限容量測定の成功と予約peak |
| S054 [main](../../scripts/fill-template-validation.py#L408) | 入口として以下の検証を束ねる：11page/66baselineの配置、320 glyph抽出/percent/結合文字、ぴったりfitと下端、pyHanko実署名/フォーム16形、50MiB/1000page/200field/1000lineの単独/同時、builder予約上限を確認。 |

## [from-images-as-validation.py](../../scripts/from-images-as-validation.py)

実行: 手動のAS較正・計画再現 / 今回未実行。経路: 直接pdfcpu・HTTPとAS差替wrapper。

固定image/plan/repeatでAS1088/1152MiB等の成功率/資源を比較し、起動失敗とHTTPを区別して証拠化。Adam7全系統fixtureを生成。

判定根拠: 固定計画/入力hash・実status/資源・driverのPillowテスト。

本番との差・重複判断: 明示AS差替は検討用実験であり既定本番の値の検証ではない。探索計画JSONとセットで読む。

| 検証scope | 守ること |
| --- | --- |
| S055 [<module>](../../scripts/from-images-as-validation.py#L1) | モジュールのトップレベルで判定：固定image/plan/repeatでAS1088/1152MiB等の成功率/資源を比較し、起動失敗とHTTPを区別して証拠化。Adam7全系統fixtureを生成。 |
| S056 [generate](../../scripts/from-images-as-validation.py#L84) | 再現fixture/生成物の前提と整合：固定image/plan/repeatでAS1088/1152MiB等の成功率/資源を比較し、起動失敗とHTTPを区別して証拠化。Adam7全系統fixtureを生成。 |
| S057 [generate.add](../../scripts/from-images-as-validation.py#L88) | 個別fixtureの条件を検査：固定image/plan/repeatでAS1088/1152MiB等の成功率/資源を比較し、起動失敗とHTTPを区別して証拠化。Adam7全系統fixtureを生成。 |
| S058 [environment](../../scripts/from-images-as-validation.py#L157) | 固定計画と実環境の一致を確認：固定image/plan/repeatでAS1088/1152MiB等の成功率/資源を比較し、起動失敗とHTTPを区別して証拠化。Adam7全系統fixtureを生成。 |
| S059 [direct_batch](../../scripts/from-images-as-validation.py#L189) | 直接tool測定の前提/資源を確認：固定image/plan/repeatでAS1088/1152MiB等の成功率/資源を比較し、起動失敗とHTTPを区別して証拠化。Adam7全系統fixtureを生成。 |
| S060 [http_batch](../../scripts/from-images-as-validation.py#L235) | HTTP測定の前提/資源を確認：固定image/plan/repeatでAS1088/1152MiB等の成功率/資源を比較し、起動失敗とHTTPを区別して証拠化。Adam7全系統fixtureを生成。 |
| S061 [measure](../../scripts/from-images-as-validation.py#L319) | 測定計画/環境の成立を確認：固定image/plan/repeatでAS1088/1152MiB等の成功率/資源を比較し、起動失敗とHTTPを区別して証拠化。Adam7全系統fixtureを生成。 |

## [from-images-followup-test.py](../../scripts/from-images-followup-test.py)

実行: CI host / 今回8件成功。経路: Python測定driver単体。

readonly qpdfのFSIZE例外、writer禁止、空例外保持、起動/非JSON失敗の証拠、共通PNG working境界、Adam7生成画素、prefix境界を守る。

判定根拠: 模擬/proc・固定vectors・独立Pillow decode・unittest。

本番との差・重複判断: API本体の回帰8ケースではなく計測driverの健全性。旧Monitorはこのテストの対象外。

| 検証scope | 守ること |
| --- | --- |
| S062 [DriverTests.test_monitor_handles_qpdf_inspection_exit_and_rejects_unlimited_writer](../../scripts/from-images-followup-test.py#L17) | process終了raceを許容しreadonly qpdfだけFSIZE無制限を許す |
| S063 [DriverTests.test_only_read_only_qpdf_commands_allow_unlimited_fsize](../../scripts/from-images-followup-test.py#L44) | readonly flag/引数数/tool種別のallowlistを限定する |
| S064 [DriverTests.test_monitor_preserves_an_empty_exception_as_a_failure](../../scripts/from-images-followup-test.py#L51) | メッセージが空のAssertionErrorも監視失敗として保存する |
| S065 [DriverTests.test_startup_failure_keeps_non_http_evidence](../../scripts/from-images-followup-test.py#L69) | 起動失敗をHTTP未送信として保存し成功判定しない |
| S066 [DriverTests.test_working_estimate_matches_shared_vectors_and_default_admission](../../scripts/from-images-followup-test.py#L85) | PNG working式と.NET共通固定vector/320MiB admissionの一致 |
| S067 [DriverTests.test_adam7_generator_preserves_decoded_pixels](../../scripts/from-images-followup-test.py#L96) | packed depth/Adam7の生成器を独立Pillowでdecodeし非interlaceと画素一致 |
| S068 [DriverTests.test_non_json_error_keeps_http_evidence](../../scripts/from-images-followup-test.py#L114) | 非JSON error応答のstatus/media/body長を保持しtransport失敗と混同しない |
| S069 [DriverTests.test_prefix_boundary_matches_complete_png_sizes](../../scripts/from-images-followup-test.py#L129) | PNG encoded実サイズ込みworking境界の直下/直上 |

## [from-images-followup.py](../../scripts/from-images-followup.py)

実行: 手動容量/追試 / 今回未実行。経路: HTTP from-images/compressと負荷監視。

PNG系統/境界・JPEG48MP/50MiB・混在負荷の期待status/reason、同時実行数、health/後始末/資源、失敗を含む証拠保存を確認。

判定根拠: 固定expected status・cgroup/proc・独立生成fixture。

本番との差・重複判断: Monitorは旧classを継承するがloop/finishを上書き。共通化時はreadonly/writerの適用範囲を保つ。CIはdriver単体のみ。

| 検証scope | 守ること |
| --- | --- |
| S070 [boundary_height](../../scripts/from-images-followup.py#L114) | PNG実サイズを含むworking上限の上下境界 |
| S071 [generate](../../scripts/from-images-followup.py#L136) | 再現fixture/生成物の前提と整合：PNG系統/境界・JPEG48MP/50MiB・混在負荷の期待status/reason、同時実行数、health/後始末/資源、失敗を含む証拠保存を確認。 |
| S072 [generate.add](../../scripts/from-images-followup.py#L139) | 個別fixtureの条件を検査：PNG系統/境界・JPEG48MP/50MiB・混在負荷の期待status/reason、同時実行数、health/後始末/資源、失敗を含む証拠保存を確認。 |
| S073 [Monitor.loop](../../scripts/from-images-followup.py#L238) | readonly qpdfのFSIZE無制限だけを許しwriter/他toolの有限limitとCORE=0を要求 |
| S074 [validate_pdf](../../scripts/from-images-followup.py#L334) | 生成PDFのpage/構造とmetadata非露出 |
| S075 [run_case](../../scripts/from-images-followup.py#L360) | case別expected status/reason、資源・後始末・証拠の整合 |
| S076 [capacity](../../scripts/from-images-followup.py#L439) | 以下の容量/資源条件を判定：PNG系統/境界・JPEG48MP/50MiB・混在負荷の期待status/reason、同時実行数、health/後始末/資源、失敗を含む証拠保存を確認。 |

## [from-images-validation.py](../../scripts/from-images-validation.py)

実行: CI既定render / 手動 --capacity・--reservations / 今回未実行。経路: HTTP from-images・直接processor測定。

両quality、8orientation/JFIF/EXIF/GPS除去、A4/10mm/order、alpha、起動異常を独立比較。容量branchは実資源、reservationsは.NET測定を呼ぶ。

判定根拠: Pillow exif_transpose・Poppler pixel・PDF matrix・binary metadata・cgroup。

本番との差・重複判断: 旧Monitorは全qpdfに有限FSIZEを要求。readonly qpdfを一般に監視すると新版と差が出るが、本branchのimage専用qpdfはFSIZE付き。今回の模擬入力だけから現行capacityが失敗すると断定しない。

| 検証scope | 守ること |
| --- | --- |
| S077 [request](../../scripts/from-images-validation.py#L33) | HTTP応答/構造/後始末を判定：両quality、8orientation/JFIF/EXIF/GPS除去、A4/10mm/order、alpha、起動異常を独立比較。容量branchは実資源、reservationsは.NET測定を呼ぶ。 |
| S078 [small_jpeg](../../scripts/from-images-validation.py#L54) | EXIF/GPS等の意図したJPEG fixture条件 |
| S079 [assert_private_metadata_absent](../../scripts/from-images-validation.py#L81) | 展開PDF/画像に既知private metadata markerが残らないこと |
| S080 [verify_layout](../../scripts/from-images-validation.py#L96) | 独立計算A4/10mm/page matrixと画像向き |
| S081 [render](../../scripts/from-images-validation.py#L114) | 以下の描画比較を判定：両quality、8orientation/JFIF/EXIF/GPS除去、A4/10mm/order、alpha、起動異常を独立比較。容量branchは実資源、reservationsは.NET測定を呼ぶ。 |
| S082 [make_fixtures](../../scripts/from-images-validation.py#L173) | 負荷fixtureの入力条件を検査：両quality、8orientation/JFIF/EXIF/GPS除去、A4/10mm/order、alpha、起動異常を独立比較。容量branchは実資源、reservationsは.NET測定を呼ぶ。 |
| S083 [Monitor.__init__](../../scripts/from-images-validation.py#L237) | 計測環境の前提を確認：両quality、8orientation/JFIF/EXIF/GPS除去、A4/10mm/order、alpha、起動異常を独立比較。容量branchは実資源、reservationsは.NET測定を呼ぶ。 |
| S084 [Monitor.loop](../../scripts/from-images-validation.py#L243) | 全監視toolのAS/FSIZEが有限、CORE=0であること（image専用scope） |
| S085 [Monitor.finish](../../scripts/from-images-validation.py#L268) | 終了時の監視結果/資源条件を確認：両quality、8orientation/JFIF/EXIF/GPS除去、A4/10mm/order、alpha、起動異常を独立比較。容量branchは実資源、reservationsは.NET測定を呼ぶ。 |
| S086 [capacity](../../scripts/from-images-validation.py#L280) | 以下の容量/資源条件を判定：両quality、8orientation/JFIF/EXIF/GPS除去、A4/10mm/order、alpha、起動異常を独立比較。容量branchは実資源、reservationsは.NET測定を呼ぶ。 |
| S087 [select_cases](../../scripts/from-images-validation.py#L318) | 指定caseが存在して意図した測定集合を選ぶこと |

## [memory-fixtures.py](../../scripts/memory-fixtures.py)

実行: 補助fixture生成 / 今回未実行。経路: 既知PDF/JPEGを生成。

大JPEG/Flate/多頁/混在画像を固定seed等で作り、メモリ・入力サイズ境界を再現可能にする。

判定根拠: 生成bytes/metadata、呼出元の検証。

本番との差・重複判断: 単独の合否gateではない。実利用者PDFを使わず合成fixtureで監査。

| 検証scope | 守ること |
| --- | --- |
| S088 [<module>](../../scripts/memory-fixtures.py#L1) | モジュールのトップレベルで判定：大JPEG/Flate/多頁/混在画像を固定seed等で作り、メモリ・入力サイズ境界を再現可能にする。 |

## [overlay-validation.py](../../scripts/overlay-validation.py)

実行: CI --render / 手動 --capacity / 今回未実行。経路: Docker内の.NET builder直呼出、独立Poppler。

6生成PDFの元内容/geometry/リンク等と表示領域外を含む描画を比較。容量は50MiB/32MiB JSON/5万object/1000pageなどを単独・同時に測る。

判定根拠: 独立bitmap差分・構造・実予約peak・cgroup/tmpfs。

本番との差・重複判断: HTTPを通る描画検証ではない。上限負荷は--capacity指定時のみ。通常.NETの小fixture実行と区別。

| 検証scope | 守ること |
| --- | --- |
| S089 [fixture](../../scripts/overlay-validation.py#L35) | fixtureの入力条件を検査：6生成PDFの元内容/geometry/リンク等と表示領域外を含む描画を比較。容量は50MiB/32MiB JSON/5万object/1000pageなどを単独・同時に測る。 |
| S090 [font_characters](../../scripts/overlay-validation.py#L75) | TTF対応文字の集合/字形数と負荷用文字列 |
| S091 [Container.__init__](../../scripts/overlay-validation.py#L122) | 計測環境の前提を確認：6生成PDFの元内容/geometry/リンク等と表示領域外を含む描画を比較。容量は50MiB/32MiB JSON/5万object/1000pageなどを単独・同時に測る。 |
| S092 [Container.stopped](../../scripts/overlay-validation.py#L146) | 計測後の子プロセス不在と資源集計 |
| S093 [run_capacity_case](../../scripts/overlay-validation.py#L173) | 上限fixtureの実予約/時間/資源を判定：6生成PDFの元内容/geometry/リンク等と表示領域外を含む描画を比較。容量は50MiB/32MiB JSON/5万object/1000pageなどを単独・同時に測る。 |
| S094 [compare_files](../../scripts/overlay-validation.py#L264) | 元画像/領域外/overlay座標を独立Popplerで比較 |
| S095 [main](../../scripts/overlay-validation.py#L326) | 入口として以下の検証を束ねる：6生成PDFの元内容/geometry/リンク等と表示領域外を含む描画を比較。容量は50MiB/32MiB JSON/5万object/1000pageなどを単独・同時に測る。 |

## [page-numbers-validation.py](../../scripts/page-numbers-validation.py)

実行: CI Docker / 今回未実行。経路: HTTP page-numbers。

4位置×3形式×3sizeの36条件、12混在pageの本文保存・期待番号・font size・physical margin・回転/UserUnitを確認。

判定根拠: 独立pdftotext bboxと固定期待文字/位置。

本番との差・重複判断: 内部layer数式を再利用しないので単体の自己整合検証とは別価値。

| 検証scope | 守ること |
| --- | --- |
| S096 [verify](../../scripts/page-numbers-validation.py#L66) | 期待番号文字/物理font size/位置/余白と元内容保持 |
| S097 [main](../../scripts/page-numbers-validation.py#L112) | 入口として以下の検証を束ねる：4位置×3形式×3sizeの36条件、12混在pageの本文保存・期待番号・font size・physical margin・回転/UserUnitを確認。 |

## [pdfcpu-update-validation.py](../../scripts/pdfcpu-update-validation.py)

実行: CI Docker / 今回未実行。経路: 直接pdfcpu・HTTP fill-template。

percent/backslash/不足glyph、負座標とdy、99/100/101/200文字のCMap分割と複数pageのUnicode抽出を確認。

判定根拠: 実output CMap・Poppler/MuPDF抽出。

本番との差・重複判断: fill-template extractionと一部重なるが100文字区切り・依存更新の特有境界がある。現在もCIなので単なる古い更新メモではない。

| 検証scope | 守ること |
| --- | --- |
| S098 [runs](../../scripts/pdfcpu-update-validation.py#L58) | この関数で判定する対象：percent/backslash/不足glyph、負座標とdy、99/100/101/200文字のCMap分割と複数pageのUnicode抽出を確認。 |
| S099 [text_behavior](../../scripts/pdfcpu-update-validation.py#L74) | percent/backslash/未対応glyphを実pdfcpuの出力と比較 |
| S100 [coordinates](../../scripts/pdfcpu-update-validation.py#L89) | pdfcpuの負座標/dy解釈と期待配置 |
| S101 [unicode](../../scripts/pdfcpu-update-validation.py#L108) | 99/100/101/200文字と複数pageの独立文字抽出 |
| S102 [main](../../scripts/pdfcpu-update-validation.py#L130) | 入口として以下の検証を束ねる：percent/backslash/不足glyph、負座標とdy、99/100/101/200文字のCMap分割と複数pageのUnicode抽出を確認。 |

## [pdfcpu-validation.py](../../scripts/pdfcpu-validation.py)

実行: CI Docker / 今回未実行。経路: 実pdfcpu self-test・直接描画。

Docker配置/limits/fontに加え、日本語文字抽出とembedded fontを確認。

判定根拠: pdftotext/pdffonts・既存smoke.pdfcpu_smoke。

本番との差・重複判断: トップレベルで実行され関数宣言がない。assert-bearing関数の数だけで漏らさない。

| 検証scope | 守ること |
| --- | --- |
| S103 [<module>](../../scripts/pdfcpu-validation.py#L1) | モジュールのトップレベルで判定：Docker配置/limits/fontに加え、日本語文字抽出とembedded fontを確認。 |

## [redact-thresholds.py](../../scripts/redact-thresholds.py)

実行: 手動decoder閾値の探索 / 今回未実行。経路: 実rendererのDCT/JPX/JBIG2試行。

encoded imageの寸法/RAM限界と実pixel描画を採集し、現在の安全閾値を選ぶ根拠を作る。

判定根拠: 実exit/RSS/time/pixelを結果に記録。

本番との差・重複判断: 全試行成功を求める回帰gateではなく、失敗域も測る探索。既定pytest/MSTestに数えない。

| 検証scope | 守ること |
| --- | --- |
| S104 [measure](../../scripts/redact-thresholds.py#L67) | 測定環境成立を確認しdecoder成功/失敗/画素を採集（成功一律を要求しない） |

## [redact-validation.py](../../scripts/redact-validation.py)

実行: CI geometry / 手動 --capacity / 今回未実行。経路: HTTP redact・混在merge。

6pageの表示座標/黒塗り/対象外pixel・headers。容量では30page/30MiB共有fontとmerge50MiBの実render同時性、tmpfs/OOMを確認。

判定根拠: 独立Poppler・render phase barrier・Cのtmpfs sampler・cgroup。

本番との差・重複判断: 容量は任意branch。単独成功を並行成功と数えないため実重なりを観測する。

| 検証scope | 守ること |
| --- | --- |
| S105 [geometry](../../scripts/redact-validation.py#L52) | 以下の座標/描画を独立比較：6pageの表示座標/黒塗り/対象外pixel・headers。容量では30page/30MiB共有fontとmerge50MiBの実render同時性、tmpfs/OOMを確認。 |
| S106 [capacity](../../scripts/redact-validation.py#L166) | 以下の容量/資源条件を判定：6pageの表示座標/黒塗り/対象外pixel・headers。容量では30page/30MiB共有fontとmerge50MiBの実render同時性、tmpfs/OOMを確認。 |

## [renderer-validation.py](../../scripts/renderer-validation.py)

実行: CI Docker validate / 手動 --measure / 今回未実行。経路: 実renderer CLI、API起動自己テスト。

rotation/crop intersection/UserUnit/塗りpixel、日本語font/permission/license、silent CLI failure、空font起動拒否、native limitsを確認。

判定根拠: 独立pixel基準・実limits・起動log。

本番との差・重複判断: --measureの6試行は時間記録であり既定CIではない。docsのgen_geometry.pyを現役で利用。

| 検証scope | 守ること |
| --- | --- |
| S107 [check_image](../../scripts/renderer-validation.py#L46) | rotation/crop/UserUnit/黒塗りの独立pixel期待値 |
| S108 [validate](../../scripts/renderer-validation.py#L62) | 以下のCLI/起動/出力契約を判定：rotation/crop intersection/UserUnit/塗りpixel、日本語font/permission/license、silent CLI failure、空font起動拒否、native limitsを確認。 |
| S109 [measure](../../scripts/renderer-validation.py#L125) | 測定計画/環境の成立を確認：rotation/crop intersection/UserUnit/塗りpixel、日本語font/permission/license、silent CLI failure、空font起動拒否、native limitsを確認。 |

## [split-validation.py](../../scripts/split-validation.py)

実行: 手動容量・混在負荷 / 今回未実行。経路: HTTP splitと他API混在。

近50MiB/展開肥大/100part/混在7条件でZIP妥当性、実資源、cleanup、TZ影響などを測る。

判定根拠: 実ZIP/qpdf・cgroup/tmpfs・観測status。

本番との差・重複判断: requestは200/422/504を許容しcase別の成功期待を要求しない。明示的な測定なのでPASSを全入力の機能成功と読まない。

| 検証scope | 守ること |
| --- | --- |
| S110 [check_pdf](../../scripts/split-validation.py#L61) | 出力PDFのqpdf妥当性 |
| S111 [Monitor.__init__](../../scripts/split-validation.py#L69) | 計測環境の前提を確認：近50MiB/展開肥大/100part/混在7条件でZIP妥当性、実資源、cleanup、TZ影響などを測る。 |
| S112 [Monitor.finish](../../scripts/split-validation.py#L95) | 終了時の監視結果/資源条件を確認：近50MiB/展開肥大/100part/混在7条件でZIP妥当性、実資源、cleanup、TZ影響などを測る。 |
| S113 [Monitor.assert_stopped](../../scripts/split-validation.py#L106) | 計測後の子プロセス不在 |
| S114 [request](../../scripts/split-validation.py#L114) | 200/422/504を許容し各形式を検査。caseごとの200期待は保証しない |
| S115 [case](../../scripts/split-validation.py#L144) | 容量/cleanup/ZIPと負荷の観測。全ケース200を要求するgateではない |
| S116 [main](../../scripts/split-validation.py#L162) | 入口として以下の検証を束ねる：近50MiB/展開肥大/100part/混在7条件でZIP妥当性、実資源、cleanup、TZ影響などを測る。 |

## [stamp-image-validation.py](../../scripts/stamp-image-validation.py)

実行: CI render / 手動 --capacity / 今回未実行。経路: HTTP stamp-image。

2layer形×2PNG(8bit/16bit Adam7)×12geometryの48条件で位置/alpha/実色/対象外page保存。容量は50MiB/1000page/2同時。

判定根拠: 独立Poppler mask/pixelとPDF構造・cgroup。

本番との差・重複判断: 既定容量PNGはIEND後paddingで約1MiBにするため最悪のencoded pixel負荷の証拠にはならない。--capacity-imageで実near-limit入力を指定する別モードを区別。

| 検証scope | 守ること |
| --- | --- |
| S117 [render](../../scripts/stamp-image-validation.py#L89) | 以下の描画比較を判定：2layer形×2PNG(8bit/16bit Adam7)×12geometryの48条件で位置/alpha/実色/対象外page保存。容量は50MiB/1000page/2同時。 |
| S118 [capacity](../../scripts/stamp-image-validation.py#L174) | 以下の容量/資源条件を判定：2layer形×2PNG(8bit/16bit Adam7)×12geometryの48条件で位置/alpha/実色/対象外page保存。容量は50MiB/1000page/2同時。 |
| S119 [capacity.send](../../scripts/stamp-image-validation.py#L189) | 単一HTTP応答と内容を確認：2layer形×2PNG(8bit/16bit Adam7)×12geometryの48条件で位置/alpha/実色/対象外page保存。容量は50MiB/1000page/2同時。 |

## [verify-pdfcpu-modules.py](../../scripts/verify-pdfcpu-modules.py)

実行: CI host / 今回未実行。経路: 依存binary metadata。

go version -mのmodule/version/checksumとlock宣言、license hashの一致を確認。

判定根拠: binary metadataと固定manifest。

本番との差・重複判断: PDF機能テストとは別の供給物整合。更新が必要な固定値を単なるbrittle testとして除去しない。

| 検証scope | 守ること |
| --- | --- |
| S120 [<module>](../../scripts/verify-pdfcpu-modules.py#L1) | モジュールのトップレベルで判定：go version -mのmodule/version/checksumとlock宣言、license hashの一致を確認。 |

## [watermark-glyphs.py](../../scripts/watermark-glyphs.py)

実行: 開発時生成・--check / 今回未実行。経路: 固定TTF→生成C#。

許可glyph/range/幅の生成と固定font hash、生成物一致を確認。

判定根拠: TTF table/固定生成物。

本番との差・重複判断: CIのwatermark-validation.pyが実pdfcpu/widthを別途検証。生成器は常時CI実行ではない。

assert-bearing scopeなし。生成・表示・外部コマンド失敗による検知はファイルの役割として確認。

## [watermark-validation.py](../../scripts/watermark-validation.py)

実行: CI Docker（容量を含む）/ 今回未実行。経路: HTTP watermark。

6text×2style×2colorの24条件/12page、透過・位置・角度・実色/抽出、21,498 scalar字形/幅、1000page成功/1001page拒否を確認。

判定根拠: Poppler色pixel・PCA・抽出文字・固定font表照合。

本番との差・重複判断: LV08の弱い色assertを補完する実描画。容量はこのmainから実行されるので単に手動扱いしない。

| 検証scope | 守ること |
| --- | --- |
| S121 [text_frame_center](../../scripts/watermark-validation.py#L122) | 描画maskが存在し外接frame中心を測れること |
| S122 [verify_structure](../../scripts/watermark-validation.py#L152) | text回数・透明度・font/cmap・page構造の保持 |
| S123 [verify](../../scripts/watermark-validation.py#L181) | 実色/opacity・文字抽出・PCA角度・配置/サイズ/元内容保持 |
| S124 [verify_glyphs](../../scripts/watermark-validation.py#L245) | 許可scalarの実描画/幅とTTF/生成表の一致 |
| S125 [capacity](../../scripts/watermark-validation.py#L287) | 以下の容量/資源条件を判定：6text×2style×2colorの24条件/12page、透過・位置・角度・実色/抽出、21,498 scalar字形/幅、1000page成功/1001page拒否を確認。 |
| S126 [main](../../scripts/watermark-validation.py#L318) | 入口として以下の検証を束ねる：6text×2style×2colorの24条件/12page、透過・位置・角度・実色/抽出、21,498 scalar字形/幅、1000page成功/1001page拒否を確認。 |

## 二つの画像容量Monitorの扱い

旧from-images-validation.pyとfollowup.pyのloopは重複するが、readonly qpdfのFSIZE例外に差がある。模擬/procに`qpdf --check input.pdf`、有限AS、CORE=0、FSIZE=unlimitedを与えると旧版はAssertionError/0 sample、新版は正常/1 sampleになった（[証拠](evidence/monitor-witness.json)）。

ただし旧版の現在の呼出元はfrom-images専用capacityで、その生成PDF検査はQpdfProcessor.CheckImagePdfAsyncからFSIZE付きで起動する。したがってこの証拠だけでは「現行手動capacityが誤って失敗する」とは断定できず、低価値候補へは登録しない。混在APIの監視器として再利用する際は新版のallowlistと既存8テストを使い、writerを無制限にしない条件を保つ。
