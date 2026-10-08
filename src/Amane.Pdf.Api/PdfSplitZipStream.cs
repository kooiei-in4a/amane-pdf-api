namespace Amane.Pdf.Api;

// Does not own the underlying stream. The ZIP builder closes it explicitly, including after Abort.
internal sealed class PdfSplitZipStream(Stream underlying, PdfSplitCapacity capacity, CancellationToken token) : Stream
{
    private bool aborted;
    internal void Abort() => aborted = true;
    public override bool CanRead => false;
    public override bool CanSeek => underlying.CanSeek;
    public override bool CanWrite => underlying.CanWrite && !aborted;
    public override long Length => underlying.Length;
    public override long Position
    {
        get => underlying.Position;
        set { CheckState(); underlying.Position = value; }
    }
    private void CheckState(CancellationToken caller = default)
    {
        token.ThrowIfCancellationRequested();
        caller.ThrowIfCancellationRequested();
        if (aborted) throw new InvalidOperationException("Split ZIP was aborted.");
    }
    private void CheckWrite(int count, CancellationToken caller = default)
    {
        CheckState(caller);
        if (underlying.Position > long.MaxValue - count) throw new PdfSplitOutputTooLargeException();
        capacity.CheckZipLength(Math.Max(underlying.Length, underlying.Position + count));
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckWrite(buffer.Length);
        underlying.Write(buffer);
    }
    public override void WriteByte(byte value)
    {
        CheckWrite(1);
        underlying.WriteByte(value);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckWrite(buffer.Length, cancellationToken);
        return underlying.WriteAsync(buffer, cancellationToken);
    }
    public override void SetLength(long value)
    {
        CheckState();
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        capacity.CheckZipLength(value);
        underlying.SetLength(value);
    }
    public override long Seek(long offset, SeekOrigin origin) { CheckState(); return underlying.Seek(offset, origin); }
    public override void Flush() { CheckState(); underlying.Flush(); }
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        CheckState(cancellationToken);
        return underlying.FlushAsync(cancellationToken);
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
