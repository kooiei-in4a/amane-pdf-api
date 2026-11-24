using System.Globalization;
using System.Text;

namespace Amane.Pdf.Api;

// Builds the invisible OCR text layer from Tesseract TSV word boxes (Issue #116).
// Tesseract's own PDF renderer appends U+0020 to every word, and PDF.js also infers spaces from gaps,
// so Japanese phrases were split. Recognized text never appears in exception messages or logs.
internal static class OcrTextLayer
{
    internal const string FontName = "F1";
    internal const int MaxWords = 20_000;
    internal const int MaxWordLength = 256;
    private const string Header = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext";
    private const string Failure = "PDFのOCR処理に失敗しました。";

    // Gaps are measured against the line thickness: the height of a horizontal line or the width of a vertical one.
    // With 1.2, no word was split across 18 pages of three fonts and sizes; a CJK/Latin limit of 0.2 split
    // 第3章, 合計12件, USBメモリ and PDFを変換. Narrow table cells and spaces inside Japanese text are joined.
    internal const double CjkJoinGap = 1.2;
    // Tesseract splits thousands groups such as "12," + "000". PDF.js search tolerates that space; copied text does not.
    internal const double DigitGroupJoinGap = 0.6;
    // PDF.js 6.3 infers a space above 0.102 em and starts a new text item above 0.6 em. Keep separated words
    // above both so long Latin runs cannot drift when a viewer's fallback font has wider Latin glyphs.
    internal const double MinimumSpaceGap = 0.15;
    internal const double LatinItemGap = 0.65;
    // PDF.js 6.3 positions text-layer spans with the browser fallback font's ascent (about 0.9) and a box about
    // 1.2 em tall, not with the PDF font's ascent. These values centre the search highlight on the line box.
    internal const double FontSizeRatio = 0.85;
    internal const double BaselineRatio = 0.77;
    // In vertical requests a line is placed vertically only when its box is clearly taller than wide, because a
    // vertical page can also contain horizontal headings, dates and captions.
    internal const double VerticalLineAspect = 1.2;
    // PDF.js spreads a line item evenly over its characters. Trailing punctuation has a narrow ink box, so the last
    // word of such a line gets the line's median character pitch; stretching other words overshoots.
    private const string TrailingPunctuation = "。、．，」』）〕】";
    // PDF.js search drops a line break right after a CJK character so that wrapped Japanese text matches, which
    // also joins independent fields ("開館時間" + "返却ポスト" matched "時間返却"). After the last CJK word of a field,
    // U+034F COMBINING GRAPHEME JOINER (invisible, attached without a position check, kept by search
    // normalization) turns that line break into a space. A field ends at a new Tesseract block, an orientation
    // change, a gap above FieldGap line thicknesses, lines that do not overlap, or a step backwards.
    internal const char FieldBoundary = '\u034F';
    internal const double FieldGap = 1.5;

    internal sealed record Word(int Left, int Top, int Width, int Height, string Text, double Confidence);
    internal sealed record Line(int Left, int Top, int Width, int Height, List<Word> Words, int Block = 1);

