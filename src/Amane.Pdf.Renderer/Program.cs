using System.Globalization;
using System.Runtime.Versioning;

namespace Amane.Pdf.Renderer;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Never print exceptions, native errors, paths or PDF content.
        try
        {
            if (!OperatingSystem.IsLinux()) return 2;
            var options = Arguments.Parse(args);
            if (options is null) return 2;
            try
            {
                if (!options.SelfTest && !NativeLimits.SetFileSize(options.MaxBytes + 1)) return 1;
                using var pdfium = new Pdfium(options.Pdfium, options.FontDirectory);
                return options.SelfTest ? SelfTest() : Render(options);
            }
            catch (Exception) { return options.SelfTest ? 20 : 1; }
        }
        catch (Exception) { return 2; }
    }

    [SupportedOSPlatform("linux")]
    private static int Render(Arguments options)
    {
        var document = Pdfium.FPDF_LoadDocument(options.Input, IntPtr.Zero);
        if (document == IntPtr.Zero)
        {
            _ = Pdfium.FPDF_GetLastError(); // Deliberately discard native error details.
            return 3;
        }
        try
        {
            if (options.Page > Pdfium.FPDF_GetPageCount(document)) return 4;
            var page = Pdfium.FPDF_LoadPage(document, options.Page - 1);
            if (page == IntPtr.Zero) return 4;
            try
            {
                var width = Pdfium.FPDF_GetPageWidthF(page);
                var height = Pdfium.FPDF_GetPageHeightF(page);
                if (!float.IsFinite(width) || !float.IsFinite(height) ||
                    // Include the exact 0.01pt boundary despite binary subtraction rounding.
                    Math.Abs(width - options.ExpectedWidth) > 0.010000001 ||
                    Math.Abs(height - options.ExpectedHeight) > 0.010000001) return 12;
                var bitmap = Draw(page, options.Width, options.Height);
                if (bitmap == IntPtr.Zero) return 5;
                try
                {
                    foreach (var fill in options.Fills)
                        if (Pdfium.FPDFBitmap_FillRect(bitmap, fill.Left, fill.Top,
                            fill.Right - fill.Left, fill.Bottom - fill.Top, 0xFF000000) == 0) return 1;
                    using var jpeg = new TurboJpeg();
                    if (!jpeg.Compress(bitmap, options.Width, options.Height)) return 6;
                    if ((ulong)jpeg.Size > (ulong)options.MaxBytes) return 10;
                    // No output is created until rendering, filling and the size check finish.
                    using var output = new FileStream(options.Output, new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    });
                    output.Write(jpeg.Bytes);
                    return 0;
                }
                finally { Pdfium.FPDFBitmap_Destroy(bitmap); }
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
        finally { Pdfium.FPDF_CloseDocument(document); }
    }

    private static IntPtr Draw(IntPtr page, int width, int height)
    {
        var bitmap = Pdfium.FPDFBitmap_Create(width, height, 0);
        if (bitmap == IntPtr.Zero) return IntPtr.Zero;
        if (Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF) == 0)
        {
            Pdfium.FPDFBitmap_Destroy(bitmap);
            return IntPtr.Zero;
        }
        Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0);
        return bitmap;
    }

    private static unsafe int SelfTest()
    {
        var pdf = SelfTestPdf.Bytes;
        fixed (byte* bytes = pdf)
        {
            var document = Pdfium.FPDF_LoadMemDocument((IntPtr)bytes, pdf.Length, IntPtr.Zero);
            if (document == IntPtr.Zero) return 20;
            try
            {
                var page = Pdfium.FPDF_LoadPage(document, 0);
                if (page == IntPtr.Zero) return 20;
                try
                {
                    var bitmap = Draw(page, 600, 240);
                    if (bitmap == IntPtr.Zero) return 20;
                    try
                    {
                        var buffer = (byte*)Pdfium.FPDFBitmap_GetBuffer(bitmap);
                        var stride = Pdfium.FPDFBitmap_GetStride(bitmap);
                        if (buffer == null || stride < 600 * 4) return 20;
                        var dark = 0;
                        for (var y = 40; y < 180; y++)
                            for (var x = 30; x < 340; x++)
                            {
                                var pixel = buffer + y * stride + x * 4;
                                if (pixel[0] < 128 && pixel[1] < 128 && pixel[2] < 128) dark++;
                            }
                        if (dark < 1000) return 20;
                        if (Pdfium.FPDFBitmap_FillRect(bitmap, 400, 20, 100, 60, 0xFF000000) == 0) return 20;
                        for (var y = 20; y < 80; y++)
                            for (var x = 400; x < 500; x++)
                            {
                                var pixel = buffer + y * stride + x * 4;
                                if (pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0) return 20;
                            }
                        using var jpeg = new TurboJpeg();
                        if (!jpeg.Compress(bitmap, 600, 240) || jpeg.Size < 4) return 20;
                        var encoded = jpeg.Bytes;
                        return encoded[0] == 0xFF && encoded[1] == 0xD8 &&
                            encoded[^2] == 0xFF && encoded[^1] == 0xD9 ? 0 : 20;
                    }
                    finally { Pdfium.FPDFBitmap_Destroy(bitmap); }
                }
                finally { Pdfium.FPDF_ClosePage(page); }
            }
            finally { Pdfium.FPDF_CloseDocument(document); }
        }
    }
}

