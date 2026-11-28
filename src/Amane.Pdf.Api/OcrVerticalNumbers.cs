using System.Buffers.Binary;
using System.IO.Compression;

namespace Amane.Pdf.Api;

internal sealed class OcrVerticalNumbers
{
    internal const int MaxCandidates = 32;
    private const double SuspectConfidence = 50;
    private const double AcceptConfidence = 60;
    private const int MinColumnWidth = 12;
    private const int MaxColumnWidth = 256;
    private const byte InkThreshold = 128;

    private readonly record struct Cell(int Top, int Bottom, int DigitBands)
    {
        internal int Height => Bottom - Top;
    }

    private sealed record Candidate(int Line, int Word, int Left, int Width, int ColumnWidth, Cell Cell, Cell Previous, Cell Next);

    private readonly byte[] grayscale;
    private readonly int imageWidth;
    private readonly List<Candidate> candidates;
    private readonly int margin;
    private readonly int pitch;
    private readonly bool shrink;

    private OcrVerticalNumbers(byte[] grayscale, int imageWidth, List<Candidate> candidates)
    {
        this.grayscale = grayscale;
        this.imageWidth = imageWidth;
        this.candidates = candidates;
        var widest = candidates.Max(candidate => candidate.Width);
        var padding = Math.Max(widest, 40);
        shrink = widest >= 50;
        margin = Scale(padding);
        Width = Scale(widest + 2 * padding);
        pitch = Scale(candidates.Max(candidate => candidate.Cell.Height) + 2 * padding);
    }

    internal int Width { get; }
    internal int Height => pitch * candidates.Count;

    private int Scale(int length) => shrink ? (3 * length + 2) / 4 : length;

    internal static OcrVerticalNumbers? Find(string png, int width, int height, IReadOnlyList<OcrTextLayer.Line> lines,
        bool vertical, CancellationToken token)
    {
        if (!lines.Any(line => OcrTextLayer.IsVertical(line, vertical) && line.Words.Any(IsSuspect))) return null;

        byte[] pixels;
        try { pixels = OcrPageImage.ReadGrayscale(png, width, height, token); }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException) { return null; }

        var candidates = new List<Candidate>();
        for (var index = 0; index < lines.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var line = lines[index];
            if (!OcrTextLayer.IsVertical(line, vertical) || !line.Words.Any(IsSuspect)) continue;

            // Word edges describe the body column more closely than a line box that also contains ruby.
            var left = Median(line.Words.Select(word => word.Left));
            var right = Median(line.Words.Select(word => word.Left + word.Width));
            var columnWidth = right - left;
            if (columnWidth is < MinColumnWidth or > MaxColumnWidth) continue;

            var padding = columnWidth / 5;
            left = Math.Max(0, left - padding);
            right = Math.Min(width, right + padding);
            var cells = Project(pixels, width, left, right, columnWidth, line.Top, line.Top + line.Height, token);
            var suspects = line.Words.Select((word, i) => (Word: word, Index: i))
                .Where(entry => IsSuspect(entry.Word) && entry.Word.Top >= line.Top &&
                    entry.Word.Top <= line.Top + line.Height).OrderBy(entry => entry.Word.Top).ToArray();
            // A TSV word can cover multiple glyphs and its leading edge may drift by one cell.
            // Prefer its nearest ink cell and do not send the same crop twice.
            var cursor = 0;
            var tolerance = columnWidth * 1.35;
            for (var cellIndex = 0; cellIndex < cells.Count && cursor < suspects.Length; cellIndex++)
            {
                token.ThrowIfCancellationRequested();
                var cell = cells[cellIndex];
                if (cell.DigitBands == 0) continue;
                while (cursor + 1 < suspects.Length &&
                    Math.Abs(suspects[cursor + 1].Word.Top - cell.Top) < Math.Abs(suspects[cursor].Word.Top - cell.Top))
                    cursor++;
                if (Math.Abs(suspects[cursor].Word.Top - cell.Top) > tolerance) continue;
                var wordIndex = suspects[cursor++].Index;
                var previous = cellIndex > 0 ? cells[cellIndex - 1] : default;
                var next = cellIndex + 1 < cells.Count ? cells[cellIndex + 1] : default;
                candidates.Add(new(index, wordIndex, left, right - left, columnWidth, cell, previous, next));
                if (candidates.Count > MaxCandidates) return null;
            }
        }

