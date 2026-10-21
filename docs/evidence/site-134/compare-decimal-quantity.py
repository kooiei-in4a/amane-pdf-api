#!/usr/bin/env python3
"""#134: 数量の小数点揃えだけを検証用コピーで比較する。製品実装ではない。"""
import argparse
import copy
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import xml.etree.ElementTree as ET

import numpy as np
from PIL import Image
import reproduce as r

spec = importlib.util.spec_from_file_location("standard_json_fixture", r.ROOT / "validate-standard-json.py")
v = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v)

QUANTITIES = ["99999", "0.001", "2.50", "1.000", "3", "11111", "9.999", "0.010"]
CELL_X, CELL_WIDTH, SIZE = .38, .095, 8
RIGHT_PT = (CELL_X + CELL_WIDTH) * 595
MAX_DIGIT_PT = max(v.WIDTHS[c] for c in "0123456789") * SIZE / 1000
DOT_PT = v.WIDTHS["."] * SIZE / 1000
# API座標の小数4桁への丸め（最大0.02975pt）でも右端を超えない余白。
ROUNDING_GUARD_PT = .06
ANCHOR_PT = RIGHT_PT - .05 * SIZE - ROUNDING_GUARD_PT - DOT_PT - 3 * MAX_DIGIT_PT


def advance(text):
    return sum(v.WIDTHS[c] for c in text) * SIZE / 1000


def decimal_fields(definition, values):
    revised = copy.deepcopy(definition)
    for f in revised["fields"]:
        if f["key"] == "heading.name" or f["key"].endswith(".name"):
            f["rect"]["width"] = .32
        if f["key"] == "heading.quantity" or f["key"].endswith(".quantity"):
            f["rect"].update(x=CELL_X, width=CELL_WIDTH)
    fields, keys = r.fields_for(revised, values)
    placement = []
    for key, f in zip(keys, fields):
        if not key.startswith("row") or not key.endswith(".quantity"):
            continue
        prefix = f["text"].partition(".")[0]
        # 文字列全体を1 fieldで保持する。空白埋め・0補完・分割・数値変換はしない。
        x = round((ANCHOR_PT - advance(prefix) - .05 * SIZE) / 595, 4)
        f.update(x=x, width=round(CELL_X + CELL_WIDTH - x, 4), align="left")
        assert f["x"] >= CELL_X
        assert advance(f["text"]) <= f["width"] * 595 - .1 * SIZE
        placement.append({"key": key, "quantity": f["text"], "anchor_pt": ANCHOR_PT,
                          "prefix_advance_pt": advance(prefix), "field": f})
    return revised, fields, keys, placement


def inspect_quantity(directory, fields, keys):
    words = ET.fromstring((directory / "final.bbox.html").read_bytes()).findall(".//{http://www.w3.org/1999/xhtml}word")
    pixels = np.array(Image.open(directory / "final.png").convert("RGB"))
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
        assert abs((right - left) - advance(f["text"])) < .05
        anchor = left + advance(f["text"].partition(".")[0])
        anchors.append(anchor)
        metric = {"key": key, "quantity": f["text"], "word_x_min_pt": left,
                  "word_x_max_pt": right, "decimal_or_integer_end_pt": anchor}
        if "." in f["text"]:
            # PDF実描画のドット画素を測る。予測領域内に隣の数字や罫線は入らない。
            x0, x1 = math.floor(anchor * 2), math.ceil((anchor + DOT_PT) * 2)
            y0, y1 = math.floor(f["y"] * 842 * 2), math.ceil((f["y"] + f["height"]) * 842 * 2)
            ys, xs = np.nonzero(np.all(pixels[y0:y1, x0:x1] < 100, axis=2))
            assert len(xs) > 0 and max(ys) - min(ys) <= 5
            center = float(np.mean(xs + x0) / 2)
            dots.append(center)
            metric["dot_ink_center_pt"] = center
        # 品名・単価の実wordと非交差、定義セルにも入ることを確認。
        neighbors = {}
        for suffix in ("name", "price"):
            neighbor_key = f"row{row}.{suffix}"
            neighbor = fields[keys.index(neighbor_key)]
            matches = [w for w in words if w.text == neighbor["text"] and same_row(w, neighbor)]
            assert len(matches) == 1
            neighbors[suffix] = [float(matches[0].attrib[k]) for k in ("xMin", "xMax")]
        assert neighbors["name"][1] < left and right < neighbors["price"][0]
        metric["gap_to_name_pt"] = left - neighbors["name"][1]
        metric["gap_to_price_pt"] = neighbors["price"][0] - right
        metrics.append(metric)
    return {"rows": metrics, "anchor_spread_pt": max(anchors) - min(anchors),
            "dot_ink_center_spread_pt": max(dots) - min(dots) if dots else None}


