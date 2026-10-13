using System.Buffers.Binary;
using System.IO.Compression;

namespace Amane.Pdf.Api;

// Only sanitized PNGs with stamp's byte and side limits enter this validator.
// Decompressed pixels are never retained: one scanline suffices for every Adam7 pass.
internal static class PngDataValidator
{
    internal static async Task ValidateAsync(string path, CancellationToken token)
    {
        try { await ValidateCoreAsync(path, token); }
        catch (InvalidDataException) { throw PdfStampImageException.Unsupported(); }
        catch (EndOfStreamException) { throw PdfStampImageException.Unsupported(); }
    }

    private static async Task ValidateCoreAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        if (file.Length > StampImageInput.MaxBytes) throw new InvalidDataException();
        var ihdr = new byte[33];
        await file.ReadExactlyAsync(ihdr, token);
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(16));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(20));
        if (width is < 1 or > StampImageInput.MaxSide || height is < 1 or > StampImageInput.MaxSide)
            throw new InvalidDataException();
        var depth = ihdr[24];
        var channels = ihdr[25] switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException() };
        using var idat = new MemoryStream();
        var header = new byte[8];
        var buffer = new byte[16 * 1024];
        while (true)
        {
            await file.ReadExactlyAsync(header, token);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length > file.Length - file.Position - 4) throw new InvalidDataException();
            if (header.AsSpan(4).SequenceEqual("IEND"u8)) break;
            if (header.AsSpan(4).SequenceEqual("IDAT"u8))
            {
                // Bounded by the already verified <=1 MiB encoded file, not image dimensions.
                for (long remaining = length; remaining > 0;)
                {
                    var count = (int)Math.Min(buffer.Length, remaining);
                    await file.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                    idat.Write(buffer, 0, count);
                    remaining -= count;
                }
            }
            else file.Seek(length, SeekOrigin.Current);
            file.Seek(4, SeekOrigin.Current);
        }
        // ZLibStream can accept a truncated trailer. Check the zlib header and
        // Adler-32 explicitly as well as reading the exact decompressed length.
        var encoded = idat.GetBuffer().AsMemory(0, (int)idat.Length);
        if (encoded.Length < 6 || (encoded.Span[0] & 15) != 8 || (encoded.Span[0] >> 4) > 7 ||
            ((encoded.Span[0] << 8) + encoded.Span[1]) % 31 != 0 || (encoded.Span[1] & 32) != 0)
            throw new InvalidDataException();
        var checksum = BinaryPrimitives.ReadUInt32BigEndian(encoded.Span[^4..]);
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true);
        (int X, int Y, int Dx, int Dy)[] passes = ihdr[28] == 1
            ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
            : [(0, 0, 1, 1)];
        var row = new byte[1 + (width * channels * depth + 7) / 8];
        uint a = 1, b = 0;
        foreach (var (x, y, dx, dy) in passes)
        {
            var passWidth = width > x ? (width - x + dx - 1) / dx : 0;
            var passHeight = height > y ? (height - y + dy - 1) / dy : 0;
            if (passWidth == 0 || passHeight == 0) continue;
            var bytes = 1 + (passWidth * channels * depth + 7) / 8;
            for (var line = 0; line < passHeight; line++)
            {
                await zlib.ReadExactlyAsync(row.AsMemory(0, bytes), token);
                if (row[0] > 4) throw new InvalidDataException();
                for (var i = 0; i < bytes; i++) { a = (a + row[i]) % 65521; b = (b + a) % 65521; }
            }
        }
        if (await zlib.ReadAsync(row.AsMemory(0, 1), token) != 0 || (b << 16 | a) != checksum)
            throw new InvalidDataException();
    }
}
