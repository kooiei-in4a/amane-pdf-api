using System.Net;
using System.Runtime.Versioning;
using System.Text;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfPageSelectionOperationTests
{
    [TestMethod]
    [DataRow("2", "90")]
    [DataRow("3,1-2", "180,0,90")]
    [DataRow("1-4", "0,90,180,270")]
    public async Task Extract_SelectsPagesInRequestedOrder(string pages, string rotations)
    {
        await using var test = new PdfTestContext();
        var input = await test.CreateRotationMarkedPdfAsync(4);
        using var form = PdfTestContext.FileForm(input, "../../untrusted.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/extract?pages=" + pages, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("extracted.pdf", response.Content.Headers.ContentDisposition?.FileName);
        var output = await response.Content.ReadAsByteArrayAsync();
        var expected = rotations.Split(',').Select(int.Parse).ToArray();
        CollectionAssert.AreEqual(expected, await test.ReadPageRotationsAsync(output));
        await test.AssertValidPdfAsync(output, expected.Length);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("2", "0,180,270")]
    [DataRow("4,2", "0,180")]
    public async Task DeletePages_RemovesPagesAndPreservesRemainingOrder(string pages, string rotations)
    {
        await using var test = new PdfTestContext();
        var input = await test.CreateRotationMarkedPdfAsync(4);
        using var form = PdfTestContext.FileForm(input, "../../untrusted.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/delete-pages?pages=" + pages, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("pages-deleted.pdf", response.Content.Headers.ContentDisposition?.FileName);
        var output = await response.Content.ReadAsByteArrayAsync();
        var expected = rotations.Split(',').Select(int.Parse).ToArray();
        CollectionAssert.AreEqual(expected, await test.ReadPageRotationsAsync(output));
        await test.AssertValidPdfAsync(output, expected.Length);
        test.AssertClean();
    }

    [TestMethod]
    public async Task DeletePages_RemovesDeletedPageContent()
    {
        await using var test = new PdfTestContext();
        var keep = Path.Combine(test.Root, "keep.pdf");
        var remove = Path.Combine(test.Root, "remove.pdf");
        var input = Path.Combine(test.Root, "sentinel-input.pdf");
        await File.WriteAllBytesAsync(keep, PdfTestContext.Fixture);
        await File.WriteAllBytesAsync(remove, Encoding.ASCII.GetBytes(
            Encoding.ASCII.GetString(PdfTestContext.Fixture).Replace("Hello PDF", "REMOVEME!", StringComparison.Ordinal)));
        Assert.AreEqual(0, (await test.QpdfAsync("--empty", "--pages", keep, "1", remove, "1", "--", input)).ExitCode);

        using var form = PdfTestContext.FileForm(await File.ReadAllBytesAsync(input));
        using var response = await test.Client.PostAsync("/api/pdf/delete-pages?pages=2", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var output = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(output, 1);
        var selected = Path.Combine(test.Root, "selected.pdf");
        var qdf = Path.Combine(test.Root, "selected-qdf.pdf");
        await File.WriteAllBytesAsync(selected, output);
        Assert.AreEqual(0, (await test.QpdfAsync("--qdf", "--object-streams=disable", selected, qdf)).ExitCode);
        var normalized = await File.ReadAllTextAsync(qdf, Encoding.Latin1);
        StringAssert.Contains(normalized, "Hello PDF");
        Assert.IsFalse(normalized.Contains("REMOVEME!", StringComparison.Ordinal));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("2,1,3-4", "90,0,180,270")]
    [DataRow("4,3,2,1", "270,180,90,0")]
    [DataRow("1-4", "0,90,180,270")]
    public async Task Reorder_RequiresAndUsesEveryPageOnce(string pages, string rotations)
    {
        await using var test = new PdfTestContext();
        var input = await test.CreateRotationMarkedPdfAsync(4);
        using var form = PdfTestContext.FileForm(input, "../../untrusted.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/reorder?pages=" + pages, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("reordered.pdf", response.Content.Headers.ContentDisposition?.FileName);
        var output = await response.Content.ReadAsByteArrayAsync();
        var expected = rotations.Split(',').Select(int.Parse).ToArray();
        CollectionAssert.AreEqual(expected, await test.ReadPageRotationsAsync(output));
        await test.AssertValidPdfAsync(output, expected.Length);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("/api/pdf/extract", "pages=5")]
    [DataRow("/api/pdf/extract", "pages=1,1")]
    [DataRow("/api/pdf/extract", "pages=1%202")]
    [DataRow("/api/pdf/delete-pages", "pages=1-4")]
    [DataRow("/api/pdf/delete-pages", "pages=5")]
    [DataRow("/api/pdf/reorder", "pages=1-3")]
    [DataRow("/api/pdf/reorder", "pages=1,1,2,3")]
    [DataRow("/api/pdf/reorder", "pages=1-3,5")]
    public async Task InvalidOperationSpecificPages_Return400(string path, string query)
    {
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(4);
        using var form = PdfTestContext.FileForm(input);
        await AssertProblemAsync(test, path + "?" + query, form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow("/api/pdf/extract")]
    [DataRow("/api/pdf/delete-pages")]
    [DataRow("/api/pdf/reorder")]
    public async Task MissingOrDuplicatePages_Return400BeforeQpdf(string path)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using (var missing = PdfTestContext.FileForm(PdfTestContext.Fixture))
            await AssertProblemAsync(test, path, missing, HttpStatusCode.BadRequest);
        using (var duplicate = PdfTestContext.FileForm(PdfTestContext.Fixture))
            await AssertProblemAsync(test, path + "?pages=1&pages=2", duplicate, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task OversizedReorderPages_Returns400BeforeQpdf()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        await AssertProblemAsync(test, "/api/pdf/reorder?pages=" + new string('1', Amane.Pdf.Api.PdfPageSelection.MaxLength + 1),
            form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task DeletePages_PassesCompactComplementRatherThanRawQueryToQpdf()
    {
        if (!RequireLinux()) return;
        var wrapperRoot = Path.Combine(Path.GetTempPath(), "amane-qpdf-selection-argv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wrapperRoot);
        try
        {
            var wrapper = Path.Combine(wrapperRoot, "qpdf.sh");
            await File.WriteAllTextAsync(wrapper,
                $"#!/bin/sh\nif [ \"$2\" = --pages ]; then printf '%s\\n' \"$@\" > '{wrapperRoot}/argv'; fi\nexec qpdf \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
            using var form = PdfTestContext.FileForm(await test.CreatePagedPdfAsync(10));
            using var response = await test.Client.PostAsync("/api/pdf/delete-pages?pages=5,2", form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var arguments = await File.ReadAllLinesAsync(Path.Combine(wrapperRoot, "argv"));
            Assert.AreEqual("--pages", arguments[1]);
            Assert.AreEqual(".", arguments[2]);
            Assert.AreEqual("1,3-4,6-10", arguments[3]);
            Assert.IsFalse(arguments.Contains("5,2"));
            test.AssertClean();
        }
        finally
        {
            Directory.Delete(wrapperRoot, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("/api/pdf/extract?pages=1")]
    [DataRow("/api/pdf/delete-pages?pages=1")]
    [DataRow("/api/pdf/reorder?pages=1")]
    public async Task InvalidAndEncryptedPdf_Return422(string path)
    {
        await using var test = new PdfTestContext();
        using (var invalid = PdfTestContext.FileForm(Encoding.UTF8.GetBytes("PDF-CONTENT-SENTINEL: not a PDF")))
            await AssertProblemAsync(test, path, invalid, HttpStatusCode.UnprocessableEntity);

        using var protectForm = PdfTestContext.Form(PdfTestContext.Fixture);
        using var protectedResponse = await test.Client.PostAsync("/api/pdf/protect", protectForm);
        Assert.AreEqual(HttpStatusCode.OK, protectedResponse.StatusCode);
        using var encrypted = PdfTestContext.FileForm(await protectedResponse.Content.ReadAsByteArrayAsync());
        await AssertProblemAsync(test, path, encrypted, HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    [DataRow(2, 500)]
    [DataRow(3, 422)]
    [DataRow(9, 500)]
    public async Task PageSelectionFailure_IsSanitizedAndCleansFiles(int exitCode, int status)
    {
        if (!RequireLinux()) return;
        var wrapperRoot = Path.Combine(Path.GetTempPath(), "amane-qpdf-selection-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wrapperRoot);
        try
        {
            var wrapper = Path.Combine(wrapperRoot, "qpdf.sh");
            await File.WriteAllTextAsync(wrapper,
                $"#!/bin/sh\nif [ \"$2\" = --pages ]; then echo 'stderr PDF-CONTENT-SENTINEL {wrapperRoot}' >&2; exit {exitCode}; fi\nexec qpdf \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
            using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
            await AssertProblemAsync(test, "/api/pdf/extract?pages=1", form, (HttpStatusCode)status);
            Assert.IsFalse(test.Logs.Any(log => log.Contains(wrapperRoot, StringComparison.Ordinal) ||
                log.Contains("PDF-CONTENT-SENTINEL", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(wrapperRoot, recursive: true);
        }
    }

    private static async Task AssertProblemAsync(PdfTestContext test, string path, HttpContent form, HttpStatusCode expected)
    {
        using var response = await test.Client.PostAsync(path, form);
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