        return candidates.Count == 0 ? null : new(pixels, width, candidates);
    }

    private static bool IsSuspect(OcrTextLayer.Word word) => word.Confidence < SuspectConfidence ||
        word.Text.Any(c => char.IsAsciiLetterOrDigit(c) || c is >= '０' and <= '９' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ');

    private static char NormalizeDigit(char c) => c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c;

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static List<Cell> Project(byte[] pixels, int stride, int left, int right, int columnWidth, int top, int bottom,
        CancellationToken token)
    {
        var width = right - left;
        var cells = new List<Cell>();
        var start = -1;
        var lastInk = -1;
        var columns = new int[width];
        var gap = Math.Max(1, columnWidth / 20);
        for (var y = top; y < bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            var rowHasInk = false;
            for (var x = left; x < right; x++)
            {
                if (pixels[y * stride + x] >= InkThreshold) continue;
                columns[x - left]++;
                rowHasInk = true;
            }
            if (rowHasInk)
            {
                if (start < 0) start = y;
                lastInk = y;
            }
            else if (start >= 0 && y - lastInk > gap)
            {
                AddCell();
            }
        }
        if (start >= 0) AddCell();
        return cells;

        void AddCell()
        {
            var height = lastInk + 1 - start;
            var first = Array.FindIndex(columns, count => count > 0);
            var last = Array.FindLastIndex(columns, count => count > 0);
            var bands = 0;
            var bandStart = -1;
            var substantialBands = 0;
            for (var x = first; x <= last + 1; x++)
            {
                if (x <= last && columns[x] > 0)
                {
                    if (bandStart < 0) bandStart = x;
                }
                else if (bandStart >= 0)
                {
                    bands++;
                    if (x - bandStart >= columnWidth * 0.13) substantialBands++;
                    bandStart = -1;
                }
            }
            var digitBands = height >= columnWidth * 0.38 && height <= columnWidth * 1.12 &&
                last - first + 1 >= columnWidth * 0.52 && bands is 2 or 3 && substantialBands == bands ? bands : 0;
            cells.Add(new(start, lastInk + 1, digitBands));
            start = -1;
            Array.Clear(columns);
        }
    }

    internal List<OcrTextLayer.Line> Apply(List<OcrTextLayer.Line> original, IReadOnlyList<OcrTextLayer.Line> recognized)
    {
        // Each row of the batch has a separate slot, including white space on either side.
        // Reject any slot touched by multiple TSV lines or a line spanning two slots.
        var slots = new List<OcrTextLayer.Line>?[candidates.Count];
        foreach (var line in recognized.Where(line => line.Words.Count > 0 && line.Height > 0))
        {
            var first = line.Top / pitch;
            var last = (line.Top + line.Height - 1) / pitch;
            for (var slot = first; slot <= last && slot < slots.Length; slot++)
            {
                slots[slot] ??= [];
                slots[slot]!.Add(line);
                if (first != last) slots[slot]!.Add(line);
            }
        }

        var edits = new Dictionary<int, List<(Candidate Candidate, OcrTextLayer.Word Word)>>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (slots[i] is not [{ Words: [var word] }] || word.Confidence < AcceptConfidence) continue;
            var number = string.Concat(word.Text.Select(NormalizeDigit));
            var candidate = candidates[i];
            if (number.Length != candidate.Cell.DigitBands || !number.All(char.IsAsciiDigit)) continue;
            // Separate upright strokes can be kana or kanji. The digit-only retry cannot
            // distinguish them from a run of ones without evidence in the primary OCR.
            var primary = original[candidate.Line].Words[candidate.Word];
            if (number.All(c => c == '1') && !primary.Text.Any(c => c is '1' or '１')) continue;
            var top = i * pitch + margin;
            if (word.Top >= top + Scale(candidate.Cell.Height) || word.Top + word.Height <= top) continue;
            if (!edits.TryGetValue(candidate.Line, out var changes)) edits.Add(candidate.Line, changes = []);
            changes.Add((candidate, new(candidate.Left, candidate.Cell.Top, candidate.Width, candidate.Cell.Height,
                number, word.Confidence, IsVerticalNumber: true)));
        }
        if (edits.Count == 0) return original;

        var result = new List<OcrTextLayer.Line>(original);
        foreach (var (lineIndex, changes) in edits)
        {
            var old = original[lineIndex];
            var words = new List<OcrTextLayer.Word>(old.Words);
            foreach (var (candidate, number) in changes.OrderByDescending(change => change.Candidate.Word))
            {
                var index = candidate.Word;
                var suspect = words[index];
                // A long CJK word that starts below the cell can represent the next glyph,
                // with the number omitted entirely by the vertical model.
                if (suspect.Top >= candidate.Cell.Bottom && suspect.Height >= candidate.ColumnWidth * 1.5 &&
                    suspect.Text.EnumerateRunes().All(rune => OcrTextLayer.IsCjk(rune.Value)))
                    words.Insert(index, number);
                else
                    words[index] = number;

                // A low-confidence digit can be emitted as a separate word next to the
                // suspect, with its TSV box shifted down into the following glyph.
                if (index + 2 < words.Count && candidate.Next.Height > 0 &&
                    candidate.Next.Top - candidate.Cell.Bottom < candidate.ColumnWidth * 0.55)
                {
                    var fragment = words[index + 1];
                    if (fragment.Confidence < SuspectConfidence && fragment.Text.Length == 1 &&
                        number.Text.Contains(NormalizeDigit(fragment.Text[0])) &&
                        fragment.Top < candidate.Next.Top + candidate.ColumnWidth / 2 &&
                        OcrTextLayer.IsCjk(words[index + 2].Text.EnumerateRunes().First().Value))
                        words.RemoveAt(index + 1);
                }

                var previous = candidate.Previous;
                if (index > 0 && words[index - 1].Top >= number.Top && previous.Height > 0 &&
                    number.Top - previous.Bottom < candidate.ColumnWidth)
                    words[index - 1] = words[index - 1] with { Top = previous.Top, Height = previous.Height };
                if (index + 1 < words.Count && !words[index + 1].IsVerticalNumber &&
                    candidate.Next.Height > 0 && candidate.Next.Top - candidate.Cell.Bottom < candidate.ColumnWidth * 0.55 &&
                    words[index + 1].Top - candidate.Next.Top < candidate.ColumnWidth * 1.35)
                {
                    var next = words[index + 1];
                    var bottom = next.Top + next.Height;
                    words[index + 1] = next with { Top = candidate.Cell.Bottom, Height = Math.Max(1, bottom - candidate.Cell.Bottom) };
                }
            }
            result[lineIndex] = old with { Words = words };
        }
        return result;
    }

    internal void Write(Stream output, CancellationToken token)
    {
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], Height);
        header[8] = 8; // grayscale, filter 0
        WriteChunk(output, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[Width + 1];
            for (var y = 0; y < Height; y++)
            {
                token.ThrowIfCancellationRequested();
                row.AsSpan(1).Fill(255);
                var candidate = candidates[y / pitch];
                var cellRow = y % pitch - margin;
                if (cellRow >= 0 && cellRow < Scale(candidate.Cell.Height))
                {
                    var scaledWidth = Scale(candidate.Width);
                    var target = row.AsSpan(1 + (Width - scaledWidth) / 2, scaledWidth);
                    CopyRow(candidate, cellRow, target);
                }
                zlib.Write(row);
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
    }

    private void CopyRow(Candidate candidate, int row, Span<byte> destination)
    {
        if (!shrink)
        {
            grayscale.AsSpan((candidate.Cell.Top + row) * imageWidth + candidate.Left, candidate.Width).CopyTo(destination);
            return;
        }

        // Sample at pixel centres so rescaling keeps the strokes in proportion.
        var y = SourceCoordinate(row, candidate.Cell.Height, Scale(candidate.Cell.Height));
        var y0 = (int)y;
        var y1 = Math.Min(y0 + 1, candidate.Cell.Height - 1);
        var first = (candidate.Cell.Top + y0) * imageWidth + candidate.Left;
        var second = (candidate.Cell.Top + y1) * imageWidth + candidate.Left;
        for (var i = 0; i < destination.Length; i++)
        {
            var x = SourceCoordinate(i, candidate.Width, destination.Length);
            var x0 = (int)x;
            var x1 = Math.Min(x0 + 1, candidate.Width - 1);
            var a = (1 + x0 - x) * grayscale[first + x0] + (x - x0) * grayscale[first + x1];
            var b = (1 + x0 - x) * grayscale[second + x0] + (x - x0) * grayscale[second + x1];
            destination[i] = (byte)Math.Round((1 + y0 - y) * a + (y - y0) * b);
        }
    }

    private static double SourceCoordinate(int pixel, int sourceLength, int targetLength)
        => Math.Clamp((pixel + 0.5) * sourceLength / targetLength - 0.5, 0, sourceLength - 1);

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        stream.Write(buffer);
        stream.Write(type);
        stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, ~PngSanitizer.UpdateCrc(PngSanitizer.UpdateCrc(uint.MaxValue, type), data));
        stream.Write(buffer);
    }
}
