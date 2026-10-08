namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class JpegHeaderTests
{
    [TestMethod]
    [DataRow(0xc0)]
    [DataRow(0xc1)]
    [DataRow(0xc2)]
    public void SupportedFrames_ParseThroughScansAndStuffedBytes(int marker)
        => Assert.AreEqual(new JpegHeader(7014, 7014, 8, 3), Parse(Jpeg(marker)));

    [TestMethod]
    public void MalformedAndUnsupportedMarkers_AreRejectedAfterFirstSof()
    {
        var good = Jpeg(0xc0);
        Assert.IsNull(Parse([.. good[..^2], 0xff, 0xdc, 0, 4, 0, 1, 0xff, 0xd9]));
        Assert.IsNull(Parse([.. good[..^2], .. good[2..]]));
        Assert.IsNull(Parse(Jpeg(0xc3)));
        Assert.IsNull(Parse(good[..^1]));
        Assert.IsNull(Parse([.. good[..2], 0xff, 0xe0, 0, 1, .. good[2..]]));
        Assert.IsNull(Parse(good[..21])); // SOF without a scan/EOI.
        Assert.IsNull(Parse([.. good[..21], 0xff, 0xd9])); // EOI without SOS.
        var zero = good.ToArray(); zero[7] = zero[8] = 0; Assert.IsNull(Parse(zero));
        var precision = good.ToArray(); precision[6] = 12; Assert.IsNull(Parse(precision));
        Assert.IsNull(Parse(good, limit: good.Length - 1));
        Assert.IsNull(Parse(good, pixels: 100));
    }

    [TestMethod]
    public void FirstEoi_EndsPrimaryJpeg_WithoutParsingTrailingGainMapData()
    {
        var good = Jpeg(0xc0);
        Assert.AreEqual(Parse(good), Parse([.. good, 0xff, 0xdc, 0, 1, .. Jpeg(0xc3), 0xff]));
        Assert.IsNull(Parse([.. good[..^2], 0xff, 0xdc, 0, 4, 0, 1, .. good]));
    }

    [TestMethod]
    public void FinalizationCapacity_IncludesEntryAndUpdateCoexistence_AndRounding()
    {
        const long mib = 1024 * 1024;
        Assert.IsFalse(new CompressCapacity(13 * mib).CanFinalize(mib, mib, 6 * mib, 6 * mib, 4 * mib));
        Assert.IsTrue(new CompressCapacity(14 * mib).CanFinalize(mib, mib, 6 * mib, 6 * mib, 4 * mib));
        Assert.IsFalse(new CompressCapacity(14 * mib).CanFinalize(mib, mib, 6 * mib, 6 * mib + 1, 4 * mib));
        var capacity = new CompressCapacity(14 * mib);
        capacity.Reserve("input", mib); capacity.Reserve("jpeg", mib); capacity.Reserve("entries", 6 * mib);
        capacity.Reserve("update", 6 * mib); // Joining can reserve the full update before deleting entries.
        capacity.Release("entries"); capacity.Reserve("output", 4 * mib);
        Assert.IsTrue(capacity.Peak <= 14 * mib);
    }

    [TestMethod]
    public void CeilScaleAndPnmWorstCase_UseCheckedIntegerBounds()
    {
        Assert.AreEqual(3, PdfCompressProcessor.Scale(7014, 1754));
        Assert.AreEqual(2, PdfCompressProcessor.Scale(7014, 1169));
        Assert.AreEqual(2, PdfCompressProcessor.Scale(8064, 1754));
        Assert.AreEqual(2, PdfCompressProcessor.Scale(8064, 1169));
        Assert.AreEqual(8, PdfCompressProcessor.Scale(1200, 1754));
        Assert.AreEqual(1, PdfCompressProcessor.Scale(65535, 1169));
        Assert.AreEqual(20_766_500, PdfCompressProcessor.PnmSize(7014, 7014, 3, 3));
        Assert.IsTrue(CompressCapacity.Allocated(PdfCompressProcessor.PnmSize(7014, 7014, 3, 3) + 512) < 24 * 1024 * 1024);
        var ledger = new CompressCapacity(124 * 1024 * 1024);
        ledger.Reserve("input", 50 * 1024 * 1024);
        var slot = CompressCapacity.Allocated(71_170 + 4096);
        ledger.Reserve("raw", 50 * slot);
        ledger.Reserve("pnm", PdfCompressProcessor.PnmSize(7014, 7014, 3, 3) + 512);
        Assert.AreEqual(3_891_200, 50 * slot);
        Assert.ThrowsExactly<CompressLimitException>(() => ledger.Reserve("overflow", 124 * 1024 * 1024));
        Assert.IsTrue(ledger.Peak <= 124 * 1024 * 1024);
        ledger.Release("raw"); ledger.Release("pnm");
        Assert.AreEqual(50 * 1024 * 1024, ledger.Used);
    }

    private static byte[] Jpeg(int marker)
        => [0xff, 0xd8, 0xff, (byte)marker, 0, 17, 8, 0x1b, 0x66, 0x1b, 0x66, 3,
            1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0,
            0xff, 0xda, 0, 12, 3, 1, 0, 2, 0, 3, 0, 0, 63, 0,
            1, 2, 0xff, 0, 3, 0xff, 0xd0, 4, 0xff, 0xd9];
    private static JpegHeader? Parse(byte[] bytes, long? limit = null, long pixels = 100_000_000)
    {
        var path = Path.Combine(Path.GetTempPath(), "jpeg-parser-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllBytes(path, bytes); return JpegHeader.Read(path, limit ?? bytes.Length, pixels, CancellationToken.None); }
        finally { File.Delete(path); }
    }
}
