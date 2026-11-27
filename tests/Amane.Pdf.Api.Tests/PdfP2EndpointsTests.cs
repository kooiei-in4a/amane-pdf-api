using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Amane.Pdf.TextExtractor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfP2EndpointsTests
{
    [TestMethod]
    [DataRow("102399")]
    [DataRow("0102400")]
    [DataRow("102400%00")]
    [DataRow("102400x")]
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
    public async Task TargetCompressionTriesJpegWhenLosslessEstimateExceedsCapacity()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var source = CompressFixtures.Pdf(await CompressFixtures.JpegAsync(test));
        var capacity = CompressCapacity.Allocated(source.Length) +
            CompressCapacity.Allocated(source.Length + 4 * 1024 * 1024) - 4096;
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.CompressJobLimitBytes = capacity;
        var target = source.Length / 4;
        Assert.IsGreaterThanOrEqualTo(102_400, target);
        using var form = PdfTestContext.FileForm(source);
        using var response = await test.Client.PostAsync("/api/pdf/compress?maxBytes=" + target.ToString(CultureInfo.InvariantCulture), form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("true", response.Headers.GetValues("X-Pdf-Target-Reached").Single());
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        var output = await response.Content.ReadAsByteArrayAsync();
        Assert.IsLessThanOrEqualTo(target, output.Length);
        await test.AssertValidPdfAsync(output, 1);
        test.AssertClean();
    }

    [TestMethod]
    public async Task TargetCompressionKeepsInputWhenLosslessWouldIncreaseItsSize()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        // For this small PDF, the object stream and cross-reference stream cost more than they save.
        var source = TextPdf("A");
        using var form = PdfTestContext.FileForm(source);
        using var response = await test.Client.PostAsync("/api/pdf/compress?maxBytes=102400", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("true", response.Headers.GetValues("X-Pdf-Target-Reached").Single());
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        var output = await response.Content.ReadAsByteArrayAsync();
        Assert.IsTrue(source.AsSpan().SequenceEqual(output));
        test.AssertClean();
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
        TestExecutable.Write(wrapper, """
            #!/bin/sh
            for arg do
                if [ "$arg" = "--object-streams=generate" ]; then
                    exec sleep 30
                fi
            done
            exec qpdf "$@"
            """);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/compress?maxBytes=102400", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("true", response.Headers.GetValues("X-Pdf-Target-Reached").Single());
        CollectionAssert.AreEqual(PdfTestContext.Fixture, await response.Content.ReadAsByteArrayAsync());
        test.AssertClean();
    }

    [TestMethod]
    public async Task TargetCompressionFinishesAdoptedImagesAfterSoftDeadline()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:CompressSoftTimeoutSeconds"] = "3",
            ["Pdf:CompressImageTimeoutSeconds"] = "10"
        });
        var source = CompressFixtures.Many(await CompressFixtures.JpegAsync(test), 2);
        var attempts = Path.Combine(test.Root, "cjpeg-attempts");
        var wrapper = Path.Combine(test.Root, "cjpeg.sh");
        TestExecutable.Write(wrapper, $$"""
            #!/bin/sh
            printf 'image\n' >> '{{attempts}}'
            if [ "$(wc -l < '{{attempts}}')" -gt 1 ]; then
                exec sleep 30
            fi
            exec cjpeg "$@"
            """);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.CjpegPath = wrapper;
        using var form = PdfTestContext.FileForm(source);
        using var response = await test.Client.PostAsync("/api/pdf/compress?maxBytes=102400", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        Assert.HasCount(2, await File.ReadAllLinesAsync(attempts));
        var output = await response.Content.ReadAsByteArrayAsync();
        Assert.IsLessThan(source.Length, output.Length);
        await test.AssertValidPdfAsync(output, 1);
        test.AssertClean();
    }

    [TestMethod]
    public async Task TargetCompressionReturnsBestFileWhenPassCannotFinishBeforeDeadline()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfTimeoutSeconds"] = "6",
            ["Pdf:CompressSoftTimeoutSeconds"] = "3"
        });
        var source = CompressFixtures.Pdf(await CompressFixtures.JpegAsync(test));
        Assert.IsGreaterThan(102_400, source.Length);
        var pidMarker = Path.Combine(test.Root, "qpdf-update.pid");
        var wrapper = Path.Combine(test.Root, "qpdf.sh");
        // Only the lossy pass writes with --update-from-json; stall it past the soft deadline.
        TestExecutable.Write(wrapper, $$"""
            #!/bin/sh
            for argument in "$@"; do
                case "$argument" in --update-from-json=*) printf '%s' "$$" > '{{pidMarker}}'; exec sleep 60 ;; esac
            done
            exec qpdf "$@"
            """);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var form = PdfTestContext.FileForm(source);
        using var response = await test.Client.PostAsync("/api/pdf/compress?maxBytes=102400", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("false", response.Headers.GetValues("X-Pdf-Target-Reached").Single());
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
        Assert.IsFalse(Directory.Exists("/proc/" + await File.ReadAllTextAsync(pidMarker)));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("72%00")]
    [DataRow("072")]
    [DataRow("201")]
    public async Task ToImagesRejectsInvalidDpi(string dpi)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        await test.AssertRejectedBeforeReadingBodyAsync("/api/pdf/to-images?format=png&dpi=" + dpi, form);
    }

    [TestMethod]
    public async Task TargetCompressionSelectsImagesOnceAcrossStages()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var source = CompressFixtures.Many(await CompressFixtures.JpegAsync(test), 6);
        var events = Path.Combine(test.Root, "qpdf-events");
        var wrapper = Path.Combine(test.Root, "qpdf.sh");
        TestExecutable.Write(wrapper, $$"""
            #!/bin/sh
            for arg do
                case "$arg" in
                    --json-key=pages) printf 'pages\n' >> '{{events}}' ;;
                    --update-from-json=*) printf 'write\n' >> '{{events}}' ;;
                esac
            done
            exec qpdf "$@"
            """);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var form = PdfTestContext.FileForm(source);
        using var response = await test.Client.PostAsync("/api/pdf/compress?maxBytes=102400", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var operations = await File.ReadAllLinesAsync(events);
        Assert.AreEqual(1, operations.Count(operation => operation == "pages"));
        Assert.IsGreaterThan(1, operations.Count(operation => operation == "write"));
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
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
    public async Task CompareKeepsApiWorkerOutputWithinEightMiBForLargeUnicodeDifference()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:TextExtractorPath"] = ExtractorPath() });
        using var form = CompareForm(WinAnsiTextPdf(new string('\u00e9', 500_000)),
            WinAnsiTextPdf(new string('\u00f1', 500_000)));
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.IsGreaterThan(2 * 1024 * 1024, bytes.Length);
        Assert.IsLessThan(8 * 1024 * 1024, bytes.Length);
        using var json = JsonDocument.Parse(bytes);
        Assert.IsFalse(json.RootElement.GetProperty("truncated").GetBoolean());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(0, 5, false)]
    [DataRow(5, 5, false)]
    [DataRow(10, 10, false)]
    [DataRow(5, 5, true)]
    public async Task CompareReportsLongRunsOfInsertedOrDeletedPages(int offset, int count, bool delete)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:TextExtractorPath"] = ExtractorPath() });
        var original = ContractPages("Original agreement", 10);
        var added = ContractPages("New supplemental terms", count);
        var expanded = original.Take(offset).Concat(added).Concat(original.Skip(offset));
        var expected = string.Concat(added.SelectMany(page => page)).Replace(" ", "", StringComparison.Ordinal);
        Assert.IsGreaterThan(4096, expected.Length);

        using var form = new MultipartFormDataContent();
        var originalPdf = PageTextPdf(original);
        var expandedPdf = PageTextPdf(expanded);
        form.Add(new ByteArrayContent(delete ? expandedPdf : originalPdf), "old", "old.pdf");
        form.Add(new ByteArrayContent(delete ? originalPdf : expandedPdf), "new", "new.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var change = json.RootElement.GetProperty("changes").EnumerateArray().Single();
        Assert.AreEqual(delete ? "delete" : "insert", change.GetProperty("type").GetString());
        Assert.AreEqual(JsonValueKind.Null, change.GetProperty(delete ? "new" : "old").ValueKind);
        var excerpt = change.GetProperty(delete ? "old" : "new");
        Assert.AreEqual(offset + 1, excerpt.GetProperty("page").GetInt32());
        Assert.AreEqual(expected, excerpt.GetProperty("text").GetString());
        Assert.IsFalse(json.RootElement.GetProperty("truncated").GetBoolean());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("dash", false)]
    [DataRow("dash", true)]
    [DataRow("fraction", false)]
    [DataRow("fraction", true)]
    [DataRow("header", false)]
    public async Task CompareAlignsNumberedPagesAfterInsertingOrDeletingFivePages(string format, bool delete)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:TextExtractorPath"] = ExtractorPath() });
        var original = ContractPages("Original agreement", 10);
        var added = ContractPages("New supplemental terms", 5);
        var revised = original.Take(5).Concat(added).Concat(original.Skip(5)).ToArray();
        var originalPdf = PageTextPdf(NumberPages(original, format));
        var revisedPdf = PageTextPdf(NumberPages(revised, format));
        using var form = CompareForm(delete ? revisedPdf : originalPdf, delete ? originalPdf : revisedPdf);
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var changes = json.RootElement.GetProperty("changes").EnumerateArray().ToArray();
        var action = delete ? "delete" : "insert";
        var change = changes.Single(item => item.GetProperty("type").GetString() == action);
        var excerpt = change.GetProperty(delete ? "old" : "new");
        Assert.AreEqual(6, excerpt.GetProperty("page").GetInt32());
        Assert.AreEqual(ComparableText(NumberPages(revised, format).Skip(5).Take(5)), excerpt.GetProperty("text").GetString());
        Assert.IsTrue(changes.Any(item => item.GetProperty("type").GetString() == "replace"),
            "The text of the renumbered pages is still compared.");
        Assert.IsFalse(json.RootElement.GetProperty("truncated").GetBoolean());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(4)]
    [DataRow(5)]
    public async Task CompareReturnsOneReplacementWhenPagesAreInsertedBesideASmallEdit(int editedPage)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:TextExtractorPath"] = ExtractorPath() });
        var original = ContractPages("Original agreement", 10);
        var originalPdf = PageTextPdf(original);
        var before = ComparableText([original[editedPage]]);
        original[editedPage][0] = original[editedPage][0].Replace("annual term.", "annual team.", StringComparison.Ordinal);
        var added = ContractPages("New supplemental terms", 5);
        var revised = original.Take(5).Concat(added).Concat(original.Skip(5)).ToArray();
        using var form = CompareForm(originalPdf, PageTextPdf(revised));
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var change = json.RootElement.GetProperty("changes").EnumerateArray().Single();
        Assert.AreEqual("replace", change.GetProperty("type").GetString());
        Assert.AreEqual(editedPage + 1, change.GetProperty("old").GetProperty("page").GetInt32());
        Assert.AreEqual(editedPage + 1, change.GetProperty("new").GetProperty("page").GetInt32());
        Assert.AreEqual(before, change.GetProperty("old").GetProperty("text").GetString());
        Assert.AreEqual(ComparableText(revised.Skip(editedPage).Take(6)), change.GetProperty("new").GetProperty("text").GetString());
        Assert.IsFalse(json.RootElement.GetProperty("truncated").GetBoolean());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(100, "AB CD", 200)]
    [DataRow(5000, "AB CD", 422)]
    [DataRow(100, "ABCDE", 422)]
    public async Task CompareCountsComparableTextAndCapsWhitespaceScan(int spaces, string text, int expectedStatus)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:TextExtractorPath"] = ExtractorPath(),
            ["Pdf:MaxCompareCharacters"] = "4"
        });
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(TextPdf(new string(' ', spaces) + text)), "old", "old.pdf");
        form.Add(new ByteArrayContent(TextPdf("ABCD")), "new", "new.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(expectedStatus, (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        if (expectedStatus == 200)
        {
            Assert.AreEqual(4, json.RootElement.GetProperty("old").GetProperty("characters").GetInt32());
            Assert.AreEqual(0, json.RootElement.GetProperty("changes").GetArrayLength());
        }
        else Assert.AreEqual("too-large-to-compare", json.RootElement.GetProperty("reason").GetString());
        test.AssertClean();
    }

    [TestMethod]
    public void ExtractorClassifiesOnlyPdfPigInputExceptionsAsUnsupported()
    {
        // PdfPig recovers from the broken fonts tried so far, so classify the exception types directly.
        // CompareRejectsPdfPigPageParsingFailure covers the stack depth exception with a real PDF.
        foreach (var exception in new Exception[]
                 {
                     new UglyToad.PdfPig.Core.PdfDocumentFormatException("format"),
                     new UglyToad.PdfPig.Fonts.InvalidFontFormatException("font"),
                     new UglyToad.PdfPig.Fonts.CorruptCompressedDataException("data")
                 })
            Assert.IsTrue(Amane.Pdf.TextExtractor.Program.IsUnsupportedInput(exception), exception.GetType().Name);
        Assert.IsFalse(Amane.Pdf.TextExtractor.Program.IsUnsupportedInput(new InvalidOperationException()));
        Assert.IsFalse(Amane.Pdf.TextExtractor.Program.IsUnsupportedInput(new OutOfMemoryException()));
    }

    [TestMethod]
    public async Task CompareRejectsPdfPigPageParsingFailure()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:TextExtractorPath"] = ExtractorPath() });
        // qpdf accepts this content stream, but PdfPig refuses its array nesting depth.
        var input = ContentPdf(new string('[', 260) + "1" + new string(']', 260) + "\nBT /F1 12 Tf (A) Tj ET");
        await test.AssertValidPdfAsync(input, 1);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(input), "old", "old.pdf");
        form.Add(new ByteArrayContent(TextPdf("A")), "new", "new.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(422, (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("unsupported-pdf", json.RootElement.GetProperty("reason").GetString());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompareRunsNearLimitDiffInBoundedWorker(bool stall)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:TextExtractorPath"] = ExtractorPath(),
            ["Pdf:QpdfTimeoutSeconds"] = stall ? "6" : "30"
        });
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        var marker = Path.Combine(test.Root, "diff.pid");
        var runner = Path.Combine(test.Root, "compare-worker.sh");
        TestExecutable.Write(runner, $$"""
            #!/bin/sh
            if [ "$2" = "--diff" ]; then
                [ "$(ulimit -d)" = 393216 ] && [ "$DOTNET_GCHeapHardLimit" = 0x10000000 ] || exit 1
                printf '%s' "$$" > '{{marker}}'
                {{(stall ? "exec sleep 60" : "")}}
            fi
            exec '{{options.DotnetPath}}' "$@"
            """);
        options.DotnetPath = runner;

        // Disjoint alphabets give an insertion/deletion distance of exactly 4,096.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(TextPdf(new string('A', 2048))), "old", "old.pdf");
        form.Add(new ByteArrayContent(TextPdf(new string('B', 2048))), "new", "new.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(stall ? HttpStatusCode.GatewayTimeout : HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(File.Exists(marker));
        Assert.IsFalse(Directory.Exists("/proc/" + await File.ReadAllTextAsync(marker)));
        if (!stall)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var change = json.RootElement.GetProperty("changes").EnumerateArray().Single();
            Assert.AreEqual("replace", change.GetProperty("type").GetString());
            Assert.AreEqual(2048, change.GetProperty("old").GetProperty("text").GetString()!.Length);
            Assert.AreEqual(2048, change.GetProperty("new").GetProperty("text").GetString()!.Length);
        }
        test.AssertClean();
    }

    [TestMethod]
    public async Task CompareReportsExtractorExceptionsAsInternalError()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        var broken = Path.Combine(test.Root, "broken.pdf");
        // The undefined font raises InvalidOperationException, which does not classify the failure.
        await File.WriteAllBytesAsync(broken, ContentPdf("BT /F2 12 Tf (A) Tj ET"));
        var result = await ExternalProcessRunner.RunAsync(new(options.DotnetPath,
            [ExtractorPath(), broken, Path.Combine(test.Root, "text.json"), "300", "500000"]), CancellationToken.None);
        Assert.AreEqual(1, result.ExitCode);

        var runner = Path.Combine(test.Root, "failed-extractor.sh");
        TestExecutable.Write(runner, "#!/bin/sh\nprintf 'EXTRACTOR-DETAIL-SENTINEL' >&2\nexit 1\n");
        options.DotnetPath = runner;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(PdfTestContext.Fixture), "old", "old.pdf");
        form.Add(new ByteArrayContent(PdfTestContext.Fixture), "new", "new.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("EXTRACTOR-DETAIL-SENTINEL"));
        Assert.IsFalse(test.Logs.Any(log => log.Contains("EXTRACTOR-DETAIL-SENTINEL")));
        test.AssertClean();
    }

    [TestMethod]
    public async Task CompareReportsAFullDiffOutputAsTooLarge()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        using var scripts = new OverlayJob();
        options.DotnetPath = scripts.Script("""
            if [ "$2" = "--diff" ]; then
                truncate -s 8388608 "$5" || exit 99
                exit 1
            fi
            printf '{"pages":["text"]}' > "$3"
            """);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(PdfTestContext.Fixture), "old", "old.pdf");
        form.Add(new ByteArrayContent(PdfTestContext.Fixture), "new", "new.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/compare", form);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("too-large-to-compare", json.RootElement.GetProperty("reason").GetString());
        test.AssertClean();
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
        TestExecutable.Write(runner, $"#!/bin/sh\nprintf '%s' \"$$\" > '{pidMarker}'\nexec sleep 60\n");
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
        var result = TextDiff.Compare(Side("甲🐈乙丙丁"), Side("甲🐕乙丁戊"));
        CollectionAssert.AreEqual(new[] { "replace", "delete", "insert" }, result.Changes.Select(change => change.Type).ToArray());
        Assert.AreEqual("🐈", result.Changes[0].Old?.Text);
        Assert.AreEqual(2, result.Changes[0].New?.Page);
        Assert.AreEqual("甲", result.Changes[0].Before);
        var many = TextDiff.Compare(Side(string.Concat(Enumerable.Repeat("甲乙", 1001))),
            Side(string.Concat(Enumerable.Repeat("丙乙", 1001))));
        Assert.HasCount(1000, many.Changes);
        Assert.IsTrue(many.Truncated);

        static CompareText Side(string s)
        {
            Rune[] text = [.. s.EnumerateRunes()];
            return new(text, [1, .. Enumerable.Repeat(2, text.Length - 1)], 2);
        }
    }

    [TestMethod]
    public void TextDiffKeepsSmallReplacementsBetweenLargePageEdits()
    {
        var deleted = new string('削', 4500);
        var inserted = new string('追', 4500);
        var old = Side("表紙", deleted, "契約内容", "支払期日は三十日", "注意事項", "最終頁");
        var revised = Side("表紙", "契約内容", "支払期日は六十日", "注意事項", inserted, "最終頁");
        var result = TextDiff.Compare(old, revised);
        CollectionAssert.AreEqual(new[] { "delete", "replace", "insert" }, result.Changes.Select(change => change.Type).ToArray());
        Assert.AreEqual(new CompareExcerpt(2, deleted), result.Changes[0].Old);
        Assert.AreEqual(new CompareExcerpt(4, "三"), result.Changes[1].Old);
        Assert.AreEqual(new CompareExcerpt(3, "六"), result.Changes[1].New);
        Assert.AreEqual(new CompareExcerpt(5, inserted), result.Changes[2].New);
        Assert.IsFalse(result.Truncated);
        var unaligned = TextDiff.Compare(Side(deleted), Side(inserted)).Changes.Single();
        Assert.AreEqual("replace", unaligned.Type);
        Assert.AreEqual(new CompareExcerpt(1, deleted), unaligned.Old);
        Assert.AreEqual(new CompareExcerpt(1, inserted), unaligned.New);

        static CompareText Side(params string[] pages) => new(
            [.. pages.SelectMany(page => page.EnumerateRunes())],
            [.. pages.SelectMany((page, i) => Enumerable.Repeat(i + 1, page.Length))],
            pages.Length);
    }

    [TestMethod]
    [DataRow("png", "png", "2", 2, 1)]
    [DataRow("jpeg", "jpg", "2", 2, 1)]
    [DataRow("png", "png", "1-3", 1, 3)]
    [DataRow("jpeg", "jpg", "1-3", 1, 3)]
    public async Task ToImagesReturnsSelectedPagesAsStoredZip(string format, string suffix, string pages,
        int firstPage, int pageCount)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.FileForm(await test.CreatePagedPdfAsync(3));
        using var response = await test.Client.PostAsync("/api/pdf/to-images?format=" + format + "&dpi=72&pages=" + pages, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("images.zip", response.Content.Headers.ContentDisposition?.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.HasCount(pageCount, zip.Entries);
        PdfSplitTests.AssertStoredDosHeaders(bytes, pageCount);
        for (var i = 0; i < pageCount; i++)
        {
            var entry = zip.Entries[i];
            Assert.AreEqual($"page-{firstPage + i:D3}.{suffix}", entry.FullName);
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
        }
        test.AssertClean();
    }

    [TestMethod]
    public void ToImagesSettingsValidateBoundsAndKeepDefaultValues()
    {
        if (!OperatingSystem.IsLinux()) return;
        var defaults = new PdfOptions();
        Assert.AreEqual(50, defaults.MaxToImagesPages);
        Assert.AreEqual(64L * 1024 * 1024, defaults.MaxToImagesZipBytes);
        Assert.IsTrue(PdfToImagesEndpoint.IsValid(defaults));
        Assert.IsTrue(PdfToImagesEndpoint.IsValid(new()
        {
            MaxToImagesPages = PdfToImagesEndpoint.MaxConfiguredPages,
            MaxToImagesZipBytes = PdfToImagesEndpoint.MaxConfiguredZipBytes
        }));
        Assert.IsFalse(PdfToImagesEndpoint.IsValid(new() { MaxToImagesPages = 0 }));
        Assert.IsFalse(PdfToImagesEndpoint.IsValid(new()
        { MaxToImagesPages = PdfToImagesEndpoint.MaxConfiguredPages + 1 }));
        Assert.IsFalse(PdfToImagesEndpoint.IsValid(new() { MaxToImagesZipBytes = 0 }));
        Assert.IsFalse(PdfToImagesEndpoint.IsValid(new()
        { MaxToImagesZipBytes = PdfToImagesEndpoint.MaxConfiguredZipBytes + 1 }));
    }

    [TestMethod]
    public async Task ToImagesAcceptsExpandedLimits()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxToImagesPages"] = PdfToImagesEndpoint.MaxConfiguredPages.ToString(),
            ["Pdf:MaxToImagesZipBytes"] = PdfToImagesEndpoint.MaxConfiguredZipBytes.ToString()
        });
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/to-images?format=jpeg&dpi=72", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.HasCount(1, zip.Entries);
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

    [TestMethod]
    public async Task ToImagesUsesConfiguredJobBudgetForUploadAndOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxRedactInputBytes"] = "4096",
            ["Pdf:RedactJobLimitBytes"] = "16384"
        });
        const string route = "/api/pdf/to-images?format=png&dpi=72";
        using (var large = PdfTestContext.FileForm(new byte[16_385]))
        using (var rejectedUpload = await test.Client.PostAsync(route, large))
            Assert.AreEqual(413, (int)rejectedUpload.StatusCode);
        test.AssertClean();

        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var rejectedOutput = await test.Client.PostAsync(route, form);
        Assert.AreEqual(422, (int)rejectedOutput.StatusCode);
        using var json = JsonDocument.Parse(await rejectedOutput.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("output-too-large", json.RootElement.GetProperty("reason").GetString());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false, "too-complex")]
    [DataRow(true, "output-too-large")]
    public async Task ToImagesPreservesSecondPageFailureAndCleansJob(bool exceedsBudget, string reason)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxRedactInputBytes"] = "4096",
            ["Pdf:RedactJobLimitBytes"] = "32768"
        });
        var marker = await ReplaceSecondPageRendererAsync(test, exceedsBudget
            ? """
                max_bytes=${14}
                output=${16}
                exec truncate -s "$((max_bytes + 1))" "$output"
                """
            : "exit 5");
        using var form = PdfTestContext.FileForm(await test.CreatePagedPdfAsync(2));
        using var response = await test.Client.PostAsync("/api/pdf/to-images?format=png&dpi=72", form);
        Assert.IsTrue(File.Exists(marker), "The renderer must reach page two after writing the first ZIP entry.");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(reason, problem.RootElement.GetProperty("reason").GetString());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false, "Timeout")]
    [DataRow(true, "ClientCancelled")]
    public async Task ToImagesPreservesOutcomeWhenSecondPageIsInterrupted(bool cancelClient, string outcome)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfTimeoutSeconds"] = cancelClient ? "30" : "4" });
        var marker = await ReplaceSecondPageRendererAsync(test, "exec sleep 60");
        using var form = PdfTestContext.FileForm(await test.CreatePagedPdfAsync(2));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = test.Client.PostAsync("/api/pdf/to-images?format=png&dpi=72", form, cancellation.Token);
        try
        {
            if (cancelClient)
            {
                while (!File.Exists(marker))
                {
                    Assert.IsFalse(request.IsCompleted, "The renderer must reach page two.");
                    await Task.Delay(20, cancellation.Token);
                }
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => { using var response = await request; });
            }
            else
            {
                using var response = await request;
                Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
                Assert.IsTrue(File.Exists(marker), "The renderer must reach page two.");
            }
        }
        finally { cancellation.Cancel(); }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            lock (test.Logs)
            {
                if (test.Logs.Any(log => log.StartsWith("PDF request ", StringComparison.Ordinal) &&
                    log.Contains($"Operation=to-images Outcome={outcome} ", StringComparison.Ordinal))) break;
            }
            await Task.Delay(20, deadline.Token);
        }
        var pid = await File.ReadAllTextAsync(marker);
        Assert.IsFalse(Directory.Exists("/proc/" + pid));
        test.AssertClean();
    }

    private static async Task<string> ReplaceSecondPageRendererAsync(PdfTestContext test, string action)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        var marker = Path.Combine(test.Root, "second-page.pid");
        var runner = Path.Combine(test.Root, "to-images-renderer.sh");
        TestExecutable.Write(runner, $$"""
            #!/bin/sh
            if [ "$5" = --page ] && [ "$6" = 2 ]; then
                printf '%s' "$$" > '{{marker}}'
                {{action}}
            fi
            exec '{{options.DotnetPath}}' "$@"
            """);
        options.DotnetPath = runner;
        return marker;
    }

    private static byte[] TextPdf(string text) => ContentPdf($"BT /F1 12 Tf 20 100 Td ({text}) Tj ET");

    private static byte[] WinAnsiTextPdf(string text) => CompressFixtures.Objects(
    [
        "<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray(),
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"u8.ToArray(),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"u8.ToArray(),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"u8.ToArray(),
        CompressFixtures.Stream("", Encoding.Latin1.GetBytes($"BT /F1 12 Tf 20 100 Td ({text}) Tj ET"))
    ]);

    private static string[][] ContractPages(string title, int pages) => Enumerable.Range(1, pages).Select(page =>
        Enumerable.Range(1, 25).Select(line =>
            FormattableString.Invariant($"{title} page {page:00} item {line:00} governs the annual term.")).ToArray()).ToArray();

    private static IEnumerable<string[]> NumberPages(string[][] pages, string format) =>
        pages.Select<string[], string[]>((page, i) => format switch
        {
            "dash" => [.. page, FormattableString.Invariant($" - {i + 1} - ")],
            "fraction" => [.. page, FormattableString.Invariant($" {i + 1} / {pages.Length} ")],
            _ => [FormattableString.Invariant($" {i + 1} "), .. page]
        });

    private static string ComparableText(IEnumerable<string[]> pages) =>
        string.Concat(pages.SelectMany(page => page)).Replace(" ", "", StringComparison.Ordinal);

    private static MultipartFormDataContent CompareForm(byte[] oldPdf, byte[] newPdf)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(oldPdf), "old", "old.pdf");
        form.Add(new ByteArrayContent(newPdf), "new", "new.pdf");
        return form;
    }

    private static byte[] PageTextPdf(IEnumerable<string[]> pages)
    {
        List<byte[]> objects = ["<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray(), [],
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"u8.ToArray()];
        var kids = new List<string>();
        foreach (var lines in pages)
        {
            var page = objects.Count + 1;
            kids.Add($"{page} 0 R");
            objects.Add(Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
                $"/Resources << /Font << /F1 3 0 R >> >> /Contents {page + 1} 0 R >>"));
            var content = $"BT /F1 12 Tf 40 780 Td ({string.Join(") Tj 0 -24 Td (", lines)}) Tj ET";
            objects.Add(CompressFixtures.Stream("", Encoding.ASCII.GetBytes(content)));
        }
        objects[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {kids.Count} >>");
        return CompressFixtures.Objects(objects);
    }

    private static byte[] ContentPdf(string content) => CompressFixtures.Objects(
    [
        "<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray(),
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"u8.ToArray(),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"u8.ToArray(),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"u8.ToArray(),
        CompressFixtures.Stream("", Encoding.ASCII.GetBytes(content))
    ]);

    private static string ExtractorPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/Amane.Pdf.TextExtractor/bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0/Amane.Pdf.TextExtractor.dll"));
}
