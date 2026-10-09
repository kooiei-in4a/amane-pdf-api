namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class CleanCapacityTests
{
    [TestMethod]
    public void Reservations_RoundEveryFileAndReturnActualAllocationAfterWriting()
    {
        var capacity = new CleanCapacity(124 * 1048576);
        capacity.Reserve("input", 50 * 1048576);
        var json = capacity.ReserveWrite("json", 32 * 1048576);
        Assert.AreEqual(32 * 1048576L, json);
        capacity.Reserve("json", 1);
        Assert.AreEqual(50 * 1048576L + 4096, capacity.Used);
        Assert.AreEqual(32 * 1048576L, capacity.ReserveWrite("update", 32 * 1048576));
        Assert.AreEqual(42 * 1048576L - 4096, capacity.ReserveWrite("output", 54 * 1048576));
        Assert.Throws<PdfCleanTooComplexException>(() => capacity.ReserveWrite("extra", 1));
    }

    [TestMethod]
    public async Task BoundedWriter_RejectsBeforeDiskWriteAndHonorsCancellation()
    {
        using var underlying = new MemoryStream();
        using var stream = new CleanLimitedWriteStream(underlying, 4);
        stream.Write([1, 2, 3]);
        await stream.WriteAsync(new byte[] { 4 });
        Assert.Throws<PdfCleanTooComplexException>(() => stream.WriteByte(5));
        Assert.AreEqual(4L, underlying.Length);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await stream.WriteAsync(new byte[] { 5 }, cancel.Token));
    }

    [TestMethod]
    public void Startup_RejectsInvalidLimitsOverflowAndJobSmallerThanRoundedUpload()
    {
        Assert.IsTrue(CleanCapacity.IsValid(new()));
        Assert.IsFalse(CleanCapacity.IsValid(new() { CleanJsonLimitBytes = 0 }));
        Assert.IsFalse(CleanCapacity.IsValid(new() { CleanJsonLimitBytes = (long)int.MaxValue + 1 }));
        Assert.IsFalse(CleanCapacity.IsValid(new() { CleanOutputLimitBytes = 0 }));
        Assert.IsFalse(CleanCapacity.IsValid(new() { CleanOutputLimitBytes = long.MaxValue }));
        Assert.IsFalse(CleanCapacity.IsValid(new() { CleanJobLimitBytes = long.MaxValue }));
        Assert.IsFalse(CleanCapacity.IsValid(new() { CleanJobLimitBytes = 50 * 1048576 - 1 }));
        Assert.IsTrue(CleanCapacity.IsValid(new() { MaxFileBytes = 4097, CleanJobLimitBytes = 8192 }));
        Assert.IsFalse(CleanCapacity.IsValid(new() { MaxFileBytes = 4097, CleanJobLimitBytes = 8191 }));
    }
}
