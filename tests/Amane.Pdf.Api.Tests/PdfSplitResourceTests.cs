using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfSplitResourceTests
{
    [TestMethod]
    public async Task Timeout_StopsProcessTreeCleansJobAndReleasesPermit()
    {
        if (!Linux()) return;
        using var blocker = new BlockingQpdf("*/input.pdf");
        await using var test = new PdfTestContext(new()
        { ["Pdf:QpdfPath"] = blocker.Executable, ["Pdf:QpdfTimeoutSeconds"] = "1", ["Pdf:MaxConcurrentProcesses"] = "1" });
        var input = await test.CreatePagedPdfAsync(2);
        for (var i = 0; i < 2; i++)
        {
            using var response = await PdfSplitTests.PostAsync(test, input);
            await PdfSplitTests.AssertProblemAsync(test, response, 504);
            await blocker.AssertStoppedAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationOrStopping_CleansJobAndStopsProcessTree(bool stopping)
    {
        if (!Linux()) return;
        using var blocker = new BlockingQpdf("*/input.pdf");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        var input = await test.CreatePagedPdfAsync(2);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var job = PdfSplitTests.PostAsync(test, input, token: cancellation.Token);
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
    public async Task Split_SharesLimiterWithCompressMergeUnlockAndKeepsHealthAvailable()
    {
        if (!Linux()) return;
        using var blocker = new BlockingQpdf("*/input.pdf");
        await using var test = new PdfTestContext(new()
        { ["Pdf:QpdfPath"] = blocker.Executable, ["Pdf:MaxConcurrentProcesses"] = "1" });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var job = PdfSplitTests.PostAsync(test, await test.CreatePagedPdfAsync(2), token: cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
            foreach (var path in new[] { "/api/pdf/compress?level=standard", "/api/pdf/merge", "/api/pdf/unlock" })
            {
                using var form = PdfTestContext.FileForm(PdfTestContext.Fixture);
                using var response = await test.Client.PostAsync(path, form);
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

    [TestMethod]
    public async Task AllParts_ShareOneDeadline()
    {
        if (!Linux()) return;
        using var blocker = new BlockingQpdf("never-match", 1, "*/input.pdf");
        await using var test = new PdfTestContext(new()
        { ["Pdf:QpdfPath"] = blocker.Executable, ["Pdf:QpdfTimeoutSeconds"] = "3" });
        using var result = await PdfSplitTests.PostAsync(test, await test.CreatePagedPdfAsync(4));
        await PdfSplitTests.AssertProblemAsync(test, result, 504);
        await blocker.AssertStoppedAsync();
        Assert.IsTrue(blocker.Calls.Count(call => call.EndsWith("/input.pdf", StringComparison.Ordinal)) <= 3);
    }

    private static bool Linux()
    {
        if (OperatingSystem.IsLinux()) return true;
        Assert.Inconclusive("Linux process tree tests are required.");
        return false;
    }
    private static async Task ObserveAsync(Task<HttpResponseMessage> task)
    {
        try { using var response = await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }
    private static async Task CleanupAsync(PdfTestContext test)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Directory.Exists(test.TempRoot) && Directory.EnumerateFileSystemEntries(test.TempRoot).Any())
            await Task.Delay(20, timeout.Token);
        test.AssertClean();
    }
}
