#!/usr/bin/env python3
"""site #134 描画だけの小さな検証。標準JSON契約・業務計算は実装しない。"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import importlib.util
import io
import json
import math
from pathlib import Path
import platform
import subprocess
import threading
import time
import xml.etree.ElementTree as ET

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
API_SHA = "465088ed0b575f055beb6851e98d28ed22fb9928"
SITE_SHA = "e08aed241245f9f84a7336302edcb7d6a89a57b1"
ASSETS = ROOT / "site-assets/src/Amane.Tools.Site/wwwroot/pdf-templates"
spec = importlib.util.spec_from_file_location("smoke", ROOT / "api-source/scripts/docker-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, timeout=60).stdout


def save(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n")


def fixture(kind, count):
    # 金額は意図的に数量×単価や税計算と一致させず、受け取った確定値を使う。
    values = dict(recipient="架空取引先株式会社 御中", date="2026年10月22日",
                  issuer="架空帳票研究株式会社", subject="帳票描画検証", number=f"TEST-{kind[:3]}-{count}",
                  expiry="2026年11月30日", address="東京都架空区見本一丁目\n試験ビル二階", registration="T0000000000000",
                  contact="00-0000-0000", subtotal10="12,345円", tax10="777円",
                  subtotal8="6,789円" if count == 8 else "0円", tax8="333円" if count == 8 else "0円",
                  total="99,999円", bank="架空銀行 見本支店\n普通 0000000", notes="取引期間\n2026/10/01～10/31" if count == 8 else "取引日 2026/10/20")
    for i in range(count):
        values.update({f"row{i}.name": f"試験品目{i+1}" + ("※" if i % 2 else ""),
                       f"row{i}.quantity": ["1.25", "0.125", "2.50", "3", "4.75", "5", "6.5", "7"][i],
                       f"row{i}.price": "100円", f"row{i}.rate": "8%" if i % 2 else "10%",
                       f"row{i}.amount": f"{98765+i:,}円"})
    return values


def fields_for(definition, values):
    # computed の指定も無視し、表示済み文字列をそのままマッピングする。
    # 存在しないkeyは黙って捨てず検証driverで拒否。siteの実装ではない。
    keys = {f["key"] for f in definition["fields"]}
    unknown = set(values) - keys
    if unknown:
        raise ValueError(f"表示先なし: {sorted(unknown)}")
    output, mapping = [], []
    for f in definition["fields"]:
        text = f.get("text") if f["type"] == "fixed" else values.get(f["key"])
        if text is None:
            continue
        output.append(dict(page=f["page"], **f["rect"], fontSize=f["fontSize"], align=f["align"], text=text))
        mapping.append(f["key"])
    return output, mapping


def images(directory):
    logo = Image.new("RGBA", (300, 100))
    d = ImageDraw.Draw(logo)
    d.rectangle((5, 5, 294, 94), outline=(20, 70, 170, 255), width=4)
    d.rectangle((30, 25, 90, 75), fill=(20, 70, 170, 128))
    d.text((110, 40), "FICTIONAL LAB", fill=(20, 70, 170, 255))
    seal = Image.new("RGBA", (120, 120))
    d = ImageDraw.Draw(seal)
    d.ellipse((4, 4, 115, 115), outline=(210, 20, 30, 200), width=6)
    d.rectangle((45, 45, 74, 74), fill=(210, 20, 30, 128))
    d.text((40, 25), "TEST", fill=(210, 20, 30, 255))
    logo.save(directory / "logo.png")
    seal.save(directory / "seal.png")
    return [(directory / name).read_bytes() for name in ("logo.png", "seal.png")]


def post(api, source, fields=None, image=None, query=""):
    start = time.monotonic()
    body, content_type = smoke.multipart(source, password=None, image=image,
        restriction={"fields": json.dumps(fields, ensure_ascii=False, separators=(",", ":"))} if fields is not None else None)
    status, headers, pdf = api.request("POST", "/api/pdf/" + ("fill-template" if fields is not None else "stamp-image" + query), body, content_type)
    elapsed = time.monotonic() - start
    return status, pdf, {"seconds": elapsed, "status": status, "bytes": len(pdf),
        "content_type": headers.get("Content-Type"), "problem": None if status == 200 else json.loads(pdf)}


def chain(api, template, fields, pics, placement=None):
    placement = placement or ["?page=1&x=0.05&y=0.04&width=0.18", "?page=1&x=0.82&y=0.26&width=0.1"]
    start = time.monotonic()
    status, filled, fill = post(api, template, fields=fields)
    assert status == 200, fill
    status, logo, stamp1 = post(api, filled, image=pics[0], query=placement[0])
    assert status == 200, stamp1
    status, final, stamp2 = post(api, logo, image=pics[1], query=placement[1])
    assert status == 200, stamp2
    seconds = time.monotonic() - start
    return [filled, logo, final], {"fill": fill, "logo": stamp1, "seal": stamp2, "total_seconds": seconds}


def check_pdf(path, fields, mapping):
    command("qpdf", "--check", str(path))
    assert command("qpdf", "--show-npages", str(path)).strip() == b"1"
    raw = command("pdftotext", "-raw", str(path), "-").decode()
    path.with_suffix(".txt").write_text(raw)
    # Whole-page extraction joins adjacent columns. Compare each original rectangle;
    # remove only Poppler's final form-feed, without Unicode normalization/space deletion.
    extracted = {}
    for key,f in zip(mapping, fields):
        x,y = math.floor(f["x"]*595), math.floor(f["y"]*842)
        w,h = math.ceil((f["x"]+f["width"])*595)-x, math.ceil((f["y"]+f["height"])*842)-y
        actual=command("pdftotext","-raw","-r","72","-x",str(x),"-y",str(y),"-W",str(w),"-H",str(h),str(path),"-").decode()
        assert actual == f["text"]+"\f", (path.name,key,repr(actual),repr(f["text"]))
        extracted[key]=actual[:-1]
    save(path.with_suffix(".extracted.json"),extracted)
    bbox = command("pdftotext", "-bbox", str(path), "-")
    path.with_suffix(".bbox.html").write_bytes(bbox)
    tree = ET.fromstring(bbox)
    words = tree.findall(".//{http://www.w3.org/1999/xhtml}word")
    # Each word must fit the originally requested rectangle. Ascender/descent uses font metrics.
    outside = []
    for word in words:
        x0,y0,x1,y1 = [float(word.attrib[k]) for k in ("xMin","yMin","xMax","yMax")]
        if not any(x0 >= f["x"]*595-.1 and x1 <= (f["x"]+f["width"])*595+.1 and
                   y0 >= f["y"]*842-.1 and y1 <= (f["y"]+f["height"])*842+.1 for f in fields):
            outside.append(word.text)
    assert not outside, outside
    command("pdftoppm", "-singlefile", "-r", "144", "-png", str(path), str(path.with_suffix("")))
    return {"exact_per_rectangle_text": True, "characters": sum(len(f["text"]) for f in fields), "words": len(words), "words_outside_rectangles": outside, "pages": 1}


def image_check(directory, placement):
    base = np.array(Image.open(directory / "filled.png").convert("RGB"))
    after_logo = np.array(Image.open(directory / "logo.png").convert("RGB"))
    final = np.array(Image.open(directory / "final.png").convert("RGB"))
    rows = []
    for name, prior, after, (x,y,w), dimensions, color, samples in [
        ("logo", base, after_logo, placement[0], (300,100), (20,70,170), [(60,50,128),(150,15,0),(6,50,255)]),
        ("seal", after_logo, final, placement[1], (120,120), (210,20,30), [(50,50,128),(60,60,128),(10,10,0),(60,6,200)])]:
        left, top, width = x*595*2, y*842*2, w*595*2
        height = width*dimensions[1]/dimensions[0]
        delta = np.any(after != prior, axis=2)
        ys,xs = np.nonzero(delta)
        assert len(xs) and xs.min() >= left-2 and xs.max() <= left+width+2
        assert ys.min() >= top-2 and ys.max() <= top+height+2
        sampled = []
        for sx,sy,alpha in samples:
            px,py = round(left+sx/dimensions[0]*width), round(top+sy/dimensions[1]*height)
            expected = prior[py,px]*(1-alpha/255)+np.array(color)*alpha/255
            error = abs(after[py,px].astype(float)-expected).max()
            assert error <= 5, (name,sx,sy,error,after[py,px],expected)
            sampled.append({"alpha":alpha,"max_channel_error":float(error)})
        rows.append({"image":name,"expected_pt":[left/2,top/2,width/2,height/2],
                     "changed_pixel_bbox":[int(xs.min()),int(ys.min()),int(xs.max()),int(ys.max())],"samples":sampled})
    return rows


def image_limits(api, template, pics, output):
    large = io.BytesIO()
    Image.new("RGBA", (2001,10), (210,20,30,128)).save(large,format="PNG")
    cases = [
        ("width_too_small", pics[0], "?page=1&x=0&y=0&width=0.0199",400,None),
        ("bottom_overflow", pics[1], "?page=1&x=0.1&y=0.99&width=0.1",400,None),
        ("side_2001", large.getvalue(), "?page=1&x=0.1&y=0.1&width=0.1",422,"unsupported-image"),
        ("over_1MiB", pics[0]+b"\0"*1048576, "?page=1&x=0.1&y=0.1&width=0.1",413,None),
    ]
    rows={}
    for name,pic,query,expected,reason in cases:
        status,_,measure=post(api,template,image=pic,query=query)
        assert status==expected and measure["problem"].get("reason")==reason,measure
        rows[name]=measure
        api.assert_clean()
    save(output/"image-limits.json",rows)
    return rows


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--image", default="amane-pdf-api:site134-465088e")
    p.add_argument("--output", type=Path, default=ROOT/"results")
    args = p.parse_args()
    output = args.output.resolve()
    output.mkdir(exist_ok=True)
    pics = images(output)
    definitions = {kind:json.loads((ASSETS/kind/"definition.json").read_text()) for kind in ("invoice","estimate")}
    # 不要なbankをestimateへ持ち込まない。それ以外の未知keyはfields_forで失敗する。
    fixtures = {}
    for kind in definitions:
        for count in (1,8):
            values = fixture(kind,count)
            if kind == "estimate": del values["bank"]
            fields, mapping = fields_for(definitions[kind],values)
            fixtures[f"{kind}-{count}"] = (values,fields,mapping)
    results = {"api_sha":API_SHA,"site_sha":SITE_SHA,"environment":{
        "platform":platform.platform(),"python":platform.python_version(),"numpy":np.__version__,
        "pillow":Image.__version__,"poppler":subprocess.run(["pdftotext","-v"],capture_output=True).stderr.decode(),
        "host_qpdf":command("qpdf","--version").decode(),"image":json.loads(smoke.docker("image","inspect",args.image).stdout)[0]["Id"],
        "cpu":command("lscpu").decode(),"transport":"existing smoke ApiContainer isolated=True, docker exec loopback HTTP",
        "limits":"CPU=1, RAM=1536MiB, swap=1536MiB (no extra swap), tmpfs=256MiB, read-only, network=none, default shared=2/queue=0"},
        "criteria_proposal":{"serial_3_calls_seconds":10,"two_chains_seconds_each":20,"final_bytes":2097152,"status":"検証上の仮目安。標準JSON/SLAの合意ではない"},
        "assets_sha256":{str(f.relative_to(ROOT)):hashlib.sha256(f.read_bytes()).hexdigest() for f in ASSETS.glob("*/*")},
        "cases":{},"limits":{}}
    with smoke.running_container(args.image, isolated=True) as api:
        results["environment"]["runtime"] = api.exec("dotnet","--info").stdout.decode()
        results["environment"]["qpdf"] = api.exec("qpdf","--version").stdout.decode()
        results["environment"]["pdfcpu"] = api.exec("/opt/amane-pdf/bin/pdfcpu","version").stdout.decode()
        for name,(values,fields,mapping) in fixtures.items():
            directory = output/name
            directory.mkdir(exist_ok=True)
            kind = name.split("-")[0]
            save(directory/"input.display-values.json",values)
            save(directory/"fields.json",fields)
            save(directory/"field-index.json",mapping)
            pdfs, measure = chain(api,(ASSETS/kind/"template.pdf").read_bytes(),fields,pics)
            api.assert_clean()
            measure["fields"] = len(fields)
            measure["lines"] = sum(f["text"].count("\n")+1 for f in fields)
            measure["extraction"] = {}
            for stage,pdf in zip(("filled","logo","final"),pdfs):
                path = directory/(stage+".pdf")
                path.write_bytes(pdf)
                measure["extraction"][stage] = check_pdf(path,fields,mapping)
            measure["images"] = image_check(directory,[(.05,.04,.18),(.82,.26,.1)])
            results["cases"][name] = measure
            save(output/"results.json",results)
        # 最大文字数付近でも文字が同じか。APIの幾何判定とsite定義のmaxLengthは別物。
        for kind in definitions:
            values = dict(fixtures[f"{kind}-8"][0])
            values.update(issuer="架空日本帳票研究開発株式会社本社",recipient="架空日本帳票取引先株式会社御中様",
                          address="東京都架空試験区見本町一丁目二番地\n架空帳票研究開発試験センター第一棟")
            values["row0.name"] = "架空帳票生成用長文検証試験商品名称"
            fields,mapping = fields_for(definitions[kind],values)
            directory = output/(kind+"-long")
            directory.mkdir(exist_ok=True)
            save(directory/"input.display-values.json",values)
            save(directory/"fields.json",fields)
            status,pdf,measure = post(api,(ASSETS/kind/"template.pdf").read_bytes(),fields=fields)
            assert status == 200, measure
            path=directory/"filled.pdf"
            path.write_bytes(pdf)
            measure["extraction"] = check_pdf(path,fields,mapping)
            results["cases"][kind+"-long"] = measure
            api.assert_clean()
        fields = fixtures["invoice-8"][1]
        template = (ASSETS/"invoice/template.pdf").read_bytes()
        # 9行目はAPIが業務上の行数を知らないことを示す。8行目の次の座標へ直接送る。
        ninth = []
        definition = definitions["invoice"]
        extra_values = {f"row8.{key}":value for key,value in dict(name="第九明細",quantity="9",price="100円",rate="10%",amount="909円").items()}
        try:
            fields_for(definition, fixtures["invoice-8"][0] | extra_values)
        except ValueError as e:
            results["limits"]["ninth_mapping_guard"]={"error":str(e),"source":"検証用mapperのみ。site側未実装"}
        else: raise AssertionError("unknown mapping accepted")
        for key,value in extra_values.items():
            original = definition["fields"][next(i for i,f in enumerate(definition["fields"]) if f["key"] == key.replace("row8.","row7."))]
            ninth.append(dict(page=1,**(original["rect"]|{"y":.623}),fontSize=original["fontSize"],align=original["align"],text=value))
        status,pdf,measure = post(api,template,fields=fields+ninth)
        assert status == 200,measure
        (output/"ninth-direct.pdf").write_bytes(pdf)
        save(output/"ninth-direct.fields.json",fields+ninth)
        command("pdftoppm","-singlefile","-r","144","-png",str(output/"ninth-direct.pdf"),str(output/"ninth-direct"))
        raw = command("pdftotext","-raw",str(output/"ninth-direct.pdf"),"-").decode()
        assert "第九明細" in raw and "909円" in raw
        (output/"ninth-direct.txt").write_text(raw)
        results["limits"]["ninth_direct"] = measure | {"extracted":True,"layout":"y=.623 at reduced-note y=.632: overlap"}
        probes = {
            "issuer_overflow":("issuer","架空"*20,422), "recipient_overflow":("recipient","架空"*20,422),
            "address_overflow":("address","架空"*20+"\n"+"住所"*20,422),
            "item_overflow":("row0.name","長文"*20,422),"quantity_overflow":("row0.quantity","12345.6789",422),
            "supplementary":("issuer","株式会社𠮷野",400),"emoji":("issuer","架空😀",400),
            "variation_selector":("issuer","葛\ufe00",400),"percent_placeholder":("issuer","架空%p",400),
            "missing_text_key":("issuer",None,400),"empty_text":("issuer","",400),
            "nfd":("issuer","カ\u3099",200),"literal_percent":("issuer","末尾% 連続%% 8%",200),
        }
        keymap = fixtures["invoice-8"][2]
        for name,(key,value,expected) in probes.items():
            modified = [dict(f) for f in fields]
            target=modified[keymap.index(key)]
            if value is None: del target["text"]
            else: target["text"]=value
            save(output/(name+".fields.json"),modified)
            status,pdf,measure=post(api,template,fields=modified)
            assert status == expected,measure
            if status == 200:
                path=output/(name+".pdf")
                path.write_bytes(pdf)
                measure["extraction"] = check_pdf(path,modified,keymap)
            else:
                if status == 422: assert measure["problem"].get("reason")=="field-overflow"
                save(output/(name+".problem.json"),measure["problem"])
            results["limits"][name]=measure
            api.assert_clean()
        # 完全に項目を省く場合、APIは必須業務項目を知らず成功する。
        omitted=[f for i,f in enumerate(fields) if keymap[i]!="issuer"]
        status,pdf,measure=post(api,template,fields=omitted)
        assert status == 200
        (output/"omitted-issuer.pdf").write_bytes(pdf)
        save(output/"omitted-issuer.fields.json",omitted)
        results["limits"]["omitted_required_business_field"]=measure
        # 文字→logo→sealの重ね順を同じ位置で検査。既存layout資産は変更しない。
        directory=output/"overlap-order"
        directory.mkdir(exist_ok=True)
        pdfs,measure=chain(api,template,fields,pics,["?page=1&x=0.17&y=0.185&width=0.18","?page=1&x=0.17&y=0.185&width=0.1"])
        for stage,pdf in zip(("filled","logo","final"),pdfs):
            path=directory/(stage+".pdf")
            path.write_bytes(pdf)
            check_pdf(path,fields,keymap)
        measure["images"]=image_check(directory,[(.17,.185,.18),(.17,.185,.1)])
        results["cases"]["overlap_order"]=measure
        results["repeat_invoice8"]=[]
        for _ in range(3):
            _,measure=chain(api,template,fields,pics)
            results["repeat_invoice8"].append(measure)
            api.assert_clean()
        barrier=threading.Barrier(2)
        def concurrent(kind):
            barrier.wait()
            pdfs,measure=chain(api,(ASSETS/kind/"template.pdf").read_bytes(),fixtures[kind+"-8"][1],pics)
            path=output/(kind+"-concurrent-final.pdf")
            path.write_bytes(pdfs[-1])
            measure["extraction"]=check_pdf(path,fixtures[kind+"-8"][1],fixtures[kind+"-8"][2])
            return measure
        with ThreadPoolExecutor(max_workers=2) as pool:
            results["concurrent2"]=dict(zip(("invoice","estimate"),pool.map(concurrent,("invoice","estimate"))))
        api.assert_clean()
        results["health_status"]=api.request("GET","/healthz")[0]
        results["memory_events"]=api.exec("cat","/sys/fs/cgroup/memory.events").stdout.decode()
        results["memory_peak_bytes"]=int(api.exec("cat","/sys/fs/cgroup/memory.peak").stdout)
        results["cleanup"]="existing api.assert_clean passed after sequential requests and after both chains finished"
        results["image_limits"]=image_limits(api,template,pics,output)
    save(output/"results.json",results)
    print(json.dumps({k:round(v.get("total_seconds",v.get("seconds",0)),3) for k,v in results["cases"].items()}))


if __name__ == "__main__":
    main()
