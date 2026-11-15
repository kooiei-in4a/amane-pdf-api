using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal sealed class PdfOcrStartupException : Exception;

internal static class PdfOcrEndpoint
{
    private const int Dpi = 300;
    private const long ImageLimit = 64 * 1024 * 1024;
    private const long TesseractMemoryLimit = 384 * 1024 * 1024;
    private const string Failure = "PDFのOCR処理に失敗しました。";
    private const string Horizontal = "jpn";
    private const string Vertical = "jpn_vert";

    internal static bool IsValid(PdfOptions options) =>
        options.MaxOcrPages is >= 1 and <= 10 && options.OcrTimeoutSeconds is > 0 and <= int.MaxValue / 1000 &&
        !string.IsNullOrWhiteSpace(options.TesseractPath) && !options.TesseractPath.Contains('\0');

    internal static async Task HandleAsync(HttpContext context, QpdfProcessor qpdf, PdfRenderer renderer,
        RenderSlot slot, IOptions<PdfOptions> options)
    {
        using var lease = slot.TryAcquire();
        if (lease is null)
        {
            await Results.Problem(statusCode: 503, title: "重いPDF処理の同時実行上限に達しました。").ExecuteAsync(context);
            return;
        }

        var settings = options.Value;
        // PDFium's shared execution group uses the same job budget as redact and to-images.
        var jobLimit = settings.RedactJobLimitBytes / 4096 * 4096;
        var inputLimit = Math.Min(settings.MaxFileBytes, jobLimit);
        var language = Horizontal;
        PdfPageSelection? selection = null;
        await PdfEndpointHelpers.ExecuteAsync(context, settings, "ocr.pdf", () =>
        {
            var query = context.Request.Query;
            PdfEndpointHelpers.ValidateQueryKeys(query, "direction", "pages");
            language = query["direction"].ToString() switch
            {
                "horizontal" => Horizontal,
                "vertical" => Vertical,
                _ => throw new BadHttpRequestException("Invalid direction query.")
            };
            if (query.TryGetValue("pages", out var pages)) selection = PdfPageSelection.Parse(pages.ToString());
        }, (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, inputLimit, token),
        (files, token) => OcrAsync(files, qpdf, renderer, settings, jobLimit, language, selection, token),
        maxRequestBytes: inputLimit + PdfOptions.MultipartOverheadBytes,
        processingTimeoutSeconds: settings.OcrTimeoutSeconds, renderFailureTitle: "このPDFをOCR処理できません。");
    }

