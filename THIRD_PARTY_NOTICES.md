# 第三者ソフトウェア

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