def other_word_positions(directory, fields, keys):
    quantities = [f for k, f in zip(keys, fields) if k.startswith("row") and k.endswith(".quantity")]
    words = ET.fromstring((directory / "final.bbox.html").read_bytes()).findall(".//{http://www.w3.org/1999/xhtml}word")
    # Popplerのword列挙順は列配置で変わるため、文字と実矩形の集合を比較する。
    return sorted((w.text, tuple(sorted(w.attrib.items()))) for w in words if not any(
        w.text == f["text"] and float(w.attrib["yMin"]) >= f["y"] * 842 - .1 and
        float(w.attrib["yMax"]) <= (f["y"] + f["height"]) * 842 + .1 for f in quantities))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    output = parser.parse_args().output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    pics = r.images(output)
    template = (r.ROOT / "line-comparison/refined-template.pdf").read_bytes()
    company = json.loads((r.ROOT / "standard-json-draft/examples/company-registration.json").read_text())
    result = {"api_sha": r.API_SHA, "site_assets_sha": r.SITE_SHA,
              "scope": "fixture-only comparison, no product implementation",
              "baseline": "adopted ruling background + existing right-aligned quantity geometry",
              "quantities": QUANTITIES, "logical_quantity_cell": {"x": CELL_X, "width": CELL_WIDTH},
              "decimal_anchor_pt": ANCHOR_PT, "rounding_guard_pt": ROUNDING_GUARD_PT,
              "required_width_pt": 8 * MAX_DIGIT_PT + DOT_PT + .1 * SIZE + ROUNDING_GUARD_PT,
              "glyph_source_sha256": hashlib.sha256((r.ROOT / "api-source/src/Amane.Pdf.Api/FillTemplateGlyphs.g.cs").read_bytes()).hexdigest(),
              "environment": "CPU1/RAM1536MiB/tmpfs256MiB/read-only/network none; existing Docker exec loopback HTTP",
              "timing": "fill-template + stamp-image twice; excludes JSON reads/mapping and PDF checks",
              "image_id": json.loads(r.smoke.docker("image", "inspect", "amane-pdf-api:site134-465088e").stdout)[0]["Id"],
              "cases": {}, "extended_cases": {}}
    with r.smoke.running_container("amane-pdf-api:site134-465088e", isolated=True) as api:
        for kind in ("invoice", "estimate"):
            for count in (1, 8):
                name = f"{kind}-{count}"
                case = output / name
                case.mkdir()
                request = json.loads((r.ROOT / "standard-json-draft/examples" / (name + ".json")).read_text())
                for i, item in enumerate(request["items"]):
                    item.update(quantity=QUANTITIES[i], name="証" * 17 if i % 2 == 0 else "明" * 17, unitPrice=99999999)
                definition = json.loads((r.ROOT / "standard-json-validation" / name / "definition.json").read_text())
                _, values, current_fields, keys, sources, definition = v.map_fields(company, request, definition)
                revised, fields, new_keys, placement = decimal_fields(definition, values)
                assert new_keys == keys
                r.save(case / "company.json", company)
                r.save(case / "request.json", request)
                r.save(case / "display-values.json", values)
                r.save(case / "field-sources.json", sources)
                r.save(case / "quantity-placement.json", placement)
                measures = {}
                for mode, d, fs in (("current", definition, current_fields), ("proposed", revised, fields)):
                    directory = case / mode
                    directory.mkdir()
                    r.save(directory / "definition.json", d)
                    r.save(directory / "fields.json", fs)
                    r.save(directory / "field-index.json", keys)
                    pdfs, measure = r.chain(api, template, fs, pics)
                    api.assert_clean()
                    for stage, pdf in zip(("filled", "logo", "final"), pdfs):
                        path = directory / (stage + ".pdf")
                        path.write_bytes(pdf)
                        if stage == "final":
                            measure["extraction"] = r.check_pdf(path, fs, keys)
                        else:
                            r.command("pdftoppm", "-singlefile", "-r", "144", "-png", str(path), str(path.with_suffix("")))
                    measure["images"] = r.image_check(directory, [(.05, .04, .18), (.82, .26, .1)])
                    measure["quantity_positions"] = inspect_quantity(directory, fs, keys)
                    measure["fields"] = len(fs)
                    measure["rendered_lines"] = sum(f["text"].count("\n") + 1 for f in fs)
                    measures[mode] = measure
                old = json.loads((case / "current/final.extracted.json").read_text())
                new = json.loads((case / "proposed/final.extracted.json").read_text())
                assert old == new
                assert (case / "current/final.txt").read_bytes() == (case / "proposed/final.txt").read_bytes()
                assert other_word_positions(case / "current", current_fields, keys) == other_word_positions(case / "proposed", fields, keys)
                for i, item in enumerate(request["items"]):
                    assert new[f"row{i}.quantity"] == item["quantity"]
                    assert new[f"row{i}.amount"] == f'{item["amount"]:,}円'
                    assert new[f"row{i}.price"] == f'{item["unitPrice"]:,}円'
                assert new["total"] == f'{request["totalAmount"]:,}円'
                proposed_metrics = measures["proposed"]["quantity_positions"]
                assert proposed_metrics["anchor_spread_pt"] < .1
                if count == 8:
                    assert proposed_metrics["dot_ink_center_spread_pt"] <= .5
                r.command("qpdf", "--empty", "--pages", str(case / "current/final.pdf"), "1", str(case / "proposed/final.pdf"), "1", "--", str(case / "comparison.pdf"))
                r.command("qpdf", "--check", str(case / "comparison.pdf"))
                assert r.command("qpdf", "--show-npages", str(case / "comparison.pdf")).strip() == b"2"
                result["cases"][name] = measures | {"all_text_and_confirmed_amounts_match": True,
                                                     "non_quantity_word_positions_unchanged": True,
                                                     "whole_page_raw_text_unchanged": True}
                r.save(output / "results.json", result)
        for kind in ("invoice", "estimate"):
            # 5桁整数部＋3桁小数部は現行JSON案の5文字上限外。描画可否だけを別に検証。
            base = output / (kind + "-1")
            case = output / "extended" / kind
            case.mkdir(parents=True)
            request = json.loads((base / "request.json").read_text())
            request["items"][0]["quantity"] = "99999.999"
            definition = json.loads((base / "current/definition.json").read_text())
            r.save(case / "request.json", request)
            try:
                v.map_fields(company, request, definition)
            except v.Invalid as error:
                assert error.problem["errors"][0]["code"] == "quantity-format"
                r.save(case / "preflight-error.json", error.problem)
            else:
                raise AssertionError("v0.1 quantity limit was not enforced")
            keys = json.loads((base / "current/field-index.json").read_text())
            original = json.loads((base / "current/fields.json").read_text())
            original[keys.index("row0.quantity")]["text"] = "99999.999"
            status, _, overflow = r.post(api, template, fields=original)
            assert status == 422 and overflow["problem"].get("reason") == "field-overflow"
            api.assert_clean()
            r.save(case / "original.fields.json", original)
            r.save(case / "original.problem.json", overflow["problem"])
            values = json.loads((base / "display-values.json").read_text())
            values["row0.quantity"] = "99999.999"
            next(f for f in definition["fields"] if f["key"] == "row0.quantity")["maxLength"] = 9
            revised, fields, keys, placement = decimal_fields(definition, values)
            r.save(case / "display-values.json", values)
            r.save(case / "quantity-placement.json", placement)
            r.save(case / "definition.json", revised)
            r.save(case / "fields.json", fields)
            r.save(case / "field-index.json", keys)
            pdfs, measure = r.chain(api, template, fields, pics)
            api.assert_clean()
            for stage, pdf in zip(("filled", "logo", "final"), pdfs):
                path = case / (stage + ".pdf")
                path.write_bytes(pdf)
                if stage == "final":
                    measure["extraction"] = r.check_pdf(path, fields, keys)
                else:
                    r.command("pdftoppm", "-singlefile", "-r", "144", "-png", str(path), str(path.with_suffix("")))
            actual = json.loads((case / "final.extracted.json").read_text())
            expected = json.loads((base / "current/final.extracted.json").read_text())
            expected["row0.quantity"] = "99999.999"
            assert actual == expected
            measure["images"] = r.image_check(case, [(.05, .04, .18), (.82, .26, .1)])
            measure["quantity_positions"] = inspect_quantity(case, fields, keys)
            result["extended_cases"][kind] = measure | {"original_api": overflow,
                "standard_json_v01_accepted": False, "all_other_fields_match_baseline": True}
            r.save(output / "results.json", result)
        result.update(post_count=api.post_count, health_status=api.request("GET", "/healthz")[0],
                      cleanup="assert_clean passed after all ten chains and two explicit API overflows")
    r.save(output / "results.json", result)
    print(json.dumps({k: {"seconds": round(m["proposed"]["total_seconds"], 3),
                          "bytes": m["proposed"]["seal"]["bytes"],
                          "alignment": m["proposed"]["quantity_positions"]["anchor_spread_pt"]} for k, m in result["cases"].items()}))


if __name__ == "__main__":
    main()