    private static async Task OcrAsync(TemporaryPdfFiles files, QpdfProcessor qpdf, PdfRenderer renderer,
        PdfOptions settings, long jobLimit, string language, PdfPageSelection? selection, CancellationToken token)
    {
        var job = new PdfPageJob(files, qpdf, settings, jobLimit);
        job.TrackInput();
        await qpdf.ValidateAsync(files, token);
        var source = Path.GetFullPath(files.InputPath);
        var update = job.PathFor("ocr-geometry.json");
        var image = Path.GetFullPath(job.PathFor("ocr-page.png"));
        int count;
        int[] pages;
        PdfPageAttributes[] geometry;
        (int Width, int Height)[] pixels;
        using (var input = await job.ReadAsync(files.InputPath, token))
        {
            count = input.Pages.Count;
            if (count == 0) throw new PdfInputException();
            var ranges = selection?.GetRanges(count) ?? [(1, count)];
            if (ranges.Sum(range => (long)range.End - range.Start + 1) > settings.MaxOcrPages)
                throw new BadHttpRequestException("OCR page limit exceeded.");
            if (PdfFormDetector.ContainsSignatureStructure(input, token))
                throw new PdfOverlayInputException(PdfOverlayReason.SignatureStructure);
            pages = ranges.SelectMany(range => Enumerable.Range(range.Start, range.End - range.Start + 1)).ToArray();
            geometry = new PdfPageAttributes[pages.Length];
            pixels = new (int, int)[pages.Length];
            var document = new RedactDocument(input, token);
            for (var i = 0; i < pages.Length; i++)
            {
                PdfRenderer.CheckImages(document, pages[i]);
                geometry[i] = input.Read(pages[i] - 1, token);
                pixels[i] = PdfRenderer.GetPixelSize(geometry[i], Dpi);
            }
            // qpdf anchors overlay to the TrimBox. Temporarily use the same effective CropBox as PDFium.
            job.WriteJson(update, writer => input.WriteUpdate(writer, pages, geometry, restore: false, token), token);
        }

        var textLayers = new List<string>();
        for (var i = 0; i < pages.Length; i++)
        {
            var imageBudget = job.Reserve(image, ImageLimit);
            var page = geometry[i];
            await renderer.RenderPngAsync(files, new(source, image, pages[i],
                page.DisplayWidth / page.UserUnit, page.DisplayHeight / page.UserUnit,
                pixels[i].Width, pixels[i].Height, imageBudget, []), token);
            job.Commit(image, imageBudget);
            var layer = job.PathFor(string.Create(CultureInfo.InvariantCulture, $"ocr-text-{i}.pdf"));
            await RecognizeAsync(files, job, settings, language, image, layer, token);
            job.Delete(image);
            if (await qpdf.GetPageCountAsync(layer, token) != 1) throw new InvalidOperationException(Failure);
            textLayers.Add(layer);
        }

        var combined = job.PathFor("ocr-text.pdf");
        var merge = new List<string> { "--empty", "--pages" };
        foreach (var layer in textLayers) merge.AddRange([layer, "1"]);
        merge.AddRange(["--", combined]);
        await job.WritePdfAsync(combined, [.. merge], settings.OverlayGeneratedLimitBytes, token);
        foreach (var layer in textLayers) job.Delete(layer);
        await qpdf.ValidateOutputAsync(combined, token);

        var overlaid = job.PathFor("ocr-overlay.pdf");
        var destinations = string.Join(',', pages.Select(page => page.ToString(CultureInfo.InvariantCulture)));
        await job.WritePdfAsync(overlaid, [files.InputPath, "--update-from-json=" + update, overlaid,
            "--overlay", combined, "--to=" + destinations, "--from=1-z", "--"], settings.OverlayOutputLimitBytes, token);
        job.Delete(update);
        job.Delete(combined);
        job.Delete(files.InputPath);

        using (var metadata = await job.ReadAsync(overlaid, token, generated: true))
        {
            if (metadata.Pages.Count != count) throw new InvalidOperationException(Failure);
            job.WriteJson(update, writer => metadata.WriteUpdate(writer, pages, geometry, restore: true, token), token);
        }
        await job.WritePdfAsync(files.OutputPath, [overlaid, "--update-from-json=" + update, files.OutputPath],
            settings.OverlayOutputLimitBytes, token);
        job.Delete(overlaid);
        job.Delete(update);
        await qpdf.ValidateOutputAsync(files.OutputPath, token);

        using var result = await job.ReadAsync(files.OutputPath, token, generated: true);
        if (result.Pages.Count != count ||
            pages.Where((page, i) => !geometry[i].SameEffective(result.Read(page - 1, token))).Any())
            throw new InvalidOperationException(Failure);
    }

    private static async Task RecognizeAsync(TemporaryPdfFiles files, PdfPageJob job, PdfOptions settings,
        string language, string image, string layer, CancellationToken token)
    {
        var budget = job.Reserve(layer, settings.OverlayGeneratedLimitBytes);
        var basePath = Path.ChangeExtension(Path.GetFullPath(layer), null);
        var psm = language == Vertical ? "5" : "6";
        string[] arguments = [image, basePath, "-l", language, "--psm", psm, "--dpi",
            Dpi.ToString(CultureInfo.InvariantCulture), "-c", "textonly_pdf=1", "pdf"];
        var result = await BoundedOutput.RunAsync(limit => CreateRequest(settings, arguments, limit)
            with { WorkingDirectory = Path.GetFullPath(files.DirectoryPath) }, layer, budget, token);
        if (result.ExceedsBudget) throw new PdfRenderException(PdfRenderReason.OutputTooLarge);
        if (!result.Succeeded) throw new InvalidOperationException(Failure);
        job.Commit(layer, budget);
    }

    internal static async Task ValidateStartupAsync(PdfOptions options, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            var result = await ExternalProcessRunner.RunAsync(CreateRequest(options, ["--list-langs"], null, 8192), token);
            if (result.ExitCode != 0 || result.Stdout is null) throw new InvalidOperationException();
            var languages = Encoding.UTF8.GetString(result.Stdout).Split('\n', StringSplitOptions.TrimEntries);
            if (!languages.Contains(Horizontal) || !languages.Contains(Vertical)) throw new InvalidOperationException();
        }
        catch (Exception) { throw new PdfOcrStartupException(); }
    }

    private static ExternalProcessRequest CreateRequest(PdfOptions options, string[] arguments, long? fileLimit,
        int? stdoutLimit = null)
        => ProcessMemoryLimits.CreateRequest(options.PrlimitPath, options.TesseractPath, arguments,
            TesseractMemoryLimit, fileLimit, new Dictionary<string, string> { ["OMP_THREAD_LIMIT"] = "1" }, stdoutLimit);
}
