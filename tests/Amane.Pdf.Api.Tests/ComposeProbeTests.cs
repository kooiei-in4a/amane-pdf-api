#if COMPOSE_PROBE
using System.Diagnostics;
using System.Text.Json;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ComposeProbeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task Measure()
    {
        var fixture = Environment.GetEnvironmentVariable("COMPOSE_FIXTURE")!;
        var results = Environment.GetEnvironmentVariable("COMPOSE_RESULTS")!;
        using var job = new OverlayJob();
        var files = job.Files;
        job.Settings.PrlimitPath = "/fixture/count-prlimit.sh";

        var stages = new List<OverlayStage>();
        var completedRuns = 0;
        long sanitizedBytes = 0;
        string? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(job.Settings.QpdfTimeoutSeconds));
        var timer = Stopwatch.StartNew();
        File.Copy(Path.Combine(fixture, "background.pdf"), files.InputPath);
        File.Copy(Path.Combine(fixture, "layout.json"), files.JobPath);
        var layout = JsonSerializer.Deserialize<Layout>(await File.ReadAllBytesAsync(files.JobPath, deadline.Token), Json)!;
        Assert.AreEqual("ds-layout/1", layout.LayoutVersion);
        Assert.IsFalse(layout.Blank);
        var imagePaths = new Dictionary<string, string>();
        foreach (var id in layout.Elements.Where(element => element.Kind == "image").Select(element => element.Image!).Distinct())
        {
            var target = Path.Combine(files.DirectoryPath, id + ".png");
            File.Copy(Path.Combine("/fixture", id + ".png"), target);
            imagePaths.Add(id, target);
        }
        var layoutBytes = new FileInfo(files.JobPath).Length;
        var inputBytes = new FileInfo(files.InputPath).Length;
        var imageBytes = imagePaths.Values.Sum(path => new FileInfo(path).Length);
        var groups = new List<List<Element>>();
        foreach (var page in layout.Elements.GroupBy(element => element.Page).OrderBy(page => page.Key))
        {
            foreach (var element in page)
            {
                var last = groups.LastOrDefault();
                if (last is null || last[0].Page != element.Page || last[0].Kind != element.Kind)
                    groups.Add(last = []);
                last.Add(element);
            }
        }

        var runs = groups.Select(elements => new OverlayRun(elements[0].Page, elements[0].Kind == "text",
            async (canvas, capacity, token) =>
            {
                var actual = canvas.Pages[0];
                var expected = layout.Pages.Single(page => page.Index == elements[0].Page);
                Assert.AreEqual(expected.Width, (int)decimal.Floor((decimal)actual.Width * 100));
                Assert.AreEqual(expected.Height, (int)decimal.Floor((decimal)actual.Height * 100));
                if (elements[0].Kind == "text")
                {
                    var texts = elements.SelectMany(element => PlaceText(element, actual)).ToArray();
                    await job.Pdfcpu.CreateAsync(files, new(new Dictionary<int, IReadOnlyList<PdfcpuText>>
                        { [1] = texts }, canvas.Pages), canvas.BlankPath, canvas.LayerPath,
                        canvas.JsonBudget, canvas.PdfBudget, token, maxTextsPerPage: 600);
                }
                else
                {
                    var previous = canvas.BlankPath;
                    for (var i = 0; i < elements.Count; i++)
                    {
                        var element = elements[i];
                        var source = imagePaths[element.Image!];
                        var sanitized = files.StampImagePath;
                        var pngBudget = capacity.ReserveWrite(sanitized, new FileInfo(source).Length);
                        var output = i == elements.Count - 1
                            ? canvas.LayerPath : Path.Combine(files.DirectoryPath, $"stamp-{i % 2}.pdf");
                        var pdfBudget = output == canvas.LayerPath ? canvas.PdfBudget
                            : capacity.ReserveWrite(output, job.Settings.OverlayGeneratedLimitBytes, true);
                        try
                        {
                            var info = await PngSanitizer.WriteAsync(source, sanitized, pngBudget,
                                4_000_000, job.Settings.ImagePngWorkingLimitBytes, token);
                            capacity.Commit(sanitized, pngBudget);
                            sanitizedBytes += new FileInfo(sanitized).Length;
                            await PngDataValidator.ValidateAsync(sanitized, token);
                            var image = new StampImage(sanitized, info.Width, info.Height);
                            await job.Pdfcpu.StampImageAsync(files, image, PlaceImage(element, image, actual),
                                previous, output, pdfBudget, token);
                            if (output != canvas.LayerPath) capacity.Commit(output, pdfBudget);
                        }
                        finally
                        {
                            capacity.Delete(sanitized);
                            if (previous != canvas.BlankPath) capacity.Delete(previous);
                        }
                        previous = output;
                    }
                }
                completedRuns++;
            })).ToArray();

        try
        {
            await job.Builder.BuildRunsAsync(files, runs, [files.JobPath, .. imagePaths.Values], deadline.Token, stages.Add);
        }
        catch (Exception exception) when (exception is OperationCanceledException or PdfOverlayInputException or
            PdfStampImageException or PdfInputException or InvalidOperationException)
        {
            failure = exception switch
            {
                PdfOverlayInputException overlay => overlay.Reason.ToString(),
                PdfStampImageException stamp => stamp.Reason.ToString(),
                _ => exception.GetType().Name
            };
        }
        timer.Stop();
        var outputBytes = failure is null ? new FileInfo(files.OutputPath).Length : (long?)null;
        if (failure is null) File.Move(files.OutputPath, Path.Combine(results, "output.pdf"));
        var counters = File.ReadAllLines(Path.Combine(results, "calls.txt"));
        var metrics = new
        {
            seconds = timer.Elapsed.TotalSeconds,
            inputBytes, imageBytes, layoutBytes, sanitizedBytes, outputBytes,
            pages = layout.Pages.Length,
            texts = layout.Elements.Count(element => element.Kind == "text"),
            images = imagePaths.Count,
            maxElementsOnPage = layout.Elements.GroupBy(element => element.Page).Max(page => page.Count()),
            runCount = runs.Length, completedRuns,
            pdfcpuCalls = counters.Count(line => line == "pdfcpu"),
            qpdfCalls = counters.Count(line => line is "qpdf" or "overlay"),
            qpdfOverlayCalls = counters.Count(line => line == "overlay"),
            peakLedgerBytes = stages.Single(stage => stage.Name == "job-reserved-peak").Bytes,
            failure, stages
        };
        await File.WriteAllTextAsync(Path.Combine(results, "metrics.json"), JsonSerializer.Serialize(metrics, Json));
    }

    private static IReadOnlyList<PdfcpuText> PlaceText(Element element, PdfcpuPageSize page)
    {
        Assert.AreEqual("gothic", element.Font);
        Assert.AreEqual("middle", element.Valign);
        Assert.AreEqual(120, element.LineHeight);
        Assert.AreEqual("#000000", element.Color);
        var rect = element.Rect;
        return FillTemplateLayout.Place(new(element.Page,
            rect[0] / 100m / (decimal)page.Width, rect[1] / 100m / (decimal)page.Height,
            rect[2] / 100m / (decimal)page.Width, rect[3] / 100m / (decimal)page.Height,
            element.Size / 100m, element.Align!, element.Text!), page);
    }

    private static StampPlacement PlaceImage(Element element, StampImage image, PdfcpuPageSize page)
    {
        var rect = element.Rect.Select(value => value / 100.0).ToArray();
        var scale = Math.Min(rect[2] / image.Width, rect[3] / image.Height);
        var width = scale * image.Width;
        var height = scale * image.Height;
        return new(rect[0] + (rect[2] - width) / 2, page.Height - rect[1] - (rect[3] + height) / 2,
            width, height, scale, page.Width, page.Height);
    }

    private sealed record Layout([property: System.Text.Json.Serialization.JsonPropertyName("layout")] string LayoutVersion,
        bool Blank, Page[] Pages, Element[] Elements);
    private sealed record Page(int Index, int Width, int Height);
    private sealed record Element(int Page, string Kind, int[] Rect, string? Image, string? Text,
        string? Font, int Size, string? Color, string? Align, string? Valign, int LineHeight);
}
#endif
