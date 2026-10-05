using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfProtectEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        string? password = null;
        await PdfEndpointHelpers.ExecuteAsync(
            context,
            options.Value,
            "protected.pdf",
            validateRequest: null,
            async (files, cancellationToken) =>
            {
                password = await MultipartPdfUpload.ReadAsync(context.Request, files, options.Value, cancellationToken);
            },
            async (files, cancellationToken) =>
            {
                await processor.ValidateAsync(files, cancellationToken);
                await processor.ProtectAsync(files, password!, cancellationToken);
            });
    }
}
