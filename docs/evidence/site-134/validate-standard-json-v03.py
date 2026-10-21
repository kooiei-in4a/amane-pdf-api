#!/usr/bin/env python3
"""#134 v0.3のnameLines→事前検証→変換→既存API。検証専用、製品未実装。"""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import platform
import re
import time
import xml.etree.ElementTree as ET

import reproduce as r

spec = importlib.util.spec_from_file_location("v02_fixture", r.ROOT / "validate-standard-json-v02.py")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
qr, v = base.qr, base.v
Invalid, fail, child = v.Invalid, v.fail, v.child
read_json, obj, text = v.read_json, v.obj, v.text
lines, identifier, day, integer = v.lines, v.identifier, v.day, v.integer
WIDTHS, check_images = v.WIDTHS, base.check_images
DRAFT = r.ROOT / "standard-json-draft-v0.3"
ALIGN = "quantityAlignment"


def name_lines(value, path):
    if type(value) is not list: fail(path, "invalid-type")
    if not 1 <= len(value) <= 2: fail(path, "line-count")
    for i, line in enumerate(value):
        if type(line) is str and any(c in line for c in "\r\n"):
            fail(child(path,i), "invalid-line-break")
        text(line, child(path,i), 20)
    joined = "\n".join(value)
    if len(joined.encode("utf-16-le"))//2 > 41: fail(path,"too-long")
    return joined


def company_check(company):
    obj(company,"",{"nameLines","addressLines","registrationNumber","contact","bankTransferLines","logoAssetId","sealAssetId"},("nameLines","addressLines"))
    name_lines(company["nameLines"],"/nameLines")
    name_lines(company["addressLines"],"/addressLines")
    if "registrationNumber" in company:
        text(company["registrationNumber"],"/registrationNumber",14)
        if not re.fullmatch(r"T[0-9]{13}",company["registrationNumber"]): fail("/registrationNumber","invalid-registration-number")
    if "contact" in company: text(company["contact"],"/contact",16)
    if "bankTransferLines" in company: lines(company["bankTransferLines"],"/bankTransferLines",1,3,48)
    for key,expected in (("logoAssetId","logo_demo"),("sealAssetId","seal_demo")):
        if key in company:
            identifier(company[key],"/"+key)
            if company[key] != expected: fail("/"+key,"invalid-reference")


def map_fields(company, settings, request, definition):
    """fixtureのcompany_demoのみを解決し、v0.3候補を表示文字列へ変換する。"""
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
    obj(request["recipient"], "/recipient", {"nameLines"}, ("nameLines",))
    name_lines(request["recipient"]["nameLines"], "/recipient/nameLines")
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
    values = {"issuer":"\n".join(company["nameLines"]), "address":"\n".join(company["addressLines"]),
              "recipient":"\n".join(request["recipient"]["nameLines"]), "date":japanese(request["issueDate"]),
              "number":request["number"], "total":yen(request["totalAmount"])}
    sources = {"issuer":{"kind":"company","path":"/nameLines"}, "address":{"kind":"company","path":"/addressLines"},
               "recipient":{"path":"/recipient/nameLines"}, "date":{"path":"/issueDate"}, "number":{"path":"/number"}, "total":{"path":"/totalAmount"}}
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
            origin = dict(sources.get(key, {"path":""}))
            if key in ("issuer", "recipient", "address") and max(widths) > f["width"]*595-.1*f["fontSize"]:
                i = next(i for i,w in enumerate(widths) if w > f["width"]*595-.1*f["fontSize"])
                origin["path"] = child(origin["path"], i)
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
    expected.update(issuer="\n".join(company["nameLines"]), address="\n".join(company["addressLines"]),
                    recipient="\n".join(request["recipient"]["nameLines"]), date=jp(request["issueDate"]), number=request["number"])
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
    measure["header_layout"] = header_layout(directory, fields, keys)
    measure.update(quantity_positions=positions, fields=len(fields), rendered_lines=sum(f["text"].count("\n")+1 for f in fields),
                   notes_lines=len(notes), notes_characters=len("\n".join(notes)),
                   input_objects_unchanged=True, input_fields_preserved=True, optional_labels_and_unused_rows_checked=True)
    return measure



