using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfToImagesEndpoint
{
    internal const int MaxConfiguredPages = 1000;
    internal const long MaxConfiguredZipBytes = 120L * 1024 * 1024;
    private const int ZipHeadroom = 16 * 1024;

    internal static bool IsValid(PdfOptions options) =>
        options.MaxToImagesPages is >= 1 and <= MaxConfiguredPages &&
        options.MaxToImagesZipBytes is > 0 && options.MaxToImagesZipBytes <= MaxConfiguredZipBytes;

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
        // The same job budget as redact applies to PDFium's shared execution group.
        var jobLimit = settings.RedactJobLimitBytes / 4096 * 4096;
        var inputLimit = Math.Min(settings.MaxFileBytes, jobLimit);
        await PdfEndpointHelpers.ExecuteAsync(context, settings, "images.zip", () =>
        {
            var query = context.Request.Query;
            PdfEndpointHelpers.ValidateQueryKeys(query, "format", "dpi", "pages");
            png = query["format"] is [ "png" ];
            if (!png && query["format"] is not [ "jpeg" ]) throw new BadHttpRequestException("Invalid format query.");
            var value = query["dpi"].ToString();
            // Numeric parsing accepts trailing NUL even with NumberStyles.None.
            if (value.Length is < 2 or > 3 || value[0] == '0' || value[^1] == '\0' ||
                !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out dpi) || dpi is < 72 or > 200)
                throw new BadHttpRequestException("Invalid dpi query.");
            if (query.TryGetValue("pages", out var pages)) selection = PdfPageSelection.Parse(pages.ToString());
        }, (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, inputLimit, token),
        (files, token) => RenderAsync(files, qpdf, renderer, settings, jobLimit, dpi, png, selection, token),
        maxRequestBytes: inputLimit + PdfOptions.MultipartOverheadBytes, contentType: "application/zip");
    }

    private static async Task RenderAsync(TemporaryPdfFiles files, QpdfProcessor qpdf, PdfRenderer renderer,
        PdfOptions options, long jobLimit, int dpi, bool png, PdfPageSelection? selection, CancellationToken token)
    {
        await qpdf.ValidateAsync(files, token);
        var directory = Path.GetFullPath(files.DirectoryPath);
        var input = Path.GetFullPath(files.InputPath);
        var inputBytes = CleanCapacity.Allocated(new FileInfo(input).Length);
        var metadata = Path.Combine(directory, "images-metadata.json");
        var metadataLimit = Math.Min(options.OverlayJsonLimitBytes, jobLimit - inputBytes - 4096);
        if (metadataLimit <= 0) throw new PdfRenderException(PdfRenderReason.TooComplex);
        (int Page, PdfPageAttributes Attributes, int Width, int Height)[] plans;
        try
        {
            await qpdf.RunOverlayWriteAsync(files,
                ["--json=2", "--json-key=pages", "--json-key=qpdf", "--json-stream-data=none", "--decode-level=none", input, metadata],
                metadata, metadataLimit, token);
            using var boxes = new PdfPageBoxes(await File.ReadAllBytesAsync(metadata, token), metadataLimit, PdfPageBoxes.InputObjectLimit, token);
            var ranges = selection?.GetRanges(boxes.Pages.Count) ?? [(1, boxes.Pages.Count)];
            if (ranges.Sum(range => (long)range.End - range.Start + 1) > options.MaxToImagesPages)
                throw new PdfRenderException(PdfRenderReason.TooManyPages);
            // Apply redact's measured PDFium image limits before rendering any page.
            var document = new RedactDocument(boxes, token);
            plans = ranges.SelectMany(range => Enumerable.Range(range.Start, range.End - range.Start + 1)).Select(number =>
            {
                PdfRenderer.CheckImages(document, number);
                var page = boxes.Read(number - 1, token);
                var (width, height) = PdfRenderer.GetPixelSize(page, dpi);
                return (number, page, width, height);
            }).ToArray();
        }
        finally { File.Delete(metadata); }

        Stream? output = TemporaryPdfFiles.CreatePrivateFile(files.OutputPath);
        Stream? source = null;
        var bounded = new PdfZipWriteStream(output, length => CheckZipLength(length, options.MaxToImagesZipBytes), OutputTooLarge, token);
        try
        {
            var zip = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true);
            var extension = png ? "png" : "jpg";
            var imagePath = Path.Combine(directory, "rendered." + extension);
            foreach (var plan in plans)
            {
                token.ThrowIfCancellationRequested();
                // The rendered page temporarily exists both on disk and within the ZIP.
                var budget = Math.Min(options.MaxToImagesZipBytes - bounded.Length - ZipHeadroom,
                    (jobLimit - inputBytes - CleanCapacity.Allocated(bounded.Length) - ZipHeadroom) / 2);
                if (budget <= 0) throw OutputTooLarge();
                var page = plan.Attributes;
                var render = new RenderPage(input, imagePath, plan.Page, page.DisplayWidth / page.UserUnit,
                    page.DisplayHeight / page.UserUnit, plan.Width, plan.Height, budget, []);
                if (png) await renderer.RenderPngAsync(files, render, token);
                else await renderer.RenderJpegAsync(files, render, token);
                var entry = zip.CreateEntry(string.Create(CultureInfo.InvariantCulture, $"page-{plan.Page:D3}.{extension}"),
                    CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                source = File.OpenRead(imagePath);
                // Close the entry only after the copy succeeds, as in split.
                var target = entry.Open();
                await source.CopyToAsync(target, token);
                target.Dispose();
                PdfZipWriteStream.CloseOwnedStream(ref source);
                File.Delete(imagePath);
            }
            zip.Dispose();
            token.ThrowIfCancellationRequested();
            PdfZipWriteStream.CloseOwnedStream(ref output);
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            bounded.Abort();
            throw;
        }
        finally
        {
            // Preserve the original failure if closing a file also fails.
            try { PdfZipWriteStream.CloseOwnedStream(ref source); } catch { }
            try { PdfZipWriteStream.CloseOwnedStream(ref output); } catch { }
        }
    }

    private static void CheckZipLength(long length, long maxOutput)
    {
        if (length > maxOutput) throw OutputTooLarge();
    }

    private static PdfRenderException OutputTooLarge() => new(PdfRenderReason.OutputTooLarge);
}
