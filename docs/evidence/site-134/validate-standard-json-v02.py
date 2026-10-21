#!/usr/bin/env python3
"""#134 標準JSON v0.2候補のfixture検証。製品実装・正式HTTP契約ではない。"""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import platform
import re
import time

import reproduce as r

spec = importlib.util.spec_from_file_location("quantity_resolver", r.ROOT / "validate-quantity-resolution.py")
qr = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qr)
v = qr.v
Invalid, fail, child = v.Invalid, v.fail, v.child
read_json, obj, text = v.read_json, v.obj, v.text
lines, identifier, day, integer = v.lines, v.identifier, v.day, v.integer
company_check, WIDTHS = v.company_check, v.WIDTHS
DRAFT = r.ROOT / "standard-json-draft-v0.2"
ALIGN = "quantityAlignment"


def map_fields(company, settings, request, definition):
    """fixtureのcompany_demoのみを解決し、v0.2候補を表示文字列へ変換する。"""
    obj(request, "", {"companyId", "templateId", "currency", "recipient", "issueDate", "number", "subject", "paymentDueDate", "validUntil", "transaction", "items", "taxSummary", "totalAmount", "notesLines", "quantityAlignment"},
        ("companyId", "templateId", "currency", "recipient", "issueDate", "number", "items", "taxSummary", "totalAmount"))
    identifier(request["companyId"], "/companyId")
    if request["companyId"] != "company_demo": fail("/companyId", "invalid-reference")
    identifier(request["templateId"], "/templateId")
    if request["templateId"] not in ("invoice-standard-v1", "estimate-standard-v1"): fail("/templateId", "invalid-reference")
    kind = request["templateId"].split("-")[0]
    if definition["id"] != kind: fail("/templateId", "invalid-reference")
    if type(request["currency"]) is not str: fail("/currency", "invalid-type")
    if request["currency"] != "JPY": fail("/currency", "unsupported-currency")
    obj(request["recipient"], "/recipient", {"name"}, ("name",))
    text(request["recipient"]["name"], "/recipient/name", 16)
    day(request["issueDate"], "/issueDate")
    text(request["number"], "/number", 16)
    if "subject" in request: text(request["subject"], "/subject", 16)
    expiry = "paymentDueDate" if kind == "invoice" else "validUntil"
    forbidden = "validUntil" if kind == "invoice" else "paymentDueDate"
    if forbidden in request: fail("/"+forbidden, "not-applicable")
    if expiry in request: day(request[expiry], "/"+expiry)
    note = []
    if "transaction" not in request and kind == "invoice": fail("/transaction", "required")
    if "transaction" in request:
        t = request["transaction"]
        obj(t, "/transaction", {"date", "period"})
        if len(t) != 1: fail("/transaction", "ambiguous-transaction")
        if "date" in t:
            day(t["date"], "/transaction/date")
            note = ["取引日 " + t["date"].replace("-", "/")]
        else:
            obj(t["period"], "/transaction/period", {"start", "end"}, ("start", "end"))
            start = day(t["period"]["start"], "/transaction/period/start")
            end = day(t["period"]["end"], "/transaction/period/end")
            if start > end: fail("/transaction/period/end", "invalid-period")
            note = ["取引期間", t["period"]["start"].replace("-", "/") + "～" + t["period"]["end"].replace("-", "/")]
    free = request.get("notesLines", [])
    lines(free, "/notesLines", 0, 3, 48)
    if len(note)+len(free) > 3: fail(child("/notesLines", 3-len(note)), "notes-budget-exceeded")
    note += free
    if len("\n".join(note)) > 48: fail("/notesLines", "notes-budget-exceeded")
    items = request["items"]
    if type(items) is not list: fail("/items", "invalid-type")
    if len(items) > 8: fail("/items/8", "too-many-items")
    if not items: fail("/items", "too-few-items")
    for i, item in enumerate(items):
        p = f"/items/{i}"
        obj(item, p, {"name", "quantity", "unitPrice", "taxRate", "amount"}, ("name", "quantity", "unitPrice", "taxRate", "amount"))
        text(item["name"], p+"/name", 17)
        q = item["quantity"]
        if type(q) is not str: fail(p+"/quantity", "invalid-type")
        if not re.fullmatch(r"(0|[1-9][0-9]{0,4})(\.[0-9]{1,3})?", q) or not any(c in "123456789" for c in q): fail(p+"/quantity", "quantity-format")
        integer(item["unitPrice"], p+"/unitPrice", 99999999)
        integer(item["amount"], p+"/amount")
        if type(item["taxRate"]) is not int or item["taxRate"] not in (8, 10): fail(p+"/taxRate", "unsupported-tax-rate")
    obj(request["taxSummary"], "/taxSummary", {"8", "10"}, ("10", "8"))
    for rate in ("10", "8"):
        bucket = request["taxSummary"][rate]; p = "/taxSummary/"+rate
        obj(bucket, p, {"taxableAmount", "taxAmount"}, ("taxableAmount", "taxAmount"))
        for key in ("taxableAmount", "taxAmount"): integer(bucket[key], p+"/"+key)
    integer(request["totalAmount"], "/totalAmount")
    try: company_check(company)
    except Invalid as error:
        source = error.problem["errors"][0]
        raise Invalid("/companyId", "registered-data-invalid", source={"kind":"company", **source}) from error
    # fixtureの登録設定レコード。DB/認証/登録APIの検証ではない。
    obj(settings, "", {"companyId", "templateId", "quantityAlignment"}, ("companyId", "templateId"))
    for ref in ("companyId", "templateId"):
        if settings[ref] != request[ref]: fail("/"+ref, "invalid-reference")
    effective = qr.resolve(settings, request)
    # 表示用書式だけを加える。乗算、税計算、金額の合算や比較はしない。
    def yen(n): return f"{n:,}円"
    def japanese(d): return d.replace("-", "年", 1).replace("-", "月") + "日"
    values = {"issuer":company["name"], "address":"\n".join(company["addressLines"]),
              "recipient":request["recipient"]["name"], "date":japanese(request["issueDate"]),
              "number":request["number"], "total":yen(request["totalAmount"])}
    sources = {"issuer":{"kind":"company","path":"/name"}, "address":{"kind":"company","path":"/addressLines"},
               "recipient":{"path":"/recipient/name"}, "date":{"path":"/issueDate"}, "number":{"path":"/number"}, "total":{"path":"/totalAmount"}}
    for src, key in (("registrationNumber","registration"), ("contact","contact"), ("bankTransferLines","bank")):
        if src in company and (key != "bank" or kind == "invoice"):
            values[key] = "\n".join(company[src]) if key == "bank" else company[src]
            sources[key] = {"kind":"company", "path":"/"+src}
    for src, key in (("subject","subject"), (expiry,"expiry")):
        if src in request:
            values[key] = japanese(request[src]) if key == "expiry" else request[src]
            sources[key] = {"path":"/"+src}
    if note:
        values["notes"] = "\n".join(note); sources["notes"] = {"path":"/notesLines", "relatedPaths":["/transaction"] if "transaction" in request else []}
    for i, item in enumerate(items):
        for src, suffix, value in (("name","name",item["name"]+("※" if item["taxRate"]==8 else "")), ("quantity","quantity",item["quantity"]), ("unitPrice","price",yen(item["unitPrice"])), ("taxRate","rate",str(item["taxRate"])+"%"), ("amount","amount",yen(item["amount"]))):
            key = f"row{i}.{suffix}"; values[key] = value; sources[key] = {"path":f"/items/{i}/{src}"}
    for rate in ("10", "8"):
        for src, key in (("taxableAmount","subtotal"+rate), ("taxAmount","tax"+rate)):
            values[key] = yen(request["taxSummary"][rate][src]); sources[key] = {"path":f"/taxSummary/{rate}/{src}"}
    definition = copy.deepcopy(definition)
    definition["fields"] = [f for f in definition["fields"] if not f["key"].startswith("label.") or f["key"][6:] in values]
    for f in definition["fields"]:
        if f["key"] == "label.notes": f["text"] = "取引・備考"
    fields, keys, placement = qr.m.mode_fields(definition, values, effective["quantityAlignment"])
    for key, f in zip(keys, fields):
        if len(f["text"].encode("utf-16-le"))//2 > 200 or f["text"].count("\n")+1 > 5:
            fail(sources.get(key, {"path":""})["path"], "render-budget-exceeded")
        widths = [sum(WIDTHS[c] for c in line)*f["fontSize"]/1000 for line in f["text"].split("\n")]
        height = f["fontSize"]*(1+1.2*f["text"].count("\n"))
        if max(widths) > f["width"]*595-.1*f["fontSize"] or height > f["height"]*842:
            origin = sources.get(key, {"path":""})
            if origin.get("kind") == "company": raise Invalid("/companyId", "registered-data-invalid", source=origin|{"code":"field-overflow"})
            fail(origin["path"], "field-overflow")
    if len(fields)>200 or sum(f["text"].count("\n")+1 for f in fields)>100: fail("", "render-budget-exceeded")
    if len(json.dumps(fields, ensure_ascii=False, separators=(",", ":")).encode())>65536: fail("", "render-budget-exceeded")
    return kind, values, fields, keys, sources, definition, effective, placement



