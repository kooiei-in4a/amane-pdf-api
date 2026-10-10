using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Amane.Pdf.Api.Tests;

internal static class FromImagesFixtures
{
    internal static byte[] Segment(byte marker, byte[] payload)
    {
        var bytes = new byte[payload.Length + 4];
        bytes[0] = 255; bytes[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), checked((ushort)(payload.Length + 2)));
        payload.CopyTo(bytes, 4); return bytes;
    }
    internal static byte[] Tiff(int orientation, bool little = true)
    {
        var bytes = Convert.FromHexString(little
            ? "49492A0008000000010012010300010000000100000000000000"
            : "4D4D002A00000008000101120003000000010001000000000000");
        if (little) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), (ushort)orientation);
        else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(18), (ushort)orientation);
        return bytes;
    }
    internal static byte[] Exif(byte[] jpeg, int orientation, bool little = true, bool late = false)
    {
        var exif = Segment(0xe1, [.. "Exif\0\0"u8, .. Tiff(orientation, little)]);
        var padding = late ? Segment(0xe2, new byte[65530]) : [];
        return [.. jpeg[..2], .. padding, .. exif, .. Segment(0xfe, "private-image-marker"u8.ToArray()), .. jpeg[2..]];
    }
    internal static byte[] Chunk(string kind, byte[] payload)
    {
        using var stream = new MemoryStream();
        var type = Encoding.ASCII.GetBytes(kind); var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)payload.Length);
        stream.Write(bytes); stream.Write(type); stream.Write(payload);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, PngSanitizer.UpdateCrc(
            PngSanitizer.UpdateCrc(uint.MaxValue, type), payload) ^ uint.MaxValue);
        stream.Write(bytes); return stream.ToArray();
    }
    internal static byte[] Png(int width = 16, int height = 24, int depth = 8, int color = 6,
        bool interlaced = false, bool metadata = true)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = (byte)depth; header[9] = (byte)color; header[12] = interlaced ? (byte)1 : (byte)0;
        var components = color switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width * components; x++)
            {
                raw.WriteByte(color == 3 ? (byte)0 : (byte)96);
                if (depth == 16) raw.WriteByte(0);
            }
        }
        using var encoded = new MemoryStream();
        using (var zlib = new ZLibStream(encoded, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zlib);
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header),
            .. (color == 3 ? Chunk("PLTE", [0, 128, 255]) : []),
            .. (color == 3 ? Chunk("tRNS", [128]) : []),
            .. (metadata ? Chunk("tEXt", "Author\0private-image-marker"u8.ToArray()) : []),
            .. Chunk("IDAT", encoded.ToArray()), .. Chunk("IEND", []),
            .. (metadata ? "private-trailing-marker"u8.ToArray() : [])];
    }
}
