#!/usr/bin/env python3
"""#134案のfixture専用検証。公開API・製品validatorではない。金額は計算しない。"""
import argparse
import base64
import copy
from datetime import date
from decimal import Decimal
import hashlib
import json
from pathlib import Path
import re
import struct
import time

import reproduce as r

ROOT = Path(__file__).resolve().parent
EXAMPLES = ROOT / "standard-json-draft/examples"
font_source = (ROOT / "api-source/src/Amane.Pdf.Api/FillTemplateGlyphs.g.cs").read_text()
expression = re.search(r"Glyphs = Convert\.FromBase64String\(([\s\S]*?)\);", font_source)[1]
font_data = base64.b64decode("".join(re.findall(r'"([A-Za-z0-9+/=]+)"', expression)))
assert hashlib.sha256(font_data).hexdigest() == re.search(r"data_sha256: ([0-9a-f]+)", font_source)[1]
WIDTHS = {chr(c): w for c, _, w in struct.iter_unpack("<HHH", font_data)}


class Invalid(Exception):
    def __init__(self, path, code, status=422, source=None):
        self.problem = {"status": status, "code": "validation-failed",
                        "errors": [{"path": path, "code": code}]}
        if source: self.problem["errors"][0]["source"] = source


def fail(path, code, status=422):
    raise Invalid(path, code, status)


def child(path, key):
    return path + "/" + str(key).replace("~", "~0").replace("/", "~1")


def read_json(raw):
    if len(raw) > 65536: fail("", "request-too-large", 413)
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result: fail("", "duplicate-key", 400)
            result[key] = value
        return result
    def constant(_): fail("", "invalid-json", 400)
    try:
        return json.loads(raw.decode("utf-8"), object_pairs_hook=pairs,
                          parse_float=Decimal, parse_constant=constant)
    except (ValueError, UnicodeError): fail("", "invalid-json", 400)


def obj(value, path, allowed, required=()):
    if type(value) is not dict: fail(path, "invalid-type")
    for key in value:
        if key not in allowed: fail(child(path, key), "unknown-field", 400)
    for key in required:
        if key not in value: fail(child(path, key), "required")


def text(value, path, maximum):
    if type(value) is not str: fail(path, "invalid-type")
    if not value or not value.strip(): fail(path, "required")
    for c in value:
        if c in "\u3099\u309a": fail(path, "combining-mark-not-supported")
        if c not in WIDTHS: fail(path, "unsupported-character")
    if re.search(r"%[pPtv]|\\n", value): fail(path, "forbidden-sequence")
    if len(value.encode("utf-16-le")) // 2 > maximum: fail(path, "too-long")
    return value


def lines(value, path, minimum, maximum, joined):
    if type(value) is not list: fail(path, "invalid-type")
    if not minimum <= len(value) <= maximum: fail(path, "line-count")
    for i, line in enumerate(value): text(line, child(path, i), 16)
    result = "\n".join(value)
    if len(result) > joined: fail(path, "too-long")
    return result


def identifier(value, path):
    if type(value) is not str: fail(path, "invalid-type")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", value): fail(path, "invalid-reference")


def day(value, path):
    if type(value) is not str: fail(path, "invalid-type")
    if not re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}", value): fail(path, "invalid-date")
    try: return date.fromisoformat(value)
    except ValueError: fail(path, "invalid-date")


def integer(value, path, maximum=999999999999):
    if type(value) is not int: fail(path, "invalid-integer")
    if not 0 <= value <= maximum: fail(path, "out-of-range")


def company_check(company):
    obj(company, "", {"name", "addressLines", "registrationNumber", "contact", "bankTransferLines", "logoAssetId", "sealAssetId"}, ("name", "addressLines"))
    text(company["name"], "/name", 16)
    lines(company["addressLines"], "/addressLines", 1, 2, 32)
    if "registrationNumber" in company:
        text(company["registrationNumber"], "/registrationNumber", 14)
        if not re.fullmatch(r"T[0-9]{13}", company["registrationNumber"]): fail("/registrationNumber", "invalid-registration-number")
    if "contact" in company: text(company["contact"], "/contact", 16)
    if "bankTransferLines" in company: lines(company["bankTransferLines"], "/bankTransferLines", 1, 3, 48)
    for key, fixture_id in (("logoAssetId", "logo_demo"), ("sealAssetId", "seal_demo")):
        if key in company:
            identifier(company[key], "/"+key)
            if company[key] != fixture_id: fail("/"+key, "invalid-reference")


