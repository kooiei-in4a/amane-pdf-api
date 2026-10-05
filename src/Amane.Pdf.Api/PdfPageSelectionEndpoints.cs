using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfPageSelectionEndpoints
{
    public static Task ExtractAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options) =>
        HandleAsync(context, processor, options.Value, "extracted.pdf", static (selection, pageCount) => selection.ToQpdfRange(pageCount));

    public static Task DeletePagesAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options) =>
        HandleAsync(context, processor, options.Value, "pages-deleted.pdf", static (selection, pageCount) => selection.ToComplementQpdfRange(pageCount));

    public static Task ReorderAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options) =>
        HandleAsync(context, processor, options.Value, "reordered.pdf", static (selection, pageCount) => selection.ToCompleteQpdfRange(pageCount));

    private static async Task HandleAsync(
        HttpContext context,
        QpdfProcessor processor,
        PdfOptions options,
        string downloadFileName,
        Func<PdfPageSelection, int, string> createPageRange)
    {
        PdfPageSelection? selection = null;
        await PdfEndpointHelpers.ExecuteAsync(
            context,
            options,
            downloadFileName,
            validateRequest: () =>
            {
                var pageValues = context.Request.Query["pages"];
                if (pageValues.Count != 1) throw new BadHttpRequestException("Invalid pages query.");
                selection = PdfPageSelection.Parse(pageValues[0] ?? string.Empty);
            },
            (files, cancellationToken) => MultipartPdfUpload.ReadFileAsync(context.Request, files, options, cancellationToken),
            async (files, cancellationToken) =>
            {
                await processor.ValidateAsync(files, cancellationToken);
                var pageCount = await processor.GetPageCountAsync(files, cancellationToken);
                var pageRange = createPageRange(selection!, pageCount);
                await processor.SelectPagesAsync(files, pageRange, cancellationToken);
            });
    }
}
