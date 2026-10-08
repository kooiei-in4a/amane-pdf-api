using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfCompressEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        var level = "";
        var count = 0;
        await PdfEndpointHelpers.ExecuteAsync(context, options.Value, "compressed.pdf", () =>
        {
            var query = context.Request.Query;
            if (query.Count != 1 || !query.TryGetValue("level", out var values) || values.Count != 1 ||
                values[0] is not ("standard" or "strong"))
                throw new BadHttpRequestException("Invalid compression level query.");
            level = values[0]!;
        },
        (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, options.Value, token),
        async (files, token) =>
        {
            // ExecuteAsync starts the shared hard budget immediately before this delegate.
            var start = Stopwatch.GetTimestamp();
            await processor.ValidateAsync(files, token);
            count = await new PdfCompressProcessor(options.Value, files).CompressAsync(level, start, token);
        }, onSend: response => response.Headers["X-Pdf-Images-Recompressed"] = count.ToString(CultureInfo.InvariantCulture));
    }
}
