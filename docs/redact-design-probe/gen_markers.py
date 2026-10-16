"""Spike: marker PDF (qpdf JSON v2). Every MK_* string must be absent from the output."""
import base64
import json
import sys

w = sys.argv[1]
b = lambda s: base64.b64encode(s.encode()).decode()
objs = {
    "obj:1 0 R": {"value": {
        "/Type": "/Catalog", "/Pages": "2 0 R",
        "/Names": {"/EmbeddedFiles": {"/Names": ["u:doc.txt", "30 0 R"]}, "/Dests": {"/Names": ["u:MK_DEST", [ "3 0 R", "/Fit"]]}},
        "/Outlines": "40 0 R",
        "/StructTreeRoot": "50 0 R", "/MarkInfo": {"/Marked": True},
        "/AcroForm": {"/Fields": ["20 0 R"], "/NeedAppearances": False},
        "/OpenAction": {"/S": "/JavaScript", "/JS": "u:app.alert('MK_JS')"},
        "/Metadata": "60 0 R",
        "/PageLabels": {"/Nums": [0, {"/S": "/D", "/P": "u:MK_PAGELABEL-"}]},
        "/AF": ["31 0 R"]}},
    "obj:2 0 R": {"value": {"/Type": "/Pages", "/Count": 3, "/Kids": ["3 0 R", "5 0 R", "7 0 R"],
                            "/Resources": {"/Font": {"/F1": "10 0 R", "/F2": "11 0 R"}}}},
    "obj:10 0 R": {"value": {"/Type": "/Font", "/Subtype": "/Type1", "/BaseFont": "/Helvetica"}},
    "obj:11 0 R": {"value": {"/Type": "/Font", "/Subtype": "/Type1", "/BaseFont": "/Courier"}},
    # Form XObject drawn on page 1, referenced (unused) from page 2 and from a nested form on page 3.
    "obj:12 0 R": {"stream": {"dict": {"/Type": "/XObject", "/Subtype": "/Form", "/BBox": [0, 0, 300, 50],
                                       "/Resources": {"/Font": {"/F1": "10 0 R"}}},
                              "data": b("BT /F1 12 Tf 0 10 Td (MK_FORMX) Tj ET")}},
    "obj:13 0 R": {"stream": {"dict": {"/Type": "/XObject", "/Subtype": "/Form", "/BBox": [0, 0, 300, 50],
                                       "/Resources": {"/Font": {"/F2": "11 0 R"}, "/XObject": {"/Xs": "12 0 R"}}},
                              "data": b("BT /F2 12 Tf 0 10 Td (PUBLIC_NESTED) Tj ET")}},
    # page 1 (target)
    "obj:3 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842],
                            "/Resources": {"/Font": {"/F1": "10 0 R"}, "/XObject": {"/X1": "12 0 R"},
                                           "/Properties": {"/MC0": {"/ActualText": "u:MK_PROPS"}}},
                            "/Contents": "4 0 R", "/Annots": ["20 0 R", "21 0 R", "22 0 R"],
                            "/StructParents": 0, "/PieceInfo": {"/MK_PIECE": {"/Private": "u:MK_PIECEINFO"}},
                            "/AF": ["32 0 R"]}},
    "obj:4 0 R": {"stream": {"dict": {}, "data": b(
        "/Span << /ActualText (MK_INLINE_ACTUALTEXT) /MCID 0 >> BDC BT /F1 18 Tf 50 750 Td (MK_BODY1) Tj ET EMC "
        "/P /MC0 BDC EMC q 1 0 0 1 50 600 cm /X1 Do Q")}},
    # Widget with value + appearance
    "obj:20 0 R": {"value": {"/Type": "/Annot", "/Subtype": "/Widget", "/FT": "/Tx", "/T": "u:MK_FIELDNAME",
                             "/V": "u:MK_FIELDVALUE", "/Rect": [50, 500, 300, 530], "/P": "3 0 R",
                             "/AP": {"/N": "23 0 R"}, "/F": 4}},
    "obj:23 0 R": {"stream": {"dict": {"/Type": "/XObject", "/Subtype": "/Form", "/BBox": [0, 0, 250, 30],
                                       "/Resources": {"/Font": {"/F1": "10 0 R"}}},
                              "data": b("/Tx BMC BT /F1 12 Tf 2 10 Td (MK_FIELDAP) Tj ET EMC")}},
    # FileAttachment annotation
    "obj:21 0 R": {"value": {"/Type": "/Annot", "/Subtype": "/FileAttachment", "/Rect": [400, 500, 420, 520],
                             "/FS": {"/Type": "/Filespec", "/F": "u:a.txt", "/EF": {"/F": "33 0 R"}},
                             "/Contents": "u:MK_ANNOT_CONTENTS"}},
    "obj:33 0 R": {"stream": {"dict": {"/Type": "/EmbeddedFile"}, "data": b("MK_ANNOT_ATTACHMENT")}},
    # Link without appearance on page 1
    "obj:22 0 R": {"value": {"/Type": "/Annot", "/Subtype": "/Link", "/Rect": [50, 400, 200, 420],
                             "/A": {"/S": "/URI", "/URI": "u:https://example.invalid/MK_URI1"}}},
    # page 2 (non-target): unused ref to X1, /AF, link without AP
    "obj:5 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842],
                            "/Resources": {"/Font": {"/F2": "11 0 R"}, "/XObject": {"/X1": "12 0 R"}},
                            "/Contents": "6 0 R", "/Annots": ["24 0 R"], "/AF": ["34 0 R"]}},
    "obj:6 0 R": {"stream": {"dict": {}, "data": b("BT /F2 18 Tf 50 750 Td (PUBLIC_P2) Tj ET")}},
    "obj:24 0 R": {"value": {"/Type": "/Annot", "/Subtype": "/Link", "/Rect": [50, 400, 200, 420],
                             "/A": {"/S": "/URI", "/URI": "u:https://example.invalid/MK_URI2"}}},
    "obj:34 0 R": {"value": {"/Type": "/Filespec", "/F": "u:af2.txt", "/EF": {"/F": "35 0 R"}}},
    "obj:35 0 R": {"stream": {"dict": {"/Type": "/EmbeddedFile"}, "data": b("MK_AF_PAGE2")}},
    # page 3 (non-target): inherits fonts from /Pages, draws nested form 13 (which references 12 unused)
    "obj:7 0 R": {"value": {"/Type": "/Page", "/Parent": "2 0 R", "/MediaBox": [0, 0, 595, 842],
                            "/Resources": {"/XObject": {"/X3": "13 0 R"}, "/Font": {"/F2": "11 0 R"}},
                            "/Contents": "8 0 R"}},
    "obj:8 0 R": {"stream": {"dict": {}, "data": b("q 1 0 0 1 50 700 cm /X3 Do Q")}},
    # document attachments
    "obj:30 0 R": {"value": {"/Type": "/Filespec", "/F": "u:doc.txt", "/EF": {"/F": "36 0 R"}}},
    "obj:36 0 R": {"stream": {"dict": {"/Type": "/EmbeddedFile"}, "data": b("MK_DOC_ATTACHMENT")}},
    "obj:31 0 R": {"value": {"/Type": "/Filespec", "/F": "u:root-af.txt", "/EF": {"/F": "37 0 R"}}},
    "obj:37 0 R": {"stream": {"dict": {"/Type": "/EmbeddedFile"}, "data": b("MK_ROOT_AF")}},
    "obj:32 0 R": {"value": {"/Type": "/Filespec", "/F": "u:p1-af.txt", "/EF": {"/F": "38 0 R"}}},
    "obj:38 0 R": {"stream": {"dict": {"/Type": "/EmbeddedFile"}, "data": b("MK_AF_PAGE1")}},
    # outlines
    "obj:40 0 R": {"value": {"/Type": "/Outlines", "/First": "41 0 R", "/Last": "41 0 R", "/Count": 1}},
    "obj:41 0 R": {"value": {"/Title": "u:MK_OUTLINE", "/Parent": "40 0 R", "/Dest": ["3 0 R", "/Fit"]}},
    # structure
    "obj:50 0 R": {"value": {"/Type": "/StructTreeRoot", "/K": "51 0 R", "/ParentTree": {"/Nums": [0, ["51 0 R"]]}}},
    "obj:51 0 R": {"value": {"/Type": "/StructElem", "/S": "/P", "/P": "50 0 R", "/Pg": "3 0 R", "/K": 0,
                             "/ActualText": "u:MK_ACTUALTEXT", "/Alt": "u:MK_ALT"}},
    "obj:60 0 R": {"stream": {"dict": {"/Type": "/Metadata", "/Subtype": "/XML"}, "data": b("<x:xmpmeta>MK_XMP</x:xmpmeta>")}},
    "obj:61 0 R": {"value": {"/Title": "u:MK_INFO_TITLE", "/Author": "u:MK_INFO_AUTHOR"}},
    "trailer": {"value": {"/Root": "1 0 R", "/Info": "61 0 R", "/Size": 62}},
}
json.dump({"qpdf": [{"jsonversion": 2, "pdfversion": "1.7"}, objs]}, open(f"{w}/markers.json", "w"))
