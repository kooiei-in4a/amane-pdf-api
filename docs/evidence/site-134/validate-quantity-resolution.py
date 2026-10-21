#!/usr/bin/env python3
"""#134 設定解決のfixture検証。製品validator・保存・HTTPエラー実装ではない。"""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import platform

import reproduce as r

spec = importlib.util.spec_from_file_location("quantity_modes", r.ROOT / "compare-quantity-modes.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
v = m.c.v
KEY = "quantityAlignment"
REQUEST_KEYS = {"companyId", "templateId", "currency", "recipient", "issueDate", "number", "subject",
                "paymentDueDate", "validUntil", "transaction", "items", "taxSummary", "totalAmount", "notesLines", KEY}
OMIT = object()


def mode_error(value):
    if type(value) is not str:
        return "invalid-type"
    if value not in ("decimal", "right"):
        return "unsupported-quantity-alignment"
    return None


def resolve(settings, request):
    """参照解決済みの登録設定を受ける。保存／参照権限／他項目の全検証は対象外。"""
    v.obj(settings, "", {"companyId", "templateId", KEY})
    v.obj(request, "", REQUEST_KEYS)
    for i, item in enumerate(request.get("items", [])):
        if KEY in item:
            v.fail(f"/items/{i}/{KEY}", "unknown-field", 400)
    if KEY in settings:
        code = mode_error(settings[KEY])
        if code:
            raise v.Invalid("/templateId", "registered-data-invalid", source={
                "kind": "templateSettings", "path": "/" + KEY, "code": code})
    if KEY in request:
        code = mode_error(request[KEY])
        if code:
            v.fail("/" + KEY, code)
        return {"quantityAlignment": request[KEY], "source": "request"}
    if KEY in settings:
        return {"quantityAlignment": settings[KEY], "source": "templateSettings"}
    return {"quantityAlignment": "decimal", "source": "default"}


def encode(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode()


def process(api, settings_raw, request_raw, base, directory, template, pics):
    """全ケース共通の入口。解決が成功するまでfieldsもPDFも作らない。"""
    settings, request = v.read_json(settings_raw), v.read_json(request_raw)
    before = copy.deepcopy((settings, request))
    try:
        effective = resolve(settings, request)
    finally:
        assert (settings, request) == before, "resolver mutated input"
    # ここ以降だけが描画経路。modeは期待値やcase名から渡さない。
    values = json.loads((base / "display-values.json").read_text())
    definition = json.loads((base / "definition.json").read_text())
    fields, keys, placement = m.mode_fields(definition, values, effective[KEY])
    expected = m.confirmed_values(request)
    assert all(values[k] == text for k, text in expected.items())
    r.save(directory / "effective.json", effective)
    r.save(directory / "fields.json", fields)
    r.save(directory / "field-index.json", keys)
    r.save(directory / "quantity-placement.json", placement)
    r.save(directory / "confirmed-values.json", expected)
    pdfs, measure = r.chain(api, template, fields, pics)
    for stage, pdf in zip(("filled", "logo", "final"), pdfs):
        path = directory / (stage + ".pdf")
        path.write_bytes(pdf)
        if stage == "final":
            measure["extraction"] = r.check_pdf(path, fields, keys)
        else:
            r.command("pdftoppm", "-singlefile", "-r", "144", "-png", str(path), str(path.with_suffix("")))
    actual = json.loads((directory / "final.extracted.json").read_text())
    assert all(actual[k] == text for k, text in expected.items())
    previous = base / effective[KEY]
    assert actual == json.loads((previous / "final.extracted.json").read_text())
    assert fields == json.loads((previous / "fields.json").read_text())
    assert (directory / "final.txt").read_bytes() == (previous / "final.txt").read_bytes()
    assert m.c.other_word_positions(directory, fields, keys) == m.c.other_word_positions(previous, fields, keys)
    positions = m.c.inspect_quantity(directory, fields, keys)
    ends = [row["word_x_max_pt"] for row in positions["rows"]]
    positions["right_edge_spread_pt"] = max(ends) - min(ends)
    if effective[KEY] == "decimal":
        assert positions["anchor_spread_pt"] < .1
        assert positions["dot_ink_center_spread_pt"] <= .5
    else:
        assert positions["right_edge_spread_pt"] < .05
    measure.update(effective=effective, quantity_positions=positions,
                   images=r.image_check(directory, [(.05, .04, .18), (.82, .26, .1)]),
                   confirmed_values_preserved=True, same_as_previous_mode_fields_and_text=True,
                   non_quantity_word_positions_match=True, input_objects_unchanged=True)
    return measure


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    output = parser.parse_args().output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    pics = r.images(output)
    template = (r.ROOT / "line-comparison/refined-template.pdf").read_bytes()
    result = {"api_sha": r.API_SHA, "site_assets_sha": r.SITE_SHA,
              "evidence_parent_commit": "8a7ae693d23c70e96e8c217de6f7e837f9a71a5f",
              "scope": "fixture resolver + existing PDF API; not product HTTP/storage/full JSON validation",
              "environment": "CPU1/RAM1536MiB/no extra swap/tmpfs256MiB/read-only/network none/shared2 queue0; Docker exec loopback HTTP",
              "python": platform.python_version(),
              "timing": "fill-template + stamp-image twice; excludes resolution, file IO and PDF checks",
              "image_id": json.loads(r.smoke.docker("image", "inspect", "amane-pdf-api:site134-465088e").stdout)[0]["Id"],
              "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "template_sha256": hashlib.sha256(template).hexdigest(), "valid": {}, "rejected": {}}
    # 期待値はresolverと独立の明示表。両帳票1/8明細を両モードでカバーする。
    valid = [
        ("01-default", OMIT, OMIT, "decimal", "default", "invoice-1"),
        ("02-registered-decimal", "decimal", OMIT, "decimal", "templateSettings", "estimate-1"),
        ("03-registered-right", "right", OMIT, "right", "templateSettings", "invoice-8"),
        ("04-request-decimal", OMIT, "decimal", "decimal", "request", "estimate-8"),
        ("05-request-right", OMIT, "right", "right", "request", "invoice-1"),
        ("06-same-decimal", "decimal", "decimal", "decimal", "request", "invoice-8"),
        ("07-override-right", "decimal", "right", "right", "request", "estimate-8"),
        ("08-override-decimal", "right", "decimal", "decimal", "request", "estimate-1"),
        ("09-same-right", "right", "right", "right", "request", "estimate-1"),
    ]
    bad_values = [("null", None, "invalid-type"), ("number", 42, "invalid-type"),
                  ("boolean", True, "invalid-type"), ("array", [], "invalid-type"),
                  ("object", {}, "invalid-type"), ("empty", "", "unsupported-quantity-alignment"),
                  ("left", "left", "unsupported-quantity-alignment"),
                  ("uppercase", "DECIMAL", "unsupported-quantity-alignment"),
                  ("spaces", " right ", "unsupported-quantity-alignment")]
    base = r.ROOT / "quantity-mode-comparison/invoice-8"
    normal = json.loads((base / "request.json").read_text())
    settings = {"companyId": normal["companyId"], "templateId": normal["templateId"]}
    rejected = []
    for label, bad, code in bad_values:
        # 正しい発行値2種だけでなく省略時も登録不正を拒否する。
        for override in (OMIT, "decimal", "right"):
            req = copy.deepcopy(normal)
            if override is not OMIT: req[KEY] = override
            name = "registered-" + label + "-request-" + ("omitted" if override is OMIT else override)
            rejected.append((name, encode(dict(settings, **{KEY: bad})), encode(req), 422,
                             "/templateId", "registered-data-invalid", code))
        req = dict(normal, **{KEY: bad})
        rejected.append(("request-" + label, encode(dict(settings, **{KEY: "right"})), encode(req),
                         422, "/" + KEY, code, None))
    extras = [
        ("malformed", b'{"quantityAlignment":', "", "invalid-json"),
        ("duplicate", b'{"quantityAlignment":"decimal","quantityAlignment":"right"}', "", "duplicate-key"),
        ("unknown-key", encode(dict(normal, quantityAlign="right")), "/quantityAlign", "unknown-field"),
    ]
    misplaced = copy.deepcopy(normal)
    misplaced["items"][0][KEY] = "right"
    extras.append(("per-item", encode(misplaced), "/items/0/" + KEY, "unknown-field"))
    for name, raw, path, code in extras:
        rejected.append((name, encode(settings), raw, 400, path, code, None))
    with r.smoke.running_container("amane-pdf-api:site134-465088e", isolated=True) as api:
        # 実API clientの全requestを観測する。GETも含む（startup/最後のhealthはcase外）。
        observed = []
        original = api.request
        def counted(method, path, *args, **kwargs):
            observed.append({"method": method, "path": path})
            return original(method, path, *args, **kwargs)
        api.request = counted
        for name, reg, issued, mode, source, fixture in valid:
            directory = output / name
            directory.mkdir()
            source_base = r.ROOT / "quantity-mode-comparison" / fixture
            req = json.loads((source_base / "request.json").read_text())
            registered = {"companyId": req["companyId"], "templateId": req["templateId"]}
            if reg is not OMIT: registered[KEY] = reg
            if issued is not OMIT: req[KEY] = issued
            raw_reg, raw_req = encode(registered), encode(req)
            (directory / "settings.json").write_bytes(raw_reg)
            (directory / "request.json").write_bytes(raw_req)
            start, posts = len(observed), api.post_count
            measure = process(api, raw_reg, raw_req, source_base, directory, template, pics)
            assert measure["effective"] == {KEY: mode, "source": source}
            assert api.post_count - posts == 3 and len(observed) - start == 3
            assert (directory / "settings.json").read_bytes() == raw_reg
            assert (directory / "request.json").read_bytes() == raw_req
            api.assert_clean()
            measure.update(fixture=fixture, api_calls=observed[start:], post_count_delta=api.post_count-posts)
            result["valid"][name] = measure
        for name, raw_reg, raw_req, status, path, code, source_code in rejected:
            directory = output / "rejected" / name
            directory.mkdir(parents=True)
            (directory / "settings.json").write_bytes(raw_reg)
            (directory / "request.json").write_bytes(raw_req)
            start, posts = len(observed), api.post_count
            try:
                process(api, raw_reg, raw_req, base, directory, template, pics)
            except v.Invalid as error:
                expected_error = {"path": path, "code": code}
                if source_code:
                    expected_error["source"] = {"kind": "templateSettings", "path": "/" + KEY, "code": source_code}
                assert error.problem == {"status": status, "code": "validation-failed", "errors": [expected_error]}
                r.save(directory / "error.json", error.problem)
            else:
                raise AssertionError("invalid input reached PDF generation")
            assert api.post_count == posts and len(observed) == start
            assert not list(directory.glob("*.pdf")) and not (directory / "fields.json").exists()
            result["rejected"][name] = {"problem": error_problem(directory), "api_call_count": 0,
                "post_count_before": posts, "post_count_after": api.post_count, "pdf_created": False}
        result.update(post_count=api.post_count, observed_case_api_calls=len(observed),
                      health_status=api.request("GET", "/healthz")[0], cleanup="assert_clean passed")
        api.assert_clean()
    result["summary"] = {"valid": len(valid), "rejected": len(rejected),
                         "all_rejections_before_api": True, "input_objects_unchanged": True}
    r.save(output / "results.json", result)
    print(json.dumps(result["summary"]))


def error_problem(directory):
    return json.loads((directory / "error.json").read_text())


if __name__ == "__main__":
    main()
