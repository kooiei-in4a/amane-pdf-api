using System.Net;
using System.Runtime.Versioning;
using System.Text;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfOptimizeTests
{
    [TestMethod]
    public async Task Optimize_ReturnsValidPdfWithSamePages_FixedName_AndAllowsLargerOutput()
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture, "../../untrusted.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/optimize", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("optimized.pdf", response.Content.Headers.ContentDisposition?.FileName);
        var output = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(output, 1);
        Assert.IsTrue(output.Length > PdfTestContext.Fixture.Length,
            "This small fixture documents that a valid optimized result may be larger than its input.");
        test.AssertClean();
    }

    [TestMethod]
    public async Task Optimize_PreservesPageCountOrderAndRotations_WithUntrustedContentType()
    {
        await using var test = new PdfTestContext();
        var input = await test.CreateRotationMarkedPdfAsync(4);
        using var form = PdfTestContext.FileForm(input, "untrusted.txt");
        form.First().Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        using var response = await test.Client.PostAsync("/api/pdf/optimize", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var output = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(output, 4);
        CollectionAssert.AreEqual(new[] { 0, 90, 180, 270 }, await test.ReadPageRotationsAsync(output));
        test.AssertClean();
    }

    [TestMethod]
    public async Task Optimize_UsesOnlyExpectedLosslessArguments()
    {
        if (!RequireLinux()) return;
        var wrapperRoot = Path.Combine(Path.GetTempPath(), "amane-qpdf-optimize-argv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wrapperRoot);
        try
        {
            var wrapper = Path.Combine(wrapperRoot, "qpdf.sh");
            await File.WriteAllTextAsync(wrapper,
                $"#!/bin/sh\nif [ \"$1\" = --object-streams=generate ]; then printf '%s\\n' \"$@\" > '{wrapperRoot}/argv'; fi\nexec qpdf \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
            using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
            using var response = await test.Client.PostAsync("/api/pdf/optimize", form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var arguments = await File.ReadAllLinesAsync(Path.Combine(wrapperRoot, "argv"));
            Assert.AreEqual(5, arguments.Length);
            CollectionAssert.AreEqual(new[]
            {
                "--object-streams=generate",
                "--recompress-flate",
                "--compression-level=9",
                arguments[^2],
                arguments[^1]
            }, arguments);
            Assert.IsFalse(arguments.Contains("--optimize-images"));
            Assert.IsFalse(arguments.Contains("--decode-level=all"));
            Assert.AreEqual("input.pdf", Path.GetFileName(arguments[3]));
            Assert.AreEqual("output.pdf", Path.GetFileName(arguments[4]));
            Assert.AreEqual(Path.GetDirectoryName(arguments[3]), Path.GetDirectoryName(arguments[4]));
            await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
            test.AssertClean();
        }
        finally
        {
            Directory.Delete(wrapperRoot, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("text")]
    [DataRow("broken")]
    [DataRow("warning")]
    public async Task InvalidPdf_Returns422(string kind)
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
        using var form = PdfTestContext.FileForm(bytes);
        await AssertProblemAsync(test, form, HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    public async Task EncryptedPdf_Returns422()
    {
        await using var test = new PdfTestContext();
        using var protectForm = PdfTestContext.Form(PdfTestContext.Fixture);
        using var protectedResponse = await test.Client.PostAsync("/api/pdf/protect", protectForm);
        Assert.AreEqual(HttpStatusCode.OK, protectedResponse.StatusCode);
        using var optimizeForm = PdfTestContext.FileForm(await protectedResponse.Content.ReadAsByteArrayAsync());
        await AssertProblemAsync(test, optimizeForm, HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    [DataRow(2, 500)]
    [DataRow(3, 422)]
    [DataRow(9, 500)]
    public async Task OptimizeFailure_IsSanitizedAndCleansFiles(int exitCode, int status)
    {
        if (!RequireLinux()) return;
        var wrapperRoot = Path.Combine(Path.GetTempPath(), "amane-qpdf-optimize-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wrapperRoot);
        try
        {
            var wrapper = Path.Combine(wrapperRoot, "qpdf.sh");
            await File.WriteAllTextAsync(wrapper,
                $"#!/bin/sh\ncase \"$1\" in\n--object-streams=generate) echo 'stderr PDF-CONTENT-SENTINEL {wrapperRoot}' >&2; exit {exitCode};;\n*) exec qpdf \"$@\";;\nesac\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
            using var form = PdfTestContext.FileForm(PdfTestContext.Fixture, "private-name.pdf");
            await AssertProblemAsync(test, form, (HttpStatusCode)status);
            Assert.IsFalse(test.Logs.Any(log => log.Contains(wrapperRoot, StringComparison.Ordinal) ||
                log.Contains("PDF-CONTENT-SENTINEL", StringComparison.Ordinal) ||
                log.Contains("private-name.pdf", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(wrapperRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task PasswordAndDuplicateFile_AreRejectedBeforeQpdf()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using (var missing = PdfTestContext.FileForm())
            await AssertProblemAsync(test, missing, HttpStatusCode.BadRequest);
        using (var password = PdfTestContext.Form(PdfTestContext.Fixture))
            await AssertProblemAsync(test, password, HttpStatusCode.BadRequest);
        using (var duplicate = PdfTestContext.FileForm(PdfTestContext.Fixture))
        {
            duplicate.Add(new ByteArrayContent(PdfTestContext.Fixture), "file", "duplicate.pdf");
            await AssertProblemAsync(test, duplicate, HttpStatusCode.BadRequest);
        }
    }

    [TestMethod]
    [DataRow("application/json", "{}")]
    [DataRow("multipart/form-data", "broken")]
    [DataRow("multipart/form-data; boundary=missing", "broken")]
    public async Task MalformedRequests_Return400BeforeQpdf(string contentType, string body)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var content = new StringContent(body);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        await AssertProblemAsync(test, content, HttpStatusCode.BadRequest);
    }

    private static async Task AssertProblemAsync(PdfTestContext test, HttpContent form, HttpStatusCode expected)
    {
        using var response = await test.Client.PostAsync("/api/pdf/optimize", form);
        Assert.AreEqual(expected, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains(test.Root, StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("PDF-CONTENT-SENTINEL", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("stderr", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("stack", StringComparison.OrdinalIgnoreCase));
        test.AssertClean();
    }

    [SupportedOSPlatformGuard("linux")]
    private static bool RequireLinux()
    {
        if (OperatingSystem.IsLinux()) return true;
        Assert.Inconclusive("Linuxのqpdf wrapperを検証します。");
        return false;
    }
}
