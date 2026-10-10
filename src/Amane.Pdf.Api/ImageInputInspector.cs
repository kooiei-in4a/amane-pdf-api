namespace Amane.Pdf.Api;

internal static class ImageInputInspector
{
    internal static bool IsPng(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length == 0) throw new PdfImageInputException(PdfImageReason.Empty);
        Span<byte> signature = stackalloc byte[8];
        var count = stream.Read(signature);
        return count == 8 && signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    }

    internal static ImageInfo ReadJpeg(string path, long maxBytes, long maxPixels, CancellationToken token)
    {
        var header = JpegHeader.Read(path, maxBytes, maxPixels, token) ?? throw PdfImageInputException.Unsupported();
        using var stream = File.OpenRead(path);
        stream.Position = 2;
        while (stream.Position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            if (stream.ReadByte() != 255) throw PdfImageInputException.Unsupported();
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 255);
            if (marker == 1) continue;
            var high = stream.ReadByte(); var low = stream.ReadByte();
            var length = high * 256 + low - 2;
            if (high < 0 || low < 0 || length < 0 || length > stream.Length - stream.Position)
                throw PdfImageInputException.Unsupported();
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                var payload = new byte[length];
                stream.ReadExactly(payload);
                var maxH = 1; var maxV = 1;
                for (var i = 0; i < header.Components; i++)
                {
                    var sampling = payload[7 + i * 3];
                    var h = sampling >> 4; var v = sampling & 15;
                    if (h is < 1 or > 4 || v is < 1 or > 4) throw PdfImageInputException.Unsupported();
                    maxH = Math.Max(maxH, h); maxV = Math.Max(maxV, v);
                }
                return new(header.Width, header.Height, header.Components,
                    header.Components == 1 ? 8 : 8 * maxH, header.Components == 1 ? 8 : 8 * maxV);
            }
            stream.Seek(length, SeekOrigin.Current);
        }
        throw PdfImageInputException.Unsupported();
    }
}