internal readonly record struct Fill(int Left, int Top, int Right, int Bottom);

internal sealed record Arguments(bool SelfTest, string Pdfium, string FontDirectory, string Input,
    string Output, int Page, double ExpectedWidth, double ExpectedHeight, int Width, int Height,
    long MaxBytes, IReadOnlyList<Fill> Fills)
{
    internal static Arguments? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("self-test" or "render-jpeg") || args.Length % 2 != 1) return null;
        var selfTest = args[0] == "self-test";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var fills = new List<string>();
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!selfTest && args[i] == "--fill") { fills.Add(args[i + 1]); continue; }
            if (args[i] is not ("--pdfium" or "--font-dir") && (selfTest ||
                args[i] is not ("--input" or "--output" or "--page" or "--expect-size" or "--width" or "--height" or "--quality" or "--max-bytes"))) return null;
            if (!values.TryAdd(args[i], args[i + 1])) return null;
        }
        string Value(string key) => values.GetValueOrDefault(key, "");
        bool Absolute(string key) => Path.IsPathFullyQualified(Value(key)) && !Value(key).Contains('\0');
        if (!Absolute("--pdfium") || !Absolute("--font-dir") || values.Count != (selfTest ? 2 : 10)) return null;
        if (selfTest) return new(true, Value("--pdfium"), Value("--font-dir"), "", "", 0, 0, 0, 0, 0, 0, []);
        if (!Absolute("--input") || !Absolute("--output") ||
            File.Exists(Value("--output")) || Directory.Exists(Value("--output")) ||
            !PositiveInt(Value("--page"), out var page) || !PositiveInt(Value("--width"), out var width) ||
            !PositiveInt(Value("--height"), out var height) || (long)width * height > 16_000_000 ||
            Value("--quality") != "85" || fills.Count > 100 ||
            !long.TryParse(Value("--max-bytes"), NumberStyles.None, CultureInfo.InvariantCulture, out var maxBytes) ||
            maxBytes is <= 0 or long.MaxValue) return null;
        var expected = Value("--expect-size").Split(',');
        if (expected.Length != 2 || !PositiveDouble(expected[0], out var expectedWidth) ||
            !PositiveDouble(expected[1], out var expectedHeight)) return null;
        var rectangles = new List<Fill>();
        foreach (var fill in fills)
        {
            var parts = fill.Split(',');
            if (parts.Length != 4 || !Coordinate(parts[0], out var left) || !Coordinate(parts[1], out var top) ||
                !Coordinate(parts[2], out var right) || !Coordinate(parts[3], out var bottom) ||
                left >= right || top >= bottom || right > width || bottom > height) return null;
            rectangles.Add(new(left, top, right, bottom));
        }
        return new(false, Value("--pdfium"), Value("--font-dir"), Value("--input"), Value("--output"),
            page, expectedWidth, expectedHeight, width, height, maxBytes, rectangles);
    }

    private static bool Coordinate(string text, out int number)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
    private static bool PositiveInt(string text, out int number) => Coordinate(text, out number) && number > 0;
    private static bool PositiveDouble(string text, out double number)
        => double.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out number) &&
            double.IsFinite(number) && number > 0 && number <= float.MaxValue;
}