    internal static List<Line> Read(string path, int imageWidth, int imageHeight, CancellationToken token)
    {
        var lines = new List<Line>();
        var words = 0;
        try
        {
            using var reader = new StreamReader(path, new UTF8Encoding(false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false);
            if (reader.ReadLine() != Header) throw new InvalidDataException();
            (int Block, int Paragraph, int Line)? current = null;
            var row = 0;
            while (reader.ReadLine() is { } text)
            {
                if (++row % 256 == 0) token.ThrowIfCancellationRequested();
                if (text.Length == 0) continue;
                var columns = text.Split('\t', 12);
                if (columns.Length != 12) throw new InvalidDataException();
                var level = Number(columns[0]);
                if (level is < 1 or > 5) throw new InvalidDataException();
                var key = (Number(columns[2]), Number(columns[3]), Number(columns[4]));
                var (left, top, width, height) = (Number(columns[6]), Number(columns[7]), Number(columns[8]), Number(columns[9]));
                if ((long)left + width > imageWidth || (long)top + height > imageHeight) throw new InvalidDataException();
                if (level == 4)
                {
                    lines.Add(new(left, top, width, height, [], key.Item1));
                    current = key;
                }
                else if (level == 5)
                {
                    if (current != key) throw new InvalidDataException();
                    var word = Clean(columns[11]);
                    if (word.Length == 0) continue;
                    if (word.Length > MaxWordLength || ++words > MaxWords)
                        throw new PdfRenderException(PdfRenderReason.OutputTooLarge);
                    var confidence = double.Parse(columns[10], NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(confidence)) throw new InvalidDataException();
                    lines[^1].Words.Add(new(left, top, width, height, word, confidence));
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException or
            FormatException or OverflowException)
        {
            throw new InvalidOperationException(Failure);
        }
        token.ThrowIfCancellationRequested();
        return lines;
    }

    // Character-weighted mean of Tesseract's word confidence (0-100), used to choose between two recognitions.
    internal static double MeanConfidence(IReadOnlyList<Line> lines)
    {
        double sum = 0, count = 0;
        foreach (var word in lines.SelectMany(line => line.Words))
        {
            sum += Math.Max(word.Confidence, 0) * word.Text.Length;
            count += word.Text.Length;
        }
        return count == 0 ? 0 : sum / count;
    }

    internal static bool IsCjk(int scalar) => scalar is >= 0x3000 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x3FFFF;

    internal static bool Joins(string previous, string next, double gap, double thickness)
    {
        if (IsDigitGroup(previous, next)) return gap < DigitGroupJoinGap * thickness;
        return (EndsCjk(previous) || StartsCjk(next)) && gap < CjkJoinGap * thickness;
    }

    // "12," + "000" (thousands) or "2." + "5" (decimal).
    internal static bool IsDigitGroup(string previous, string next)
    {
        if (previous.Length < 2 || !char.IsAsciiDigit(previous[^2]) || next.Length == 0 || !char.IsAsciiDigit(next[0]))
            return false;
        if (previous[^1] == '.') return true;
        if (previous[^1] != ',') return false;
        var digits = next.TakeWhile(char.IsAsciiDigit).Count();
        return digits == 3 && (next.Length == 3 || next[3] is ',' or '.');
    }

    // Writes the content stream for Tesseract's text-only page: the rendered image at dpi, origin bottom-left.
    // The page's font resource is renamed to FontName; GlyphLessFont maps UTF-16 code units to 500/1000 em glyphs.
    internal static void Write(Stream output, IReadOnlyList<Line> lines, bool vertical, int imageHeight, int dpi,
        CancellationToken token)
    {
        var k = 72.0 / dpi;
        var pageHeight = imageHeight * k;
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("BT");
        writer.WriteLine("3 Tr");
        var count = 0;
        var plan = new List<(double Advance, bool Separated, bool Joined)>();
        var placed = lines.Where(line => line.Words.Count > 0 && (IsVertical(line, vertical) ? line.Width : line.Height) > 0)
            .ToArray();
        for (var index = 0; index < placed.Length; index++)
        {
            var line = placed[index];
            var lineVertical = IsVertical(line, vertical);
            var thickness = lineVertical ? line.Width : line.Height;
            var fontSize = thickness * k * FontSizeRatio;
            plan.Clear();
            for (var i = 0; i < line.Words.Count; i++)
            {
                if (++count % 256 == 0) token.ThrowIfCancellationRequested();
                var word = line.Words[i];
                double begin = lineVertical ? word.Top : word.Left;
                double extent = Math.Max(lineVertical ? word.Height : word.Width, 1);
                if (i + 1 == line.Words.Count)
                {
                    plan.Add((extent, false, false));
                    continue;
                }
                var next = line.Words[i + 1];
                double nextBegin = lineVertical ? next.Top : next.Left;
                if (Joins(word.Text, next.Text, nextBegin - (begin + extent), thickness))
                {
                    // Advance exactly to the next word so PDF.js neither keeps nor infers a space. Tesseract boxes
                    // can overlap; trimming them keeps the per-character highlight in place.
                    plan.Add((Math.Max(nextBegin - begin, 1), false, true));
                }
                else
                {
                    var gap = Math.Max(MinimumSpaceGap * thickness, LatinItemGap * thickness * FontSizeRatio);
                    plan.Add((Math.Min(extent, Math.Max(nextBegin - begin - gap, extent * 0.5)), true, false));
                }
            }
            var last = line.Words[^1].Text;
            if (!lineVertical && TrailingPunctuation.Contains(last[^1]))
            {
                var pitches = line.Words.Take(line.Words.Count - 1).Select((word, i) => (word.Text, Plan: plan[i]))
                    .Where(pair => pair.Plan.Joined && pair.Text.All(c => IsCjk(c)))
                    .Select(pair => pair.Plan.Advance / pair.Text.Length).Order().ToArray();
                if (pitches.Length > 0)
                    plan[^1] = plan[^1] with { Advance = Math.Max(plan[^1].Advance, last.Length * pitches[pitches.Length / 2]) };
            }
            for (var i = 0; i < line.Words.Count; i++)
            {
                var word = line.Words[i];
                var (advance, separated, _) = plan[i];
                var horizontalScale = advance * k / (word.Text.Length * 0.5 * fontSize) * 100;
                writer.Write(lineVertical
                    ? Invariant($"0 -1 1 0 {(line.Left + (1 - BaselineRatio) * line.Width) * k:0.###} {pageHeight - word.Top * k:0.###} Tm")
                    : Invariant($"1 0 0 1 {word.Left * k:0.###} {pageHeight - (line.Top + BaselineRatio * line.Height) * k:0.###} Tm"));
                writer.Write(Invariant($" /{FontName} {fontSize:0.###} Tf {horizontalScale:0.###} Tz [ <"));
                writer.Write(Convert.ToHexStringLower(Encoding.BigEndianUnicode.GetBytes(word.Text)));
                writer.WriteLine("> ] TJ");
                if (separated) writer.WriteLine("100 Tz [ <0020> ] TJ");
            }
            if (index + 1 < placed.Length && EndsCjk(last) && EndsField(line, placed[index + 1], vertical))
                writer.WriteLine(Invariant($"100 Tz [ <{(int)FieldBoundary:x4}> ] TJ"));
        }
        writer.WriteLine("ET");
    }

    private static bool IsVertical(Line line, bool vertical) => vertical && line.Height > line.Width * VerticalLineAspect;

    // Lines of one paragraph follow each other closely and overlap across the reading direction.
    internal static bool EndsField(Line line, Line next, bool vertical)
    {
        var lineVertical = IsVertical(line, vertical);
        if (line.Block != next.Block || lineVertical != IsVertical(next, vertical)) return true;
        if (lineVertical)
        {
            var thickness = Math.Max(line.Width, next.Width);
            var overlap = Math.Min(line.Top + line.Height, next.Top + next.Height) - Math.Max(line.Top, next.Top);
            var gap = line.Left - (next.Left + next.Width);
            return overlap <= 0 || next.Left > line.Left || gap > FieldGap * thickness;
        }
        var height = Math.Max(line.Height, next.Height);
        var shared = Math.Min(line.Left + line.Width, next.Left + next.Width) - Math.Max(line.Left, next.Left);
        var below = next.Top - (line.Top + line.Height);
        return shared <= 0 || next.Top < line.Top || below > FieldGap * height;
    }

    private static int Number(string value)
    {
        var number = int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (number < 0) throw new InvalidDataException();
        return number;
    }

    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
            if (!char.IsControl(character) && !char.IsWhiteSpace(character)) builder.Append(character);
        return builder.ToString();
    }

    private static bool StartsCjk(string text) => IsCjk(char.ConvertToUtf32(text, 0));
    private static bool EndsCjk(string text)
        => IsCjk(text.Length > 1 && char.IsLowSurrogate(text[^1]) ? char.ConvertToUtf32(text[^2], text[^1]) : text[^1]);
    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
