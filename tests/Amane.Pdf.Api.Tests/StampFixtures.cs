using System.Buffers.Binary;
using System.IO.Compression;

namespace Amane.Pdf.Api.Tests;

internal static class StampFixtures
{
    internal static byte[] Png(int width = 32, int height = 24, int depth = 8, int color = 6,
        bool interlaced = false, string? fault = null, bool expensive = false, bool transparency = false)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = (byte)depth; header[9] = (byte)color; header[12] = interlaced ? (byte)1 : (byte)0;
        var channels = color switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        using var encoded = new MemoryStream();
        using (var zlib = new ZLibStream(encoded, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            (int X, int Y, int Dx, int Dy)[] passes = interlaced
                ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
                : [(0, 0, 1, 1)];
            foreach (var (x, y, dx, dy) in passes)
            {
                var w = width > x ? (width - x + dx - 1) / dx : 0;
                var h = height > y ? (height - y + dy - 1) / dy : 0;
                if (w == 0 || h == 0) continue;
                var row = new byte[1 + (w * channels * depth + 7) / 8];
                if (depth >= 8 && color != 3) Array.Fill(row, (byte)96, 1, row.Length - 1);
                if (expensive)
                {
                    new Random(29).NextBytes(row.AsSpan(1));
                    for (var i = 4; i < row.Length; i += 4) row[i] = 255;
                }
                for (var line = 0; line < h; line++)
                {
                    row[0] = fault == "filter" ? (byte)7 : (byte)0;
                    if (expensive && line > 0)
                    {
                        row[0] = 2;
                        for (var i = 1; i < row.Length; i++) row[i] = i % 4 == 0 ? (byte)0 : (byte)1;
                    }
                    if (fault != "short" || line < h / 2) zlib.Write(row);
                }
            }
            if (fault == "long") zlib.WriteByte(0);
        }
        var payload = encoded.ToArray();
        if (fault == "adler") payload[^1] ^= 1;
        if (fault == "truncated") payload = payload[..^2];
        if (fault == "zlib") payload[2] = 255;
        if (fault == "dictionary") { payload[1] = 32; payload[1] += (byte)((31 - (payload[0] * 256 + payload[1]) % 31) % 31); }
        var transparent = depth < 8 ? new byte[] { 0, 0 } : depth == 16 ? [96, 96] : [0, 96];
        // Split the zlib header and trailer across IDAT chunks to exercise concatenation.
        return [137, 80, 78, 71, 13, 10, 26, 10, .. FromImagesFixtures.Chunk("IHDR", header),
            .. (color == 3 ? FromImagesFixtures.Chunk("PLTE", [255, 0, 0]) : []),
            .. (color == 3 ? FromImagesFixtures.Chunk("tRNS", [128]) : []),
            .. (transparency && color is 0 or 2 ? FromImagesFixtures.Chunk("tRNS", color == 0 ? transparent : [.. transparent, .. transparent, .. transparent]) : []),
            .. FromImagesFixtures.Chunk("tEXt", "Author\0PRIVATE_STAMP_METADATA"u8.ToArray()),
            .. FromImagesFixtures.Chunk("IDAT", payload[..1]),
            .. FromImagesFixtures.Chunk("IDAT", []),
            .. FromImagesFixtures.Chunk("IDAT", payload[1..^1]),
            .. FromImagesFixtures.Chunk("IDAT", payload[^1..]), .. FromImagesFixtures.Chunk("IEND", []),
            .. "PRIVATE_STAMP_TRAILING"u8];
    }
}
