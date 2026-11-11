using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfRestrictEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        PdfRestriction? restriction = null;
        await PdfEndpointHelpers.ExecuteAsync(
            context,
            options.Value,
            "restricted.pdf",
            validateRequest: () => PdfEndpointHelpers.ValidateQueryKeys(context.Request.Query),
            async (files, token) =>
            {
                restriction = await MultipartPdfUpload.ReadRestrictAsync(context.Request, files, options.Value, token);
            },
            async (files, token) =>
            {
                await processor.ValidateAsync(files, token);
                await processor.RestrictAsync(files, restriction!, token);
            });
    }
}
