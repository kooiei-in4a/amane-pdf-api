using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfFillTemplateEndpoint
{
    internal static Task HandleAsync(HttpContext context, PdfOverlayBuilder overlay, PdfcpuProcessor pdfcpu,
        IOptions<PdfOptions> options)
    {
        FillTemplateFields? fields = null;
        var settings = options.Value;
        return PdfEndpointHelpers.ExecuteAsync(context, settings, "filled.pdf",
            () => { if (context.Request.QueryString.HasValue) throw new BadHttpRequestException("Unexpected query.", 400); },
            async (files, token) => fields = FillTemplateFields.Parse(await MultipartPdfUpload.ReadFileAndTextAsync(
                context.Request, files, "fields", FillTemplateFields.MaxBytes, settings.MaxFileBytes,
                settings.MaxFillTemplateRequestBytes, token, textTooLargeStatus: 413)),
            (files, token) => overlay.BuildAsync(files, fields!.ResolveRange,
                (canvas, drawToken) => pdfcpu.CreateAsync(files, fields!.CreateLayer(canvas), canvas.BlankPath,
                    canvas.LayerPath, canvas.JsonBudget, canvas.PdfBudget, drawToken,
                    maxTextsPerPage: FillTemplateFields.MaxLinesPerPage, maxTextLength: FillTemplateFields.MaxTextLength),
                token, fillTemplate: fields),
            maxRequestBytes: settings.MaxFillTemplateRequestBytes);
    }
}
