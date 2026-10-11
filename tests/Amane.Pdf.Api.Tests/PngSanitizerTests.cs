using System.Text.Json;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PngSanitizerTests
{
    public static IEnumerable<object[]> WorkingVectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "png-working-vectors.json")));
        foreach (var row in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [row.GetProperty("name").GetString()!, row.GetProperty("width").GetInt32(),
                row.GetProperty("height").GetInt32(), row.GetProperty("depth").GetInt32(),
                row.GetProperty("color").GetInt32(), row.GetProperty("transparency").GetBoolean(),
                row.GetProperty("interlace").GetInt32() == 1, row.GetProperty("encoded_bytes").GetInt64(), row.GetProperty("working_bytes").GetInt64(),
                row.GetProperty("accepted").GetBoolean()];
    }

    [TestMethod]
    [DynamicData(nameof(WorkingVectors))]
    public void WorkingEstimate_MatchesSharedVectorsAndDefaultAdmission(string name, int width, int height,
        int depth, int color, bool transparency, bool interlace, long encoded, long expected,
        bool accepted)
    {
        var estimate = PngSanitizer.WorkingBytes(width, height, depth, color, transparency, interlace, encoded);
        Assert.AreEqual(expected, estimate, name);
        var options = new PdfOptions();
        Assert.AreEqual(320 * 1048576L, options.ImagePngWorkingLimitBytes);
        Assert.AreEqual(accepted, estimate <= options.ImagePngWorkingLimitBytes, name);
    }

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

    [TestMethod]
    public async Task Gray16_TransparencyRequiresTheExpandedDecoderBudget()
    {
        var gray = FromImagesFixtures.Png(64, 48, 16, 0, metadata: false);
        byte[] transparent = [.. gray[..33], .. FromImagesFixtures.Chunk("tRNS", [96, 0]), .. gray[33..]];
        await WithFiles(async (input, output) =>
        {
            const long working = 64 * 1048576 + 64 * 48 * 30;
            await File.WriteAllBytesAsync(input, gray);
            await PngSanitizer.WriteAsync(input, output, 1048576, 50_000_000, working, CancellationToken.None);
            foreach (var source in new[] { transparent, FromImagesFixtures.Png(64, 48, 16, 4) })
            {
                File.Delete(output);
                await File.WriteAllBytesAsync(input, source);
                var error = await Assert.ThrowsAsync<PdfImageInputException>(() => PngSanitizer.WriteAsync(
                    input, output, 1048576, 50_000_000, working, CancellationToken.None));
                Assert.AreEqual(PdfImageReason.Unsupported, error.Reason);
            }
        });
    }

    [TestMethod]
    public async Task Adam7_ReservesAnotherDecodedImage()
    {
        var plain = FromImagesFixtures.Png(64, 48, 8, 6, metadata: false);
        // Only the estimate is checked here; the sanitizer does not decode pixel data.
        byte[] interlaced = [.. plain[..8], .. FromImagesFixtures.Chunk("IHDR", [.. plain[16..28], 1]), .. plain[33..]];
        const long pixels = 64 * 48;
        var working = 64 * 1048576 + Math.Max(2L * plain.Length + 14 * pixels, 22 * pixels) + 2 * pixels;
        await WithFiles(async (input, output) =>
        {
            await File.WriteAllBytesAsync(input, plain);
            await PngSanitizer.WriteAsync(input, output, 1048576, 50_000_000, working, CancellationToken.None);
            File.Delete(output);
            await File.WriteAllBytesAsync(input, interlaced);
            var error = await Assert.ThrowsAsync<PdfImageInputException>(() => PngSanitizer.WriteAsync(
                input, output, 1048576, 50_000_000, working, CancellationToken.None));
            Assert.AreEqual(PdfImageReason.Unsupported, error.Reason);
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
