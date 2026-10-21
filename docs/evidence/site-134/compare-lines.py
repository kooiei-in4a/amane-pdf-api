#!/usr/bin/env python3
"""固定版の罫線だけを整理する比較サンプル。製品テンプレートは変更しない。"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil

import reproduce as r


def sample_template():
    # 元generatorと同じ最小PDF構造。文字は引き続き既存fill-template APIで描く。
    # 左上原点の比率を、A4のPDF座標へ変換する。
    commands = ["q", "0.96 g", f"29.750 {(1-.368)*842:.3f} 535.500 {(.368-.333)*842:.3f} re f"]

    def rule(y, left=.05, right=.95, gray=.80, weight=.35):
        commands.append(f"{gray:.2f} G {weight:.2f} w {left*595:.3f} {(1-y)*842:.3f} m {right*595:.3f} {(1-y)*842:.3f} l S")

    rule(.368, gray=.45, weight=.70)
    for row in range(8):
        rule(.404+row*.031)
    rule(.748, left=.52, right=.93, gray=.35, weight=.85)
    rule(.790, left=.52, right=.93, gray=.35, weight=.85)
    commands.append("Q")
    stream = ("\n".join(commands)+"\n").encode("ascii")
    bodies = [b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Rotate 0 /Resources << >> /Contents 4 0 R >>",
        b"<< /Length "+str(len(stream)).encode()+b" >>\nstream\n"+stream+b"endstream"]
    pdf = b"%PDF-1.7\n"
    offsets = []
    for index, body in enumerate(bodies,1):
        offsets.append(len(pdf))
        pdf += f"{index} 0 obj\n".encode()+body+b"\nendobj\n"
    xref = len(pdf)
    pdf += b"xref\n0 5\n0000000000 65535 f \n"
    pdf += b"".join(f"{offset:010} 00000 n \n".encode() for offset in offsets)
    pdf += f"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    return pdf


def gallery(output):
    sections=[]
    for kind,label in (("invoice","請求書"),("estimate","見積書")):
        for count in (1,8):
            case=f"{kind}-{count}"
            columns="".join(f'<figure><figcaption>{caption} · <a href="{case}/{name}.pdf">PDF</a></figcaption><a href="{case}/{name}.pdf"><img src="{case}/{name}.png" alt="{label}{count}明細 {caption}" loading="lazy"></a></figure>' for name,caption in (("current","現行"),("final","罫線整理案")))
            sections.append(f'<section><h2>{label}・{count}明細</h2><p><a href="{case}/comparison.pdf">2ページ比較PDF（1:現行 → 2:整理案）</a></p><div class="pair">{columns}</div></section>')
    (output/"index.html").write_text('''<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>帳票の罫線比較</title><style>body{font-family:system-ui,sans-serif;background:#f2f3f5;color:#20262e;margin:24px auto;max-width:1440px;padding:0 20px}h1{font-size:24px}p{line-height:1.7}.pair{display:grid;grid-template-columns:1fr 1fr;gap:20px}figure{margin:0}figcaption{padding:12px 0;font-weight:600}img{width:100%;background:white;box-shadow:0 2px 8px #0002}section{margin:40px 0 64px}a{color:#164fa3}@media(max-width:700px){.pair{grid-template-columns:1fr}}</style><h1>請求書・見積書 — 罫線整理の比較</h1><p>同じ入力・ロゴ・印影・文字位置で比較。宛先等の囲み枠と明細の縦線を外し、薄い横線と合計の上下線を使っています。合計だけ文字サイズを大きくしています。製品未反映の検討用サンプルです。金額は以前の描画検証と同じ意図的な不整合データで、実際の取引用ではありません。</p>'''+"".join(sections)+"</html>\n")


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--output",type=Path,default=Path("/tmp/amane-site134-line-comparison"))
    args=parser.parse_args()
    output=args.output.resolve()
    output.mkdir(parents=True,exist_ok=True)
    template=sample_template()
    (output/"refined-template.pdf").write_bytes(template)
    r.command("qpdf","--check",str(output/"refined-template.pdf"))
    pics=[(r.ROOT/"results"/name).read_bytes() for name in ("logo.png","seal.png")]
    results={"api_sha":r.API_SHA,"site_sha":r.SITE_SHA,"cases":{},
             "template_sha256":hashlib.sha256(template).hexdigest(),
             "changes":["non-table boxes removed","table vertical lines removed; horizontal rules retained", "gray table heading background", "total top/bottom rules; total font 9->12pt and label 9->10pt"],
             "unchanged":["all text and amounts", "all field rectangles", "all fields except two font sizes", "logo/seal files and placement", "fixed 8 detail rows and 1-page layout"],
             "image_id":json.loads(r.smoke.docker("image","inspect","amane-pdf-api:site134-465088e").stdout)[0]["Id"]}
    with r.smoke.running_container("amane-pdf-api:site134-465088e",isolated=True) as api:
        for kind in ("invoice","estimate"):
            definition=json.loads((r.ASSETS/kind/"definition.json").read_text())
            revised=copy.deepcopy(definition)
            for f in revised["fields"]:
                if f["key"]=="total": f["fontSize"]=12
                if f["key"]=="label.total": f["fontSize"]=10
            revised["sha256"]=results["template_sha256"]
            r.save(output/(kind+".definition.json"),revised)
            for count in (1,8):
                name=f"{kind}-{count}"
                directory=output/name
                directory.mkdir(exist_ok=True)
                original=r.ROOT/"results"/name
                values=json.loads((original/"input.display-values.json").read_text())
                fields,keys=r.fields_for(revised,values)
                prior_fields=json.loads((original/"fields.json").read_text())
                differences=[]
                for key,before,after in zip(keys,prior_fields,fields):
                    for prop in before:
                        if before[prop]!=after[prop]:
                            differences.append({"key":key,"property":prop,"before":before[prop],"after":after[prop]})
                assert differences==[{"key":"label.total","property":"fontSize","before":9,"after":10},{"key":"total","property":"fontSize","before":9,"after":12}]
                r.save(directory/"fields.json",fields)
                r.save(directory/"field-index.json",keys)
                shutil.copyfile(original/"input.display-values.json",directory/"input.display-values.json")
                shutil.copyfile(original/"final.pdf",directory/"current.pdf")
                shutil.copyfile(original/"final.png",directory/"current.png")
                pdfs,measure=r.chain(api,template,fields,pics)
                api.assert_clean()
                for stage,pdf in zip(("filled","logo","final"),pdfs):
                    path=directory/(stage+".pdf")
                    path.write_bytes(pdf)
                    if stage=="final":
                        measure["extraction"]=r.check_pdf(path,fields,keys)
                    else:
                        r.command("pdftoppm","-singlefile","-r","144","-png",str(path),str(path.with_suffix("")))
                measure["images"]=r.image_check(directory,[(.05,.04,.18),(.82,.26,.1)])
                actual=json.loads((directory/"final.extracted.json").read_text())
                prior=json.loads((original/"final.extracted.json").read_text())
                assert actual==prior
                r.command("qpdf","--empty","--pages",str(directory/"current.pdf"),"1",str(directory/"final.pdf"),"1","--",str(directory/"comparison.pdf"))
                assert r.command("qpdf","--show-npages",str(directory/"comparison.pdf")).strip()==b"2"
                r.command("qpdf","--check",str(directory/"comparison.pdf"))
                results["cases"][name]=measure|{"differences":differences,"text_matches_current":True,
                    "current_sha256":hashlib.sha256((directory/"current.pdf").read_bytes()).hexdigest(),
                    "refined_sha256":hashlib.sha256(pdfs[-1]).hexdigest()}
                r.save(output/"results.json",results)
        api.assert_clean()
    gallery(output)
    print(json.dumps({k:{"seconds":round(v["total_seconds"],3),"bytes":v["seal"]["bytes"],"text_matches_current":v["text_matches_current"]} for k,v in results["cases"].items()}))


if __name__=="__main__":
    main()