def header_layout(directory, fields, keys):
    words=ET.fromstring((directory/'final.bbox.html').read_bytes()).findall('.//{http://www.w3.org/1999/xhtml}word')
    box=lambda w:tuple(float(w.attrib[k]) for k in ('xMin','yMin','xMax','yMax'))
    metrics={}
    for key in ('issuer','recipient','address'):
        f=fields[keys.index(key)]
        inside=lambda w:box(w)[0]>=f['x']*595-.1 and box(w)[2]<=(f['x']+f['width'])*595+.1 and box(w)[1]>=f['y']*842-.1 and box(w)[3]<=(f['y']+f['height'])*842+.1
        mine=[w for w in words if inside(w)];other=[w for w in words if not inside(w)]
        for a in mine:
            x0,y0,x1,y1=box(a)
            assert not any(min(x1,box(b)[2])-max(x0,box(b)[0])>.01 and min(y1,box(b)[3])-max(y0,box(b)[1])>.01 for b in other),key
        metrics[key]={'lines':f['text'].count('\n')+1,'utf16_units':len(f['text'].encode('utf-16-le'))//2,
                      'line_advance_pt':[sum(WIDTHS[c] for c in line)*f['fontSize']/1000 for line in f['text'].split('\n')],
                      'no_intersection_with_other_words':True}
    return metrics


def fixtures():
    company=read_json((DRAFT/'examples/company-registration.json').read_bytes())
    long_company=read_json((DRAFT/'examples/company-registration-long.json').read_bytes())
    settings={k:read_json((DRAFT/'settings-examples'/(k+'.json')).read_bytes()) for k in ('invoice','estimate')}
    cases,expected={},{}
    for name in ('invoice-1','invoice-8','estimate-1','estimate-8','provided-amounts-unchanged'):
        q=read_json((DRAFT/'examples'/(name+'.json')).read_bytes());kind=q['templateId'].split('-')[0]
        cases[name]=(copy.deepcopy(company),copy.deepcopy(settings[kind]),q)
    expected.update({'invoice-1':('decimal','request'),'invoice-8':('right','templateSettings'),
                     'estimate-1':('right','request'),'estimate-8':('decimal','templateSettings'),
                     'provided-amounts-unchanged':('right','templateSettings')})
    for kind in ('invoice','estimate'):
        long_q=read_json((DRAFT/'examples'/(kind+'-8-long.json')).read_bytes())
        for count in (1,8):
            q=copy.deepcopy(long_q if count==8 else cases[kind+'-1'][2])
            q['recipient']=copy.deepcopy(long_q['recipient'])
            name=f'{kind}-{count}-roles'
            cases[name]=(copy.deepcopy(long_company),copy.deepcopy(settings[kind]),q)
            expected[name]=expected[kind+'-'+str(count)]
    # 独立した9組合せの期待表。boundary8件+defaultで全て確認する。
    omit=object()
    matrix=[('invoice',1,'decimal',omit,'decimal','request'),('invoice',1,'right',omit,'right','request'),
            ('invoice',8,'decimal','decimal',omit,'templateSettings'),('invoice',8,'right','right',omit,'templateSettings'),
            ('estimate',1,'decimal','decimal','decimal','request'),('estimate',1,'right','right','right','request'),
            ('estimate',8,'decimal','right','decimal','request'),('estimate',8,'right','decimal','right','request')]
    for kind,count,mode,registered,issued,source in matrix:
        c,s,q=copy.deepcopy(cases[kind+'-'+str(count)])
        c.update(nameLines=['証'*20,'明'*20],addressLines=['所'*20,'在'*20])
        q['recipient']['nameLines']=['先'*20,'宛'*20]
        for i,item in enumerate(q['items']):item['quantity']=qr.m.QUANTITIES[i]
        s.pop(ALIGN,None);q.pop(ALIGN,None)
        if registered is not omit:s[ALIGN]=registered
        if issued is not omit:q[ALIGN]=issued
        name=f'{kind}-{count}-boundary-{mode}'
        cases[name]=(c,s,q);expected[name]=(mode,source)
    c,s,q=copy.deepcopy(cases['provided-amounts-unchanged']);s.pop(ALIGN,None);q.pop(ALIGN,None)
    cases['default-decimal']=(c,s,q);expected['default-decimal']=('decimal','default')
    return cases,expected


