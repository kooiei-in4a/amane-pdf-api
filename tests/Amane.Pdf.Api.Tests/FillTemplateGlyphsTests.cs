using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class FillTemplateGlyphsTests
{
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "third_party/pdfcpu.lock"))) return directory.FullName;
        throw new AssertFailedException("Repository root not found.");
    }

    [TestMethod]
    public void GeneratedDataMatchesHashesAndLookup()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src/Amane.Pdf.Api/FillTemplateGlyphs.g.cs"));
        using var locked = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "third_party/pdfcpu.lock")));
        Assert.AreEqual(locked.RootElement.GetProperty("font_sha256").GetString(),
            Regex.Match(source, @"// font_sha256: ([0-9a-f]{64})").Groups[1].Value);
        var expression = Regex.Match(source, @"Glyphs = Convert\.FromBase64String\(([\s\S]*?)\);").Groups[1].Value;
        var encoded = string.Concat(Regex.Matches(expression, "\"([A-Za-z0-9+/=]+)\"").Select(m => m.Groups[1].Value));
        var bytes = Convert.FromBase64String(encoded);
        Assert.AreEqual(encoded, Convert.ToBase64String(bytes));
        Assert.AreEqual(Regex.Match(source, @"// data_sha256: ([0-9a-f]{64})").Groups[1].Value,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.AreEqual("11425", Regex.Match(source, @"// entries: (\d+)").Groups[1].Value);
        Assert.AreEqual(11425 * 6, bytes.Length);
        var previous = -1;
        var gids = new HashSet<ushort>();
        for (var offset = 0; offset < bytes.Length; offset += 6)
        {
            var scalar = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
            var gid = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2));
            var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4));
            Assert.IsTrue(scalar > previous);
            previous = scalar;
            Assert.IsTrue(gid != 0 && gids.Add(gid), $"U+{scalar:X}");
            Assert.IsTrue(width is >= 1 and <= 1000, $"U+{scalar:X}");
            Assert.IsTrue(FillTemplateGlyphs.TryGet(scalar, out var actualGid, out var actualWidth));
            Assert.AreEqual(gid, actualGid);
            Assert.AreEqual(width, actualWidth);
        }
        Assert.AreEqual(11425, Enumerable.Range(0, 0x10000).Count(c => FillTemplateGlyphs.TryGet(c, out _, out _)));
    }

    [TestMethod]
    public void ExcludedScalarsAreAbsent()
    {
        var scalars = new[] { -1, 0x110000, 0x02dc, 0x0303, 0x20bb7, 0x1f600, 0x29fce, 0x29fd7 }
            .Concat(Enumerable.Range(0, 0x20)).Concat(Enumerable.Range(0x7f, 0x21))
            .Concat(Enumerable.Range(0xfe00, 16)).Concat(Enumerable.Range(0xd800, 0x800));
        foreach (var scalar in scalars)
        {
            Assert.IsFalse(FillTemplateGlyphs.TryGet(scalar, out var gid, out var advance), $"U+{scalar:X}");
            Assert.AreEqual((ushort)0, gid);
            Assert.AreEqual((ushort)0, advance);
        }
    }

    [TestMethod]
    public void RequiredSymbolsAndSpecialCharactersArePresent()
    {
        foreach (var c in "①㈱￥¥※〒×−№﨑%\\\u3000\u3099\u00ad\u00a0")
            Assert.IsTrue(FillTemplateGlyphs.TryGet(c, out _, out _), $"U+{(int)c:X}");
    }

    [TestMethod]
    [DataRow(0x0020, 1256, 333)] [DataRow(0x0025, 1262, 1000)]
    [DataRow(0x0041, 1291, 759)] [DataRow(0x005c, 1323, 500)]
    [DataRow(0x0069, 1338, 319)] [DataRow(0x3042, 1409, 979)]
    [DataRow(0xff71, 12434, 500)] [DataRow(0x2460, 1170, 1000)]
    [DataRow(0xfa11, 5084, 1000)] [DataRow(0x3099, 13811, 1000)]
    public void RepresentativeGlyphsHaveIndependentExpectedValues(int scalar, int expectedGid, int expectedAdvance)
    {
        Assert.IsTrue(FillTemplateGlyphs.TryGet(scalar, out var gid, out var advance));
        Assert.AreEqual(expectedGid, (int)gid);
        Assert.AreEqual(expectedAdvance, (int)advance);
    }

    [TestMethod]
    public void ExistingWatermarkCoverageAndAsciiWidthsAgree()
    {
        foreach (var scalar in Enumerable.Range(0, 0x10000).Where(WatermarkText.IsDrawable))
            Assert.IsTrue(FillTemplateGlyphs.TryGet(scalar, out _, out _), $"U+{scalar:X}");
        for (var i = 0; i < WatermarkGlyphs.AsciiAdvances.Length; i++)
        {
            Assert.IsTrue(FillTemplateGlyphs.TryGet(i + 0x20, out _, out var advance));
            Assert.AreEqual(WatermarkGlyphs.AsciiAdvances[i] * 1000 / 2048, (int)advance);
        }
    }
}
