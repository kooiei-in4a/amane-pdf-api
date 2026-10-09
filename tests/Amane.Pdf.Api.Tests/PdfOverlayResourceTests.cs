using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfOverlayResourceTests
{
    [TestMethod]
    [DataRow("input", "cancel")]
    [DataRow("blank", "cancel")]
    [DataRow("draw", "cancel")]
    [DataRow("overlay", "cancel")]
    [DataRow("restore", "cancel")]
    [DataRow("final", "cancel")]
    [DataRow("input", "timeout")]
    [DataRow("blank", "timeout")]
    [DataRow("draw", "timeout")]
    [DataRow("overlay", "timeout")]
    [DataRow("restore", "timeout")]
    [DataRow("final", "timeout")]
    [DataRow("input", "stopping")]
    [DataRow("blank", "stopping")]
    [DataRow("draw", "stopping")]
    [DataRow("overlay", "stopping")]
    [DataRow("restore", "stopping")]
    [DataRow("final", "stopping")]
    public async Task EveryStage_CancelTimeoutAndStopping_WaitForProcessTree_AndDeleteJob(string stage, string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        var pattern = stage switch
        {
            "input" => "--json=2",
            "blank" => "--json-input",
            "draw" => "-c",
            "overlay" => "*/input.pdf",
            "restore" => "*/overlaid.pdf",
            _ => "--check"
        };
        using var job = new OverlayJob(); await job.CreateInput(OverlayFixtures.Geometry());
        // For final checks, count --check calls: input, processor layer, builder
        // layer and final output. Block only the fourth.
        var root = job.Root; var real = stage == "draw" ? job.Settings.PdfcpuPath : "qpdf";
        var wrapper = job.Script(
            "pause() {\n  sleep 300 &\n  child=$!\n" +
            $"  printf '%s %s\\n' \"$$\" \"$child\" > '{root}/blocked.pids'\n" +
            "  wait \"$child\"\n}\ncase \"$1\" in\n" + pattern + ")\n" +
            (stage == "final" ?
                $"n=0\nif [ -f '{root}/count' ]; then n=$(cat '{root}/count'); fi\n" +
                $"n=$((n+1))\nprintf '%s' \"$n\" > '{root}/count'\nif [ \"$n\" = 4 ]; then pause; fi\n" :
                "pause\n") +
            $";;\nesac\nexec '{real}' \"$@\"\n");
        if (stage == "draw") job.Settings.PdfcpuPath = wrapper; else job.Settings.QpdfPath = wrapper;
        using var caller = new CancellationTokenSource(); using var stopping = new CancellationTokenSource();
        using var token = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, stopping.Token);
        var processing = job.Builder.BuildAsync(job.Files, 1, 1, job.Draw, token.Token);
        var until = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "blocked.pids")))
        {
            Assert.IsFalse(processing.IsCompleted, "The intended stage was not reached");
            Assert.IsTrue(until.Elapsed.TotalSeconds < 10); await Task.Delay(10);
        }
        if (kind == "stopping") stopping.Cancel();
        else if (kind == "timeout") token.CancelAfter(20);
        else caller.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => processing);
        var pids = (await File.ReadAllTextAsync(Path.Combine(root, "blocked.pids"))).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var text in pids)
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try { using var process = System.Diagnostics.Process.GetProcessById(int.Parse(text.Trim())); if (process.HasExited) break; }
                catch (ArgumentException) { break; }
                Assert.IsTrue(wait.Elapsed.TotalSeconds < 5, "Child was not reaped");
                await Task.Delay(20);
            }
        }
        var directory = job.Files.DirectoryPath;
        job.Files.Dispose();
        Assert.IsFalse(Directory.Exists(directory));
        // OverlayJob owns disposal normally; recreate an empty dir for its Dispose.
        Directory.CreateDirectory(directory);
    }

    [TestMethod]
    [DataRow("too-complex", 422)]
    [DataRow("unsupported-pdf", 422)]
    [DataRow("internal", 500)]
    public async Task Helper_UsesOverlayReasonAndFixedErrors_WithoutEndpoint(string reason, int status)
    {
        var root = Path.Combine(Path.GetTempPath(), "overlay-helper-" + Guid.NewGuid().ToString("N"));
        using var lifetime = new Lifetime();
        using var services = new ServiceCollection().AddLogging().AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var options = new PdfOptions { TempRoot = root };
        try
        {
            await PdfEndpointHelpers.ExecuteAsync(context, options, "output.pdf", null, (_, _) => Task.CompletedTask,
                (_, _) => throw (reason == "internal" ? new InvalidOperationException("SECRET/path") :
                    new PdfOverlayInputException(reason == "too-complex" ? PdfOverlayReason.TooComplex : PdfOverlayReason.UnsupportedPdf)));
            Assert.AreEqual(status, context.Response.StatusCode);
            context.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(context.Response.Body);
            if (status == 422) Assert.AreEqual(reason, json.RootElement.GetProperty("reason").GetString());
            else Assert.AreEqual("PDF処理に失敗しました。", json.RootElement.GetProperty("title").GetString());
            Assert.IsFalse(json.RootElement.GetRawText().Contains("SECRET"));
            Assert.AreEqual(0, Directory.GetDirectories(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ThousandAndOneTargets_AreRejectedBeforeDrawing()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob();
        var root = OverlayFixtures.Metadata(); root.Remove("pages");
        var page = OverlayFixtures.Page(root).DeepClone();
        var objects = OverlayFixtures.Objects(root); var parent = OverlayFixtures.Parent(root);
        var kids = new System.Text.Json.Nodes.JsonArray();
        parent["/Count"] = 1001; parent["/Kids"] = kids;
        for (var i = 3; i <= 1003; i++)
        {
            kids.Add($"{i} 0 R");
            objects[$"obj:{i} 0 R"] = new System.Text.Json.Nodes.JsonObject { ["value"] = page.DeepClone() };
        }
        objects["trailer"]!["value"]!["/Size"] = 1004;
        await job.CreateInput(root.ToJsonString());
        Assert.AreEqual(PdfOverlayReason.TooComplex,
            (await Assert.ThrowsExactlyAsync<PdfOverlayInputException>(() => job.Builder.BuildAsync(job.Files, 1, 1001,
                (_, _) => throw new AssertFailedException("Drawing must not start above the page limit"), default))).Reason);
        CollectionAssert.AreEquivalent(new[] { job.Files.InputPath }, Directory.GetFiles(job.Files.DirectoryPath));
    }

    [TestMethod]
    public async Task TargetPageAndJsonAndOutputBudgets_RejectProvenOverflow()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob(); await job.CreateInput(OverlayFixtures.Geometry());
        job.Settings.OverlayMaxPages = 1;
        Assert.AreEqual(PdfOverlayReason.TooComplex,
            (await Assert.ThrowsExactlyAsync<PdfOverlayInputException>(() => job.Builder.BuildAsync(job.Files, 1, 2, job.Draw, default))).Reason);
        job.Settings.OverlayMaxPages = 1000; job.Settings.OverlayJsonLimitBytes = 4096;
        Assert.AreEqual(PdfOverlayReason.TooComplex,
            (await Assert.ThrowsExactlyAsync<PdfOverlayInputException>(() => job.Builder.BuildAsync(job.Files, 1, 1, job.Draw, default))).Reason);
        // Actual file length is proof, even if a writer returns exit zero.
        var output = Path.Combine(job.Files.DirectoryPath, "bounded.pdf");
        job.Settings.QpdfPath = job.Script("for last do :; done\nhead -c 9000 /dev/zero > \"$last\"\nexit 0");
        Assert.AreEqual(PdfOverlayReason.TooComplex,
            (await Assert.ThrowsExactlyAsync<PdfOverlayInputException>(() => job.Qpdf.RunOverlayWriteAsync(job.Files,
                ["--empty", output], output, 4096, default))).Reason);
        Assert.AreEqual(4097L, new FileInfo(output).Length);
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }
}
