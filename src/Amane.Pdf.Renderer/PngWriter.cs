using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.Versioning;

namespace Amane.Pdf.Renderer;

[SupportedOSPlatform("linux")]
internal static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static unsafe int Write(IntPtr bitmap, Arguments options)
    {
        if (options.MaxBytes < Signature.Length) return 10;
        var pixels = (byte*)Pdfium.FPDFBitmap_GetBuffer(bitmap);
        var stride = Pdfium.FPDFBitmap_GetStride(bitmap);
        if (pixels == null || stride < options.Width * 4) return 5;
        var complete = false;
        using var output = new FileStream(options.Output, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        try
        {
            output.Write(Signature);
            using var chunks = new ChunkStream(output, options.MaxBytes);
            Span<byte> header = stackalloc byte[13];
            header.Clear();
            BinaryPrimitives.WriteInt32BigEndian(header, options.Width);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], options.Height);
            header[8] = 8;
            header[9] = 2; // RGB, opaque white background
            chunks.WriteChunk("IHDR"u8, header);

            using (var zlib = new ZLibStream(chunks, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[options.Width * 3 + 1]; // PNG filter 0
                for (var y = 0; y < options.Height; y++)
                {
                    var pixel = pixels + (long)y * stride;
                    for (var offset = 1; offset < row.Length; offset += 3, pixel += 4)
                    {
                        row[offset] = pixel[2];
                        row[offset + 1] = pixel[1];
                        row[offset + 2] = pixel[0];
                    }
                    zlib.Write(row);
                }
            }
            chunks.WriteChunk("IEND"u8, []);
            output.Flush();
            complete = true;
            return 0;
        }
        catch (PngSizeException) { return 10; }
        finally
        {
            if (!complete)
            {
                output.Dispose();
                File.Delete(options.Output);
            }
        }
    }

    private sealed class PngSizeException : Exception;

    private sealed class ChunkStream(FileStream output, long maxBytes) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => output.Length;
        public override long Position { get => output.Position; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => WriteChunk("IDAT"u8, buffer);

        internal void WriteChunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length + 12L > maxBytes - output.Position) throw new PngSizeException();
            Span<byte> integer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(integer, bytes.Length);
            output.Write(integer);
            output.Write(type);
            output.Write(bytes);
            var crc = UpdateCrc(UpdateCrc(uint.MaxValue, type), bytes) ^ uint.MaxValue;
            BinaryPrimitives.WriteUInt32BigEndian(integer, crc);
            output.Write(integer);
        }
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            table[i] = crc;
        }
        return table;
    }
}
