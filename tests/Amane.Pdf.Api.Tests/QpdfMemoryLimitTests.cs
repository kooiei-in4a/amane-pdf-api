using System.IO.Compression;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class QpdfMemoryLimitTests
{
    private const string Title = "このPDFは処理できません。PDFの破損・パスワード設定や、画像が大きすぎないか確認してください。";

    [TestMethod]
    [DataRow("Pdf:PrlimitPath", "")]
    [DataRow("Pdf:PrlimitPath", " ")]
    [DataRow("Pdf:QpdfAddressSpaceLimitBytes", "0")]
    [DataRow("Pdf:QpdfAddressSpaceLimitBytes", "-1")]
    [DataRow("Pdf:QpdfJpegMemory", "")]
    [DataRow("Pdf:QpdfJpegMemory", "0M")]
    [DataRow("Pdf:QpdfJpegMemory", "-1")]
    [DataRow("Pdf:QpdfJpegMemory", "+64M")]
    [DataRow("Pdf:QpdfJpegMemory", "64MiB")]
    [DataRow("Pdf:QpdfJpegMemory", "64 M")]
    [DataRow("Pdf:QpdfJpegMemory", "64M\n")]
    [DataRow("Pdf:QpdfJpegMemory", "６４M")]
    [DataRow("Pdf:QpdfJpegMemory", "000000000000000000000000000064M")]
    [DataRow("Pdf:QpdfJpegMemory", "9223372036854775807M")]
    [DataRow("Pdf:QpdfJpegMemory", "9223372036854775807")]
    public void InvalidOptions_PreventStartup(string key, string value)
    {
        using var factory = Factory(new() { [key] = value });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("64000")]
    [DataRow("64m")]
    [DataRow("64M")]
    public void ValidJpegMemory_Starts(string value)
    {
        using var factory = Factory(new() { ["Pdf:QpdfJpegMemory"] = value });
        using var client = factory.CreateClient();
    }

    [TestMethod]
    [DataRow("Pdf:PrlimitPath", "/missing/prlimit")]
    [DataRow("Pdf:PrlimitPath", "/bin/false")]
    [DataRow("Pdf:QpdfPath", "/missing/qpdf")]
    [DataRow("Pdf:QpdfPath", "/bin/false")]
    [DataRow("Pdf:QpdfAddressSpaceLimitBytes", "1")]
    public async Task FailedSelfTest_PreventsStartup_WithoutInternalDetails(string key, string value)
    {
        if (!RequireLinux()) return;
        using var factory = Factory(new() { [key] = value });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        var options = new PdfOptions();
        if (key == "Pdf:PrlimitPath") options.PrlimitPath = value;
        if (key == "Pdf:QpdfPath") options.QpdfPath = value;
        if (key == "Pdf:QpdfAddressSpaceLimitBytes") options.QpdfAddressSpaceLimitBytes = long.Parse(value);
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ProcessMemoryLimits.ValidateStartupAsync(options, CancellationToken.None));
        Assert.AreEqual("PDF処理のメモリ制限の自己テストに失敗しました。", exception.Message);
        Assert.IsNull(exception.InnerException);
    }

    [TestMethod]
    [DataRow("/api/pdf/protect")]
    [DataRow("/api/pdf/optimize")]
    [DataRow("/api/pdf/merge")]
    [DataRow("/api/pdf/rotate?angle=90&pages=1")]
    [DataRow("/api/pdf/extract?pages=1")]
    [DataRow("/api/pdf/delete-pages?pages=2")]
    [DataRow("/api/pdf/reorder?pages=2,1")]
    public async Task RealAddressSpaceLimit_ReturnsCommon422_AndIncreasingLimitProcessesSamePdf(string path)
    {
        if (!RequireLinux()) return;
        var fixture = FlateImagePdf();
        foreach (var limitMiB in new[] { 64, 288 })
        {
            await using var test = new PdfTestContext(new()
                { ["Pdf:QpdfAddressSpaceLimitBytes"] = (limitMiB * 1024 * 1024).ToString() });
            using var form = path.EndsWith("merge", StringComparison.Ordinal)
                ? PdfTestContext.MergeForm(fixture, PdfTestContext.Fixture)
                : PdfTestContext.Form(fixture, path.EndsWith("protect", StringComparison.Ordinal) ? "test-password" : null);
            using var response = await test.Client.PostAsync(path, form);
            Assert.AreEqual(limitMiB == 64 ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
            if (limitMiB == 64)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(Title, body.RootElement.GetProperty("title").GetString());
                Assert.IsFalse(body.RootElement.TryGetProperty("reason", out _));
            }
            else if (!path.EndsWith("protect", StringComparison.Ordinal))
                await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(),
                    path.EndsWith("merge", StringComparison.Ordinal) ? 3 : path.Contains("extract") || path.Contains("delete-pages") ? 1 : 2);
            test.AssertClean();
        }
    }

    [TestMethod]
    public async Task Unlock_LimitedPlainAndAuthenticatedCheck_KeepInvalidPdfReason()
    {
        if (!RequireLinux()) return;
        await using var source = new PdfTestContext();
        var fixture = FlateImagePdf();
        var encrypted = await source.CreateEncryptedPdfAsync("test-password", "owner-password", input: fixture);
        foreach (var input in new[] { fixture, encrypted })
        {
            await using var test = new PdfTestContext(new() { ["Pdf:QpdfAddressSpaceLimitBytes"] = (64 * 1024 * 1024).ToString() });
            using var form = PdfTestContext.Form(input);
            using var response = await test.Client.PostAsync("/api/pdf/unlock", form);
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid-pdf", body.RootElement.GetProperty("reason").GetString());
            Assert.AreEqual("正常なPDFが必要です。", body.RootElement.GetProperty("title").GetString());
            test.AssertClean();
        }
        await using var larger = new PdfTestContext();
        using var successForm = PdfTestContext.Form(encrypted);
        using var success = await larger.Client.PostAsync("/api/pdf/unlock", successForm);
        Assert.AreEqual(HttpStatusCode.OK, success.StatusCode);
        await larger.AssertValidPdfAsync(await success.Content.ReadAsByteArrayAsync(), 2);
        larger.AssertClean();
    }

    [TestMethod]
    public async Task RealLimiter_AppliesEnvironmentAndAddressSpaceToBothExecutionPaths()
    {
        if (!RequireLinux()) return;
        await using var test = new PdfTestContext();
        var wrapper = Path.Combine(test.Root, "inspect-limits.sh");
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\n" +
            "[ \"$JPEGMEM\" = 64000 ] || exit 9\n" +
            "grep -Eq '^Max address space +67108864 +67108864 +bytes' /proc/$$/limits || exit 9\n" +
            "exec qpdf \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.QpdfPath = wrapper;
        options.QpdfAddressSpaceLimitBytes = 64 * 1024 * 1024;
        options.QpdfJpegMemory = "64000";
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/rotate?angle=90&pages=1", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    public async Task SelfTest_QpdfTooSmallAndTimeout_AreRejected_AndProcessTreeStops()
    {
        if (!RequireLinux()) return;
        var options = new PdfOptions { QpdfAddressSpaceLimitBytes = 16 * 1024 * 1024 };
        var trueRequest = ProcessMemoryLimits.CreateRequest(options.PrlimitPath, "/bin/true", [],
            options.QpdfAddressSpaceLimitBytes, options.QpdfJpegMemory);
        Assert.AreEqual(0, (await ExternalProcessRunner.RunAsync(trueRequest, CancellationToken.None)).ExitCode);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ProcessMemoryLimits.ValidateStartupAsync(options, CancellationToken.None));
        using var blocker = new BlockingQpdf("--version");
        options.QpdfPath = blocker.Executable;
        options.QpdfAddressSpaceLimitBytes = new PdfOptions().QpdfAddressSpaceLimitBytes;
        options.QpdfTimeoutSeconds = 1;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ProcessMemoryLimits.ValidateStartupAsync(options, CancellationToken.None));
        await blocker.AssertStoppedAsync();
    }

    [TestMethod]
    [DataRow(false, 126)]
    [DataRow(true, 127)]
    public async Task RealPrlimit_ExecFailureAfterStartup_Is500(bool missing, int exitCode)
    {
        if (!RequireLinux()) return;
        await using var test = new PdfTestContext();
        var executable = Path.Combine(test.Root, "unavailable");
        if (!missing) await File.WriteAllTextAsync(executable, "not executable");
        var options = new PdfOptions { QpdfPath = executable };
        var processor = new QpdfProcessor(Options.Create(options));
        Assert.AreEqual(exitCode, await processor.RunAsync(["--version"], CancellationToken.None));
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = executable;
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/optimize", form);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        test.AssertClean();
    }

    // 16 MP RGB Flate, only a small compressed fixture. No image library or large repository file.
    internal static byte[] FlateImagePdf()
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[4096 * 3];
            for (var x = 0; x < row.Length; x++) row[x] = (byte)(x % 251);
            for (var y = 0; y < 4096; y++) zlib.Write(row);
        }
        var image = compressed.ToArray();
        using var pdf = new MemoryStream();
        void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        void Object(int number, string value)
        {
            offsets.Add(pdf.Position);
            Write($"{number} 0 obj\n{value}\nendobj\n");
        }
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        for (var n = 3; n <= 4; n++)
            Object(n, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im0 5 0 R >> >> >>");
        offsets.Add(pdf.Position);
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 4096 /Height 4096 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {image.Length} >>\nstream\n");
        pdf.Write(image);
        Write("\nendstream\nendobj\n");
        var xref = pdf.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static WebApplicationFactory<Program> Factory(Dictionary<string, string?> settings)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

    [SupportedOSPlatformGuard("linux")]
    private static bool RequireLinux()
    {
        if (OperatingSystem.IsLinux()) return true;
        Assert.Inconclusive("RLIMIT_AS requires Linux and real prlimit/qpdf.");
        return false;
    }
}
