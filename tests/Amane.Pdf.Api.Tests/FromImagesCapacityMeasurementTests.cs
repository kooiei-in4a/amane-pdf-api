using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FromImagesCapacityMeasurementTests
{
    [TestMethod]
    [TestCategory("FromImagesCapacity")]
    public async Task MeasureActualReservations_WithSyntheticInputs()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Large synthetic inputs are opt-in; the ordinary test still checks the same path.
        var manifest = Environment.GetEnvironmentVariable("FROM_IMAGES_CAPACITY_MANIFEST");
        var options = new PdfOptions();
        new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("Pdf").Bind(options);
        var cases = manifest is null ? new[] { new MeasurementCase("small-png", [], "original", 200) }
            : JsonSerializer.Deserialize<MeasurementCase[]>(await File.ReadAllTextAsync(manifest))!;
        var results = new List<object>();
        foreach (var item in cases)
        {
            using var files = new TemporaryPdfFiles(Path.GetFullPath(options.TempRoot));
            var inputs = item.Inputs.Length == 0 ? new[] { files.ImagePath(1, "upload.bin") }
                : item.Inputs.Select((_, i) => files.ImagePath(i + 1, "upload.bin")).ToArray();
            for (var i = 0; i < inputs.Length; i++)
            {
                await using var output = TemporaryPdfFiles.CreatePrivateFile(inputs[i]);
                if (item.Inputs.Length == 0) await output.WriteAsync(FromImagesFixtures.Png());
                else { await using var input = File.OpenRead(item.Inputs[i]); await input.CopyToAsync(output); }
            }
            var processor = new PdfFromImagesProcessor(options, files, new(Options.Create(options)), new(Options.Create(options)));
            var stages = new List<FromImagesStage>(); var status = 200; string? reason = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var watch = Stopwatch.StartNew();
            try { await processor.ProcessAsync(inputs, item.Quality == "standard", deadline.Token, stages.Add); }
            catch (PdfImageInputException error) { status = 422; reason = error.Reason.ToString(); }
            watch.Stop();
            Assert.AreEqual(item.Expected, status, item.Name);
            Assert.IsTrue(processor.Capacity.Peak <= options.ImageJobLimitBytes);
            if (status == 200)
            {
                Assert.IsTrue(inputs.All(input => !File.Exists(input)), "Uploads must be released before import.");
                Assert.IsFalse(Directory.GetFiles(files.DirectoryPath).Any(path => path.EndsWith(".jpg", StringComparison.Ordinal) ||
                    path.EndsWith(".png", StringComparison.Ordinal) || path.EndsWith(".pnm", StringComparison.Ordinal)));
            }
            results.Add(new { item.Name, item.Quality, status, reason, seconds = watch.Elapsed.TotalSeconds,
                reserved_peak = processor.Capacity.Peak, stages });
        }
        if (Environment.GetEnvironmentVariable("FROM_IMAGES_CAPACITY_RESULTS") is { } resultPath)
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(results));
    }
    private sealed record MeasurementCase(string Name, string[] Inputs, string Quality, int Expected);
}
