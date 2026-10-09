namespace Amane.Pdf.Api;

internal enum PdfOverlayReason { TooComplex, UnsupportedPdf }
internal sealed class PdfOverlayInputException(PdfOverlayReason reason) : Exception
{
    internal PdfOverlayReason Reason { get; } = reason;
    internal static PdfOverlayInputException TooComplex() => new(PdfOverlayReason.TooComplex);
    internal static PdfOverlayInputException Unsupported() => new(PdfOverlayReason.UnsupportedPdf);
}

internal sealed class OverlayCapacity(long limit)
{
    private readonly Dictionary<string, long> reservations = [];
    internal long Used { get; private set; }
    internal long Peak { get; private set; }
    internal long Remaining => limit - Used;
    internal static long Allocated(long bytes) => checked((bytes + 4095) / 4096 * 4096);
    internal static bool IsValid(PdfOptions options) =>
        options.OverlayJsonLimitBytes is > 0 and <= 32 * 1048576 &&
        options.OverlayGeneratedLimitBytes is > 0 and <= 8 * 1048576 &&
        options.OverlayGeneratedJsonLimitBytes is > 0 and <= 40 * 1048576 &&
        options.OverlayOutputLimitBytes is > 0 and <= 62 * 1048576 &&
        options.OverlayJobLimitBytes is > 0 and <= 124 * 1048576 &&
        options.OverlayMaxPages is > 0 and <= 1000 &&
        options.MaxFileBytes is > 0 and <= long.MaxValue - 4095 &&
        options.OverlayJobLimitBytes >= Allocated(options.MaxFileBytes);

    // External writers may leave budget+1 bytes. Reserve that sentinel too.
    internal long ReserveWrite(string path, long perFileLimit, bool external = false)
    {
        if (reservations.ContainsKey(path)) throw new InvalidOperationException("Duplicate overlay reservation.");
        var budget = Math.Min(perFileLimit, Remaining / 4096 * 4096 - (external ? 1 : 0));
        if (budget <= 0) throw PdfOverlayInputException.TooComplex();
        Reserve(path, budget + (external ? 1 : 0));
        return budget;
    }
    internal void Reserve(string path, long bytes)
    {
        if (bytes < 0 || bytes > long.MaxValue - 4095) throw PdfOverlayInputException.TooComplex();
        var allocated = Allocated(bytes);
        var remaining = limit - Used + reservations.GetValueOrDefault(path);
        if (allocated > remaining) throw PdfOverlayInputException.TooComplex();
        Used += allocated - reservations.GetValueOrDefault(path);
        reservations[path] = allocated;
        Peak = Math.Max(Peak, Used);
    }
    internal void Commit(string path, long budget)
    {
        var file = new FileInfo(path);
        if (file.Exists && file.Length > budget) throw PdfOverlayInputException.TooComplex();
        if (!file.Exists || file.Length == 0) throw new InvalidOperationException(PdfOverlayBuilder.Failure);
        Reserve(path, file.Length);
    }
    internal void Delete(string path)
    {
        File.Delete(path);
        if (reservations.Remove(path, out var bytes)) Used -= bytes;
    }
}

internal sealed class OverlayLimitedWriteStream(Stream inner, long limit, CancellationToken token) : Stream
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
        if (count > limit - written) throw PdfOverlayInputException.TooComplex();
        written += count;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); inner.Write(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Check(buffer.Length);
        await inner.WriteAsync(buffer, cancellationToken);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
