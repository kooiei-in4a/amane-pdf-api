using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class OverlayCapacityTests
{
    [TestMethod]
    public void SentinelAndBlockRounding_AreReservedBeforeWriting()
    {
        var capacity = new OverlayCapacity(3 * 4096);
        capacity.Reserve("input", 1);
        Assert.AreEqual(4096L, capacity.ReserveWrite("managed", 4096));
        Assert.AreEqual(4095L, capacity.ReserveWrite("tool", 4096, true));
        Assert.AreEqual(12288L, capacity.Used);
        Assert.AreEqual(12288L, capacity.Peak);
        Assert.ThrowsExactly<PdfOverlayInputException>(() => capacity.ReserveWrite("extra", 1));
        capacity.Reserve("managed", 0);
        Assert.AreEqual(8192L, capacity.Used);
        Assert.ThrowsExactly<PdfOverlayInputException>(() => capacity.Reserve("overflow", long.MaxValue));
    }
    [TestMethod]
    public async Task ManagedWrite_StopsBeforeOverflow_AndReleaseFollowsDeletion()
    {
        using var job = new OverlayJob();
        var capacity = new OverlayCapacity(4096);
        var path = job.Files.JobPath; var budget = capacity.ReserveWrite(path, 4);
        await using (var file = TemporaryPdfFiles.CreatePrivateFile(path))
        using (var stream = new OverlayLimitedWriteStream(file, budget, default))
        {
            await stream.WriteAsync(new byte[4]);
            Assert.ThrowsExactly<PdfOverlayInputException>(() => stream.WriteByte(1));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await stream.WriteAsync(new byte[1], cancel.Token));
        }
        capacity.Commit(path, budget); Assert.AreEqual(4L, new FileInfo(path).Length);
        capacity.Delete(path); Assert.IsFalse(File.Exists(path)); Assert.AreEqual(0L, capacity.Used);
    }
    [TestMethod]
    public void Settings_CannotRaiseMeasuredEnvelope()
    {
        Assert.IsTrue(OverlayCapacity.IsValid(new()));
        foreach (var settings in new[] { new PdfOptions { OverlayMaxPages = 1001 }, new() { OverlayGeneratedLimitBytes = 8388609 },
            new() { OverlayJsonLimitBytes = 33554433 }, new() { OverlayGeneratedJsonLimitBytes = 41943041 },
            new() { OverlayOutputLimitBytes = 65011713 }, new() { OverlayJobLimitBytes = 130023425 },
            new() { OverlayMaxPages = 0 }, new() { MaxFileBytes = 4097, OverlayJobLimitBytes = 8191 } })
            Assert.IsFalse(OverlayCapacity.IsValid(settings));
    }

    [TestMethod]
    [TestCategory("OverlayCapacity")]
    public Task MeasureRealBuilder_Single() => Measure(1);

    [TestMethod]
    [TestCategory("OverlayCapacity")]
    public Task MeasureRealBuilder_Concurrent() => Measure(2);

    private static async Task Measure(int concurrent)
    {
        if (!OperatingSystem.IsLinux()) return;
        var source = Environment.GetEnvironmentVariable("OVERLAY_CAPACITY_INPUT");
        var glyphsPath = Environment.GetEnvironmentVariable("OVERLAY_CAPACITY_GLYPHS");
        var glyphs = glyphsPath is null ? "日本語容量測定文字字体描画番号印鑑透黒赤緑青白" : await File.ReadAllTextAsync(glyphsPath);
        var runes = glyphs.EnumerateRunes().ToArray();
        var multiline = Environment.GetEnvironmentVariable("OVERLAY_CAPACITY_LAYOUT") == "lines-tabs";
        var jobs = Enumerable.Range(0, concurrent).Select(_ => new OverlayJob()).ToArray();
        try
        {
            foreach (var job in jobs)
            {
                if (source is null) await job.CreateInput(OverlayFixtures.Geometry());
                else File.Copy(source, job.Files.InputPath);
                await ProcessMemoryLimits.ValidateStartupAsync(job.Settings, default);
                await PdfcpuProcessor.ValidateStartupAsync(job.Settings, default);
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await Task.WhenAll(jobs.Select(async (job, index) =>
            {
                var stages = new List<OverlayStage>(); var watch = Stopwatch.StartNew();
                var pages = source is null ? 6 : 1000;
                try { await job.Builder.BuildAsync(job.Files, 1, pages, async (canvas, token) =>
                {
                    var model = Enumerable.Range(1, pages).ToDictionary(page => page, page =>
                        (IReadOnlyList<PdfcpuText>)Enumerable.Range(0, PdfcpuProcessor.MaxTextsPerPage).Select(element =>
                        {
                            var value = new StringBuilder();
                            var start = (page - 1) * 512 + element * 128;
                            var length = PdfcpuProcessor.MaxTextLength;
                            var breaks = 0;
                            for (var c = 0; value.Length < length; c++)
                            {
                                if (multiline && breaks < 3 && value.Length >= 30 + 32 * breaks)
                                { value.Append("\n\t"); breaks++; continue; }
                                var rune = runes[(start + c) % runes.Length];
                                if (value.Length + rune.Utf16SequenceLength > length) value.Append('語');
                                else value.Append(rune.ToString());
                            }
                            var size = element switch { 0 => 1, 1 => 12, 2 => 72, _ => 14400 };
                            return new PdfcpuText(value.ToString(), Anchor: null, X: page % 2 == 0 ? 14400 : 0,
                                Y: 0, Dx: element == 0 ? -14400 : element * 12, Dy: 14400,
                                FontSize: size, Rotation: element switch { 0 => -360, 1 => -45, 2 => 90, _ => 360 }, Color: "#f1a2b3");
                        }).ToArray());
                    await job.Pdfcpu.CreateAsync(job.Files, new(model, canvas.Pages), canvas.BlankPath, canvas.LayerPath, canvas.JsonBudget, canvas.PdfBudget, token);
                }, deadline.Token, stages.Add); }
                catch
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { index, concurrent, seconds = watch.Elapsed.TotalSeconds, stages }));
                    throw;
                }
                watch.Stop();
                var layer = stages.Single(stage => stage.Name == "draw").Bytes;
                var budget = stages.Single(stage => stage.Name == "layer-budget").Bytes;
                CollectionAssert.AreEquivalent(new[] { job.Files.OutputPath }, Directory.GetFiles(job.Files.DirectoryPath));
                File.Delete(job.Files.OutputPath);
                Assert.AreEqual(0, Directory.GetFiles(job.Files.DirectoryPath).Length);
                return new { index, pages, seconds = watch.Elapsed.TotalSeconds, layer, budget, remaining = budget - layer, stages };
            }));
            var json = JsonSerializer.Serialize(new { concurrent, results });
            Console.WriteLine(json);
            if (Environment.GetEnvironmentVariable("OVERLAY_CAPACITY_RESULTS") is { } output)
            { Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, $"capacity-{concurrent}.json"), json); }
            // Persist failed timing/budget evidence too, after output cleanup.
            foreach (var result in results)
            {
                Assert.IsTrue(result.seconds <= 24, $"Builder exceeded 24 seconds: {result.seconds:F3}");
                Assert.IsTrue(result.layer < result.budget && result.layer < 8 * 1048576);
            }
        }
        finally
        {
            var roots = jobs.Select(job => job.Root).ToArray(); foreach (var job in jobs) job.Dispose();
            foreach (var root in roots) Assert.IsFalse(Directory.Exists(root));
        }
    }
}
