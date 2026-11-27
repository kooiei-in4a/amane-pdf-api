using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfMergeEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        IReadOnlyList<string> inputPaths = [];
        await PdfEndpointHelpers.ExecuteAsync(
            context,
            options.Value,
            "merged.pdf",
            validateRequest: () => PdfEndpointHelpers.ValidateQueryKeys(context.Request.Query),
            async (files, cancellationToken) =>
                inputPaths = await MultipartPdfUpload.ReadMergeFilesAsync(context.Request, files, options.Value, cancellationToken),
            async (files, cancellationToken) =>
            {
                await processor.ValidateForRewriteAsync(inputPaths, cancellationToken);
                await processor.MergeAsync(files, inputPaths, cancellationToken);
            },
            maxRequestBytes: options.Value.MaxMergeRequestBytes);
    }
}
