namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PngSanitizerTests
{
    [TestMethod]
    [DataRow(8, 6, false, 16, 24)]
    [DataRow(16, 0, false, 16, 24)]
    [DataRow(8, 3, false, 16, 24)]
    [DataRow(8, 6, true, 1, 1)]
    public async Task Sanitizer_PreservesPixelChunksAndDropsMetadataAndTrailingBytes(int depth, int color,
        bool interlaced, int w, int h)
    {
        await WithFiles(async (input, output) =>
        {
            await File.WriteAllBytesAsync(input, FromImagesFixtures.Png(w, h, depth, color, interlaced));
            var info = await PngSanitizer.WriteAsync(input, output, 1024 * 1024, 50_000_000, 320 * 1048576,
                CancellationToken.None);
            Assert.AreEqual(w, info.Width); Assert.AreEqual(h, info.Height); Assert.AreEqual(depth, info.BitDepth);
            CollectionAssert.AreEqual(FromImagesFixtures.Png(w, h, depth, color, interlaced, metadata: false),
                await File.ReadAllBytesAsync(output));
            if (OperatingSystem.IsLinux())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output));
        });
    }

    [TestMethod]
    public async Task Sanitizer_RejectsMalformedStructureAndCrcEvenOnDiscardedChunks()
    {
        var valid = FromImagesFixtures.Png(metadata: false);
        var corrupt = valid.ToArray(); corrupt[29] ^= 1;
        var badAncillary = FromImagesFixtures.Chunk("tEXt", [1]); badAncillary[^1] ^= 1;
        byte[][] cases =
        [
            valid[..^1], corrupt,
            [.. valid[..33], .. FromImagesFixtures.Chunk("IHDR", valid[16..29]), .. valid[33..]],
            [.. valid[..33], .. FromImagesFixtures.Chunk("acTL", new byte[8]), .. valid[33..]],
            [.. valid[..33], .. FromImagesFixtures.Chunk("ABCD", []), .. valid[33..]],
            [.. valid[..33], .. FromImagesFixtures.Chunk("tExt", []), .. valid[33..]],
            [.. valid[..33], .. badAncillary, .. valid[33..]],
            [.. valid[..^12], .. FromImagesFixtures.Chunk("tEXt", []), .. FromImagesFixtures.Chunk("IDAT", []), .. valid[^12..]],
            [.. valid[..33], .. FromImagesFixtures.Chunk("tRNS", [1]), .. valid[33..]]
        ];
        foreach (var bytes in cases)
            await WithFiles(async (input, output) =>
            {
                await File.WriteAllBytesAsync(input, bytes);
                var exception = await Assert.ThrowsAsync<PdfImageInputException>(() => PngSanitizer.WriteAsync(input,
                    output, 1048576, 50_000_000, 320 * 1048576, CancellationToken.None));
                Assert.AreEqual(PdfImageReason.Unsupported, exception.Reason);
            });
    }

    [TestMethod]
    public async Task Sanitizer_EnforcesPixelsWorkingEstimateWriteBudgetAndCancellation()
    {
        await WithFiles(async (input, output) =>
        {
            await File.WriteAllBytesAsync(input, FromImagesFixtures.Png());
            foreach (var (budget, pixels, working, expected) in new[]
            {
                (1L, 50_000_000L, 320 * 1048576L, PdfImageReason.Capacity),
                (1048576L, 16 * 24 - 1L, 320 * 1048576L, PdfImageReason.Unsupported),
                (1048576L, 50_000_000L, 64 * 1048576L, PdfImageReason.Unsupported)
            })
            {
                File.Delete(output);
                var exception = await Assert.ThrowsAsync<PdfImageInputException>(() => PngSanitizer.WriteAsync(input,
                    output, budget, pixels, working, CancellationToken.None));
                Assert.AreEqual(expected, exception.Reason);
                Assert.IsTrue(new FileInfo(output).Length <= budget);
            }
            File.Delete(output); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => PngSanitizer.WriteAsync(input, output,
                1048576, 50_000_000, 320 * 1048576, cancel.Token));
        });
    }

    private static async Task WithFiles(Func<string, string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "amane-png-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(Path.Combine(root, "input"), Path.Combine(root, "output")); }
        finally { Directory.Delete(root, true); }
    }
}
