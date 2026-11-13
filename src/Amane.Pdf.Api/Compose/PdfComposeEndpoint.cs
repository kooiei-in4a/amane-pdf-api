using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfComposeEndpoint
{
    internal static Task HandleAsync(HttpContext context, PdfOverlayBuilder overlay, PdfcpuProcessor pdfcpu,
        IOptions<PdfOptions> options)
    {
        var settings = options.Value;
        var capacity = new OverlayCapacity(settings.OverlayJobLimitBytes);
        ComposeUpload? upload = null;
        return PdfEndpointHelpers.ExecuteAsync(context, settings, "document.pdf",
            () => PdfEndpointHelpers.ValidateQueryKeys(context.Request.Query),
            async (files, token) => upload = await ComposeUpload.ReadAsync(context.Request, files, settings, capacity, token),
            async (files, token) =>
            {
                try
                {
                    await upload!.VerifyImagesAsync(token);
                    await overlay.ComposeAsync(files, upload, pdfcpu, capacity, token);
                }
                finally
                {
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PdfCompose")
                        .LogInformation("PDF合成の台帳ピーク {Bytes} bytes", capacity.Peak);
                }
            }, maxRequestBytes: ComposeUpload.RequestLimit(settings), processingTimeoutSeconds: 30);
    }

    internal static IResult Capabilities(IOptions<PdfOptions> options) =>
        Results.Ok(new { enabled = true, fonts = ComposeLayout.FontsAvailable(options.Value), layout = new[] { ComposeLayout.Version } });
}
