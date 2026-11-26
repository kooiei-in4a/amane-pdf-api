using System.Buffers.Binary;
using System.IO.Compression;

namespace Amane.Pdf.Api;

internal enum OcrBinarization { Sauvola, AdaptiveOtsu }

internal static class OcrPageImage
{
    private const int TilesPerSide = 8;
    private const int SamplesPerTileSide = 8;
    private const int GridSize = TilesPerSide * SamplesPerTileSide;
    private const int SamplesPerTile = SamplesPerTileSide * SamplesPerTileSide;
    private const int UnevenSpread = 24;
    private const int StrokeSampleStep = 16;
    private const int MinStrokeSamples = 32;
    private const int FineStrokePercent = 35;
    private const int LightBackground = 224;
    private const int BackgroundPixelDifference = 4;
    private const int MaxVaryingBackgroundPercent = 5;

    internal static OcrBinarization SelectBinarization(string path, int width, int height, CancellationToken token)
    {
        try
        {
            using var png = File.OpenRead(path);
            return SelectBinarization(png, width, height, token);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            // Leave a renderer output outside this expected format to the existing Tesseract error handling.
            return OcrBinarization.Sauvola;
        }
    }

    private static OcrBinarization SelectBinarization(Stream png, int width, int height, CancellationToken token)
    {
        // PngWriter emits non-interlaced, unfiltered RGB8 scanlines.
        Span<byte> header = stackalloc byte[33];
        png.ReadExactly(header);
        if (header[0] != 137 || !header[1..16].SequenceEqual("PNG\r\n\x1a\n\0\0\0\rIHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(header[16..]) != width ||
            BinaryPrimitives.ReadInt32BigEndian(header[20..]) != height ||
            !header[24..29].SequenceEqual("\x08\x02\0\0\0"u8))
            throw new InvalidDataException();

        using var imageData = new ImageDataStream(png, token);
        using var pixels = new ZLibStream(imageData, CompressionMode.Decompress);
        var row = new byte[width * 3 + 1];
        Span<byte> samples = stackalloc byte[GridSize * GridSize];
        Span<int> sampleOffsets = stackalloc int[GridSize];
        for (var x = 0; x < GridSize; x++)
            sampleOffsets[x] = 1 + 3 * SampleCoordinate(x, width);

        var firstX = Math.Min(StrokeSampleStep / 2, width / 2);
        var firstY = Math.Min(StrokeSampleStep / 2, height / 2);
        var verticalRuns = new int[1 + (width - 1 - firstX) / StrokeSampleStep];
        var lastColumnBrightness = new int[verticalRuns.Length];
        var strokes = new StrokeCounts();
        var background = new BackgroundPairs();
        var ruleSpan = Math.Max(2, (verticalRuns.Length + 2) / 3);
        var horizontalRules = 0;
        var previousRowHasRule = false;
        var sampleY = 0;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            pixels.ReadExactly(row);
            if (row[0] != 0) throw new InvalidDataException();

            while (sampleY < GridSize && SampleCoordinate(sampleY, height) == y)
            {
                var tileRow = sampleY / SamplesPerTileSide * TilesPerSide;
                var rowOffset = sampleY % SamplesPerTileSide * SamplesPerTileSide;
                for (var x = 0; x < GridSize; x++)
                {
                    var tile = tileRow + x / SamplesPerTileSide;
                    samples[tile * SamplesPerTile + rowOffset + x % SamplesPerTileSide] =
                        (byte)Brightness(row, sampleOffsets[x]);
                }
                sampleY++;
            }

            // Runs across both axes estimate the prevalence of thin strokes in either page orientation.
            if (y % StrokeSampleStep == firstY)
            {
                var run = 0;
                var lastBrightness = Brightness(row, 1);
                strokes.AddPixel(lastBrightness < 128, ref run);
                for (var offset = 4; offset < row.Length; offset += 3)
                {
                    var brightness = Brightness(row, offset);
                    strokes.AddPixel(brightness < 128, ref run);
                    background.Add(lastBrightness, brightness);
                    lastBrightness = brightness;
                }
                strokes.EndRun(ref run);
            }
            var darkColumns = 0;
            var rowHasRule = false;
            for (int i = 0, offset = 1 + 3 * firstX; i < verticalRuns.Length; i++, offset += 3 * StrokeSampleStep)
            {
                var brightness = Brightness(row, offset);
                var dark = brightness < 128;
                strokes.AddPixel(dark, ref verticalRuns[i]);
                darkColumns = dark ? darkColumns + 1 : 0;
                rowHasRule |= darkColumns >= ruleSpan;
                if (y > 0) background.Add(lastColumnBrightness[i], brightness);
                lastColumnBrightness[i] = brightness;
            }
            // A rule may cover several consecutive rows. Count the band once.
            if (rowHasRule && !previousRowHasRule) horizontalRules++;
            previousRowHasRule = rowHasRule;
        }
        foreach (ref var run in verticalRuns.AsSpan()) strokes.EndRun(ref run);

        // Prefer Sauvola on ordinary or uncertain scans. Adaptive Otsu can turn background noise
        // into foreground and disrupt both segmentation and automatic orientation.
        return horizontalRules >= 2 && strokes.FineStrokesAreCommon &&
            background.IsClean && BackgroundSpread(samples) < UnevenSpread
            ? OcrBinarization.AdaptiveOtsu : OcrBinarization.Sauvola;
    }

