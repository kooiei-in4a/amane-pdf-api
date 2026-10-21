#!/usr/bin/env python3
"""#134: 長い名称の表示枠だけを検証。標準JSON validator・製品実装ではない。"""
import argparse
import copy
import json
from pathlib import Path

import reproduce as r


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    output = parser.parse_args().output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    pics = r.images(output)
    template = (r.ROOT / "line-comparison/refined-template.pdf").read_bytes()
    examples = {
        "single": {
            "company.nameLines": ["架空帳票研究株式会社"],
            "company.addressLines": ["東京都架空区見本一丁目", "試験ビル二階"],
            "recipient.nameLines": ["架空取引先株式会社 御中"],
        },
        "example": {
            "company.nameLines": ["株式会社架空帳票研究開発センター", "東日本事業統括本部"],
            "company.addressLines": ["東京都架空区見本町一丁目二番地", "架空帳票研究開発試験センター第一棟"],
            "recipient.nameLines": ["架空日本事業支援企画開発株式会社", "営業企画部 御中"],
        },
        "boundary": {
            "company.nameLines": ["証" * 20, "明" * 20],
            "company.addressLines": ["所" * 20, "在" * 20],
            "recipient.nameLines": ["先" * 20, "宛" * 20],
        },
    }
    result = {"api_sha": r.API_SHA, "site_assets_sha": r.SITE_SHA,
              "scope": "field-level layout probe, not v0.2 standard JSON validation",
              "base": "standard-json-validation/{invoice,estimate}-8; only 3 header fields widened and allowed 2 lines",
              "environment": "existing isolated Docker: CPU1, RAM1536MiB, tmpfs256MiB; docker exec loopback",
              "image_id": json.loads(r.smoke.docker("image", "inspect", "amane-pdf-api:site134-465088e").stdout)[0]["Id"],
              "timing": "fill-template + stamp-image twice; excludes fixture reads and PDF checks",
              "cases": {}}
    with r.smoke.running_container("amane-pdf-api:site134-465088e", isolated=True) as api:
        for kind in ("invoice", "estimate"):
            base = r.ROOT / "standard-json-validation" / (kind + "-8")
            for suffix, header in examples.items():
                name = kind + "-" + suffix
                directory = output / name
                directory.mkdir()
                definition = json.loads((base / "definition.json").read_text())
                values = json.loads((base / "display-values.json").read_text())
                for key, source in (("issuer", "company.nameLines"), ("address", "company.addressLines"), ("recipient", "recipient.nameLines")):
                    assert 1 <= len(header[source]) <= 2
                    assert all(0 < len(line) <= 20 for line in header[source])
                    values[key] = "\n".join(header[source])
                    field = next(f for f in definition["fields"] if f["key"] == key)
                    field.update(type="multiline", maxLines=2, maxLength=41)
                    field["rect"]["width"] = .34
                fields, keys = r.fields_for(definition, values)
                r.save(directory / "header-lines.json", header)
                r.save(directory / "definition.json", definition)
                r.save(directory / "display-values.json", values)
                r.save(directory / "fields.json", fields)
                r.save(directory / "field-index.json", keys)
                pdfs, measure = r.chain(api, template, fields, pics)
                api.assert_clean()
                for stage, pdf in zip(("filled", "logo", "final"), pdfs):
                    path = directory / (stage + ".pdf")
                    path.write_bytes(pdf)
                    if stage == "final":
                        measure["extraction"] = r.check_pdf(path, fields, keys)
                    else:
                        r.command("pdftoppm", "-singlefile", "-r", "144", "-png", str(path), str(path.with_suffix("")))
                measure["images"] = r.image_check(directory, [(.05, .04, .18), (.82, .26, .1)])
                measure["rendered_lines"] = sum(f["text"].count("\n") + 1 for f in fields)
                original = json.loads((base / "final.extracted.json").read_text())
                actual = json.loads((directory / "final.extracted.json").read_text())
                assert all(actual[k] == v for k, v in original.items() if k not in ("issuer", "address", "recipient"))
                measure["all_other_fields_unchanged"] = True
                result["cases"][name] = measure
                r.save(output / "results.json", result)
        # 同じ20文字×2行を元の宛先矩形へ送ると、APIは枠超過として明示拒否する。
        fields = copy.deepcopy(fields)
        fields[keys.index("recipient")].update(width=.29, height=.026)
        status, _, measure = r.post(api, template, fields=fields)
        assert status == 422 and measure["problem"].get("reason") == "field-overflow"
        r.save(output / "original-recipient.fields.json", fields)
        r.save(output / "original-recipient.problem.json", measure["problem"])
        result["original_recipient"] = measure
        api.assert_clean()
        result["post_count"] = api.post_count
        result["health_status"] = api.request("GET", "/healthz")[0]
        result["cleanup"] = "assert_clean passed after each chain and error"
    r.save(output / "results.json", result)
    print(json.dumps({"pdf_cases": len(result["cases"]), "post_count": result["post_count"]}))


if __name__ == "__main__":
    main()
