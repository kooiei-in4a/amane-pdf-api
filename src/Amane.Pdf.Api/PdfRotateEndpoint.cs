using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfRotateEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        var angle = 0;
        PdfPageSelection? selection = null;
        await PdfEndpointHelpers.ExecuteAsync(
            context,
            options.Value,
            "rotated.pdf",
            validateRequest: () =>
            {
                var angleValues = context.Request.Query["angle"];
                if (angleValues.Count != 1) throw new BadHttpRequestException("Invalid angle query.");
                angle = angleValues[0] switch
                {
                    "90" => 90,
                    "180" => 180,
                    "270" => 270,
                    _ => throw new BadHttpRequestException("Invalid angle query.")
                };

                if (context.Request.Query.TryGetValue("pages", out var pageValues))
                {
                    if (pageValues.Count != 1) throw new BadHttpRequestException("Invalid pages query.");
                    selection = PdfPageSelection.Parse(pageValues[0] ?? string.Empty);
                }
            },
            (files, cancellationToken) =>
                MultipartPdfUpload.ReadFileAsync(context.Request, files, options.Value, cancellationToken),
            async (files, cancellationToken) =>
            {
                await processor.ValidateAsync(files, cancellationToken);
                var pageRange = selection is null
                    ? null
                    : selection.ToQpdfRange(await processor.GetPageCountAsync(files, cancellationToken));
                await processor.RotateAsync(files, angle, pageRange, cancellationToken);
            });
    }
}
