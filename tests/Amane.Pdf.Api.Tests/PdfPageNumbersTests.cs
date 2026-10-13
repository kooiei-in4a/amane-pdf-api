using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfPageNumbersTests
{
    private const string Endpoint = "/api/pdf/page-numbers";
    private const string Query = "position=bottom-center&format=number";

    [TestMethod]
    public async Task InvalidQueries_Return400BeforeReadingUploadOrRunningQpdf()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        string[] queries = ["", "position=bottom-center", "format=number", Query + "&unknown=1",
            "position=&format=number", "position=Bottom-center&format=number", "position=center&format=number",
            "position=bottom-center&format=", "position=bottom-center&format=Number", "position=bottom-center&format=other",
            Query + "&position=bottom-right", Query + "&Position=bottom-center", Query + "&format=dash",
            Query + "&from=1&From=2", Query + "&start=1&start=2", Query + "&size=small&size=large",
            Query + "&size=", Query + "&size=Medium", Query + "&size=huge"];
        queries = [.. queries, .. new[] { "from", "start" }.SelectMany(key =>
            new[] { "", "0", "01", "100000", "-1", "%2B1", "1.0", "1%20", "%EF%BC%91", "١", "1e2" }
                .Select(value => Query + "&" + key + "=" + value))];
        foreach (var query in queries)
        {
            // Invalid multipart would have a different title if upload parsing ran first.
            using var body = new StringContent("not multipart");
            using var response = await test.Client.PostAsync(Endpoint + "?" + query, body);
            using var problem = await Problem(test, response, HttpStatusCode.BadRequest);
            Assert.AreEqual("リクエストの形式が不正です。", problem.RootElement.GetProperty("title").GetString(), query);
        }
    }

    [TestMethod]
    [DataRow("number", "10", "11")]
    [DataRow("number-total", "10 / 11", "11 / 11")]
    [DataRow("dash", "- 10 -", "- 11 -")]
    public void Layer_CountsFromSelectedPage_UsesPhysicalSizesAndInvariantNumbers(string format, string first, string last)
    {
        var options = Parse("position=bottom-right&format=" + format + "&from=3&start=10&size=large");
        Assert.AreEqual((3, 4), options.ResolveRange(4));
        PdfcpuPageSize[] sizes = [new(842, 1191), new(283.46, 419.53)];
        var layer = options.CreateLayer(new("unused.pdf", "unused-layer.pdf", sizes, 4096, 4096));
        CollectionAssert.AreEqual(new[] { 1, 2 }, layer.Pages.Keys.ToArray());
        Assert.AreSame(sizes, layer.PageSizes);
        Assert.AreEqual(first, layer.Pages[1].Single().Value);
        Assert.AreEqual(last, layer.Pages[2].Single().Value);
        Assert.AreEqual(new PdfcpuText(first, Anchor: "br", Dx: -28, Dy: 28, FontSize: 12), layer.Pages[1].Single());
    }

    [TestMethod]
    public void Range_EnforcesFinalNumberBoundary_WithoutIntegerOverflow()
    {
        var options = Parse(Query + "&from=2&start=99998");
        Assert.AreEqual((2, 3), options.ResolveRange(3));
        Assert.ThrowsExactly<BadHttpRequestException>(() => options.ResolveRange(1));
        Assert.ThrowsExactly<BadHttpRequestException>(() => options.ResolveRange(4));
        Assert.ThrowsExactly<BadHttpRequestException>(() => options.ResolveRange(int.MaxValue));
        Assert.AreEqual((1, 1), Parse(Query + "&start=99999").ResolveRange(1));
        Assert.AreEqual((99999, 99999), Parse(Query + "&from=99999").ResolveRange(99999));
    }

    [TestMethod]
    [DataRow("number")]
    [DataRow("number-total")]
    [DataRow("dash")]
    public async Task Http_PreservesFirstPageContent_PageCountAndAttributes(string format)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = new OverlayJob(); await source.CreateInput(OverlayFixtures.Geometry());
        await using var test = new PdfTestContext();
        var input = await File.ReadAllBytesAsync(source.Files.InputPath);
        using var before = await Metadata(test, input);
        using var form = PdfTestContext.FileForm(input, "../../private-numbered.pdf");
        using var response = await test.Client.PostAsync(Endpoint + "?position=bottom-center&format=" + format + "&from=2&start=10", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("numbered.pdf", response.Content.Headers.ContentDisposition?.FileName);
        var output = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(output, 7);
        using var after = await Metadata(test, output);
        Assert.AreEqual(PageContent(before, 0), PageContent(after, 0), "The cover page must be unchanged.");
        for (var i = 1; i < 7; i++) StringAssert.Contains(PageContent(after, i), " Do", "Target page must draw the overlay.");
        using var original = new PdfPageBoxes(Encoding.UTF8.GetBytes(before.RootElement.GetRawText()), 32 * 1048576, 50000, default);
        using var final = new PdfPageBoxes(Encoding.UTF8.GetBytes(after.RootElement.GetRawText()), 32 * 1048576, 50000, default);
        for (var i = 0; i < 7; i++) Assert.IsTrue(original.Read(i, default).SameEffective(final.Read(i, default)), $"page {i + 1}");
        AssertNoExposure(test, await response.Content.ReadAsStringAsync());
        test.AssertClean();
    }

    [TestMethod]
    public async Task Http_KeepsBookmarkDestination_FormAndLink()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = new OverlayJob();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "clean-source.json")))!.AsObject();
        var objects = OverlayFixtures.Objects(json);
        objects["obj:3 0 R"]!["value"]!["/Annots"] = JsonNode.Parse("""["32 0 R","35 0 R"]""");
        objects["obj:35 0 R"] = JsonNode.Parse("""{"value":{"/Type":"/Annot","/Subtype":"/Link","/Rect":[20,40,80,60],"/A":{"/S":"/URI","/URI":"u:https://example.test/retained"}}}""");
        objects["trailer"]!["value"]!["/Size"] = 36;
        await source.CreateInput(json.ToJsonString());
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.FileForm(await File.ReadAllBytesAsync(source.Files.InputPath));
        using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var output = await Metadata(test, await response.Content.ReadAsByteArrayAsync(), allKeys: true);
        var data = output.RootElement.GetRawText();
        foreach (var marker in new[] { "OUTLINE_RETAINED", "FORM_NAME_RETAINED", "FORM_VALUE_RETAINED", "https://example.test/retained" })
            StringAssert.Contains(data, marker);
        Assert.AreEqual(1, output.RootElement.GetProperty("acroform").GetProperty("fields").GetArrayLength());
        Assert.AreEqual(output.RootElement.GetProperty("pages")[0].GetProperty("object").GetString(),
            output.RootElement.GetProperty("outlines")[0].GetProperty("dest")[0].GetString());
        test.AssertClean();
    }

    [TestMethod]
    public async Task Http_PageCountDependentErrors_Return400_And99999IsAccepted()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(3);
        foreach (var query in new[] { "from=4", "start=99998", "from=2&start=99999" })
        {
            using var form = PdfTestContext.FileForm(input);
            using var response = await test.Client.PostAsync(Endpoint + "?" + Query + "&" + query, form);
            using var problem = await Problem(test, response, HttpStatusCode.BadRequest);
            Assert.IsFalse(problem.RootElement.TryGetProperty("reason", out _));
        }
        using var accepted = PdfTestContext.FileForm(input);
        using var success = await test.Client.PostAsync(Endpoint + "?POSITION=bottom-center&FORMAT=number-total&from=3&start=99999", accepted);
        Assert.AreEqual(HttpStatusCode.OK, success.StatusCode);
        await test.AssertValidPdfAsync(await success.Content.ReadAsByteArrayAsync(), 3);
        test.AssertClean();
    }

    [TestMethod]
    public async Task Http_ThousandTargetLimit_CountsOnlyNumberedPages()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(1001);
        using (var form = PdfTestContext.FileForm(input))
        using (var response = await test.Client.PostAsync(Endpoint + "?" + Query, form))
        using (var problem = await Problem(test, response, HttpStatusCode.UnprocessableEntity))
            Assert.AreEqual("too-complex", problem.RootElement.GetProperty("reason").GetString());
        using var accepted = PdfTestContext.FileForm(input);
        using var success = await test.Client.PostAsync(Endpoint + "?" + Query + "&from=2", accepted);
        Assert.AreEqual(HttpStatusCode.OK, success.StatusCode);
        await test.AssertValidPdfAsync(await success.Content.ReadAsByteArrayAsync(), 1001);
        test.AssertClean();
    }

    [TestMethod]
    public async Task Http_InvalidPdfAndMultipart_AreRejectedWithoutExposure()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        byte[][] invalid = [[], Encoding.ASCII.GetBytes("PRIVATE_PDF_SENTINEL"), Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"),
            PdfTestContext.Fixture.AsSpan(0, 80).ToArray(),
            Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(PdfTestContext.Fixture).Replace("/Length 41", "/Length 39")),
            await test.CreateEncryptedPdfAsync("synthetic-password", "synthetic-owner")];
        foreach (var input in invalid)
        {
            using var form = PdfTestContext.FileForm(input);
            using var response = await test.Client.PostAsync(Endpoint + "?" + Query + "&from=99999", form);
            using var problem = await Problem(test, response, HttpStatusCode.UnprocessableEntity);
            Assert.IsFalse(problem.RootElement.TryGetProperty("reason", out _), "Input validation must precede page range validation.");
        }
        foreach (var kind in new[] { "missing", "password", "duplicate" })
        {
            using var form = kind switch
            {
                "missing" => PdfTestContext.FileForm(),
                "password" => PdfTestContext.Form(PdfTestContext.Fixture),
                _ => PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture)
            };
            using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
            using var problem = await Problem(test, response, HttpStatusCode.BadRequest);
        }
    }

    [TestMethod]
    public async Task Http_FileSizeBoundary_IsEnforced()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString() });
        using (var form = PdfTestContext.FileForm(PdfTestContext.Fixture))
        using (var response = await test.Client.PostAsync(Endpoint + "?" + Query, form))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = "/must-not-run/qpdf";
        using var oversized = PdfTestContext.FileForm([.. PdfTestContext.Fixture, (byte)'X']);
        using var rejected = await test.Client.PostAsync(Endpoint + "?" + Query, oversized);
        using var problem = await Problem(test, rejected, HttpStatusCode.RequestEntityTooLarge);
    }

    [TestMethod]
    public async Task Http_OverlayCapacityAndUnsupportedAttributes_KeepTheirReasons()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.OverlayJsonLimitBytes = 4096;
        using (var form = PdfTestContext.FileForm(await test.CreatePagedPdfAsync(20)))
        using (var response = await test.Client.PostAsync(Endpoint + "?" + Query, form))
        using (var problem = await Problem(test, response, HttpStatusCode.UnprocessableEntity))
            Assert.AreEqual("too-complex", problem.RootElement.GetProperty("reason").GetString());
        options.OverlayJsonLimitBytes = 32 * 1048576;
        using var source = new OverlayJob();
        var json = JsonNode.Parse(PdfcpuProcessor.StartupBlankJson)!.AsObject();
        OverlayFixtures.Page(json)["/Rotate"] = 45;
        await source.CreateInput(json.ToJsonString());
        using var unsupported = PdfTestContext.FileForm(await File.ReadAllBytesAsync(source.Files.InputPath));
        using var rejected = await test.Client.PostAsync(Endpoint + "?" + Query, unsupported);
        using var unsupportedProblem = await Problem(test, rejected, HttpStatusCode.UnprocessableEntity);
        Assert.AreEqual("unsupported-pdf", unsupportedProblem.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task Http_RunsInputCheckOnce_WithoutSeparatePageCountProbe()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = new OverlayJob();
        var calls = Path.Combine(source.Root, "calls");
        var wrapper = source.Script($"printf '%s %s\\n' \"$1\" \"$2\" >> '{calls}'\nexec qpdf \"$@\"");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var commands = await File.ReadAllLinesAsync(calls);
        Assert.AreEqual(1, commands.Count(call => call.StartsWith("--check ") && call.EndsWith("/input.pdf")));
        Assert.AreEqual(1, commands.Count(call => call.StartsWith("--is-encrypted ") && call.EndsWith("/input.pdf")));
        Assert.IsFalse(commands.Any(call => call.StartsWith("--show-npages ") && call.EndsWith("/input.pdf")));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("qpdf")]
    [DataRow("pdfcpu")]
    public async Task Http_ToolFailure_ReturnsFixed500_AndCleansJob(string tool)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var settings = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        if (tool == "qpdf") settings.QpdfPath = "/missing/private-tool";
        else settings.PdfcpuPath = "/missing/private-tool";
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
        using var problem = await Problem(test, response, HttpStatusCode.InternalServerError);
        Assert.AreEqual("PDF処理に失敗しました。", problem.RootElement.GetProperty("title").GetString());
        Assert.IsFalse(problem.RootElement.GetRawText().Contains("private-tool"));
        Assert.IsFalse(string.Join('\n', test.Logs).Contains("private-tool"));
    }

    [TestMethod]
    public async Task Http_TimeoutAndBusyPermit_CleanUpAndReleaseSharedSlot()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var blocker = new BlockingQpdf("--json=2");
        await using var test = new PdfTestContext(new()
        { ["Pdf:QpdfPath"] = blocker.Executable, ["Pdf:QpdfTimeoutSeconds"] = "2", ["Pdf:MaxConcurrentProcesses"] = "1" });
        using var firstForm = PdfTestContext.FileForm(PdfTestContext.Fixture);
        var firstRequest = test.Client.PostAsync(Endpoint + "?" + Query, firstForm);
        await blocker.WaitForJobsAsync(1);
        using (var busyForm = PdfTestContext.FileForm(PdfTestContext.Fixture))
        using (var busy = await test.Client.PostAsync("/api/pdf/optimize", busyForm))
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        using var first = await firstRequest;
        using var problem = await Problem(test, first, HttpStatusCode.GatewayTimeout);
        await blocker.AssertStoppedAsync();
        using var nextForm = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var next = await test.Client.PostAsync("/api/pdf/optimize", nextForm);
        Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    public async Task Http_Cancel_WaitsForProcessTreeAndDeletesJob()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var blocker = new BlockingQpdf("--json=2");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancellation = new CancellationTokenSource();
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        var request = test.Client.PostAsync(Endpoint + "?" + Query, form, cancellation.Token);
        await blocker.WaitForJobsAsync(1);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    public async Task Builder_ResolverReceivesCount_AndFailureRemovesIntermediates()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob(); await job.CreateInput(OverlayFixtures.Geometry());
        var failure = new BadHttpRequestException("Invalid range.");
        Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<BadHttpRequestException>(() => job.Builder.BuildAsync(job.Files,
            count => { Assert.AreEqual(7, count); throw failure; }, (_, _) => throw new AssertFailedException("Must not draw"), default)));
        CollectionAssert.AreEquivalent(new[] { job.Files.InputPath }, Directory.GetFiles(job.Files.DirectoryPath));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => job.Builder.BuildAsync(job.Files,
            _ => (0, 7), (_, _) => throw new AssertFailedException("Must not draw"), default));
        CollectionAssert.AreEquivalent(new[] { job.Files.InputPath }, Directory.GetFiles(job.Files.DirectoryPath));
    }

    private static PageNumberOptions Parse(string query)
    {
        var context = new DefaultHttpContext(); context.Request.QueryString = new("?" + query);
        return PageNumberOptions.Parse(context.Request.Query);
    }

    private static async Task<JsonDocument> Metadata(PdfTestContext test, byte[] bytes, bool allKeys = false)
    {
        var path = Path.Combine(test.Root, Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, bytes);
        string[] keys = allKeys ? [] : ["--json-key=pages", "--json-key=qpdf"];
        var result = await test.QpdfAsync(["--json=2", .. keys, "--json-stream-data=inline", "--decode-level=all", path]);
        Assert.AreEqual(0, result.ExitCode);
        return JsonDocument.Parse(result.Output);
    }

    private static string PageContent(JsonDocument document, int page)
    {
        var objects = document.RootElement.GetProperty("qpdf")[1];
        return string.Concat(document.RootElement.GetProperty("pages")[page].GetProperty("contents").EnumerateArray()
            .Select(reference => Encoding.ASCII.GetString(Convert.FromBase64String(objects.GetProperty("obj:" + reference.GetString())
                .GetProperty("stream").GetProperty("data").GetString()!))));
    }

    private static async Task<JsonDocument> Problem(PdfTestContext test, HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status, response.StatusCode, body);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertNoExposure(test, body);
        test.AssertClean();
        var json = JsonDocument.Parse(body);
        Assert.AreEqual((int)status, json.RootElement.GetProperty("status").GetInt32());
        return json;
    }

    private static void AssertNoExposure(PdfTestContext test, string body)
    {
        foreach (var marker in new[] { "private-numbered.pdf", "PRIVATE_PDF_SENTINEL", "synthetic-password", "synthetic-owner", test.TempRoot })
        {
            Assert.IsFalse(body.Contains(marker), marker);
            Assert.IsFalse(string.Join('\n', test.Logs).Contains(marker), marker);
        }
    }
}
