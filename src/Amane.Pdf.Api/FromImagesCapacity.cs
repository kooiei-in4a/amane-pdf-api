namespace Amane.Pdf.Api;

internal enum PdfImageReason { Empty, Unsupported, Capacity }
internal sealed class PdfImageInputException(PdfImageReason reason) : Exception
{
    internal PdfImageReason Reason { get; } = reason;
    internal static PdfImageInputException Unsupported() => new(PdfImageReason.Unsupported);
    internal static PdfImageInputException Capacity() => new(PdfImageReason.Capacity);
}

internal sealed class FromImagesCapacity
{
    internal const long AuxiliaryBytes = 1024 * 1024;
    private readonly long limit;
    private readonly Dictionary<string, long> reservations = [];
    internal long Used { get; private set; } = AuxiliaryBytes;
    internal long Peak { get; private set; } = AuxiliaryBytes;
    internal long Remaining => limit - Used;
    internal FromImagesCapacity(long limit) => this.limit = limit;
    internal static long Allocated(long bytes) => checked((bytes + 4095) / 4096 * 4096);

    internal static bool IsValid(PdfOptions o) =>
        o.MaxImageFiles is >= 1 and <= 100 &&
        o.MaxImageInputBytes is > 0 and <= 50 * 1048576 &&
        o.MaxUploadImagePixels is > 0 and <= 50_000_000 &&
        o.ImageOutputLimitBytes is > 0 and <= 54 * 1048576 &&
        o.ImageJobLimitBytes is > 0 and <= 124 * 1048576 &&
        o.ImagePnmLimitBytes is > 0 and <= 24 * 1048576 &&
        o.ImagePngWorkingLimitBytes is > 0 and <= 320 * 1048576 &&
        o.ImageJobLimitBytes >= AuxiliaryBytes + Allocated(o.MaxImageInputBytes) + o.MaxImageFiles * 4096L &&
        !string.IsNullOrWhiteSpace(o.JpegtranPath) &&
        ValidJpegMemory(o.ImageDjpegAddressSpaceLimitBytes, o.ImageDjpegMaxMemoryMegabytes) &&
        ValidJpegMemory(o.ImageJpegtranAddressSpaceLimitBytes, o.ImageJpegtranMaxMemoryMegabytes);

    internal static bool ValidJpegMemory(long address, int megabytes) =>
        address is > 64 * 1048576 and <= 384 * 1048576 && megabytes is >= 1 and <= 320 &&
        checked((long)megabytes * 1_000_000) <= address - 64 * 1048576;

    internal void Reserve(string path, long bytes)
    {
        if (bytes < 0 || bytes > long.MaxValue - 4095) throw PdfImageInputException.Capacity();
        var rounded = Allocated(bytes);
        var old = reservations.GetValueOrDefault(path);
        if (rounded > Remaining + old) throw PdfImageInputException.Capacity();
        Used += rounded - old;
        Peak = Math.Max(Peak, Used);
        reservations[path] = rounded;
    }
    internal long ReserveWrite(string path, long fileLimit, bool external)
    {
        if (reservations.ContainsKey(path)) throw new InvalidOperationException("Duplicate image reservation.");
        var budget = Math.Min(fileLimit, Remaining / 4096 * 4096 - (external ? 1 : 0));
        if (budget <= 0) throw PdfImageInputException.Capacity();
        Reserve(path, budget + (external ? 1 : 0));
        return budget;
    }
    internal void Commit(string path, long budget)
    {
        var file = new FileInfo(path);
        if (file.Exists && file.Length > budget) throw PdfImageInputException.Capacity();
        if (!file.Exists || file.Length <= 0) throw new InvalidOperationException("Image output is invalid.");
        Reserve(path, file.Length);
    }
    internal void Delete(string path)
    {
        File.Delete(path);
        if (reservations.Remove(path, out var bytes)) Used -= bytes;
    }
}

internal sealed record ImageInfo(int Width, int Height, int Components, int McuWidth = 8, int McuHeight = 8,
    bool Png = false, int BitDepth = 8);

internal sealed record ImagePageLayout(double Width, double Height, double Scale)
{
    internal static ImagePageLayout For(ImageInfo image)
    {
        var width = (image.Width >= image.Height ? 297 : 210) * 72 / 25.4;
        var height = (image.Width >= image.Height ? 210 : 297) * 72 / 25.4;
        var margin = 10 * 72 / 25.4;
        var fit = Math.Min((width - 2 * margin) / image.Width, (height - 2 * margin) / image.Height);
        var full = Math.Min(width / image.Width, height / image.Height);
        return new(width, height, fit / full);
    }
    internal string Description => FormattableString.Invariant($"dim:{Width:R} {Height:R}, pos:c, sc:{Scale:R} rel");
}
