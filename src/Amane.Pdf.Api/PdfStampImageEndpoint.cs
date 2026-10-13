using System.Globalization;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfStampImageEndpoint
{
    public static Task HandleAsync(HttpContext context, PdfOverlayBuilder overlay, PdfcpuProcessor pdfcpu,
        IOptions<PdfOptions> options)
    {
        StampImageQuery? query = null;
        return PdfEndpointHelpers.ExecuteAsync(context, options.Value, "stamped.pdf",
            () => query = StampImageQuery.Parse(context.Request.Query),
            (files, token) => MultipartPdfUpload.ReadFileAndImageAsync(context.Request, files, options.Value, token),
            async (files, token) =>
            {
                var image = await StampImageInput.PrepareAsync(files, options.Value, token);
                await overlay.BuildAsync(files, query!.ResolveRange,
                    (canvas, drawToken) => pdfcpu.StampImageAsync(files, image,
                        StampPlacement.Calculate(query!, image, canvas.Pages[0]), canvas.BlankPath,
                        canvas.LayerPath, canvas.PdfBudget, drawToken),
                    token, drawInputs: [image.Path], layerJson: false);
            }, maxRequestBytes: options.Value.MaxStampRequestBytes,
            tooLargeTitle: "PDF・画像またはリクエストのサイズ上限を超過しました。");
    }
}

internal sealed record StampImageQuery(int Page, decimal X, decimal Y, decimal Width)
{
    internal static StampImageQuery Parse(IQueryCollection query)
    {
        string[] keys = ["page", "x", "y", "width"];
        if (query.Count != keys.Length || query.Any(pair => pair.Value.Count != 1 ||
            !keys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))) throw Invalid();
        var digits = query["page"].ToString();
        if (digits.Length is < 1 or > 10 || digits[0] == '0' || digits.Any(c => c is < '0' or > '9') ||
            !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var page)) throw Invalid();
        decimal Number(string key)
        {
            var value = query[key].ToString();
            if (value.Length is < 1 or > 6 || value[0] is < '0' or > '9' ||
                (value.Length > 1 && (value.Length < 3 || value[1] != '.' ||
                    value.AsSpan(2).ContainsAnyExceptInRange('0', '9'))) ||
                !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
                number is < 0 or > 1) throw Invalid();
            return number;
        }
        var width = Number("width");
        if (width is < 0.02m or > 0.5m) throw Invalid();
        return new(page, Number("x"), Number("y"), width);
    }

    internal (int From, int To) ResolveRange(int count)
        => Page <= count ? (Page, Page) : throw Invalid();

    private static BadHttpRequestException Invalid() => new("Invalid stamp placement query.");
}
