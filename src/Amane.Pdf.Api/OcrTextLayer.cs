using System.Globalization;
using System.Text;
using System.Xml;

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

    internal sealed record Word(int Left, int Top, int Width, int Height, string Text);
    internal sealed record Line(int Left, int Top, int Width, int Height, List<Word> Words, int Block = 1, int Angle = 0);

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
                    lines[^1].Words.Add(new(left, top, width, height, word));
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

    internal static bool IsCjk(int scalar) => scalar is >= 0x3000 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or
        >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x3FFFF;

    // hOCR and TSV use the same image-space line boxes. Use hOCR only for the line angle, leaving TSV
    // responsible for word order and text. Rotate boxes to reading coordinates before spacing the words.
    internal static void Orient(List<Line> lines, string hocr, int imageWidth, int imageHeight, CancellationToken token)
    {
        var angles = new Dictionary<(int Left, int Top, int Width, int Height), int>();
        try
        {
            using var file = File.OpenRead(hocr);
            using var reader = XmlReader.Create(file, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = file.Length
            });
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "span" ||
                    reader.GetAttribute("class") is not ("ocr_line" or "ocr_header" or "ocr_textfloat" or "ocr_caption"))
                    continue;
                var title = reader.GetAttribute("title")?.Split(';', StringSplitOptions.TrimEntries);
                var rotation = title?.FirstOrDefault(part => part.StartsWith("textangle ", StringComparison.Ordinal));
                if (rotation is null) continue;
                var angle = Number(rotation["textangle ".Length..]);
                var box = title![0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (angle is not (90 or 180 or 270) || box.Length != 5 || box[0] != "bbox")
                    throw new InvalidDataException();
                var left = Number(box[1]);
                var top = Number(box[2]);
                var width = Number(box[3]) - left;
                var height = Number(box[4]) - top;
                if (width < 0 || height < 0 || (long)left + width > imageWidth || (long)top + height > imageHeight)
                    throw new InvalidDataException();
                angles[(left, top, width, height)] = angle;
            }
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException or FormatException or OverflowException)
        {
            throw new InvalidOperationException(Failure);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var line = lines[i];
            if (!angles.TryGetValue((line.Left, line.Top, line.Width, line.Height), out var angle)) continue;
            var box = Upright(line.Left, line.Top, line.Width, line.Height, angle, imageWidth, imageHeight);
            var words = line.Words.Select(word =>
            {
                var bounds = Upright(word.Left, word.Top, word.Width, word.Height, angle, imageWidth, imageHeight);
                return new Word(bounds.Left, bounds.Top, bounds.Width, bounds.Height, word.Text);
            }).ToList();
            lines[i] = new(box.Left, box.Top, box.Width, box.Height, words, line.Block, angle);
        }
    }

    private static (int Left, int Top, int Width, int Height) Upright(
        int left, int top, int width, int height, int angle, int imageWidth, int imageHeight) => angle switch
    {
        90 => (imageHeight - top - height, left, height, width),
        180 => (imageWidth - left - width, imageHeight - top - height, width, height),
        270 => (top, imageWidth - left - width, height, width),
        _ => throw new InvalidOperationException(Failure)
    };

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
    internal static void Write(Stream output, IReadOnlyList<Line> lines, bool vertical, int imageWidth, int imageHeight, int dpi,
        CancellationToken token)
    {
        var k = 72.0 / dpi;
        var pageWidth = imageWidth * k;
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
            var endsField = index + 1 < placed.Length && EndsCjk(last) && EndsField(line, placed[index + 1], vertical);
            if (line.Angle == 180)
            {
                WriteUpsideDown(writer, line, plan, fontSize, k, pageWidth, pageHeight, endsField);
                continue;
            }
            for (var i = 0; i < line.Words.Count; i++)
            {
                var word = line.Words[i];
                var (advance, separated, _) = plan[i];
                var horizontalScale = advance * k / (word.Text.Length * 0.5 * fontSize) * 100;
                writer.Write(lineVertical
                    ? Invariant($"0 -1 1 0 {(line.Left + (1 - BaselineRatio) * line.Width) * k:0.###} {pageHeight - word.Top * k:0.###} Tm")
                    : HorizontalMatrix(line, word, k, pageWidth, pageHeight));
                writer.Write(Invariant($" /{FontName} {fontSize:0.###} Tf {horizontalScale:0.###} Tz [ <"));
                writer.Write(Convert.ToHexStringLower(Encoding.BigEndianUnicode.GetBytes(word.Text)));
                writer.WriteLine("> ] TJ");
                if (separated) writer.WriteLine("100 Tz [ <0020> ] TJ");
            }
            if (endsField)
                writer.WriteLine(Invariant($"100 Tz [ <{(int)FieldBoundary:x4}> ] TJ"));
        }
        writer.WriteLine("ET");
    }

    // PDFium sorts text objects left to right on the same horizontal baseline, even for a 180° matrix.
    // Keep each continuous run in one text object. Fit its character pitch to the measured run width,
    // and start a new object for widely separated fields so their positions still align with the scan.
    private static void WriteUpsideDown(StreamWriter writer, Line line,
        List<(double Advance, bool Separated, bool Joined)> plan, double fontSize, double k,
        double pageWidth, double pageHeight, bool endsField)
    {
        var start = 0;
        var text = new StringBuilder();
        for (var i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            text.Append(word.Text);
            var last = i + 1 == line.Words.Count;
            var separateField = !last && plan[i].Separated &&
                line.Words[i + 1].Left - word.Left - word.Width > FieldGap * line.Height;
            if (!last && !separateField)
            {
                if (plan[i].Separated) text.Append(' ');
                continue;
            }
            var first = line.Words[start];
            var width = Math.Max(word.Left + plan[i].Advance - first.Left, 1);
            var scale = width * k / (text.Length * 0.5 * fontSize) * 100;
            if ((separateField || endsField) && EndsCjk(word.Text)) text.Append(FieldBoundary);
            writer.Write(HorizontalMatrix(line, first, k, pageWidth, pageHeight));
            writer.Write(Invariant($" /{FontName} {fontSize:0.###} Tf {scale:0.###} Tz [ <"));
            writer.Write(Convert.ToHexStringLower(Encoding.BigEndianUnicode.GetBytes(text.ToString())));
            writer.WriteLine("> ] TJ");
            text.Clear();
            start = i + 1;
        }
    }

    private static string HorizontalMatrix(Line line, Word word, double k, double pageWidth, double pageHeight)
    {
        var x = word.Left * k;
        var top = (line.Top + BaselineRatio * line.Height) * k;
        return line.Angle switch
        {
            90 => Invariant($"0 1 -1 0 {top:0.###} {x:0.###} Tm"),
            180 => Invariant($"-1 0 0 -1 {pageWidth - x:0.###} {top:0.###} Tm"),
            270 => Invariant($"0 -1 1 0 {pageWidth - top:0.###} {pageHeight - x:0.###} Tm"),
            _ => Invariant($"1 0 0 1 {x:0.###} {pageHeight - top:0.###} Tm")
        };
    }

    private static bool IsVertical(Line line, bool vertical) => vertical && line.Height > line.Width * VerticalLineAspect;

    // Lines of one paragraph follow each other closely and overlap across the reading direction.
    internal static bool EndsField(Line line, Line next, bool vertical)
    {
        var lineVertical = IsVertical(line, vertical);
        if (line.Block != next.Block || line.Angle != next.Angle || lineVertical != IsVertical(next, vertical)) return true;
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
