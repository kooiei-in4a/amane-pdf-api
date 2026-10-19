# 第三者ソフトウェア

## pdfcpu・日本語font・Go依存物

pdfcpu [v0.16.1](https://github.com/pdfcpu/pdfcpu/tree/v0.16.1) は別プロセスで使用します。ライセンスはApache-2.0、原文とThe pdfcpu Authorsの著作権表示を `third_party/pdfcpu/` に保存します。内部のpkcs7（Andrew Smith、MIT）とlzw（The Go Authors、BSD-3-Clause）のlicenseも同梱します。

日本語には[BIZ UDPゴシックv1.051](https://github.com/googlefonts/morisawa-biz-ud-gothic/tree/v1.051)（Morisawa、OFL-1.1）を使い、OFL原文と著作権表示を `BIZ-UDGothic-OFL.txt` に保持します。pdfcpuに同梱されたRoboto RegularはApache-2.0で、`Copyright 2011 Google Inc. All Rights Reserved.` とlicense全文を `Roboto-NOTICE.txt` / `Roboto-LICENSE.txt` に収録します。

透かしの生成表 `WatermarkGlyphs.g.cs`（字幅・字形収録bitset）も、この固定BIZ UDPゴシック由来です。OFL-1.1の表示と原文を同じ場所に保持します。

差し込みの生成表 `FillTemplateGlyphs.g.cs`（文字・字形番号・送り幅）も、この固定BIZ UDPゴシック由来です。OFL-1.1の表示と原文を同じ場所に保持します。

`third_party/pdfcpu-modules.json` は固定go.modのindirectを含む11 module、version、Go module checksum、licenseファイル、Linux binaryへの組込みの有無を記録します。Linux x86_64配布binaryの `go version -m` は9 moduleを示し、jsonschema-goとWindows用mousetrapは組み込まれていません。11件ともlicense原文・付属NOTICE/PATENTSを `third_party/pdfcpu/modules/` に保持します。

| Module | Version | License |
| --- | --- | --- |
| github.com/clipperhouse/uax29/v2 | v2.7.0 | MIT |
| github.com/google/jsonschema-go | v0.4.3 | MIT |
| github.com/hhrutter/tiff | v1.0.7 | BSD-3-Clause（内部lzwの表示も収録） |
| github.com/inconshreveable/mousetrap | v1.1.0 | Apache-2.0 |
| github.com/mattn/go-runewidth | v0.0.30 | MIT |
| github.com/spf13/cobra | v1.10.2 | Apache-2.0 |
| github.com/spf13/pflag | v1.0.10 | BSD-3-Clause |
| go.yaml.in/yaml/v3 | v3.0.5 | MIT / Apache-2.0（原文の両条件を保持） |
| golang.org/x/crypto | v0.57.0 | BSD-3-Clause |
| golang.org/x/image | v0.46.0 | BSD-3-Clause |
| golang.org/x/text | v0.42.0 | BSD-3-Clause |

配布binaryのGo runtime・標準ライブラリはGo 1.27.1、CGO_ENABLED=0です。GoのBSD-3-Clause全文、PATENTS、著作権表示とruntime vendorのlicenseを `Go-LICENSE.txt` / `Go-PATENTS.txt` / `Go-COPYRIGHTS.txt` / `go-runtime-vendor/` に収録します。pdfcpu側の同梱config・default certificate dataは同プロジェクトのApache-2.0表示とともに扱い、外部から起動時に取得しません。

installerは上記すべてとmodule一覧、このnoticeをfinal imageの `/opt/amane-pdf/licenses/` にコピーします。`third_party/pdfcpu-licenses.sha256` とDocker smokeで原文の一致を検査します。更新時は固定Release・go.mod・binary metadata・各licenseを再照合してください。照合用Goと取得toolはruntimeへ追加しません。

## 描画プログラム（PDFium）

PDFium [chromium/8100（157.0.8100.0）](https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8100) を別プロセスの Amane.Pdf.Renderer から直接P/Invokeします。PDFtoImage・SkiaSharpは使用しません。third_party/pdfium.lock のtgz全体のsha256を検証し、共有ライブラリとライセンス原文だけを取り出します。

配布imageの /opt/amane-pdf/licenses/pdfium/ に、build scriptの LICENSE（MIT、Copyright Benoit BLANCHON）と、下表の licenses/ 17ファイルを改変せず同梱します。各著作権表示、NOTICE、許諾条件の全文はこれらの原文に保持します。PDFiumに含まれるlibjpeg-turboと、OSの libturbojpeg.so.0 は別の配布物です。

| ソフトウェア | ライセンス | 配布image内の原文（上記dirからの相対path） |
| --- | --- | --- |
| PDFium（The PDFium Authors） | BSD-3-Clause / Apache-2.0 | licenses/pdfium.txt |
| pdfium-binaries build script（Benoit BLANCHON） | MIT | LICENSE |
| FreeType | FreeType Project License（FTL） | licenses/freetype.txt |
| libjpeg-turbo | IJG / BSD-3-Clause / zlib | licenses/libjpeg_turbo.md、licenses/libjpeg_turbo.ijg |
| OpenJPEG | BSD-2-Clause | licenses/libopenjpeg.txt |
| lcms2 | MIT | licenses/lcms.txt |
| libpng | PNG Reference Library License v2 | licenses/libpng.txt |
| zlib | zlib | licenses/zlib.txt |
| AGG 2.3 | AGGのpermissive license | licenses/agg23.txt |
| abseil | Apache-2.0 | licenses/abseil.txt |
| ICU | Unicode License v3 と同梱の第三者表示 | licenses/icu.txt |
| HarfBuzz | Old MIT と同梱の第三者表示 | licenses/harfbuzz.txt |
| fast_float | MIT | licenses/fast_float.txt |
| simdutf | MIT | licenses/simdutf.txt |
| dragonbox | Apache-2.0 WITH LLVM-exception / BSL-1.0 | licenses/dragonbox-Apache2-LLVM.txt、licenses/dragonbox-Boost.txt |
| llvm-libc | Apache-2.0 WITH LLVM-exception | licenses/llvm-libc.txt |

FreeTypeの配布表示: Portions of this software are copyright © 1996-2002, 2006 The FreeType Project (www.freetype.org). All rights reserved.

描画用の日本語フォントは上記と同じ固定版BIZ UDPゴシックのTTFです。OFL原文と著作権表示は /opt/amane-pdf/licenses/BIZ-UDGothic-OFL.txt に保持します。

## qpdf

このプロジェクトはPDF処理にqpdfを使用します。

- プロジェクト: qpdf
- 公式Repository: https://github.com/qpdf/qpdf
- ライセンス: Apache License 2.0

qpdf本体の著作権表示およびライセンス条件は、qpdfの配布物と公式Repositoryを参照してください。

## prlimit（util-linux）

Linuxではqpdfのメモリ制限にprlimitを使用します。

- プロジェクト: util-linux
- 公式Repository: https://github.com/util-linux/util-linux
- prlimitのライセンス: GPL-2.0-or-later
- コンテナ内の著作権・ライセンス情報: `/usr/share/doc/util-linux/copyright`

prlimitはベースOSに元から含まれるユーティリティを別プロセスとして実行します。APIコードへリンクしません。Docker smokeでcopyrightファイルの存在を確認します。

## libjpeg-turbo（djpeg／cjpeg／jpegtranとJPEGライブラリ）

LinuxのJPEG画像縮小・再圧縮に `libjpeg-turbo-progs` のdjpeg／cjpeg、画像→PDFの向き補正・metadata除去に同じパッケージのjpegtranを別プロセスとして使用します。APIコードへ新しい画像ライブラリをリンクしません。runtimeのqpdfの依存として既に導入されていた `libjpeg-turbo8` はqpdf等が使うlibjpeg互換ライブラリです。今回 `libjpeg-turbo-progs` とともに追加される `libturbojpeg0` はTurboJPEG APIの共有ライブラリで、APTの依存として同梱されます。別プロセスの描画プログラムは `libturbojpeg.so.0` を直接P/Invokeし、PDFiumのbitmapからJPEGを作ります。APIプロセス内では呼びません。

- 公式Repository: https://github.com/libjpeg-turbo/libjpeg-turbo
- ライセンス構成: IJG、BSD-3-Clause、zlib（各ファイルの条件は配布物を参照）
- コンテナ内のcopyright: `/usr/share/doc/libjpeg-turbo-progs/copyright`、`/usr/share/doc/libjpeg-turbo8/copyright`、`/usr/share/doc/libturbojpeg0/copyright`
- Docker smokeで上の3ファイルの存在を検査します。

IJGのバイナリ配布に伴う表示:

> This software is based in part on the work of the Independent JPEG Group.

Ubuntu配布物に記載された著作権者にはThomas G. Lane、Guido Vollbeding、MIYASAKA Masaru、D. R. Commander、Nokia Corporation、Pierre Ossman／Cendio AB、Siarhei Siamashka、Linaro、MIPS Technologies、Matthieu Darbois、Google、Intel、Arm等が含まれます。ファイルごとの年・権利者・条件は上記copyrightファイルに保持されます。

BSD-3-Clause全文（配布物のlibjpeg-turbo表示）:

```text
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

- Redistributions of source code must retain the above copyright notice,
  this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
- Neither the name of the libjpeg-turbo Project nor the names of its
  contributors may be used to endorse or promote products derived from this
  software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS",
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDERS OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

zlibライセンスの部分には、無保証・損害責任の否認、商用利用を含む利用・変更・再配布の許可、原作者の詐称禁止、変更版の明示、ソース配布時の表示保持が定められています。第三者ソフトウェアの原著作権表示・ライセンスを削除せず、上のcopyrightファイルをコンテナへ残します。poppler-utils／Pillow／NumPyはCIと一時fixture生成・測定のみに使い、runtime imageへ追加しません。
