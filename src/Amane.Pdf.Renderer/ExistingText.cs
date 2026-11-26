using System.Globalization;
using System.Numerics;
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
    private const double MinimumScanCoverage = 0.5;

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
            if (Pdfium.FPDF_GetPageBoundingBox(page, out var bounds) == 0) return false;
            var width = (double)bounds.Right - bounds.Left;
            var height = (double)bounds.Top - bounds.Bottom;
            if (!(width > 0 && height > 0 && double.IsFinite(width * height))) return false;
            var remaining = MaxObjects;
            var hasScanImage = MayContainScanImage(page, Matrix3x2.Identity, bounds, width * height * MinimumScanCoverage,
                depth: 0, ref remaining);
            var letters = 0;
            for (var i = 0; i < count; i++)
            {
                // Page numbers alone don't make a page searchable. On a scan, visible form values can
                // be searchable while the printed body still needs OCR.
                if (!Rune.TryCreate((int)Pdfium.FPDFText_GetUnicode(text, i), out var rune) || !Rune.IsLetter(rune))
                    continue;
                if (hasScanImage)
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
                if (hasScanImage && (y < BodyMargin || y >= DeviceSize - BodyMargin)) continue;
                if (++letters >= MinimumLetters) return true;
            }
            return false;
        }
        finally { Pdfium.FPDFText_ClosePage(text); }
    }

    private static bool MayContainScanImage(IntPtr container, Matrix3x2 toPage, Pdfium.PageBounds bounds,
        double minimumArea, int depth, ref int remaining)
    {
        const int ImageObject = 3;
        const int FormObject = 5;
        // On an incomplete inspection, require stronger evidence before skipping.
        if (depth > 32) return true;
        var form = depth > 0;
        var count = form ? Pdfium.FPDFFormObj_CountObjects(container) : Pdfium.FPDFPage_CountObjects(container);
        if (count < 0 || count > remaining) return true;
        remaining -= count;
        for (var i = 0; i < count; i++)
        {
            var item = form ? Pdfium.FPDFFormObj_GetObject(container, (nuint)i) : Pdfium.FPDFPage_GetObject(container, i);
            if (item == IntPtr.Zero) return true;
            var type = Pdfium.FPDFPageObj_GetType(item);
            if (type is not (ImageObject or FormObject)) continue;
            // PDFium reports each object's matrix relative to its containing page or form.
            if (Pdfium.FPDFPageObj_GetMatrix(item, out var local) == 0) return true;
            var transform = local * toPage;
            if (type == ImageObject
                ? CoversPage(transform, bounds, minimumArea)
                : MayContainScanImage(item, transform, bounds, minimumArea, depth + 1, ref remaining))
                return true;
        }
        return false;
    }

    private static bool CoversPage(Matrix3x2 transform, Pdfium.PageBounds bounds, double minimumArea)
    {
        // An image occupies a unit square before transformation. Its area can only shrink when cropped.
        var area = Math.Abs((double)transform.M11 * transform.M22 - (double)transform.M12 * transform.M21);
        if (!(area >= minimumArea)) return false;
        Span<Vector2> polygon = stackalloc Vector2[8];
        polygon[0] = Vector2.Transform(Vector2.Zero, transform);
        polygon[1] = Vector2.Transform(Vector2.UnitX, transform);
        polygon[2] = Vector2.Transform(Vector2.One, transform);
        polygon[3] = Vector2.Transform(Vector2.UnitY, transform);

        // A quadrilateral clipped to the displayed page rectangle has at most eight corners.
        Span<Vector2> scratch = stackalloc Vector2[8];
        var count = Clip(polygon[..4], scratch, Vector2.UnitX, bounds.Left);
        count = Clip(scratch[..count], polygon, -Vector2.UnitX, -bounds.Right);
        count = Clip(polygon[..count], scratch, Vector2.UnitY, bounds.Bottom);
        count = Clip(scratch[..count], polygon, -Vector2.UnitY, -bounds.Top);
        if (count < 3) return false;
        double twiceArea = 0;
        for (var i = 1; i + 1 < count; i++)
        {
            var a = polygon[i] - polygon[0];
            var b = polygon[i + 1] - polygon[0];
            twiceArea += (double)a.X * b.Y - (double)a.Y * b.X;
        }
        return Math.Abs(twiceArea) >= 2 * minimumArea;
    }

    private static int Clip(ReadOnlySpan<Vector2> polygon, Span<Vector2> output, Vector2 normal, float boundary)
    {
        if (polygon.IsEmpty) return 0;
        var count = 0;
        var previous = polygon[^1];
        var previousDistance = Vector2.Dot(previous, normal) - boundary;
        foreach (var point in polygon)
        {
            var distance = Vector2.Dot(point, normal) - boundary;
            if (distance < 0 && previousDistance > 0 || distance > 0 && previousDistance < 0)
                output[count++] = Vector2.Lerp(previous, point, previousDistance / (previousDistance - distance));
            if (distance >= 0) output[count++] = point;
            previous = point;
            previousDistance = distance;
        }
        return count;
    }
}
