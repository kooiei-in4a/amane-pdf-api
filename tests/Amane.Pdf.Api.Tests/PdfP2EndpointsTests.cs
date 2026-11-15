using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfP2EndpointsTests
{
    [TestMethod]
    [DataRow("102399")]
    [DataRow("0102400")]
    [DataRow("52428801")]
    [DataRow("102400&level=standard")]
    [DataRow("102400&maxBytes=102400")]
    public async Task TargetCompressionRejectsInvalidQuery(string value)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        await test.AssertRejectedBeforeReadingBodyAsync("/api/pdf/compress?maxBytes=" + value, form);
    }

    [TestMethod]
    public async Task TargetCompressionOptimizesSmallInputAndRecompressesLargeJpeg()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        await Check(PdfTestContext.Fixture, 102_400, 0);
        var input = CompressFixtures.Pdf(await CompressFixtures.JpegAsync(test));
        await Check(input, Math.Max(102_400, input.Length * 3 / 4), 1);

        async Task Check(byte[] source, int target, int count)
        {
            using var form = PdfTestContext.FileForm(source);
            using var response = await test.Client.PostAsync("/api/pdf/compress?maxBytes=" + target.ToString(CultureInfo.InvariantCulture), form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("true", response.Headers.GetValues("X-Pdf-Target-Reached").Single());
            Assert.AreEqual(count.ToString(CultureInfo.InvariantCulture), response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
            var output = await response.Content.ReadAsByteArrayAsync();
            Assert.IsTrue(output.Length <= target);
            await test.AssertValidPdfAsync(output, 1);
            test.AssertClean();
        }
    }

    [TestMethod]
    public async Task TargetCompressionReturnsValidFallbackWhenTargetCannotBeReached()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        // A custom Decode array makes this image ineligible for replacement.
        var source = CompressFixtures.Pdf(await CompressFixtures.JpegAsync(test), extra: "/Decode [0 1 0 1 0 1]");
        Assert.IsGreaterThan(102_400, source.Length);
        using var form = PdfTestContext.FileForm(source);
        using var response = await test.Client.PostAsync("/api/pdf/compress?maxBytes=102400", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("false", response.Headers.GetValues("X-Pdf-Target-Reached").Single());
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        var output = await response.Content.ReadAsByteArrayAsync();
        Assert.IsGreaterThan(102_400, output.Length);
        await test.AssertValidPdfAsync(output, 1);
        test.AssertClean();
    }

    [TestMethod]
    public async Task TargetCompressionCompletesWithValidatedInputWhenTheSoftDeadlineExpires()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:CompressSoftTimeoutSeconds"] = "1" });
        var wrapper = Path.Combine(test.Root, "qpdf.sh");
        await File.WriteAllTextAsync(wrapper, """
            #!/bin/sh
            for arg do
                if [ "$arg" = "--object-streams=generate" ]; then
                    exec sleep 30
                fi
            done
            exec qpdf "$@"
            """);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/compress?maxBytes=102400", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("true", response.Headers.GetValues("X-Pdf-Target-Reached").Single());
        CollectionAssert.AreEqual(PdfTestContext.Fixture, await response.Content.ReadAsByteArrayAsync());
        test.AssertClean();
    }

    [TestMethod]
    public async Task CompareExtractsJapaneseAndIgnoresWhitespace()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:TextExtractorPath"] = ExtractorPath() });
        var old = await Fill("支払期日は三十日以内です。");
        var revised = await Fill("支払期日は 六十日以内です。");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(revised), "new", "new.pdf");
        multipart.Add(new ByteArrayContent(old), "old", "old.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/compare", multipart);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.IsNull(response.Content.Headers.ContentDisposition);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var change = json.RootElement.GetProperty("changes").EnumerateArray().Single();
        Assert.AreEqual("replace", change.GetProperty("type").GetString());
        Assert.AreEqual("三", change.GetProperty("old").GetProperty("text").GetString());
        Assert.AreEqual("六", change.GetProperty("new").GetProperty("text").GetString());
        Assert.AreEqual(1, change.GetProperty("new").GetProperty("page").GetInt32());
        Assert.IsTrue(change.GetProperty("before").GetString()!.EndsWith("支払期日は", StringComparison.Ordinal));
        Assert.AreEqual("十日以内です。", change.GetProperty("after").GetString());
        test.AssertClean();

        async Task<byte[]> Fill(string text)
        {
            using var form = PdfFillTemplateTests.Form(fields: Encoding.UTF8.GetBytes("[" + FillTemplateFieldsTests.Change("text", text) + "]"));
            using var result = await test.Client.PostAsync(PdfFillTemplateTests.Endpoint, form);
            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
            return await result.Content.ReadAsByteArrayAsync();
        }
    }

    [TestMethod]
    public async Task CompareRejectsTextlessPdfAndOverLimitInput()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:TextExtractorPath"] = ExtractorPath(),
            ["Pdf:MaxComparePages"] = "1",
        });
        var blank = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(PdfTestContext.Fixture)
            .Replace("Hello PDF", "         ", StringComparison.Ordinal));
        await Check(blank, "text-unavailable");
        await Check(await test.CreatePagedPdfAsync(2), "too-large-to-compare");

        async Task Check(byte[] input, string reason)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(input), "old", "old.pdf");
            form.Add(new ByteArrayContent(PdfTestContext.Fixture), "new", "new.pdf");
            using var response = await test.Client.PostAsync("/api/pdf/compare", form);
            Assert.AreEqual(422, (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            Assert.AreEqual(reason, json.RootElement.GetProperty("reason").GetString());
            test.AssertClean();
        }
    }

    [TestMethod]
    public async Task CompareDeadlineReapsExtractorAndCleansJob()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfTimeoutSeconds"] = "2" });
        var runner = Path.Combine(test.Root, "slow-extractor.sh");
        var pidMarker = Path.Combine(test.Root, "extractor.pid");
        await File.WriteAllTextAsync(runner, $"#!/bin/sh\nprintf '%s' \"$$\" > '{pidMarker}'\nexec sleep 60\n");
        File.SetUnixFileMode(runner, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.DotnetPath = runner;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(PdfTestContext.Fixture), "old", "old.pdf");
        form.Add(new ByteArrayContent(PdfTestContext.Fixture), "new", "new.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var pid = await File.ReadAllTextAsync(pidMarker);
        Assert.IsFalse(Directory.Exists("/proc/" + pid));
        test.AssertClean();
    }

    [TestMethod]
    public void TextDiffPreservesScalarAndPageBoundariesAndTruncates()
    {
        var result = TextDiff.Compare(Side("甲🐈乙丙丁"), Side("甲🐕乙丁戊"), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "replace", "delete", "insert" }, result.Changes.Select(change => change.Type).ToArray());
        Assert.AreEqual("🐈", result.Changes[0].Old?.Text);
        Assert.AreEqual(2, result.Changes[0].New?.Page);
        Assert.AreEqual("甲", result.Changes[0].Before);
        var many = TextDiff.Compare(Side(string.Concat(Enumerable.Repeat("甲乙", 1001))),
            Side(string.Concat(Enumerable.Repeat("丙乙", 1001))), CancellationToken.None);
        Assert.HasCount(1000, many.Changes);
        Assert.IsTrue(many.Truncated);

        static CompareText Side(string s)
        {
            Rune[] text = [.. s.EnumerateRunes()];
            return new(text, [1, .. Enumerable.Repeat(2, text.Length - 1)], 2);
        }
    }

    [TestMethod]
    [DataRow("png", "png")]
    [DataRow("jpeg", "jpg")]
    public async Task ToImagesReturnsSelectedPageAsZip(string format, string suffix)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.FileForm(await test.CreatePagedPdfAsync(3));
        using var response = await test.Client.PostAsync("/api/pdf/to-images?format=" + format + "&dpi=72&pages=2", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("images.zip", response.Content.Headers.ContentDisposition?.FileName);
        using var bytes = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        var entry = zip.Entries.Single();
        Assert.AreEqual("page-002." + suffix, entry.FullName);
        using var rendered = entry.Open();
        var header = new byte[24];
        rendered.ReadExactly(header);
        if (format == "png")
        {
            CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header[..8]);
            Assert.IsGreaterThan(0, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)));
            Assert.IsGreaterThan(0, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
        }
        else CollectionAssert.AreEqual(new byte[] { 255, 216 }, header[..2]);
        test.AssertClean();
    }

    [TestMethod]
    public async Task ToImagesRejectsPageAndImageLimitsAndSharesRenderSlotWithCompare()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        using (var oversized = PdfTestContext.FileForm(await test.CreatePagedPdfAsync(51)))
        using (var rejected = await test.Client.PostAsync("/api/pdf/to-images?format=png&dpi=72", oversized))
        {
            Assert.AreEqual(422, (int)rejected.StatusCode);
            using var json = JsonDocument.Parse(await rejected.Content.ReadAsByteArrayAsync());
            Assert.AreEqual("too-many-pages", json.RootElement.GetProperty("reason").GetString());
        }
        // 10001 x 3000 x 3 exceeds the DCT sample limit measured for PDFium.
        var image = CompressFixtures.Pdf(await CompressFixtures.JpegAsync(test), width: 10_001, height: 3000);
        using (var large = PdfTestContext.FileForm(image))
        using (var rejected = await test.Client.PostAsync("/api/pdf/to-images?format=png&dpi=72", large))
        {
            Assert.AreEqual(422, (int)rejected.StatusCode);
            using var json = JsonDocument.Parse(await rejected.Content.ReadAsByteArrayAsync());
            Assert.AreEqual("too-complex", json.RootElement.GetProperty("reason").GetString());
        }
        using var lease = test.Factory.Services.GetRequiredService<RenderSlot>().TryAcquire();
        Assert.IsNotNull(lease);
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var busy = await test.Client.PostAsync("/api/pdf/to-images?format=jpeg&dpi=200", form);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        using var compare = new MultipartFormDataContent();
        compare.Add(new ByteArrayContent(PdfTestContext.Fixture), "old", "old.pdf");
        compare.Add(new ByteArrayContent(PdfTestContext.Fixture), "new", "new.pdf");
        using var compareBusy = await test.Client.PostAsync("/api/pdf/compare", compare);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, compareBusy.StatusCode);
        test.AssertClean();
    }

    private static string ExtractorPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/Amane.Pdf.TextExtractor/bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0/Amane.Pdf.TextExtractor.dll"));
}
