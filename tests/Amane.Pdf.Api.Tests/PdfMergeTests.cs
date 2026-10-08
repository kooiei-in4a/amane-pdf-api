using System.Net;
using System.Runtime.Versioning;
using System.Text;
using Amane.Pdf.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfMergeTests
{
    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(10)]
    public async Task Merge_ReturnsAllPages_ValidPdf_FixedFilename_AndCleansFiles(int count)
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.MergeForm(Enumerable.Repeat(PdfTestContext.Fixture, count).ToArray());
        using var response = await test.Client.PostAsync("/api/pdf/merge", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.AreEqual("merged.pdf", response.Content.Headers.ContentDisposition?.FileName);
        var defaults = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        Assert.AreEqual(10, defaults.MaxMergeFiles);
        Assert.AreEqual(50L * 1024 * 1024, defaults.MaxMergeInputBytes);
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), count);
        test.AssertClean();
        AssertNoExposure(test, response.ToString());
    }

    [TestMethod]
    public async Task Merge_PreservesMultipartOrder_AndEveryPageWithinEachInput()
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.MergeForm(await test.CreateRotationMarkedPdfAsync(3),
            PdfTestContext.Fixture, await test.CreateRotationMarkedPdfAsync(2));
        foreach (var content in form)
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        using var response = await test.Client.PostAsync("/api/pdf/merge", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var output = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(output, 6);
        CollectionAssert.AreEqual(new[] { 0, 90, 180, 0, 0, 90 }, await test.ReadPageRotationsAsync(output));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(0, 10)]
    [DataRow(1, 10)]
    [DataRow(11, 10)]
    [DataRow(3, 2)]
    public async Task InvalidFileCount_Returns400BeforeQpdf(int count, int maximum)
    {
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = "/must-not-run/qpdf",
            ["Pdf:MaxMergeFiles"] = maximum.ToString()
        });
        using var form = PdfTestContext.MergeForm(Enumerable.Repeat(PdfTestContext.Fixture, count).ToArray());
        await AssertProblemAsync(test, form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task EleventhPart_IsRejectedWithoutCreatingItsTemporaryFile()
    {
        await using var test = new PdfTestContext();
        using var files = new TemporaryPdfFiles(test.TempRoot);
        using var form = PdfTestContext.MergeForm(Enumerable.Repeat(PdfTestContext.Fixture, 11).ToArray());
        await using var body = await form.ReadAsStreamAsync();
        var request = new DefaultHttpContext().Request;
        request.ContentType = form.Headers.ContentType!.ToString();
        request.Body = body;
        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            MultipartPdfUpload.ReadMergeFilesAsync(request, files, new PdfOptions(), CancellationToken.None));
        Assert.AreEqual(400, error.StatusCode);
        Assert.AreEqual(10, Directory.GetFiles(files.DirectoryPath).Length);
        Assert.IsFalse(File.Exists(files.MergeInputPath(11)));
        for (var i = 1; i <= 10; i++)
        {
            Assert.AreEqual(PdfTestContext.Fixture.Length, new FileInfo(files.MergeInputPath(i)).Length);
            if (OperatingSystem.IsLinux())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.MergeInputPath(i)));
        }
        if (OperatingSystem.IsLinux())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(files.DirectoryPath));
    }

    [TestMethod]
    [DataRow("password")]
    [DataRow("other-file")]
    [DataRow("file-text")]
    public async Task UnexpectedFields_Return400BeforeQpdf(string kind)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
        if (kind == "other-file") form.Add(new ByteArrayContent(PdfTestContext.Fixture), "other", "extra.pdf");
        else form.Add(new StringContent("PDF-CONTENT-SENTINEL"), kind == "password" ? "password" : "file");
        await AssertProblemAsync(test, form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow("application/json", "{}")]
    [DataRow("multipart/form-data", "broken")]
    [DataRow("multipart/form-data; boundary=missing", "broken")]
    public async Task MalformedMultipart_Returns400(string contentType, string body)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var content = new StringContent(body);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        await AssertProblemAsync(test, content, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("text")]
    [DataRow("broken")]
    [DataRow("warning")]
    public async Task InvalidMiddlePdf_RejectsWholeMerge_AndCleansAllInputs(string kind)
    {
        await using var test = new PdfTestContext();
        var bytes = kind switch
        {
            "empty" => [],
            "text" => Encoding.UTF8.GetBytes("PDF-CONTENT-SENTINEL: not a PDF"),
            "broken" => Encoding.UTF8.GetBytes("%PDF-1.4\nPDF-CONTENT-SENTINEL\n%%EOF"),
            _ => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(PdfTestContext.Fixture)
                .Replace("/Length 41", "/Length 39", StringComparison.Ordinal))
        };
        if (kind == "warning")
        {
            var path = Path.Combine(test.Root, "warning.pdf");
            await File.WriteAllBytesAsync(path, bytes);
            Assert.AreEqual(3, (await test.QpdfAsync("--check", path)).ExitCode);
        }
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, bytes, PdfTestContext.Fixture);
        await AssertProblemAsync(test, form, HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("input-password")]
    public async Task EncryptedMiddlePdf_IsRejectedIncludingEmptyPassword(string password)
    {
        await using var test = new PdfTestContext();
        byte[] encrypted;
        using (var files = new TemporaryPdfFiles(test.TempRoot))
        {
            await File.WriteAllBytesAsync(files.InputPath, PdfTestContext.Fixture);
            await new QpdfProcessor(Options.Create(new PdfOptions())).ProtectAsync(files, password, CancellationToken.None);
            encrypted = await File.ReadAllBytesAsync(files.OutputPath);
        }
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, encrypted, PdfTestContext.Fixture);
        await AssertProblemAsync(test, form, HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    public async Task ValidationAndMerge_UseOnlyGeneratedPaths_Sequentially()
    {
        if (!RequireLinux()) return;
        await using var test = new PdfTestContext();
        var wrapper = Path.Combine(test.Root, "qpdf.sh");
        await File.WriteAllTextAsync(wrapper,
            $"#!/bin/sh\nprintf '%s\\n' \"$@\" >> '{test.Root}/argv'\nexec qpdf \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/merge", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var argv = await File.ReadAllLinesAsync(Path.Combine(test.Root, "argv"));
        var first = argv[1];
        var second = argv[5];
        CollectionAssert.AreEqual(new[]
        {
            "--is-encrypted", first, "--check", first,
            "--is-encrypted", second, "--check", second,
            "--empty", "--pages", first, "1-z", second, "1-z", "--",
            Path.Combine(Path.GetDirectoryName(first)!, "output.pdf")
        }, argv);
        Assert.AreEqual("input-0001.pdf", Path.GetFileName(first));
        Assert.AreEqual("input-0002.pdf", Path.GetFileName(second));
        Assert.AreEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
        StringAssert.StartsWith(first, test.TempRoot + Path.DirectorySeparatorChar);
        Assert.IsFalse(string.Join('\n', argv).Contains("private-name", StringComparison.Ordinal));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("--empty", 2, 500)]
    [DataRow("--empty", 3, 422)]
    [DataRow("--empty", 9, 500)]
    [DataRow("--check", 9, 500)]
    public async Task QpdfFailure_DiscardsStdoutAndStderr_AndCleansFiles(string stage, int exitCode, int status)
    {
        if (!RequireLinux()) return;
        await using var test = new PdfTestContext();
        var wrapper = Path.Combine(test.Root, "qpdf.sh");
        await File.WriteAllTextAsync(wrapper, $"#!/bin/sh\ncase \"$1\" in\n{stage})\n" +
            $"  echo 'stdout PDF-CONTENT-SENTINEL {test.Root}'\n  echo 'stderr PDF-CONTENT-SENTINEL {test.Root}' >&2\n" +
            $"  exit {exitCode};;\n*) exec qpdf \"$@\";;\nesac\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
        await AssertProblemAsync(test, form, (HttpStatusCode)status);
    }

    [TestMethod]
    [DataRow("Pdf:MaxMergeFiles", "0")]
    [DataRow("Pdf:MaxMergeFiles", "-1")]
    [DataRow("Pdf:MaxMergeFiles", "1")]
    [DataRow("Pdf:MaxMergeFiles", "11")]
    [DataRow("Pdf:MaxMergeInputBytes", "0")]
    [DataRow("Pdf:MaxMergeInputBytes", "-1")]
    [DataRow("Pdf:MaxMergeInputBytes", "9223372036854775807")]
    [DataRow("Pdf:MaxMergeInputBytes", "9223372036854710272")]
    public async Task InvalidMergeConfiguration_IsRejectedOnStartup(string key, string value)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        // Invalid configuration now exits with a fixed log before the test host starts.
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [TestMethod]
    public async Task ValidMergeConfiguration_IsApplied_IndependentlyOfSingleFileLimit()
    {
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString(),
            ["Pdf:MaxMergeFiles"] = "2",
            ["Pdf:MaxMergeInputBytes"] = (2 * PdfTestContext.Fixture.Length).ToString()
        });
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/merge", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        Assert.AreEqual(2, options.MaxMergeFiles);
        Assert.AreEqual(2 * PdfTestContext.Fixture.Length + PdfOptions.MultipartOverheadBytes, options.MaxMergeRequestBytes);
        test.AssertClean();
    }

    internal static async Task AssertProblemAsync(PdfTestContext test, HttpContent content, HttpStatusCode expected)
    {
        using var response = await test.Client.PostAsync("/api/pdf/merge", content);
        Assert.AreEqual(expected, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertNoExposure(test, await response.Content.ReadAsStringAsync());
        test.AssertClean();
    }

    internal static void AssertNoExposure(PdfTestContext test, string response)
    {
        foreach (var sentinel in new[] { test.Root, "private-name", "PDF-CONTENT-SENTINEL", "stdout", "stderr", "stack" })
        {
            Assert.IsFalse(response.Contains(sentinel, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(test.Logs.Any(log => log.Contains(sentinel, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [SupportedOSPlatformGuard("linux")]
    internal static bool RequireLinux()
    {
        if (OperatingSystem.IsLinux()) return true;
        Assert.Inconclusive("Linuxのprocess treeとprivate permissionを検証します。");
        return false;
    }
}
