using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfUnlockEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        string? password = null;
        await PdfEndpointHelpers.ExecuteAsync(
            context,
            options.Value,
            "unlocked.pdf",
            validateRequest: null,
            async (files, cancellationToken) =>
            {
                password = await MultipartPdfUpload.ReadAsync(context.Request, files, options.Value, cancellationToken);
            },
            (files, cancellationToken) => processor.UnlockAsync(files, password!, cancellationToken));
    }
}
