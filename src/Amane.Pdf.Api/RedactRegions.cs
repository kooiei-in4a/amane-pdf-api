using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amane.Pdf.Api;

internal readonly record struct RedactRegion(int Page, decimal X, decimal Y, decimal Width, decimal Height);

internal sealed class RedactRegions
{
    internal const int MaxBytes = 16 * 1024, MaxRegions = 100, MaxTargetPages = 20, MaxRasterizedPages = 30;
    internal IReadOnlyList<RedactRegion> Items { get; }
    internal int[] Pages { get; }
    private RedactRegions(IReadOnlyList<RedactRegion> items)
    {
        Items = items;
        Pages = items.Select(x => x.Page).Distinct().Order().ToArray();
        if (Pages.Length > MaxTargetPages) throw Invalid();
    }

    internal static RedactRegions Parse(byte[] bytes)
    {
        try
        {
            if (bytes.Length > MaxBytes || bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw Invalid();
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 3 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > MaxRegions) throw Invalid();
            var items = new List<RedactRegion>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Invalid();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                    if (property.Name is not ("page" or "x" or "y" or "width" or "height") || !names.Add(property.Name)) throw Invalid();
                if (names.Count != 5) throw Invalid();
                var page = item.GetProperty("page");
                if (page.ValueKind != JsonValueKind.Number ||
                    !Regex.IsMatch(page.GetRawText(), @"\A[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant) ||
                    !page.TryGetInt32(out var number)) throw Invalid();
                decimal Coordinate(string key)
                {
                    var value = item.GetProperty(key);
                    if (value.ValueKind != JsonValueKind.Number ||
                        !Regex.IsMatch(value.GetRawText(), @"\A[01](\.[0-9]{1,4})?\z", RegexOptions.CultureInvariant) ||
                        !decimal.TryParse(value.GetRawText(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var coordinate) ||
                        coordinate > 1) throw Invalid();
                    return coordinate;
                }
                var x = Coordinate("x"); var y = Coordinate("y");
                var width = Coordinate("width"); var height = Coordinate("height");
                if (width <= 0 || height <= 0 || x + width > 1 || y + height > 1) throw Invalid();
                items.Add(new(number, x, y, width, height));
            }
            return new(items);
        }
        catch (JsonException) { throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
    }

    internal void CheckPageCount(int count)
    {
        if (Pages[^1] > count) throw Invalid();
    }
    private static BadHttpRequestException Invalid() => new("Invalid redact regions.", 400);
}
