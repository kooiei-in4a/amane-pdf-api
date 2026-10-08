using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfSplitEndpoint
{
    public static Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        PdfSplitPlan? plan = null;
        return PdfEndpointHelpers.ExecuteAsync(context, options.Value, "split.zip",
            () => plan = PdfSplitPlan.Parse(context.Request.Query, options.Value.MaxSplitParts),
            (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, options.Value, token),
            (files, token) => new PdfSplitProcessor(processor, options.Value, files).SplitAsync(plan!, token),
            contentType: "application/zip");
    }
}
