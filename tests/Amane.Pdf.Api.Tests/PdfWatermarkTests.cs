using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfWatermarkTests
{
    private const string Endpoint = "/api/pdf/watermark", Query = "style=diagonal&color=gray";

    [TestInitialize]
    public void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The watermark endpoint is Linux-only.");
    }

    [TestMethod]
    public async Task InvalidQueryPrecedesUploadAndTools()
    {
        await using var test = new PdfTestContext();
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = "/must-not-run/qpdf";
        foreach (var query in new[] { "", "style=diagonal", "color=gray", "style=&color=gray", "style=Diagonal&color=gray",
                     "style=diagonal&color=Gray", "style=unknown&color=gray", "style=diagonal&color=blue",
                     Query + "&style=corner", Query + "&Style=diagonal", Query + "&color=red", Query + "&text=SECRET",
                     Query + "&extra=1", "style=corner&color=" })
        {
            using var body = new StringContent("not multipart");
            using var response = await test.Client.PostAsync(Endpoint + "?" + query, body);
            using var problem = await Problem(test, response, HttpStatusCode.BadRequest);
            Assert.AreEqual("リクエストの形式が不正です。", problem.RootElement.GetProperty("title").GetString());
        }
    }

    [TestMethod]
    [DataRow("diagonal", "gray", "社外秘")] [DataRow("diagonal", "red", "取扱注意")]
    [DataRow("corner", "gray", "見本")] [DataRow("corner", "red", "社外秘")]
    [DataRow("diagonal", "gray", "DRAFT")] [DataRow("diagonal", "red", "DRAFT")]
    [DataRow("corner", "gray", "DRAFT")] [DataRow("corner", "red", "DRAFT")]
    public async Task AllStylesAndColorsPreserveGeometryAndDrawOnceOnEveryPage(string style, string color, string text)
    {
        using var source = new OverlayJob(); await source.CreateInput(OverlayFixtures.Geometry());
        await using var test = new PdfTestContext();
        var input = await File.ReadAllBytesAsync(source.Files.InputPath);
        using var before = await Metadata(test, input);
        using var form = Form(input, Encoding.UTF8.GetBytes(text), textFirst: true);
        using var response = await test.Client.PostAsync(Endpoint + $"?STYLE={style}&COLOR={color}", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("watermarked.pdf", response.Content.Headers.ContentDisposition?.FileName);
        var output = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(output, 7);
        using var after = await Metadata(test, output);
        using var original = new PdfPageBoxes(Encoding.UTF8.GetBytes(before.RootElement.GetRawText()), 32 * 1048576, 50000, default);
        using var final = new PdfPageBoxes(Encoding.UTF8.GetBytes(after.RootElement.GetRawText()), 32 * 1048576, 75000, default);
        for (var i = 0; i < 7; i++)
        {
            Assert.IsTrue(original.Read(i, default).SameEffective(final.Read(i, default)), $"page {i + 1}");
            var (dictionary, content) = LastDrawnForm(after, i);
            Assert.AreEqual(1, Regex.Matches(content, @"\bTj\b").Count, "Each page must own a distinct text stream.");
            var resources = dictionary.GetProperty("/Resources");
            if (style == "diagonal")
            {
                Assert.IsTrue(content.TrimStart().StartsWith("/AmaneAlpha gs"));
                var alpha = resources.GetProperty("/ExtGState").GetProperty("/AmaneAlpha");
                Assert.AreEqual(0.25, alpha.GetProperty("/ca").GetDouble());
                Assert.AreEqual(0.25, alpha.GetProperty("/CA").GetDouble());
            }
            else Assert.IsFalse(resources.TryGetProperty("/ExtGState", out _));
        }
        test.AssertClean();
        Assert.IsFalse(string.Join('\n', test.Logs).Contains(text));
    }

    [TestMethod]
    public async Task ImageIsDrawnBeforeWatermarkAndBookmarkFormAndLinkSurvive()
    {
        using var source = new OverlayJob();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "clean-source.json")))!.AsObject();
        var objects = OverlayFixtures.Objects(json);
        objects["obj:3 0 R"]!["value"]!["/Annots"] = JsonNode.Parse("""["32 0 R","35 0 R"]""");
        objects["obj:35 0 R"] = JsonNode.Parse("""{"value":{"/Type":"/Annot","/Subtype":"/Link","/Rect":[20,40,80,60],"/A":{"/S":"/URI","/URI":"u:https://example.test/retained"}}}""");
        objects["trailer"]!["value"]!["/Size"] = 36;
        await source.CreateInput(json.ToJsonString());
        await using var test = new PdfTestContext();
        using var form = Form(await File.ReadAllBytesAsync(source.Files.InputPath));
        using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var output = await Metadata(test, await response.Content.ReadAsByteArrayAsync(), allKeys: true);
        var data = output.RootElement.GetRawText();
        foreach (var marker in new[] { "OUTLINE_RETAINED", "FORM_NAME_RETAINED", "FORM_VALUE_RETAINED", "https://example.test/retained" })
            StringAssert.Contains(data, marker);
        Assert.AreEqual(1, output.RootElement.GetProperty("acroform").GetProperty("fields").GetArrayLength());
        Assert.AreEqual(output.RootElement.GetProperty("pages")[0].GetProperty("object").GetString(),
            output.RootElement.GetProperty("outlines")[0].GetProperty("dest")[0].GetString());
        var allObjects = output.RootElement.GetProperty("qpdf")[1];
        var page = output.RootElement.GetProperty("pages")[0];
        var pageDictionary = allObjects.GetProperty("obj:" + page.GetProperty("object").GetString()).GetProperty("value");
        var draws = Regex.Matches(PageContent(output, 0), @"(/\S+)\s+Do");
        Assert.AreEqual(2, draws.Count);
        var first = pageDictionary.GetProperty("/Resources").GetProperty("/XObject").GetProperty(draws[0].Groups[1].Value).GetString();
        StringAssert.Contains(allObjects.GetProperty("obj:" + first).GetRawText(), "/XObject");
        StringAssert.Contains(LastDrawnForm(output, 0).Content, "/AmaneAlpha gs");
        test.AssertClean();
    }

    [TestMethod]
    public async Task TextIsLiteralPrivateJsonAndAbsentFromArgvErrorsAndLogs()
    {
        const string text = "-DRAFT, 社外秘";
        using var source = new OverlayJob();
        var calls = Path.Combine(source.Root, "calls");
        var copy = Path.Combine(source.Root, "layer.json");
        var mode = Path.Combine(source.Root, "mode");
        var wrapper = source.Script($"printf '%s\\n' \"$@\" > '{calls}'\nfor arg do\n case \"$arg\" in */pdfcpu-layer.json) cp \"$arg\" '{copy}'; stat -c %a \"$arg\" > '{mode}';; esac\ndone\nexec '{source.Settings.PdfcpuPath}' \"$@\"");
        await using var test = new PdfTestContext();
        var settings = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        settings.PdfcpuPath = wrapper;
        using (var form = Form(PdfTestContext.Fixture, Encoding.UTF8.GetBytes(text)))
        using (var response = await test.Client.PostAsync(Endpoint + "?" + Query, form))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse((await File.ReadAllTextAsync(calls)).Contains(text));
        Assert.AreEqual("600", (await File.ReadAllTextAsync(mode)).Trim());
        using var layer = JsonDocument.Parse(await File.ReadAllBytesAsync(copy));
        Assert.AreEqual(text, layer.RootElement.GetProperty("pages").GetProperty("1").GetProperty("content")
            .GetProperty("text")[0].GetProperty("value").GetString());
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity, HttpStatusCode.InternalServerError })
        {
            if (status == HttpStatusCode.InternalServerError) settings.PdfcpuPath = "/missing/private-tool";
            using var form = Form(status == HttpStatusCode.UnprocessableEntity ? "PRIVATE_PDF_SENTINEL"u8.ToArray() : PdfTestContext.Fixture,
                Encoding.UTF8.GetBytes(status == HttpStatusCode.BadRequest ? text + "%" : text));
            using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
            using var problem = await Problem(test, response, status);
            Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains(text));
        }
        Assert.IsFalse(string.Join('\n', test.Logs).Contains(text));
    }

    [TestMethod]
    public async Task MultipartRejectsMissingDuplicateUnknownAndMisclassifiedParts()
    {
        await using var test = new PdfTestContext();
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = "/must-not-run/qpdf";
        foreach (var kind in new[] { "missing-file", "missing-text", "duplicate-file", "duplicate-text", "password", "charset", "file-field", "text-file" })
        {
            using var form = new MultipartFormDataContent();
            if (kind != "missing-file")
            {
                if (kind == "file-field") form.Add(new ByteArrayContent(PdfTestContext.Fixture), "file");
                else form.Add(new ByteArrayContent(PdfTestContext.Fixture), "file", "input.pdf");
            }
            if (kind != "missing-text")
            {
                if (kind == "text-file") form.Add(new StringContent("社外秘"), "text", "text.txt");
                else form.Add(new StringContent("社外秘"), "text");
            }
            if (kind == "duplicate-file") form.Add(new ByteArrayContent(PdfTestContext.Fixture), "file", "other.pdf");
            if (kind == "duplicate-text") form.Add(new StringContent("見本"), "text");
            if (kind is "password" or "charset") form.Add(new StringContent("PRIVATE_FIELD"), kind == "charset" ? "_charset_" : "password");
            using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
            using var problem = await Problem(test, response, HttpStatusCode.BadRequest);
        }
    }

    [TestMethod]
    public async Task Utf8ByteBoundaryCharsetIgnoredAndInvalidTextPrecedesTools()
    {
        await using var test = new PdfTestContext();
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("A\u3099", 30)));
        using (var form = Form(PdfTestContext.Fixture, text))
        using (var response = await test.Client.PostAsync(Endpoint + "?" + Query, form))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = "/must-not-run/qpdf";
        foreach (var bytes in new byte[][] { [.. text, (byte)'A'], new byte[4096], [], " 　"u8.ToArray(), "%"u8.ToArray(),
                     Convert.FromHexString("EDA080"), Convert.FromHexString("C0AF"), "丢"u8.ToArray() })
        {
            using var form = Form(PdfTestContext.Fixture, bytes, textFirst: true);
            using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
            using var problem = await Problem(test, response, HttpStatusCode.BadRequest);
            Assert.IsFalse(problem.RootElement.TryGetProperty("reason", out _));
        }
    }

    [TestMethod]
    public async Task SizeLimitsPrecedeSemanticTextValidationAndTools()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString() });
        using (var form = Form(PdfTestContext.Fixture))
        using (var response = await test.Client.PostAsync(Endpoint + "?" + Query, form))
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = "/must-not-run/qpdf";
        using var oversized = Form([.. PdfTestContext.Fixture, (byte)'X'], "%"u8.ToArray(), textFirst: true);
        using var rejected = await test.Client.PostAsync(Endpoint + "?" + Query, oversized);
        using var problem = await Problem(test, rejected, HttpStatusCode.RequestEntityTooLarge);
    }

    [TestMethod]
    public async Task ThousandPagesSucceedAndThousandAndOneIsTooComplex()
    {
        await using var test = new PdfTestContext();
        using (var form = Form(await test.CreatePagedPdfAsync(1000)))
        using (var response = await test.Client.PostAsync(Endpoint + "?" + Query, form))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1000);
        }
        using var excessive = Form(await test.CreatePagedPdfAsync(1001));
        using var rejected = await test.Client.PostAsync(Endpoint + "?" + Query, excessive);
        using var problem = await Problem(test, rejected, HttpStatusCode.UnprocessableEntity);
        Assert.AreEqual("too-complex", problem.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task InputCheckRunsOnceAndNoSeparatePageCountProbe()
    {
        using var source = new OverlayJob();
        var calls = Path.Combine(source.Root, "calls");
        var wrapper = source.Script($"printf '%s %s\\n' \"$1\" \"$2\" >> '{calls}'\nexec qpdf \"$@\"");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
        using var form = Form(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var commands = await File.ReadAllLinesAsync(calls);
        Assert.AreEqual(1, commands.Count(call => call.StartsWith("--check ") && call.EndsWith("/input.pdf")));
        Assert.IsFalse(commands.Any(call => call.StartsWith("--show-npages ") && call.EndsWith("/input.pdf")));
        test.AssertClean();
    }

    [TestMethod]
    public async Task PdfValidationCapacityUnsupportedAndToolFailuresKeepErrorContracts()
    {
        await using var test = new PdfTestContext();
        foreach (var input in new byte[][] { [], "%PDF-1.4\n%%EOF"u8.ToArray(), PdfTestContext.Fixture.AsSpan(0, 80).ToArray(),
                     await test.CreateEncryptedPdfAsync("synthetic-password", "synthetic-owner") })
        {
            using var form = Form(input);
            using var response = await test.Client.PostAsync(Endpoint + "?" + Query, form);
            using var problem = await Problem(test, response, HttpStatusCode.UnprocessableEntity);
            Assert.IsFalse(problem.RootElement.TryGetProperty("reason", out _));
        }
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.OverlayJsonLimitBytes = 4096;
        using (var form = Form(await test.CreatePagedPdfAsync(20)))
        using (var response = await test.Client.PostAsync(Endpoint + "?" + Query, form))
        using (var problem = await Problem(test, response, HttpStatusCode.UnprocessableEntity))
            Assert.AreEqual("too-complex", problem.RootElement.GetProperty("reason").GetString());
        options.OverlayJsonLimitBytes = 32 * 1048576;
        using var source = new OverlayJob();
        var json = JsonNode.Parse(PdfcpuProcessor.StartupBlankJson)!.AsObject();
        OverlayFixtures.Page(json)["/Rotate"] = 45;
        await source.CreateInput(json.ToJsonString());
        using (var form = Form(await File.ReadAllBytesAsync(source.Files.InputPath)))
        using (var response = await test.Client.PostAsync(Endpoint + "?" + Query, form))
        using (var problem = await Problem(test, response, HttpStatusCode.UnprocessableEntity))
            Assert.AreEqual("unsupported-pdf", problem.RootElement.GetProperty("reason").GetString());
        options.QpdfPath = "/missing/private-tool";
        using var failure = Form(PdfTestContext.Fixture);
        using var failed = await test.Client.PostAsync(Endpoint + "?" + Query, failure);
        using var internalProblem = await Problem(test, failed, HttpStatusCode.InternalServerError);
        Assert.AreEqual("PDF処理に失敗しました。", internalProblem.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task TimeoutReleasesSharedSlotAndCancelStopsProcessTree()
    {
        using var blocker = new BlockingQpdf("--json=2");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "2", ["Pdf:MaxConcurrentProcesses"] = "1" });
        using var firstForm = Form(PdfTestContext.Fixture);
        var request = test.Client.PostAsync(Endpoint + "?" + Query, firstForm);
        await blocker.WaitForJobsAsync(1);
        using (var busyForm = Form(PdfTestContext.Fixture))
        using (var busy = await test.Client.PostAsync(Endpoint + "?" + Query, busyForm))
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        using (var response = await request)
        using (var problem = await Problem(test, response, HttpStatusCode.GatewayTimeout)) { }
        await blocker.AssertStoppedAsync();
        using (var nextForm = PdfTestContext.FileForm(PdfTestContext.Fixture))
        using (var next = await test.Client.PostAsync("/api/pdf/optimize", nextForm))
            Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
        using var cancellation = new CancellationTokenSource();
        using var cancelledForm = Form(PdfTestContext.Fixture);
        var cancelled = test.Client.PostAsync(Endpoint + "?" + Query, cancelledForm, cancellation.Token);
        await blocker.WaitForJobsAsync(2);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    private static MultipartFormDataContent Form(byte[] input, byte[]? text = null, bool textFirst = false)
    {
        var form = new MultipartFormDataContent();
        var field = new ByteArrayContent(text ?? "社外秘"u8.ToArray());
        field.Headers.ContentType = new("text/plain") { CharSet = "iso-8859-1" };
        if (textFirst) form.Add(field, "text");
        form.Add(new ByteArrayContent(input), "file", "../../private-watermark.pdf");
        if (!textFirst) form.Add(field, "text");
        return form;
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
            .Select(reference => StreamText(objects.GetProperty("obj:" + reference.GetString()))));
    }

    private static string StreamText(JsonElement obj)
        => Encoding.ASCII.GetString(Convert.FromBase64String(obj.GetProperty("stream").GetProperty("data").GetString()!));

    private static (JsonElement Dictionary, string Content) LastDrawnForm(JsonDocument document, int page)
    {
        var objects = document.RootElement.GetProperty("qpdf")[1];
        var dictionary = objects.GetProperty("obj:" + document.RootElement.GetProperty("pages")[page].GetProperty("object").GetString()).GetProperty("value");
        var draw = Regex.Matches(PageContent(document, page), @"(/\S+)\s+Do")[^1];
        var reference = dictionary.GetProperty("/Resources").GetProperty("/XObject").GetProperty(draw.Groups[1].Value).GetString();
        var form = objects.GetProperty("obj:" + reference);
        return (form.GetProperty("stream").GetProperty("dict"), StreamText(form));
    }

    private static async Task<JsonDocument> Problem(PdfTestContext test, HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status, response.StatusCode, body);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        foreach (var marker in new[] { "private-watermark.pdf", "PRIVATE_PDF_SENTINEL", "PRIVATE_FIELD", "private-tool", test.TempRoot })
        {
            Assert.IsFalse(body.Contains(marker), marker);
            Assert.IsFalse(string.Join('\n', test.Logs).Contains(marker), marker);
        }
        test.AssertClean();
        return JsonDocument.Parse(body);
    }
}