def encode(value):
    return (json.dumps(value, ensure_ascii=False, indent=2)+"\n").encode()


def preflight(raw_company, raw_settings, raw_request, definitions):
    company, settings, request = map(read_json, (raw_company, raw_settings, raw_request))
    snapshot = copy.deepcopy((company, settings, request, definitions))
    try:
        obj(request, "", qr.REQUEST_KEYS,
            ("companyId", "templateId", "currency", "recipient", "issueDate", "number", "items", "taxSummary", "totalAmount"))
        identifier(request["templateId"], "/templateId")
        kind = {"invoice-standard-v1":"invoice", "estimate-standard-v1":"estimate"}.get(request["templateId"])
        if kind is None: fail("/templateId", "invalid-reference")
        mapped = map_fields(company, settings, request, definitions[kind])
    finally:
        assert (company, settings, request, definitions) == snapshot, "preflight mutated input"
    return company, settings, request, mapped


def process(api, raw_company, raw_settings, raw_request, definitions, template, pics, directory):
    # 正常/不正で共通。事前検証と変換が終わるまでPDF APIは呼ばない。
    started = time.monotonic()
    company, settings, request, mapped = preflight(raw_company, raw_settings, raw_request, definitions)
    kind, values, fields, keys, sources, definition, effective, placement = mapped
    preflight_seconds = time.monotonic()-started
    rendered, stage_pdfs, measure = [], {}, {}
    status, current, step = r.post(api, template, fields=fields)
    assert status == 200, step
    stage_pdfs["filled"], measure["fill"] = current, step
    for name, key, index, query in (
        ("logo", "logoAssetId", 0, "?page=1&x=0.05&y=0.04&width=0.18"),
        ("seal", "sealAssetId", 1, "?page=1&x=0.82&y=0.26&width=0.1")):
        if key in company:
            status, current, step = r.post(api, current, image=pics[index], query=query)
            assert status == 200, step
            stage_pdfs[name], measure[name] = current, step
            rendered.append(name)
    measure.update(json_to_pdf_seconds=time.monotonic()-started, preflight_seconds=preflight_seconds,
                   total_seconds=sum(measure[s]["seconds"] for s in ("fill", *rendered)),
                   final_bytes=len(current), effective=effective, rendered_images=rendered)
    # 完全な入力JSONから作った値とfieldsを保存。前回のdisplay-valuesは読まない。
    for name, value in (("display-values", values), ("fields", fields), ("field-index", keys),
                        ("field-sources", sources), ("definition", definition),
                        ("effective", effective), ("quantity-placement", placement)):
        r.save(directory/(name+".json"), value)
    stage_pdfs["final"] = current
    for stage, pdf in stage_pdfs.items():
        path = directory/(stage+".pdf")
        path.write_bytes(pdf)
        if stage == "final":
            measure["extraction"] = r.check_pdf(path, fields, keys)
        else:
            r.command("pdftoppm", "-singlefile", "-r", "144", "-png", str(path), str(path.with_suffix("")))
    actual = json.loads((directory/"final.extracted.json").read_text())
    # 確定金額・数量だけでなく、会社・宛先・日付・取引/備考も元入力から独立照合。
    expected = qr.m.confirmed_values(request)
    jp = lambda s: s[:4]+"年"+s[5:7]+"月"+s[8:]+"日"
    expected.update(issuer=company["name"], address="\n".join(company["addressLines"]),
                    recipient=request["recipient"]["name"], date=jp(request["issueDate"]), number=request["number"])
    for src, key in (("registrationNumber","registration"), ("contact","contact"), ("bankTransferLines","bank")):
        if src in company and (key != "bank" or kind == "invoice"):
            expected[key] = "\n".join(company[src]) if key == "bank" else company[src]
        else:
            assert key not in actual and "label."+key not in actual
    expiry = "paymentDueDate" if kind == "invoice" else "validUntil"
    for src, key in (("subject", "subject"), (expiry, "expiry")):
        if src in request: expected[key] = jp(request[src]) if key == "expiry" else request[src]
        else: assert key not in actual and "label."+key not in actual
    notes = []
    if "transaction" in request:
        t = request["transaction"]
        if "date" in t: notes.append("取引日 "+t["date"].replace("-", "/"))
        else: notes += ["取引期間", t["period"]["start"].replace("-", "/")+"～"+t["period"]["end"].replace("-", "/")]
    notes += request.get("notesLines", [])
    if notes: expected.update(notes="\n".join(notes), **{"label.notes":"取引・備考"})
    else: assert "notes" not in actual and "label.notes" not in actual
    expected["title"] = "請求書" if kind == "invoice" else "見積書"
    assert all(actual[k] == value for k, value in expected.items())
    for i in range(len(request["items"]),8):
        assert not any(key.startswith(f"row{i}.") for key in actual)
    r.save(directory/"expected-from-input.json", expected)
    measure["images"] = check_images(directory, rendered)
    positions = qr.m.c.inspect_quantity(directory, fields, keys)
    ends = [row["word_x_max_pt"] for row in positions["rows"]]
    positions["right_edge_spread_pt"] = max(ends)-min(ends)
    if effective[ALIGN] == "decimal":
        assert positions["anchor_spread_pt"] < .1
        if positions["dot_ink_center_spread_pt"] is not None:
            assert positions["dot_ink_center_spread_pt"] <= .5
    else: assert positions["right_edge_spread_pt"] < .05
    measure.update(quantity_positions=positions, fields=len(fields), rendered_lines=sum(f["text"].count("\n")+1 for f in fields),
                   notes_lines=len(notes), notes_characters=len("\n".join(notes)),
                   input_objects_unchanged=True, input_fields_preserved=True, optional_labels_and_unused_rows_checked=True)
    return measure


