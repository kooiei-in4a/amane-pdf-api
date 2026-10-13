using System.Globalization;
using System.Text;

namespace Amane.Pdf.Api;

internal static class WatermarkText
{
    internal const int MaxBytes = 120, MaxElements = 30;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 1 or > MaxBytes) throw Invalid();
        string text;
        try { text = StrictUtf8.GetString(utf8); }
        catch (DecoderFallbackException) { throw Invalid(); }
        if (StringInfo.ParseCombiningCharacters(text).Length is < 1 or > MaxElements) throw Invalid();
        var visible = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsDrawable(rune.Value)) throw Invalid();
            visible |= rune.Value is not (0x20 or 0x3000);
        }
        if (!visible) throw Invalid();
        return text;
    }

    internal static bool IsDrawable(int scalar)
        => scalar is not (0x25 or 0x5c) && WatermarkGlyphs.Contains(scalar);

    // ASCII uses the locked TTF's advances (pdfcpu rounds down to 1/1000 em);
    // halfwidth is exactly 0.5 em and other allowed glyphs are at most 1 em.
    // Count runes, including marks.
    internal static double EmWidth(string text)
    {
        double width = 0;
        foreach (var rune in text.EnumerateRunes())
            width += rune.Value switch
            {
                >= 0x20 and <= 0x7e => WatermarkGlyphs.AsciiAdvances[rune.Value - 0x20] / 2048.0,
                >= 0xff61 and <= 0xff9f => 0.5,
                _ => 1
            };
        return width;
    }

    private static BadHttpRequestException Invalid() => new("Invalid watermark text.");
}
