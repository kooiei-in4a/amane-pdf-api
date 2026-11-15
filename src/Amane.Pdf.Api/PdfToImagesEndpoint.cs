using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfToImagesEndpoint
{
    private const int MaxPages = 50;
    private const long MaxOutput = 64L * 1024 * 1024;
    private const long MaxJob = 124L * 1024 * 1024;
    private const int ZipHeadroom = 16 * 1024;

    internal static async Task HandleAsync(HttpContext context, QpdfProcessor qpdf, PdfRenderer renderer,
        RenderSlot slot, IOptions<PdfOptions> options)
    {
        using var lease = slot.TryAcquire();
        if (lease is null)
        {
            await Results.Problem(statusCode: 503, title: "PDF処理の同時実行上限に達しました。").ExecuteAsync(context);
            return;
        }
        var dpi = 0;
        var png = false;
        PdfPageSelection? selection = null;
        var settings = options.Value;
        await PdfEndpointHelpers.ExecuteAsync(context, settings, "images.zip", () =>
        {
            var query = context.Request.Query;
            PdfEndpointHelpers.ValidateQueryKeys(query, "format", "dpi", "pages");
            png = query["format"] is [ "png" ];
            if (!png && query["format"] is not [ "jpeg" ]) throw new BadHttpRequestException("Invalid format query.");
            var value = query["dpi"].ToString();
            if (value.Length is < 2 or > 3 || value[0] == '0' ||
                !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out dpi) || dpi is < 72 or > 200)
                throw new BadHttpRequestException("Invalid dpi query.");
            if (query.TryGetValue("pages", out var pages)) selection = PdfPageSelection.Parse(pages.ToString());
        }, (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, settings, token),
        (files, token) => RenderAsync(files, qpdf, renderer, settings, dpi, png, selection, token), contentType: "application/zip");
    }

    private static async Task RenderAsync(TemporaryPdfFiles files, QpdfProcessor qpdf, PdfRenderer renderer,
        PdfOptions options, int dpi, bool png, PdfPageSelection? selection, CancellationToken token)
    {
        await qpdf.ValidateAsync(files, token);
        var directory = Path.GetFullPath(files.DirectoryPath);
        var input = Path.GetFullPath(files.InputPath);
        var inputBytes = CleanCapacity.Allocated(new FileInfo(input).Length);
        var metadata = Path.Combine(directory, "images-metadata.json");
        var metadataLimit = Math.Min(options.OverlayJsonLimitBytes, MaxJob - inputBytes - 4096);
        if (metadataLimit <= 0) throw new PdfRenderException(PdfRenderReason.TooComplex);
        (int Page, PdfPageAttributes Attributes, int Width, int Height)[] plans;
        try
        {
            await qpdf.RunOverlayWriteAsync(files,
                ["--json=2", "--json-key=pages", "--json-key=qpdf", "--json-stream-data=none", "--decode-level=none", input, metadata],
                metadata, metadataLimit, token);
            using var boxes = new PdfPageBoxes(await File.ReadAllBytesAsync(metadata, token), metadataLimit, PdfPageBoxes.InputObjectLimit, token);
            var ranges = selection?.GetRanges(boxes.Pages.Count) ?? [(1, boxes.Pages.Count)];
            if (ranges.Sum(range => (long)range.End - range.Start + 1) > MaxPages)
                throw new PdfRenderException(PdfRenderReason.TooManyPages);
            // Apply redact's measured PDFium image limits before rendering any page.
            var document = new RedactDocument(boxes, token);
            plans = ranges.SelectMany(range => Enumerable.Range(range.Start, range.End - range.Start + 1)).Select(number =>
            {
                CheckImages(document, number);
                var page = boxes.Read(number - 1, token);
                var width = Math.Ceiling(page.DisplayWidth * dpi / 72);
                var height = Math.Ceiling(page.DisplayHeight * dpi / 72);
                if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 ||
                    width > RedactRaster.MaxSide || height > RedactRaster.MaxSide || width * height > RedactRaster.MaxPixels)
                    throw new PdfRenderException(PdfRenderReason.PageTooLarge);
                return (number, page, (int)width, (int)height);
            }).ToArray();
        }
        finally { File.Delete(metadata); }

        await using var output = TemporaryPdfFiles.CreatePrivateFile(files.OutputPath);
        using var bounded = new CleanLimitedWriteStream(output, MaxOutput, token, OutputTooLarge);
        using var zip = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true);
        var extension = png ? "png" : "jpg";
        var imagePath = Path.Combine(directory, "rendered." + extension);
        foreach (var plan in plans)
        {
            token.ThrowIfCancellationRequested();
            // The rendered page temporarily exists both on disk and within the ZIP.
            var budget = Math.Min(MaxOutput - bounded.Length - ZipHeadroom,
                (MaxJob - inputBytes - CleanCapacity.Allocated(bounded.Length) - ZipHeadroom) / 2);
            if (budget <= 0) throw OutputTooLarge();
            var page = plan.Attributes;
            var render = new RenderPage(input, imagePath, plan.Page, page.DisplayWidth / page.UserUnit,
                page.DisplayHeight / page.UserUnit, plan.Width, plan.Height, budget, []);
            if (png) await renderer.RenderPngAsync(files, render, token);
            else await renderer.RenderJpegAsync(files, render, token);
            var entry = zip.CreateEntry(string.Create(CultureInfo.InvariantCulture, $"page-{plan.Page:D3}.{extension}"),
                CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await using (var source = File.OpenRead(imagePath))
            await using (var target = entry.Open())
                await source.CopyToAsync(target, token);
            File.Delete(imagePath);
        }
    }

    private static void CheckImages(RedactDocument document, int page)
    {
        try { document.CheckImages(page); }
        catch (PdfRedactException exception)
        {
            throw new PdfRenderException(exception.Reason == PdfRedactReason.UnsupportedPdf
                ? PdfRenderReason.UnsupportedPdf : PdfRenderReason.TooComplex);
        }
    }

    private static PdfRenderException OutputTooLarge() => new(PdfRenderReason.OutputTooLarge);
}