def invalid_fixtures(cases,definitions):
    errors=[]
    def add(name,change=lambda q:None,path='',code='',status=422,company_change=None,settings_change=None,definition_change=None,source=None):
        c,s,q=copy.deepcopy(cases['invoice-1']);d=copy.deepcopy(definitions);change(q)
        if company_change:company_change(c)
        if settings_change:settings_change(s)
        if definition_change:definition_change(d['invoice'])
        errors.append((name,encode(c),encode(s),encode(q),d,{'status':status,'code':'validation-failed','errors':[dict(path=path,code=code,**({'source':source} if source else {}))]}))
    bad_lines=[('21-units',['先','宛'*21],'/1','too-long'),('three-lines',['一','二','三'],'','line-count'),
               ('empty-array',[],'','line-count'),('empty-line',['一',''],'/1','required'),('blank-line',['一','  '],'/1','required'),
               ('null',None,'','invalid-type'),('string','一','','invalid-type'),('null-element',['一',None],'/1','invalid-type'),
               ('number-element',['一',1],'/1','invalid-type'),('line-feed',['一','二\n三'],'/1','invalid-line-break'),
               ('carriage-return',['一','二\r三'],'/1','invalid-line-break'),('unsupported',['一','𠮷'],'/1','unsupported-character')]
    for target in ('recipient','company-name','company-address'):
        for label,value,suffix,code in bad_lines:
            if target=='recipient':
                add(target+'-'+label,lambda q,value=value:q['recipient'].update(nameLines=value),'/recipient/nameLines'+suffix,code)
            else:
                key='nameLines' if target=='company-name' else 'addressLines'
                add(target+'-'+label,path='/companyId',code='registered-data-invalid',company_change=lambda c,key=key,value=value:c.update(**{key:value}),source={'kind':'company','path':'/'+key+suffix,'code':code})
    for both in (False,True):
        def legacy_recipient(q,both=both):
            if not both:q['recipient'].pop('nameLines')
            q['recipient']['name']='旧宛先'
        add('recipient-old-'+('both' if both else 'only'),legacy_recipient,'/recipient/name','unknown-field',400)
        def legacy_company(c,both=both):
            if not both:c.pop('nameLines')
            c['name']='旧会社'
        add('company-old-'+('both' if both else 'only'),path='/companyId',code='registered-data-invalid',company_change=legacy_company,source={'kind':'company','path':'/name','code':'unknown-field'})
    for key,value in (('addressLines',['架空住所']),('department','営業部'),('contactName','架空花子'),('honorific','様')):
        add('recipient-unknown-'+key,lambda q,key=key,value=value:q['recipient'].update(**{key:value}),'/recipient/'+key,'unknown-field',400)
    add('recipient-name-required',lambda q:q['recipient'].pop('nameLines'),'/recipient/nameLines','required')
    for key in ('nameLines','addressLines'):
        add('company-required-'+key,path='/companyId',code='registered-data-invalid',company_change=lambda c,key=key:c.pop(key),source={'kind':'company','path':'/'+key,'code':'required'})
    def narrow(d,key):next(f for f in d['fields'] if f['key']==key)['rect']['width']=.03
    add('recipient-overflow',path='/recipient/nameLines/0',code='field-overflow',definition_change=lambda d:narrow(d,'recipient'))
    for field,path in (('issuer','/nameLines/0'),('address','/addressLines/0')):
        add(field+'-overflow',path='/companyId',code='registered-data-invalid',definition_change=lambda d,field=field:narrow(d,field),source={'kind':'company','path':path,'code':'field-overflow'})
    add('nine-items',lambda q:q.update(items=q['items']*9),'/items/8','too-many-items')
    add('missing-amount',lambda q:q['items'][0].pop('amount'),'/items/0/amount','required')
    for label,value,code in (('int6','100000','quantity-format'),('fraction4','1.0000','quantity-format'),('number',1.25,'invalid-type')):
        add('quantity-'+label,lambda q,value=value:q['items'][0].update(quantity=value),'/items/0/quantity',code)
    add('amount-fraction',lambda q:q['items'][0].update(amount=1.5),'/items/0/amount','invalid-integer')
    add('period-two-notes',lambda q:q.update(transaction=cases['invoice-8'][2]['transaction'],notesLines=['一','二']),'/notesLines/1','notes-budget-exceeded')
    for label,value,code in (('null',None,'invalid-type'),('enum','left','unsupported-quantity-alignment')):
        add('mode-request-'+label,lambda q,value=value:q.update(**{ALIGN:value}),'/'+ALIGN,code)
        for issued in ('decimal','right'):
            add('mode-registered-'+label+'-'+issued,lambda q,issued=issued:q.update(**{ALIGN:issued}),'/templateId','registered-data-invalid',settings_change=lambda s,value=value:s.update(**{ALIGN:value}),source={'kind':'templateSettings','path':'/'+ALIGN,'code':code})
    c,s,q=cases['invoice-1'];craw,sraw=encode(c),encode(s)
    for name,raw,status,code in (('malformed',b'{',400,'invalid-json'),('duplicate',b'{"recipient":{"nameLines":["a"],"nameLines":["b"]}}',400,'duplicate-key'),('body-limit',b' '*65537,413,'request-too-large')):
        errors.append((name,craw,sraw,raw,definitions,{'status':status,'code':'validation-failed','errors':[{'path':'','code':code}]}))
    return errors


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True)
    output=parser.parse_args().output.resolve();output.mkdir(parents=True,exist_ok=False)
    pics=r.images(output);template=(r.ROOT/'line-comparison/refined-template.pdf').read_bytes()
    definitions={k:json.loads((DRAFT/'layout'/(k+'.definition.json')).read_text()) for k in ('invoice','estimate')}
    cases,expected=fixtures();errors=invalid_fixtures(cases,definitions)
    result={'api_sha':r.API_SHA,'site_assets_sha':r.SITE_SHA,'evidence_parent_commit':'ad6c7b1ea67cf719b843c7507691e85dbb79f6eb',
            'scope':'v0.3 fixture validation/mapping, adopted names/quantity/settings; not product HTTP/storage/auth',
            'script_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),'template_sha256':hashlib.sha256(template).hexdigest(),
            'environment':{'image_id':json.loads(r.smoke.docker('image','inspect','amane-pdf-api:site134-465088e').stdout)[0]['Id'],
                'limits':'CPU1/RAM1536MiB/no extra swap/tmpfs256MiB/read-only/network none/shared2 queue0','transport':'docker exec loopback HTTP, sequential',
                'python':platform.python_version(),'timing':'JSON parse/preflight/map to final body; excludes save/PDF checks/startup'},'cases':{},'errors':{}}
    for c,s,q in cases.values():preflight(*map(encode,(c,s,q)),definitions)
    with r.smoke.running_container('amane-pdf-api:site134-465088e',isolated=True) as api:
        calls=[];original=api.request
        def counted(method,path,*args,**kwargs):
            calls.append({'method':method,'path':path});return original(method,path,*args,**kwargs)
        api.request=counted
        for name,(c,s,q) in cases.items():
            d=output/name;d.mkdir();raws=list(map(encode,(c,s,q)))
            for file,raw in zip(('company.json','settings.json','request.json'),raws):(d/file).write_bytes(raw)
            before,posts=len(calls),api.post_count
            measure=process(api,*raws,definitions,template,pics,d)
            assert measure['effective']=={ALIGN:expected[name][0],'source':expected[name][1]},name
            assert api.post_count-posts==3 and len(calls)-before==3
            assert all((d/file).read_bytes()==raw for file,raw in zip(('company.json','settings.json','request.json'),raws))
            measure.update(api_calls=calls[before:],post_count_delta=api.post_count-posts)
            result['cases'][name]=measure;api.assert_clean()
        for name,craw,sraw,qraw,defs,expected_error in errors:
            d=output/'errors'/name;d.mkdir(parents=True)
            for file,raw in zip(('company.json','settings.json','request.json'),(craw,sraw,qraw)):(d/file).write_bytes(raw)
            if defs!=definitions:r.save(d/'definitions.json',defs)
            before,posts=len(calls),api.post_count
            try:process(api,craw,sraw,qraw,defs,template,pics,d)
            except Invalid as error:
                assert error.problem==expected_error,(name,error.problem,expected_error);r.save(d/'error.json',error.problem)
            else:raise AssertionError('invalid accepted: '+name)
            assert api.post_count==posts and len(calls)==before and not list(d.glob('*.pdf'))
            result['errors'][name]={'problem':expected_error,'api_call_count':0,'posts_before':posts,'posts_after':api.post_count,'pdf_created':False}
        result.update(api_post_count=api.post_count,health_status=api.request('GET','/healthz')[0],cleanup='assert_clean passed after each success and at end')
        api.assert_clean()
    result['comparisons']={}
    for kind in ('invoice','estimate'):
        for count in (1,8):
            name=f'{kind}-{count}';a,b=[output/(name+'-boundary-'+mode) for mode in ('decimal','right')]
            assert (a/'final.extracted.json').read_bytes()==(b/'final.extracted.json').read_bytes()
            assert (a/'final.txt').read_bytes()==(b/'final.txt').read_bytes()
            keys=json.loads((a/'field-index.json').read_text());fa,fb=[json.loads((d/'fields.json').read_text()) for d in (a,b)]
            assert qr.m.c.other_word_positions(a,fa,keys)==qr.m.c.other_word_positions(b,fb,keys)
            qa,qb=[json.loads((d/'request.json').read_text()) for d in (a,b)]
            qa.pop(ALIGN,None);qb.pop(ALIGN,None);assert qa==qb
            r.command('qpdf','--empty','--pages',str(a/'final.pdf'),'1',str(b/'final.pdf'),'1','--',str(output/(name+'-comparison.pdf')))
            r.command('qpdf','--check',str(output/(name+'-comparison.pdf')))
            result['comparisons'][name]={'same_request_except_mode':True,'same_text':True,'same_non_quantity_word_positions':True}
    result['summary']={'valid':len(cases),'rejected':len(errors),'rejected_api_calls':0,'all_four_boundary_shapes_both_modes':True}
    r.save(output/'results.json',result);print(json.dumps(result['summary']))


if __name__=='__main__':main()
