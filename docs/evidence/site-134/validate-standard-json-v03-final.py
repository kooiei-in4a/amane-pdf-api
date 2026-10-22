#!/usr/bin/env python3
"""#134 採用R1-R8の最終fixture検証。製品・共有mainは変更しない。"""
import argparse
import copy
from concurrent.futures import ThreadPoolExecutor
from decimal import Decimal
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import platform
import re
import threading
import time
import xml.etree.ElementTree as ET
import reproduce as r

spec=importlib.util.spec_from_file_location('v03_history',r.ROOT/'validate-standard-json-v03.py')
old=importlib.util.module_from_spec(spec);spec.loader.exec_module(old)
Invalid,fail=old.Invalid,old.fail
DRAFT=old.DRAFT
ALIGN='quantityAlignment'

def read_json(raw):
    if len(raw)>65536:fail('','request-too-large',413)
    def pairs(items):
        result={}
        for key,value in items:
            if key in result:fail('','duplicate-key',400)
            result[key]=value
        return result
    def constant(_):fail('','invalid-json',400)
    try:
        # -0をint(0)へ失わず、金額の符号なし整数規則で拒否する。
        return json.loads(raw.decode('utf-8'),object_pairs_hook=pairs,parse_float=Decimal,
                          parse_int=lambda s:Decimal(s) if s=='-0' else int(s),parse_constant=constant)
    except (ValueError,UnicodeError):fail('','invalid-json',400)

original_map=old.map_fields

def map_fields(company,settings,request,definition):
    if type(request) is dict and type(request.get('items')) is list:
        for i,item in enumerate(request['items']):
            q=item.get('quantity') if type(item) is dict else None
            if type(q) is str and re.fullmatch(r'0(?:\.0{1,3})?',q):
                fail(f'/items/{i}/quantity','out-of-range')
    return original_map(company,settings,request,definition)

# このprocess内の検証専用moduleだけに適用。履歴スクリプトのファイルは変更しない。
old.read_json=read_json;old.map_fields=map_fields
MESSAGES={
 'required':'必須項目に空でない値を指定してください。','invalid-type':'項目の型を確認してください。',
 'out-of-range':'数量は正数、金額は指定された非負整数範囲で指定してください。',
 'invalid-integer':'金額は符号・小数部・指数のないJSON整数で指定してください。',
 'quantity-format':'数量は整数部5桁・小数部3桁以内の正の十進文字列で指定してください。',
 'unknown-field':'対応していない項目を削除してください。','duplicate-key':'重複したkeyを削除してください。',
 'invalid-json':'UTF-8の正しいJSONを指定してください。','request-too-large':'本文を65536 bytes以内にしてください。',
 'registered-data-invalid':'sourceで示した登録情報を修正してください。',
 'too-long':'文字数の上限を超えています。','line-count':'指定した行数の範囲内にしてください。',
 'invalid-line-break':'配列の1行内に改行を含めないでください。',
 'unsupported-character':'固定フォントの対応文字へ入力を修正してください。',
 'combining-mark-not-supported':'結合濁点・半濁点を使わず合成済み文字を指定してください。',
 'forbidden-sequence':'描画時に解釈される禁止文字列を削除してください。',
 'notes-budget-exceeded':'取引情報と備考を共有3行・48文字の配分内にしてください。',
 'invalid-date':'実在するYYYY-MM-DDの日付を指定してください。',
 'invalid-period':'期間の開始日を終了日以前にしてください。',
 'ambiguous-transaction':'dateまたはperiodの片方だけを指定してください。',
 'unsupported-tax-rate':'税率は整数8または10で指定してください。',
 'unsupported-currency':'通貨はJPYを指定してください。','field-overflow':'文字が表示枠を超えています。',
 'too-many-items':'明細は8件以内にしてください。','too-few-items':'明細は1件以上指定してください。',
 'unsupported-quantity-alignment':'decimalまたはrightを指定してください。',
 'not-applicable':'この帳票種別では使用できない項目を削除してください。',
 'invalid-reference':'参照IDを確認してください。','invalid-registration-number':'Tと数字13桁を指定してください。'}