def check_images(directory, rendered):
    if rendered == ["logo", "seal"]:
        # 既存検査はlogo後とfinalを使う。
        return r.image_check(directory, [(.05,.04,.18),(.82,.26,.1)])
    if not rendered:
        assert (directory/"filled.pdf").read_bytes() == (directory/"final.pdf").read_bytes()
        return {"absent_images":True, "same_as_filled_pdf":True}
    # 片方のみも同じalphaサンプルと実画素の変化領域で確認。
    np, Image = r.np, r.Image
    prior = np.array(Image.open(directory/"filled.png").convert("RGB"))
    after = np.array(Image.open(directory/"final.png").convert("RGB"))
    if rendered == ["logo"]:
        x,y,w,dimensions,color,samples = .05,.04,.18,(300,100),(20,70,170),[(60,50,128),(150,15,0),(6,50,255)]
    else:
        assert rendered == ["seal"]
        x,y,w,dimensions,color,samples = .82,.26,.1,(120,120),(210,20,30),[(50,50,128),(60,60,128),(10,10,0),(60,6,200)]
    left,top,width = x*1190,y*1684,w*1190
    height = width*dimensions[1]/dimensions[0]
    ys,xs = np.nonzero(np.any(after != prior, axis=2))
    assert len(xs) and xs.min()>=left-2 and xs.max()<=left+width+2 and ys.min()>=top-2 and ys.max()<=top+height+2
    errors = []
    for sx,sy,alpha in samples:
        px,py = round(left+sx/dimensions[0]*width),round(top+sy/dimensions[1]*height)
        expected = prior[py,px]*(1-alpha/255)+np.array(color)*alpha/255
        error = float(abs(after[py,px].astype(float)-expected).max())
        assert error <= 5
        errors.append(error)
    return {"image":rendered[0], "changed_pixel_bbox":[int(xs.min()),int(ys.min()),int(xs.max()),int(ys.max())], "sample_channel_errors":errors}


