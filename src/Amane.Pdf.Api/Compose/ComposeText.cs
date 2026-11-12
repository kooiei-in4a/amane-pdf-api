using System.Text;
using System.Text.RegularExpressions;

namespace Amane.Pdf.Api;

internal sealed record ComposeLines(string[] Lines, int[] Widths, string? Error = null, int? Index = null);

internal static partial class ComposeText
{
    private const string NoStart = "、。，．,.・：；:;？！?!ー‐–—～…‥ゝゞヽヾ々〻）］｝〕〉》」』】〙〗’”)]}ぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶ％%";
    private const string NoEnd = "（［｛〔〈《「『【〘〖‘“([{";
    private static readonly Dictionary<string, decimal> BottomCompensation = new(StringComparer.Ordinal)
    {
        ["gothic"] = 246m / 2048m, ["mincho"] = 127m / 1000m
    };

    [GeneratedRegex(@"[!-~]+|[^!-~]", RegexOptions.CultureInvariant)]
    private static partial Regex Units();

    [GeneratedRegex(@"%[pPtv]|\\n", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenSequence();

    private static int Width(char character, string font) =>
        (font == "mincho" ? MinchoGlyphs.TryGet(character, out _, out var advance) :
            FillTemplateGlyphs.TryGet(character, out _, out advance)) ? advance : throw new InvalidOperationException();

    internal static ComposeLines Wrap(string value, int size, int width, int height, int lineHeight, string font)
    {
        var index = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value != '\n' && (!FillTemplateGlyphs.TryGet(rune.Value, out _, out _) ||
                !MinchoGlyphs.TryGet(rune.Value, out _, out _)))
                return new([], [], "unsupported-text", index);
            index++;
        }
        if (ForbiddenSequence().Match(value) is { Success: true } match) return new([], [], "unsupported-text", match.Index);
        if (value.All(c => c is ' ' or '\u00a0' or '\u3000' or '\n')) return new([], []);
        var available = width - 0.1m * size;
        bool Fits(int units) => units * (decimal)size / 1000m <= available;
        var lines = new List<string>();
        var widths = new List<int>();
        void Emit(string text, int advance) { lines.Add(text); widths.Add(advance); }

        foreach (var paragraph in value.Split('\n'))
        {
            var units = Units().Matches(paragraph).Select(m => m.Value).ToArray();
            var line = new List<(string Text, int Width)>();
            var lineWidth = 0;
            for (var i = 0; i < units.Length;)
            {
                var text = units[i];
                var unitWidth = text.Sum(c => Width(c, font));
                if (Fits(lineWidth + unitWidth))
                {
                    line.Add((text, unitWidth));
                    lineWidth += unitWidth;
                    i++;
                    continue;
                }
                if (line.Count == 0)
                {
                    var end = 0;
                    var prefix = 0;
                    while (end < text.Length && Fits(prefix + Width(text[end], font))) prefix += Width(text[end++], font);
                    if (end == 0) return new([], [], "text-overflow");
                    Emit(text[..end], prefix);
                    units[i] = text[end..];
                    continue;
                }
                var last = line[^1];
                var carry = line.Count > 1 && (NoStart.Contains(text[0]) || NoEnd.Contains(last.Text[^1]));
                if (carry)
                {
                    line.RemoveAt(line.Count - 1);
                    lineWidth -= last.Width;
                }
                Emit(string.Concat(line.Select(unit => unit.Text)), lineWidth);
                line.Clear();
                if (carry) line.Add(last);
                lineWidth = carry ? last.Width : 0;
            }
            Emit(string.Concat(line.Select(unit => unit.Text)), lineWidth);
        }

        var overflow = lines.Count > 40 || size * (1 + lineHeight / 100m * (lines.Count - 1)) > height;
        return new(lines.ToArray(), overflow ? [] : widths.ToArray(), overflow ? "text-overflow" : null);
    }

    internal static IEnumerable<PdfcpuText> Place(ComposeElement element, ComposeLines lines, ComposePage page)
    {
        if (lines.Lines.Length == 0) yield break;
        var r = element.Rect;
        var font = element.Font!;
        var size = element.Size!.Value / 100m;
        var step = size * element.LineHeight!.Value / 100m;
        var extra = r[3] / 100m - size - step * (lines.Lines.Length - 1);
        var top = (page.Height - r[1]) / 100m - (element.Valign switch
        {
            "middle" => extra / 2, "bottom" => extra, _ => 0
        });
        for (var i = 0; i < lines.Lines.Length; i++)
        {
            if (lines.Lines[i].Length == 0) continue;
            var remaining = r[2] / 100m - lines.Widths[i] * size / 1000m;
            var x = r[0] / 100m + (element.Align switch
            {
                "center" => remaining / 2, "right" => remaining - 0.05m * size, _ => 0.05m * size
            });
            var y = top - step * i - 1802m / 2048m * size - decimal.Ceiling(BottomCompensation[font] * size);
            yield return new(lines.Lines[i], Anchor: null, X: (double)x, Y: (double)Math.Max(0, y),
                Dy: (double)Math.Min(0, y), FontSize: (double)size, Color: element.Color!,
                Font: font == "mincho" ? PdfcpuProcessor.MinchoFont : PdfcpuProcessor.JapaneseFont);
        }
    }
}
