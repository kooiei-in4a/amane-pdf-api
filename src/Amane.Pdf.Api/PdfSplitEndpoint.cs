using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfSplitEndpoint
{
    public static Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        PdfSplitPlan? plan = null;
        var baseName = "document";
        return PdfEndpointHelpers.ExecuteAsync(context, options.Value, "split.zip",
            () => plan = PdfSplitPlan.Parse(context.Request.Query, options.Value.MaxSplitParts),
            async (files, token) => baseName = PdfSplitFileName.NormalizeBaseName(
                await MultipartPdfUpload.ReadFileNameAsync(context.Request, files, options.Value, token)),
            (files, token) => new PdfSplitProcessor(processor, options.Value, files).SplitAsync(plan!, baseName, token),
            contentType: "application/zip");
    }
}
