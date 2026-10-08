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
