using System.Globalization;
using System.Text;

namespace Amane.Pdf.Api;

internal readonly record struct OcrPageOrientation(int Degrees, double Confidence)
{
    // Tesseract's min_orientation_margin default.
    private const double MinimumMargin = 7;
    private const string DegreesField = "Orientation in degrees:";
    private const string ConfidenceField = "Orientation confidence:";

    internal bool IsConfidentlyUpright => Degrees == 0 && Confidence >= MinimumMargin;

    internal static OcrPageOrientation? Parse(byte[] output)
    {
        int? degrees = null;
        double? confidence = null;
        foreach (var line in Encoding.ASCII.GetString(output).Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(DegreesField, StringComparison.Ordinal))
            {
                if (degrees is not null ||
                    !int.TryParse(line.AsSpan(DegreesField.Length).Trim(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var angle) || angle is not (0 or 90 or 180 or 270))
                    return null;
                degrees = angle;
            }
            else if (line.StartsWith(ConfidenceField, StringComparison.Ordinal))
            {
                if (confidence is not null ||
                    !double.TryParse(line.AsSpan(ConfidenceField.Length).Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var margin) || !double.IsFinite(margin) || margin < 0)
                    return null;
                confidence = margin;
            }
        }
        return degrees is { } value && confidence is { } score ? new(value, score) : null;
    }
}
