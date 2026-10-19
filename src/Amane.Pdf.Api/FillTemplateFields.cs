using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amane.Pdf.Api;

internal sealed record FillTemplateField(int Page, decimal X, decimal Y, decimal Width, decimal Height,
    decimal FontSize, string Align, string Text);

internal sealed class FillTemplateFields
{
    internal const int MaxBytes = 64 * 1024, MaxFields = 200, MaxTextLength = 200, MaxLines = 5, MaxLinesPerPage = 100;
    internal IReadOnlyList<FillTemplateField> Items { get; }
    internal int From { get; }
    internal int To { get; }
    private FillTemplateFields(IReadOnlyList<FillTemplateField> items)
    {
        Items = items;
        From = items.Min(x => x.Page);
        To = items.Max(x => x.Page);
    }

    internal static FillTemplateFields Parse(byte[] bytes)
    {
        try
        {
            if (bytes.Length > MaxBytes || bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw Invalid();
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 3 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > MaxFields) throw Invalid();
            var items = new List<FillTemplateField>();
            var pageLines = new Dictionary<int, int>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Invalid();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                    if (property.Name is not ("page" or "x" or "y" or "width" or "height" or "fontSize" or "align" or "text") ||
                        !names.Add(property.Name)) throw Invalid();
                if (names.Count != 8) throw Invalid();
                var page = item.GetProperty("page");
                if (page.ValueKind != JsonValueKind.Number ||
                    !Regex.IsMatch(page.GetRawText(), @"\A[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant) ||
                    !page.TryGetInt32(out var number)) throw Invalid();
                decimal Number(string key, string pattern, decimal min, decimal max)
                {
                    var value = item.GetProperty(key);
                    if (value.ValueKind != JsonValueKind.Number ||
                        !Regex.IsMatch(value.GetRawText(), pattern, RegexOptions.CultureInvariant) ||
                        !decimal.TryParse(value.GetRawText(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ||
                        n < min || n > max) throw Invalid();
                    return n;
                }
                decimal Coordinate(string key) => Number(key, @"\A[01](\.[0-9]{1,4})?\z", 0, 1);
                var x = Coordinate("x"); var y = Coordinate("y");
                var width = Coordinate("width"); var height = Coordinate("height");
                if (width <= 0 || height <= 0 || x + width > 1 || y + height > 1) throw Invalid();
                var size = Number("fontSize", @"\A[0-9]{1,2}(\.[0-9])?\z", 6, 72);
                // JSON syntax already rejects leading zeroes; the lexical checks reject signs and exponents.
                var align = item.GetProperty("align");
                if (align.ValueKind != JsonValueKind.String || align.GetString() is not ("left" or "center" or "right")) throw Invalid();
                var text = item.GetProperty("text");
                if (text.ValueKind != JsonValueKind.String) throw Invalid();
                var valueText = text.GetString()!;
                ValidateText(valueText);
                var lines = valueText.Count(c => c == '\n') + 1;
                if (lines > MaxLines || (pageLines[number] = pageLines.GetValueOrDefault(number) + lines) > MaxLinesPerPage) throw Invalid();
                items.Add(new(number, x, y, width, height, size, align.GetString()!, valueText));
            }
            return new(items);
        }
        catch (JsonException) { throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); } // Includes malformed escaped UTF-16.
    }

    private static void ValidateText(string value)
    {
        if (value.Length is < 1 or > MaxTextLength) throw Invalid();
        var visible = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\n') continue;
            if (!FillTemplateGlyphs.TryGet(c, out _, out _)) throw Invalid();
            if (!char.IsWhiteSpace(c)) visible = true;
            if (i + 1 < value.Length && ((c == '%' && value[i + 1] is 'p' or 'P' or 't' or 'v') ||
                (c == '\\' && value[i + 1] == 'n'))) throw Invalid();
        }
        if (!visible) throw Invalid();
    }

    internal (int From, int To) ResolveRange(int count)
    {
        if (To > count) throw Invalid();
        return (From, To);
    }

    internal PdfcpuLayer CreateLayer(OverlayCanvas canvas)
    {
        var pages = Enumerable.Range(1, canvas.Pages.Count).ToDictionary(n => n, _ => new List<PdfcpuText>());
        foreach (var field in Items)
            pages[field.Page - From + 1].AddRange(FillTemplateLayout.Place(field, canvas.Pages[field.Page - From]));
        return new(pages.ToDictionary(x => x.Key, x => (IReadOnlyList<PdfcpuText>)x.Value), canvas.Pages);
    }

    private static BadHttpRequestException Invalid() => new("Invalid template fields.", 400);
}

internal static class FillTemplateLayout
{
    internal static IReadOnlyList<PdfcpuText> Place(FillTemplateField field, PdfcpuPageSize page)
    {
        // Decimal arithmetic keeps exact fit and a 0.1 pt overflow distinct. Page geometry
        // is converted once; doubles are used only at the pdfcpu serialization boundary.
        var w = (decimal)page.Width; var h = (decimal)page.Height; var s = field.FontSize;
        var lines = field.Text.Split('\n');
        var widths = lines.Select(line => line.Sum(c =>
            FillTemplateGlyphs.TryGet(c, out _, out var advance) ? (int)advance :
                throw new InvalidOperationException(PdfOverlayBuilder.Failure)) * s / 1000m).ToArray();
        var boxWidth = field.Width * w; var boxHeight = field.Height * h;
        var neededHeight = s + 1.2m * s * (lines.Length - 1);
        if (widths.Max() > boxWidth - 0.1m * s || neededHeight > boxHeight)
            throw new PdfFillTemplateException(PdfFillTemplateReason.FieldOverflow);
        var top = h * (1 - field.Y) - (boxHeight - neededHeight) / 2;
        var output = new List<PdfcpuText>();
        for (var i = 0; i < lines.Length; i++)
        {
            // Empty lines occupy layout space and count against limits, but draw no glyphs.
            if (lines[i].Length == 0) continue;
            var x = field.X * w + (field.Align switch
            {
                "left" => 0.05m * s,
                "center" => (boxWidth - widths[i]) / 2,
                "right" => boxWidth - 0.05m * s - widths[i],
                _ => throw new InvalidOperationException(PdfOverlayBuilder.Failure)
            });
            var baseline = top - 1.2m * s * i - 1802m / 2048m * s;
            output.Add(new(lines[i], Anchor: null, X: (double)x,
                Y: (double)(baseline - decimal.Ceiling(246m / 2048m * s)), FontSize: (double)s));
        }
        return output;
    }
}

internal enum PdfFillTemplateReason { FieldOverflow, FormFields }
internal sealed class PdfFillTemplateException(PdfFillTemplateReason reason) : Exception
{
    internal string Code => reason == PdfFillTemplateReason.FieldOverflow ? "field-overflow" : "form-fields";
    internal string Title => reason == PdfFillTemplateReason.FieldOverflow
        ? "文字が指定した矩形に収まりません。" : "フォーム・署名関連の構造を含むPDFは処理できません。";
}
