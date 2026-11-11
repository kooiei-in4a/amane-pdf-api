using System.Globalization;

namespace Amane.Pdf.Api;

internal static class PdfCoordinate
{
    internal static bool TryParse(ReadOnlySpan<char> text, out decimal value)
    {
        value = 0;
        if (text.Length is < 1 or > 6 || text[0] is not ('0' or '1')) return false;
        if (text.Length > 1)
        {
            if (text.Length < 3 || text[1] != '.') return false;
            foreach (var digit in text[2..])
                if (!char.IsAsciiDigit(digit)) return false;
        }
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value <= 1;
    }
}
