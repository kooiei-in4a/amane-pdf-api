using System.Globalization;

namespace Amane.Pdf.Api;

public sealed class PdfPageSelection
{
    public const int MaxLength = 4096;

    private readonly PageRange[] ranges;

    private PdfPageSelection(List<PageRange> ranges)
    {
        this.ranges = [.. ranges];
    }

    public static PdfPageSelection Parse(string value)
    {
        if (value.Length is 0 or > MaxLength)
        {
            throw InvalidPages();
        }

        var ranges = new List<PageRange>();
        var index = 0;
        while (index < value.Length)
        {
            var start = ParsePageNumber(value, ref index);
            var end = start;
            if (index < value.Length && value[index] == '-')
            {
                index++;
                end = ParsePageNumber(value, ref index);
                if (start > end) throw InvalidPages();
            }

            ranges.Add(new PageRange(start, end));
            if (index == value.Length) break;
            if (value[index] != ',') throw InvalidPages();
            index++;
            if (index == value.Length) throw InvalidPages();
        }

        var ordered = ranges.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Start <= ordered[i - 1].End) throw InvalidPages();
        }

        return new PdfPageSelection(ranges);
    }

    public string ToQpdfRange(int pageCount)
    {
        if (pageCount <= 0 || ranges.Any(range => range.End > pageCount))
        {
            throw InvalidPages();
        }

        return string.Join(',', ranges.Select(range => range.Start == range.End
            ? range.Start.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{range.Start}-{range.End}")));
    }

    private static int ParsePageNumber(string value, ref int index)
    {
        if (index >= value.Length || value[index] is < '1' or > '9') throw InvalidPages();

        var number = 0;
        while (index < value.Length && value[index] is >= '0' and <= '9')
        {
            try
            {
                number = checked(number * 10 + value[index] - '0');
            }
            catch (OverflowException)
            {
                throw InvalidPages();
            }
            index++;
        }
        return number;
    }

    private static BadHttpRequestException InvalidPages() => new("Invalid pages query.");

    private readonly record struct PageRange(int Start, int End);
}