def fixtures():
    company = read_json((DRAFT/"examples/company-registration.json").read_bytes())
    settings = {kind:read_json((DRAFT/"settings-examples"/(kind+".json")).read_bytes()) for kind in ("invoice","estimate")}
    requests = {name:read_json((DRAFT/"examples"/(name+".json")).read_bytes()) for name in
                ("invoice-1","invoice-8","estimate-1","estimate-8","provided-amounts-unchanged")}
    cases = {name:(copy.deepcopy(company),copy.deepcopy(settings[req["templateId"].split("-")[0]]),req) for name,req in requests.items()}
    expected = {"invoice-1":("decimal","request"),"invoice-8":("right","templateSettings"),
                "estimate-1":("right","request"),"estimate-8":("decimal","templateSettings"),
                "provided-amounts-unchanged":("right","templateSettings")}
    def add(name, base, change, mode="decimal", source="request", company_override=None, settings_override=None):
        c,s,req = copy.deepcopy(cases[base]); change(req)
        if company_override is not None: c=copy.deepcopy(company_override)
        if settings_override is not None: s=copy.deepcopy(settings_override)
        cases[name]=(c,s,req); expected[name]=(mode,source)
    no_mode = copy.deepcopy(settings["invoice"]); no_mode.pop(ALIGN)
    def money(req):
        req.pop(ALIGN,None);req["items"][0].update(quantity="99999.999",unitPrice=99999999,amount=999999999999)
        req["taxSummary"]={rate:{"taxableAmount":999999999999,"taxAmount":999999999999} for rate in ("8","10")}
        req["totalAmount"]=999999999999
    add("default-money-boundary","invoice-1",money,source="default",settings_override=no_mode)
    for mode in ("decimal","right"):
        def boundary(req, mode=mode):
            req[ALIGN]=mode
            for i,(item,q) in enumerate(zip(req["items"],qr.m.QUANTITIES)):
                item.update(quantity=q,name=("証" if i%2==0 else "明")*17,unitPrice=99999999)
        add("quantity-boundary-"+mode,"invoice-8",boundary,mode=mode)
    add("date-notes-boundary","invoice-1",lambda req:req.update(notesLines=["証"*16,"証"*16]))
    def period(req):
        req.update(issueDate="2027-01-02",transaction={"period":{"start":"2026-12-31","end":"2027-01-01"}},notesLines=["証"*16])
    add("period-notes-boundary","estimate-8",period,source="templateSettings")
    def no_transaction(req):
        req.pop("transaction"); req.update(notesLines=["証"*16,"証"*16,"証"*14])
    add("estimate-no-transaction","estimate-1",no_transaction,mode="right")
    minimal_company={key:company[key] for key in ("name","addressLines")}
    def minimal(req):
        for key in ("subject","validUntil","transaction",ALIGN):req.pop(key,None)
        req["notesLines"]=[]
    add("minimal-no-images","estimate-1",minimal,source="templateSettings",company_override=minimal_company)
    for image,mode in (("logo","decimal"),("seal","right")):
        c=copy.deepcopy(minimal_company);c[image+"AssetId"]=image+"_demo"
        add(image+"-only","estimate-1",lambda req,mode=mode:req.update(**{ALIGN:mode}),mode=mode,company_override=c)
    return cases,expected


