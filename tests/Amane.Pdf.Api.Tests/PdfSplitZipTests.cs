using System.Buffers.Binary;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfSplitZipTests
{
    private static readonly Func<Stream, Task>[] Mutations =
    [
        s => { s.Write(new byte[3], 0, 3); return Task.CompletedTask; },
        s => { s.Write(new byte[3].AsSpan()); return Task.CompletedTask; },
        s => s.WriteAsync(new byte[3], 0, 3, CancellationToken.None),
        s => s.WriteAsync(new byte[3].AsMemory()).AsTask(),
        s => { s.WriteByte(1); return Task.CompletedTask; },
        s => { s.SetLength(3); return Task.CompletedTask; },
        s => { s.Seek(0, SeekOrigin.Begin); return Task.CompletedTask; },
        s => { s.Position = 0; return Task.CompletedTask; },
        s => { s.Flush(); return Task.CompletedTask; },
        s => s.FlushAsync()
    ];

    [TestMethod]
    public async Task Abort_RejectsEveryMutationWithoutTouchingUnderlyingStream()
    {
        using var raw = new FaultStream();
        var stream = new PdfSplitZipStream(raw, new(1, 10, 1048576, 2), CancellationToken.None);
        stream.Abort();
        foreach (var operation in Mutations)
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation(stream));
        Assert.AreEqual(0, raw.Mutations);
        Assert.IsFalse(raw.Closed);
    }

    [TestMethod]
    public async Task Cancellation_PrecedesCapacityForEveryMutation()
    {
        using var raw = new FaultStream();
        using var cancellation = new CancellationTokenSource();
        var stream = new PdfSplitZipStream(raw, new(1, 10, 1048576, 0), cancellation.Token);
        cancellation.Cancel();
        foreach (var operation in Mutations)
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation(stream));
        Assert.AreEqual(0, raw.Mutations);
    }

    [TestMethod]
    public async Task EveryWriteOverloadAndSetLength_EnforcesCapacityBeforeWriting()
    {
        foreach (var operation in Mutations.Take(6))
        {
            using var raw = new FaultStream();
            var stream = new PdfSplitZipStream(raw, new(1, 10, 1048576, 0), CancellationToken.None);
            await Assert.ThrowsAsync<PdfSplitOutputTooLargeException>(() => operation(stream));
            Assert.AreEqual(0L, raw.Length);
            Assert.AreEqual(0, raw.Mutations);
        }
    }

    [TestMethod]
    public void HeaderRewrites_UseFileLengthInsteadOfCumulativeWrites()
    {
        using var raw = new MemoryStream();
        var stream = new PdfSplitZipStream(raw, new(1, 10, 1048576, 3), CancellationToken.None);
        stream.Write(new byte[3]);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(new byte[3]);
        Assert.AreEqual(3L, raw.Length);
        Assert.Throws<PdfSplitOutputTooLargeException>(() => stream.WriteByte(1));
    }

    [TestMethod]
    [DataRow("data", false)]
    [DataRow("seek", false)]
    [DataRow("central", false)]
    [DataRow("data", true)]
    [DataRow("close", false)]
    public async Task ZipFailures_PreservePrimaryExceptionCloseHandlesAndNeverFinalizeAgain(string stage, bool closeAlsoFails)
    {
        await using var test = new PdfTestContext();
        using var files = new TemporaryPdfFiles(test.Root);
        await File.WriteAllBytesAsync(files.InputPath, PdfTestContext.Fixture);
        var options = test.Factory.Services.GetService(typeof(IOptions<PdfOptions>)) as IOptions<PdfOptions>;
        var processor = new PdfSplitProcessor(new(options!), options!.Value, files);
        var raw = new FaultStream { Stage = stage, CloseFails = closeAlsoFails || stage == "close" };
        var thrown = await Assert.ThrowsAsync<IOException>(() => processor.WriteZipAsync(raw,
            [new(1, 1, 1), new(2, 1, 1)], CancellationToken.None));
        Assert.AreSame(stage == "close" ? raw.CloseFailure : raw.Primary, thrown);
        Assert.IsTrue(raw.Closed);
        Assert.AreEqual(1, raw.DisposeCalls);
        Assert.AreEqual(1, raw.Faults);
        foreach (var path in Directory.GetFiles(files.DirectoryPath))
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [TestMethod]
    public async Task ZipOverheadFailure_IsCapacityFailureAndClosesTheStream()
    {
        await using var test = new PdfTestContext();
        using var files = new TemporaryPdfFiles(test.Root);
        await File.WriteAllBytesAsync(files.InputPath, PdfTestContext.Fixture);
        var options = (IOptions<PdfOptions>)test.Factory.Services.GetService(typeof(IOptions<PdfOptions>))!;
        var raw = new FaultStream();
        var processor = new PdfSplitProcessor(new(options), options.Value, files);
        await Assert.ThrowsAsync<PdfSplitOutputTooLargeException>(() => processor.WriteZipAsync(raw,
            [new(1, 1, 1), new(2, 1, 1)], CancellationToken.None, zipOverheadBytes: 1));
        Assert.IsTrue(raw.Closed);
    }

    [TestMethod]
    [DataRow("data")]
    [DataRow("seek")]
    [DataRow("central")]
    public async Task CancelledCopyOrFinalization_PreservesCancellationDespiteCloseFailure(string stage)
    {
        await using var test = new PdfTestContext();
        using var files = new TemporaryPdfFiles(test.Root);
        await File.WriteAllBytesAsync(files.InputPath, PdfTestContext.Fixture);
        using var cancellation = new CancellationTokenSource();
        var raw = new FaultStream { Stage = stage, CloseFails = true, CancelOnFault = cancellation };
        var options = (IOptions<PdfOptions>)test.Factory.Services.GetService(typeof(IOptions<PdfOptions>))!;
        await Assert.ThrowsAsync<OperationCanceledException>(() => new PdfSplitProcessor(new(options), options.Value, files)
            .WriteZipAsync(raw, [new(1, 1, 1), new(2, 1, 1)], cancellation.Token));
        Assert.IsTrue(raw.Closed);
        Assert.AreEqual(1, raw.Faults);
        Assert.AreEqual(1, raw.DisposeCalls);
    }

    private sealed class FaultStream : MemoryStream
    {
        internal string Stage = "";
        internal bool CloseFails;
        internal CancellationTokenSource? CancelOnFault;
        internal bool Closed;
        internal int DisposeCalls, Mutations, Faults;
        internal readonly IOException Primary = new("Synthetic ZIP failure.");
        internal readonly IOException CloseFailure = new("Synthetic close failure.");
        public override long Position { get => base.Position; set { Mutations++; base.Position = value; } }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Write(buffer.AsSpan(offset, count)); return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Write(buffer.Span); return ValueTask.CompletedTask; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Mutations++;
            if ((Stage == "data" && buffer.Length > 100) ||
                (Stage == "central" && buffer.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 0x02014b50))
            { Fail(); }
            var data = buffer.ToArray();
            base.Write(data, 0, data.Length);
        }
        public override void WriteByte(byte value) { Mutations++; base.WriteByte(value); }
        public override void SetLength(long value) { Mutations++; base.SetLength(value); }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Mutations++;
            if (Stage == "seek") Fail();
            return base.Seek(offset, origin);
        }
        public override void Flush() { Mutations++; base.Flush(); }
        private void Fail()
        {
            Faults++;
            CancelOnFault?.Cancel();
            CancelOnFault?.Token.ThrowIfCancellationRequested();
            throw Primary;
        }
        protected override void Dispose(bool disposing)
        {
            DisposeCalls++; Closed = true;
            base.Dispose(disposing);
            if (CloseFails) { if (Stage == "close") Faults++; throw CloseFailure; }
        }
    }
}
