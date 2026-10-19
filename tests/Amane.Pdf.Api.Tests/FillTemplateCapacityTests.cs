using System.Diagnostics;
using System.Text.Json;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FillTemplateCapacityTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [TestCategory("FillTemplateCapacity")]
    public async Task UpperLimits_IncludeUnicodeInCapacityLedger(int concurrent)
    {
        // CI's Docker validation opts in; ordinary dotnet test performs no heavy load.
        var source = Environment.GetEnvironmentVariable("FILL_TEMPLATE_CAPACITY_INPUT");
        var json = Environment.GetEnvironmentVariable("FILL_TEMPLATE_CAPACITY_FIELDS");
        if (source is null || json is null) return;
        var fields = FillTemplateFields.Parse(await File.ReadAllBytesAsync(json));
        var jobs = Enumerable.Range(0, concurrent).Select(_ => new OverlayJob()).ToArray();
        try
        {
            foreach (var job in jobs) File.Copy(source, job.Files.InputPath);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var results = await Task.WhenAll(jobs.Select(async job =>
            {
                var stages = new List<OverlayStage>(); var watch = Stopwatch.StartNew();
                var drawing = new Stopwatch();
                await job.Builder.BuildAsync(job.Files, fields.ResolveRange,
                    async (canvas, token) =>
                    {
                        drawing.Start();
                        await job.Pdfcpu.CreateAsync(job.Files, fields.CreateLayer(canvas), canvas.BlankPath,
                            canvas.LayerPath, canvas.JsonBudget, canvas.PdfBudget, token,
                            maxTextsPerPage: FillTemplateFields.MaxLinesPerPage, maxTextLength: FillTemplateFields.MaxTextLength);
                        drawing.Stop();
                    },
                    deadline.Token, observe: stages.Add, fillTemplate: fields);
                watch.Stop();
                Assert.IsTrue(stages.Any(stage => stage.Name == "to-unicode"));
                var reserved = stages.Single(stage => stage.Name == "job-reserved-peak").Bytes;
                Assert.IsTrue(reserved <= 124 * 1048576);
                Assert.IsTrue(watch.Elapsed.TotalSeconds < 30);
                CollectionAssert.AreEquivalent(new[] { job.Files.OutputPath }, Directory.GetFiles(job.Files.DirectoryPath));
                // The second metadata mark includes drawing since the preceding blank mark.
                // Remove the measured delegate time, then add the CMap write/update phase.
                // This includes metadata parsing and cleanup, rather than timing only qpdf's update.
                var unicodeMilliseconds = stages.Where(stage => stage.Name == "metadata").Skip(1).First().Milliseconds
                    - drawing.Elapsed.TotalMilliseconds + stages.Single(stage => stage.Name == "to-unicode").Milliseconds;
                Assert.IsTrue(unicodeMilliseconds > 0);
                return new { seconds = watch.Elapsed.TotalSeconds, reserved,
                    unicode_seconds = unicodeMilliseconds / 1000,
                    reserved_fraction = (double)reserved / job.Settings.OverlayJobLimitBytes, stages };
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
