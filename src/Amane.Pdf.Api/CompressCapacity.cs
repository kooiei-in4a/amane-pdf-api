namespace Amane.Pdf.Api;

// Known image-stage limits move to final writing; unexpected metadata is an error.
internal sealed class CompressLimitException : Exception;

internal sealed class CompressCapacity(long limit)
{
    private readonly Dictionary<string, long> reservations = [];
    internal long Used { get; private set; }
    internal long Peak { get; private set; }
    internal long Remaining => limit - Used;
    internal static long Allocated(long bytes) => checked((bytes + 4095) / 4096 * 4096);
    internal bool CanFinalize(long inputBytes, long jpegAllocation, long entriesAllocation, long jsonBytes, long outputBytes)
    {
        var retained = checked(Allocated(inputBytes) + jpegAllocation + Allocated(jsonBytes));
        // Joining entries and writing the PDF are distinct peaks. When only the
        // rounded estimate fits, the external writer uses its last byte as the sentinel.
        return checked(retained + entriesAllocation) <= limit && checked(retained + Allocated(outputBytes)) <= limit;
    }

    internal long ReserveWrite(string key, long perFileLimit)
    {
        var budget = Math.Min(perFileLimit, Remaining / 4096 * 4096 - 1);
        if (budget <= 0) throw new CompressLimitException();
        Reserve(key, BoundedOutput.FileSizeLimit(budget));
        return budget;
    }

    internal void Reserve(string key, long bytes)
    {
        var value = Allocated(bytes);
        var next = checked(Used - reservations.GetValueOrDefault(key) + value);
        if (bytes < 0 || next > limit) throw new CompressLimitException();
        reservations[key] = value;
        Used = next;
        Peak = Math.Max(Peak, Used);
    }
    internal void Release(string key)
    {
        if (reservations.Remove(key, out var value)) Used -= value;
    }
    internal void Delete(string path)
    {
        File.Delete(path);
        Release(path);
    }
}
