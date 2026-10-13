using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class StampPlacementTests
{
    [TestMethod]
    public void Query_RequiresAsciiBoundedNumbersAndUniqueKnownKeys()
    {
        string[] invalid = ["", "page=1&x=0&y=0", "page=0&x=0&y=0&width=0.1",
            "page=01&x=0&y=0&width=0.1", "page=2147483648&x=0&y=0&width=0.1",
            "page=١&x=0&y=0&width=0.1", "page=1&x=0&y=0&width=0.1&X=0",
            "page=1&x=0&y=0&width=0.1&other=0"];
        foreach (var key in new[] { "x", "y", "width" })
            invalid = [.. invalid, .. new[] { "", ".5", "1.", "00.5", "+0.5", "-0", "0.5e1", "0,5",
                " 0.5", "0.12345", "0.١٢", "０.５", "1.0001" }.Select(v =>
                $"page=1&x={(key == "x" ? Uri.EscapeDataString(v) : "0")}&y={(key == "y" ? Uri.EscapeDataString(v) : "0")}&width={(key == "width" ? Uri.EscapeDataString(v) : "0.1")}")];
        foreach (var text in invalid) Assert.ThrowsExactly<BadHttpRequestException>(() => Parse(text), text);
        foreach (var width in new[] { "0", "0.0199", "0.5001", "1" })
            Assert.ThrowsExactly<BadHttpRequestException>(() => Parse("page=1&x=0&y=0&width=" + width));
        var query = Parse("PAGE=2147483647&x=0.7200&y=0.0800&width=0.1200");
        Assert.AreEqual((int.MaxValue, int.MaxValue), query.ResolveRange(int.MaxValue));
        Assert.ThrowsExactly<BadHttpRequestException>(() => query.ResolveRange(1));
    }

    [TestMethod]
    public void Placement_UsesDisplayedDimensionsAndKeepsAspectRatio()
    {
        foreach (var (w, h) in new[] { (600d, 800d), (800d, 600d), (1600d, 1200d), (300d, 400d) })
        {
            var place = StampPlacement.Calculate(new(1, 0.72m, 0.08m, 0.12m), new("unused", 300, 200), new(w, h));
            Assert.AreEqual(0.72 * w, place.Left, 1e-8);
            Assert.AreEqual(0.12 * w, place.Width, 1e-8);
            Assert.AreEqual(0.08 * w, place.Height, 1e-8);
            Assert.AreEqual(h * 0.92 - place.Height, place.Bottom, 1e-8);
            Assert.IsTrue(place.IsValid);
        }
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var placement = StampPlacement.Calculate(new(1, 0.88m, 0.88m, 0.12m), new("unused", 32, 32), new(600, 600));
            Assert.AreEqual(0, placement.Bottom, StampPlacement.Tolerance);
            StringAssert.Contains(placement.Description, "scale:2.25 abs, rot:0");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    public void Placement_RejectsOverflowAndExtremelyTallImages()
    {
        foreach (var query in new[] { new StampImageQuery(1, 0.8801m, 0, 0.12m), new(1, 0, 0.8801m, 0.12m),
                     new(1, 1, 0, 0.02m), new(1, 0, 1, 0.02m) })
            Assert.ThrowsExactly<BadHttpRequestException>(() => StampPlacement.Calculate(query, new("unused", 32, 32), new(600, 600)));
        Assert.ThrowsExactly<BadHttpRequestException>(() => StampPlacement.Calculate(new(1, 0, 0, 0.02m), new("unused", 1, 2000), new(600, 600)));
        var tiny = StampPlacement.Calculate(new(1, 0, 0, 0.02m), new("unused", 2000, 2000), new(0.000001, 0.000001));
        Assert.IsTrue(tiny.IsValid);
        Assert.IsFalse(tiny.Description.Contains("scale:0 abs"));
    }

    internal static StampImageQuery Parse(string query) => StampImageQuery.Parse(new QueryCollection(QueryHelpers.ParseQuery(query)));
}
