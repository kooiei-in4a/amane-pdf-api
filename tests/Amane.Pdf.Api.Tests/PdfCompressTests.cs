using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfCompressTests
{
    [TestMethod]
    [DataRow("standard", false, false, false, 1925)]
    [DataRow("strong", true, false, false, 1375)]
    [DataRow("strong", false, true, false, 1375)]
    [DataRow("standard", false, false, true, 1925)]
    public async Task RealCompression_PreservesSharedPagesTextVectorsFonts(string level, bool gray, bool progressive, bool sample444, int expectedWidth)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test, gray: gray, progressive: progressive, sample444: sample444);
        var input = CompressFixtures.Pdf(jpeg, gray: gray, pages: 2);
        using var response = await PostAsync(test, input, level);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        Assert.AreEqual("compressed.pdf", response.Content.Headers.ContentDisposition?.FileName);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var output = await response.Content.ReadAsByteArrayAsync();
        Assert.IsTrue(output.Length < input.Length);
        await test.AssertValidPdfAsync(output, 2);
        var result = await InspectAsync(test, output);
        var source = await InspectAsync(test, input);
        var pages = result.GetProperty("pages");
        Assert.AreEqual(pages[0].GetProperty("images")[0].GetProperty("object").GetString(), pages[1].GetProperty("images")[0].GetProperty("object").GetString());
        Assert.AreEqual(expectedWidth, pages[0].GetProperty("images")[0].GetProperty("width").GetInt32());
        CollectionAssert.AreEqual(DataStreams(source, "/Font"), DataStreams(result, "/Font"));
        CollectionAssert.AreEqual(DataStreams(source, null), DataStreams(result, null));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(1600, 1200, "standard", 1600)]
    [DataRow(1600, 1200, "strong", 1200)]
    [DataRow(8064, 6048, "standard", 2016)]
    [DataRow(8064, 6048, "strong", 2016)]
    public async Task CeilScale_NoEnlargement_AndLevelsCanHaveSameResolution(int width, int height, string level, int expected)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test, width, height);
        using var response = await PostAsync(test, CompressFixtures.Pdf(jpeg, width, height), level);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        var json = await InspectAsync(test, await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(expected, json.GetProperty("pages")[0].GetProperty("images")[0].GetProperty("width").GetInt32());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("?level=")]
    [DataRow("?level=Standard")]
    [DataRow("?level=standard&level=strong")]
    [DataRow("?level=standard&other=1")]
    public async Task InvalidQuery_Is400BeforeProcesses(string query)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/missing/qpdf" });
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/compress" + query, form);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
        test.AssertClean();
    }

    [TestMethod]
    public async Task MultipartAndInputErrors_KeepExistingContract()
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:MaxFileBytes"] = "1024" });
        using (var form = PdfTestContext.Form(PdfTestContext.Fixture)) await ProblemAsync(test, form, 400);
        using (var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture)) await ProblemAsync(test, form, 400);
        using (var form = PdfTestContext.FileForm(new byte[1025])) await ProblemAsync(test, form, 413);
        using (var form = PdfTestContext.FileForm([])) await ProblemAsync(test, form, 422);
        var encrypted = await test.CreateEncryptedPdfAsync("test-password", "owner-password");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.MaxFileBytes = encrypted.Length;
        using (var form = PdfTestContext.FileForm(encrypted)) await ProblemAsync(test, form, 422);
        test.AssertClean();
    }

    [TestMethod]
    public async Task NoImages_ReturnsZero_AndMayBeLarger()
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        using var response = await PostAsync(test, PdfTestContext.Fixture);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        var output = await response.Content.ReadAsByteArrayAsync();
        Assert.IsTrue(output.Length > PdfTestContext.Fixture.Length);
        await test.AssertValidPdfAsync(output, 1);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("/Decode [0 1 0 1 0 1]")]
    [DataRow("/DecodeParms << /ColorTransform 1 >>")]
    [DataRow("/Mask [0 0 0 0 0 0]")]
    public async Task ExcludedDictionary_KeepsRawJpeg(string extra)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var response = await PostAsync(test, CompressFixtures.Pdf(jpeg, extra: extra));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        var result = await InspectAsync(test, await response.Content.ReadAsByteArrayAsync());
        CollectionAssert.Contains(DataStreams(result, "/Image"), Convert.ToBase64String(jpeg));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("[/CalRGB << /WhitePoint [0.9505 1 1.0890] >>]", false)]
    [DataRow("[/CalGray << /WhitePoint [0.9505 1 1.0890] >>]", true)]
    [DataRow("[/ICCBased 7 0 R]", false)]
    public async Task SupportedColorSpaces_ArePreserved(string color, bool gray)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test, gray: gray);
        // Profile validity is intentionally outside candidate eligibility; qpdf does not decode it.
        var extras = color.Contains("ICCBased") ? new[] { CompressFixtures.Stream("/N 3 /Alternate /DeviceRGB", new byte[128]) } : null;
        var input = CompressFixtures.Pdf(jpeg, gray: gray, color: color, extras: extras);
        using var response = await PostAsync(test, input);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        var result = await InspectAsync(test, await response.Content.ReadAsByteArrayAsync());
        var image = result.GetProperty("pages")[0].GetProperty("images")[0];
        Assert.AreEqual(JsonValueKind.Array, image.GetProperty("colorspace").ValueKind);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("Pdf:CompressMaxPixels", "1")]
    [DataRow("Pdf:CompressPnmLimitBytes", "4096")]
    [DataRow("Pdf:CompressJsonLimitBytes", "10")]
    [DataRow("Pdf:CompressSpoolLimitBytes", "4096")]
    [DataRow("Pdf:CompressStdoutLimitBytes", "10")]
    [DataRow("Pdf:CompressJsonDepth", "2")]
    public async Task ConfiguredImageLimits_UseValidZeroReplacementResult(string key, string value)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext(new() { [key] = value });
        var jpeg = await CompressFixtures.JpegAsync(test);
        // Page dictionaries are released before image selection. Two image dictionaries
        // still cannot fit in a 4 KiB spool, so this verifies the limit rather than a leak.
        var input = key == "Pdf:CompressSpoolLimitBytes" ? CompressFixtures.Many(jpeg, 2) : CompressFixtures.Pdf(jpeg);
        using var response = await PostAsync(test, input);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("/bin/false", 200)]
    [DataRow("/missing/djpeg", 500)]
    public async Task ImageFailureVersusStartFailure_IsDistinct(string executable, int status)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:DjpegPath"] = executable });
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var response = await PostAsync(test, CompressFixtures.Pdf(jpeg));
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        if (status == 200) Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        else Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(153)]
    public async Task FinalWriteFailure_Is500_WithNoSuccessHeader(int exit)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var wrapper = await WrapperAsync(test, $"case \"$1\" in */input.pdf) echo 'PDF-CONTENT-SENTINEL' >&2; exit {exit};; esac\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        await ProblemAsync(test, form, 500);
        Assert.IsFalse(test.Logs.Any(log => log.Contains("PDF-CONTENT-SENTINEL")));
    }

    [TestMethod]
    public async Task SoftDeadlineIncludesInputCheck_StopsImageProcess_AndReturnsPartialResult()
    {
        if (!Linux()) return;
        using var blocker = new BlockingQpdf("-scale", checkDelaySeconds: 0.7);
        await using var test = new PdfTestContext(new() { ["Pdf:DjpegPath"] = blocker.Executable,
            ["Pdf:QpdfPath"] = blocker.Executable, ["Pdf:CompressSoftTimeoutSeconds"] = "1" });
        var jpeg = await CompressFixtures.JpegAsync(test);
        var watch = Stopwatch.StartNew();
        using var response = await PostAsync(test, CompressFixtures.Pdf(jpeg));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(3));
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImageTimeoutVersusHardTimeout_TerminatesTree(bool hard)
    {
        if (!Linux()) return;
        using var blocker = new BlockingQpdf("-scale");
        await using var test = new PdfTestContext(new() { ["Pdf:DjpegPath"] = blocker.Executable,
            ["Pdf:CompressImageTimeoutSeconds"] = hard ? "5" : "1", ["Pdf:QpdfTimeoutSeconds"] = hard ? "1" : "30" });
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var response = await PostAsync(test, CompressFixtures.Pdf(jpeg));
        Assert.AreEqual(hard ? HttpStatusCode.GatewayTimeout : HttpStatusCode.OK, response.StatusCode);
        if (hard) Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    public async Task CancelAndConcurrency_KeepSharedLimiter_AndTerminateTree()
    {
        if (!Linux()) return;
        using var blocker = new BlockingQpdf("-scale");
        await using var test = new PdfTestContext(new() { ["Pdf:DjpegPath"] = blocker.Executable,
            ["Pdf:CompressImageTimeoutSeconds"] = "15", ["Pdf:MaxConcurrentProcesses"] = "1" });
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var cancel = new CancellationTokenSource();
        using var first = PdfTestContext.FileForm(CompressFixtures.Pdf(jpeg));
        var pending = test.Client.PostAsync("/api/pdf/compress?level=standard", first, cancel.Token);
        await blocker.WaitForJobsAsync(1);
        using var second = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var rejected = await test.Client.PostAsync("/api/pdf/optimize", second);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => { using var response = await pending; });
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("Pdf:DjpegPath", "/missing/djpeg")]
    [DataRow("Pdf:CjpegPath", "/bin/false")]
    [DataRow("Pdf:JpegAddressSpaceLimitBytes", "1")]
    public void JpegStartupFailure_HasNoBypass(string key, string value)
    {
        if (!Linux()) return;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    internal static async Task<HttpResponseMessage> PostAsync(PdfTestContext test, byte[] pdf, string level = "standard")
    {
        using var form = PdfTestContext.FileForm(pdf);
        return await test.Client.PostAsync("/api/pdf/compress?level=" + level, form);
    }
    internal static async Task<JsonElement> InspectAsync(PdfTestContext test, byte[] pdf)
    {
        var path = Path.Combine(test.Root, "inspect-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, pdf);
        var result = await test.QpdfAsync("--json=2", "--json-stream-data=inline", "--decode-level=generalized", path);
        Assert.AreEqual(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.Clone();
    }
    private static string[] DataStreams(JsonElement document, string? subtype)
    {
        if (subtype is null)
            return [.. document.GetProperty("pages").EnumerateArray().SelectMany(page => page.GetProperty("contents").EnumerateArray())
                .Select(reference => reference.GetString()!).Distinct().Select(reference =>
                    document.GetProperty("qpdf")[1].GetProperty("obj:" + reference).GetProperty("stream").GetProperty("data").GetString()!)];
        return [.. document.GetProperty("qpdf")[1].EnumerateObject().Select(obj => obj.Value).Where(obj =>
            subtype == "/Font" ? obj.TryGetProperty("value", out var value) && value.TryGetProperty("/Type", out var type) && type.GetString() == subtype :
            obj.TryGetProperty("stream", out var stream) && (subtype is null ? !stream.GetProperty("dict").TryGetProperty("/Subtype", out _) :
                stream.GetProperty("dict").TryGetProperty("/Subtype", out var streamType) && streamType.GetString() == subtype))
            .Select(obj => subtype == "/Font" ? obj.GetProperty("value").GetRawText() : obj.GetProperty("stream").GetProperty("data").GetString()!)];
    }
    private static async Task ProblemAsync(PdfTestContext test, HttpContent form, int status)
    {
        using var response = await test.Client.PostAsync("/api/pdf/compress?level=standard", form);
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains(test.Root) || body.Contains("PDF-CONTENT-SENTINEL"));
        if (status == 422) Assert.IsFalse(JsonDocument.Parse(body).RootElement.TryGetProperty("reason", out _));
        test.AssertClean();
    }
    internal static async Task<string> WrapperAsync(PdfTestContext test, string body)
    {
        var path = Path.Combine(test.Root, "wrapper-" + Guid.NewGuid().ToString("N") + ".sh");
        await File.WriteAllTextAsync(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    [SupportedOSPlatformGuard("linux")]
    internal static bool Linux()
    {
        if (OperatingSystem.IsLinux()) return true;
        Assert.Inconclusive("compress is provided on Linux only."); return false;
    }
}
