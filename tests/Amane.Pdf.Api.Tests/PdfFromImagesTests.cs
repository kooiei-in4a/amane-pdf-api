using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfFromImagesTests
{
    [TestMethod]
    [DataRow("standard")]
    [DataRow("original")]
    public async Task JpegOrientations_AreAppliedForColorAndGrayAndMetadataRemoved(string quality)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        foreach (var gray in new[] { false, true })
        {
            var source = await CompressFixtures.JpegAsync(test, 100, 60, gray, progressive: true);
            var original = ImageInputInspector.ReadJpeg(Write(test, source, "header.jpg"), 1048576, 50_000_000,
                CancellationToken.None);
            Assert.AreEqual(gray ? 8 : 16, original.McuWidth);
            for (var orientation = 1; orientation <= 8; orientation++)
            {
                using var form = PdfTestContext.MergeForm(FromImagesFixtures.Exif(source, orientation,
                    little: orientation % 2 == 0, late: orientation == 6));
                using var response = await test.Client.PostAsync("/api/pdf/from-images?quality=" + quality, form);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                var pdf = await response.Content.ReadAsByteArrayAsync();
                Assert.AreEqual("images.pdf", response.Content.Headers.ContentDisposition!.FileName);
                await test.AssertValidPdfAsync(pdf, 1);
                var image = await ImageDictionaryAsync(test, pdf);
                var trimmedHeight = gray ? 56 : 48;
                (int Width, int Height)[] expectedSizes = [(100, 60), (96, 60), (96, trimmedHeight),
                    (100, trimmedHeight), (60, 100), (trimmedHeight, 100), (trimmedHeight, 96), (60, 96)];
                var expected = expectedSizes[orientation - 1];
                Assert.AreEqual(expected.Width, image.GetProperty("/Width").GetInt32());
                Assert.AreEqual(expected.Height, image.GetProperty("/Height").GetInt32());
                Assert.IsFalse(Encoding.Latin1.GetString(pdf).Contains("private-image-marker", StringComparison.Ordinal));
                test.AssertClean();
            }
        }
    }

    [TestMethod]
    public async Task MixedInputs_KeepUploadOrderAndAcceptSniffedPngVariants()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test, 48, 32);
        var inputs = new[] { jpeg, FromImagesFixtures.Png(), FromImagesFixtures.Png(17, 25, 16, 0),
            FromImagesFixtures.Png(18, 26, 8, 3), FromImagesFixtures.Png(1, 1, 8, 6, interlaced: true) };
        foreach (var quality in new[] { "standard", "original" })
        {
            using var form = PdfTestContext.MergeForm(inputs);
            using var response = await test.Client.PostAsync("/api/pdf/from-images?quality=" + quality, form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var pdf = await response.Content.ReadAsByteArrayAsync(); await test.AssertValidPdfAsync(pdf, inputs.Length);
            var path = Write(test, pdf, "mixed.pdf");
            var json = await test.QpdfAsync("--json", path);
            using var document = JsonDocument.Parse(json.Output);
            var widths = document.RootElement.GetProperty("pages").EnumerateArray().Select(page =>
                page.GetProperty("images")[0].GetProperty("width").GetInt32()).ToArray();
            CollectionAssert.AreEqual(new[] { 48, 16, 17, 18, 1 }, widths);
            Assert.IsFalse(Encoding.Latin1.GetString(pdf).Contains("private-image-marker", StringComparison.Ordinal));
            test.AssertClean();
        }
    }

    [TestMethod]
    public async Task Errors_AreFixedProblemsWithoutLeakingMetadataPathsOrToolOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        foreach (var query in new[] { "", "?quality=", "?quality=STANDARD", "?quality=original&quality=standard", "?quality=original&extra=x" })
        {
            using var form = PdfTestContext.MergeForm(FromImagesFixtures.Png());
            await Problem(test, form, query, 400);
        }
        using (var none = PdfTestContext.MergeForm()) await Problem(test, none, "?quality=original", 400);
        using (var unknown = PdfTestContext.Form(FromImagesFixtures.Png()))
            await Problem(test, unknown, "?quality=original", 400);
        foreach (var bytes in new[] { Array.Empty<byte>(), "%PDF-private-image-marker"u8.ToArray(), FromImagesFixtures.Png()[..40] })
        {
            using var form = PdfTestContext.MergeForm(bytes);
            await Problem(test, form, "?quality=original", 422, bytes.Length == 0 ? null : "unsupported-image");
        }
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.MaxImageFiles = 1;
        using (var two = PdfTestContext.MergeForm(FromImagesFixtures.Png(), FromImagesFixtures.Png()))
            await Problem(test, two, "?quality=original", 400);
        options.MaxImageInputBytes = FromImagesFixtures.Png().Length - 1;
        using (var large = PdfTestContext.MergeForm(FromImagesFixtures.Png()))
            await Problem(test, large, "?quality=original", 413);
        options.MaxImageInputBytes = 50 * 1048576; options.ImageOutputLimitBytes = 1024;
        using (var capacity = PdfTestContext.MergeForm(FromImagesFixtures.Png()))
            // pdfcpu removes partial output on failure; bytes cannot prove FSIZE was the cause.
            await Problem(test, capacity, "?quality=original", 422, "unsupported-image");
        options.ImageOutputLimitBytes = 54 * 1048576;
        options.ImageJobLimitBytes = FromImagesCapacity.AuxiliaryBytes;
        using (var capacity = PdfTestContext.MergeForm(FromImagesFixtures.Png()))
            await Problem(test, capacity, "?quality=original", 422, "output-too-large");
    }

    [TestMethod]
    public async Task ToolFailures_AreDistinguishedFromInvalidInputAndSuccessfulInvalidOutputs()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test, 32, 48);
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        foreach (var (exit, status, reason) in new[] { (1, 422, "unsupported-image"), (127, 500, (string?)null),
            (139, 500, null), (0, 500, null) })
        {
            var script = Path.Combine(test.Root, "fault.sh");
            await File.WriteAllTextAsync(script, $"#!/bin/sh\necho private-image-marker >&2\nexit {exit}\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.JpegtranPath = script;
            using var form = PdfTestContext.MergeForm(jpeg);
            await Problem(test, form, "?quality=original", status, reason);
        }
    }

    [TestMethod]
    public async Task TinyTranspose_IsAcceptedButTrimWithoutAFullMcuIsUnsupported()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test, 3, 2);
        foreach (var quality in new[] { "standard", "original" })
        {
            using (var form = PdfTestContext.MergeForm(FromImagesFixtures.Exif(jpeg, 5)))
            using (var response = await test.Client.PostAsync("/api/pdf/from-images?quality=" + quality, form))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var image = await ImageDictionaryAsync(test, await response.Content.ReadAsByteArrayAsync());
                Assert.AreEqual(2, image.GetProperty("/Width").GetInt32());
                Assert.AreEqual(3, image.GetProperty("/Height").GetInt32()); test.AssertClean();
            }
            using var trimmed = PdfTestContext.MergeForm(FromImagesFixtures.Exif(jpeg, 6));
            await Problem(test, trimmed, "?quality=" + quality, 422, "unsupported-image");
        }
    }

    [TestMethod]
    public async Task UploadDeletionFailure_StopsBeforeImportWithoutReleasingReservation()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        using var files = new TemporaryPdfFiles(test.TempRoot);
        var input = files.ImagePath(1, "upload.bin");
        await File.WriteAllBytesAsync(input, FromImagesFixtures.Png());
        var processor = new PdfFromImagesProcessor(options, files, new(Options.Create(options)), new(Options.Create(options)));
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => processor.ProcessAsync([input], false,
            CancellationToken.None, stage =>
            {
                if (stage.Name != "normalized") return;
                File.Move(input, Path.Combine(files.DirectoryPath, "retained-upload"));
                Directory.CreateDirectory(input); // Deleting a directory as a file is a managed I/O failure.
            }));
        Assert.IsNotNull(error);
        Assert.IsFalse(File.Exists(files.ImagePath(1, "page.pdf")));
        Assert.AreEqual(FromImagesCapacity.AuxiliaryBytes + 8192, processor.Capacity.Used);
    }

    [TestMethod]
    public async Task GeneratedPdfFailure_IsInternalEvenWithAnOversizedPartialFileAndSignalExit()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        var script = Path.Combine(test.Root, "merge-fault.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\n" +
            "if [ \"$1\" = --empty ]; then for last do :; done; dd if=/dev/zero of=\"$last\" bs=16385 count=1 status=none; exit 139; fi\nexec qpdf \"$@\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        options.QpdfPath = script; options.ImageOutputLimitBytes = 16384;
        using var form = PdfTestContext.MergeForm(FromImagesFixtures.Png());
        await Problem(test, form, "?quality=original", 500);
    }

    internal static async Task Problem(PdfTestContext test, HttpContent form, string query, int status, string? reason = null)
    {
        using var response = await test.Client.PostAsync("/api/pdf/from-images" + query, form);
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        if (reason is null) Assert.IsFalse(json.RootElement.TryGetProperty("reason", out _));
        else Assert.AreEqual(reason, json.RootElement.GetProperty("reason").GetString());
        Assert.IsFalse(text.Contains(test.Root, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("private-image-marker", StringComparison.Ordinal));
        Assert.IsFalse(test.Logs.Any(line => line.Contains("private-image-marker", StringComparison.Ordinal)));
        test.AssertClean();
    }

    private static string Write(PdfTestContext test, byte[] bytes, string name)
    {
        var path = Path.Combine(test.Root, name); File.WriteAllBytes(path, bytes); return path;
    }
    private static async Task<JsonElement> ImageDictionaryAsync(PdfTestContext test, byte[] pdf)
    {
        var result = await test.QpdfAsync("--json", Write(test, pdf, "image.pdf"));
        Assert.AreEqual(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var objects = document.RootElement.GetProperty("qpdf")[1];
        return objects.EnumerateObject().Select(x => x.Value)
            .Where(x => x.TryGetProperty("stream", out _)).Select(x => x.GetProperty("stream").GetProperty("dict"))
            .First(x => x.TryGetProperty("/Subtype", out var type) && type.GetString() == "/Image").Clone();
    }
}
