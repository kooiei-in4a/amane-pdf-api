using System.Buffers.Binary;
using System.Globalization;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class FromImagesDesignTests
{
    [TestMethod]
    [DataRow(2339, 8, 2339)]
    [DataRow(2340, 7, 2048)]
    [DataRow(4000, 4, 2000)]
    [DataRow(20000, 1, 2500)]
    public void StandardScale_UsesFloorAndRoundsDecodedDimensionsUp(int width, int scale, int decoded)
    {
        Assert.AreEqual(scale, JpegNormalizer.Scale(width));
        Assert.AreEqual(decoded, (width * scale + 7) / 8);
        Assert.AreEqual(17, (65 * JpegNormalizer.Scale(8064) + 7) / 8);
    }

    [TestMethod]
    public void Layout_UsesExactA4AndTenMillimeterMarginsRegardlessOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("fr-FR");
            foreach (var (w, h) in new[] { (100, 60), (60, 100), (100, 100), (20000, 1) })
            {
                var layout = ImagePageLayout.For(new(w, h, 3));
                Assert.AreEqual((w >= h ? 297 : 210) * 72 / 25.4, layout.Width, 0.000001);
                var full = Math.Min(layout.Width / w, layout.Height / h);
                var margin = Math.Min((layout.Width - w * full * layout.Scale) / 2,
                    (layout.Height - h * full * layout.Scale) / 2);
                Assert.AreEqual(10 * 72 / 25.4, margin, 0.000001);
                StringAssert.Contains(layout.Description, ".");
                Assert.AreEqual(2, layout.Description.Count(c => c == ','));
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    public void Capacity_ReservesRoundedSentinelAndReleasesOnlyAfterDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var capacity = new FromImagesCapacity(FromImagesCapacity.AuxiliaryBytes + 8192);
            var first = Path.Combine(root, "first");
            Assert.AreEqual(4096L, capacity.ReserveWrite(first, 4096, external: true));
            Assert.AreEqual(FromImagesCapacity.AuxiliaryBytes + 8192, capacity.Used);
            File.WriteAllBytes(first, [1]); capacity.Commit(first, 4096);
            Assert.AreEqual(FromImagesCapacity.AuxiliaryBytes + 4096, capacity.Used);
            Assert.AreEqual(4095L, capacity.ReserveWrite("second", 4096, external: true));
            Assert.Throws<PdfImageInputException>(() => capacity.ReserveWrite("third", 1, false));
            capacity.Delete(first);
            Assert.IsFalse(File.Exists(first));
            Assert.AreEqual(FromImagesCapacity.AuxiliaryBytes + 4096, capacity.Used);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Settings_Leave64MiBForLibrariesAndRejectOverflow()
    {
        Assert.IsTrue(FromImagesCapacity.IsValid(new()));
        Assert.IsTrue(FromImagesCapacity.ValidJpegMemory(320 * 1048576, 268));
        Assert.IsFalse(FromImagesCapacity.ValidJpegMemory(320 * 1048576, 269));
        Assert.IsFalse(FromImagesCapacity.IsValid(new() { ImageDjpegAddressSpaceLimitBytes = 64 * 1048576 }));
        Assert.IsFalse(FromImagesCapacity.IsValid(new() { ImageJpegtranMaxMemoryMegabytes = int.MaxValue }));
        Assert.IsFalse(FromImagesCapacity.IsValid(new() { ImageJobLimitBytes = long.MaxValue }));
        Assert.IsFalse(FromImagesCapacity.IsValid(new() { MaxImageFiles = 101 }));
        Assert.IsFalse(FromImagesCapacity.IsValid(new() { ImageJobLimitBytes = 50 * 1048576 }));
    }

    [TestMethod]
    public void Exif_HandlesByteOrdersDuplicatesAndHostileOffsets()
    {
        foreach (var little in new[] { false, true })
            for (var orientation = 1; orientation <= 8; orientation++)
                Assert.AreEqual(orientation, ExifOrientation.ReadPayload(FromImagesFixtures.Tiff(orientation, little)));
        var tiff = FromImagesFixtures.Tiff(6);
        Assert.AreEqual(1, ExifOrientation.ReadPayload(tiff[..^1]));
        var bad = tiff.ToArray(); bad[12] = 4;
        Assert.AreEqual(1, ExifOrientation.ReadPayload(bad));
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(4), uint.MaxValue);
        Assert.AreEqual(1, ExifOrientation.ReadPayload(bad));
        var duplicate = new byte[38]; tiff[..10].CopyTo(duplicate, 0); duplicate[8] = 2;
        tiff.AsSpan(10, 12).CopyTo(duplicate.AsSpan(10));
        tiff.AsSpan(10, 12).CopyTo(duplicate.AsSpan(22));
        Assert.AreEqual(1, ExifOrientation.ReadPayload(duplicate));
    }

    [TestMethod]
    public void Trim_UsesEightPixelsForGrayAndAllowsSmallTranspose()
    {
        Assert.AreEqual((56, 100), JpegNormalizer.TransformedSize(new(100, 60, 1), 6));
        Assert.AreEqual((48, 100), JpegNormalizer.TransformedSize(new(100, 60, 3, 16, 16), 6));
        Assert.AreEqual((3, 2), JpegNormalizer.TransformedSize(new(2, 3, 3, 16, 16), 5));
        Assert.Throws<PdfImageInputException>(() => JpegNormalizer.TransformedSize(new(2, 3, 3, 16, 16), 6));
    }

    [TestMethod]
    public void ToolResult_ClassifiesObservableSentinelBeforeExitButNeverInfersDeletedOutput()
    {
        var path = Path.GetTempFileName();
        try
        {
            foreach (var length in new[] { 9, 10 })
            {
                File.WriteAllBytes(path, new byte[length]);
                JpegNormalizer.CheckResult(new(0, null), path, 10, CancellationToken.None);
            }
            File.WriteAllBytes(path, new byte[11]);
            foreach (var exit in new[] { 0, 1 })
                Assert.AreEqual(PdfImageReason.Capacity, Assert.Throws<PdfImageInputException>(() =>
                    JpegNormalizer.CheckResult(new(exit, null), path, 10, CancellationToken.None)).Reason);
            Assert.Throws<InvalidOperationException>(() => JpegNormalizer.CheckResult(new(139, null), path, 10, CancellationToken.None));
            File.Delete(path);
            Assert.AreEqual(PdfImageReason.Unsupported, Assert.Throws<PdfImageInputException>(() =>
                JpegNormalizer.CheckResult(new(1, null), path, 10, CancellationToken.None)).Reason);
            Assert.Throws<InvalidOperationException>(() => JpegNormalizer.CheckResult(new(0, null), path, 10, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void MetadataInspection_RejectsAppMarkersInLaterScansAndTrailingBytes()
    {
        var path = Path.GetTempFileName();
        try
        {
            byte[] scan = [255, 216, 255, 218, 0, 2, 40, 255, 0, 10, 255, 217];
            File.WriteAllBytes(path, scan); JpegNormalizer.NormalizeMarkers(path, CancellationToken.None);
            File.WriteAllBytes(path, [.. scan[..^2], .. FromImagesFixtures.Segment(0xe1, "private-image-marker"u8.ToArray()), .. scan[^2..]]);
            Assert.Throws<InvalidOperationException>(() => JpegNormalizer.NormalizeMarkers(path, CancellationToken.None));
            File.WriteAllBytes(path, [.. scan, .. "private-image-marker"u8]);
            Assert.Throws<InvalidOperationException>(() => JpegNormalizer.NormalizeMarkers(path, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }
}
