using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfFromImagesEndpoint
{
    public static async Task HandleAsync(HttpContext context, QpdfProcessor qpdf, PdfcpuProcessor pdfcpu,
        IOptions<PdfOptions> options)
    {
        IReadOnlyList<string> inputs = []; var standard = false;
        await PdfEndpointHelpers.ExecuteAsync(context, options.Value, "images.pdf", () =>
        {
            var query = context.Request.Query;
            if (query.Count != 1 || !query.TryGetValue("quality", out var values) || values.Count != 1 ||
                values[0] is not ("standard" or "original"))
                throw new BadHttpRequestException("Invalid image quality.");
            standard = values[0] == "standard";
        },
        async (files, token) => inputs = await MultipartPdfUpload.ReadImageFilesAsync(context.Request, files, options.Value, token),
        (files, token) => new PdfFromImagesProcessor(options.Value, files, qpdf, pdfcpu).ProcessAsync(inputs, standard, token),
        maxRequestBytes: options.Value.MaxImageRequestBytes, images: true);
    }
}
