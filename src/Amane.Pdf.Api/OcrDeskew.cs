namespace Amane.Pdf.Api;

internal static class OcrDeskew
{
    private const int AnalysisSide = 900;
    private const int Ink = 160;
    private const int EdgeInk = 232;
    private const int MaxAngle = 10;
    private const double MinStandardAngle = 0.8;
    private const double MinHighAngle = 2.5;
    private const double MinImprovement = 1.2;
    private const double MinPeakRatio = 1.08;

    internal static double TryCorrect(PdfPageJob job, string source, string corrected,
        int width, int height, long imageLimit, CancellationToken token, bool high = false)
    {
        var complete = false;
        try
        {
            var gray = OcrPageImage.ReadGrayscale(source, width, height, token);
            var angle = Estimate(gray, width, height, token, high);
            if (angle == 0) return 0;

            var rgb = OcrPageImage.ReadRgb(source, width, height, token);
            job.WriteFile(corrected, output => WriteCorrected(output, rgb, width, height, angle, token), token, imageLimit);
            complete = true;
            return angle;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or PdfOverlayInputException)
        {
            token.ThrowIfCancellationRequested();
            return 0;
        }
        finally
        {
            if (!complete) job.Delete(corrected);
        }
    }

    // Positive angles slope down to the right in image coordinates.
    internal static double Estimate(byte[] gray, int width, int height, CancellationToken token, bool high = false)
    {
        var step = (Math.Max(width, height) + AnalysisSide - 1) / AnalysisSide;
        var columns = width / step;
        var rows = height / step;
        if (columns < 40 || rows < 40) return 0;

        var sampleCount = columns * rows;
        var points = new List<(int X, int Y)>();
        for (var row = 0; row < rows; row++)
        {
            token.ThrowIfCancellationRequested();
            for (var column = 0; column < columns; column++)
            {
                if (!HasInk(gray, width, column * step, row * step, step)) continue;
                points.Add((column, row));
                if (points.Count > sampleCount / 3) return 0;
            }
        }
        if (points.Count < Math.Max(40, sampleCount / 500)) return 0;
        // Dense text still has light paper between strokes. A broad dark or midtone region can
        // dominate the projection on a photo or a page with a dark background.
        if (points.Count > sampleCount / 5 && !HasLightBackground(gray, width, height, token)) return 0;

        // Distribute each ink sample between neighboring projected rows to avoid rounding peaks.
        var padding = (int)Math.Ceiling(columns * Math.Tan(Radians(MaxAngle))) + 1;
        var profile = new int[rows + 2 * padding];
        var shifts = new int[columns];
        var scores = new double[4 * MaxAngle + 1];
        var best = 2 * MaxAngle;
        for (var i = 0; i < scores.Length; i++)
        {
            scores[i] = Score((i - 2 * MaxAngle) * 0.5);
            if (scores[i] > scores[best]) best = i;
        }
        // A peak at an endpoint may continue growing outside the supported range.
        if (best == 0 || best == scores.Length - 1) return 0;

        var coarseAngle = (best - 2 * MaxAngle) * 0.5;
        var angle = coarseAngle;
        var peak = scores[best];
        for (var i = -4; i <= 4; i++)
        {
            if (i == 0) continue;
            var candidate = coarseAngle + i * 0.1;
            var score = Score(candidate);
            if (score <= peak) continue;
            peak = score;
            angle = candidate;
        }
        var upright = scores[2 * MaxAngle];
        var otherPeak = 0.0;
        for (var i = 0; i < scores.Length; i++)
        {
            if (Math.Abs((i - 2 * MaxAngle) * 0.5 - angle) >= 1.5)
                otherPeak = Math.Max(otherPeak, scores[i]);
        }
        // Mild skew often reads correctly with tessdata-best. Resampling can change the
        // segmentation or shape of those characters even when the estimated angle is sound.
        var minAngle = high ? MinHighAngle : MinStandardAngle;
        if (Math.Abs(angle) < minAngle || peak <= upright * MinImprovement || peak <= otherPeak * MinPeakRatio)
            return 0;
        return WouldClipInk(gray, width, height, angle, token) ? 0 : angle;

        double Score(double candidate)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(profile);
            const int scale = 16;
            var slope = Math.Tan(Radians(candidate)) * scale;
            for (var x = 0; x < shifts.Length; x++)
                shifts[x] = padding * scale - (int)Math.Round(x * slope);
            foreach (var point in points)
            {
                var shift = shifts[point.X];
                var row = point.Y + shift / scale;
                var fraction = shift % scale;
                profile[row] += scale - fraction;
                profile[row + 1] += fraction;
            }
            double score = 0;
            for (var row = 2; row < profile.Length; row++)
            {
                var difference = (double)profile[row] - profile[row - 2];
                score += difference * difference;
            }
            return score;
        }
    }

    private static bool HasLightBackground(byte[] gray, int width, int height, CancellationToken token)
    {
        const int tiles = 8;
        const int samples = 8;
        const int grid = tiles * samples;
        const int light = 224;
        var darkTiles = 0;
        for (var tileY = 0; tileY < tiles; tileY++)
        {
            token.ThrowIfCancellationRequested();
            for (var tileX = 0; tileX < tiles; tileX++)
            {
                var bright = 0;
                for (var y = 0; y < samples; y++)
                {
                    var offset = (2 * (tileY * samples + y) + 1) * height / (2 * grid) * width;
                    for (var x = 0; x < samples; x++)
                    {
                        var column = (2 * (tileX * samples + x) + 1) * width / (2 * grid);
                        if (gray[offset + column] >= light) bright++;
                    }
                }
                if (bright < samples * samples / 2 && ++darkTiles >= tiles) return false;
            }
        }
        return true;
    }

    private static bool HasInk(byte[] gray, int width, int left, int top, int step)
    {
        for (var y = 0; y < step; y++)
        {
            var first = (top + y) * width + left;
            for (var x = 0; x < step; x++)
            {
                if (gray[first + x] < Ink) return true;
            }
        }
        return false;
    }

    private static bool WouldClipInk(byte[] gray, int width, int height, double angle, CancellationToken token)
    {
        var (sin, cos) = Math.SinCos(Radians(angle));
        var cx = (width - 1) / 2.0;
        var cy = (height - 1) / 2.0;
        // A two-pixel margin covers sampling near the rim. Interior pixels cannot reach it.
        var marginX = (int)Math.Ceiling((1 - cos) * cx + Math.Abs(sin) * cy) + 2;
        var marginY = (int)Math.Ceiling((1 - cos) * cy + Math.Abs(sin) * cx) + 2;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var dx = cx + sin * (y - cy) - cos * cx;
            var dy = cy + cos * (y - cy) + sin * cx;
            if (y >= marginY && y < height - marginY && width > 2 * marginX)
            {
                if (Clips(0, marginX) || Clips(width - marginX, width)) return true;
            }
            else if (Clips(0, width)) return true;

            bool Clips(int start, int end)
            {
                for (var x = start; x < end; x++)
                {
                    if (gray[y * width + x] >= EdgeInk) continue;
                    var targetX = dx + cos * x;
                    var targetY = dy - sin * x;
                    if (targetX < 1 || targetX > width - 2 || targetY < 1 || targetY > height - 2)
                        return true;
                }
                return false;
            }
        }
        return false;
    }

    internal static void WriteCorrected(Stream output, byte[] rgb, int width, int height, double angle, CancellationToken token)
    {
        var (sin, cos) = Math.SinCos(Radians(angle));
        var cx = (width - 1) / 2.0;
        var cy = (height - 1) / 2.0;
        OcrPageImage.WritePng(output, width, height, rgb: true, (y, row) =>
        {
            var sourceX = cx - sin * (y - cy) - cos * cx;
            var sourceY = cy + cos * (y - cy) - sin * cx;
            // Inverse mapping samples the original RGB pixels at the corrected pixel centres.
            row.Fill(255);
            for (int x = 0, offset = 0; x < width; x++, offset += 3, sourceX += cos, sourceY += sin)
            {
                if (sourceX < 0 || sourceX >= width - 1 || sourceY < 0 || sourceY >= height - 1) continue;
                var left = (int)sourceX;
                var top = (int)sourceY;
                var first = (top * width + left) * 3;
                var second = first + width * 3;
                var fx = sourceX - left;
                var fy = sourceY - top;
                for (var channel = 0; channel < 3; channel++, first++, second++)
                {
                    var a = rgb[first] + (rgb[first + 3] - rgb[first]) * fx;
                    var b = rgb[second] + (rgb[second + 3] - rgb[second]) * fx;
                    row[offset + channel] = (byte)(a + (b - a) * fy + 0.5);
                }
            }
        }, token);
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
