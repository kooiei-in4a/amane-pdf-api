using System.Globalization;
using System.Text;

namespace Amane.Pdf.Api;

internal sealed record RedactRaster(int Width, int Height, string WidthPoints, string HeightPoints)
{
    internal const long MaxPixels = 16_000_000;
    internal const int MaxSide = 65535;

    internal static RedactRaster Create(double widthPoints, double heightPoints)
    {
        if (!double.IsFinite(widthPoints) || !double.IsFinite(heightPoints) ||
            widthPoints is < 1 or > 14400 || heightPoints is < 1 or > 14400) throw PdfRedactException.Unsupported();
        var scale = Math.Min(200.0 / 72, Math.Sqrt(MaxPixels / (widthPoints * heightPoints)));
        scale = Math.Min(scale, Math.Min(MaxSide / widthPoints, MaxSide / heightPoints));
        var width = Math.Clamp((int)Math.Floor(widthPoints * scale), 1, MaxSide);
        var height = Math.Clamp((int)Math.Floor(heightPoints * scale), 1, MaxSide);
        while ((long)width * height > MaxPixels)
        {
            if (width >= height) width--; else height--;
        }
        return new(width, height, Points(widthPoints), Points(heightPoints));
    }

    internal RenderFill Fill(RedactRegion region) => new(
        (int)decimal.Floor(region.X * Width), (int)decimal.Floor(region.Y * Height),
        Math.Min(Width, (int)decimal.Ceiling((region.X + region.Width) * Width)),
        Math.Min(Height, (int)decimal.Ceiling((region.Y + region.Height) * Height)));

    internal byte[] Content => Encoding.ASCII.GetBytes($"q {WidthPoints} 0 0 {HeightPoints} 0 0 cm /Im0 Do Q");
    private static string Points(double value) => decimal.Round((decimal)value, 4, MidpointRounding.AwayFromZero)
        .ToString("0.####", CultureInfo.InvariantCulture);
}

internal sealed record RedactPage(int Number, RedactRaster Raster, double ExpectedWidth, double ExpectedHeight,
    string JpegPath, IReadOnlyList<RenderFill> Fills);
