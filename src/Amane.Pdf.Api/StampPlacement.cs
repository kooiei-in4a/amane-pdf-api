using System.Globalization;

namespace Amane.Pdf.Api;

internal readonly record struct StampPlacement(double Left, double Bottom, double Width, double Height,
    double Scale, double PageWidth, double PageHeight)
{
    internal const double Tolerance = 1e-6;

    internal static StampPlacement Calculate(StampImageQuery query, StampImage image, PdfcpuPageSize page)
    {
        if (!double.IsFinite(page.Width) || !double.IsFinite(page.Height) ||
            page.Width is <= 0 or > 14400 || page.Height is <= 0 or > 14400 ||
            image.Width is < 1 or > StampImageInput.MaxSide || image.Height is < 1 or > StampImageInput.MaxSide ||
            query.X is < 0 or > 1 || query.Y is < 0 or > 1 || query.Width is < 0.02m or > 0.5m)
            throw new ArgumentOutOfRangeException(nameof(page));
        var width = (double)query.Width * page.Width;
        var height = width * image.Height / image.Width;
        var left = (double)query.X * page.Width;
        var top = (double)query.Y * page.Height;
        if (query.X + query.Width > 1 || query.Y == 1 || top + height > page.Height + Tolerance)
            throw new BadHttpRequestException("Stamp extends beyond displayed page.");
        return new(left, Math.Max(0, page.Height - top - height), width, height,
            width / image.Width, page.Width, page.Height);
    }

    internal bool IsValid => new[] { Left, Bottom, Width, Height, Scale, PageWidth, PageHeight }.All(double.IsFinite) &&
        Left >= 0 && Bottom >= 0 && Width > 0 && Height > 0 && Scale > 0 &&
        PageWidth is > 0 and <= 14400 && PageHeight is > 0 and <= 14400 &&
        Left + Width <= PageWidth + Tolerance && Bottom + Height <= PageHeight + Tolerance;

    internal string Description => string.Create(CultureInfo.InvariantCulture,
        $"pos:bl, off:{Left:R} {Bottom:R}, scale:{Scale:R} abs, rot:0");
}
