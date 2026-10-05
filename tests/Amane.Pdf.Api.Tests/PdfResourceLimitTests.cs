using System.Net;
using System.Text;
using Amane.Pdf.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfResourceLimitTests
{
    [TestMethod]
    [DataRow(0, 200)]
    [DataRow(-1, 413)]
    public async Task RotateFileSizeBoundary_IsEnforcedBeforeQpdf(int difference, int status)
    {
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxFileBytes"] = (PdfTestContext.Fixture.Length + difference).ToString(),
            ["Pdf:QpdfPath"] = difference < 0 ? "/must-not-run/qpdf" : "qpdf"
        });
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/rotate?angle=90", form);
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(0, 200)]
    [DataRow(-1, 413)]
    public async Task FileSizeBoundary_IsEnforcedBeforeQpdf(int difference, int status)
    {
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxFileBytes"] = (PdfTestContext.Fixture.Length + difference).ToString(),
            ["Pdf:QpdfPath"] = difference < 0 ? "/must-not-run/qpdf" : "qpdf"
        });
        using var form = PdfTestContext.Form(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/protect", form);
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HugeBody_StopsReadingEarly_WithOrWithoutContentLength(bool advertisedLength)
    {
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxFileBytes"] = "1024",
            ["Pdf:QpdfPath"] = "/must-not-run/qpdf"
        });
        using var stream = new HugeMultipartStream();
        var context = new DefaultHttpContext { RequestServices = test.Factory.Services };
        context.Request.ContentType = "multipart/form-data; boundary=limit-boundary";
        context.Request.ContentLength = advertisedLength ? stream.TotalLength : null;
        context.Request.Body = stream;
        context.Response.Body = new MemoryStream();
        await PdfProtectEndpoint.HandleAsync(context,
            test.Factory.Services.GetRequiredService<QpdfProcessor>(), test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>());
        Assert.AreEqual(413, context.Response.StatusCode);
        Assert.IsTrue(stream.BytesRead < 16 * 1024, "The oversized upload was consumed instead of being rejected early.");
        if (advertisedLength) Assert.AreEqual(0L, stream.BytesRead);
        test.AssertClean();
    }

    [TestMethod]
    public async Task AdvertisedRequestAboveEnvelopeLimit_IsRejectedBeforeQpdf()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:MaxFileBytes"] = "1024", ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var form = PdfTestContext.Form(PdfTestContext.Fixture);
        var bytes = await form.ReadAsByteArrayAsync();
        var body = new byte[1024 + PdfOptions.MultipartOverheadBytes + 1];
        bytes.CopyTo(body, 0);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = form.Headers.ContentType;
        using var response = await test.Client.PostAsync("/api/pdf/protect", content);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    public async Task ActualRequestBytes_IncludeMetadata_WhenContentLengthIsMissing()
    {
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString(),
            ["Pdf:QpdfPath"] = "/must-not-run/qpdf"
        });
        var prefix = new string('P', 16350) + "\r\n--limit-boundary\r\n" +
            "Content-Disposition: form-data; name=\"file\"; filename=\"input.pdf\"\r\n" +
            "X-Padding: " + new string('H', 16300) + "\r\n\r\n";
        var suffix = "\r\n--limit-boundary\r\nContent-Disposition: form-data; name=\"password\"\r\n" +
            "X-Padding: " + new string('H', 16300) + "\r\n\r\nfixture-password\r\n--limit-boundary--\r\n" + new string('E', 16370);
        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes(prefix));
        body.Write(PdfTestContext.Fixture);
        body.Write(Encoding.ASCII.GetBytes(suffix));
        body.Position = 0;
        var context = new DefaultHttpContext { RequestServices = test.Factory.Services };
        context.Request.ContentType = "multipart/form-data; boundary=limit-boundary";
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        await PdfProtectEndpoint.HandleAsync(context,
            test.Factory.Services.GetRequiredService<QpdfProcessor>(), test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>());
        Assert.AreEqual(413, context.Response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("--is-encrypted")]
    [DataRow("--check")]
    [DataRow("--job-json-file=*")]
    public async Task Timeout_KillsProcessTree_CleansFiles_AndReleasesPermit(string blockPattern)
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf(blockPattern);
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "1",
            ["Pdf:MaxConcurrentProcesses"] = "1"
        });
        using var first = PdfTestContext.Form(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/protect", first);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains(test.Root, StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("test-password", StringComparison.Ordinal));
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
        using var second = PdfTestContext.Form(PdfTestContext.Fixture);
        using var next = await test.Client.PostAsync("/api/pdf/protect", second);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, next.StatusCode);
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("--show-npages", "&pages=1")]
    [DataRow("--rotate=*", "")]
    public async Task RotateTimeout_KillsPageCountOrRotateProcess_AndCleansFiles(string blockPattern, string pages)
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf(blockPattern);
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "1",
            ["Pdf:MaxConcurrentProcesses"] = "1"
        });
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/rotate?angle=90" + pages, form);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
    }

    [TestMethod]
    public async Task PageSelectionTimeout_KillsProcessTree_AndCleansFiles()
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf("*/input.pdf");
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "1"
        });
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/extract?pages=1", form);
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
    }

    [TestMethod]
    public async Task ConcurrencyLimit_RejectsThirdRequest_WithoutQueue_AndKeepsHealthAvailable()
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf();
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first = PdfTestContext.Form(PdfTestContext.Fixture);
        using var second = PdfTestContext.Form(PdfTestContext.Fixture);
        var firstJob = test.Client.PostAsync("/api/pdf/protect", first, cancellation.Token);
        var secondJob = test.Client.PostAsync("/api/pdf/protect", second, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(2);
            using var third = PdfTestContext.Form(PdfTestContext.Fixture);
            using var response = await test.Client.PostAsync("/api/pdf/protect", third).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual(2, Directory.GetFiles(blocker.Root, "*.pids").Length);
            using var health = await test.Client.GetAsync("/healthz");
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveCancellationAsync(firstJob);
            await ObserveCancellationAsync(secondJob);
        }
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    public async Task ProtectAndRotate_ShareConcurrencyLimiter()
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf("--rotate=*");
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:MaxConcurrentProcesses"] = "1"
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var rotateForm = PdfTestContext.FileForm(PdfTestContext.Fixture);
        var rotateJob = test.Client.PostAsync("/api/pdf/rotate?angle=90", rotateForm, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
            using var protectForm = PdfTestContext.Form(PdfTestContext.Fixture);
            using var response = await test.Client.PostAsync("/api/pdf/protect", protectForm).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveCancellationAsync(rotateJob);
        }
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
    }

    [TestMethod]
    public async Task PageSelectionEndpoints_ShareConcurrencyLimiter()
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf("*/input.pdf");
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:MaxConcurrentProcesses"] = "3"
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var extract = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var delete = PdfTestContext.FileForm(await test.CreatePagedPdfAsync(2));
        using var reorder = PdfTestContext.FileForm(PdfTestContext.Fixture);
        var jobs = new[]
        {
            test.Client.PostAsync("/api/pdf/extract?pages=1", extract, cancellation.Token),
            test.Client.PostAsync("/api/pdf/delete-pages?pages=2", delete, cancellation.Token),
            test.Client.PostAsync("/api/pdf/reorder?pages=1", reorder, cancellation.Token)
        };
        try
        {
            await blocker.WaitForJobsAsync(3);
            using var protect = PdfTestContext.Form(PdfTestContext.Fixture);
            using var response = await test.Client.PostAsync("/api/pdf/protect", protect).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var job in jobs) await ObserveCancellationAsync(job);
        }
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
    }

    [TestMethod]
    public async Task RotateCancellationDuringPageCount_KillsProcessTree_AndCleansFiles()
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf("--show-npages");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        var job = test.Client.PostAsync("/api/pdf/rotate?angle=90&pages=1", form, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveCancellationAsync(job);
        }
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
    }

    [TestMethod]
    public async Task PageSelectionCancellation_KillsProcessTree_AndCleansFiles()
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf("*/input.pdf");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
        var job = test.Client.PostAsync("/api/pdf/extract?pages=1", form, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveCancellationAsync(job);
        }
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
    }

    [TestMethod]
    public async Task ClientCancellation_KillsProcessTree_AndCleansFiles()
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf();
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var form = PdfTestContext.Form(PdfTestContext.Fixture);
        var job = test.Client.PostAsync("/api/pdf/protect", form, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveCancellationAsync(job);
        }
        await blocker.AssertStoppedAsync();
        await WaitForCleanupAsync(test);
    }

    [TestMethod]
    public async Task CancelledUpload_CleansPartialFile()
    {
        await using var test = new PdfTestContext();
        using var cancellation = new CancellationTokenSource();
        using var stream = new HugeMultipartStream(cancellation);
        var context = new DefaultHttpContext { RequestServices = test.Factory.Services, RequestAborted = cancellation.Token };
        context.Request.ContentType = "multipart/form-data; boundary=limit-boundary";
        context.Request.Body = stream;
        context.Response.Body = new MemoryStream();
        await PdfProtectEndpoint.HandleAsync(context,
            test.Factory.Services.GetRequiredService<QpdfProcessor>(), test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>());
        Assert.IsTrue(cancellation.IsCancellationRequested);
        test.AssertClean();
    }

    private static bool RequireLinux()
    {
        if (OperatingSystem.IsLinux()) return true;
        Assert.Inconclusive("Linuxのprocess treeを検証します。");
        return false;
    }

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

    private sealed class HugeMultipartStream(CancellationTokenSource? cancellation = null) : Stream
    {
        private readonly byte[] prefix = Encoding.ASCII.GetBytes("--limit-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"input.pdf\"\r\n\r\n");
        public long TotalLength => 1024L * 1024 * 1024;
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cancellation is not null && BytesRead >= 8192)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            var count = (int)Math.Min(buffer.Length, TotalLength - BytesRead);
            buffer.Span[..count].Fill((byte)'X');
            if (BytesRead < prefix.Length)
            {
                var part = Math.Min(count, prefix.Length - (int)BytesRead);
                prefix.AsMemory((int)BytesRead, part).CopyTo(buffer);
            }
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
