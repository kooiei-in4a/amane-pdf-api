using System.Buffers.Binary;
using System.IO.Compression;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class OcrPageImageTests
{
    [TestMethod]
    [DataRow(0, false, false, false, "Sauvola")]
    [DataRow(0, false, true, false, "Sauvola")]
    [DataRow(0, true, false, false, "Sauvola")]
    [DataRow(0, true, true, false, "AdaptiveOtsu")]
    [DataRow(89, true, true, false, "Sauvola")]
    [DataRow(0, true, true, true, "Sauvola")]
    public void OcrSelectsBinarizationFromTheRenderedPng(int gradient, bool fineStrokes, bool ruled, bool noise, string expected)
    {
        const int width = 128;
        const int height = 96;
        using var files = new OverlayJob();
        var path = Path.Combine(files.Root, "page.png");
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;

        using var encoded = new MemoryStream();
        using (var zlib = new ZLibStream(encoded, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 3 + 1];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var noiseOffset = noise && (x + y) % 2 == 0 ? 8 : 0;
                    var shade = (byte)(255 - x * gradient / (width - 1) - noiseOffset);
                    var dot = x % 16 is 8 or 9 && y % 16 is 8 or 9 && (x / 16 + y / 16) % 2 == 0;
                    var stroke = x % 16 < 6 && y % 16 < 6;
                    var rule = ruled && (y is >= 19 and <= 22 or >= 72 and <= 75);
                    if ((fineStrokes ? dot : stroke) || rule) shade = 0;
                    row.AsSpan(1 + x * 3, 3).Fill(shade);
                }
                zlib.Write(row);
            }
        }
        var pixels = encoded.ToArray();
        File.WriteAllBytes(path,
        [
            137, 80, 78, 71, 13, 10, 26, 10,
            .. FromImagesFixtures.Chunk("IHDR", header),
            .. FromImagesFixtures.Chunk("IDAT", pixels[..1]),
            .. FromImagesFixtures.Chunk("IDAT", pixels[1..]),
            .. FromImagesFixtures.Chunk("IEND", [])
        ]);

        Assert.AreEqual(expected, OcrPageImage.SelectBinarization(path, width, height, CancellationToken.None).ToString());
    }
}
