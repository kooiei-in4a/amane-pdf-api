using System.Net;
using System.Text;
using Amane.Pdf.Api;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfValidationTests
{
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task MissingFields_Return400(bool file, bool password)
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.Form(file ? PdfTestContext.Fixture : null, password ? "fixture-password" : null);
        await AssertProblemAsync(test, form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("null\0password")]
    [DataRow("line\npassword")]
    [DataRow("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz")]
    public async Task InvalidPasswords_Return400(string password)
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.Form(PdfTestContext.Fixture, password);
        await AssertProblemAsync(test, form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("text")]
    [DataRow("broken")]
    [DataRow("warning")]
    public async Task InvalidPdf_Return422_RegardlessOfContentType(string kind)
    {
        await using var test = new PdfTestContext();
        var bytes = kind switch
        {
            "empty" => [],
            "text" => Encoding.UTF8.GetBytes("PDF-CONTENT-SENTINEL: this is not a PDF"),
            "broken" => Encoding.UTF8.GetBytes("%PDF-1.4\nPDF-CONTENT-SENTINEL\n%%EOF"),
            _ => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(PdfTestContext.Fixture).Replace("/Length 41", "/Length 39", StringComparison.Ordinal))
        };
        if (kind == "warning")
        {
            var input = Path.Combine(test.Root, "warning.pdf");
            await File.WriteAllBytesAsync(input, bytes);
            Assert.AreEqual(3, (await test.QpdfAsync("--check", input)).ExitCode);
        }
        using var form = PdfTestContext.Form(bytes);
        await AssertProblemAsync(test, form, HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("input-fixture-password")]
    public async Task EncryptedPdf_Return422_EvenWithEmptyOrMatchingPassword(string inputPassword)
    {
        await using var test = new PdfTestContext();
        byte[] encrypted;
        using (var files = new TemporaryPdfFiles(test.TempRoot))
        {
            await File.WriteAllBytesAsync(files.InputPath, PdfTestContext.Fixture);
            await new QpdfProcessor(Options.Create(new PdfOptions())).ProtectAsync(files, inputPassword, CancellationToken.None);
            encrypted = await File.ReadAllBytesAsync(files.OutputPath);
        }
        using var form = PdfTestContext.Form(encrypted, "input-fixture-password");
        await AssertProblemAsync(test, form, HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    [DataRow("日本語のパスワードé🔒")]
    [DataRow("  spaces-are-preserved  ")]
    public async Task UnicodeAndSpaces_AreUsablePasswords(string password)
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.Form(PdfTestContext.Fixture, password, "名前.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/protect", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var output = Path.Combine(test.Root, "unicode.pdf");
        await File.WriteAllBytesAsync(output, await response.Content.ReadAsByteArrayAsync());
        var passwordFile = Path.Combine(test.Root, "password.json");
        await File.WriteAllTextAsync(passwordFile, password);
        Assert.AreEqual(0, (await test.QpdfAsync("--password-file=" + passwordFile, "--check", output)).ExitCode);
        await File.WriteAllTextAsync(passwordFile, "wrong-fixture-password");
        Assert.AreEqual(2, (await test.QpdfAsync("--password-file=" + passwordFile, "--check", output)).ExitCode);
        Assert.IsFalse(test.Logs.Any(log => log.Contains(password, StringComparison.Ordinal)));
        test.AssertClean();
    }

    [TestMethod]
    public async Task ValidPdf_WithUntrustedContentType_IsAccepted()
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.Form(PdfTestContext.Fixture, fileName: "untrusted.txt");
        form.First().Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        using var response = await test.Client.PostAsync("/api/pdf/protect", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DuplicateFields_Return400(bool duplicateFile)
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.Form(PdfTestContext.Fixture);
        if (duplicateFile) form.Add(new ByteArrayContent(PdfTestContext.Fixture), "file", "extra.pdf");
        else form.Add(new StringContent("second-fixture-password"), "password");
        await AssertProblemAsync(test, form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow("application/json", "{}")]
    [DataRow("multipart/form-data", "broken")]
    [DataRow("multipart/form-data; boundary=missing", "broken")]
    public async Task MalformedRequests_Return400(string contentType, string body)
    {
        await using var test = new PdfTestContext();
        using var content = new StringContent(body);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        await AssertProblemAsync(test, content, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow(2, 500)]
    [DataRow(3, 422)]
    [DataRow(9, 500)]
    public async Task QpdfJobFailure_IsSanitized_AndCleansFiles(int exitCode, int status)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Linuxでqpdf異常終了を検証します。");
            return;
        }
        var wrapperRoot = Path.Combine(Path.GetTempPath(), "amane-qpdf-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wrapperRoot);
        try
        {
            var wrapper = Path.Combine(wrapperRoot, "qpdf.sh");
            await File.WriteAllTextAsync(wrapper, $"#!/bin/sh\ncase \"$1\" in\n--job-json-file=*) echo 'stderr-fixture-password PDF-CONTENT-SENTINEL {wrapperRoot}' >&2; exit {exitCode};;\nesac\nexec qpdf \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
            using var form = PdfTestContext.Form(PdfTestContext.Fixture, "stderr-fixture-password");
            await AssertProblemAsync(test, form, (HttpStatusCode)status);
            Assert.IsFalse(test.Logs.Any(log => log.Contains(wrapperRoot, StringComparison.Ordinal) || log.Contains("stderr-fixture-password", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(wrapperRoot, recursive: true);
        }
    }

    private static async Task AssertProblemAsync(PdfTestContext test, HttpContent form, HttpStatusCode expected)
    {
        using var response = await test.Client.PostAsync("/api/pdf/protect", form);
        Assert.AreEqual(expected, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        if (expected == HttpStatusCode.UnprocessableEntity)
        {
            using var problem = System.Text.Json.JsonDocument.Parse(body);
            Assert.AreEqual("未暗号化の正常なPDFが必要です。", problem.RootElement.GetProperty("title").GetString());
            Assert.IsFalse(problem.RootElement.TryGetProperty("reason", out _));
        }
        Assert.IsFalse(body.Contains(test.Root, StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("fixture-password", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("PDF-CONTENT-SENTINEL", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("stderr", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("stack", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(test.Logs.Any(log => log.Contains("PDF-CONTENT-SENTINEL", StringComparison.Ordinal)));
        test.AssertClean();
    }
}
