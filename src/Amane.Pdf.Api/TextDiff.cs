using System.Text;

namespace Amane.Pdf.Api;

internal sealed record CompareText(Rune[] Text, int[] PageNumbers, int Pages)
{
    internal CompareSide Describe() => new(Pages, Text.Length);
}

internal sealed record CompareSide(int Pages, int Characters);
internal sealed record CompareExcerpt(int Page, string Text);
internal sealed record CompareChange(string Type, CompareExcerpt? Old, CompareExcerpt? New, string Before, string After);
internal sealed record CompareResult(CompareSide Old, CompareSide New, IReadOnlyList<CompareChange> Changes, bool Truncated);

internal static class TextDiff
{
    private const int MaxDistance = 4096;
    private const int MaxWork = 32_000_000;
    private const int MaxChanges = 1000;
    private const int ContextLength = 30;
    private readonly record struct Edit(int Old, int New, bool Insert);

    internal static CompareResult Compare(CompareText oldText, CompareText newText, CancellationToken token)
    {
        var edits = FindEdits(oldText.Text, newText.Text, token);
        var changes = new List<CompareChange>();
        var index = 0;
        while (index < edits.Count && changes.Count < MaxChanges)
        {
            var first = edits[index];
            var oldEnd = first.Old;
            var newEnd = first.New;
            do
            {
                if (edits[index].Insert) newEnd++;
                else oldEnd++;
                index++;
            }
            while (index < edits.Count && edits[index].Old == oldEnd && edits[index].New == newEnd);

            var beforeStart = Math.Max(0, first.Old - ContextLength);
            var before = Slice(oldText.Text, beforeStart, first.Old);
            var after = Slice(oldText.Text, oldEnd, Math.Min(oldText.Text.Length, oldEnd + ContextLength));
            var oldExcerpt = Excerpt(oldText, first.Old, oldEnd);
            var newExcerpt = Excerpt(newText, first.New, newEnd);
            changes.Add(new(oldExcerpt is null ? "insert" : newExcerpt is null ? "delete" : "replace",
                oldExcerpt, newExcerpt, before, after));
        }
        return new(oldText.Describe(), newText.Describe(), changes, index < edits.Count);
    }

    private static CompareExcerpt? Excerpt(CompareText side, int from, int to)
        => from == to ? null : new(side.PageNumbers[from], Slice(side.Text, from, to));

    private static string Slice(Rune[] text, int from, int to)
    {
        var result = new StringBuilder(to - from);
        Span<char> encoded = stackalloc char[2];
        for (var i = from; i < to; i++) result.Append(encoded[..text[i].EncodeToUtf16(encoded)]);
        return result.ToString();
    }

    private static List<Edit> FindEdits(Rune[] oldText, Rune[] newText, CancellationToken token)
    {
        if (Math.Abs(oldText.Length - newText.Length) > MaxDistance) throw TooLarge();

        // Myers' frontier. Row d stores only diagonals reachable in exactly d edits.
        // Keeping the rows allows backtracking and bounds the trace to about 32 MiB.
        var trace = new List<int[]>();
        var work = 0;
        for (var distance = 0; distance <= MaxDistance; distance++)
        {
            token.ThrowIfCancellationRequested();
            var frontier = new int[distance + 1];
            trace.Add(frontier);
            for (var step = 0; step <= distance; step++)
            {
                CheckWork();
                var diagonal = 2 * step - distance;
                var x = 0;
                if (distance > 0)
                {
                    var previous = trace[distance - 1];
                    x = UseInsertion(previous, step) ? previous[step] : previous[step - 1] + 1;
                }
                var y = x - diagonal;
                while (x < oldText.Length && y < newText.Length && oldText[x] == newText[y])
                {
                    CheckWork();
                    x++;
                    y++;
                }
                frontier[step] = x;
                if (x >= oldText.Length && y >= newText.Length)
                    return Backtrack(trace, oldText.Length, newText.Length);
            }
        }
        throw TooLarge();

        void CheckWork()
        {
            if (++work > MaxWork) throw TooLarge();
            if ((work & 4095) == 0) token.ThrowIfCancellationRequested();
        }
    }

    private static bool UseInsertion(int[] previous, int step)
        => step == 0 || (step < previous.Length && previous[step - 1] < previous[step]);

    private static List<Edit> Backtrack(List<int[]> trace, int x, int y)
    {
        var edits = new List<Edit>(trace.Count - 1);
        for (var distance = trace.Count - 1; distance > 0; distance--)
        {
            var previous = trace[distance - 1];
            var diagonal = x - y;
            var step = (diagonal + distance) / 2;
            var insert = UseInsertion(previous, step);
            var previousX = previous[insert ? step : step - 1];
            var previousY = previousX - (insert ? diagonal + 1 : diagonal - 1);
            edits.Add(new(previousX, previousY, insert));
            x = previousX;
            y = previousY;
        }
        edits.Reverse();
        return edits;
    }

    private static PdfCompareException TooLarge() => new(PdfCompareReason.TooLarge);
}