def invalid_fixtures(cases, definitions):
    errors=[]
    def add(name, change, path, code, status=422, company_change=None, settings_change=None, definition_change=None, source=None):
        c,s,q=copy.deepcopy(cases["invoice-1"]);d=copy.deepcopy(definitions)
        change(q)
        if company_change: company_change(c)
        if settings_change: settings_change(s)
        if definition_change: definition_change(d["invoice"])
        errors.append((name,encode(c),encode(s),encode(q),d,{"status":status,"code":"validation-failed","errors":[dict(path=path,code=code,**({"source":source} if source else {}))]}))
    add("nine-items",lambda q:q.update(items=q["items"]*9),"/items/8","too-many-items")
    add("zero-items",lambda q:q.update(items=[]),"/items","too-few-items")
    for key in ("companyId","templateId","currency","recipient","issueDate","number","items","taxSummary","totalAmount"):
        add("missing-"+key,lambda q,key=key:q.pop(key),"/"+key,"required")
    add("missing-amount",lambda q:q["items"][0].pop("amount"),"/items/0/amount","required")
    for label,value,code in (("number",1.25,"invalid-type"),("null",None,"invalid-type"),("int6","100000","quantity-format"),
                             ("fraction4","1.0000","quantity-format"),("leading-zero","01.5","quantity-format"),
                             ("exponent","1e3","quantity-format"),("spaces"," 1 ","quantity-format"),
                             ("zero","0.000","quantity-format"),("negative","-1","quantity-format")):
        add("quantity-"+label,lambda q,value=value:q["items"][0].update(quantity=value),"/items/0/quantity",code)
    for label,value,code in (("negative",-1,"out-of-range"),("fraction",1.5,"invalid-integer"),("string","125","invalid-integer"),("boolean",True,"invalid-integer")):
        add("amount-"+label,lambda q,value=value:q["items"][0].update(amount=value),"/items/0/amount",code)
    add("total-limit",lambda q:q.update(totalAmount=1000000000000),"/totalAmount","out-of-range")
    add("unit-price-limit",lambda q:q["items"][0].update(unitPrice=100000000),"/items/0/unitPrice","out-of-range")
    add("tax-rate",lambda q:q["items"][0].update(taxRate=0),"/items/0/taxRate","unsupported-tax-rate")
    add("missing-tax-bucket",lambda q:q["taxSummary"].pop("8"),"/taxSummary/8","required")
    add("missing-tax-amount",lambda q:q["taxSummary"]["10"].pop("taxAmount"),"/taxSummary/10/taxAmount","required")
    add("currency",lambda q:q.update(currency="USD"),"/currency","unsupported-currency")
    for label,change,path in (
        ("recipient-address",lambda q:q["recipient"].update(addressLines=["架空"]),"/recipient/addressLines"),
        ("company-override",lambda q:q.update(company={}),"/company"),
        ("per-item-mode",lambda q:q["items"][0].update(quantityAlignment="right"),"/items/0/quantityAlignment")):
        add(label,change,path,"unknown-field",400)
    for label,value,code in (("unsupported","架空𠮷野","unsupported-character"),("combining","カ\u3099","combining-mark-not-supported"),
                             ("placeholder","架空%p","forbidden-sequence"),("literal-newline","架空\\n","forbidden-sequence"),
                             ("long","証"*17,"too-long"),("null",None,"invalid-type")):
        add("recipient-"+label,lambda q,value=value:q["recipient"].update(name=value),"/recipient/name",code)
    add("issue-date",lambda q:q.update(issueDate="2026-02-30"),"/issueDate","invalid-date")
    add("transaction-date",lambda q:q.update(transaction={"date":"2026-02-30"}),"/transaction/date","invalid-date")
    add("period-reversed",lambda q:q.update(transaction={"period":{"start":"2026-10-31","end":"2026-10-01"}}),"/transaction/period/end","invalid-period")
    add("period-missing-end",lambda q:q.update(transaction={"period":{"start":"2026-10-01"}}),"/transaction/period/end","required")
    add("transaction-ambiguous",lambda q:q["transaction"].update(period={"start":"2026-10-01","end":"2026-10-31"}),"/transaction","ambiguous-transaction")
    add("transaction-missing",lambda q:q.pop("transaction"),"/transaction","required")
    add("date-three-notes",lambda q:q.update(notesLines=["一","二","三"]),"/notesLines/2","notes-budget-exceeded")
    add("period-two-notes",lambda q:q.update(transaction=cases["invoice-8"][2]["transaction"],notesLines=["一","二"]),"/notesLines/1","notes-budget-exceeded")
    add("notes-long",lambda q:q.update(notesLines=["証"*17]),"/notesLines/0","too-long")
    add("wrong-expiry",lambda q:q.update(validUntil="2026-11-30"),"/validUntil","not-applicable")
    add("unknown-company",lambda q:q.update(companyId="missing"),"/companyId","invalid-reference")
    add("unknown-template",lambda q:q.update(templateId="missing"),"/templateId","invalid-reference")
    add("registered-company-character",lambda q:None,"/companyId","registered-data-invalid",company_change=lambda c:c.update(name="𠮷"),source={"kind":"company","path":"/name","code":"unsupported-character"})
    add("registered-company-required",lambda q:None,"/companyId","registered-data-invalid",company_change=lambda c:c.pop("addressLines"),source={"kind":"company","path":"/addressLines","code":"required"})
    add("registered-image-reference",lambda q:None,"/companyId","registered-data-invalid",company_change=lambda c:c.update(logoAssetId="missing"),source={"kind":"company","path":"/logoAssetId","code":"invalid-reference"})
    def narrow(d,key):next(f for f in d["fields"] if f["key"]==key)["rect"]["width"]=.03
    add("recipient-overflow",lambda q:None,"/recipient/name","field-overflow",definition_change=lambda d:narrow(d,"recipient"))
    add("registered-company-overflow",lambda q:None,"/companyId","registered-data-invalid",definition_change=lambda d:narrow(d,"issuer"),source={"kind":"company","path":"/name","code":"field-overflow"})
    for label,value,code in (("null",None,"invalid-type"),("enum","left","unsupported-quantity-alignment")):
        add("mode-request-"+label,lambda q,value=value:q.update(**{ALIGN:value}),"/"+ALIGN,code)
        add("mode-registered-"+label,lambda q:q.update(**{ALIGN:"decimal"}),"/templateId","registered-data-invalid",settings_change=lambda s,value=value:s.update(**{ALIGN:value}),source={"kind":"templateSettings","path":"/"+ALIGN,"code":code})
    c,s,q=cases["invoice-1"];craw,sraw,qraw=map(encode,(c,s,q))
    for name,raw,status,code in (("malformed",b'{',400,"invalid-json"),("duplicate",b'{"quantityAlignment":"decimal","quantityAlignment":"right"}',400,"duplicate-key"),
                                 ("body-limit",b' '*65537,413,"request-too-large")):
        errors.append((name,craw,sraw,raw,definitions,{"status":status,"code":"validation-failed","errors":[{"path":"","code":code}]}))
    errors.append(("exponent-money",craw,sraw,qraw.replace(b'"amount": 125',b'"amount": 1e3'),definitions,
                   {"status":422,"code":"validation-failed","errors":[{"path":"/items/0/amount","code":"invalid-integer"}]}))
    return errors