def map_fields(company, request, definition):
    """fixtureのcompany_demoのみを解決し、v0.1案を表示文字列へ変換する。"""
    obj(request, "", {"companyId", "templateId", "currency", "recipient", "issueDate", "number", "subject", "paymentDueDate", "validUntil", "transaction", "items", "taxSummary", "totalAmount", "notesLines"},
        ("companyId", "templateId", "currency", "recipient", "issueDate", "number", "items", "taxSummary", "totalAmount"))
    identifier(request["companyId"], "/companyId")
    if request["companyId"] != "company_demo": fail("/companyId", "invalid-reference")
    if request["templateId"] not in ("invoice-standard-v1", "estimate-standard-v1"): fail("/templateId", "invalid-reference")
    kind = request["templateId"].split("-")[0]
    if definition["id"] != kind: fail("/templateId", "invalid-reference")
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
        if len(q) > 5 or not re.fullmatch(r"(0|[1-9][0-9]*)(\.[0-9]{1,3})?", q) or not any(c in "123456789" for c in q): fail(p+"/quantity", "quantity-format")
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
    fields, keys = r.fields_for(definition, values)
    for key, f in zip(keys, fields):
        widths = [sum(WIDTHS[c] for c in line)*f["fontSize"]/1000 for line in f["text"].split("\n")]
        height = f["fontSize"]*(1+1.2*f["text"].count("\n"))
        if max(widths) > f["width"]*595-.1*f["fontSize"] or height > f["height"]*842:
            origin = sources.get(key, {"path":""})
            if origin.get("kind") == "company": raise Invalid("/companyId", "registered-data-invalid", source=origin|{"code":"field-overflow"})
            fail(origin["path"], "field-overflow")
    if len(fields)>200 or sum(f["text"].count("\n")+1 for f in fields)>100: fail("", "render-budget-exceeded")
    if len(json.dumps(fields, ensure_ascii=False, separators=(",", ":")).encode())>65536: fail("", "render-budget-exceeded")
    return kind, values, fields, keys, sources, definition


