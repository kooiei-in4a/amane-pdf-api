using System.Net;
using System.Runtime.Versioning;
using System.Text;
using Amane.Pdf.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfRotateTests
{
    [TestMethod]
    [DataRow(90)]
    [DataRow(180)]
    [DataRow(270)]
    public async Task Angle_RotatesAllPages_AndReturnsFixedFileName(int angle)
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture, "../../untrusted.pdf");
        using var response = await test.Client.PostAsync($"/api/pdf/rotate?angle={angle}", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("rotated.pdf", response.Content.Headers.ContentDisposition?.FileName);
        CollectionAssert.AreEqual(new[] { angle }, await test.ReadPageRotationsAsync(await response.Content.ReadAsByteArrayAsync()));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(null, "1,2,3,4,5")]
    [DataRow("1", "1")]
    [DataRow("1,3", "1,3")]
    [DataRow("1-3", "1,2,3")]
    [DataRow("5,1,3-4", "1,3,4,5")]
    [DataRow("3-3", "3")]
    public async Task Pages_RotatesOnlySelectedPages(string? pages, string selected)
    {
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(5);
        using var form = PdfTestContext.FileForm(input);
        var query = pages is null ? string.Empty : "&pages=" + pages;
        using var response = await test.Client.PostAsync("/api/pdf/rotate?angle=90" + query, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var selectedPages = selected.Split(',').Select(int.Parse).ToHashSet();
        var expected = Enumerable.Range(1, 5).Select(page => selectedPages.Contains(page) ? 90 : 0).ToArray();
        CollectionAssert.AreEqual(expected, await test.ReadPageRotationsAsync(await response.Content.ReadAsByteArrayAsync()));
        test.AssertClean();
    }

    [TestMethod]
    public async Task Rotation_IsRelative_ForRepeatedAndInitiallyRotatedPdf()
    {
        await using var test = new PdfTestContext();
        using var firstForm = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var first = await test.Client.PostAsync("/api/pdf/rotate?angle=90", firstForm);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var onceRotated = await first.Content.ReadAsByteArrayAsync();
        using var secondForm = PdfTestContext.FileForm(onceRotated);
        using var second = await test.Client.PostAsync("/api/pdf/rotate?angle=90", secondForm);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        CollectionAssert.AreEqual(new[] { 180 }, await test.ReadPageRotationsAsync(await second.Content.ReadAsByteArrayAsync()));

        var input = Path.Combine(test.Root, "input.pdf");
        var rotated180 = Path.Combine(test.Root, "rotated-180.pdf");
        await File.WriteAllBytesAsync(input, PdfTestContext.Fixture);
        Assert.AreEqual(0, (await test.QpdfAsync("--rotate=+180", input, rotated180)).ExitCode);
        using var thirdForm = PdfTestContext.FileForm(await File.ReadAllBytesAsync(rotated180));
        using var third = await test.Client.PostAsync("/api/pdf/rotate?angle=90", thirdForm);
        Assert.AreEqual(HttpStatusCode.OK, third.StatusCode);
        CollectionAssert.AreEqual(new[] { 270 }, await test.ReadPageRotationsAsync(await third.Content.ReadAsByteArrayAsync()));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("+90")]
    [DataRow("090")]
    [DataRow("450")]
    [DataRow("0")]
    [DataRow("-90")]
    [DataRow("invalid")]
    public async Task InvalidAngle_Returns400_BeforeQpdf(string angle)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        await AssertProblemAsync(test, "/api/pdf/rotate?angle=" + angle, form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task MissingOrDuplicateAngle_Returns400()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var missing = PdfTestContext.FileForm(PdfTestContext.Fixture);
        await AssertProblemAsync(test, "/api/pdf/rotate", missing, HttpStatusCode.BadRequest);
        using var duplicate = PdfTestContext.FileForm(PdfTestContext.Fixture);
        await AssertProblemAsync(test, "/api/pdf/rotate?angle=90&angle=180", duplicate, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("01")]
    [DataRow("-1")]
    [DataRow("3-1")]
    [DataRow("1,,2")]
    [DataRow("1,")]
    [DataRow(",1")]
    [DataRow("1 2")]
    [DataRow("1,a")]
    [DataRow("1,1")]
    [DataRow("1-3,3")]
    [DataRow("")]
    public async Task InvalidPages_Returns400_BeforeQpdf(string pages)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        await AssertProblemAsync(test, "/api/pdf/rotate?angle=90&pages=" + pages, form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task DuplicateOrOversizedPagesQuery_Returns400_BeforeReadingBody()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        foreach (var query in new[] { "pages=1&pages=2", "pages=" + new string('1', PdfPageSelection.MaxLength + 1) })
        {
            var body = new UnreadableStream();
            var context = new DefaultHttpContext { RequestServices = test.Factory.Services };
            context.Request.QueryString = new QueryString("?angle=90&" + query);
            context.Request.ContentType = "multipart/form-data; boundary=must-not-read";
            context.Request.Body = body;
            context.Response.Body = new MemoryStream();
            await PdfRotateEndpoint.HandleAsync(context,
                test.Factory.Services.GetRequiredService<QpdfProcessor>(),
                test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>());
            Assert.AreEqual(400, context.Response.StatusCode);
            Assert.AreEqual(0, body.ReadCount);
        }
        test.AssertClean();
    }

    [TestMethod]
    public async Task PageOutsideDocument_Returns400()
    {
        await using var test = new PdfTestContext();
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        await AssertProblemAsync(test, "/api/pdf/rotate?angle=90&pages=2", form, HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task PagesOmitted_SkipsPageCount_WhilePagesSpecifiedUsesIt()
    {
        if (!RequireLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "amane-qpdf-calls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wrapper = Path.Combine(root, "qpdf.sh");
            await File.WriteAllTextAsync(wrapper, $"#!/bin/sh\nprintf '%s\\n' \"$1\" >> '{root}/calls'\nexec qpdf \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
            using (var allPages = PdfTestContext.FileForm(PdfTestContext.Fixture))
            using (var response = await test.Client.PostAsync("/api/pdf/rotate?angle=90", allPages))
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse((await File.ReadAllLinesAsync(Path.Combine(root, "calls"))).Contains("--show-npages"));

            using (var onePage = PdfTestContext.FileForm(PdfTestContext.Fixture))
            using (var response = await test.Client.PostAsync("/api/pdf/rotate?angle=90&pages=1", onePage))
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue((await File.ReadAllLinesAsync(Path.Combine(root, "calls"))).Contains("--show-npages"));
            test.AssertClean();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OversizedPageCountOutput_IsRejectedWithoutDisclosure()
    {
        if (!RequireLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "amane-qpdf-page-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wrapper = Path.Combine(root, "qpdf.sh");
            await File.WriteAllTextAsync(wrapper,
                $"#!/bin/sh\ncase \"$1\" in\n--show-npages) printf '%066d' 1; echo ' PDF-CONTENT-SENTINEL {root}' >&2; exit 0;;\n*) exec qpdf \"$@\";;\nesac\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
            using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
            await AssertProblemAsync(test, "/api/pdf/rotate?angle=90&pages=1", form, HttpStatusCode.InternalServerError);
            Assert.IsFalse(test.Logs.Any(log => log.Contains(root, StringComparison.Ordinal) || log.Contains("PDF-CONTENT-SENTINEL", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
        await AssertProblemAsync(test, "/api/pdf/rotate?angle=90", form, HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    public async Task EncryptedPdf_Returns422()
    {
        await using var test = new PdfTestContext();
        using var protectForm = PdfTestContext.Form(PdfTestContext.Fixture);
        using var protectedResponse = await test.Client.PostAsync("/api/pdf/protect", protectForm);
        Assert.AreEqual(HttpStatusCode.OK, protectedResponse.StatusCode);
        using var rotateForm = PdfTestContext.FileForm(await protectedResponse.Content.ReadAsByteArrayAsync());
        await AssertProblemAsync(test, "/api/pdf/rotate?angle=90", rotateForm, HttpStatusCode.UnprocessableEntity);
    }

    [TestMethod]
    [DataRow(2, 500)]
    [DataRow(3, 422)]
    [DataRow(9, 500)]
    public async Task RotateFailure_IsSanitized_AndCleansFiles(int exitCode, int status)
    {
        if (!RequireLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "amane-qpdf-rotate-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wrapper = Path.Combine(root, "qpdf.sh");
            await File.WriteAllTextAsync(wrapper,
                $"#!/bin/sh\ncase \"$1\" in\n--rotate=*) echo 'stderr PDF-CONTENT-SENTINEL {root}' >&2; exit {exitCode};;\n*) exec qpdf \"$@\";;\nesac\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
            using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
            await AssertProblemAsync(test, "/api/pdf/rotate?angle=90", form, (HttpStatusCode)status);
            Assert.IsFalse(test.Logs.Any(log => log.Contains(root, StringComparison.Ordinal) || log.Contains("PDF-CONTENT-SENTINEL", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PasswordAndDuplicateFile_AreRejected()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var missing = PdfTestContext.FileForm();
        await AssertProblemAsync(test, "/api/pdf/rotate?angle=90", missing, HttpStatusCode.BadRequest);
        using var password = PdfTestContext.Form(PdfTestContext.Fixture);
        await AssertProblemAsync(test, "/api/pdf/rotate?angle=90", password, HttpStatusCode.BadRequest);
        using var duplicate = PdfTestContext.FileForm(PdfTestContext.Fixture);
        duplicate.Add(new ByteArrayContent(PdfTestContext.Fixture), "file", "extra.pdf");
        await AssertProblemAsync(test, "/api/pdf/rotate?angle=90", duplicate, HttpStatusCode.BadRequest);
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

    private sealed class UnreadableStream : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("Request body must not be read.");
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("Request body must not be read.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
