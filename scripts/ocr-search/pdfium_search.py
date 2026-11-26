"""Search with the PDFium bundled in the API and return first-match boxes in display pixels."""

from contextlib import ExitStack
import ctypes as c
import os


def find(library_path, pdf_path, terms):
    library = c.CDLL(os.fspath(library_path))
    handle, integer, double = c.c_void_p, c.c_int, c.c_double
    pointer = c.POINTER(double)

    def bind(name, result, *arguments):
        function = getattr(library, name)
        function.restype = result
        function.argtypes = arguments
        return function

    with ExitStack() as cleanup:
        bind("FPDF_InitLibrary", None)()
        cleanup.callback(bind("FPDF_DestroyLibrary", None))
        document = bind("FPDF_LoadDocument", handle, c.c_char_p, c.c_char_p)(os.fsencode(pdf_path), None)
        assert document, "PDFium could not open the OCR output."
        cleanup.callback(bind("FPDF_CloseDocument", None, handle), document)
        page = bind("FPDF_LoadPage", handle, handle, integer)(document, 0)
        assert page, "PDFium could not open the OCR page."
        cleanup.callback(bind("FPDF_ClosePage", None, handle), page)
        height = bind("FPDF_GetPageHeightF", c.c_float, handle)(page)
        text = bind("FPDFText_LoadPage", handle, handle)(page)
        assert text, "PDFium could not load the OCR text."
        cleanup.callback(bind("FPDFText_ClosePage", None, handle), text)
        start = bind("FPDFText_FindStart", handle, handle, c.c_char_p, c.c_ulong, integer)
        advance = bind("FPDFText_FindNext", integer, handle)
        close = bind("FPDFText_FindClose", None, handle)
        first_index = bind("FPDFText_GetSchResultIndex", integer, handle)
        character_count = bind("FPDFText_GetSchCount", integer, handle)
        count_rectangles = bind("FPDFText_CountRects", integer, handle, integer, integer)
        rectangle = bind("FPDFText_GetRect", integer, handle, integer, pointer, pointer, pointer, pointer)

        result = {}
        dpi_scale = 300 / 72
        for term in terms:
            search = start(text, term.encode("utf-16-le") + b"\0\0", 0, 0)
            assert search, "PDFium could not start the search."
            try:
                if not advance(search):
                    continue
                boxes = []
                for i in range(count_rectangles(text, first_index(search), character_count(search))):
                    corners = [double(), double(), double(), double()]
                    assert rectangle(text, i, *(c.byref(corner) for corner in corners))
                    left, top, right, bottom = (corner.value for corner in corners)
                    boxes.append((left * dpi_scale, (height - top) * dpi_scale,
                                  right * dpi_scale, (height - bottom) * dpi_scale))
                if boxes:
                    result[term] = [min(box[0] for box in boxes), min(box[1] for box in boxes),
                                    max(box[2] for box in boxes), max(box[3] for box in boxes)]
            finally:
                close(search)
        return result
