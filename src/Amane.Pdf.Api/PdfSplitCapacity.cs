namespace Amane.Pdf.Api;

internal sealed class PdfSplitCapacity(long inputBytes, long maxPdfBytes, long maxJobBytes,
    long zipOverheadBytes = PdfSplitCapacity.SplitZipOverheadBytes)
{
    internal const long SplitZipOverheadBytes = 1024 * 1024;
    internal const long MaxZip32PdfBytes = uint.MaxValue - SplitZipOverheadBytes - 1;
    internal long AcceptedPdfBytes { get; private set; }
    internal long CurrentPartBytes { get; private set; }

    internal static bool IsValid(PdfOptions options)
    {
        try
        {
            return options.MaxSplitParts is >= 2 and <= 500 &&
                options.MaxSplitOutputBytes is > 0 and <= MaxZip32PdfBytes &&
                options.MaxSplitJobBytes <= long.MaxValue - 4095 && options.MaxFileBytes > 0 &&
                options.MaxSplitJobBytes >= checked(Allocated(options.MaxFileBytes) + SplitZipOverheadBytes + 3 * 4096);
        }
        catch (OverflowException) { return false; }
    }

    internal static long Allocated(long bytes) => checked((bytes + 4095) / 4096 * 4096);

    internal long PartBudget(long zipLength)
    {
        var remainingPdf = maxPdfBytes - AcceptedPdfBytes;
        var available = maxJobBytes;
        Subtract(ref available, Allocated(inputBytes));
        Subtract(ref available, Allocated(zipLength));
        Subtract(ref available, zipOverheadBytes);
        Subtract(ref available, 4096); // The FSIZE overflow sentinel can allocate another page.
        var capacity = available / 2 / 4096 * 4096;
        var budget = Math.Min(remainingPdf, capacity);
        if (budget < 1) throw new PdfSplitOutputTooLargeException();
        return budget;
    }

    internal void AcceptPart(long bytes)
    {
        if (bytes < 1) throw new InvalidOperationException("Empty split output.");
        if (bytes > maxPdfBytes - AcceptedPdfBytes) throw new PdfSplitOutputTooLargeException();
        AcceptedPdfBytes += bytes;
        CurrentPartBytes = bytes;
    }

    internal void ReleasePart() => CurrentPartBytes = 0;

    internal void CheckZipLength(long length)
    {
        if (length < 0 || (length > AcceptedPdfBytes && length - AcceptedPdfBytes > zipOverheadBytes))
            throw new PdfSplitOutputTooLargeException();
        var remaining = maxJobBytes;
        Subtract(ref remaining, Allocated(inputBytes));
        Subtract(ref remaining, Allocated(CurrentPartBytes));
        Subtract(ref remaining, Allocated(length));
    }

    private static void Subtract(ref long remaining, long bytes)
    {
        if (bytes < 0 || bytes > remaining) throw new PdfSplitOutputTooLargeException();
        remaining -= bytes;
    }
}
