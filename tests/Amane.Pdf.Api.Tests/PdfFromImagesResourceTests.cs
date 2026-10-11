using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfFromImagesResourceTests
{
    [TestMethod]
    [DataRow("--check")]
    [DataRow("--empty")]
    public async Task Timeout_KillsTreeCleansFilesAndReturnsPermit(string stage)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var blocker = new BlockingQpdf(stage);
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "1", ["Pdf:MaxConcurrentProcesses"] = "1" });
        using var form = PdfTestContext.MergeForm(FromImagesFixtures.Png());
        await PdfFromImagesTests.Problem(test, form, "?quality=standard", 504);
        await blocker.AssertStoppedAsync();
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = "qpdf";
        using var next = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var response = await test.Client.PostAsync("/api/pdf/optimize", next);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode); test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationOrStopping_KillsTreeAndCleansAllIntermediates(bool stopping)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var blocker = new BlockingQpdf("--check");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var form = PdfTestContext.MergeForm(FromImagesFixtures.Png(), FromImagesFixtures.Png());
        var task = test.Client.PostAsync("/api/pdf/from-images?quality=original", form, cancel.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
            if (stopping) test.Factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            else cancel.Cancel();
            try { using var response = await task; }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) when (stopping) { }
        }
        finally { cancel.Cancel(); }
        await blocker.AssertStoppedAsync();
        for (var i = 0; i < 100 && Directory.EnumerateFileSystemEntries(test.TempRoot).Any(); i++) await Task.Delay(20);
        test.AssertClean();
    }

    [TestMethod]
    public async Task TwoImageJobs_ShareExistingLimiterAndRejectThirdPdfRequest()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var blocker = new BlockingQpdf("--check");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first = PdfTestContext.MergeForm(FromImagesFixtures.Png());
        using var second = PdfTestContext.MergeForm(FromImagesFixtures.Png());
        var tasks = new[] { test.Client.PostAsync("/api/pdf/from-images?quality=standard", first, cancel.Token),
            test.Client.PostAsync("/api/pdf/from-images?quality=original", second, cancel.Token) };
        try
        {
            await blocker.WaitForJobsAsync(2);
            using var third = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture);
            using var response = await test.Client.PostAsync("/api/pdf/merge", third);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            cancel.Cancel();
            foreach (var task in tasks) try { using var response = await task; } catch (OperationCanceledException) { }
        }
        await blocker.AssertStoppedAsync();
        for (var i = 0; i < 100 && Directory.EnumerateFileSystemEntries(test.TempRoot).Any(); i++) await Task.Delay(20);
        test.AssertClean();
    }
}
