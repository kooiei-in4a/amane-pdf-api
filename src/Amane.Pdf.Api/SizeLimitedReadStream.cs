namespace Amane.Pdf.Api;

// Counts actual request bytes, including requests without Content-Length. Leaves the body open.
internal sealed class SizeLimitedReadStream(Stream body, long limit) : Stream
{
    private long bytesRead;
    public override bool CanRead => body.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await body.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - bytesRead + 1)], cancellationToken);
        Count(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = body.Read(buffer, offset, (int)Math.Min(count, limit - bytesRead + 1));
        Count(read);
        return read;
    }

    private void Count(int count)
    {
        bytesRead += count;
        if (bytesRead > limit) throw new BadHttpRequestException("Request size limit exceeded.", 413);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
