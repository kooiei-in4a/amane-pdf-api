using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfOptimizeEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        await PdfEndpointHelpers.ExecuteAsync(
            context,
            options.Value,
            "optimized.pdf",
            validateRequest: null,
            (files, cancellationToken) =>
                MultipartPdfUpload.ReadFileAsync(context.Request, files, options.Value, cancellationToken),
            async (files, cancellationToken) =>
            {
                await processor.ValidateAsync(files, cancellationToken);
                await processor.OptimizeAsync(files, cancellationToken);
            });
    }
}
