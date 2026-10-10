using System.Buffers.Binary;

namespace Amane.Pdf.Api;

internal static class ExifOrientation
{
    internal static int Read(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 255 || stream.ReadByte() != 216) return 1;
        while (stream.Position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            if (stream.ReadByte() != 255) return 1;
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 255);
            if (marker is < 0 or 0xda or 0xd9) return 1;
            if (marker == 1) continue;
            var high = stream.ReadByte(); var low = stream.ReadByte();
            var length = high * 256 + low - 2;
            if (high < 0 || low < 0 || length < 0 || length > stream.Length - stream.Position) return 1;
            if (marker == 0xe1)
            {
                var payload = new byte[length]; // At most one JPEG segment, never the whole image.
                stream.ReadExactly(payload);
                if (payload.AsSpan().StartsWith("Exif\0\0"u8)) return ReadPayload(payload.AsSpan(6));
            }
            else stream.Seek(length, SeekOrigin.Current);
        }
        return 1;
    }

    internal static int ReadPayload(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 1;
        var little = tiff[..2].SequenceEqual("II"u8);
        if (!little && !tiff[..2].SequenceEqual("MM"u8)) return 1;
        if (U16(tiff[2..], little) != 42) return 1;
        var offset = (long)U32(tiff[4..], little);
        if (offset < 8 || offset > tiff.Length - 2) return 1;
        var count = U16(tiff[(int)offset..], little);
        if (count > 1000 || offset + 2 + 12L * count + 4 > tiff.Length) return 1;
        var orientation = 1; var found = false;
        for (var i = 0; i < count; i++)
        {
            var entry = tiff.Slice((int)offset + 2 + 12 * i, 12);
            if (U16(entry, little) != 0x112) continue;
            if (found || U16(entry[2..], little) != 3 || U32(entry[4..], little) != 1) return 1;
            orientation = U16(entry[8..], little);
            if (orientation is < 1 or > 8) return 1;
            found = true;
        }
        return orientation;
    }
    private static ushort U16(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static uint U32(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
