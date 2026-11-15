using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfCompressEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
    {
        var level = "";
        long? target = null;
        var reached = false;
        var count = 0;
        await PdfEndpointHelpers.ExecuteAsync(context, options.Value, "compressed.pdf", () =>
        {
            var query = context.Request.Query;
            PdfEndpointHelpers.ValidateQueryKeys(query, "level", "maxBytes");
            if (query.Count != 1) throw new BadHttpRequestException("Specify exactly one compression option.");
            if (query.TryGetValue("level", out var values))
            {
                if (values[0] is not ("standard" or "strong"))
                    throw new BadHttpRequestException("Invalid compression level query.");
                level = values[0]!;
                return;
            }
            var bytes = query["maxBytes"].ToString();
            // Numeric parsing accepts trailing NUL even with NumberStyles.None.
            if (bytes.Length == 0 || bytes[0] == '0' || bytes[^1] == '\0' ||
                !long.TryParse(bytes, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
                size < 102_400 || size > options.Value.MaxFileBytes)
                throw new BadHttpRequestException("Invalid maxBytes query.");
            target = size;
        },
        (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, options.Value, token),
        async (files, token) =>
        {
            // ExecuteAsync starts the shared hard budget immediately before this delegate.
            var start = Stopwatch.GetTimestamp();
            await processor.ValidateAsync(files, token);
            if (target is { } maxBytes)
                (count, reached) = await PdfCompressProcessor.CompressTargetAsync(options.Value, files, maxBytes, start, token);
            else
                count = await new PdfCompressProcessor(options.Value, files).CompressAsync(level, start, token);
        }, onSend: response =>
        {
            response.Headers["X-Pdf-Images-Recompressed"] = count.ToString(CultureInfo.InvariantCulture);
            if (target.HasValue) response.Headers["X-Pdf-Target-Reached"] = reached ? "true" : "false";
        });
    }
}
