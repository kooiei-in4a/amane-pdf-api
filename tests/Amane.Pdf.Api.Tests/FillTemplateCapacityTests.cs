using System.Diagnostics;
using System.Text.Json;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FillTemplateCapacityTests
{
    [TestMethod]
    [TestCategory("FillTemplateCapacity")]
    public Task UpperLimits_Single() => UpperLimits(1);

    [TestMethod]
    [TestCategory("FillTemplateCapacity")]
    public Task UpperLimits_TwoConcurrent() => UpperLimits(2);

    private static async Task UpperLimits(int concurrent)
    {
        // CI's Docker validation opts in; ordinary dotnet test performs no heavy load.
        var source = Environment.GetEnvironmentVariable("FILL_TEMPLATE_CAPACITY_INPUT");
        var json = Environment.GetEnvironmentVariable("FILL_TEMPLATE_CAPACITY_FIELDS");
        if (source is null && json is null)
            Assert.Inconclusive("上限容量測定にはFILL_TEMPLATE_CAPACITY_INPUTとFILL_TEMPLATE_CAPACITY_FIELDSが必要です。");
        Assert.IsNotNull(source, "FILL_TEMPLATE_CAPACITY_INPUT is required.");
        Assert.IsNotNull(json, "FILL_TEMPLATE_CAPACITY_FIELDS is required.");
        var fields = FillTemplateFields.Parse(await File.ReadAllBytesAsync(json));
        var jobs = Enumerable.Range(0, concurrent).Select(_ => new OverlayJob()).ToArray();
        try
        {
            foreach (var job in jobs) File.Copy(source, job.Files.InputPath);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await Task.WhenAll(jobs.Select(async job =>
            {
                var stages = new List<OverlayStage>(); var watch = Stopwatch.StartNew();
                Dictionary<string, long>? overlayReservations = null;
                void Observe(OverlayStage stage)
                {
                    stages.Add(stage);
                    if (stage.Name != "draw") return;
                    // Blank canvas and drawing JSON are deleted before the overlay write.
                    overlayReservations = new[] { job.Files.InputPath,
                        Path.Combine(job.Files.DirectoryPath, "overlay-update.json"),
                        Path.Combine(job.Files.DirectoryPath, "overlay-layer.pdf") }
                        .ToDictionary(path => Path.GetFileName(path), path => OverlayCapacity.Allocated(new FileInfo(path).Length));
                    overlayReservations["overlaid.pdf write + sentinel"] =
                        OverlayCapacity.Allocated(job.Settings.OverlayOutputLimitBytes + 1);
                }
                await job.Builder.BuildAsync(job.Files, fields.ResolveRange,
                    async (canvas, token) =>
                    {
                        await job.Pdfcpu.CreateAsync(job.Files, fields.CreateLayer(canvas), canvas.BlankPath,
                            canvas.LayerPath, canvas.JsonBudget, canvas.PdfBudget, token,
                            maxTextsPerPage: FillTemplateFields.MaxLinesPerPage, maxTextLength: FillTemplateFields.MaxTextLength);
                    },
                    deadline.Token, observe: Observe, rejectForms: true);
                watch.Stop();
                var reserved = stages.Single(stage => stage.Name == "job-reserved-peak").Bytes;
                Assert.IsTrue(reserved <= 124 * 1048576);
                Assert.IsTrue(watch.Elapsed.TotalSeconds < 30);
                CollectionAssert.AreEquivalent(new[] { job.Files.OutputPath }, Directory.GetFiles(job.Files.DirectoryPath));
                return new { seconds = watch.Elapsed.TotalSeconds, reserved,
                    reserved_fraction = (double)reserved / job.Settings.OverlayJobLimitBytes,
                    overlay_reservations = overlayReservations, stages };
            }));
            var result = JsonSerializer.Serialize(new { concurrent, results });
            Console.WriteLine(result);
            if (Environment.GetEnvironmentVariable("FILL_TEMPLATE_CAPACITY_RESULTS") is { } output)
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, $"fill-template-budget-{concurrent}.json"), result);
            }
        }
        finally { foreach (var job in jobs) job.Dispose(); }
    }
}
