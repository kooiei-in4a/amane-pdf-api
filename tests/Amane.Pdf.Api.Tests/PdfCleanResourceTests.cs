using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfCleanResourceTests
{
    [TestMethod]
    [DataRow("--json=2")]
    [DataRow("--job-json-file=*")]
    public async Task Timeout_CoversWholeJobStopsTreeAndReleasesPermit(string stage)
    {
        if (!PdfCleanTests.Linux()) return;
        using var blocker = new BlockingQpdf(stage);
        await using var test = new PdfTestContext(new()
        { ["Pdf:QpdfPath"] = blocker.Executable, ["Pdf:QpdfTimeoutSeconds"] = "1", ["Pdf:MaxConcurrentProcesses"] = "1" });
        for (var i = 0; i < 2; i++)
        {
            using var response = await PdfCleanTests.PostAsync(test, PdfTestContext.Fixture);
            await PdfCleanTests.ProblemAsync(test, response, 504);
            await blocker.AssertStoppedAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationOrStopping_StopsTreeAndDeletesAllFiles(bool stopping)
    {
        if (!PdfCleanTests.Linux()) return;
        using var blocker = new BlockingQpdf("--json=2");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var job = PdfCleanTests.PostAsync(test, PdfTestContext.Fixture, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
            if (stopping) test.Factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            else cancellation.Cancel();
            await ObserveAsync(job);
        }
        finally { cancellation.Cancel(); await ObserveAsync(job); }
        await blocker.AssertStoppedAsync();
        await CleanupAsync(test);
    }

    [TestMethod]
    public async Task SharedLimiter_RejectsAllPdfApisWhileCleanRuns_HealthAndNextJobWork()
    {
        if (!PdfCleanTests.Linux()) return;
        using var blocker = new BlockingQpdf("--json=2");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable, ["Pdf:MaxConcurrentProcesses"] = "1" });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var job = PdfCleanTests.PostAsync(test, PdfTestContext.Fixture, cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
            foreach (var path in new[] { "clean", "compress?level=standard", "merge", "split?every=1", "unlock", "protect" })
            {
                using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
                using var response = await test.Client.PostAsync("/api/pdf/" + path, form);
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            }
            using var health = await test.Client.GetAsync("/healthz");
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        }
        finally { cancellation.Cancel(); await ObserveAsync(job); }
        await blocker.AssertStoppedAsync();
        await CleanupAsync(test);
        using var next = PdfTestContext.Form(PdfTestContext.Fixture);
        using var result = await test.Client.PostAsync("/api/pdf/protect", next);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        test.AssertClean();
    }

    private static async Task ObserveAsync(Task<HttpResponseMessage> task)
    {
        try { using var response = await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }
    private static async Task CleanupAsync(PdfTestContext test)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Directory.Exists(test.TempRoot) && Directory.EnumerateFileSystemEntries(test.TempRoot).Any()) await Task.Delay(20, timeout.Token);
        test.AssertClean();
    }
}
