using System.Net;
using System.Text;
using Amane.Pdf.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfUnlockResourceTests
{
    [TestMethod]
    [DataRow("--requires-password")]
    [DataRow("--job-json-file=*/job.json")]
    [DataRow("--job-json-file=*/unlock-check.json")]
    [DataRow("--job-json-file=*/unlock-decrypt.json")]
    [DataRow("--is-encrypted")]
    [DataRow("--check")]
    public async Task Unlock_TimeoutAtEveryStage_StopsProcessTreeCleansFilesAndReleasesPermit(string stage)
    {
        if (!PdfUnlockTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf(stage);
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "1",
            ["Pdf:MaxConcurrentProcesses"] = "1"
        });
        var encrypted = await test.CreateEncryptedPdfAsync(PdfUnlockTests.UserPassword, PdfUnlockTests.OwnerPassword);
        using var form = PdfTestContext.Form(encrypted, PdfUnlockTests.UserPassword);
        await PdfUnlockTests.AssertProblemAsync(test, form, 504);
        await blocker.AssertStoppedAsync();
        using var next = PdfTestContext.Form(encrypted, PdfUnlockTests.UserPassword);
        // A second 504 rather than 503 proves that the permit was released.
        await PdfUnlockTests.AssertProblemAsync(test, next, 504);
        await blocker.AssertStoppedAsync();
    }

    [TestMethod]
    [DataRow("--requires-password", false)]
    [DataRow("--job-json-file=*/job.json", false)]
    [DataRow("--job-json-file=*/unlock-check.json", false)]
    [DataRow("--job-json-file=*/unlock-decrypt.json", false)]
    [DataRow("--is-encrypted", false)]
    [DataRow("--check", false)]
    [DataRow("--requires-password", true)]
    [DataRow("--job-json-file=*/job.json", true)]
    [DataRow("--job-json-file=*/unlock-check.json", true)]
    [DataRow("--job-json-file=*/unlock-decrypt.json", true)]
    [DataRow("--is-encrypted", true)]
    [DataRow("--check", true)]
    public async Task Unlock_CancellationOrApplicationStoppingAtEveryStage_StopsProcessTreeAndCleansFiles(string stage, bool stopping)
    {
        if (!PdfUnlockTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf(stage);
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        var encrypted = await test.CreateEncryptedPdfAsync(PdfUnlockTests.UserPassword, PdfUnlockTests.OwnerPassword);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var form = PdfTestContext.Form(encrypted, PdfUnlockTests.UserPassword);
        var job = test.Client.PostAsync("/api/pdf/unlock", form, cancellation.Token);
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
        PdfUnlockTests.AssertNoExposure(test);
    }

    [TestMethod]
    public async Task Unlock_TimeBudgetIsSharedAcrossPasswordAuthenticationAndInputCheck()
    {
        if (!PdfUnlockTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf("--job-json-file=*/unlock-decrypt.json", checkDelaySeconds: 1.8,
            delayPattern: "--job-json-file=*");
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "3"
        });
        var encrypted = await test.CreateEncryptedPdfAsync(PdfUnlockTests.UserPassword, PdfUnlockTests.OwnerPassword);
        using var form = PdfTestContext.Form(encrypted, PdfUnlockTests.UserPassword);
        await PdfUnlockTests.AssertProblemAsync(test, form, 504);
        await blocker.AssertStoppedAsync();
        var calls = blocker.Calls;
        Assert.AreEqual(3, calls.Length);
        Assert.AreEqual("--requires-password", calls[0]);
        StringAssert.EndsWith(calls[1], "/job.json");
        StringAssert.EndsWith(calls[2], "/unlock-check.json");
    }

    [TestMethod]
    public async Task Unlock_OutputCheckReceivesOnlyRemainingTimeBudget()
    {
        if (!PdfUnlockTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf("--check", checkDelaySeconds: 1,
            delayPattern: "--job-json-file=*/job.json");
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "3"
        });
        var encrypted = await test.CreateEncryptedPdfAsync(PdfUnlockTests.UserPassword, PdfUnlockTests.OwnerPassword);
        using var form = PdfTestContext.Form(encrypted, PdfUnlockTests.UserPassword);
        var job = test.Client.PostAsync("/api/pdf/unlock", form);
        await blocker.WaitForJobsAsync(2);
        using var response = await job.WaitAsync(TimeSpan.FromSeconds(2.5));
        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.AreEqual(6, blocker.Calls.Length);
        Assert.AreEqual("--check", blocker.Calls[^1]);
        await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    [TestMethod]
    public async Task Unlock_SharesConcurrencyLimiterWithProtect_AndKeepsHealthAvailable()
    {
        if (!PdfUnlockTests.RequireLinux()) return;
        using var blocker = new BlockingQpdf("--job-json-file=*/unlock-check.json");
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:MaxConcurrentProcesses"] = "1"
        });
        var encrypted = await test.CreateEncryptedPdfAsync(PdfUnlockTests.UserPassword, PdfUnlockTests.OwnerPassword);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var form = PdfTestContext.Form(encrypted, PdfUnlockTests.UserPassword);
        var job = test.Client.PostAsync("/api/pdf/unlock", form, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
            using var protect = PdfTestContext.Form(PdfTestContext.Fixture);
            using var rejected = await test.Client.PostAsync("/api/pdf/protect", protect).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
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
        using var next = PdfTestContext.Form(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/protect", next);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(false, -1)]
    [DataRow(true, -1)]
    public async Task Unlock_FileSizeBoundaryWithOrWithoutContentLength_IsEnforced(bool advertised, int difference)
    {
        await using var generator = new PdfTestContext();
        var encrypted = await generator.CreateEncryptedPdfAsync(PdfUnlockTests.UserPassword, PdfUnlockTests.OwnerPassword);
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxFileBytes"] = (encrypted.Length + difference).ToString(),
            ["Pdf:QpdfPath"] = difference < 0 ? "/must-not-run/qpdf" : "qpdf"
        });
        using var form = PdfTestContext.Form(encrypted, PdfUnlockTests.UserPassword);
        using var body = new MemoryStream(await form.ReadAsByteArrayAsync());
        var context = Context(test, body, form.Headers.ContentType!.ToString(), advertised);
        await HandleAsync(test, context);
        Assert.AreEqual(difference < 0 ? 413 : 200, context.Response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Unlock_RequestEnvelopeLimitIncludesEpilogue_WithOrWithoutContentLength(bool advertised)
    {
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString(),
            ["Pdf:QpdfPath"] = "/must-not-run/qpdf"
        });
        // Keep each MIME preamble/header/epilogue below MultipartReader's own limits,
        // while exceeding the total request envelope with valid multipart metadata.
        var prefix = new string('P', 16350) + "\r\n--limit-boundary\r\n" +
            "Content-Disposition: form-data; name=\"file\"; filename=\"input.pdf\"\r\n" +
            "X-Padding: " + new string('H', 16300) + "\r\n\r\n";
        var suffix = "\r\n--limit-boundary\r\nContent-Disposition: form-data; name=\"password\"\r\n" +
            "X-Padding: " + new string('H', 16300) + "\r\n\r\n" + PdfUnlockTests.UserPassword +
            "\r\n--limit-boundary--\r\n" + new string('E', 16370);
        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes(prefix));
        body.Write(PdfTestContext.Fixture);
        body.Write(Encoding.ASCII.GetBytes(suffix));
        body.Position = 0;
        var context = Context(test, body, "multipart/form-data; boundary=limit-boundary", advertised);
        await HandleAsync(test, context);
        Assert.AreEqual(413, context.Response.StatusCode);
        if (advertised) Assert.AreEqual(0L, body.Position);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Unlock_CancelledOrStoppingUpload_CleansPartialFiles(bool stopping)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var cancellation = new CancellationTokenSource();
        var lifetime = test.Factory.Services.GetRequiredService<IHostApplicationLifetime>();
        var prefix = Encoding.ASCII.GetBytes("--upload-boundary\r\nContent-Disposition: form-data; name=\"file\"; filename=\"input.pdf\"\r\n\r\n%PDF-1.4\npartial");
        using var body = new CancelUploadStream(prefix, stopping ? lifetime.StopApplication : cancellation.Cancel);
        var context = Context(test, body, "multipart/form-data; boundary=upload-boundary", advertised: false);
        context.RequestAborted = cancellation.Token;
        await HandleAsync(test, context);
        Assert.IsTrue(stopping ? lifetime.ApplicationStopping.IsCancellationRequested : cancellation.IsCancellationRequested);
        Assert.AreEqual(0L, context.Response.Body.Length);
        test.AssertClean();
    }

    private static DefaultHttpContext Context(PdfTestContext test, Stream body, string contentType, bool advertised)
    {
        var context = new DefaultHttpContext { RequestServices = test.Factory.Services };
        context.Request.Body = body;
        context.Request.ContentType = contentType;
        context.Request.ContentLength = advertised ? body.Length : null;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Task HandleAsync(PdfTestContext test, HttpContext context) => PdfUnlockEndpoint.HandleAsync(context,
        test.Factory.Services.GetRequiredService<QpdfProcessor>(), test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>());

    private static async Task ObserveCancellationAsync(Task<HttpResponseMessage> job)
    {
        try { using var response = await job.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }

    private static async Task WaitForCleanupAsync(PdfTestContext test)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Directory.Exists(test.TempRoot) && Directory.EnumerateFileSystemEntries(test.TempRoot).Any())
            await Task.Delay(20, timeout.Token);
        test.AssertClean();
    }

    private sealed class CancelUploadStream(byte[] prefix, Action cancel) : MemoryStream(prefix)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length)
            {
                cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
