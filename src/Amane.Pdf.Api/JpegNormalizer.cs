using System.Globalization;
using System.Text;

namespace Amane.Pdf.Api;

internal sealed class JpegNormalizer(PdfOptions options, TemporaryPdfFiles files, FromImagesCapacity capacity)
{
    internal static int Scale(int length) => length <= 2339 ? 8 : Math.Clamp(2339 * 8 / length, 1, 8);
    internal static (int Width, int Height) TransformedSize(ImageInfo image, int orientation)
    {
        var w = image.Width; var h = image.Height;
        if (orientation is 2 or 3 or 7 or 8)
        {
            if (w < image.McuWidth) throw PdfImageInputException.Unsupported();
            w = w / image.McuWidth * image.McuWidth;
        }
        if (orientation is 3 or 4 or 6 or 7)
        {
            if (h < image.McuHeight) throw PdfImageInputException.Unsupported();
            h = h / image.McuHeight * image.McuHeight;
        }
        return orientation >= 5 ? (h, w) : (w, h);
    }
    private static string[] Transform(int orientation) => orientation switch
    {
        1 => [],
        2 => ["-flip", "horizontal", "-trim"],
        3 => ["-rotate", "180", "-trim"],
        4 => ["-flip", "vertical", "-trim"],
        5 => ["-transpose", "-trim"],
        6 => ["-rotate", "90", "-trim"],
        7 => ["-transverse", "-trim"],
        8 => ["-rotate", "270", "-trim"],
        _ => throw new ArgumentOutOfRangeException(nameof(orientation))
    };

