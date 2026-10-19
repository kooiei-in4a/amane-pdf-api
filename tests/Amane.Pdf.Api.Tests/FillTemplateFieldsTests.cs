using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class FillTemplateFieldsTests
{
    internal const string Field = """{"page":1,"x":0.1,"y":0.2,"width":0.8,"height":0.6,"fontSize":12,"align":"left","text":"日本語"}""";
    internal static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
    internal static FillTemplateFields Parse(string field = Field) => FillTemplateFields.Parse(Bytes("[" + field + "]"));
    internal static string Change(string key, object? value)
    {
        var field = JsonNode.Parse(Field)!;
        field[key] = JsonSerializer.SerializeToNode(value);
        return field.ToJsonString();
    }
    internal static IEnumerable<string> InvalidFields()
    {
        foreach (var key in new[] { "page", "x", "y", "width", "height", "fontSize", "align", "text" })
        {
            var missing = JsonNode.Parse(Field)!.AsObject(); missing.Remove(key);
            yield return "[" + missing.ToJsonString() + "]";
            yield return "[" + Field[..^1] + ",\"" + key + "\":" + JsonNode.Parse(Field)![key]!.ToJsonString() + "}]";
            yield return "[" + Change(key, null) + "]";
        }
        yield return "[]"; yield return "{}"; yield return "null"; yield return "[[]]";
        yield return "[" + Field[..^1] + ",\"unknown\":1}]";
        foreach (var value in new[] { "0", "-1", "1.0", "1e0", "01", "2147483648", "\"1\"" })
            yield return "[" + Field.Replace("\"page\":1", "\"page\":" + value) + "]";
        foreach (var key in new[] { "x", "y", "width", "height" })
            foreach (var value in new[] { "-0", "-0.1", "1.0001", "0.00001", "1e-1", "\"0.1\"", "true", "[0]" })
                yield return "[" + JsonNode.Parse(Field)!.AsObject().Where(x => x.Key != key)
                    .Aggregate("{", (s, p) => s + JsonSerializer.Serialize(p.Key) + ":" + p.Value!.ToJsonString() + ",") +
                    JsonSerializer.Serialize(key) + ":" + value + "}]";
        foreach (var key in new[] { "width", "height" }) yield return "[" + Change(key, 0) + "]";
        foreach (var key in new[] { "x", "y" }) yield return "[" + Change(key, 1) + "]";
        foreach (var value in new[] { "5.9", "72.1", "6.00", "6e0", "-6", "\"12\"", "true" })
            yield return "[" + Field.Replace("\"fontSize\":12", "\"fontSize\":" + value) + "]";
        foreach (var align in new[] { "LEFT", "", "justify" }) yield return "[" + Change("align", align) + "]";
        foreach (var text in new[] { "", " \u00a0\u3000\n", "a\r\nb", "a\tb", "\0", "𠮷", "😀", "a\ufe0f", "%p", "%P", "%t", "%v", "a\\nb", "a\nb\nc\nd\ne\nf", new string('a', 201), "\u02dc", "\u0303" })
            yield return "[" + Change("text", text) + "]";
        yield return "[" + Field.Replace("日本語", "\\ud800") + "]";
        yield return "[" + Field.Replace("日本語", "\\udc00") + "]";
        yield return "[" + string.Join(',', Enumerable.Repeat(Field, 201)) + "]";
        yield return "[" + string.Join(',', Enumerable.Repeat(Field, 101)) + "]";
    }

    [TestMethod]
    public void StrictJson_RejectsInvalidContractsAndMalformedUtf8()
    {
        foreach (var value in InvalidFields())
            Assert.AreEqual(400, Assert.ThrowsExactly<BadHttpRequestException>(() => FillTemplateFields.Parse(Bytes(value)), value).StatusCode);
        foreach (var bytes in new[] { new byte[] { 0xef, 0xbb, 0xbf }.Concat(Bytes("[" + Field + "]")).ToArray(),
            new byte[] { 0xc0, 0xaf }, new byte[65537], Bytes("[[[[0]]]]"), Bytes("[" + Field + ",]"), Bytes("/*a*/[" + Field + "]") })
            Assert.ThrowsExactly<BadHttpRequestException>(() => FillTemplateFields.Parse(bytes));
    }

    [TestMethod]
    public void Text_PreservesWhitespaceNormalizationAndLiteralPercent()
    {
        // The table contains exactly these Unicode White_Space characters: SPACE,
        // NO-BREAK SPACE and IDEOGRAPHIC SPACE. LF is the only allowed separator.
        var spaces = Enumerable.Range(0, 65536).Select(c => (char)c)
            .Where(c => char.IsWhiteSpace(c) && FillTemplateGlyphs.TryGet(c, out _, out _)).ToArray();
        CollectionAssert.AreEqual(new[] { ' ', '\u00a0', '\u3000' }, spaces);
        foreach (var c in spaces) Assert.ThrowsExactly<BadHttpRequestException>(() => Parse(Change("text", $"{c}\n{c}")));
        foreach (var value in new[] { " \u00a0\u3000か\u3099\n", "%", "a%%", "%1%%%a", "\\", new string('a', 200), "\n\n日\n\n" })
            Assert.AreEqual(value, Parse(Change("text", value)).Items.Single().Text);
    }

    [TestMethod]
    public void NumericAndCountBoundaries_AreExact()
    {
        foreach (var size in new[] { 6m, 6.1m, 71.9m, 72m }) Assert.AreEqual(size, Parse(Change("fontSize", size)).Items.Single().FontSize);
        var field = JsonNode.Parse(Field)!; field["x"] = 0; field["width"] = 1; field["y"] = 0.9999m; field["height"] = 0.0001m;
        Assert.AreEqual(0.9999m, Parse(field.ToJsonString()).Items.Single().Y);
        field["page"] = int.MaxValue;
        Assert.AreEqual(int.MaxValue, Parse(field.ToJsonString()).To);
        var many = Enumerable.Range(1, 200).Select(n => Change("page", n));
        Assert.AreEqual(200, FillTemplateFields.Parse(Bytes("[" + string.Join(',', many) + "]")).Items.Count);
        Assert.AreEqual(100, FillTemplateFields.Parse(Bytes("[" + string.Join(',', Enumerable.Repeat(Field, 100)) + "]")).Items.Count);
        Assert.AreEqual((1, 1), Parse().ResolveRange(1));
        Assert.ThrowsExactly<BadHttpRequestException>(() => Parse().ResolveRange(0));
    }

    [TestMethod]
    public void Placement_FixedCoordinatesAndBaseline_AndBlankLines()
    {
        var field = new FillTemplateField(1, 0.1m, 0.2m, 0.8m, 0.6m, 12, "left", "日本\n語");
        foreach (var align in new[] { "left", "center", "right" })
        {
            var lines = FillTemplateLayout.Place(field with { Align = align }, new(600, 800));
            Assert.AreEqual(2, lines.Count);
            Assert.IsNull(lines[0].Anchor);
            // Box: (60,640), 480x480. Block height 26.4; top 413.2;
            // baseline 402.64140625; pdfcpu pos.y = baseline - ceil(1.44140625).
            Assert.AreEqual(400.64140625, lines[0].Y, 1e-10);
            Assert.AreEqual(386.24140625, lines[1].Y, 1e-10);
            var width = 24d;
            Assert.AreEqual(align switch { "left" => 60.6, "center" => 288, _ => 515.4 }, lines[0].X, 1e-10);
            Assert.AreEqual(width, lines[0].Value.Sum(c => { FillTemplateGlyphs.TryGet(c, out _, out var a); return a; }) * 12d / 1000);
        }
        var blank = FillTemplateLayout.Place(field with { Text = "日\n\n語" }, new(600, 800));
        Assert.AreEqual(2, blank.Count);
        Assert.AreEqual(28.8, blank[0].Y - blank[1].Y, 1e-10);
    }

    [TestMethod]
    public void Overflow_ExactFitAndPointOnePtOverflow_UseDecimal()
    {
        var field = new FillTemplateField(1, 0, 0, 0.5m, 0.5m, 12, "left", "日");
        // Width 13.2 = glyph 12 + margin 1.2; height 12 exactly.
        Assert.AreEqual(1, FillTemplateLayout.Place(field, new(26.4, 24)).Count);
        foreach (var size in new[] { new PdfcpuPageSize(26.2, 24), new PdfcpuPageSize(26.4, 23.8), new PdfcpuPageSize(26.399999, 24) })
            Assert.AreEqual("field-overflow", Assert.ThrowsExactly<PdfFillTemplateException>(() => FillTemplateLayout.Place(field, size)).Code);
    }

    [TestMethod]
    public void Cmap_UsesOriginalPercentCombiningMarksAndBlocksOfAtMost100()
    {
        var chars = Enumerable.Range(0x4e00, 2000).Select(c => (char)c).Where(c => FillTemplateGlyphs.TryGet(c, out _, out _)).Take(201).ToArray();
        var json = "[" + string.Join(',', chars.Chunk(100).Select(chunk => Change("text", new string(chunk)))) + "," + Change("text", "%か\u3099") + "]";
        var map = FillTemplateUnicode.CMap(FillTemplateFields.Parse(Bytes(json)), default);
        Assert.AreEqual(3, System.Text.RegularExpressions.Regex.Matches(map, " beginbfchar").Count);
        foreach (var c in chars.Concat("%か\u3099"))
        {
            FillTemplateGlyphs.TryGet(c, out var gid, out _);
            StringAssert.Contains(map, $"<{gid:X4}> <{(int)c:X4}>");
        }
    }
}
