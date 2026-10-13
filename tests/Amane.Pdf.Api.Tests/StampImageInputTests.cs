namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class StampImageInputTests
{
    [TestMethod]
    public async Task Png_ValidatesAllDepthsAndAdam7WithSmallAndOddDimensions()
    {
        foreach (var color in new[] { 0, 2, 3, 4, 6 })
        foreach (var depth in color switch { 0 => new[] { 1, 2, 4, 8, 16 }, 3 => [1, 2, 4, 8], _ => [8, 16] })
        foreach (var interlaced in new[] { false, true })
        foreach (var (width, height) in new[] { (1, 1), (17, 23) })
        {
            using var files = new TemporaryPdfFiles(Path.GetTempPath());
            await File.WriteAllBytesAsync(files.StampUploadPath, StampFixtures.Png(width, height, depth, color, interlaced));
            var image = await StampImageInput.PrepareAsync(files, new(), default);
            Assert.AreEqual(width, image.Width); Assert.AreEqual(height, image.Height);
            Assert.IsFalse(File.Exists(files.StampUploadPath));
            var output = System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(image.Path));
            Assert.IsFalse(output.Contains("PRIVATE_STAMP"));
        }
    }

    [TestMethod]
    public async Task Png_RejectsCorruptCompressedDataDespiteValidChunkCrc()
    {
        foreach (var fault in new[] { "zlib", "adler", "short", "long", "filter", "truncated", "dictionary" })
        {
            using var files = new TemporaryPdfFiles(Path.GetTempPath());
            await File.WriteAllBytesAsync(files.StampUploadPath, StampFixtures.Png(fault: fault));
            var error = await Assert.ThrowsExactlyAsync<PdfStampImageException>(() => StampImageInput.PrepareAsync(files, new(), default), fault);
            Assert.AreEqual(PdfStampImageReason.Unsupported, error.Reason);
        }
    }

    [TestMethod]
    public async Task Png_RejectsEachSideEvenWhenPixelProductFits()
    {
        foreach (var (w, h) in new[] { (2001, 1), (1, 2001), (4000, 1000) })
        {
            using var files = new TemporaryPdfFiles(Path.GetTempPath());
            await File.WriteAllBytesAsync(files.StampUploadPath, StampFixtures.Png(w, h));
            await Assert.ThrowsExactlyAsync<PdfStampImageException>(() => StampImageInput.PrepareAsync(files, new(), default));
        }
    }

    [TestMethod]
    public void Settings_RejectOverflowAndInsufficientTemporaryCapacity()
    {
        Assert.IsTrue(StampImageInput.IsValid(new()));
        foreach (var size in new[] { 0L, -1, StampImageInput.MaxBytes + 1 }) Assert.IsFalse(StampImageInput.IsValid(new() { MaxStampImageBytes = size }));
        Assert.IsFalse(StampImageInput.IsValid(new() { MaxFileBytes = long.MaxValue - 65536 }));
        Assert.IsFalse(StampImageInput.IsValid(new() { OverlayJobLimitBytes = 51 * 1048576 }));
        Assert.AreEqual(53542912, new PdfOptions().MaxStampRequestBytes);
    }
}