def main():
    parser=argparse.ArgumentParser();parser.add_argument("--output",type=Path,required=True)
    output=parser.parse_args().output.resolve();output.mkdir(parents=True,exist_ok=False)
    pics=r.images(output)
    definitions={k:json.loads((DRAFT/"quantity-layout"/(k+".definition.json")).read_text()) for k in ("invoice","estimate")}
    template=(r.ROOT/"line-comparison/refined-template.pdf").read_bytes()
    cases,expected=fixtures();errors=invalid_fixtures(cases,definitions)
    result={"api_sha":r.API_SHA,"site_assets_sha":r.SITE_SHA,"evidence_parent_commit":"fcae4f1942eeb52b0dd1298a189d0bfcc2d6059e",
            "scope":"full v0.2 candidate fixture validation/mapping + adopted quantity/settings; not product HTTP/storage/auth",
            "script_sha256":hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),"template_sha256":hashlib.sha256(template).hexdigest(),
            "environment":{"image_id":json.loads(r.smoke.docker("image","inspect","amane-pdf-api:site134-465088e").stdout)[0]["Id"],
                "limits":"CPU1/RAM1536MiB/no extra swap/tmpfs256MiB/read-only/network none/shared2 queue0",
                "transport":"docker exec loopback HTTP, sequential","python":platform.python_version(),
                "timing":"JSON parse/preflight/map to final body; excludes save/PDF checks/startup"},"cases":{},"errors":{}}
    # 先に全例の事前検証を行い、途中まで生成することを避ける。
    for c,s,q in cases.values():preflight(*map(encode,(c,s,q)),definitions)
    with r.smoke.running_container("amane-pdf-api:site134-465088e",isolated=True) as api:
        calls=[];original=api.request
        def counted(method,path,*args,**kwargs):
            calls.append({"method":method,"path":path});return original(method,path,*args,**kwargs)
        api.request=counted
        for name,(company,settings,request) in cases.items():
            directory=output/name;directory.mkdir()
            raws=list(map(encode,(company,settings,request)))
            for file,raw in zip(("company.json","settings.json","request.json"),raws):(directory/file).write_bytes(raw)
            before,posts=len(calls),api.post_count
            measure=process(api,*raws,definitions,template,pics,directory)
            assert measure["effective"]=={ALIGN:expected[name][0],"source":expected[name][1]}
            expected_posts=1+sum(key in company for key in ("logoAssetId","sealAssetId"))
            assert api.post_count-posts==expected_posts and len(calls)-before==expected_posts
            assert all((directory/file).read_bytes()==raw for file,raw in zip(("company.json","settings.json","request.json"),raws))
            measure.update(api_calls=calls[before:],post_count_delta=api.post_count-posts)
            result["cases"][name]=measure;api.assert_clean()
        for name,craw,sraw,qraw,defs,expected_error in errors:
            directory=output/"errors"/name;directory.mkdir(parents=True)
            for file,raw in zip(("company.json","settings.json","request.json"),(craw,sraw,qraw)):(directory/file).write_bytes(raw)
            if defs!=definitions:r.save(directory/"definitions.json",defs)
            before,posts=len(calls),api.post_count
            try:process(api,craw,sraw,qraw,defs,template,pics,directory)
            except Invalid as error:
                assert error.problem==expected_error,(name,error.problem,expected_error)
                r.save(directory/"error.json",error.problem)
            else:raise AssertionError("invalid input accepted: "+name)
            assert api.post_count==posts and len(calls)==before and not list(directory.glob("*.pdf"))
            result["errors"][name]={"problem":expected_error,"api_call_count":len(calls)-before,"posts_before":posts,"posts_after":api.post_count,"pdf_created":False}
        result.update(api_post_count=api.post_count,health_status=api.request("GET","/healthz")[0],cleanup="assert_clean after each success and final")
        api.assert_clean()
    # 同じ境界入力の両モードで配置以外を変えていないことを確認。
    a,b=output/"quantity-boundary-decimal",output/"quantity-boundary-right"
    assert (a/"final.extracted.json").read_bytes()==(b/"final.extracted.json").read_bytes()
    assert (a/"final.txt").read_bytes()==(b/"final.txt").read_bytes()
    ak,bk=map(lambda p:read_json((p/"field-index.json").read_bytes()),(a,b))
    af,bf=map(lambda p:json.loads((p/"fields.json").read_text()),(a,b))
    assert ak==bk
    assert qr.m.c.other_word_positions(a,af,ak)==qr.m.c.other_word_positions(b,bf,bk)
    qa,qb=map(lambda p:read_json((p/"request.json").read_bytes()),(a,b));qa.pop(ALIGN);qb.pop(ALIGN);assert qa==qb
    r.command("qpdf","--empty","--pages",str(a/"final.pdf"),"1",str(b/"final.pdf"),"1","--",str(output/"quantity-boundary-comparison.pdf"))
    r.command("qpdf","--check",str(output/"quantity-boundary-comparison.pdf"))
    result.update(summary={"valid":len(cases),"rejected":len(errors),"rejected_api_calls":0,"quantity_comparison_same_text_and_other_positions":True})
    r.save(output/"results.json",result);print(json.dumps(result["summary"]))


if __name__=="__main__":main()
