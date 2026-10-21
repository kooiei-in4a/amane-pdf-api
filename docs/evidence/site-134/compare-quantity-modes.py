#!/usr/bin/env python3
"""#134: 拡張セルでdecimal/rightを同じfixture入力から比較。製品設定APIではない。"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re

import reproduce as r

spec = importlib.util.spec_from_file_location("decimal_fixture", r.ROOT / "compare-decimal-quantity.py")
c = importlib.util.module_from_spec(spec)
spec.loader.exec_module(c)

QUANTITIES = ["99999.999", "99999", "0.001", "2.50", "1.000", "3", "123.45", "0.010"]


def confirmed_values(request):
    # 表示書式だけ。数量×単価・税計算・合算・金額間の比較は行わない。
    expected = {"total": f'{request["totalAmount"]:,}円'}
    for i, item in enumerate(request["items"]):
        expected[f"row{i}.quantity"] = item["quantity"]
        expected[f"row{i}.name"] = item["name"] + ("※" if item["taxRate"] == 8 else "")
        expected[f"row{i}.price"] = f'{item["unitPrice"]:,}円'
        expected[f"row{i}.amount"] = f'{item["amount"]:,}円'
        expected[f"row{i}.rate"] = str(item["taxRate"]) + "%"
    for rate, bucket in request["taxSummary"].items():
        expected["subtotal" + rate] = f'{bucket["taxableAmount"]:,}円'
        expected["tax" + rate] = f'{bucket["taxAmount"]:,}円'
    return expected


def mode_fields(definition, values, mode):
    if mode == "decimal":
        _, fields, keys, placement = c.decimal_fields(definition, values)
    elif mode == "right":
        fields, keys = r.fields_for(definition, values)
        placement = [{"key": key, "quantity": f["text"], "field": f} for key, f in zip(keys, fields)
                     if key.startswith("row") and key.endswith(".quantity")]
    else:
        raise ValueError("unsupported fixture mode")
    return fields, keys, placement


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    output = parser.parse_args().output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    pics = r.images(output)
    template_path = r.ROOT / "line-comparison/refined-template.pdf"
    template = template_path.read_bytes()
    result = {"api_sha": r.API_SHA, "site_assets_sha": r.SITE_SHA,
              "evidence_base_commit": "04058831200acbdf3a046271da03dbffca77453b",
              "scope": "local fixture mode selection and field mapping; not product settings or full v0.2 validation",
              "logical_quantity_cell": {"x": c.CELL_X, "width": c.CELL_WIDTH, "fontSize": c.SIZE},
              "template_sha256": hashlib.sha256(template).hexdigest(),
              "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "environment": "CPU1/RAM1536MiB/no extra swap/tmpfs256MiB/read-only/network none/shared2 queue0; Docker exec loopback HTTP",
              "timing": "fill-template + stamp-image twice to final PDF body; excludes fixture reads/mapping/save/PDF checks",
              "image_id": json.loads(r.smoke.docker("image", "inspect", "amane-pdf-api:site134-465088e").stdout)[0]["Id"],
              "cases": {}}
    with r.smoke.running_container("amane-pdf-api:site134-465088e", isolated=True) as api:
        for kind in ("invoice", "estimate"):
            for count in (1, 8):
                name = f"{kind}-{count}"
                case = output / name
                case.mkdir()
                # 既存の境界fixtureと表示値を再利用し、数量だけ改訂した文字列へ更新。
                base = r.ROOT / "quantity-decimal-comparison" / name
                company = json.loads((base / "company.json").read_text())
                request = json.loads((base / "request.json").read_text())
                values = json.loads((base / "display-values.json").read_text())
                for i, item in enumerate(request["items"]):
                    q = QUANTITIES[i]
                    assert re.fullmatch(r"(0|[1-9][0-9]{0,4})(\.[0-9]{1,3})?", q)
                    assert any(ch in "123456789" for ch in q)
                    item["quantity"] = q
                    values[f"row{i}.quantity"] = q
                expected = confirmed_values(request)
                assert all(values[k] == text for k, text in expected.items())
                definition = json.loads((r.ROOT / "standard-json-draft-v0.2/quantity-layout" / (kind + ".definition.json")).read_text())
                for f in definition["fields"]:
                    if f["key"].startswith("row") and f["key"].endswith(".quantity"):
                        assert f["rect"]["x"] == .38 and f["rect"]["width"] == .095
                        assert f["fontSize"] == 8 and f["align"] == "right" and f["maxLength"] == 9
                r.save(case / "company.json", company)
                r.save(case / "request.json", request)
                r.save(case / "display-values.json", values)
                r.save(case / "definition.json", definition)
                r.save(case / "confirmed-values.json", expected)
                input_sha = hashlib.sha256((case / "request.json").read_bytes()).hexdigest()
                measures, sent, extracted = {}, {}, {}
                for mode in ("decimal", "right"):
                    directory = case / mode
                    directory.mkdir()
                    r.save(directory / "mode.json", {"quantityAlignment": mode, "scope": "fixture only; product key undecided"})
                    fields, keys, placement = mode_fields(definition, values, mode)
                    r.save(directory / "fields.json", fields)
                    r.save(directory / "field-index.json", keys)
                    r.save(directory / "quantity-placement.json", placement)
                    pdfs, measure = r.chain(api, template, fields, pics)
                    api.assert_clean()
                    for stage, pdf in zip(("filled", "logo", "final"), pdfs):
                        path = directory / (stage + ".pdf")
                        path.write_bytes(pdf)
                        if stage == "final":
                            measure["extraction"] = r.check_pdf(path, fields, keys)
                        else:
                            r.command("pdftoppm", "-singlefile", "-r", "144", "-png", str(path), str(path.with_suffix("")))
                    actual = json.loads((directory / "final.extracted.json").read_text())
                    assert all(actual[k] == text for k, text in expected.items())
                    measure["images"] = r.image_check(directory, [(.05, .04, .18), (.82, .26, .1)])
                    positions = c.inspect_quantity(directory, fields, keys)
                    ends = [row["word_x_max_pt"] for row in positions["rows"]]
                    positions["right_edge_spread_pt"] = max(ends) - min(ends)
                    if mode == "decimal":
                        assert positions["anchor_spread_pt"] < .1
                        assert positions["dot_ink_center_spread_pt"] <= .5
                    else:
                        assert positions["right_edge_spread_pt"] < .05
                    measure.update(quantity_positions=positions, request_sha256=input_sha,
                                   fields=len(fields), rendered_lines=sum(f["text"].count("\n") + 1 for f in fields))
                    measures[mode], sent[mode], extracted[mode] = measure, fields, actual
                assert extracted["decimal"] == extracted["right"]
                assert (case / "decimal/final.txt").read_bytes() == (case / "right/final.txt").read_bytes()
                assert c.other_word_positions(case / "decimal", sent["decimal"], keys) == c.other_word_positions(case / "right", sent["right"], keys)
                for key, a, b in zip(keys, sent["decimal"], sent["right"]):
                    if key.startswith("row") and key.endswith(".quantity"):
                        assert {k: x for k, x in a.items() if k not in ("x", "width", "align")} == {k: x for k, x in b.items() if k not in ("x", "width", "align")}
                    else:
                        assert a == b
                assert hashlib.sha256((case / "request.json").read_bytes()).hexdigest() == input_sha
                r.command("qpdf", "--empty", "--pages", str(case / "decimal/final.pdf"), "1", str(case / "right/final.pdf"), "1", "--", str(case / "comparison.pdf"))
                r.command("qpdf", "--check", str(case / "comparison.pdf"))
                assert r.command("qpdf", "--show-npages", str(case / "comparison.pdf")).strip() == b"2"
                result["cases"][name] = {"modes": measures, "same_input": True,
                    "all_extracted_text_matches": True, "whole_page_raw_text_matches": True,
                    "non_quantity_word_positions_match": True, "input_confirmed_values_preserved": True}
                r.save(output / "results.json", result)
        result.update(post_count=api.post_count, health_status=api.request("GET", "/healthz")[0],
                      cleanup="assert_clean passed after each of eight chains")
    r.save(output / "results.json", result)
    print(json.dumps({name: {mode: {"seconds": round(m["total_seconds"], 3), "bytes": m["seal"]["bytes"]}
                            for mode, m in case["modes"].items()} for name, case in result["cases"].items()}))


if __name__ == "__main__":
    main()
