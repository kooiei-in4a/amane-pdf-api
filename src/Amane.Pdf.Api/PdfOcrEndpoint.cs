using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal sealed class PdfOcrStartupException : Exception;

internal static class PdfOcrEndpoint
{
    private const int MaxDpi = 300;
    private const int MinDpi = 72;
    private const long ImageLimit = 64 * 1024 * 1024;
    private const long TesseractMemoryLimit = 384 * 1024 * 1024;
    private const string Failure = "PDFのOCR処理に失敗しました。";
    private const string Horizontal = "jpn";
    private const string Vertical = "jpn_vert";

    internal static bool IsValid(PdfOptions options) =>
        options.MaxOcrPages is >= 1 and <= 10 && options.OcrTimeoutSeconds is > 0 and <= int.MaxValue / 1000 &&
        options.MaxOcrHighPages is >= 1 and <= 5 && options.OcrHighTimeoutSeconds is > 0 and <= int.MaxValue / 1000 &&
        !string.IsNullOrWhiteSpace(options.TesseractPath) && !options.TesseractPath.Contains('\0') &&
        !string.IsNullOrWhiteSpace(options.TessdataBestDir) && !options.TessdataBestDir.Contains('\0') &&
        Path.IsPathFullyQualified(options.TessdataBestDir);

    internal static async Task HandleAsync(HttpContext context, QpdfProcessor qpdf, PdfRenderer renderer,
        RenderSlot slot, IOptions<PdfOptions> options)
    {
        using var lease = slot.TryAcquire();
        if (lease is null)
        {
            await Results.Problem(statusCode: 503, title: "PDF処理の同時実行上限に達しました。").ExecuteAsync(context);
            return;
        }

        var settings = options.Value;
        // PDFium's shared execution group uses the same job budget as redact and to-images.
        var jobLimit = settings.RedactJobLimitBytes / 4096 * 4096;
        var inputLimit = Math.Min(settings.MaxFileBytes, jobLimit);
        var query = context.Request.Query;
        var quality = query.TryGetValue("quality", out var value) ? value.ToString() : "standard";
        var high = quality == "high";
        var vertical = false;
        PdfPageSelection? selection = null;
        await PdfEndpointHelpers.ExecuteAsync(context, settings, "ocr.pdf", () =>
        {
            PdfEndpointHelpers.ValidateQueryKeys(query, "direction", "pages", "quality");
            vertical = query["direction"].ToString() switch
            {
                "horizontal" => false,
                "vertical" => true,
                _ => throw new BadHttpRequestException("Invalid direction query.")
            };
            if (quality is not ("standard" or "high")) throw new BadHttpRequestException("Invalid quality query.");
            if (query.TryGetValue("pages", out var pages)) selection = PdfPageSelection.Parse(pages.ToString());
        }, (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, inputLimit, token),
        (files, token) => OcrAsync(files, qpdf, renderer, settings, jobLimit, high, vertical, selection, token),
        maxRequestBytes: inputLimit + PdfOptions.MultipartOverheadBytes,
        processingTimeoutSeconds: high ? settings.OcrHighTimeoutSeconds : settings.OcrTimeoutSeconds,
        renderFailureTitle: "このPDFをOCR処理できません。");
    }