    internal async Task<(string Path, ImageInfo Image)> NormalizeAsync(string input, ImageInfo source,
        int index, bool standard, CancellationToken token)
    {
        var orientation = ExifOrientation.Read(input, token);
        var transformedInput = input;
        if (standard)
        {
            var scale = Scale(Math.Max(source.Width, source.Height));
            var width = (source.Width * scale + 7) / 8;
            var height = (source.Height * scale + 7) / 8;
            var expectedSize = PdfCompressProcessor.PnmSize(source.Width, source.Height, source.Components, scale);
            var pnmLimit = checked(expectedSize + 512);
            if (pnmLimit > options.ImagePnmLimitBytes) throw PdfImageInputException.Unsupported();
            var pnm = files.ImagePath(index, "decoded.pnm");
            await WriteAsync(options.DjpegPath, ["-strict", "-maxscans", "100", "-scale",
                scale.ToString(CultureInfo.InvariantCulture) + "/8", "-maxmemory",
                options.ImageDjpegMaxMemoryMegabytes.ToString(CultureInfo.InvariantCulture) + "M", "-outfile", pnm, input],
                options.ImageDjpegAddressSpaceLimitBytes, pnm, pnmLimit, token);
            var header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"P{(source.Components == 1 ? 5 : 6)}\n{width} {height}\n255\n"));
            using (var stream = File.OpenRead(pnm))
            {
                var actual = new byte[header.Length];
                if (stream.Length != header.Length + (long)width * height * source.Components)
                    throw new InvalidOperationException("Decoded image is invalid.");
                stream.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(header)) throw new InvalidOperationException("Decoded image is invalid.");
            }
            var encoded = files.ImagePath(index, "encoded.jpg");
            await WriteAsync(options.CjpegPath, ["-strict", "-quality", "80", "-sample", "2x2", "-optimize",
                "-maxmemory", "64M", "-outfile", encoded, pnm], options.JpegAddressSpaceLimitBytes,
                encoded, options.ImageOutputLimitBytes, token);
            capacity.Delete(pnm);
            transformedInput = encoded;
        }
        var image = standard
            ? ReadGenerated(transformedInput, options.ImageOutputLimitBytes, token) : source;
        var expected = TransformedSize(image, orientation);
        var normalized = files.ImagePath(index, "normalized.jpg");
        await WriteAsync(options.JpegtranPath, ["-copy", "none", "-optimize", "-strict", "-maxscans", "100",
            "-maxmemory", options.ImageJpegtranMaxMemoryMegabytes.ToString(CultureInfo.InvariantCulture) + "M",
            .. Transform(orientation), "-outfile", normalized, transformedInput],
            options.ImageJpegtranAddressSpaceLimitBytes, normalized, options.ImageOutputLimitBytes, token);
        var result = ReadGenerated(normalized, options.ImageOutputLimitBytes, token);
        if (result.Width != expected.Width || result.Height != expected.Height || result.Components != source.Components)
            throw new InvalidOperationException("Normalized image is invalid.");
        NormalizeMarkers(normalized, token);
        if (standard) capacity.Delete(transformedInput);
        return (normalized, result);
    }

    private ImageInfo ReadGenerated(string path, long limit, CancellationToken token)
    {
        try { return ImageInputInspector.ReadJpeg(path, limit, options.MaxUploadImagePixels, token); }
        catch (PdfImageInputException) { throw new InvalidOperationException("Generated image is invalid."); }
    }

    private async Task WriteAsync(string tool, string[] arguments, long address, string output, long fileLimit,
        CancellationToken token)
    {
        var budget = capacity.ReserveWrite(output, fileLimit, external: true);
        using (TemporaryPdfFiles.CreatePrivateFile(output)) { }
        var request = ProcessMemoryLimits.CreateRequest(options.PrlimitPath, tool, arguments, address,
            budget + 1, null) with { WorkingDirectory = Path.GetFullPath(files.DirectoryPath) };
        var result = await ExternalProcessRunner.RunAsync(request, token);
        CheckResult(result, output, budget, token);
        capacity.Commit(output, budget);
    }
    internal static void CheckResult(ExternalProcessResult result, string path, long budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (result.ExitCode is 126 or 127 or < 0 or >= 128)
            throw new InvalidOperationException("Image tool failed.");
        var output = new FileInfo(path);
        if (output.Exists && output.Length > budget) throw PdfImageInputException.Capacity();
        if (result.ExitCode != 0) throw PdfImageInputException.Unsupported();
        if (!output.Exists || output.Length == 0) throw new InvalidOperationException("Image output is invalid.");
    }

    internal static void NormalizeMarkers(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var reader = new MarkerReader(stream, token);
        if (reader.Next() != 255 || reader.Next() != 216) throw new InvalidOperationException("Invalid output JPEG.");
        var entropy = false; var jfif = false; var adobe = false;
        while (true)
        {
            int marker;
            if (entropy)
            {
                do { marker = reader.Next(); } while (marker >= 0 && marker != 255);
                if (marker < 0) throw new InvalidOperationException("Invalid output JPEG.");
            }
            else if (reader.Next() != 255) throw new InvalidOperationException("Invalid output JPEG.");
            do { marker = reader.Next(); } while (marker == 255);
            if (entropy && (marker == 0 || marker is >= 0xd0 and <= 0xd7)) continue;
            entropy = false;
            if (marker == 0xd9)
            {
                if (reader.Position != stream.Length) throw new InvalidOperationException("JPEG trailing data.");
                return;
            }
            if (marker == 1) continue;
            if (marker < 0) throw new InvalidOperationException("Invalid output JPEG.");
            var high = reader.Next(); var low = reader.Next();
            var length = high * 256 + low - 2;
            if (high < 0 || low < 0 || length < 0) throw new InvalidOperationException("Invalid output JPEG.");
            if (marker is >= 0xe0 and <= 0xef or 0xfe)
            {
                var start = reader.Position;
                if ((marker != 0xe0 || length != 14 || jfif) && (marker != 0xee || length != 12 || adobe))
                    throw new InvalidOperationException("JPEG metadata remained.");
                var payload = new byte[length];
                for (var i = 0; i < length; i++) payload[i] = (byte)reader.Required();
                if (marker == 0xe0)
                {
                    if (!payload.AsSpan().StartsWith("JFIF\0"u8) || payload[5] != 1 ||
                        payload[6] is not (1 or 2) || payload[12] != 0 || payload[13] != 0)
                        throw new InvalidOperationException("Invalid generated JFIF.");
                    RandomAccess.Write(stream.SafeFileHandle, new byte[] { 0, 0, 1, 0, 1 }, start + 7);
                    jfif = true;
                }
                else
                {
                    if (!payload.AsSpan().StartsWith("Adobe"u8) || payload[5] != 0 || payload[6] != 100 ||
                        payload[7] != 0 || payload[8] != 0 || payload[9] != 0 || payload[10] != 0 || payload[11] > 1)
                        throw new InvalidOperationException("Invalid generated Adobe marker.");
                    adobe = true;
                }
            }
            else for (var i = 0; i < length; i++) _ = reader.Required();
            if (marker == 0xda) entropy = true;
        }
    }

    private sealed class MarkerReader(Stream stream, CancellationToken token)
    {
        private readonly byte[] buffer = new byte[64 * 1024];
        private int offset; private int count;
        internal long Position { get; private set; }
        internal int Required()
        {
            var value = Next();
            if (value < 0) throw new InvalidOperationException("Truncated output JPEG.");
            return value;
        }
        internal int Next()
        {
            if (offset == count)
            {
                token.ThrowIfCancellationRequested();
                count = stream.Read(buffer); offset = 0;
                if (count == 0) return -1;
            }
            Position++;
            return buffer[offset++];
        }
    }
}