    private static int SampleCoordinate(int sample, int size) => (2 * sample + 1) * size / (2 * GridSize);

    private static int Brightness(byte[] row, int offset)
        => (77 * row[offset] + 150 * row[offset + 1] + 29 * row[offset + 2] + 128) >> 8;

    private static int BackgroundSpread(Span<byte> samples)
    {
        Span<byte> background = stackalloc byte[TilesPerSide * TilesPerSide];
        for (var tile = 0; tile < background.Length; tile++)
        {
            var brightness = samples.Slice(tile * SamplesPerTile, SamplesPerTile);
            brightness.Sort();
            // Use the upper quartile to keep ink and isolated dark spots out of the background estimate.
            background[tile] = brightness[SamplesPerTile * 3 / 4];
        }
        background.Sort();
        // Ignore the darkest and brightest tenth of the tiles to reduce the effect of borders and graphics.
        return background[background.Length - 1 - background.Length / 10] - background[background.Length / 10];
    }

    private struct StrokeCounts
    {
        private int total;
        private int fine;

        internal readonly bool FineStrokesAreCommon
            => total >= MinStrokeSamples && fine * 100 >= total * FineStrokePercent;

        internal void AddPixel(bool isDark, ref int run)
        {
            if (isDark) run++;
            else EndRun(ref run);
        }

        internal void EndRun(ref int run)
        {
            if (run == 0) return;
            total++;
            if (run <= 3) fine++;
            run = 0;
        }
    }

    private struct BackgroundPairs
    {
        private int total;
        private int light;
        private int varying;

        internal readonly bool IsClean
            => light > 0 && light * 2 >= total && varying * 100 <= light * MaxVaryingBackgroundPercent;

        internal void Add(int first, int second)
        {
            total++;
            if (first < LightBackground || second < LightBackground) return;
            light++;
            if (Math.Abs(first - second) >= BackgroundPixelDifference) varying++;
        }
    }

    // Concatenate the renderer's IDAT payloads without buffering the encoded PNG or the whole raster.
    private sealed class ImageDataStream(Stream png, CancellationToken token) : Stream
    {
        private int remaining;
        private bool reachedEnd;

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || reachedEnd) return 0;
            Span<byte> chunk = stackalloc byte[8];
            while (remaining == 0)
            {
                token.ThrowIfCancellationRequested();
                png.ReadExactly(chunk);
                remaining = BinaryPrimitives.ReadInt32BigEndian(chunk);
                if (remaining < 0 || remaining > png.Length - png.Position - 4) throw new InvalidDataException();
                if (chunk[4..].SequenceEqual("IEND"u8))
                {
                    reachedEnd = true;
                    return 0;
                }
                if (!chunk[4..].SequenceEqual("IDAT"u8)) throw new InvalidDataException();
                if (remaining == 0) png.Seek(4, SeekOrigin.Current);
            }
            var bytes = Math.Min(remaining, buffer.Length);
            png.ReadExactly(buffer[..bytes]);
            remaining -= bytes;
            if (remaining == 0) png.Seek(4, SeekOrigin.Current);
            return bytes;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
