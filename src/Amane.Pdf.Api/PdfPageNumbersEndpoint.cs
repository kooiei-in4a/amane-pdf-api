using System.Globalization;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfPageNumbersEndpoint
{
    public static Task HandleAsync(HttpContext context, PdfOverlayBuilder overlay, PdfcpuProcessor pdfcpu,
        IOptions<PdfOptions> options)
    {
        PageNumberOptions? numbers = null;
        return PdfEndpointHelpers.ExecuteAsync(context, options.Value, "numbered.pdf",
            () => numbers = PageNumberOptions.Parse(context.Request.Query),
            (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, options.Value, token),
            (files, token) => overlay.BuildAsync(files, numbers!.ResolveRange,
                (canvas, drawToken) => pdfcpu.CreateAsync(files, numbers!.CreateLayer(canvas),
                    canvas.BlankPath, canvas.LayerPath, canvas.JsonBudget, canvas.PdfBudget, drawToken), token));
    }
}

internal sealed record PageNumberOptions(string Anchor, double Dx, double Dy, string Format,
    int From, int Start, double FontSize)
{
    internal static PageNumberOptions Parse(IQueryCollection query)
    {
        string[] keys = ["position", "format", "from", "start", "size"];
        if (query.Any(pair => pair.Value.Count != 1 || !keys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)))
            throw InvalidQuery();
        var (anchor, dx, dy) = query["position"].ToString() switch
        {
            "bottom-center" => ("bc", 0, 28),
            "bottom-right" => ("br", -28, 28),
            "bottom-left" => ("bl", 28, 28),
            "top-right" => ("tr", -28, -28),
            _ => throw InvalidQuery()
        };
        var format = query["format"].ToString();
        if (format is not ("number" or "number-total" or "dash")) throw InvalidQuery();
        var size = query.TryGetValue("size", out var values) ? values[0] : "medium";
        var fontSize = size switch
        {
            "small" => 9,
            "medium" => 10.5,
            "large" => 12,
            _ => throw InvalidQuery()
        };
        int Number(string key)
        {
            if (!query.TryGetValue(key, out var digits)) return 1;
            var value = digits[0];
            if (value is null || value.Length is < 1 or > 5 || value[0] == '0' ||
                value.Any(c => c is < '0' or > '9')) throw InvalidQuery();
            return int.Parse(value, CultureInfo.InvariantCulture);
        }
        return new(anchor, dx, dy, format, Number("from"), Number("start"), fontSize);
    }

    internal (int From, int To) ResolveRange(int pageCount)
    {
        if (pageCount < From || (long)Start + pageCount - From > 99999) throw InvalidQuery();
        return (From, pageCount);
    }

    internal PdfcpuLayer CreateLayer(OverlayCanvas canvas)
    {
        var total = Start + canvas.Pages.Count - 1;
        return new(Enumerable.Range(1, canvas.Pages.Count).ToDictionary(page => page, page =>
        {
            var number = Start + page - 1;
            var text = Format switch
            {
                "number" => number.ToString(CultureInfo.InvariantCulture),
                "number-total" => string.Create(CultureInfo.InvariantCulture, $"{number} / {total}"),
                "dash" => string.Create(CultureInfo.InvariantCulture, $"- {number} -"),
                _ => throw new InvalidOperationException("Invalid page number format.")
            };
            return (IReadOnlyList<PdfcpuText>)[new(text, Anchor: Anchor, Dx: Dx, Dy: Dy, FontSize: FontSize)];
        }), canvas.Pages);
    }

    private static BadHttpRequestException InvalidQuery() => new("Invalid page number query.");
}