def process(*args,**kwargs):
    try:return old.process(*args,**kwargs)
    except Invalid as e:
        item=e.problem['errors'][0]
        assert item['code'] in MESSAGES,item
        item['message']=MESSAGES[item['code']]
        raise


def fixtures():
    history,expected=old.fixtures();cases={};expect={}
    for kind in ('invoice','estimate'):
        for n in (1,8):
            for mode in ('decimal','right'):
                name=f'{kind}-{n}-boundary-{mode}'
                c,s,q=copy.deepcopy(history[name])
                if n==1:
                    q.update(transaction={'date':'2026-10-20'},notesLines=['証'*16,'明'*16])
                else:
                    q.update(transaction={'period':{'start':'2026-12-31','end':'2027-01-01'}},notesLines=['備'*16])
                    q['issueDate']='2027-01-02'
                # 名称・数量・金額・品名の境界を同時に描画。
                for i,item in enumerate(q['items']):
                    item.update(name='品'*17,unitPrice=99999999,amount=999999999999)
                q['totalAmount']=999999999999
                for bucket in q['taxSummary'].values():bucket.update(taxableAmount=999999999999,taxAmount=999999999999)
                cases[name]=(c,s,q);expect[name]=expected[name]
    for name in ('default-decimal','provided-amounts-unchanged'):
        cases[name]=copy.deepcopy(history[name]);expect[name]=expected[name]
    for kind in ('invoice','estimate'):
        name=f'{kind}-8-roles';c,s,q=copy.deepcopy(history[name])
        q.update(transaction={'period':{'start':'2026-10-22','end':'2026-10-22'}},notesLines=['備'*16])
        cases[name]=(c,s,q);expect[name]=expected[name]
    for n in (1,8):
        for mode in ('decimal','right'):
            c,s,q=copy.deepcopy(cases[f'estimate-{n}-boundary-{mode}']);q.pop('transaction')
            q['notesLines']=['証'*16,'明'*16,'先'*14] if n==8 else []
            name=f'estimate-{n}-omitted-{mode}'
            cases[name]=(c,s,q);expect[name]=expect[f'estimate-{n}-boundary-{mode}']
    for kind in ('invoice','estimate'):
        c,s,q=copy.deepcopy(history[kind+'-8'])
        for item in q['items']:item.update(quantity='0.001',unitPrice=0,amount=0,taxRate=10)
        for bucket in q['taxSummary'].values():bucket.update(taxableAmount=0,taxAmount=0)
        q['totalAmount']=0
        cases[kind+'-zero']=(c,s,q);expect[kind+'-zero']=expected[kind+'-8']
        c,s,q=copy.deepcopy(history[kind+'-1'])
        c={k:c[k] for k in ('nameLines','addressLines')}
        for key in ('subject','paymentDueDate','validUntil','notesLines'):q.pop(key,None)
        if kind=='estimate':q.pop('transaction',None)
        cases[kind+'-minimal-no-images']=(c,s,q);expect[kind+'-minimal-no-images']=expected[kind+'-1']
    for kind,remove in (('invoice','sealAssetId'),('estimate','logoAssetId')):
        c,s,q=copy.deepcopy(history[kind+'-1']);c.pop(remove)
        name=kind+'-single-image';cases[name]=(c,s,q);expect[name]=expected[kind+'-1']
    c,s,q=copy.deepcopy(history['invoice-1']);q['subject']='割合100%確認'
    cases['ordinary-percent']=(c,s,q);expect['ordinary-percent']=expected['invoice-1']
    c,s,q=copy.deepcopy(history['invoice-1'])
    raw=old.encode(q);padded=raw+b' '*(65536-len(raw))
    cases['body-exact-65536']=(c,s,padded);expect['body-exact-65536']=expected['invoice-1']
    return cases,expect,history


