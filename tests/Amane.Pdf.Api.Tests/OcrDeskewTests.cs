using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class OcrDeskewTests
{
    private const int Width = 800;
    private const int Height = 1000;

    [TestMethod]
    [DataRow(1.0)]
    [DataRow(2.0)]
    [DataRow(5.0)]
    [DataRow(7.0)]
    [DataRow(-2.0)]
    [DataRow(-5.0)]
    public void EstimateFindsClockwiseAndCounterclockwiseSkew(double skew)
        => Assert.AreEqual(skew, OcrDeskew.Estimate(Page(skew), Width, Height, default), 0.2);

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(0.25)]
    [DataRow(0.5)]
    [DataRow(13.0)]
    public void EstimateSkipsAnglesOutsideItsRange(double skew)
        => Assert.AreEqual(0, OcrDeskew.Estimate(Page(skew), Width, Height, default));

    [TestMethod]
    public void EstimateSkipsBlankSparseAndDarkImages()
    {
        var gray = new byte[Width * Height];
        Assert.AreEqual(0, OcrDeskew.Estimate(gray, Width, Height, default));
        Array.Fill(gray, (byte)255);
        Assert.AreEqual(0, OcrDeskew.Estimate(gray, Width, Height, default));
        gray[Width * 300 + 70] = 0;
        Assert.AreEqual(0, OcrDeskew.Estimate(gray, Width, Height, default));
    }

    [TestMethod]
    public void EstimateSkipsComparablePeaksFromOppositeSlopes()
    {
        var gray = new byte[Width * Height];
        Array.Fill(gray, (byte)255);
        var slope = Math.Tan(5 * Math.PI / 180);
        for (var x = 100; x < Width - 100; x++)
        {
            var shift = (int)Math.Round(slope * (x - Width / 2));
            foreach (var row in new[] { 240 + shift, 340 + shift, 640 - shift, 740 - shift })
            {
                gray[row * Width + x] = 0;
                gray[(row + 1) * Width + x] = 0;
            }
        }
        Assert.AreEqual(0, OcrDeskew.Estimate(gray, Width, Height, default));
    }

    [TestMethod]
    [DataRow(5.0, 0)]
    [DataRow(-5.0, Width - 1)]
    public void EstimateSkipsInkThatWouldLeaveThePage(double skew, int x)
    {
        var gray = Page(skew);
        for (var y = 0; y < 12; y++)
            gray[y * Width + x] = 200;
        Assert.AreEqual(0, OcrDeskew.Estimate(gray, Width, Height, default));
    }

    [TestMethod]
    public void CorrectionWritesAReadableRgbPngWithTheOriginalDimensions()
    {
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var job = Job(files);
        var source = job.PathFor("source.png");
        var corrected = job.PathFor("corrected.png");
        var gray = Page(5);
        WritePng(source, gray);

        Assert.AreEqual(5, OcrDeskew.TryCorrect(job, source, corrected, Width, Height, 1_000_000, default), 0.2);

        var rgb = OcrPageImage.ReadRgb(corrected, Width, Height, default);
        var correctedGray = OcrPageImage.ReadGrayscale(corrected, Width, Height, default);
        Assert.AreEqual(Width * Height * 3, rgb.Length);
        CollectionAssert.AreEqual(new byte[] { 255, 255, 255 }, rgb[..3]);
        var center = (Height / 2 * Width + Width / 2) * 3;
        CollectionAssert.AreEqual(new byte[] { 40, 100, 190 }, rgb[center..(center + 3)]);
        Assert.AreEqual(0, OcrDeskew.Estimate(correctedGray, Width, Height, default));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OptionalCorrectionFailureKeepsTheOriginalAndReleasesItsOutput(bool invalidPng)
    {
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var job = Job(files);
        var source = job.PathFor("source.png");
        var corrected = job.PathFor("corrected.png");
        if (invalidPng) File.WriteAllBytes(source, [137, 80, 78]);
        else WritePng(source, Page(5));
        var original = File.ReadAllBytes(source);

        Assert.AreEqual(0, OcrDeskew.TryCorrect(job, source, corrected, Width, Height, 50, default));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
        Assert.IsFalse(File.Exists(corrected));
        Assert.AreEqual(4096, job.Reserve(corrected, 4096));
    }

    [TestMethod]
    public void EstimatePropagatesCancellation()
    {
        var gray = Page(5);
        Assert.ThrowsExactly<OperationCanceledException>(() => OcrDeskew.Estimate(gray, Width, Height, new(true)));
    }

    private static PdfPageJob Job(TemporaryPdfFiles files)
    {
        var settings = new PdfOptions();
        return new(files, new(Options.Create(settings)), settings, 2_000_000);
    }

    private static void WritePng(string path, byte[] gray)
    {
        using var png = File.Create(path);
        OcrPageImage.WritePng(png, Width, Height, rgb: true, (y, row) =>
        {
            for (var x = 0; x < Width; x++)
                row.Slice(3 * x, 3).Fill(gray[y * Width + x]);
            if (y is >= Height / 2 - 2 and <= Height / 2 + 2)
            {
                for (var x = Width / 2 - 2; x <= Width / 2 + 2; x++)
                {
                    row[3 * x] = 40;
                    row[3 * x + 1] = 100;
                    row[3 * x + 2] = 190;
                }
            }
        }, default);
    }

    private static byte[] Page(double skew)
    {
        var gray = new byte[Width * Height];
        Array.Fill(gray, (byte)255);
        var slope = Math.Tan(skew * Math.PI / 180);
        for (var x = 110; x < Width - 110; x++)
        {
            var shift = (int)Math.Round(slope * (x - Width / 2));
            foreach (var top in new[] { 220, 480, 715 })
            {
                for (var y = top + shift; y < top + shift + 3; y++)
                    gray[y * Width + x] = 0;
            }
            if (x % 21 < 15)
            {
                for (var top = 290; top < 460; top += 50)
                    for (var y = top + shift; y < top + shift + 10; y++)
                        gray[y * Width + x] = 0;
            }
        }
        return gray;
    }
}
