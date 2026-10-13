using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class WatermarkTextTests
{
    [TestMethod]
    [DataRow(0x001f, false)] [DataRow(0x0020, true)] [DataRow(0x0024, true)]
    [DataRow(0x0025, false)] [DataRow(0x0026, true)] [DataRow(0x005b, true)]
    [DataRow(0x005c, false)] [DataRow(0x005d, true)] [DataRow(0x007e, true)]
    [DataRow(0x007f, false)] [DataRow(0x2fff, false)] [DataRow(0x3000, true)]
    [DataRow(0x303e, false)] [DataRow(0x303f, false)] [DataRow(0x3040, false)]
    [DataRow(0x3041, true)] [DataRow(0x30ff, true)] [DataRow(0x3100, false)]
    [DataRow(0x4dff, false)] [DataRow(0x4e00, true)] [DataRow(0x9fa0, true)]
    [DataRow(0x9fff, false)] [DataRow(0xa000, false)] [DataRow(0xff00, false)]
    [DataRow(0xff01, true)] [DataRow(0xff5e, true)] [DataRow(0xff5f, false)]
    [DataRow(0xff60, false)] [DataRow(0xff61, true)] [DataRow(0xff9f, true)]
    [DataRow(0xffa0, false)] [DataRow(-1, false)] [DataRow(0x110000, false)]
    public void GlyphAndRangeBoundaries(int scalar, bool accepted)
        => Assert.AreEqual(accepted, WatermarkText.IsDrawable(scalar), $"U+{scalar:X}");

    [TestMethod]
    [DataRow("")] [DataRow(" ")] [DataRow("　")] [DataRow(" 　 ")]
    [DataRow("%")] [DataRow("\\")] [DataRow("\t")] [DataRow("\n")] [DataRow("\r")]
    [DataRow("\0")] [DataRow("\u007f")] [DataRow("\u0085")] [DataRow("😀")]
    [DataRow("𠮷")] [DataRow("\ufeff")] [DataRow("é")] [DataRow("한")]
    [DataRow("丢")] [DataRow("〰")] [DataRow("﨑")] [DataRow("①")]
    public void InvalidTextHasFixedMessage(string text)
    {
        var exception = Assert.ThrowsExactly<BadHttpRequestException>(() => WatermarkText.Parse(Encoding.UTF8.GetBytes(text)));
        Assert.AreEqual("Invalid watermark text.", exception.Message);
    }

    [TestMethod]
    [DataRow("C0AF")] [DataRow("EDA080")] [DataRow("F4908080")]
    [DataRow("E381")] [DataRow("80")] [DataRow("FF")]
    public void InvalidUtf8IsRejected(string hex)
        => Assert.ThrowsExactly<BadHttpRequestException>(() => WatermarkText.Parse(Convert.FromHexString(hex)));

    [TestMethod]
    [DataRow("-DRAFT, 社外秘")] [DataRow("  見本　")]
    [DataRow("か\u3099")] [DataRow("ｶﾞ")] [DataRow("％＼")]
    public void AcceptedTextIsNotTrimmedOrNormalized(string text)
        => Assert.AreEqual(text, WatermarkText.Parse(Encoding.UTF8.GetBytes(text)));

    [TestMethod]
    public void ByteAndTextElementLimitsIncludeCombiningMarks()
    {
        var ascii = new string('A', 30);
        Assert.AreEqual(ascii, WatermarkText.Parse(Encoding.UTF8.GetBytes(ascii)));
        Assert.ThrowsExactly<BadHttpRequestException>(() => WatermarkText.Parse(Encoding.UTF8.GetBytes(ascii + "A")));
        foreach (var text in new[] { string.Concat(Enumerable.Repeat("か\u3099", 20)), string.Concat(Enumerable.Repeat("A\u3099", 30)) })
        {
            Assert.AreEqual(120, Encoding.UTF8.GetByteCount(text));
            Assert.AreEqual(text, WatermarkText.Parse(Encoding.UTF8.GetBytes(text)));
            Assert.ThrowsExactly<BadHttpRequestException>(() => WatermarkText.Parse(Encoding.UTF8.GetBytes(text + "A")));
        }
        Assert.AreEqual(1, StringInfo.ParseCombiningCharacters("か\u3099").Length);
        Assert.AreEqual(1, StringInfo.ParseCombiningCharacters("ｶﾞ").Length);
    }

    [TestMethod]
    public void WidthUsesLockedAsciiAdvancesAndCountsEveryRune()
    {
        Assert.AreEqual(7618 / 2048.0, WatermarkText.EmWidth("DRAFT"));
        Assert.AreEqual(1.0, WatermarkText.EmWidth("ｶﾞ"));
        Assert.AreEqual(3.0, WatermarkText.EmWidth("社外秘"));
        Assert.AreEqual(2.0, WatermarkText.EmWidth("か\u3099"));
    }

    [TestMethod]
    public void FontTableHasExpectedCoverageIncludingJisKanji()
    {
        Assert.AreEqual(10051, Enumerable.Range(0, 0x10000).Count(WatermarkText.IsDrawable));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var jis = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (var scalar in Enumerable.Range(0x4e00, 0x9fff - 0x4e00 + 1))
        {
            try { _ = jis.GetBytes(char.ConvertFromUtf32(scalar)); }
            catch (EncoderFallbackException) { continue; }
            Assert.IsTrue(WatermarkText.IsDrawable(scalar), $"JIS kanji U+{scalar:X}");
        }
    }

    [TestMethod]
    public void DiagonalFitsRotatedAndUnrotatedBoundsUnlessClampedToOnePoint()
    {
        foreach (var page in new[] { new PdfcpuPageSize(595.28, 841.89), new(841.89, 595.28), new(283.46, 419.53),
                     new(24, 32), new(14400, 14400), new(200, 2000) })
        foreach (var text in new[] { "社外秘", "DRAFT", new string('W', 30), string.Concat(Enumerable.Repeat("A\u3099", 30)) })
        {
            var em = WatermarkText.EmWidth(text);
            var element = new WatermarkOptions(true, "#808080").Element(page, text, em);
            Assert.AreEqual("c", element.Anchor);
            Assert.AreEqual(Math.Atan2(page.Height, page.Width) * 180 / Math.PI, element.Rotation, 0.00005);
            Assert.IsTrue(element.FontSize is >= 1 and < 14400);
            if (element.FontSize == 1) continue;
            var angle = element.Rotation * Math.PI / 180;
            Assert.IsTrue(element.FontSize * (em * Math.Cos(angle) + Math.Sin(angle)) <= 0.8 * page.Width + 0.01);
            Assert.IsTrue(element.FontSize * (em * Math.Sin(angle) + Math.Cos(angle)) <= 0.8 * page.Height + 0.01);
            Assert.IsTrue(element.FontSize * em <= 0.95 * page.Width);
            Assert.IsTrue(element.FontSize <= 0.95 * page.Height);
        }
    }

    [TestMethod]
    public void CornerShrinksOnNarrowPagesAndUsesPhysicalMargin()
    {
        var options = new WatermarkOptions(false, "#C00000");
        foreach (var width in new[] { 24.0, 283.46, 419.53, 595.28, 14400 })
        {
            var text = new string('語', 30);
            var element = options.Element(new(width, 842), text, 30);
            Assert.AreEqual(Math.Clamp(Math.Floor((width - 56) / 30 * 100) / 100, 1, 14), element.FontSize);
            Assert.AreEqual(new PdfcpuText(text, Anchor: "tr", Dx: -28, Dy: -28, FontSize: element.FontSize, Color: "#C00000"), element);
        }
    }
}
