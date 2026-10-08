using System.Globalization;

namespace Amane.Pdf.Api;

internal sealed record SplitPart(int Index, int Start, int End)
{
    internal string PageRange => Start == End ? Start.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Start}-{End}");
    internal string EntryName => string.Create(CultureInfo.InvariantCulture, $"part-{Index:D3}_p{PageRange}.pdf");
}

internal sealed class PdfSplitPlan(int? every, PdfPageSelection? ranges, int maxParts)
{
    internal static PdfSplitPlan Parse(IQueryCollection query, int maxParts)
    {
        if (query.Count != 1) throw InvalidSplit();
        if (query.TryGetValue("every", out var values) && values.Count == 1 && values[0] is { } value)
        {
            if (value.Length is < 1 or > 5 || value[0] == '0' || value.Any(c => c is < '0' or > '9'))
                throw InvalidSplit();
            return new(int.Parse(value, CultureInfo.InvariantCulture), null, maxParts);
        }
        if (query.TryGetValue("ranges", out values) && values.Count == 1 && values[0] is { } selection)
        {
            var parsed = PdfPageSelection.Parse(selection);
            if (parsed.RangeCount < 2 || parsed.RangeCount > maxParts) throw InvalidSplit();
            return new(null, parsed, maxParts);
        }
        throw InvalidSplit();
    }

    internal IReadOnlyList<SplitPart> CreateParts(int pageCount)
    {
        if (pageCount <= 0) throw InvalidSplit();
        if (every is int size)
        {
            var count = ((long)pageCount - 1) / size + 1;
            if (count < 2 || count > maxParts) throw InvalidSplit();
            return Enumerable.Range(1, (int)count).Select(index =>
            {
                var start = ((long)index - 1) * size + 1;
                return new SplitPart(index, (int)start, (int)Math.Min(start + size - 1, pageCount));
            }).ToArray();
        }
        return ranges!.GetRanges(pageCount).Select((range, index) => new SplitPart(index + 1, range.Start, range.End)).ToArray();
    }

    private static BadHttpRequestException InvalidSplit() => new("Invalid split query.");
}
