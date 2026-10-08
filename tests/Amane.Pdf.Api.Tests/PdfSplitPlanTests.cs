using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfSplitPlanTests
{
    internal static PdfSplitPlan Parse(string query, int maxParts = 100)
        => PdfSplitPlan.Parse(new QueryCollection(QueryHelpers.ParseQuery(query)), maxParts);

    [TestMethod]
    public void Every_UsesShortFinalPartAndInvariantNames()
    {
        var parts = Parse("EVERY=3").CreateParts(4);
        CollectionAssert.AreEqual(new[] { new SplitPart(1, 1, 3), new SplitPart(2, 4, 4) }, parts.ToArray());
        Assert.AreEqual("part-001_p1-3.pdf", parts[0].EntryName);
        Assert.AreEqual("part-002_p4.pdf", parts[1].EntryName);
        Assert.AreEqual("1-3", parts[0].PageRange);
    }

    [TestMethod]
    public void Ranges_PreservesOrderAndOmittedPages()
        => CollectionAssert.AreEqual(new[] { new SplitPart(1, 4, 4), new SplitPart(2, 1, 2) },
            Parse("ranges=4,1-2").CreateParts(4).ToArray());

    [TestMethod]
    [DataRow(2, 100, true)]
    [DataRow(100, 100, true)]
    [DataRow(101, 100, false)]
    [DataRow(500, 500, true)]
    [DataRow(501, 500, false)]
    [DataRow(1, 100, false)]
    [DataRow(int.MaxValue, 500, false)]
    public void PartCount_IsBoundedBeforeAllocating(int pages, int maximum, bool valid)
    {
        var plan = Parse("every=1", maximum);
        if (valid) Assert.AreEqual(pages, plan.CreateParts(pages).Count);
        else Assert.Throws<BadHttpRequestException>(() => plan.CreateParts(pages));
    }

    [TestMethod]
    public void MaximumPageNumber_DoesNotOverflow()
    {
        var parts = Parse("ranges=2147483647,1-2147483646").CreateParts(int.MaxValue);
        Assert.AreEqual("2147483647", parts[0].PageRange);
        Assert.AreEqual("1-2147483646", parts[1].PageRange);
        Assert.Throws<BadHttpRequestException>(() => Parse("every=99999", 500).CreateParts(int.MaxValue));
        Assert.AreEqual(500, Parse("every=99999", 500).CreateParts(49_999_500).Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("other=1")]
    [DataRow("every=2&ranges=1,2")]
    [DataRow("every=2&Every=3")]
    [DataRow("every=")]
    [DataRow("every=0")]
    [DataRow("every=01")]
    [DataRow("every=100000")]
    [DataRow("every=-1")]
    [DataRow("every=%2B1")]
    [DataRow("every=%EF%BC%91")]
    [DataRow("ranges=1")]
    [DataRow("ranges=1,1")]
    [DataRow("ranges=1-2,2-3")]
    [DataRow("ranges=2-1,3")]
    [DataRow("ranges=01,2")]
    [DataRow("ranges=1,%202")]
    [DataRow("ranges=1,")]
    public void InvalidSyntax_IsRejected(string query) => Assert.Throws<BadHttpRequestException>(() => Parse(query));

    [TestMethod]
    public void Ranges_CountLengthAndPageBoundsAreChecked()
    {
        Assert.Throws<BadHttpRequestException>(() => Parse("ranges=" + string.Join(',', Enumerable.Range(1, 101))));
        Assert.AreEqual(500, Parse("ranges=" + string.Join(',', Enumerable.Range(1, 500)), 500).CreateParts(500).Count);
        Assert.Throws<BadHttpRequestException>(() => Parse("ranges=" + new string('1', 4097)));
        Assert.Throws<BadHttpRequestException>(() => Parse("ranges=1,5").CreateParts(4));
        Assert.Throws<BadHttpRequestException>(() => Parse("every=99999").CreateParts(4));
    }
}
