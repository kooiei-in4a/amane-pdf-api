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
        var contents = new List<string>();
        for (var i = 0; i < pages.Length; i++)
        {
            var imageBudget = job.Reserve(image, ImageLimit);
            var page = geometry[i];
            await renderer.RenderPngAsync(files, new(source, image, pages[i],
                page.DisplayWidth / page.UserUnit, page.DisplayHeight / page.UserUnit,
                pixels[i].Width, pixels[i].Height, imageBudget, []), token);
            job.Commit(image, imageBudget);
            var (layer, lines) = await RecognizePageAsync(files, job, settings, language, image, i, pixels[i], token);
            job.Delete(image);
            try
            {
                if (await qpdf.GetPageCountAsync(layer, token) != 1) throw new InvalidOperationException(Failure);
            }
            catch (PdfInputException) { throw new InvalidOperationException(Failure); }
            textLayers.Add(layer);
            var content = job.PathFor(string.Create(CultureInfo.InvariantCulture, $"ocr-content-{i}.bin"));
            var height = pixels[i].Height;
            job.WriteFile(content, stream => OcrTextLayer.Write(stream, lines, language == Vertical, height, Dpi, token), token);
            contents.Add(content);
        }

        var merged = job.PathFor("ocr-text-merged.pdf");
        var merge = new List<string> { "--empty", "--pages" };
        foreach (var layer in textLayers) merge.AddRange([layer, "1"]);
        merge.AddRange(["--", merged]);
        await job.WritePdfAsync(merged, [.. merge], settings.OverlayGeneratedLimitBytes, token);
        foreach (var layer in textLayers) job.Delete(layer);

        // Keep Tesseract's page and GlyphLessFont, but replace its word-by-word content stream (Issue #116).
        var textUpdate = job.PathFor("ocr-text-update.json");
        using (var layers = await job.ReadAsync(merged, token, generated: true))
        {
            if (layers.Pages.Count != pages.Length) throw new InvalidOperationException(Failure);
            job.WriteJson(textUpdate, writer => WriteTextUpdate(writer, layers, contents, pixels, token), token,
                settings.OverlayGeneratedJsonLimitBytes);
        }
        foreach (var content in contents) job.Delete(content);
        var combined = job.PathFor("ocr-text.pdf");
        await job.WritePdfAsync(combined, [merged, "--update-from-json=" + textUpdate, combined],
            settings.OverlayGeneratedLimitBytes, token);
        job.Delete(textUpdate);
        job.Delete(merged);
        try { await qpdf.ValidateOutputAsync(combined, token); }
        catch (PdfInputException) { throw new InvalidOperationException(Failure); }

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
        try { await qpdf.ValidateOutputAsync(files.OutputPath, token); }
        catch (PdfInputException) { throw new InvalidOperationException(Failure); }

        using var result = await job.ReadAsync(files.OutputPath, token, generated: true);
        if (result.Pages.Count != count ||
            pages.Where((page, i) => !geometry[i].SameEffective(result.Read(page - 1, token))).Any())
            throw new InvalidOperationException(Failure);
    }

    private static async Task<(string Layer, List<OcrTextLayer.Line> Lines)> RecognizePageAsync(TemporaryPdfFiles files,
        PdfPageJob job, PdfOptions settings, string language, string image, int index, (int Width, int Height) size,
        CancellationToken token)
    {
        var layer = job.PathFor(string.Create(CultureInfo.InvariantCulture, $"ocr-text-{index}.pdf"));
        if (language != Vertical)
            return (layer, await RecognizeAsync(files, job, settings, Horizontal, "6", image, layer, size, token));

        // jpn_vert --psm 5 slices horizontal headings and captions into vertical "columns", while page segmentation
        // with both models misreads a short isolated column. Keep the recognition Tesseract is more confident in.
        var columns = await RecognizeAsync(files, job, settings, Vertical, "5", image, layer, size, token);
        var segmented = job.PathFor(string.Create(CultureInfo.InvariantCulture, $"ocr-text-{index}-segmented.pdf"));
        var lines = await RecognizeAsync(files, job, settings, Horizontal + "+" + Vertical, "3", image, segmented, size, token);
        if (OcrTextLayer.MeanConfidence(lines) <= OcrTextLayer.MeanConfidence(columns))
        {
            job.Delete(segmented);
            return (layer, columns);
        }
        job.Delete(layer);
        return (segmented, lines);
    }

    // Writes Tesseract's text-only PDF and TSV in one recognition, then reads and deletes the TSV.
    private static async Task<List<OcrTextLayer.Line>> RecognizeAsync(TemporaryPdfFiles files, PdfPageJob job,
        PdfOptions settings, string models, string psm, string image, string layer, (int Width, int Height) size,
        CancellationToken token)
    {
        var tsv = Path.ChangeExtension(layer, ".tsv");
        // prlimit's file size limit applies to each output file; reserve the same budget for both before running.
        var budget = job.ReservePair(layer, tsv, settings.OverlayGeneratedLimitBytes);
        var basePath = Path.ChangeExtension(Path.GetFullPath(layer), null);
        string[] arguments = [image, basePath, "-l", models, "--psm", psm, "--dpi",
            Dpi.ToString(CultureInfo.InvariantCulture), "-c", "textonly_pdf=1", "pdf", "tsv"];
        var result = await BoundedOutput.RunAsync(limit => CreateRequest(settings, arguments, limit)
            with { WorkingDirectory = Path.GetFullPath(files.DirectoryPath) }, layer, budget, token);
        var tsvInfo = new FileInfo(tsv);
        if (result.ExceedsBudget || tsvInfo.Exists && tsvInfo.Length > budget)
            throw new PdfRenderException(PdfRenderReason.OutputTooLarge);
        if (!result.Succeeded || !tsvInfo.Exists) throw new InvalidOperationException(Failure);
        job.Commit(layer, budget);
        job.Commit(tsv, budget);
        var lines = OcrTextLayer.Read(tsv, size.Width, size.Height, token);
        job.Delete(tsv);
        return lines;
    }

    private static void WriteTextUpdate(System.Text.Json.Utf8JsonWriter writer, PdfPageBoxes layers,
        IReadOnlyList<string> contents, (int Width, int Height)[] pixels, CancellationToken token)
    {
        PdfPageJob.StartUpdate(writer);
        for (var i = 0; i < contents.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var reference = layers.Pages[i];
            var page = layers.Dictionary(reference);
            var media = layers.Box(page.GetProperty("/MediaBox"), token);
            if (Math.Abs(media.Width - pixels[i].Width * 72.0 / Dpi) > 0.02 ||
                Math.Abs(media.Height - pixels[i].Height * 72.0 / Dpi) > 0.02 || media.Left != 0 || media.Bottom != 0)
                throw new InvalidOperationException(Failure);
            if (!page.TryGetProperty("/Contents", out var contentValue) ||
                PdfObjectRef.Parse(contentValue) is not { } contentRef ||
                !layers.Objects.TryGetValue(contentRef, out var contentObject) || !contentObject.TryGetProperty("stream", out _))
                throw new InvalidOperationException(Failure);
            var fonts = layers.Resolve(layers.Resolve(page.GetProperty("/Resources"), token).GetProperty("/Font"), token);
            var font = fonts.EnumerateObject().ToArray();
            if (font.Length != 1 || PdfObjectRef.Parse(font[0].Value) is not { } fontRef)
                throw new InvalidOperationException(Failure);

            writer.WriteStartObject(reference.Key);
            writer.WriteStartObject("value");
            foreach (var property in page.EnumerateObject())
                if (property.Name is not "/Resources") property.WriteTo(writer);
            writer.WriteStartObject("/Resources");
            writer.WriteStartObject("/Font");
            writer.WriteString("/" + OcrTextLayer.FontName, fontRef.Text);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteStartObject(contentRef.Key);
            writer.WriteStartObject("stream");
            writer.WriteStartObject("dict");
            writer.WriteEndObject();
            writer.WriteBase64String("data", File.ReadAllBytes(contents[i]));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }
        PdfPageJob.EndUpdate(writer);
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
