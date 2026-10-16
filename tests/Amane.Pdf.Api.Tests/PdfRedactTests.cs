using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfRedactTests
{
    internal const string Regions = "[" + RedactRegionsTests.Region + "]";
    internal static MultipartFormDataContent Form(byte[]? pdf = null, string? regions = Regions)
    {
        var form = new MultipartFormDataContent();
        if (pdf is not null) form.Add(new ByteArrayContent(pdf), "file", "../../private-name.pdf");
        if (regions is not null) form.Add(new StringContent(regions, Encoding.UTF8), "regions");
        return form;
    }
    internal static async Task<HttpResponseMessage> Post(PdfTestContext test, byte[] pdf, string regions = Regions, CancellationToken token = default)
    {
        using var form = Form(pdf, regions);
        return await test.Client.PostAsync("/api/pdf/redact", form, token);
    }
    internal static async Task<byte[]> JsonPdf(PdfTestContext test, string fixture, Action<JsonObject>? edit = null)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "redact-" + fixture + ".json")))!.AsObject();
        edit?.Invoke(doc);
        var json = Path.Combine(test.Root, Guid.NewGuid().ToString("N") + ".json");
        var pdf = Path.ChangeExtension(json, ".pdf");
        await File.WriteAllTextAsync(json, doc.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.AreEqual(0, (await test.QpdfAsync("--json-input", json, pdf)).ExitCode);
        return await File.ReadAllBytesAsync(pdf);
    }
    internal static async Task<string> Expanded(PdfTestContext test, byte[] pdf)
    {
        var path = Path.Combine(test.Root, Guid.NewGuid().ToString("N") + ".pdf");
        var expanded = Path.ChangeExtension(path, ".expanded.pdf");
        await File.WriteAllBytesAsync(path, pdf);
        Assert.AreEqual(0, (await test.QpdfAsync("--qdf", "--object-streams=disable", "--decode-level=all", path, expanded)).ExitCode);
        return Encoding.Latin1.GetString(await File.ReadAllBytesAsync(expanded));
    }

    [TestMethod]
    public async Task Redact_RemovesEveryMarkerIncludingAttachmentAppearance_AndKeepsPublicText()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var input = await JsonPdf(test, "markers");
        using var response = await Post(test, input);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("redacted.pdf", response.Content.Headers.ContentDisposition?.FileName);
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Redacted-Pages").Single());
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Rasterized-Pages").Single());
        var output = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(output, 3);
        var text = await Expanded(test, output);
        Assert.IsFalse(text.Contains("MK_", StringComparison.Ordinal));
        StringAssert.Contains(text, "PUBLIC_P2"); StringAssert.Contains(text, "PUBLIC_NESTED");
        foreach (var key in new[] { "/Annots", "/PageLabels", "/AcroForm", "/Outlines", "/StructTreeRoot", "/Metadata", "/Info", "/EmbeddedFile" })
            Assert.IsFalse(text.Contains(key, StringComparison.Ordinal), key);
        test.AssertClean();
    }

    [TestMethod]
    public async Task Redact_SharedEmbeddedJapaneseFont_RasterizesSecondPageAndDropsGlyphData()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var blank = Path.Combine(test.Root, "shared-blank.pdf");
        var layer = Path.Combine(test.Root, "shared-layer.json");
        var source = Path.Combine(test.Root, "shared.pdf");
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        Assert.AreEqual(0, (await test.QpdfAsync("--json-input", Path.Combine(fixture, "redact-blank2.json"), blank)).ExitCode);
        File.Copy(Path.Combine(fixture, "redact-shared-layer.json"), layer);
        var settings = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        var result = await ExternalProcessRunner.RunAsync(ProcessMemoryLimits.CreateRequest(settings.PrlimitPath,
            settings.PdfcpuPath, ["-c", settings.PdfcpuConfigDir, "--offline", "create", layer, blank, source],
            settings.PdfcpuAddressSpaceLimitBytes, null, new Dictionary<string, string> { ["HOME"] = test.Root, ["GOMEMLIMIT"] = "200MiB" }), CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode);
        using var response = await Post(test, await File.ReadAllBytesAsync(source));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("1,2", response.Headers.GetValues("X-Pdf-Rasterized-Pages").Single());
        var expanded = await Expanded(test, await response.Content.ReadAsByteArrayAsync());
        Assert.IsFalse(expanded.Contains("/FontFile") || expanded.Contains("/ToUnicode") || expanded.Contains("BIZUDPGothic"));
        test.AssertClean();
    }

    [TestMethod]
    public async Task NeedAppearances_ValueIsGeneratedOnNonTargetPage()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var source = await JsonPdf(test, "needap", doc =>
        {
            var objects = RedactDocumentTests.Objects(doc);
            objects["obj:2 0 R"]!["value"]!["/Count"] = 2;
            objects["obj:2 0 R"]!["value"]!["/Kids"]!.AsArray().Add("7 0 R");
            objects["obj:7 0 R"] = JsonNode.Parse("""{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,595,842],"/Resources":{}}}""");
            objects["trailer"]!["value"]!["/Size"] = 8;
        });
        using var response = await Post(test, source, Regions.Replace("\"page\":1", "\"page\":2"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("2", response.Headers.GetValues("X-Pdf-Rasterized-Pages").Single());
        StringAssert.Contains(await Expanded(test, await response.Content.ReadAsByteArrayAsync()), "FIELD VALUE 12345");
        test.AssertClean();
    }

    [TestMethod]
    public async Task PruningWarning_IsUnsupported_AndNeverSendsPdf()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        // The deliberately broken lexical stream already warns in --check.
        // Bypass that one check only, to exercise the real pruning warning.
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "case \"$1:$2\" in --check:*/input.pdf) exit 0;; esac\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await Post(test, await JsonPdf(test, "prune-warning"));
        await Problem(test, response, 422, "unsupported-pdf");
    }

    [TestMethod]
    [DataRow(2)] [DataRow(3)]
    public async Task PruningNonzeroExit_IsUnsupported_EvenWhenPartialOutputReachesFileLimit(int exit)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext(new()
            { ["Pdf:MaxRedactInputBytes"] = "2048", ["Pdf:RedactJobLimitBytes"] = "16384" });
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "if [ \"$1\" = --empty ]; then\nfor output do :; done\n" +
            "limit=$(awk '$1==\"Max\" && $2==\"file\" && $3==\"size\" {print $4}' /proc/self/limits)\n" +
            $"head -c \"$limit\" /dev/zero > \"$output\"\nexit {exit}\nfi\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await Post(test, PdfTestContext.Fixture);
        await Problem(test, response, 422, "unsupported-pdf");
    }

    [TestMethod]
    [DataRow("Pdf:RedactJsonLimitBytes", "128", "too-complex")]
    [DataRow("Pdf:RedactOutputLimitBytes", "128", "output-too-large")]
    [DataRow("Pdf:MaxRedactReplacementBytes", "1", "output-too-large")]
    public async Task CapacityFailures_HaveRedactReasonsAndNoHeaders(string key, string value, string reason)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext(new() { [key] = value });
        using var response = await Post(test, PdfTestContext.Fixture);
        await Problem(test, response, 422, reason);
    }

    [TestMethod]
    public async Task TooManySharedPages_Is422_AndSingleSmallPageIsUnsupported()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        using (var response = await Post(test, await test.CreatePagedPdfAsync(31)))
            await Problem(test, response, 422, "too-many-pages");
        var tiny = CompressFixtures.Objects([
            CompressFixtures.Text("<< /Type /Catalog /Pages 2 0 R >>"),
            CompressFixtures.Text("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            CompressFixtures.Text("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 0.5 100] /Resources << >> >>")]);
        using var small = await Post(test, tiny);
        await Problem(test, small, 422, "unsupported-pdf");
    }

    [TestMethod]
    public async Task BadMultipartQueryRegionsAndPageNumbers_Are400_WithoutToolOrHeaderLeaks()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        using (var form = Form(PdfTestContext.Fixture))
        using (var query = await test.Client.PostAsync("/api/pdf/redact?unexpected=1", form))
            await Problem(test, query, 400);
        foreach (var form in new[] { Form(null), Form(PdfTestContext.Fixture, null), Form(PdfTestContext.Fixture, "[]"),
                     Form(PdfTestContext.Fixture, Regions.Replace("\"page\":1", "\"page\":2")),
                     Form(PdfTestContext.Fixture, Regions.PadRight(16385)) })
        {
            using (form)
            using (var response = await test.Client.PostAsync("/api/pdf/redact", form))
                await Problem(test, response, 400);
        }
        using var duplicate = Form(PdfTestContext.Fixture);
        duplicate.Add(new StringContent(Regions), "regions");
        using (var response = await test.Client.PostAsync("/api/pdf/redact", duplicate)) await Problem(test, response, 400);
        using var fileRegions = Form(PdfTestContext.Fixture, null);
        fileRegions.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(Regions)), "regions", "regions.json");
        using (var response = await test.Client.PostAsync("/api/pdf/redact", fileRegions)) await Problem(test, response, 400);
        using var unknown = Form(PdfTestContext.Fixture); unknown.Add(new StringContent("sentinel"), "text");
        using (var response = await test.Client.PostAsync("/api/pdf/redact", unknown)) await Problem(test, response, 400);
    }

    [TestMethod]
    public async Task ExactUploadAndTextLimits_AreAccepted_AndFileRequestOverflowIs413()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:MaxRedactInputBytes"] = PdfTestContext.Fixture.Length.ToString() });
        using (var response = await Post(test, PdfTestContext.Fixture, Regions.PadRight(16384)))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using (var response = await Post(test, [.. PdfTestContext.Fixture, 0])) await Problem(test, response, 413);
        using var normal = Form(PdfTestContext.Fixture);
        var bytes = await normal.ReadAsByteArrayAsync();
        using var oversized = new ByteArrayContent([.. bytes, .. new byte[65536]]);
        oversized.Headers.ContentType = normal.Headers.ContentType;
        using (var response = await test.Client.PostAsync("/api/pdf/redact", oversized)) await Problem(test, response, 413);
    }

    [TestMethod]
    public async Task OccupiedRenderSlot_Returns503BeforeReadingUpload_AndReleasesAfterFailure()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var slot = test.Factory.Services.GetRequiredService<RenderSlot>();
        using (slot.TryAcquire())
        using (var form = Form(null))
        using (var response = await test.Client.PostAsync("/api/pdf/redact", form)) await Problem(test, response, 503);
        using (var response = await Post(test, "bad PDF"u8.ToArray())) await Problem(test, response, 422);
        using var available = slot.TryAcquire(); Assert.IsNotNull(available);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ActualRenderer_TimeoutAndCancel_RemoveProcessAndWholeJob(bool cancel)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var settings = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        var pidPath = Path.Combine(test.Root, "renderer.pid");
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "input=\nprevious=\nfor argument do\n[ \"$previous\" = --input ] && input=$argument\nprevious=$argument\ndone\n" +
            "if [ -n \"$input\" ]; then rm \"$input\"; mkfifo \"$input\"; " + $"printf '%s' \"$$\" > '{pidPath}'; fi\n" +
            $"exec '{settings.DotnetPath}' \"$@\"\n");
        settings.DotnetPath = wrapper; settings.QpdfTimeoutSeconds = cancel ? 30 : 2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = Post(test, PdfTestContext.Fixture, token: cts.Token);
        for (var i = 0; i < 400 && !File.Exists(pidPath); i++) await Task.Delay(10);
        Assert.IsTrue(File.Exists(pidPath));
        var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
        if (cancel)
        {
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        }
        else
        {
            using var response = await request; await Problem(test, response, 504);
        }
        for (var i = 0; i < 200 && (Directory.Exists($"/proc/{pid}") || Directory.Exists(test.TempRoot) && Directory.EnumerateFileSystemEntries(test.TempRoot).Any()); i++)
            await Task.Delay(10);
        Assert.IsFalse(Directory.Exists($"/proc/{pid}")); test.AssertClean();
        using var lease = test.Factory.Services.GetRequiredService<RenderSlot>().TryAcquire(); Assert.IsNotNull(lease);
    }

    [TestMethod]
    [DataRow("content")] [DataRow("catalog")] [DataRow("image")] [DataRow("annotations")] [DataRow("content-array")]
    public async Task CorruptedFinalUpdate_Is500EvenIfQpdfAcceptsIt(string mutation)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var script = Path.Combine(test.Root, "mutate.py");
        await File.WriteAllTextAsync(script, """
            import base64,json,sys
            path,mutation=sys.argv[1:]
            data=json.load(open(path))
            for wrapper in data["qpdf"][1].values():
                value=wrapper.get("value",{})
                stream=wrapper.get("stream",{})
                if mutation=="content" and "data" in stream: stream["data"]=base64.b64encode(b"q 1 0 0 1 0 0 cm /Im0 Do Q").decode()
                if mutation=="image" and "datafile" in stream: stream["dict"]["/Width"]+=1
                if mutation=="catalog" and value.get("/Type")=="/Catalog": value["/Leak"]="u:SENTINEL"
                if value.get("/Type")=="/Page":
                    if mutation=="annotations": value["/Annots"]=[]
                    if mutation=="content-array": value["/Contents"]=[value["/Contents"]]
            json.dump(data,open(path,"w"))
            """);
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "case \"$2\" in --update-from-json=*) " + $"python3 '{script}' \"${{2#--update-from-json=}}\" '{mutation}';; esac\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await Post(test, PdfTestContext.Fixture);
        await Problem(test, response, 500);
    }

    [TestMethod]
    public async Task StreamingRequestOverflow_AndEncryptedPdf_AreRejectedAndCleaned()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:MaxRedactInputBytes"] = PdfTestContext.Fixture.Length.ToString() });
        using var form = Form(PdfTestContext.Fixture);
        var bytes = await form.ReadAsByteArrayAsync();
        using var body = new ByteArrayContent([.. bytes, .. new byte[65536]]);
        body.Headers.ContentType = form.Headers.ContentType;
        using var streaming = new StreamingContent(body);
        using (var response = await test.Client.PostAsync("/api/pdf/redact", streaming)) await Problem(test, response, 413);
        // Restore the normal limit: the encrypted fixture has a larger envelope.
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.MaxRedactInputBytes = 30 * 1048576;
        using var encrypted = await Post(test, await test.CreateEncryptedPdfAsync("synthetic-user", "synthetic-owner"));
        await Problem(test, encrypted, 422);
    }

    [TestMethod]
    [DataRow("MaxRedactInputBytes", "0")] [DataRow("MaxRedactInputBytes", "31457281")]
    [DataRow("MaxRedactReplacementBytes", "0")] [DataRow("MaxRedactReplacementBytes", "31457281")]
    [DataRow("RedactOutputLimitBytes", "0")] [DataRow("RedactOutputLimitBytes", "65011713")]
    [DataRow("RedactJsonLimitBytes", "0")] [DataRow("RedactJsonLimitBytes", "33554433")]
    [DataRow("RedactJobLimitBytes", "0")] [DataRow("RedactJobLimitBytes", "130023425")]
    [DataRow("RedactJobLimitBytes", "31457279")]
    public void InvalidRedactSettings_PreventStartup(string key, string value)
    {
        if (!PdfCompressTests.Linux()) return;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Pdf:" + key] = value })));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    private sealed class StreamingContent : HttpContent
    {
        private readonly HttpContent source;
        internal StreamingContent(HttpContent source)
        {
            this.source = source;
            foreach (var header in source.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => source.CopyToAsync(stream);
    }

    internal static async Task Problem(PdfTestContext test, HttpResponseMessage response, int status, string? reason = null)
    {
        await PdfCleanTests.ProblemAsync(test, response, status, reason);
        Assert.IsFalse(response.Headers.Contains("X-Pdf-Redacted-Pages") || response.Headers.Contains("X-Pdf-Rasterized-Pages"));
    }

    [TestMethod]
    [DataRow("dct-baseline90", 32, 128)] [DataRow("dct-progressive90", 32, 128)]
    [DataRow("jpx-rgba30", 32, 128)] [DataRow("jpx-gray30", 32, 128)] [DataRow("jbig2-1000", 0, 255)]
    public async Task RealEncodedImages_AtMeasuredThreshold_DrawCorrectly_AndLargerDimensionsAre422(string fixture, int center, int outside)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        using var response = await Post(test, await JsonPdf(test, fixture));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var pdf = Path.Combine(test.Root, "threshold-output.pdf");
        await File.WriteAllBytesAsync(pdf, await response.Content.ReadAsByteArrayAsync());
        var metadata = await test.QpdfAsync("--json=2", "--json-key=qpdf", "--json-stream-data=inline", "--decode-level=none", pdf);
        using var document = System.Text.Json.JsonDocument.Parse(metadata.Output);
        var image = document.RootElement.GetProperty("qpdf")[1].EnumerateObject().Select(p => p.Value)
            .Single(value => value.TryGetProperty("stream", out var stream) && stream.GetProperty("dict").TryGetProperty("/Subtype", out var type) && type.GetString() == "/Image");
        var jpeg = Path.Combine(test.Root, "threshold.jpg");
        await File.WriteAllBytesAsync(jpeg, image.GetProperty("stream").GetProperty("data").GetBytesFromBase64());
        var decoded = await ExternalProcessRunner.RunAsync(new("djpeg", ["-scale", "1/8", "-pnm", "-rgb", jpeg], StdoutLimit: 751000), CancellationToken.None);
        Assert.AreEqual(0, decoded.ExitCode); Assert.IsNotNull(decoded.Stdout);
        var header = "P6\n500 500\n255\n"u8.ToArray();
        Assert.IsTrue(decoded.Stdout.AsSpan().StartsWith(header));
        foreach (var (coordinate, expected) in new[] { (250, center), (375, outside) })
            for (var channel = 0; channel < 3; channel++)
                Assert.IsTrue(Math.Abs(decoded.Stdout[header.Length + (coordinate * 500 + coordinate) * 3 + channel] - expected) <= 5);
        using var excessive = await Post(test, await JsonPdf(test, fixture, root =>
        {
            var dictionary = RedactDocumentTests.Objects(root)["obj:5 0 R"]!["stream"]!["dict"]!;
            dictionary["/Width"] = dictionary["/Width"]!.GetValue<int>() + 1;
        }));
        await Problem(test, excessive, 422, "too-complex");
    }

    [TestMethod]
    [DataRow("3 0 R")] [DataRow("81 0 R")] [DataRow("80 0 R")]
    [DataRow("catalog")] [DataRow("pages")]
    public async Task NonTargetPrivateReferences_CannotRetainOriginalPageContentOrFont(string reference)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var root = RedactDocumentTests.Fixture(2);
        var objects = RedactDocumentTests.Objects(root);
        RedactDocumentTests.Page(root, 1)["/Resources"] = JsonNode.Parse("""{"/Font":{"/F":"80 0 R"}}""");
        RedactDocumentTests.Page(root, 1)["/Contents"] = "81 0 R";
        RedactDocumentTests.Page(root, 2)["/Private"] = reference is "catalog" or "pages" ? "82 0 R" : reference;
        if (reference is "catalog" or "pages") objects["obj:82 0 R"] = new JsonObject { ["value"] = new JsonObject
            { ["/Type"] = reference == "catalog" ? "/Catalog" : "/Pages", ["/Private"] = "81 0 R" } };
        objects["obj:80 0 R"] = JsonNode.Parse("""{"value":{"/Type":"/Font","/Subtype":"/Type1","/BaseFont":"/Helvetica"}}""");
        objects["obj:81 0 R"] = new JsonObject { ["stream"] = new JsonObject { ["dict"] = new JsonObject(),
            ["data"] = Convert.ToBase64String("BT /F 12 Tf 50 700 Td (SENTINEL_TARGET_BODY) Tj ET"u8) } };
        var json = Path.Combine(test.Root, "private-reference.json"); var pdf = Path.ChangeExtension(json, ".pdf");
        await File.WriteAllTextAsync(json, root.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.AreEqual(0, (await test.QpdfAsync("--json-input", json, pdf)).ExitCode);
        using var response = await Post(test, await File.ReadAllBytesAsync(pdf));
        if (reference == "pages" && response.StatusCode == HttpStatusCode.OK)
        {
            // qpdf normalizes a Pages dictionary and can discard this private edge
            // before the application sees it. Successful output must still be safe.
            Assert.IsFalse((await Expanded(test, await response.Content.ReadAsByteArrayAsync())).Contains("SENTINEL_TARGET_BODY"));
            test.AssertClean();
        }
        else await Problem(test, response, 422, "unsupported-pdf");
    }
}
