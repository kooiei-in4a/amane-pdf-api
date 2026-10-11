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
    internal static readonly string[] PrivateExifText =
        ["private-camera-maker", "private-camera-model", "2001:02:03 04:05:06"];
    internal static byte[] GpsCoordinates
    {
        get
        {
            var bytes = new byte[48];
            uint[] values = [37, 1, 12, 1, 34, 1, 141, 1, 56, 1, 7, 1];
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
            return bytes;
        }
    }
    internal static byte[] ExifWithGps(byte[] jpeg)
    {
        // 合成値だけを使う、有効なlittle-endian TIFF。IFD0→Exif IFD/GPS IFDを持つ。
        const int exifIfd = 74, gpsIfd = 92, gpsData = 158;
        var maker = Encoding.ASCII.GetBytes(PrivateExifText[0] + "\0");
        var model = Encoding.ASCII.GetBytes(PrivateExifText[1] + "\0");
        var date = Encoding.ASCII.GetBytes(PrivateExifText[2] + "\0");
        var makerOffset = gpsData + GpsCoordinates.Length;
        var modelOffset = makerOffset + maker.Length;
        var dateOffset = modelOffset + model.Length;
        var tiff = new byte[dateOffset + date.Length];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        void Count(int offset, ushort count) => BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(offset), count);
        void Entry(int offset, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(offset), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(offset + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offset + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(offset + 8), value);
        }
        Count(8, 5);
        Entry(10, 0x010f, 2, (uint)maker.Length, (uint)makerOffset); // Make
        Entry(22, 0x0110, 2, (uint)model.Length, (uint)modelOffset); // Model
        Entry(34, 0x0112, 3, 1, 6); // Orientation
        Entry(46, 0x8769, 4, 1, exifIfd);
        Entry(58, 0x8825, 4, 1, gpsIfd);
        Count(exifIfd, 1);
        Entry(exifIfd + 2, 0x9003, 2, (uint)date.Length, (uint)dateOffset); // DateTimeOriginal
        Count(gpsIfd, 5);
        Entry(gpsIfd + 2, 0, 1, 4, 0x00000302); // GPSVersionID 2.3.0.0
        Entry(gpsIfd + 14, 1, 2, 2, 'N');
        Entry(gpsIfd + 26, 2, 5, 3, gpsData); // GPSLatitude
        Entry(gpsIfd + 38, 3, 2, 2, 'E');
        Entry(gpsIfd + 50, 4, 5, 3, gpsData + 24); // GPSLongitude
        GpsCoordinates.CopyTo(tiff, gpsData);
        maker.CopyTo(tiff, makerOffset); model.CopyTo(tiff, modelOffset); date.CopyTo(tiff, dateOffset);
        return [.. jpeg[..2], .. Segment(0xe1, [.. "Exif\0\0"u8, .. tiff]), .. jpeg[2..]];
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
