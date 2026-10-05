using Microsoft.AspNetCore.Http.Features;

namespace Amane.Pdf.Api;

internal static class PdfEndpointHelpers
{
    public static void ApplyRequestSizeLimit(HttpContext context, PdfOptions options)
    {
        if (context.Request.ContentLength > options.MaxRequestBytes)
        {
            throw new BadHttpRequestException("Request size limit exceeded.", 413);
        }

        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = options.MaxRequestBytes;
    }

    public static async Task SendPdfAsync(HttpContext context, string path, string fileName)
    {
        await using var output = File.OpenRead(path);
        context.Response.ContentType = "application/pdf";
        context.Response.ContentLength = output.Length;
        context.Response.Headers.ContentDisposition = $"attachment; filename={fileName}";
        await output.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
