# 第三者ソフトウェア

## pdfcpu・日本語font・Go依存物

pdfcpu [v0.16.1](https://github.com/pdfcpu/pdfcpu/tree/v0.16.1) は別プロセスで使用します。ライセンスはApache-2.0、原文とThe pdfcpu Authorsの著作権表示を `third_party/pdfcpu/` に保存します。内部のpkcs7（Andrew Smith、MIT）とlzw（The Go Authors、BSD-3-Clause）のlicenseも同梱します。

日本語には[BIZ UDPゴシックv1.051](https://github.com/googlefonts/morisawa-biz-ud-gothic/tree/v1.051)（Morisawa、OFL-1.1）を使い、OFL原文と著作権表示を `BIZ-UDGothic-OFL.txt` に保持します。pdfcpuに同梱されたRoboto RegularはApache-2.0で、`Copyright 2011 Google Inc. All Rights Reserved.` とlicense全文を `Roboto-NOTICE.txt` / `Roboto-LICENSE.txt` に収録します。

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

## libjpeg-turbo（djpeg／cjpegとJPEGライブラリ）

LinuxのJPEG画像縮小・再圧縮に `libjpeg-turbo-progs` のdjpeg／cjpegを別プロセスとして使用します。APIコードへ新しい画像ライブラリをリンクしません。runtimeのqpdfの依存として既に導入されていた `libjpeg-turbo8` はqpdf等が使うlibjpeg互換ライブラリです。今回 `libjpeg-turbo-progs` とともに追加される `libturbojpeg0` はTurboJPEG APIの共有ライブラリで、APTの依存として同梱されます。APIはTurboJPEG APIを直接呼びません。

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