def errors(history,definitions):
    result=old.invalid_fixtures(history,definitions)
    def add(name,change=lambda q:None,path='',code='',status=422,company=None,settings=None,raw_change=None):
        c,s,q=copy.deepcopy(history['invoice-1']);change(q)
        if company:company(c)
        if settings:settings(s)
        raw=old.encode(q)
        if raw_change:raw=raw_change(raw)
        result.append((name,old.encode(c),old.encode(s),raw,definitions,
                       {'status':status,'code':'validation-failed','errors':[{'path':path,'code':code}]}))
    for key in ('number','currency','issueDate','items','taxSummary','totalAmount','transaction'):
        add('required-'+key,lambda q,k=key:q.pop(k),'/'+key,'required')
    for key in ('name','quantity','unitPrice','taxRate'):
        add('item-required-'+key,lambda q,k=key:q['items'][0].pop(k),'/items/0/'+key,'required')
    for label,value,code in [('zero','0','out-of-range'),('zero3','0.000','out-of-range'),('negative','-1','quantity-format'),('negativezero','-0.000','quantity-format'),('leadingzero','01','quantity-format'),('plus','+1','quantity-format'),('exponent','1e3','quantity-format'),('comma','1,000','quantity-format'),('space',' 1','quantity-format')]:
        add('quantity-'+label,lambda q,value=value:q['items'][0].update(quantity=value),'/items/0/quantity',code)
    for label,key,value,code in [('price-max','unitPrice',100000000,'out-of-range'),('amount-max','amount',1000000000000,'out-of-range'),('negative','amount',-1,'out-of-range'),('string','amount','125','invalid-integer'),('bool','amount',True,'invalid-integer')]:
        add('money-'+label,lambda q,key=key,value=value:q['items'][0].update(**{key:value}),'/items/0/'+key,code)
    for label,token in [('exponent','1e3'),('negative-zero','-0'),('signed-plus','+1')]:
        add('money-'+label,path='/items/0/amount' if label!='signed-plus' else '',code='invalid-integer' if label!='signed-plus' else 'invalid-json',status=422 if label!='signed-plus' else 400,
            raw_change=lambda b,t=token:re.sub(rb'"amount": 125',('"amount": '+t).encode(),b))
    add('total-max',lambda q:q.update(totalAmount=1000000000000),'/totalAmount','out-of-range')
    for rate in ('10','8'):
        add('tax-bucket-missing-'+rate,lambda q,rate=rate:q['taxSummary'].pop(rate),'/taxSummary/'+rate,'required')
        for key in ('taxableAmount','taxAmount'):
            add('tax-required-'+rate+'-'+key,lambda q,rate=rate,key=key:q['taxSummary'][rate].pop(key),'/taxSummary/'+rate+'/'+key,'required')
            add('tax-max-'+rate+'-'+key,lambda q,rate=rate,key=key:q['taxSummary'][rate].update(**{key:1000000000000}),'/taxSummary/'+rate+'/'+key,'out-of-range')
    for value in (0,5,'8'):
        add('tax-rate-'+str(value)+type(value).__name__,lambda q,value=value:q['items'][0].update(taxRate=value),'/items/0/taxRate','unsupported-tax-rate')
    add('currency-usd',lambda q:q.update(currency='USD'),'/currency','unsupported-currency')
    for label,t,path,code in [('null',None,'/transaction','invalid-type'),('empty',{},'/transaction','ambiguous-transaction'),('type','2026-10-20','/transaction','invalid-type'),('both',{'date':'2026-10-20','period':{'start':'2026-10-20','end':'2026-10-21'}},'/transaction','ambiguous-transaction'),('bad-date',{'date':'2026-02-30'},'/transaction/date','invalid-date'),('start-missing',{'period':{'end':'2026-10-20'}},'/transaction/period/start','required'),('end-missing',{'period':{'start':'2026-10-20'}},'/transaction/period/end','required'),('reverse',{'period':{'start':'2026-10-21','end':'2026-10-20'}},'/transaction/period/end','invalid-period')]:
        add('transaction-'+label,lambda q,t=t:q.update(transaction=t),path,code)
    add('date-three-notes',lambda q:q.update(notesLines=['一','二','三']),'/notesLines/2','notes-budget-exceeded')
    add('notes-fifty',lambda q:(q.update(templateId='estimate-standard-v1',notesLines=['証'*16]*3),q.pop('transaction'),q.pop('paymentDueDate')),'/notesLines','too-long')
    add('notes-seventeen',lambda q:q.update(notesLines=['備'*17]),'/notesLines/0','too-long')
    for label,value,code in [('line-feed','一\n二','unsupported-character'),('combining','か\u3099','combining-mark-not-supported'),('half-combining','は\u309a','combining-mark-not-supported'),('emoji','😀','unsupported-character'),('variation','葛\ufe00','unsupported-character'),('percent-token','%p','forbidden-sequence'),('slash-n',r'\n','forbidden-sequence'),('blank','  ','required'),('null',None,'invalid-type'),('empty','','required')]:
        add('subject-'+label,lambda q,value=value:q.update(subject=value),'/subject',code)
    add('no-items',lambda q:q.update(items=[]),'/items','too-few-items')
    add('wrong-expiry',lambda q:q.update(validUntil='2026-11-30'),'/validUntil','not-applicable')
    add('multiple-errors',lambda q:(q.pop('number'),q['items'][0].update(quantity='0')),'/number','required')
    # 同じ入口で会社／登録設定の64KiB超過も拒否する。
    c,s,q=history['invoice-1']
    for label,raws in [('company-body',(b' '*65537,old.encode(s),old.encode(q))),('settings-body',(old.encode(c),b' '*65537,old.encode(q)))]:
        result.append((label,*raws,definitions,{'status':413,'code':'validation-failed','errors':[{'path':'','code':'request-too-large'}]}))
    return result


