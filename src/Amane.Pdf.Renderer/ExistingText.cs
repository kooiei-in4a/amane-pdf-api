using System.Globalization;
using System.Text;

namespace Amane.Pdf.Renderer;

internal static class ExistingText
{
    private const int DeviceSize = 1000; // Use PDFium's display coordinate system without allocating a bitmap.
    private const int BodyMargin = 80;
    private const int MinimumLetters = 4;
    private const int InvisibleTextMode = 3;
    private const int MaxCharacters = 20_000;
    private const int MaxObjects = 100_000;

    internal static int Inspect(string input, int[] selectedPages)
    {
        var document = Pdfium.FPDF_LoadDocument(input, IntPtr.Zero);
        if (document == IntPtr.Zero) return 3;
        try
        {
            var count = Pdfium.FPDF_GetPageCount(document);
            var searchable = new List<string>(selectedPages.Length);
            foreach (var number in selectedPages)
            {
                if (number > count) return 4;
                var page = Pdfium.FPDF_LoadPage(document, number - 1);
                if (page == IntPtr.Zero) return 4;
                try
                {
                    if (IsSearchable(page)) searchable.Add(number.ToString(CultureInfo.InvariantCulture));
                }
                finally { Pdfium.FPDF_ClosePage(page); }
            }
            // Only page numbers cross the worker boundary.
            Console.WriteLine(string.Join(',', searchable));
            return 0;
        }
        finally { Pdfium.FPDF_CloseDocument(document); }
    }

    private static bool IsSearchable(IntPtr page)
    {
        var text = Pdfium.FPDFText_LoadPage(page);
        if (text == IntPtr.Zero) return false;
        try
        {
            var count = Math.Min(Pdfium.FPDFText_CountChars(text), MaxCharacters);
            if (count <= 0) return false;
            var remaining = MaxObjects;
            var hasImage = MayContainImage(page, form: false, depth: 0, ref remaining);
            var letters = 0;
            for (var i = 0; i < count; i++)
            {
                // Page numbers alone don't make a page searchable. On a scan, visible form values can
                // be searchable while the printed body still needs OCR.
                if (!Rune.TryCreate((int)Pdfium.FPDFText_GetUnicode(text, i), out var rune) || !Rune.IsLetter(rune))
                    continue;
                if (hasImage)
                {
                    var item = Pdfium.FPDFText_GetTextObject(text, i);
                    if (item == IntPtr.Zero || Pdfium.FPDFTextObj_GetTextRenderMode(item) != InvisibleTextMode)
                        continue;
                }
                if (Pdfium.FPDFText_GetCharBox(text, i, out var left, out var right, out var bottom, out var top) == 0 ||
                    !(left < right && bottom < top))
                    continue;
                var centerX = left / 2 + right / 2;
                var centerY = bottom / 2 + top / 2;
                if (!double.IsFinite(centerX) || !double.IsFinite(centerY) ||
                    Pdfium.FPDF_PageToDevice(page, 0, 0, DeviceSize, DeviceSize, 0, centerX, centerY, out var x, out var y) == 0 ||
                    x is < 0 or >= DeviceSize || y is < 0 or >= DeviceSize)
                    continue;
                if (hasImage && (y < BodyMargin || y >= DeviceSize - BodyMargin)) continue;
                if (++letters >= MinimumLetters) return true;
            }
            return false;
        }
        finally { Pdfium.FPDFText_ClosePage(text); }
    }

    private static bool MayContainImage(IntPtr container, bool form, int depth, ref int remaining)
    {
        const int ImageObject = 3;
        const int FormObject = 5;
        if (depth > 32) return true;
        var count = form ? Pdfium.FPDFFormObj_CountObjects(container) : Pdfium.FPDFPage_CountObjects(container);
        if (count < 0 || count > remaining) return true;
        remaining -= count;
        for (var i = 0; i < count; i++)
        {
            var item = form ? Pdfium.FPDFFormObj_GetObject(container, (nuint)i) : Pdfium.FPDFPage_GetObject(container, i);
            if (item == IntPtr.Zero) return true;
            var type = Pdfium.FPDFPageObj_GetType(item);
            if (type == ImageObject || type == FormObject && MayContainImage(item, form: true, depth + 1, ref remaining))
                return true;
        }
        return false;
    }
}
