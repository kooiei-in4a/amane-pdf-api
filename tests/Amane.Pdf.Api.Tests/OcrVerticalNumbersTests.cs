using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class OcrVerticalNumbersTests
{
    [TestMethod]
    [DataRow("の", 35.0)]
    [DataRow("２", 95.0)]
    [DataRow("U", 95.0)]
    public void RecognizesOneCellAndKeepsAdjacentJapanese(string suspicious, double confidence)
    {
        using var job = new OverlayJob();
        var png = WritePage(job.Root, 128, 100, [30]);
        List<OcrTextLayer.Line> original = [new(20, 10, 24, 65,
            [new(20, 12, 24, 22, "が"), new(20, 30, 24, 18, suspicious, confidence), new(20, 54, 24, 20, "羽")])];
        var page = OcrVerticalNumbers.Find(png, 128, 100, original, true, default);
        Assert.IsNotNull(page);
        var final = page.Apply(original, BatchResult("１２"));
        var words = final.Single().Words;
        CollectionAssert.AreEqual(new[] { "が", "12", "羽" }, words.Select(word => word.Text).ToArray());
        Assert.AreEqual((16, 30, 32, 18), (words[1].Left, words[1].Top, words[1].Width, words[1].Height));
        Assert.AreSame(original[0].Words[0], words[0]);

        using var output = new MemoryStream();
        OcrTextLayer.Write(output, final, true, 128, 100, 300, default);
        var content = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(content, "0 -1 1 0 4.992 16.8 Tm");
        StringAssert.Contains(content, "<00310032>");
        Assert.DoesNotContain("<0020>", content);
    }

    [TestMethod]
    public void KeepsConfidentKanaEvenWhenItsBoxOverlapsTheNumber()
    {
        using var job = new OverlayJob();
        var png = WritePage(job.Root, 128, 100, [30], precedingInk: true);
        List<OcrTextLayer.Line> original = [new(20, 8, 24, 75,
            [new(20, 30, 24, 14, "が", 97), new(20, 39, 24, 17, "の", 35), new(20, 57, 24, 20, "羽", 80)])];
        var page = OcrVerticalNumbers.Find(png, 128, 100, original, true, default);
        Assert.IsNotNull(page);
        var words = page.Apply(original, BatchResult("12")).Single().Words;
        CollectionAssert.AreEqual(new[] { "が", "12", "羽" }, words.Select(word => word.Text).ToArray());
        Assert.AreEqual(10, words[0].Top);
        Assert.AreEqual(97, words[0].Confidence);
    }

    [TestMethod]
    public void SkipsConfidentJapaneseAndHorizontalRecognition()
    {
        using var job = new OverlayJob();
        var png = WritePage(job.Root, 128, 100, [30]);
        List<OcrTextLayer.Line> cjk = [new(20, 10, 24, 65, [new(20, 30, 24, 18, "字")])];
        List<OcrTextLayer.Line> horizontal = [new(20, 30, 68, 18, [new(20, 30, 24, 18, "U", 35)])];
        List<OcrTextLayer.Line> vertical = [cjk[0] with { Words = [new(20, 30, 24, 18, "2")] }];
        Assert.IsNull(OcrVerticalNumbers.Find(png, 128, 100, cjk, true, default));
        Assert.IsNull(OcrVerticalNumbers.Find(png, 128, 100, horizontal, true, default));
        Assert.IsNull(OcrVerticalNumbers.Find(png, 128, 100, vertical, false, default));
    }

    [TestMethod]
    [DataRow("1234", 95, false)]
    [DataRow("1A", 95, false)]
    [DataRow("", 95, false)]
    [DataRow("12", 30, false)]
    [DataRow("12", 95, true)]
    public void RejectsAmbiguousRecognition(string text, int confidence, bool multipleLines)
    {
        using var job = new OverlayJob();
        var png = WritePage(job.Root, 128, 100, [30]);
        List<OcrTextLayer.Line> original = [new(20, 10, 24, 65, [new(20, 30, 24, 18, "U", 35)])];
        var page = OcrVerticalNumbers.Find(png, 128, 100, original, true, default);
        Assert.IsNotNull(page);
        var recognized = text.Length == 0 ? [] : BatchResult(text, confidence);
        if (multipleLines) recognized.Add(BatchResult("34")[0]);
        Assert.AreSame(original, page.Apply(original, recognized));
    }

    [TestMethod]
    public void SkipsPagesOverTheCandidateCapAndUnexpectedPng()
    {
        using var job = new OverlayJob();
        const int height = 1200;
        var rows = Enumerable.Range(0, OcrVerticalNumbers.MaxCandidates + 1).Select(i => 30 + 32 * i).ToArray();
        var png = WritePage(job.Root, 128, height, rows);
        List<OcrTextLayer.Line> original = [new(20, 10, 24, height - 10,
            rows.Select(top => new OcrTextLayer.Word(20, top, 24, 18, "2")).ToList())];
        Assert.IsNull(OcrVerticalNumbers.Find(png, 128, height, original, true, default));
        original[0].Words.RemoveAt(rows.Length - 1);
        // Remove the last cell's ink too, so the boundary case has exactly 32 candidates.
        png = WritePage(job.Root, 128, height, rows[..^1]);
        Assert.IsNotNull(OcrVerticalNumbers.Find(png, 128, height, original, true, default));
        png = WritePage(job.Root, 128, height, rows[..^1], interlaced: true);
        Assert.IsNull(OcrVerticalNumbers.Find(png, 128, height, original, true, default));
    }

    private static List<OcrTextLayer.Line> BatchResult(string text, double confidence = 95)
        => [new(42, 40, 20, 18, [new(42, 40, 20, 18, text, confidence)])];

    private static string WritePage(string directory, int width, int height, int[] tops, bool interlaced = false, bool precedingInk = false)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        header[12] = interlaced ? (byte)1 : (byte)0;
        using var encoded = new MemoryStream();
        using (var zlib = new ZLibStream(encoded, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 3 + 1];
            for (var y = 0; y < height; y++)
            {
                row.AsSpan(1).Fill(255);
                if (precedingInk && y is >= 10 and < 24) row.AsSpan(1 + 22 * 3, 18 * 3).Clear();
                if (tops.Any(top => y >= top && y < top + 18))
                {
                    row.AsSpan(1 + 22 * 3, 5 * 3).Clear();
                    row.AsSpan(1 + 31 * 3, 9 * 3).Clear();
                }
                zlib.Write(row);
            }
        }
        var path = Path.Combine(directory, "numbers.png");
        File.WriteAllBytes(path, [137, 80, 78, 71, 13, 10, 26, 10,
            .. FromImagesFixtures.Chunk("IHDR", header), .. FromImagesFixtures.Chunk("IDAT", encoded.ToArray()),
            .. FromImagesFixtures.Chunk("IEND", [])]);
        return path;
    }
}