def reservations(directory,fields,keys,rendered):
    words=ET.fromstring((directory/'final.bbox.html').read_bytes()).findall('.//{http://www.w3.org/1999/xhtml}word')
    rects={'logo':(.05*595,.04*842,.18*595,.18*595/3),'seal':(.82*595,.26*842,.1*595,.1*595)}
    for name in rendered:
        x,y,w,h=rects[name]
        for word in words:
            a,b,c,d=[float(word.attrib[k]) for k in ('xMin','yMin','xMax','yMax')]
            assert min(c,x+w)-max(a,x)<=.01 or min(d,y+h)-max(b,y)<=.01,(name,word.text)
    return {'word_image_intersections':0,'reserved_image_regions_checked':rendered}


# 0円は単価・金額の両列に現れるため、隣接欄の検査対象をx範囲でも特定する。
def inspect_quantity(directory, fields, keys):
    words = ET.fromstring((directory / "final.bbox.html").read_bytes()).findall(".//{http://www.w3.org/1999/xhtml}word")
    pixels = r.np.array(r.Image.open(directory / "final.png").convert("RGB"))
    metrics, dots, anchors = [], [], []
    def same_row(word, field):
        return (float(word.attrib["yMin"]) >= field["y"] * 842 - .1 and
                float(word.attrib["yMax"]) <= (field["y"] + field["height"]) * 842 + .1)
    for key, f in zip(keys, fields):
        if not key.startswith("row") or not key.endswith(".quantity"):
            continue
        row = int(key.split(".")[0][3:])
        candidates = [w for w in words if w.text == f["text"] and
                      same_row(w, f) and
                      float(w.attrib["xMin"]) >= f["x"] * 595 - .1]
        assert len(candidates) == 1, (key, len(candidates))
        word = candidates[0]
        left, right = float(word.attrib["xMin"]), float(word.attrib["xMax"])
        assert abs((right - left) - old.qr.m.c.advance(f["text"])) < .05
        anchor = left + old.qr.m.c.advance(f["text"].partition(".")[0])
        anchors.append(anchor)
        metric = {"key": key, "quantity": f["text"], "word_x_min_pt": left,
                  "word_x_max_pt": right, "decimal_or_integer_end_pt": anchor}
        if "." in f["text"]:
            # PDF実描画のドット画素を測る。予測領域内に隣の数字や罫線は入らない。
            x0, x1 = math.floor(anchor * 2), math.ceil((anchor + old.qr.m.c.DOT_PT) * 2)
            y0, y1 = math.floor(f["y"] * 842 * 2), math.ceil((f["y"] + f["height"]) * 842 * 2)
            ys, xs = r.np.nonzero(r.np.all(pixels[y0:y1, x0:x1] < 100, axis=2))
            assert len(xs) > 0 and max(ys) - min(ys) <= 5
            center = float(r.np.mean(xs + x0) / 2)
            dots.append(center)
            metric["dot_ink_center_pt"] = center
        # 品名・単価の実wordと非交差、定義セルにも入ることを確認。
        neighbors = {}
        for suffix in ("name", "price"):
            neighbor_key = f"row{row}.{suffix}"
            neighbor = fields[keys.index(neighbor_key)]
            matches = [w for w in words if w.text == neighbor["text"] and same_row(w, neighbor) and float(w.attrib["xMin"]) >= neighbor["x"]*595-.1 and float(w.attrib["xMax"]) <= (neighbor["x"]+neighbor["width"])*595+.1]
            assert len(matches) == 1
            neighbors[suffix] = [float(matches[0].attrib[k]) for k in ("xMin", "xMax")]
        assert neighbors["name"][1] < left and right < neighbors["price"][0]
        metric["gap_to_name_pt"] = left - neighbors["name"][1]
        metric["gap_to_price_pt"] = neighbors["price"][0] - right
        metrics.append(metric)
    return {"rows": metrics, "anchor_spread_pt": max(anchors) - min(anchors),
            "dot_ink_center_spread_pt": max(dots) - min(dots) if dots else None}


