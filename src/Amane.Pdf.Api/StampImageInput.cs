namespace Amane.Pdf.Api;

internal sealed record StampImage(string Path, int Width, int Height);
internal enum PdfStampImageReason { Unsupported, TooComplex }
internal sealed class PdfStampImageException(PdfStampImageReason reason) : Exception
{
    internal PdfStampImageReason Reason { get; } = reason;
    internal static PdfStampImageException Unsupported() => new(PdfStampImageReason.Unsupported);
}

internal static class StampImageInput
{
    internal const int MaxSide = 2000;
    internal const long MaxBytes = 1048576;

    internal static bool IsValid(PdfOptions options) =>
        options.MaxStampImageBytes is > 0 and <= MaxBytes && options.MaxFileBytes > 0 &&
        options.MaxFileBytes <= long.MaxValue - PdfOptions.MultipartOverheadBytes - options.MaxStampImageBytes &&
        options.MaxFileBytes <= long.MaxValue - 4095 &&
        options.OverlayJobLimitBytes - 2 * OverlayCapacity.Allocated(options.MaxStampImageBytes) >=
            OverlayCapacity.Allocated(options.MaxFileBytes);

    internal static async Task<StampImage> PrepareAsync(TemporaryPdfFiles files, PdfOptions options, CancellationToken token)
    {
        ImageInfo image;
        try
        {
            image = await PngSanitizer.WriteAsync(files.StampUploadPath, files.StampImagePath,
                new FileInfo(files.StampUploadPath).Length, (long)MaxSide * MaxSide,
                options.ImagePngWorkingLimitBytes, token);
        }
        catch (PdfImageInputException exception) when (exception.Reason != PdfImageReason.Capacity)
        { throw PdfStampImageException.Unsupported(); }
        catch (PdfImageInputException) { throw new InvalidOperationException(PdfOverlayBuilder.Failure); }
        File.Delete(files.StampUploadPath);
        if (image.Width > MaxSide || image.Height > MaxSide) throw PdfStampImageException.Unsupported();
        await PngDataValidator.ValidateAsync(files.StampImagePath, token);
        return new(files.StampImagePath, image.Width, image.Height);
    }
}
