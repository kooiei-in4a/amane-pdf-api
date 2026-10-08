namespace Amane.Pdf.Api;

internal sealed record JpegHeader(int Width, int Height, int Precision, int Components)
{
    // Parse the bounded JPEG body through its first EOI, including markers after SOF.
    // Trailing MPF/gain-map data is outside the primary JPEG body.
    // The decoder still performs the entropy/Huffman validation under AS/time limits.
    internal static JpegHeader? Read(string path, long maxBytes, long maxPixels, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes) return null;
        var reader = new Reader(stream, token);
        if (reader.Next() != 0xff || reader.Next() != 0xd8) return null;
        JpegHeader? frame = null;
        var scan = false;
        var entropy = false;
        while (true)
        {
            int marker;
            if (entropy)
            {
                do { marker = reader.Next(); } while (marker >= 0 && marker != 0xff);
                if (marker < 0) return null;
            }
            else if (reader.Next() != 0xff) return null;
            do { marker = reader.Next(); } while (marker == 0xff);
            if (marker < 0) return null;
            if (entropy && (marker == 0 || marker is >= 0xd0 and <= 0xd7)) continue;
            entropy = false;
            if (marker == 0xd9) return scan && frame is not null ? frame : null;
            if (marker is 0 or 0xd8 or 0xdc or >= 0xd0 and <= 0xd7) return null;
            if (marker == 1) continue; // TEM has no segment length.
            var high = reader.Next();
            var low = reader.Next();
            if (high < 0 || low < 0) return null;
            var length = high * 256 + low - 2;
            if (length < 0) return null;
            // All SOF markers except DHT/JPG/DAC in the C0..CF range.
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (marker is not (0xc0 or 0xc1 or 0xc2) || frame is not null || length < 6) return null;
                var precision = reader.Next();
                var h1 = reader.Next(); var h2 = reader.Next();
                var w1 = reader.Next(); var w2 = reader.Next();
                var components = reader.Next();
                if (h1 < 0 || h2 < 0 || w1 < 0 || w2 < 0 || precision != 8 || components is not (1 or 3) ||
                    length != 6 + 3 * components) return null;
                frame = new(w1 * 256 + w2, h1 * 256 + h2, precision, components);
                if (frame.Width == 0 || frame.Height == 0 || checked((long)frame.Width * frame.Height) > maxPixels) return null;
                length -= 6;
            }
            if (marker == 0xda)
            {
                if (frame is null || length < 6) return null;
                var components = reader.Next();
                if (components < 1 || components > frame.Components || length != 4 + 2 * components) return null;
                length--;
                scan = true;
                entropy = true;
            }
            for (var i = 0; i < length; i++) if (reader.Next() < 0) return null;
        }
    }

    private sealed class Reader(Stream stream, CancellationToken token)
    {
        private readonly byte[] buffer = new byte[64 * 1024];
        private int position;
        private int length;
        internal int Next()
        {
            if (position == length)
            {
                token.ThrowIfCancellationRequested();
                length = stream.Read(buffer);
                position = 0;
                if (length == 0) return -1;
            }
            return buffer[position++];
        }
    }
}