    private static async Task OcrAsync(TemporaryPdfFiles files, QpdfProcessor qpdf, PdfRenderer renderer,
        PdfOptions settings, long jobLimit, bool high, bool vertical, PdfPageSelection? selection, CancellationToken token)
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
        (int Width, int Height, int Dpi)[] rasters;
        using (var input = await job.ReadAsync(files.InputPath, token))
        {
            count = input.Pages.Count;
            if (count == 0) throw new PdfInputException();
            var ranges = selection?.GetRanges(count) ?? [(1, count)];
            var maxPages = high ? settings.MaxOcrHighPages : settings.MaxOcrPages;
            if (ranges.Sum(range => (long)range.End - range.Start + 1) > maxPages)
                throw new BadHttpRequestException("OCR page limit exceeded.");
            if (PdfFormDetector.ContainsSignatureStructure(input, token))
                throw new PdfOverlayInputException(PdfOverlayReason.SignatureStructure);
            pages = ranges.SelectMany(range => Enumerable.Range(range.Start, range.End - range.Start + 1)).ToArray();
            geometry = new PdfPageAttributes[pages.Length];
            rasters = new (int, int, int)[pages.Length];
            var document = new RedactDocument(input, token);
            for (var i = 0; i < pages.Length; i++)
            {
                PdfRenderer.CheckImages(document, pages[i]);
                geometry[i] = input.Read(pages[i] - 1, token);
                rasters[i] = PdfRenderer.FitPixelSize(geometry[i], MinDpi, MaxDpi);
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
            var raster = rasters[i];
            await renderer.RenderPngAsync(files, new(source, image, pages[i],
                page.DisplayWidth / page.UserUnit, page.DisplayHeight / page.UserUnit,
                raster.Width, raster.Height, imageBudget, []), token);
            job.Commit(image, imageBudget);
            var layer = job.PathFor(string.Create(CultureInfo.InvariantCulture, $"ocr-text-{i}.pdf"));
            var lines = await RecognizePageAsync(files, job, settings, high, vertical, image, layer, raster, token);
            job.Delete(image);
            try
            {
                if (await qpdf.GetPageCountAsync(layer, token) != 1) throw new InvalidOperationException(Failure);
            }
            catch (PdfInputException) { throw new InvalidOperationException(Failure); }
            textLayers.Add(layer);
            var content = job.PathFor(string.Create(CultureInfo.InvariantCulture, $"ocr-content-{i}.bin"));
            job.WriteFile(content, stream => OcrTextLayer.Write(stream, lines, vertical, raster.Height, raster.Dpi, token), token);
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
            job.WriteJson(textUpdate, writer => WriteTextUpdate(writer, layers, contents, rasters, token), token,
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

    // Writes Tesseract's text-only PDF and TSV in one recognition, then reads and deletes the TSV.
    private static async Task<List<OcrTextLayer.Line>> RecognizePageAsync(TemporaryPdfFiles files, PdfPageJob job,
        PdfOptions settings, bool high, bool vertical, string image, string layer,
        (int Width, int Height, int Dpi) raster, CancellationToken token)
    {
        var tsv = Path.ChangeExtension(layer, ".tsv");
        // prlimit's file size limit applies to each output file; reserve the same budget for both before running.
        var budget = job.ReservePair(layer, tsv, settings.OverlayGeneratedLimitBytes);
        var basePath = Path.ChangeExtension(Path.GetFullPath(layer), null);
        var (models, psm) = (high, vertical) switch
        {
            (true, true) => (Horizontal + "+" + Vertical, "3"),
            (false, true) => (Vertical, "5"),
            _ => (Horizontal, "1")
        };
        string[] tessdata = high ? ["--tessdata-dir", settings.TessdataBestDir] : [];
        // Set the output flags directly so neither model directory needs a copy of Tesseract's configs.
        string[] arguments = [image, basePath, "-l", models, "--psm", psm, "--dpi",
            raster.Dpi.ToString(CultureInfo.InvariantCulture), "-c", "textonly_pdf=1", "-c", "thresholding_method=2",
            .. tessdata, "-c", "tessedit_create_pdf=1", "-c", "tessedit_create_tsv=1"];
        var result = await BoundedOutput.RunAsync(limit => CreateRequest(settings, arguments, limit)
            with { WorkingDirectory = Path.GetFullPath(files.DirectoryPath) }, layer, budget, token);
        var tsvInfo = new FileInfo(tsv);
        if (result.ExceedsBudget || tsvInfo.Exists && tsvInfo.Length > budget)
            throw new PdfRenderException(PdfRenderReason.OutputTooLarge);
        if (!result.Succeeded || !tsvInfo.Exists) throw new InvalidOperationException(Failure);
        job.Commit(layer, budget);
        job.Commit(tsv, budget);
        var lines = OcrTextLayer.Read(tsv, raster.Width, raster.Height, token);
        job.Delete(tsv);
        return lines;
    }

    private static void WriteTextUpdate(System.Text.Json.Utf8JsonWriter writer, PdfPageBoxes layers,
        IReadOnlyList<string> contents, (int Width, int Height, int Dpi)[] rasters, CancellationToken token)
    {
        PdfPageJob.StartUpdate(writer);
        for (var i = 0; i < contents.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var reference = layers.Pages[i];
            var page = layers.Dictionary(reference);
            var media = layers.Box(page.GetProperty("/MediaBox"), token);
            var raster = rasters[i];
            if (Math.Abs(media.Width - raster.Width * 72.0 / raster.Dpi) > 0.02 ||
                Math.Abs(media.Height - raster.Height * 72.0 / raster.Dpi) > 0.02 || media.Left != 0 || media.Bottom != 0)
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
            await ValidateLanguagesAsync(options, ["--list-langs"], token);
            await ValidateLanguagesAsync(options, ["--tessdata-dir", options.TessdataBestDir, "--list-langs"], token);
        }
        catch (Exception) { throw new PdfOcrStartupException(); }
    }

    private static async Task ValidateLanguagesAsync(PdfOptions options, string[] arguments, CancellationToken token)
    {
        var result = await ExternalProcessRunner.RunAsync(CreateRequest(options, arguments, null, 8192), token);
        if (result.ExitCode != 0 || result.Stdout is null) throw new InvalidOperationException();
        var languages = Encoding.UTF8.GetString(result.Stdout).Split('\n', StringSplitOptions.TrimEntries);
        if (!languages.Contains(Horizontal) || !languages.Contains(Vertical) || !languages.Contains("osd"))
            throw new InvalidOperationException();
    }

    private static ExternalProcessRequest CreateRequest(PdfOptions options, string[] arguments, long? fileLimit,
        int? stdoutLimit = null)
        => ProcessMemoryLimits.CreateRequest(options.PrlimitPath, options.TesseractPath, arguments,
            TesseractMemoryLimit, fileLimit, new Dictionary<string, string> { ["OMP_THREAD_LIMIT"] = "1" }, stdoutLimit);
}
