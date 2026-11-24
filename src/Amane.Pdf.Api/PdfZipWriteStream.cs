namespace Amane.Pdf.Api;

// The ZIP builder owns and closes the underlying stream, including after Abort.
internal sealed class PdfZipWriteStream(Stream underlying, Action<long> checkLength,
    Func<Exception> outputTooLarge, CancellationToken token) : Stream
{
    private bool aborted;
    internal void Abort() => aborted = true;
    internal static void CloseOwnedStream(ref Stream? stream)
    {
        var owned = stream;
        stream = null; // A throwing close is never retried.
        owned?.Dispose();
    }

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
        if (aborted) throw new InvalidOperationException("ZIP was aborted.");
    }
    private void CheckWrite(int count, CancellationToken caller = default)
    {
        CheckState(caller);
        var position = underlying.Position;
        if (position > long.MaxValue - count) throw outputTooLarge();
        checkLength(Math.Max(underlying.Length, position + count));
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
        checkLength(value);
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
