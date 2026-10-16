using System.Globalization;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfRedactEndpoint
{
    internal static async Task HandleAsync(HttpContext context, PdfRedactProcessor processor, RenderSlot slot, IOptions<PdfOptions> options)
    {
        using var lease = slot.TryAcquire();
        if (lease is null)
        {
            await Results.Problem(statusCode: 503, title: "PDF処理の同時実行上限に達しました。").ExecuteAsync(context);
            return;
        }
        RedactRegions? regions = null;
        RedactResult? result = null;
        var settings = options.Value;
        await PdfEndpointHelpers.ExecuteAsync(context, settings, "redacted.pdf",
            () => { if (context.Request.QueryString.HasValue) throw new BadHttpRequestException("Unexpected query.", 400); },
            async (files, token) => regions = RedactRegions.Parse(await MultipartPdfUpload.ReadFileAndTextAsync(
                context.Request, files, "regions", RedactRegions.MaxBytes, settings.MaxRedactInputBytes, settings.MaxRedactRequestBytes, token)),
            async (files, token) => result = await processor.RedactAsync(files, regions!, token),
            maxRequestBytes: settings.MaxRedactRequestBytes,
            onSend: response =>
            {
                response.Headers["X-Pdf-Redacted-Pages"] = Header(result!.RedactedPages);
                response.Headers["X-Pdf-Rasterized-Pages"] = Header(result.RasterizedPages);
            });
    }
    private static string Header(IEnumerable<int> pages) => string.Join(',', pages.Select(number => number.ToString(CultureInfo.InvariantCulture)));
}
