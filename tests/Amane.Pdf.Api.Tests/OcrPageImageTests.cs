using System.Buffers.Binary;
using System.IO.Compression;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class OcrPageImageTests
{
    [TestMethod]
    [DataRow(0, false, "Otsu")]
    [DataRow(16, false, "Otsu")]
    [DataRow(89, false, "Sauvola")]
    [DataRow(0, true, "AdaptiveOtsu")]
    [DataRow(89, true, "Sauvola")]
    public void OcrSelectsBinarizationFromTheRenderedPng(int gradient, bool fineStrokes, string expected)
    {
        const int width = 96;
        const int height = 64;
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
                    var shade = (byte)(255 - x * gradient / (width - 1));
                    var ink = fineStrokes ? x % 16 is 8 or 9 && y % 16 is 8 or 9 :
                        y % 16 < 4 || x < 4; // Rules and a dark left border.
                    if (ink) shade = 0;
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
