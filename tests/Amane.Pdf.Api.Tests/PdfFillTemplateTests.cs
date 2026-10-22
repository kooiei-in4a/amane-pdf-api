using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfFillTemplateTests
{
    internal const string Endpoint = "/api/pdf/fill-template";
    internal static MultipartFormDataContent Form(byte[]? pdf = null, byte[]? fields = null, bool fieldsFirst = false)
    {
        var form = new MultipartFormDataContent();
        void File() => form.Add(new ByteArrayContent(pdf ?? PdfTestContext.Fixture), "file", "private-template.pdf");
        void Fields() => form.Add(new ByteArrayContent(fields ?? Encoding.UTF8.GetBytes("[" + FillTemplateFieldsTests.Field + "]")), "fields");
        if (fieldsFirst) { Fields(); File(); } else { File(); Fields(); }
        return form;
    }

    [TestMethod]
    public async Task Http_StrictFieldsAndMultipart_AreCheckedAfterUploadBeforeTools()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        foreach (var json in FillTemplateFieldsTests.InvalidFields())
        {
            using var form = Form(fields: Encoding.UTF8.GetBytes(json), fieldsFirst: true);
            using var response = await test.Client.PostAsync(Endpoint, form);
            await Problem(test, response, 400);
        }
        foreach (var bytes in new byte[][] { [0xc0, 0xaf], [0xef, 0xbb, 0xbf, (byte)'[', (byte)']'],
                     Encoding.UTF8.GetBytes("[[[[0]]]]") })
        {
            using var form = Form(fields: bytes); using var response = await test.Client.PostAsync(Endpoint, form);
            await Problem(test, response, 400);
        }
        // Each multipart case has valid fields; only the selected part contract is invalid.
        var validFields = "[" + FillTemplateFieldsTests.Field + "]";
        foreach (var kind in new[] { "unknown", "duplicate-file", "duplicate-fields", "missing-file", "missing-fields", "fields-filename", "file-no-filename", "case" })
        {
            using var form = new MultipartFormDataContent();
            if (kind != "missing-file")
                if (kind == "file-no-filename") form.Add(new ByteArrayContent(PdfTestContext.Fixture), "file");
                else form.Add(new ByteArrayContent(PdfTestContext.Fixture), kind == "case" ? "File" : "file", "private-template.pdf");
            if (kind != "missing-fields")
                if (kind == "fields-filename") form.Add(new StringContent(validFields), "fields", "data.json");
                else form.Add(new StringContent(validFields), "fields");
            if (kind == "unknown") form.Add(new StringContent("SECRET"), "text");
            if (kind == "duplicate-file") form.Add(new ByteArrayContent(PdfTestContext.Fixture), "file", "duplicate.pdf");
            if (kind == "duplicate-fields") form.Add(new StringContent(validFields), "fields");
            using var response = await test.Client.PostAsync(Endpoint, form);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, kind);
            await Problem(test, response, 400);
        }
        using var queryBody = Form();
        using var query = await test.Client.PostAsync(Endpoint + "?a=1", queryBody);
        await Problem(test, query, 400);
        await test.AssertRejectedBeforeReadingBodyAsync(Endpoint + "?a=1", queryBody);
    }

    [TestMethod]
    public async Task Http_SizeBoundariesAndUploadCompletionPrecedeSemanticChecks()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString() });
        var valid = "[" + FillTemplateFieldsTests.Field + "]";
        var exact = Enumerable.Repeat((byte)' ', 65536).ToArray();
        Encoding.UTF8.GetBytes(valid).CopyTo(exact, 0);
        foreach (var first in new[] { false, true })
        {
            using var form = Form(fields: exact, fieldsFirst: first);
            using var response = await test.Client.PostAsync(Endpoint, form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual("filled.pdf", response.Content.Headers.ContentDisposition?.FileName);
            Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
            test.AssertClean();
        }
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.QpdfPath = "/must-not-run/qpdf";
        using (var form = Form(fields: [.. exact, (byte)' ']))
        using (var response = await test.Client.PostAsync(Endpoint, form)) await Problem(test, response, 413);
        using (var form = Form(pdf: [.. PdfTestContext.Fixture, (byte)' '], fields: "bad json"u8.ToArray(), fieldsFirst: true))
        using (var response = await test.Client.PostAsync(Endpoint, form)) await Problem(test, response, 413);
        using var excessive = new ByteArrayContent(new byte[options.MaxFillTemplateRequestBytes + 1]);
        using var request = await test.Client.PostAsync(Endpoint, excessive);
        await Problem(test, request, 413);
    }

    [TestMethod]
    public async Task Http_PageRangeOverflowAndPdfErrors_KeepFixedReasons()
    {
        await using var test = new PdfTestContext();
        async Task Send(byte[] pdf, string fields, int status, string? reason)
        {
            using var form = Form(pdf, Encoding.UTF8.GetBytes(fields));
            using var response = await test.Client.PostAsync(Endpoint, form);
            await Problem(test, response, status, reason);
        }
        await Send(PdfTestContext.Fixture, "[" + FillTemplateFieldsTests.Change("page", 2) + "]", 400, null);
        await Send(PdfTestContext.Fixture, "[" + FillTemplateFieldsTests.Change("width", 0.0001m) + "]", 422, "field-overflow");
        foreach (var pdf in new[] { Array.Empty<byte>(), "%PDF-1.4\n%%EOF"u8.ToArray(), await test.CreateEncryptedPdfAsync("fixture-password", "fixture-owner") })
            await Send(pdf, "[" + FillTemplateFieldsTests.Field + "]", 422, null);
        var pdf1001 = await test.CreatePagedPdfAsync(1001);
        await Send(pdf1001, "[" + FillTemplateFieldsTests.Field + "," + FillTemplateFieldsTests.Change("page", 1001) + "]", 422, "too-complex");
        using var source = new OverlayJob(); var json = JsonNode.Parse(PdfcpuProcessor.StartupBlankJson)!.AsObject();
        OverlayFixtures.Page(json)["/Rotate"] = 45;
        await source.CreateInput(json.ToJsonString());
        await Send(await File.ReadAllBytesAsync(source.Files.InputPath), "[" + FillTemplateFieldsTests.Field + "]", 422, "unsupported-pdf");
    }

    [TestMethod]
    public async Task Http_FormsIncludeEscapedNamesObjectStreamsAndDirectValues_AndPrecedePageCheck()
    {
        await using var test = new PdfTestContext(); using var source = new OverlayJob();
        foreach (var structure in new[] { "acroform", "perms", "widget", "sig-ft", "sig-type", "timestamp", "escaped-widget", "stream-dict", "array" })
        {
            var root = JsonNode.Parse(PdfcpuProcessor.StartupBlankJson)!.AsObject();
            var objects = OverlayFixtures.Objects(root);
            if (structure is "acroform" or "perms") objects["obj:1 0 R"]!["value"]![structure == "acroform" ? "/AcroForm" : "/Perms"] = new JsonObject();
            else if (structure == "stream-dict") objects["obj:4 0 R"]!["stream"]!["dict"]!["/FT"] = "/Sig";
            else
            {
                var marker = structure switch
                {
                    "sig-ft" => "{\"/FT\":\"/Sig\"}", "sig-type" => "{\"/Type\":\"/Sig\"}",
                    "timestamp" => "{\"/Type\":\"/DocTimeStamp\"}", "escaped-widget" => "{\"/Sub#74ype\":\"/Wid#67et\"}",
                    _ => "{\"/Subtype\":\"/Widget\"}"
                };
                OverlayFixtures.Page(root)[structure == "array" ? "/Nested" : "/Annots"] = JsonNode.Parse("[" + marker + "]");
            }
            File.Delete(source.Files.InputPath); await source.CreateInput(root.ToJsonString());
            var packed = Path.Combine(source.Root, "packed.pdf"); File.Delete(packed);
            Assert.AreEqual(0, await source.Qpdf.RunAsync(["--object-streams=generate", source.Files.InputPath, packed], default));
            foreach (var pdf in new[] { await File.ReadAllBytesAsync(source.Files.InputPath), await File.ReadAllBytesAsync(packed) })
            {
                using var form = Form(pdf, Encoding.UTF8.GetBytes("[" + FillTemplateFieldsTests.Change("page", 2) + "]"));
                using var response = await test.Client.PostAsync(Endpoint, form);
                await Problem(test, response, 422, "form-fields");
            }
        }
    }

    [TestMethod]
    [DataRow("watermark")]
    [DataRow("page-numbers")]
    [DataRow("stamp-image")]
    public async Task Http_ExistingOverlayEndpointsKeepFormDetectionDisabled(string operation)
    {
        using var source = new OverlayJob();
        var root = JsonNode.Parse(PdfcpuProcessor.StartupBlankJson)!.AsObject();
        OverlayFixtures.Objects(root)["obj:1 0 R"]!["value"]!["/AcroForm"] = new JsonObject();
        await source.CreateInput(root.ToJsonString());
        var input = await File.ReadAllBytesAsync(source.Files.InputPath);
        await using var test = new PdfTestContext();
        using (var fill = Form(input))
        using (var rejected = await test.Client.PostAsync(Endpoint, fill))
            await Problem(test, rejected, 422, "form-fields");
        using var form = PdfTestContext.FileForm(input);
        var query = operation switch
        {
            "watermark" => "style=corner&color=gray",
            "page-numbers" => "position=bottom-center&format=number",
            _ => "page=1&x=0.1&y=0.1&width=0.2"
        };
        if (operation == "watermark") form.Add(new StringContent("DRAFT"), "text");
        if (operation == "stamp-image") form.Add(new ByteArrayContent(StampFixtures.Png()), "image", "stamp.png");
        using var response = await test.Client.PostAsync("/api/pdf/" + operation + "?" + query, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
        test.AssertClean();
    }

    [TestMethod]
    public async Task Http_ThousandLinesAndCallerLimits_AndExistingWatermarkPercentPolicy()
    {
        await using var test = new PdfTestContext();
        foreach (var size in new[] { 6m, 6.1m, 71.9m, 72m })
        {
            var f = JsonNode.Parse(FillTemplateFieldsTests.Change("fontSize", size))!;
            f["text"] = "日"; f["x"] = 0; f["y"] = 0; f["width"] = 1; f["height"] = 1;
            using var form = Form(fields: Encoding.UTF8.GetBytes("[" + f.ToJsonString() + "]"));
            using var response = await test.Client.PostAsync(Endpoint, form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            test.AssertClean();
        }
        var corner = JsonNode.Parse(FillTemplateFieldsTests.Field)!;
        corner["x"] = 0.9999m; corner["y"] = 0; corner["width"] = 0.0001m; corner["height"] = 1;
        using (var form = Form(fields: Encoding.UTF8.GetBytes("[" + corner.ToJsonString() + "]")))
        using (var response = await test.Client.PostAsync(Endpoint, form))
            await Problem(test, response, 422, "field-overflow"); // Valid four-decimal coordinates reach layout.
        var fields = Enumerable.Range(0, 200).Select(n =>
        {
            var f = JsonNode.Parse(FillTemplateFieldsTests.Field)!;
            f["page"] = n / 20 + 1; f["text"] = "日\n本\n語\n文\n字";
            return f.ToJsonString();
        });
        using (var form = Form(await test.CreatePagedPdfAsync(10), Encoding.UTF8.GetBytes("[" + string.Join(',', fields) + "]")))
        using (var response = await test.Client.PostAsync(Endpoint, form))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 10);
        }
        using var watermark = new MultipartFormDataContent();
        watermark.Add(new ByteArrayContent(PdfTestContext.Fixture), "file", "template.pdf");
        watermark.Add(new StringContent("100%"), "text");
        using var rejected = await test.Client.PostAsync("/api/pdf/watermark?style=corner&color=gray", watermark);
        await Problem(test, rejected, 400);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("timeout")]
    public async Task Http_CancellationAndTimeout_WaitForProcessAndCleanup(string kind)
    {
        using var blocker = new BlockingQpdf("--check");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = kind == "timeout" ? "1" : "30" });
        using var cancellation = new CancellationTokenSource();
        using var form = Form();
        var pending = test.Client.PostAsync(Endpoint, form, cancellation.Token);
        await blocker.WaitForJobsAsync(1);
        if (kind == "cancel")
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
        else { using var response = await pending; await Problem(test, response, 504); }
        await blocker.AssertStoppedAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (Directory.Exists(test.TempRoot) && Directory.EnumerateFileSystemEntries(test.TempRoot).Any())
        { Assert.IsTrue(watch.Elapsed.TotalSeconds < 5); await Task.Delay(20); }
        test.AssertClean();
    }

    [TestMethod]
    public async Task Http_SharedTwoSlotsRejectThirdWith503()
    {
        using var blocker = new BlockingQpdf("--check");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancel = new CancellationTokenSource(); using var f1 = Form(); using var f2 = PdfTestContext.FileForm(PdfTestContext.Fixture);
        var first = test.Client.PostAsync(Endpoint, f1, cancel.Token);
        var second = test.Client.PostAsync("/api/pdf/page-numbers?position=bottom-center&format=number", f2, cancel.Token);
        await blocker.WaitForJobsAsync(2);
        using var third = Form(); using var rejected = await test.Client.PostAsync(Endpoint, third);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        cancel.Cancel();
        foreach (var pending in new[] { first, second }) await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await blocker.AssertStoppedAsync();
    }

    internal static async Task Problem(PdfTestContext test, HttpResponseMessage response, int status, string? reason = null)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status, (int)response.StatusCode, text);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(text);
        Assert.AreEqual(reason, doc.RootElement.TryGetProperty("reason", out var value) ? value.GetString() : null);
        foreach (var marker in new[] { "private-template.pdf", "日本語", test.TempRoot, "/must-not-run/", "SECRET" })
        {
            Assert.IsFalse(text.Contains(marker), marker);
            Assert.IsFalse(string.Join('\n', test.Logs).Contains(marker), marker);
        }
        test.AssertClean();
    }
}