old.qr.m.c.inspect_quantity=inspect_quantity

def clean(api):
    api.assert_clean()
    # API以外の処理子processが残らないことを/procで確認する。検査自身のshは除く。
    command="for p in /proc/[0-9]*/comm; do read n < \"$p\"; case \"$n\" in qpdf|pdfcpu) echo remaining;; esac; done"
    assert not api.exec('sh','-c',command).stdout.strip()


def main():
    p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True)
    out=p.parse_args().output.resolve();out.mkdir(parents=True,exist_ok=False)
    definitions={k:json.loads((DRAFT/'layout'/(k+'.definition.json')).read_text()) for k in ('invoice','estimate')}
    template=(r.ROOT/'line-comparison/refined-template.pdf').read_bytes();pics=r.images(out)
    cases,expect,history=fixtures();bad=errors(history,definitions)
    result={'api_sha':r.API_SHA,'site_assets_sha':r.SITE_SHA,'spec_evidence_commit':'0e76a4f19ee732221d581901310bbbaddf6f2d1f',
            'scope':'adopted R1-R8 fixture preflight/mapping and existing PDF API, not product/storage/auth/site HTTP',
            'script_sha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),'template_sha256':hashlib.sha256(template).hexdigest(),
            'environment':{'image_id':json.loads(r.smoke.docker('image','inspect','amane-pdf-api:site134-465088e').stdout)[0]['Id'],
                           'limits':'CPU1/RAM1536MiB/no extra swap/tmpfs256MiB/read-only/network none/shared2 queue0','transport':'docker exec loopback HTTP','python':platform.python_version(),
                           'timing':'JSON parse/preflight/map to final PDF body; excludes startup/save/extraction'},'cases':{},'errors':{}}
    calls=[];lock=threading.Lock();tls=threading.local();inflight=0;peak=0
    with r.smoke.running_container('amane-pdf-api:site134-465088e',isolated=True) as api:
        original=api.request
        def counted(method,path,*args,**kwargs):
            nonlocal inflight,peak
            with lock:
                calls.append({'case':getattr(tls,'name','health'),'method':method,'path':path})
                if method=='POST':inflight+=1;peak=max(peak,inflight)
            try:return original(method,path,*args,**kwargs)
            finally:
                if method=='POST':
                    with lock:inflight-=1
        api.request=counted
        def run(name,case,expected,concurrent=False,barrier=None):
            tls.name=name;c,s,q=case;directory=out/name;directory.mkdir()
            raws=(old.encode(c),old.encode(s),q if type(q) is bytes else old.encode(q))
            for f,raw in zip(('company.json','settings.json','request.json'),raws):(directory/f).write_bytes(raw)
            if barrier:barrier.wait()
            measure=process(api,*raws,definitions,template,pics,directory)
            assert measure['effective']=={ALIGN:expected[0],'source':expected[1]},name
            measure.update(reservations(directory,json.loads((directory/'fields.json').read_text()),json.loads((directory/'field-index.json').read_text()),measure['rendered_images']))
            expected_posts=1+sum(k in c for k in ('logoAssetId','sealAssetId'))
            relevant=[x for x in calls if x['case']==name]
            assert len(relevant)==expected_posts and all(x['method']=='POST' for x in relevant)
            assert measure['json_to_pdf_seconds']<=(20 if concurrent else 10)
            assert measure['final_bytes']<=2*1024*1024
            measure.update(api_calls=relevant,post_count_delta=expected_posts,acceptance_passed=True)
            for f,raw in zip(('company.json','settings.json','request.json'),raws):assert (directory/f).read_bytes()==raw
            r.save(directory/'measure.json',measure)
            return measure
        for name,case in cases.items():
            result['cases'][name]=run(name,case,expect[name]);clean(api)
            r.save(out/'results.json',result);print('PASS '+name,flush=True)
        # 先行スクリプトから追加した拒否ケースも実clientをstubせず同じprocessへ。
        for name,craw,sraw,qraw,defs,want in bad:
            tls.name='error-'+name;directory=out/'errors'/name;directory.mkdir(parents=True)
            for f,raw in zip(('company.json','settings.json','request.json'),(craw,sraw,qraw)):(directory/f).write_bytes(raw)
            if defs!=definitions:r.save(directory/'definitions.json',defs)
            before,posts=len(calls),api.post_count
            try:process(api,craw,sraw,qraw,defs,template,pics,directory)
            except Invalid as e:
                got=copy.deepcopy(e.problem)
                assert len(got['errors'])==1 and isinstance(got['errors'][0].pop('message'),str)
                assert got==want,(name,got,want)
                r.save(directory/'error.json',e.problem)
            else:raise AssertionError('invalid accepted: '+name)
            assert len(calls)==before and api.post_count==posts and not list(directory.glob('*.pdf'))
            result['errors'][name]={'problem':json.loads((directory/'error.json').read_text()),'api_call_count':0,'posts_before':posts,'posts_after':api.post_count,'pdf_created':False}
        # UTF-8 byte上限、空白保持は描画前の表現を明示検査。全欄抽出試験と区別。
        c,s,q=copy.deepcopy(history['invoice-1']);q['subject']=' 前後100% '
        parsed=old.preflight(*map(old.encode,(c,s,q)),definitions)
        assert parsed[3][1]['subject']==q['subject']
        result['preflight_only']={'leading_trailing_spaces_preserved_in_fields':True,'ordinary_percent_accepted':True,'pdf_generated_for_spaces':False}
        for kind in ('invoice','estimate'):
            for n in (1,8):
                a=out/f'{kind}-{n}-boundary-decimal';b=out/f'{kind}-{n}-boundary-right'
                assert (a/'final.extracted.json').read_bytes()==(b/'final.extracted.json').read_bytes()
                keys=json.loads((a/'field-index.json').read_text())
                fa,fb=[json.loads((d/'fields.json').read_text()) for d in (a,b)]
                assert old.qr.m.c.other_word_positions(a,fa,keys)==old.qr.m.c.other_word_positions(b,fb,keys)
                r.command('qpdf','--empty','--pages',str(a/'final.pdf'),'1',str(b/'final.pdf'),'1','--',str(out/f'{kind}-{n}-comparison.pdf'))
        barrier=threading.Barrier(2);start_posts=api.post_count;peak=0
        names=['invoice-8-boundary-decimal','estimate-8-boundary-right']
        with ThreadPoolExecutor(max_workers=2) as pool:
            futures={name:pool.submit(run,'concurrent-'+name,cases[name],expect[name],True,barrier) for name in names}
            result['concurrent']={name:f.result() for name,f in futures.items()}
        assert api.post_count-start_posts==6 and peak==2
        result['concurrent_peak_client_posts']=peak;clean(api)
        result.update(api_post_count=api.post_count,health_status=api.request('GET','/healthz')[0],cleanup='jobs and qpdf/pdfcpu children absent after successes and at end')
    result['summary']={'valid':len(cases),'concurrent_valid':2,'rejected':len(bad),'rejected_api_calls':0,'acceptance_passed':True}
    r.save(out/'results.json',result);print(json.dumps(result['summary']),flush=True)

if __name__=='__main__':main()
