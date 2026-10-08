namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfSplitCapacityTests
{
    private const long MiB = 1048576;

    [TestMethod]
    [DataRow(30, 20, true)]
    [DataRow(20, 30, false)]
    [DataRow(25, 25, false)]
    [DataRow(40, 10, false)]
    [DataRow(27, 23, false)]
    public void PartOrder_IncludesTheCopyAndAllocationBoundary(int first, int second, bool accepted)
    {
        var capacity = new PdfSplitCapacity(50 * MiB, 50 * MiB, 124 * MiB);
        var zip = 0L;
        var result = true;
        try
        {
            foreach (var size in new[] { first * MiB, second * MiB })
            {
                if (size > capacity.PartBudget(zip)) throw new PdfSplitOutputTooLargeException();
                capacity.AcceptPart(size);
                zip += size + 200;
                capacity.CheckZipLength(zip);
                capacity.ReleasePart();
            }
        }
        catch (PdfSplitOutputTooLargeException) { result = false; }
        Assert.AreEqual(accepted, result);
    }

    [TestMethod]
    public void PdfAndZipLimits_AcceptEqualityAndRejectOneExtraByte()
    {
        var capacity = new PdfSplitCapacity(1, 10, 2 * MiB, 100);
        capacity.AcceptPart(10);
        Assert.Throws<PdfSplitOutputTooLargeException>(() => capacity.AcceptPart(1));
        capacity.CheckZipLength(110);
        Assert.Throws<PdfSplitOutputTooLargeException>(() => capacity.CheckZipLength(111));
        Assert.Throws<PdfSplitOutputTooLargeException>(() => capacity.PartBudget(110));
    }

    [TestMethod]
    public void JobLimit_RoundsAllThreeFilesAndReservesOverflowSentinel()
    {
        var minimum = 4096 + MiB + 3 * 4096;
        var capacity = new PdfSplitCapacity(1, 100000, minimum);
        Assert.AreEqual(4096L, capacity.PartBudget(0));
        capacity.AcceptPart(4097);
        Assert.Throws<PdfSplitOutputTooLargeException>(() => new PdfSplitCapacity(1, 100000, minimum - 1).PartBudget(0));
        var exact = new PdfSplitCapacity(1, 100000, 4 * 4096, 100000);
        exact.AcceptPart(4097);
        exact.CheckZipLength(4096);
        Assert.Throws<PdfSplitOutputTooLargeException>(() => exact.CheckZipLength(4097));
    }

    [TestMethod]
    public void StartupValidation_IncludesZip32AndOverflow()
    {
        var options = new PdfOptions();
        Assert.IsTrue(PdfSplitCapacity.IsValid(options));
        options.MaxSplitOutputBytes = PdfSplitCapacity.MaxZip32PdfBytes;
        Assert.IsTrue(PdfSplitCapacity.IsValid(options));
        options.MaxSplitOutputBytes++;
        Assert.IsFalse(PdfSplitCapacity.IsValid(options));
        options.MaxSplitOutputBytes = 1;
        options.MaxSplitJobBytes = PdfSplitCapacity.Allocated(options.MaxFileBytes) + MiB + 3 * 4096;
        Assert.IsTrue(PdfSplitCapacity.IsValid(options));
        options.MaxSplitJobBytes--;
        Assert.IsFalse(PdfSplitCapacity.IsValid(options));
        options.MaxFileBytes = long.MaxValue;
        options.MaxSplitJobBytes = long.MaxValue;
        Assert.IsFalse(PdfSplitCapacity.IsValid(options));
        foreach (var parts in new[] { 1, 501 })
            Assert.IsFalse(PdfSplitCapacity.IsValid(new() { MaxSplitParts = parts }));
        Assert.IsFalse(PdfSplitCapacity.IsValid(new() { MaxSplitOutputBytes = 0 }));
        Assert.IsFalse(PdfSplitCapacity.IsValid(new() { MaxSplitJobBytes = long.MaxValue }));
    }
}
