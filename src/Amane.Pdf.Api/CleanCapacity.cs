namespace Amane.Pdf.Api;

internal sealed class PdfCleanTooComplexException : Exception;

internal sealed class CleanCapacity(long limit)
{
    private readonly Dictionary<string, long> reservations = [];
    internal long Used { get; private set; }
    internal long Remaining => limit - Used;
    internal static long Allocated(long bytes) => checked((bytes + 4095) / 4096 * 4096);
    internal static bool IsValid(PdfOptions options)
        => options.CleanJsonLimitBytes is > 0 and <= int.MaxValue &&
            options.CleanOutputLimitBytes is > 0 and <= long.MaxValue - 4095 &&
            options.CleanJobLimitBytes is > 0 and <= long.MaxValue - 4095 &&
            options.MaxFileBytes <= long.MaxValue - 4095 &&
            options.CleanJobLimitBytes / 4096 * 4096 >= Allocated(options.MaxFileBytes);

    internal long ReserveWrite(string path, long perFileLimit)
    {
        var bytes = Math.Min(perFileLimit, Remaining / 4096 * 4096);
        if (bytes <= 0) throw new PdfCleanTooComplexException();
        Reserve(path, bytes);
        return bytes;
    }
    internal void Reserve(string path, long bytes)
    {
        var allocated = Allocated(bytes);
        var next = checked(Used - reservations.GetValueOrDefault(path) + allocated);
        if (bytes < 0 || next > limit) throw new PdfCleanTooComplexException();
        reservations[path] = allocated;
        Used = next;
    }
    internal void Delete(string path)
    {
        File.Delete(path);
        if (reservations.Remove(path, out var bytes)) Used -= bytes;
    }
}

// Refuse the write before it reaches disk; post-write accounting alone is insufficient.
internal sealed class CleanLimitedWriteStream(Stream inner, long limit, CancellationToken token = default) : Stream
{
    private long written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => written;
    public override long Position { get => written; set => throw new NotSupportedException(); }
    private void Check(int count)
    {
        token.ThrowIfCancellationRequested();
        if (count > limit - written) throw new PdfCleanTooComplexException();
        written += count;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); inner.Write(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Check(buffer.Length);
        await inner.WriteAsync(buffer, token);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
