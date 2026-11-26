using System.Text;

namespace Amane.Pdf.TextExtractor;

internal sealed record CompareText(Rune[] Text, int[] PageNumbers, int Pages)
{
    internal CompareSide Describe() => new(Pages, Text.Length);
}

internal sealed record CompareSide(int Pages, int Characters);
internal sealed record CompareExcerpt(int Page, string Text);
internal sealed record CompareChange(string Type, CompareExcerpt? Old, CompareExcerpt? New, string Before, string After);
internal sealed record CompareResult(CompareSide Old, CompareSide New, IReadOnlyList<CompareChange> Changes, bool Truncated);

internal sealed class CompareLimitException : Exception;

internal static class TextDiff
{
    private const int MaxDistance = 4096;
    private const int MaxWork = 32_000_000;
    private const int MaxChanges = 1000;
    private const int ContextLength = 30;
    private readonly record struct Edit(int Old, int New, int Deleted, int Inserted);
    private readonly record struct PageText(int Id, int Start, int End);

    internal static CompareResult Compare(CompareText oldText, CompareText newText)
    {
        var edits = FindDocumentEdits(oldText, newText);
        var changes = new List<CompareChange>();
        var index = 0;
        while (index < edits.Count && changes.Count < MaxChanges)
        {
            var first = edits[index];
            var oldEnd = first.Old;
            var newEnd = first.New;
            do
            {
                oldEnd += edits[index].Deleted;
                newEnd += edits[index].Inserted;
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

    private static List<Edit> FindDocumentEdits(CompareText oldText, CompareText newText)
    {
        var remainingWork = MaxWork;
        try
        {
            return FindEdits(oldText.Text, newText.Text, ref remainingWork);
        }
        catch (CompareLimitException) when (remainingWork > 0)
        {
            return FindPageEdits(oldText, newText, ref remainingWork);
        }
    }

    private static List<Edit> FindPageEdits(CompareText oldText, CompareText newText, ref int remainingWork)
    {
        // Intern exact page text so equality checks in the page alignment do not rescan long pages.
        ChargeWork(ref remainingWork, oldText.Text.Length + newText.Text.Length);
        var pageIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var oldPages = ReadPages(oldText, pageIds);
        var newPages = ReadPages(newText, pageIds);
        ChargeWork(ref remainingWork, oldPages.Count * newPages.Count);

        // Longest common subsequence of unchanged pages. Both dimensions are capped at 300 pages.
        var matches = new int[oldPages.Count + 1, newPages.Count + 1];
        for (var i = oldPages.Count - 1; i >= 0; i--)
        for (var j = newPages.Count - 1; j >= 0; j--)
            matches[i, j] = oldPages[i].Id == newPages[j].Id
                ? 1 + matches[i + 1, j + 1]
                : Math.Max(matches[i + 1, j], matches[i, j + 1]);
        if (matches[0, 0] == 0) throw new CompareLimitException();

        var edits = new List<Edit>();
        var oldStart = 0;
        var newStart = 0;
        for (int i = 0, j = 0; i < oldPages.Count && j < newPages.Count;)
        {
            var oldPage = oldPages[i];
            var newPage = newPages[j];
            if (oldPage.Id == newPage.Id)
            {
                AppendGapEdits(edits, oldText.Text, oldStart..oldPage.Start, newText.Text, newStart..newPage.Start,
                    ref remainingWork);
                oldStart = oldPage.End;
                newStart = newPage.End;
                i++;
                j++;
            }
            else if (matches[i + 1, j] >= matches[i, j + 1]) i++;
            else j++;
        }
        AppendGapEdits(edits, oldText.Text, oldStart..oldText.Text.Length, newText.Text, newStart..newText.Text.Length,
            ref remainingWork);
        return edits;
    }

    private static List<PageText> ReadPages(CompareText document, Dictionary<string, int> pageIds)
    {
        var pages = new List<PageText>(document.Pages);
        var start = 0;
        while (start < document.Text.Length)
        {
            var page = document.PageNumbers[start];
            var end = start + 1;
            while (end < document.Text.Length && document.PageNumbers[end] == page) end++;
            var text = Slice(document.Text, start, end);
            if (!pageIds.TryGetValue(text, out var id)) pageIds.Add(text, id = pageIds.Count);
            pages.Add(new(id, start, end));
            start = end;
        }
        return pages;
    }

    private static void AppendGapEdits(List<Edit> edits, Rune[] oldText, Range oldGap, Rune[] newText, Range newGap,
        ref int remainingWork)
    {
        var (oldStart, oldLength) = oldGap.GetOffsetAndLength(oldText.Length);
        var (newStart, newLength) = newGap.GetOffsetAndLength(newText.Length);
        if (oldLength == 0 || newLength == 0)
        {
            if (oldLength != newLength) edits.Add(new(oldStart, newStart, oldLength, newLength));
            return;
        }
        foreach (var edit in FindEdits(oldText.AsSpan(oldStart, oldLength), newText.AsSpan(newStart, newLength), ref remainingWork))
            edits.Add(edit with { Old = oldStart + edit.Old, New = newStart + edit.New });
    }

    private static void ChargeWork(ref int remainingWork, int amount = 1)
    {
        remainingWork -= amount;
        if (remainingWork < 0) throw new CompareLimitException();
    }

    private static List<Edit> FindEdits(ReadOnlySpan<Rune> oldText, ReadOnlySpan<Rune> newText, ref int remainingWork)
    {
        if (Math.Abs(oldText.Length - newText.Length) > MaxDistance) throw new CompareLimitException();

        // Myers' frontier. Row d stores only diagonals reachable in exactly d edits.
        // Keeping the rows allows backtracking and bounds the trace to about 32 MiB.
        var trace = new List<int[]>();
        for (var distance = 0; distance <= MaxDistance; distance++)
        {
            var frontier = new int[distance + 1];
            trace.Add(frontier);
            for (var step = 0; step <= distance; step++)
            {
                ChargeWork(ref remainingWork);
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
                    ChargeWork(ref remainingWork);
                    x++;
                    y++;
                }
                frontier[step] = x;
                if (x >= oldText.Length && y >= newText.Length)
                    return Backtrack(trace, oldText.Length, newText.Length);
            }
        }
        throw new CompareLimitException();
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
            edits.Add(insert ? new(previousX, previousY, 0, 1) : new(previousX, previousY, 1, 0));
            x = previousX;
            y = previousY;
        }
        edits.Reverse();
        return edits;
    }
}
