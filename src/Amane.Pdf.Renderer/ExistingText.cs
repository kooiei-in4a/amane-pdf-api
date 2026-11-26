using System.Globalization;
using System.Text;

namespace Amane.Pdf.Renderer;

internal static class ExistingText
{
    private const int DeviceSize = 1000; // Use PDFium's display coordinate system without allocating a bitmap.
    private const int BodyMargin = 80;
    private const int MinimumBodyCharacters = 24;
    private const int MinimumBodySpan = 20;
    private const int MinimumInvisibleLetters = 4;
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
            var characters = 0;
            var invisibleLetters = 0;
            var minX = DeviceSize;
            var minY = DeviceSize;
            var maxX = 0;
            var maxY = 0;
            for (var i = 0; i < count; i++)
            {
                if (!Rune.TryCreate((int)Pdfium.FPDFText_GetUnicode(text, i), out var rune) || !Rune.IsLetterOrDigit(rune) ||
                    Pdfium.FPDFText_GetCharBox(text, i, out var left, out var right, out var bottom, out var top) == 0 ||
                    !(left < right && bottom < top))
                    continue;
                var centerX = left / 2 + right / 2;
                var centerY = bottom / 2 + top / 2;
                if (!double.IsFinite(centerX) || !double.IsFinite(centerY) ||
                    Pdfium.FPDF_PageToDevice(page, 0, 0, DeviceSize, DeviceSize, 0, centerX, centerY, out var x, out var y) == 0 ||
                    x is < 0 or >= DeviceSize || y is < 0 or >= DeviceSize)
                    continue;
                if (!hasImage) return true;
                // An image page may carry a digital title or footer. Require text within the page body
                // spanning both axes before keeping its existing text layer.
                if (y is < BodyMargin or >= DeviceSize - BodyMargin) continue;
                // Sparse scans can already have an OCR layer. Count letters so an invisible page number alone cannot qualify.
                if (Rune.IsLetter(rune))
                {
                    var item = Pdfium.FPDFText_GetTextObject(text, i);
                    if (item != IntPtr.Zero && Pdfium.FPDFTextObj_GetTextRenderMode(item) == InvisibleTextMode &&
                        ++invisibleLetters >= MinimumInvisibleLetters)
                        return true;
                }
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                if (++characters >= MinimumBodyCharacters && maxX - minX >= MinimumBodySpan && maxY - minY >= MinimumBodySpan)
                    return true;
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
