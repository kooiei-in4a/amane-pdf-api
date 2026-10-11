using System.Buffers.Binary;
using System.Text;

namespace Amane.Pdf.Api;

internal static class PngSanitizer
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = MakeCrcTable();

    internal static async Task<ImageInfo> WriteAsync(string inputPath, string outputPath, long budget,
        long pixelLimit, long workingLimit, CancellationToken token)
    {
        try { return await WriteCoreAsync(inputPath, outputPath, budget, pixelLimit, workingLimit, token); }
        catch (EndOfStreamException) { throw PdfImageInputException.Unsupported(); }
    }

    private static async Task<ImageInfo> WriteCoreAsync(string inputPath, string outputPath, long budget,
        long pixelLimit, long workingLimit, CancellationToken token)
    {
        await using var input = File.OpenRead(inputPath);
        await using var output = TemporaryPdfFiles.CreatePrivateFile(outputPath);
        var signature = new byte[8];
        await input.ReadExactlyAsync(signature, token);
        if (!signature.AsSpan().SequenceEqual(Signature)) throw PdfImageInputException.Unsupported();
        long written = 0;
        async ValueTask Write(ReadOnlyMemory<byte> bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.Length > budget - written) throw PdfImageInputException.Capacity();
            await output.WriteAsync(bytes, token);
            written += bytes.Length;
        }
        await Write(signature);
        var header = new byte[8]; var trailer = new byte[4]; var buffer = new byte[64 * 1024];
        var width = 0; var height = 0; var depth = 0; var color = 0; var palette = 0;
        var seenHeader = false; var seenPalette = false; var seenTransparency = false;
        var seenData = false; var endedData = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await input.ReadExactlyAsync(header, token);
            var unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (unsignedLength > int.MaxValue || unsignedLength > input.Length - input.Position - 4)
                throw PdfImageInputException.Unsupported();
            var length = (int)unsignedLength;
            for (var i = 4; i < 8; i++)
                if (header[i] is not (>= 65 and <= 90) and not (>= 97 and <= 122))
                    throw PdfImageInputException.Unsupported();
            if ((header[6] & 32) != 0) throw PdfImageInputException.Unsupported();
            var kind = Encoding.ASCII.GetString(header, 4, 4);
            if (!seenHeader && kind != "IHDR") throw PdfImageInputException.Unsupported();
            var keep = kind is "IHDR" or "PLTE" or "tRNS" or "IDAT" or "IEND";
            if ((!keep && (header[4] & 32) == 0) || kind is "acTL" or "fcTL" or "fdAT")
                throw PdfImageInputException.Unsupported();
            if (seenData && kind != "IDAT") endedData = true;
            byte[]? small = null;
            switch (kind)
            {
                case "IHDR":
                    if (seenHeader || length != 13) throw PdfImageInputException.Unsupported();
                    small = new byte[13]; break;
                case "PLTE":
                    if (seenPalette || seenTransparency || seenData || color is 0 or 4 || length is < 3 or > 768 || length % 3 != 0 ||
                        (color == 3 && length / 3 > 1 << depth)) throw PdfImageInputException.Unsupported();
                    small = new byte[length]; break;
                case "tRNS":
                    if (seenTransparency || seenData || color is 4 or 6 ||
                        (color == 0 && length != 2) || (color == 2 && length != 6) ||
                        (color == 3 && (!seenPalette || length < 1 || length > palette)))
                        throw PdfImageInputException.Unsupported();
                    small = new byte[length]; break;
                case "IDAT":
                    if (endedData || (color == 3 && !seenPalette)) throw PdfImageInputException.Unsupported();
                    break;
                case "IEND":
                    if (!seenData || length != 0) throw PdfImageInputException.Unsupported();
                    break;
            }
            if (keep) await Write(header);
            var crc = UpdateCrc(uint.MaxValue, header.AsSpan(4, 4));
            var copied = 0;
            while (copied < length)
            {
                var count = Math.Min(buffer.Length, length - copied);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                crc = UpdateCrc(crc, buffer.AsSpan(0, count));
                if (small is not null) buffer.AsSpan(0, count).CopyTo(small.AsSpan(copied));
                if (keep) await Write(buffer.AsMemory(0, count));
                copied += count;
            }
            await input.ReadExactlyAsync(trailer, token);
            if (BinaryPrimitives.ReadUInt32BigEndian(trailer) != (crc ^ uint.MaxValue))
                throw PdfImageInputException.Unsupported();
            if (keep) await Write(trailer);
            if (kind == "IHDR")
            {
                var w = BinaryPrimitives.ReadUInt32BigEndian(small!);
                var h = BinaryPrimitives.ReadUInt32BigEndian(small!.AsSpan(4));
                if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue || checked((long)w * h) > pixelLimit)
                    throw PdfImageInputException.Unsupported();
                width = (int)w; height = (int)h; depth = small![8]; color = small[9];
                var validDepth = color switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => depth is 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!validDepth || small[10] != 0 || small[11] != 0 || small[12] > 1)
                    throw PdfImageInputException.Unsupported();
                seenHeader = true;
            }
            if (kind == "PLTE") { seenPalette = true; palette = length / 3; }
            if (kind == "tRNS")
            {
                seenTransparency = true;
                if (color is 0 or 2)
                    for (var i = 0; i < length; i += 2)
                        if (BinaryPrimitives.ReadUInt16BigEndian(small!.AsSpan(i)) > (1L << depth) - 1)
                            throw PdfImageInputException.Unsupported();
            }
            if (kind == "IDAT") seenData = true;
            if (kind != "IEND") continue;
            if (WorkingBytes(width, height, depth, written) > workingLimit) throw PdfImageInputException.Unsupported();
            return new(width, height, color is 0 or 4 ? 1 : 3, Png: true, BitDepth: depth);
        }
    }

    internal static long WorkingBytes(int width, int height, int depth, long encoded) =>
        checked(2 * encoded + (long)width * height * (depth == 16 ? 24 : 12) + 64 * 1048576);

    internal static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }
    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var j = 0; j < 8; j++) value = (value & 1) == 0 ? value >> 1 : 0xedb88320U ^ (value >> 1);
            table[i] = value;
        }
        return table;
    }
}
