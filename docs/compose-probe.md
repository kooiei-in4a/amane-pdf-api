# Document Studio D1 限定検証（#444）

2026-11-13 JST。架空データによる試作。設計は D1-r1 `38b1d07ed15588eaf72af5295d92beab39a23f61`。[site PR #455](https://github.com/kooiei-in4a/amane-tools-site/pull/455)

## 結果

| 検証 | 合否 | 実測 |
| --- | --- | --- |
| V-S1 | 3/3 合格 | A4 30ページ、20 PNG（各2,000×2,000、計10 MiB）、文字180要素、ページ最大60要素。最大70 run、8.983〜12.461秒、台帳80.46 MiB、出力18.30 MiB。pdfcpu 70回、qpdf 219回（多重 `--overlay` 1回） |
| V-S2 | 3/3 合格 | 50 MiBの背景＋同じ20 PNGを20ページに各1枚。20 run、6.529〜7.552秒、台帳121.07 MiB、出力59.06 MiB。pdfcpu 20回、qpdf 69回（多重 `--overlay` 1回） |
| V-S3 | 合格 | BIZ UDP明朝 Regular v1.06。字幅表11,425件、ゴシックとの共通集合11,425件。2ページ各160字、計320種を MuPDF と Poppler が完全一致で抽出。ToUnicode は4ブロック（100/100/100/20）、不一致0 |

V-S1 は先頭ページで画像20枚を文字と交互に置き、全30ページに文字を置いた。1ページあたり最大 `1 + 2 × 画像数` run なので、全体の上界 `30 + 2 × 20 = 70` を実現した。既存の出力検査に加え、MuPDF で元ページの識別用文字、寸法、固定ベクタの360行、画像20枚の配置を照合した。V-S2 の背景は既存メモリ fixture と同じ独立した乱数画像ストリームを使い、50本を出力でも確認した。PNG のアップロードは計10,485,760 bytes、サニタイズ後は計10,433,965 bytes。

## §8 への提案

50 MiB の背景で文字と画像を併用し、合計200要素のまま画像数を変えた。各1回。

| 画像（受領時） | 文字 | run | ピーク台帳 | 出力 |
| --- | ---: | ---: | ---: | --- |
| 20枚（10 MiB） | 180 | 70 | 124.00 MiB | 不合格。多重 overlay の書込予算55.55 MiBを超過 |
| 12枚（6 MiB） | 188 | 54 | 124.00 MiB | 不合格。多重 overlay の書込予算60.68 MiBを超過 |
| 10枚（5 MiB） | 190 | 50 | 124.00 MiB | 合格。61.94 MiB |
| 8枚（4 MiB） | 192 | 46 | 122.76 MiB | 合格。60.67 MiB |

- L09（200要素・60/頁）、L10（20枚）、L21（30秒）は単独の上限候補として維持。L04 に、背景・生成レイヤー・中間PDFを含む124 MiBの合算条件を明記する。50 MiB背景との同時利用を固定値で案内する場合、今回余裕を確認した暫定値は8枚・4 MiB。画像の圧縮率や文字レイヤーのサイズでも結果が変わるため、#447 では実レイヤーのバイト数で判定する。
- L16 は #447 が既存 Builder を使う間、生成上限62 MiBを明記する。80 MiBは site の応答受信上限として扱える。今回の fixture は固定2行のゴシックと512 KiBのPNGを使った。L02の2 MiB/枚、L09の最大4,000描画行、明朝混在時の負荷は追加実装時の検証対象。

## 明朝の基準値

- 行ボックスは hhea と OS/2 の値が一致し、上端 `1802/2048 = 0.8798828125 em`、下端 `246/2048 = 0.1201171875 em`。§4.4 のベースラインはゴシックと同じ式を使える。
- pdfcpu 0.16.3 の実 content stream では、1000 pt指定で上端879 pt・下端127 pt、2048 pt指定で上端1800 pt・下端261 pt。描画時の下端補正には `ceil(127/1000 × s)` を使う。6、6.1、12、55、72、144 pt でページ下端に合わせたベースラインの最大誤差は0.0000051 pt未満。[pdfcpu のフォント境界読込](https://github.com/pdfcpu/pdfcpu/blob/35ea259eb27d5a607f797edd41fd8a9e50a1f818/pkg/font/install.go#L319-L349)
- フォント SHA-256 `dbcea04578ac1e9d3484525e870ce491bd04361768f4d2ba4b827d96e20f891d`。字幅表は既存と同じ6 byteのレコード（little-endian の u16 を scalar、GID、送り幅の順）で、送り幅は1/1000 em単位で切り捨て。[固定リリース（OFL 1.1）](https://github.com/googlefonts/morisawa-biz-ud-mincho/releases/tag/v1.06)

## 測定条件と再現

既存 `overlay-validation.py` と同じ Docker CPU 1、メモリ1,536 MiB、swapなし、tmpfs 256 MiB、非root、読み取り専用、ネットワークなし。1コンテナ1試行。時間は fixture の作業領域へのコピー、layout読込、画像検査、合成と Builder 出力検査を含む。測定後に別途 MuPDF で検査した。台帳は4 KiB単位の予約ピーク。追加の測定ホストは使っていない。

- image `amane-pdf-api-candidate:df3f8a8-v0818`、ID `sha256:046cf847228375bc588899ea271b2f24a429940439ad184618dcefc7e78109c9`、image のソース `df3f8a8ff78770004e06d40aad0b1f662af6655c`。試作の測定コード `37509152f827dafb5e380a2e5c437c51d0af3567` を .NET SDK 10.0.403 でビルドし、既存ハーネスの読み取り専用マウントで実行。
- qpdf 12.3.2、pdfcpu 0.16.3、MuPDF/PyMuPDF 1.26.3、Poppler 25.12.0。フォントは一時設定にだけインストールした。

```bash
python3 -m venv /tmp/amane-compose-venv
/tmp/amane-compose-venv/bin/pip install --require-hashes --only-binary=:all: -r scripts/fill-template-validation-requirements.txt
/tmp/amane-compose-venv/bin/pip install fonttools==4.60.1
dotnet build --configuration Release -p:DefineConstants=COMPOSE_PROBE
/tmp/amane-compose-venv/bin/python scripts/compose-probe.py amane-pdf-api-candidate:df3f8a8-v0818 \
  --font /tmp/BIZUDPMincho-Regular.ttf --vectors /tmp/layout-v1-vectors.json \
  --results /tmp/compose-probe-results.json
```

`--font` と `--vectors` は上記の固定リリースと設計SHAから取得する。実測値とページごとの抽出ハッシュは [JSON](compose-probe-results.json)、生成した [字幅表](compose-probe-mincho-widths.bin) を同梱。
