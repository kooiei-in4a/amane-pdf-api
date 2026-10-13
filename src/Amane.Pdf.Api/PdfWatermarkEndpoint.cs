using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfWatermarkEndpoint
{
    public static Task HandleAsync(HttpContext context, PdfOverlayBuilder overlay, PdfcpuProcessor pdfcpu,
        IOptions<PdfOptions> options)
    {
        WatermarkOptions? watermark = null;
        string? text = null;
        return PdfEndpointHelpers.ExecuteAsync(context, options.Value, "watermarked.pdf",
            () => watermark = WatermarkOptions.Parse(context.Request.Query),
            async (files, token) => text = WatermarkText.Parse(await MultipartPdfUpload.ReadFileAndTextAsync(
                context.Request, files, options.Value, WatermarkText.MaxBytes, token)),
            (files, token) => overlay.BuildAsync(files, static count => (1, count),
                (canvas, drawToken) => pdfcpu.CreateAsync(files, watermark!.CreateLayer(canvas, text!),
                    canvas.BlankPath, canvas.LayerPath, canvas.JsonBudget, canvas.PdfBudget, drawToken),
                token, fillOpacity: watermark!.Opacity));
    }
}

internal sealed record WatermarkOptions(bool Diagonal, string Color)
{
    internal const double DiagonalOpacity = 0.25, CornerFontSize = 14, CornerMargin = 28;
    internal double? Opacity => Diagonal ? DiagonalOpacity : null;

    internal static WatermarkOptions Parse(IQueryCollection query)
    {
        string[] keys = ["style", "color"];
        if (query.Any(pair => pair.Value.Count != 1 || !keys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
            throw InvalidQuery();
        var diagonal = query["style"].ToString() switch
        {
            "diagonal" => true,
            "corner" => false,
            _ => throw InvalidQuery()
        };
        var color = query["color"].ToString() switch
        {
            "gray" => "#808080",
            "red" => "#C00000",
            _ => throw InvalidQuery()
        };
        return new(diagonal, color);
    }

    internal PdfcpuLayer CreateLayer(OverlayCanvas canvas, string text)
    {
        var em = WatermarkText.EmWidth(text);
        return new(Enumerable.Range(1, canvas.Pages.Count).ToDictionary(page => page,
            page => (IReadOnlyList<PdfcpuText>)[Element(canvas.Pages[page - 1], text, em)]), canvas.Pages);
    }

    internal PdfcpuText Element(PdfcpuPageSize page, string text, double em)
    {
        static double Floor(double size) => Math.Floor(size * 100) / 100;
        if (!Diagonal)
            return new(text, Anchor: "tr", Dx: -CornerMargin, Dy: -CornerMargin,
                FontSize: Math.Clamp(Floor((page.Width - 2 * CornerMargin) / em), 1, CornerFontSize), Color: Color);
        var angle = Math.Atan2(page.Height, page.Width);
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var size = Math.Min(Math.Min(0.8 * page.Width / (em * cos + sin),
            0.8 * page.Height / (em * sin + cos)), Math.Min(0.95 * page.Width / em, 0.95 * page.Height));
        return new(text, Anchor: "c", FontSize: Math.Max(Floor(size), 1), Color: Color,
            Rotation: Math.Round(angle * 180 / Math.PI, 4));
    }

    private static BadHttpRequestException InvalidQuery() => new("Invalid watermark query.");
}
