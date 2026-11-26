using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class OcrTextLayerTests
{
    private const string Header = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext";
    private const string Secret = "秘匿語句";
    private const int Dpi = 300;
    private const double K = 72.0 / Dpi;
    private static readonly Regex Shown = new(
        @"^(?<m>[-\d. ]+) Tm /F1 (?<fs>[\d.]+) Tf (?<tz>[\d.]+) Tz \[ <(?<hex>[0-9a-f]+)> \] TJ$", RegexOptions.Multiline);

    [TestMethod]
    public void ReadKeepsTesseractLinesAndWords()
    {
        using var file = new TsvFile(Header,
            "1\t1\t0\t0\t0\t0\t0\t0\t1000\t1000\t-1\t",
            "4\t1\t1\t1\t1\t0\t10\t20\t300\t50\t-1\t",
            "5\t1\t1\t1\t1\t1\t10\t20\t100\t50\t91.5\t開館",
            "5\t1\t1\t1\t1\t2\t120\t20\t100\t50\t80\t時 間\u0007",
            "5\t1\t1\t1\t1\t3\t230\t20\t10\t50\t95\t ",
            "4\t1\t1\t1\t2\t0\t10\t80\t200\t40\t-1\t",
            "5\t1\t1\t1\t2\t1\t10\t80\t200\t40\t70\tDark");
        var lines = OcrTextLayer.Read(file.Path, 1000, 1000, default);
        Assert.HasCount(2, lines);
        Assert.AreEqual(new(10, 20, 300, 50, lines[0].Words), lines[0]);
        CollectionAssert.AreEqual(new[] { "開館", "時間" }, lines[0].Words.Select(word => word.Text).ToArray());
        Assert.AreEqual("Dark", lines[1].Words.Single().Text);
    }

    [TestMethod]
    [DataRow("header")]
    [DataRow("columns")]
    [DataRow("outside")]
    [DataRow("negative")]
    [DataRow("number")]
    [DataRow("confidence")]
    [DataRow("orphan")]
    [DataRow("encoding")]
    public void ReadRejectsMalformedTsvWithoutRevealingText(string defect)
    {
        string[] rows = defect switch
        {
            "header" => [Header.Replace("conf", "confidence"), Line(0), Word(0, Secret)],
            "columns" => [Header, Line(0), "5\t1\t1\t1\t1\t1\t10\t20\t100\t" + Secret],
            "outside" => [Header, Line(0), "5\t1\t1\t1\t1\t1\t950\t20\t100\t50\t90\t" + Secret],
            "negative" => [Header, Line(0), "5\t1\t1\t1\t1\t1\t-1\t20\t100\t50\t90\t" + Secret],
            "number" => [Header, Line(0), "5\t1\t1\t1\t1\t1\tx\t20\t100\t50\t90\t" + Secret],
            "confidence" => [Header, Line(0), "5\t1\t1\t1\t1\t1\t10\t20\t100\t50\tNaN\t" + Secret],
            "orphan" => [Header, Line(0), "5\t1\t1\t1\t2\t1\t10\t20\t100\t50\t90\t" + Secret],
            _ => [Header, Line(0)],
        };
        using var file = new TsvFile(rows);
        if (defect == "encoding") File.AppendAllBytes(file.Path, [.. "5\t1\t1\t1\t1\t1\t10\t20\t100\t50\t90\t"u8, 0xff, 0xfe]);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => OcrTextLayer.Read(file.Path, 1000, 1000, default));
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [TestMethod]
    public void ReadLimitsWordsAndWordLength()
    {
        using (var longWord = new TsvFile(Header, Line(0), Word(0, new string('語', OcrTextLayer.MaxWordLength + 1))))
            Assert.AreEqual(PdfRenderReason.OutputTooLarge, Assert.ThrowsExactly<PdfRenderException>(
                () => OcrTextLayer.Read(longWord.Path, 1000, 1000, default)).Reason);
        using (var maximum = new TsvFile(Header, Line(0), Word(0, new string('語', OcrTextLayer.MaxWordLength))))
            Assert.HasCount(1, OcrTextLayer.Read(maximum.Path, 1000, 1000, default).Single().Words);

        var rows = new List<string> { Header, Line(0) };
        rows.AddRange(Enumerable.Repeat(Word(0, "語"), OcrTextLayer.MaxWords));
        using (var full = new TsvFile([.. rows]))
            Assert.HasCount(OcrTextLayer.MaxWords, OcrTextLayer.Read(full.Path, 1000, 1000, default).Single().Words);
        rows.Add(Word(0, "語"));
        using (var tooMany = new TsvFile([.. rows]))
            Assert.AreEqual(PdfRenderReason.OutputTooLarge, Assert.ThrowsExactly<PdfRenderException>(
                () => OcrTextLayer.Read(tooMany.Path, 1000, 1000, default)).Reason);
    }

    [TestMethod]
    public void ReadAndWriteObserveCancellation()
    {
        var rows = new List<string> { Header, Line(0) };
        rows.AddRange(Enumerable.Repeat(Word(0, "語"), 300));
        using var file = new TsvFile([.. rows]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => OcrTextLayer.Read(file.Path, 1000, 1000, cancelled.Token));
        var lines = OcrTextLayer.Read(file.Path, 1000, 1000, default);
        Assert.ThrowsExactly<OperationCanceledException>(
            () => OcrTextLayer.Write(Stream.Null, lines, false, 1000, 1000, Dpi, cancelled.Token));
    }

    [TestMethod]
    [DataRow("開館", "時間", 0.5, true)]
    [DataRow("開館", "時間", 1.19, true)]
    [DataRow("開館", "時間", 1.2, false)]
    [DataRow("PDF", "を", 0.5, true)]
    [DataRow("第3", "章", 0.59, true)]
    [DataRow("は", "Windows", 1.1, true)]
    [DataRow("Windows", "11", 0.3, false)]
    [DataRow("Dark", "Mode", 0.05, false)]
    [DataRow("12,", "000", 0.59, true)]
    [DataRow("12,", "000", 0.6, false)]
    [DataRow("1,", "250円", 0.3, false)]
    [DataRow("12,", "0001", 0.3, false)]
    [DataRow("118,", "000,000", 0.3, true)]
    [DataRow("2.", "5", 0.3, true)]
    [DataRow("A.", "5", 0.3, false)]
    [DataRow("𠮷", "野", 0.5, true)]
    public void JoinsJapaneseAndDigitGroupsButKeepsLatinWordsApart(string previous, string next, double gap, bool joins)
        => Assert.AreEqual(joins, OcrTextLayer.Joins(previous, next, gap * 100, 100));

    [TestMethod]
    public void WritePlacesJoinedJapaneseWithoutSpacesAndCentresTheLine()
    {
        // 開館 | 時間 | は with ink gaps; the last word ends the line without punctuation.
        var line = new OcrTextLayer.Line(100, 200, 330, 50, [
            new(100, 200, 100, 50, "開館"), new(215, 200, 100, 50, "時間"), new(330, 200, 50, 50, "は")]);
        var (text, shows) = Write([line], vertical: false, imageHeight: 1000);
        Assert.DoesNotContain("<0020>", text);
        Assert.AreEqual("開館時間は", string.Concat(shows.Select(show => show.Text)));
        var fontSize = 50 * K * OcrTextLayer.FontSizeRatio;
        foreach (var show in shows)
        {
            Assert.AreEqual(fontSize, show.FontSize, 0.001);
            Assert.AreEqual(1000 * K - (200 + OcrTextLayer.BaselineRatio * 50) * K, show.Matrix[5], 0.001);
            CollectionAssert.AreEqual(new double[] { 1, 0, 0, 1 }, show.Matrix[..4]);
        }
        // Each joined word advances exactly to the next word's left edge; the last keeps its own width.
        Assert.AreEqual(shows[1].Matrix[4], shows[0].Matrix[4] + shows[0].Advance, 0.002);
        Assert.AreEqual(shows[2].Matrix[4], shows[1].Matrix[4] + shows[1].Advance, 0.002);
        Assert.AreEqual(50 * K, shows[2].Advance, 0.002);
    }

    [TestMethod]
    public void WriteSeparatesLatinWordsWithASpaceAndAnItemBreakingGap()
    {
        var line = new OcrTextLayer.Line(100, 200, 400, 50, [
            new(100, 200, 180, 50, "Dark"), new(290, 200, 210, 50, "Mode")]);
        var (text, shows) = Write([line], vertical: false, imageHeight: 1000);
        StringAssert.Contains(text, "100 Tz [ <0020> ] TJ");
        var fontSize = 50 * K * OcrTextLayer.FontSizeRatio;
        var gap = shows[1].Matrix[4] - (shows[0].Matrix[4] + shows[0].Advance);
        Assert.IsGreaterThanOrEqualTo(OcrTextLayer.LatinItemGap * fontSize - 0.002, gap);
        Assert.AreEqual(180 * K - (OcrTextLayer.LatinItemGap * fontSize - 10 * K), shows[0].Advance, 0.002);
    }

    [TestMethod]
    public void WriteStretchesOnlyLinesEndingInPunctuationToTheLinePitch()
    {
        // Character pitch 55 px; the final 。 has a 20 px ink box, the final 行 a 20 px one too.
        OcrTextLayer.Line Line(int top, string last) => new(0, top, 240, 50, [
            new(0, top, 100, 50, "中央"), new(110, top, 100, 50, "館の"), new(220, top, 20, 50, last)]);
        var (_, shows) = Write([Line(100, "。"), Line(300, "行")], vertical: false, imageHeight: 1000);
        Assert.AreEqual(55 * K, shows[2].Advance, 0.002);
        Assert.AreEqual(20 * K, shows[5].Advance, 0.002);
    }

    [TestMethod]
    public void WritePlacesTallLinesVerticallyOnlyInVerticalRequests()
    {
        var column = new OcrTextLayer.Line(500, 100, 50, 300, [
            new(500, 100, 50, 140, "返却"), new(500, 250, 50, 150, "ポスト")]);
        var heading = new OcrTextLayer.Line(100, 20, 300, 40, [new(100, 20, 300, 40, "見出し")]);
        var (text, shows) = Write([column, heading], vertical: true, imageHeight: 1000);
        Assert.DoesNotContain("<0020>", text);
        var fontSize = 50 * K * OcrTextLayer.FontSizeRatio;
        CollectionAssert.AreEqual(new double[] { 0, -1, 1, 0 }, shows[0].Matrix[..4]);
        Assert.AreEqual((500 + (1 - OcrTextLayer.BaselineRatio) * 50) * K, shows[0].Matrix[4], 0.001);
        Assert.AreEqual(1000 * K - 100 * K, shows[0].Matrix[5], 0.001);
        Assert.AreEqual(150 * K, shows[0].Advance, 0.002);
        Assert.AreEqual(fontSize, shows[0].FontSize, 0.001);
        CollectionAssert.AreEqual(new double[] { 1, 0, 0, 1 }, shows[2].Matrix[..4]);

        var (_, horizontal) = Write([column], vertical: false, imageHeight: 1000);
        CollectionAssert.AreEqual(new double[] { 1, 0, 0, 1 }, horizontal[0].Matrix[..4]);
    }

    [TestMethod]
    public void WriteTrimsOverlappingBoxesAndSkipsEmptyLines()
    {
        var line = new OcrTextLayer.Line(0, 0, 400, 50, [
            new(0, 0, 300, 50, "末年"), new(100, 0, 50, 50, "始")]);
        var empty = new OcrTextLayer.Line(0, 100, 0, 0, []);
        var (_, shows) = Write([empty, line], vertical: false, imageHeight: 1000);
        Assert.HasCount(2, shows);
        Assert.AreEqual(100 * K, shows[0].Advance, 0.002);
    }

    [TestMethod]
    [DataRow(0, 170, 500, 50, 1, false, false, DisplayName = "next line of a wrapped paragraph")]
    [DataRow(0, 226, 500, 50, 1, false, true, DisplayName = "gap above 1.5 line heights")]
    [DataRow(700, 170, 500, 50, 1, false, true, DisplayName = "no horizontal overlap")]
    [DataRow(0, 170, 500, 50, 2, false, true, DisplayName = "another Tesseract block")]
    [DataRow(0, 20, 500, 50, 1, false, true, DisplayName = "next line above")]
    [DataRow(0, 170, 50, 400, 1, true, true, DisplayName = "orientation change on a vertical page")]
    public void EndsFieldSeparatesIndependentLinesOnly(int left, int top, int width, int height, int block,
        bool vertical, bool ends)
    {
        // A horizontal line 600x50 at (0, 100), so its bottom edge is at 150.
        var line = new OcrTextLayer.Line(0, 100, 600, 50, [new(0, 100, 50, 50, "開館")]);
        var next = new OcrTextLayer.Line(left, top, width, height, [new(left, top, 50, 50, "時間")], block);
        Assert.AreEqual(ends, OcrTextLayer.EndsField(line, next, vertical));
    }

    [TestMethod]
    public void EndsFieldFollowsColumnsRightToLeftOnVerticalPages()
    {
        var column = new OcrTextLayer.Line(500, 100, 50, 400, [new(500, 100, 50, 100, "返却")]);
        Assert.IsFalse(OcrTextLayer.EndsField(column, column with { Left = 430 }, true));
        Assert.IsTrue(OcrTextLayer.EndsField(column, column with { Left = 300 }, true));
        Assert.IsTrue(OcrTextLayer.EndsField(column, column with { Left = 570 }, true));
        Assert.IsTrue(OcrTextLayer.EndsField(column, column with { Left = 430, Top = 600 }, true));
    }

    [TestMethod]
    public void WriteMarksTheEndOfJapaneseFieldsForPdfJsSearch()
    {
        var boundary = $"100 Tz [ <{(int)OcrTextLayer.FieldBoundary:x4}> ] TJ";
        OcrTextLayer.Line Line(int left, int top, string text, int block = 1)
            => new(left, top, 300, 50, [new(left, top, 300, 50, text)], block);

        // Far apart: 開館時間 ... 返却ポスト ... Dark Mode. Only lines ending in CJK get the marker.
        var (separate, _) = Write([Line(100, 100, "開館時間"), Line(1300, 1600, "返却ポスト"), Line(500, 2400, "Mode")],
            vertical: false, imageHeight: 3500);
        Assert.AreEqual(2, separate.Split(boundary).Length - 1);
        StringAssert.Contains(separate, Convert.ToHexStringLower(Encoding.BigEndianUnicode.GetBytes("返却ポスト")) + "> ] TJ\n" + boundary);

        // A wrapped paragraph keeps its line break removable, so 開館時|間 still matches 開館時間.
        var (wrapped, _) = Write([Line(100, 100, "中央図書館の開館時"), Line(100, 170, "間は午前九時から")],
            vertical: false, imageHeight: 3500);
        Assert.DoesNotContain(boundary, wrapped);

        // A line ending in Latin text already becomes a space in PDF.js search.
        var (latin, _) = Write([Line(100, 100, "Dark"), Line(1300, 1600, "返却")], vertical: false, imageHeight: 3500);
        Assert.DoesNotContain(boundary, latin);
    }

    private static (string Text, Show[] Shows) Write(IReadOnlyList<OcrTextLayer.Line> lines, bool vertical, int imageHeight)
    {
        using var stream = new MemoryStream();
        OcrTextLayer.Write(stream, lines, vertical, 1000, imageHeight, Dpi, default);
        var text = Encoding.ASCII.GetString(stream.ToArray());
        StringAssert.StartsWith(text, "BT\n3 Tr\n");
        StringAssert.EndsWith(text, "ET\n");
        var shows = Shown.Matches(text).Select(match =>
        {
            var matrix = match.Groups["m"].Value.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var fontSize = double.Parse(match.Groups["fs"].Value, CultureInfo.InvariantCulture);
            var scale = double.Parse(match.Groups["tz"].Value, CultureInfo.InvariantCulture);
            var word = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(match.Groups["hex"].Value));
            return new Show(matrix, fontSize, word, scale / 100 * word.Length * 0.5 * fontSize);
        }).ToArray();
        return (text, shows);
    }

    private static string Line(int index) => $"4\t1\t1\t1\t{index + 1}\t0\t10\t20\t300\t50\t-1\t";
    private static string Word(int index, string text) => $"5\t1\t1\t1\t{index + 1}\t1\t10\t20\t100\t50\t90\t{text}";

    private sealed record Show(double[] Matrix, double FontSize, string Text, double Advance);

    private sealed class TsvFile : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ocr-{Guid.NewGuid():N}.tsv");
        internal TsvFile(params string[] rows) => File.WriteAllText(Path, string.Join('\n', rows) + "\n", new UTF8Encoding(false));
        public void Dispose() => File.Delete(Path);
    }
}
