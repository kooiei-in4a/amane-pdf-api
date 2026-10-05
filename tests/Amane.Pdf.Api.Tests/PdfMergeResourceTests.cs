using System.Net;
using System.Text;
using Amane.Pdf.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfMergeResourceTests
{
    [TestMethod]
    [DataRow("Pdf:MaxFileBytes", 0, 200)]
    [DataRow("Pdf:MaxFileBytes", -1, 413)]
    [DataRow("Pdf:MaxMergeInputBytes", 0, 200)]
    [DataRow("Pdf:MaxMergeInputBytes", -1, 413)]
    public async Task FileAndTotalSizeBoundaries_AreEnforcedBeforeQpdf(string key, int difference, int status)
    {
        var bytes = PdfTestContext.Fixture.Length * (key == "Pdf:MaxFileBytes" ? 1 : 2);
        await using var test = new PdfTestContext(new()
        {
            [key] = (bytes + difference).ToString(),
            ["Pdf:QpdfPath"] = difference < 0 ? "/must-not-run/qpdf" : "qpdf"
        });
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/merge", form);
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HugeMerge_StopsReadingEarly_WithAndWithoutContentLength(bool contentLength)
    {
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxMergeInputBytes"] = (2 * PdfTestContext.Fixture.Length).ToString(),
            ["Pdf:QpdfPath"] = "/must-not-run/qpdf"
        });
        using var stream = new HugeMergeStream();
        var context = Context(test, stream);
        context.Request.ContentLength = contentLength ? stream.TotalLength : null;
        await HandleAsync(test, context);
        Assert.AreEqual(413, context.Response.StatusCode);
        Assert.IsTrue(stream.BytesRead < 16 * 1024, "An oversized merge input must not be consumed in full.");
        if (contentLength) Assert.AreEqual(0L, stream.BytesRead);
        PdfMergeTests.AssertNoExposure(test, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        test.AssertClean();
    }

    [TestMethod]
    public async Task TotalOverflow_NeverWritesBeyondAggregateLimit()
    {
        await using var test = new PdfTestContext();
        using var files = new TemporaryPdfFiles(test.TempRoot);
        using var stream = new HugeMergeStream();
        var context = Context(test, stream);
        var limit = PdfTestContext.Fixture.Length + 10;
        var exception = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            MultipartPdfUpload.ReadMergeFilesAsync(context.Request, files,
                new PdfOptions { MaxMergeInputBytes = limit }, CancellationToken.None));
        Assert.AreEqual(413, exception.StatusCode);
        var stored = Directory.GetFiles(files.DirectoryPath).Sum(path => new FileInfo(path).Length);
        Assert.IsTrue(stored <= limit);
        Assert.AreEqual(PdfTestContext.Fixture.Length, new FileInfo(files.MergeInputPath(1)).Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequestEnvelopeOverflow_CountsEpilogue_WithAndWithoutContentLength(bool contentLength)
    {
        var limit = 2 * PdfTestContext.Fixture.Length;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxMergeInputBytes"] = limit.ToString(),
            ["Pdf:QpdfPath"] = "/must-not-run/qpdf"
        });
        // Keep each preamble/header/epilogue within MultipartReader's individual
        // bounds while their combined size exceeds the request envelope.
        var header = "Content-Disposition: form-data; name=\"file\"; filename=\"input.pdf\"\r\n" +
            "X-Padding: " + new string('H', 16300) + "\r\n\r\n";
        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes(new string('P', 16350) + "\r\n--merge-boundary\r\n" + header));
        body.Write(PdfTestContext.Fixture);
        body.Write(Encoding.ASCII.GetBytes("\r\n--merge-boundary\r\n" + header));
        body.Write(PdfTestContext.Fixture);
        body.Write(Encoding.ASCII.GetBytes("\r\n--merge-boundary--\r\n" + new string('E', 16370)));
        Assert.IsTrue(body.Length > limit + PdfOptions.MultipartOverheadBytes);
        body.Position = 0;
        var context = Context(test, body);
        context.Request.ContentLength = contentLength ? body.Length : null;
        await HandleAsync(test, context);
        Assert.AreEqual(413, context.Response.StatusCode);
        if (contentLength) Assert.AreEqual(0L, body.Position);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("--is-encrypted")]
    [DataRow("--check")]
    [DataRow("--empty")]
    public async Task Timeout_KillsProcessTree_CleansEveryInput_AndReleasesPermit(string stage)
    {
        if (!PdfMergeTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf(stage);
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "1",
            ["Pdf:MaxConcurrentProcesses"] = "1"
        });
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
        await PdfMergeTests.AssertProblemAsync(test, form, HttpStatusCode.GatewayTimeout);
        await blocker.AssertStoppedAsync();
        using var next = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/optimize", next);
        Assert.AreEqual(stage == "--empty" ? HttpStatusCode.OK : HttpStatusCode.GatewayTimeout, response.StatusCode);
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    public async Task TimeoutBudget_IsSharedAcrossInputValidations()
    {
        if (!PdfMergeTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf("--empty", checkDelaySeconds: 1.8);
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "3"
        });
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
        await PdfMergeTests.AssertProblemAsync(test, form, HttpStatusCode.GatewayTimeout);
        CollectionAssert.AreEqual(new[] { "--is-encrypted", "--check", "--is-encrypted", "--check" }, blocker.Calls);
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    public async Task Merge_ReceivesOnlyRemainingTimeoutBudget_AfterAllValidations()
    {
        if (!PdfMergeTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf("--empty", checkDelaySeconds: 1);
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "3"
        });
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
        var job = test.Client.PostAsync("/api/pdf/merge", form);
        await blocker.WaitForJobsAsync(3);
        using var response = await job.WaitAsync(TimeSpan.FromSeconds(2.5));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        CollectionAssert.AreEqual(new[] { "--is-encrypted", "--check", "--is-encrypted", "--check", "--empty" }, blocker.Calls);
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("--check", false)]
    [DataRow("--empty", false)]
    [DataRow("--check", true)]
    [DataRow("--empty", true)]
    public async Task CancellationOrApplicationStopping_KillsProcessTree_AndCleansFiles(string stage, bool stopping)
    {
        if (!PdfMergeTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf(stage);
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var form = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
        var job = test.Client.PostAsync("/api/pdf/merge", form, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
            if (stopping) test.Factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            else cancellation.Cancel();
            await ObserveCancellationAsync(job);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveCancellationAsync(job);
        }
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
        PdfMergeTests.AssertNoExposure(test, string.Empty);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelledOrStoppingUpload_CleansPartialInputs(bool stopping)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var cancellation = new CancellationTokenSource();
        var lifetime = test.Factory.Services.GetRequiredService<IHostApplicationLifetime>();
        using var body = new HugeMergeStream(() =>
        {
            if (stopping) lifetime.StopApplication();
            else cancellation.Cancel();
        });
        var context = Context(test, body);
        context.RequestAborted = cancellation.Token;
        await HandleAsync(test, context);
        Assert.IsTrue(stopping ? lifetime.ApplicationStopping.IsCancellationRequested : cancellation.IsCancellationRequested);
        Assert.IsTrue(body.BytesRead < 32 * 1024);
        test.AssertClean();
    }

    [TestMethod]
    public async Task MergeWithTenInputs_HoldsOnePermit_SharedWithExistingApis_WithoutQueue()
    {
        if (!PdfMergeTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf("--empty");
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:MaxConcurrentProcesses"] = "1"
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var form = PdfTestContext.MergeForm(Enumerable.Repeat(PdfTestContext.Fixture, 10).ToArray());
        var job = test.Client.PostAsync("/api/pdf/merge", form, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
            using var protect = PdfTestContext.Form(PdfTestContext.Fixture);
            using var rejected = await test.Client.PostAsync("/api/pdf/protect", protect).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.AreEqual("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
            using var merge = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
            using var rejectedMerge = await test.Client.PostAsync("/api/pdf/merge", merge).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rejectedMerge.StatusCode);
            using var health = await test.Client.GetAsync("/healthz");
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveCancellationAsync(job);
        }
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
        using var after = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/optimize", after);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        test.AssertClean();
    }

    private static DefaultHttpContext Context(PdfTestContext test, Stream body)
    {
        var context = new DefaultHttpContext { RequestServices = test.Factory.Services };
        context.Request.ContentType = "multipart/form-data; boundary=merge-boundary";
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Task HandleAsync(PdfTestContext test, HttpContext context)
        => PdfMergeEndpoint.HandleAsync(context, test.Factory.Services.GetRequiredService<QpdfProcessor>(),
            test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>());

    private static async Task ObserveCancellationAsync(Task<HttpResponseMessage> job)
    {
        try { using var response = await job.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }

    private static async Task WaitForCleanupAsync(PdfTestContext test)
    {
        for (var i = 0; i < 250 && Directory.Exists(test.TempRoot) && Directory.EnumerateFileSystemEntries(test.TempRoot).Any(); i++)
            await Task.Delay(20);
        test.AssertClean();
    }

    private sealed class HugeMergeStream(Action? cancel = null) : Stream
    {
        private readonly byte[] prefix = [..
            Encoding.ASCII.GetBytes("--merge-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"first.pdf\"\r\n\r\n"),
            .. PdfTestContext.Fixture,
            .. Encoding.ASCII.GetBytes("\r\n--merge-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"second.pdf\"\r\n\r\n")];
        public long TotalLength => 1024L * 1024 * 1024;
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BytesRead >= 8192) cancel?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, TotalLength - BytesRead);
            buffer.Span[..count].Fill((byte)'X');
            if (BytesRead < prefix.Length)
                prefix.AsMemory((int)BytesRead, Math.Min(count, prefix.Length - (int)BytesRead)).CopyTo(buffer);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