def invalid_cases(company, base, period, definition, output):
    cases = []
    def add(name, change, path, code, source_company=None, modified_definition=None):
        request = copy.deepcopy(base); change(request)
        cases.append((name, json.dumps(request, ensure_ascii=False).encode(), source_company or company, modified_definition or definition, path, code))
    add("nine-items", lambda x:x.update(items=x["items"]*9), "/items/8", "too-many-items")
    add("empty-items", lambda x:x.update(items=[]), "/items", "too-few-items")
    add("missing-amount", lambda x:x["items"][0].pop("amount"), "/items/0/amount", "required")
    add("numeric-quantity", lambda x:x["items"][0].update(quantity=1.25), "/items/0/quantity", "invalid-type")
    add("long-quantity", lambda x:x["items"][0].update(quantity="123.45"), "/items/0/quantity", "quantity-format")
    add("negative-amount", lambda x:x["items"][0].update(amount=-1), "/items/0/amount", "out-of-range")
    add("fractional-amount", lambda x:x["items"][0].update(amount=1.5), "/items/0/amount", "invalid-integer")
    add("money-over-limit", lambda x:x.update(totalAmount=1000000000000), "/totalAmount", "out-of-range")
    add("unsupported-tax", lambda x:x["items"][0].update(taxRate=0), "/items/0/taxRate", "unsupported-tax-rate")
    add("unknown-recipient-field", lambda x:x["recipient"].update(addressLines=["架空"]), "/recipient/addressLines", "unknown-field")
    add("unsupported-character", lambda x:x["recipient"].update(name="架空𠮷野"), "/recipient/name", "unsupported-character")
    add("combining-mark", lambda x:x["recipient"].update(name="カ\u3099"), "/recipient/name", "combining-mark-not-supported")
    add("placeholder", lambda x:x["recipient"].update(name="架空%p"), "/recipient/name", "forbidden-sequence")
    add("invalid-date", lambda x:x.update(transaction={"date":"2026-02-30"}), "/transaction/date", "invalid-date")
    add("reversed-period", lambda x:x.update(transaction={"period":{"start":"2026-10-31","end":"2026-10-01"}}), "/transaction/period/end", "invalid-period")
    add("ambiguous-transaction", lambda x:x["transaction"].update(period=period["transaction"]["period"]), "/transaction", "ambiguous-transaction")
    add("missing-invoice-transaction", lambda x:x.pop("transaction"), "/transaction", "required")
    add("period-and-two-notes", lambda x:x.update(transaction=period["transaction"],notesLines=["一行目","二行目"]), "/notesLines/1", "notes-budget-exceeded")
    add("date-and-three-notes", lambda x:x.update(notesLines=["一","二","三"]), "/notesLines/2", "notes-budget-exceeded")
    add("wrong-expiry", lambda x:x.update(validUntil="2026-11-30"), "/validUntil", "not-applicable")
    add("invalid-company-reference", lambda x:x.update(companyId="company_missing"), "/companyId", "invalid-reference")
    bad_company=copy.deepcopy(company);bad_company["name"]="架空𠮷野"
    add("registered-company-character", lambda x:None, "/companyId", "registered-data-invalid", source_company=bad_company)
    narrow=copy.deepcopy(definition)
    next(f for f in narrow["fields"] if f["key"]=="recipient")["rect"]["width"]=.03
    add("narrow-template-fixture", lambda x:None, "/recipient/name", "field-overflow", modified_definition=narrow)
    raw=json.dumps(base,ensure_ascii=False).encode()
    cases += [("invalid-json", b"{", company, definition, "", "invalid-json"),
              ("duplicate-key", b'{"companyId":"a","companyId":"b"}', company, definition, "", "duplicate-key"),
              ("exponent-money", raw.replace(b'"amount": 125', b'"amount": 1e3'), company, definition, "/items/0/amount", "invalid-integer")]
    rows={}
    for name,raw,c,d,path,code in cases:
        directory=output/name;directory.mkdir()
        (directory/"request.json").write_bytes(raw)
        if c is not company:r.save(directory/"company.json",c)
        if d is not definition:r.save(directory/"definition.json",d)
        try: map_fields(c, read_json(raw), d)
        except Invalid as error:
            actual=error.problem["errors"][0]
            assert (actual["path"],actual["code"])==(path,code),(name,error.problem)
            r.save(directory/"preflight-error.json",error.problem)
            rows[name]={"problem":error.problem,"pdf_api_calls":0,"pdf_created":False,"origin":"local fixture validator, not site HTTP"}
        else: raise AssertionError("invalid input accepted: "+name)
    return rows


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--output",type=Path,default=Path("/tmp/amane-site134-standard-json"))
    args=parser.parse_args();output=args.output.resolve();output.mkdir(parents=True,exist_ok=False)
    company=read_json((EXAMPLES/"company-registration.json").read_bytes())
    definitions={k:json.loads((ROOT/"line-comparison"/(k+".definition.json")).read_text()) for k in ("invoice","estimate")}
    cases={name:read_json((EXAMPLES/(name+".json")).read_bytes()) for name in ("invoice-1","invoice-8","estimate-1","estimate-8","provided-amounts-unchanged")}
    cases["date-notes-boundary"]=copy.deepcopy(cases["invoice-1"]);cases["date-notes-boundary"]["notesLines"]=["証"*16,"証"*16]
    cases["period-notes-boundary"]=copy.deepcopy(cases["invoice-8"]);cases["period-notes-boundary"].update(issueDate="2027-01-02",paymentDueDate="2027-02-01",transaction={"period":{"start":"2026-12-31","end":"2027-01-01"}},notesLines=["証"*16])
    cases["estimate-no-transaction"]=copy.deepcopy(cases["estimate-1"]);cases["estimate-no-transaction"].pop("transaction");cases["estimate-no-transaction"]["notesLines"]=["証"*16,"証"*16,"証"*14]
    cases["money-boundary"]=copy.deepcopy(cases["invoice-1"])
    cases["money-boundary"]["items"][0].update(quantity="99999",unitPrice=99999999,amount=999999999999)
    cases["money-boundary"]["taxSummary"]={rate:{"taxableAmount":999999999999,"taxAmount":999999999999} for rate in ("8","10")};cases["money-boundary"]["totalAmount"]=999999999999
    result={"proposal_commit":"ffa49b4228faa24e0d5166a5eeb07c25d689a58c","api_sha":r.API_SHA,"site_assets_sha":r.SITE_SHA,
            "scope":"fixture-only JSON validation/mapping; no product API, persistence or authentication", "cases":{},"errors":{}}
    errors=output/"errors";errors.mkdir()
    result["errors"]=invalid_cases(company,cases["invoice-1"],cases["invoice-8"],definitions["invoice"],errors)
    template=(ROOT/"line-comparison/refined-template.pdf").read_bytes()
    pics=[(ROOT/"results"/name).read_bytes() for name in ("logo.png","seal.png")]
    result["environment"]={"image_id":json.loads(r.smoke.docker("image","inspect","amane-pdf-api:site134-465088e").stdout)[0]["Id"],
        "limits":"existing smoke isolated=True: CPU 1, RAM1536MiB/no extra swap, tmpfs256MiB, read-only, shared2/queue0", "transport":"docker exec loopback HTTP; no site relay", "timing":"JSON parse+mapping to final PDF body; excludes startup/save/render/extraction"}
    with r.smoke.running_container("amane-pdf-api:site134-465088e",isolated=True) as api:
        for name,request in cases.items():
            directory=output/name;directory.mkdir();r.save(directory/"company.json",company);r.save(directory/"request.json",request)
            kind=request["templateId"].split("-")[0]
            started=time.monotonic()
            kind,values,fields,keys,sources,definition=map_fields(read_json((directory/"company.json").read_bytes()),read_json((directory/"request.json").read_bytes()),definitions[kind])
            pdfs,measure=r.chain(api,template,fields,pics)
            measure["json_to_pdf_seconds"]=time.monotonic()-started
            api.assert_clean()
            r.save(directory/"display-values.json",values);r.save(directory/"fields.json",fields);r.save(directory/"field-index.json",keys);r.save(directory/"field-sources.json",sources);r.save(directory/"definition.json",definition)
            for stage,pdf in zip(("filled","logo","final"),pdfs):
                path=directory/(stage+".pdf");path.write_bytes(pdf)
                if stage=="final":measure["extraction"]=r.check_pdf(path,fields,keys)
                else:r.command("pdftoppm","-singlefile","-r","144","-png",str(path),str(path.with_suffix("")))
            measure["images"]=r.image_check(directory,[(.05,.04,.18),(.82,.26,.1)])
            measure["notes"]={"text":values.get("notes"),"lines":values.get("notes","").count("\n")+1,"characters":len(values.get("notes",""))}
            actual=json.loads((directory/"final.extracted.json").read_text())
            # 独立に入力の確定値と抽出結果を照合。金額の演算・相互比較はしない。
            expected={"total":f'{request["totalAmount"]:,}円'}
            for i,item in enumerate(request["items"]):
                expected[f"row{i}.amount"]=f'{item["amount"]:,}円';expected[f"row{i}.quantity"]=item["quantity"]
            for rate in ("8","10"):
                expected["subtotal"+rate]=f'{request["taxSummary"][rate]["taxableAmount"]:,}円';expected["tax"+rate]=f'{request["taxSummary"][rate]["taxAmount"]:,}円'
            assert all(actual[k]==v for k,v in expected.items())
            r.save(directory/"confirmed-values-extracted.json",{k:actual[k] for k in expected})
            measure.update(confirmed_values_preserved=True,fields=len(fields),rendered_lines=sum(f["text"].count("\n")+1 for f in fields))
            result["cases"][name]=measure;r.save(output/"results.json",result)
        result["api_errors"]={}
        good_fields=json.loads((output/"invoice-1/fields.json").read_text());keys=json.loads((output/"invoice-1/field-index.json").read_text())
        for name,value,status,reason in (("raw-overflow","架空"*20,422,"field-overflow"),("raw-unsupported","架空𠮷野",400,None),("raw-empty","",400,None)):
            fields=copy.deepcopy(good_fields);fields[keys.index("recipient")]["text"]=value
            actual,_,measure=r.post(api,template,fields=fields);assert actual==status and measure["problem"].get("reason")==reason
            r.save(output/(name+".fields.json"),fields);r.save(output/(name+".problem.json"),measure["problem"]);result["api_errors"][name]=measure;api.assert_clean()
        result["api_post_count"]=api.post_count
        result["health_status"]=api.request("GET","/healthz")[0]
        result["memory_peak_bytes"]=int(api.exec("cat","/sys/fs/cgroup/memory.peak").stdout)
        result["memory_events"]=api.exec("cat","/sys/fs/cgroup/memory.events").stdout.decode()
        result["cleanup"]="existing assert_clean passed after every success/failure"
    r.save(output/"results.json",result)
    print(json.dumps({"pdf_cases":len(result["cases"]),"local_errors":len(result["errors"]),"api_errors":len(result["api_errors"]),"api_posts":result["api_post_count"]}))


if __name__=="__main__": main()
