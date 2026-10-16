using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class RedactRegionsTests
{
    internal const string Region = """{"page":1,"x":0.1,"y":0.2,"width":0.3,"height":0.4}""";
    [TestMethod]
    public void RejectsMalformedEncodingJsonKeysNumbersAndBounds()
    {
        string[] invalid = ["", "{}", "[]", "[null]", "[1]", "[[[[1]]]]", "[//comment\n" + Region + "]",
            "[" + Region + ",]", "[" + Region.Replace("\"page\":1", "\"Page\":1") + "]",
            "[" + Region.Replace("\"page\":1", "\"page\":1,\"page\":2") + "]",
            "[" + Region.Replace(",\"x\":0.1", "") + "]", "[" + Region.Replace("\"page\":1", "\"page\":1,\"unknown\":0") + "]"];
        foreach (var page in new[] { "0", "-0", "-1", "1.0", "01", "1e0", "\"1\"", "2147483648", "99999999999", "true", "null" })
            invalid = [.. invalid, "[" + Region.Replace("\"page\":1", "\"page\":" + page) + "]"];
        foreach (var key in new[] { "x", "y", "width", "height" })
            foreach (var number in new[] { "-0", "-1", ".5", "1.", "00", "1e-1", "0.12345", "1.0001", "\"0.1\"", "null", "false", "0.١", "NaN", "Infinity" })
            {
                var original = key switch { "x" => "0.1", "y" => "0.2", "width" => "0.3", _ => "0.4" };
                invalid = [.. invalid, "[" + Region.Replace($"\"{key}\":{original}", $"\"{key}\":{number}") + "]"];
            }
        invalid = [.. invalid, "[" + Region.Replace("\"width\":0.3", "\"width\":0") + "]",
            "[" + Region.Replace("\"height\":0.4", "\"height\":0") + "]",
            "[" + Region.Replace("\"x\":0.1", "\"x\":0.8") + "]", "[" + Region.Replace("\"y\":0.2", "\"y\":1") + "]",
            "[" + string.Join(',', Enumerable.Repeat(Region, 101)) + "]",
            "[" + string.Join(',', Enumerable.Range(1, 21).Select(n => Region.Replace("\"page\":1", $"\"page\":{n}"))) + "]"];
        foreach (var json in invalid)
            Assert.ThrowsExactly<BadHttpRequestException>(() => RedactRegions.Parse(Encoding.UTF8.GetBytes(json)), json);
        foreach (var bytes in new[] { new byte[] { 0xff, 0xfe }, new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("[" + Region + "]")).ToArray(),
                     Encoding.UTF8.GetBytes("[" + Region + "]").Concat(new byte[] { 0xff }).ToArray(), Encoding.UTF8.GetBytes("[" + Region + "]" + new string(' ', 16384)) })
            Assert.ThrowsExactly<BadHttpRequestException>(() => RedactRegions.Parse(bytes));
    }

    [TestMethod]
    public void AcceptsExactBoundariesDuplicatesAndTwentyPages_UsingDecimal()
    {
        var json = """[{"page":2147483647,"x":0.0000,"y":0,"width":1.0000,"height":1}]""";
        var regions = RedactRegions.Parse(Encoding.UTF8.GetBytes(json));
        Assert.ThrowsExactly<BadHttpRequestException>(() => regions.CheckPageCount(1));
        regions.CheckPageCount(int.MaxValue);
        var hundred = "[" + string.Join(',', Enumerable.Range(0, 100).Select(n => Region.Replace("\"page\":1", $"\"page\":{n % 20 + 1}"))) + "]";
        var input = Encoding.UTF8.GetBytes(hundred.PadRight(16384));
        regions = RedactRegions.Parse(input);
        Assert.HasCount(100, regions.Items); Assert.HasCount(20, regions.Pages);
        regions = RedactRegions.Parse(Encoding.UTF8.GetBytes("""[{"page":1,"x":0.1,"y":0.3333,"width":0.9,"height":0.6667}]"""));
        Assert.AreEqual(1m, regions.Items[0].X + regions.Items[0].Width);
    }
}

[TestClass]
public sealed class RedactRasterTests
{
    [TestMethod]
    public void LimitsPixelsAndAxes_WithCorrectFloorAndOutwardRounding()
    {
        var a4 = RedactRaster.Create(595.28, 841.89);
        Assert.AreEqual((1653, 2338), (a4.Width, a4.Height));
        var a2 = RedactRaster.Create(1190.55, 1683.78);
        Assert.AreEqual((3307, 4677), (a2.Width, a2.Height));
        foreach (var (w, h) in new[] { (1683.78, 2383.94), (14400d, 14400d), (1d, 14400d), (14400d, 1d), (1d, 1d) })
        {
            var raster = RedactRaster.Create(w, h);
            Assert.IsTrue((long)raster.Width * raster.Height <= 16_000_000);
            Assert.IsTrue(raster.Width is >= 1 and <= 65535 && raster.Height is >= 1 and <= 65535);
            var fill = raster.Fill(new(1, 0.9999m, 0.9999m, 0.0001m, 0.0001m));
            Assert.AreEqual(raster.Width, fill.Right); Assert.AreEqual(raster.Height, fill.Bottom);
            Assert.IsTrue(fill.Right > fill.Left && fill.Bottom > fill.Top);
            Assert.IsFalse(raster.WidthPoints.Contains('E') || raster.HeightPoints.Contains('E'));
        }
        var exact = new RedactRaster(1000, 2000, "100", "200");
        Assert.AreEqual(new RenderFill(400, 600, 600, 800), exact.Fill(new(1, .4m, .3m, .2m, .1m)));
        Assert.AreEqual(new RenderFill(1, 1, 3, 3), exact.Fill(new(1, .0011m, .0006m, .001m, .0006m)));
        foreach (var value in new[] { 0d, .9999d, double.NaN, double.PositiveInfinity })
            Assert.ThrowsExactly<PdfRedactException>(() => RedactRaster.Create(value, 10));
        Assert.AreEqual("1.2346", RedactRaster.Create(1.23456, 10).WidthPoints);
    }
}

[TestClass]
public sealed class RedactDocumentTests
{
    internal static JsonObject Fixture(int count = 3)
    {
        var objects = new JsonObject
        {
            ["obj:1 0 R"] = new JsonObject { ["value"] = JsonNode.Parse("""{"/Type":"/Catalog","/Pages":"2 0 R","/PageLabels":{"/Nums":[0,{"/P":"u:PRIVATE"}]}}""") },
            ["obj:2 0 R"] = new JsonObject { ["value"] = new JsonObject { ["/Type"] = "/Pages", ["/Count"] = count,
                ["/Kids"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode?)JsonValue.Create($"{i+3} 0 R")).ToArray()) } },
            ["trailer"] = JsonNode.Parse("""{"value":{"/Root":"1 0 R","/Size":100}}""")
        };
        var pages = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var reference = $"{i + 3} 0 R";
            objects["obj:" + reference] = JsonNode.Parse("""{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,595,842],"/Resources":{},"/Annots":[]}}""");
            pages.Add(new JsonObject { ["object"] = reference });
        }
        return new JsonObject { ["pages"] = pages, ["qpdf"] = new JsonArray(new JsonObject { ["jsonversion"] = 2, ["pdfversion"] = "1.7", ["maxobjectid"] = 99 }, objects) };
    }
    internal static JsonObject Objects(JsonObject root) => root["qpdf"]![1]!.AsObject();
    internal static JsonObject Page(JsonObject root, int index) => Objects(root)[$"obj:{index + 2} 0 R"]!["value"]!.AsObject();
    internal static PdfPageBoxes Parse(JsonObject root) => new(Encoding.UTF8.GetBytes(root.ToJsonString()), 32 * 1048576, 50000, CancellationToken.None);
    private static RedactRegions Regions => RedactRegions.Parse(Encoding.UTF8.GetBytes("[" + RedactRegionsTests.Region + "]"));

    [TestMethod]
    public void Fonts_IndirectDirectProgramsAndNestedResources_WithoutTransitiveExpansion()
    {
        foreach (var kind in new[] { "indirect", "direct", "program", "form", "pattern", "smask", "type3" })
        {
            var root = Fixture();
            var objects = Objects(root);
            var font = JsonNode.Parse("""{"/Type":"/Font","/Subtype":"/TrueType","/BaseFont":"/Synthetic","/FontDescriptor":"80 0 R"}""")!;
            objects["obj:80 0 R"] = JsonNode.Parse("""{"value":{"/FontFile2":"81 0 R"}}""");
            objects["obj:81 0 R"] = JsonNode.Parse("""{"stream":{"dict":{}}}""");
            objects["obj:82 0 R"] = new JsonObject { ["value"] = font.DeepClone() };
            JsonNode raw = kind == "direct" ? font.DeepClone() : JsonValue.Create("82 0 R")!;
            Page(root, 1)["/Resources"] = new JsonObject { ["/Font"] = new JsonObject { ["/F1"] = raw.DeepClone() } };
            var res = new JsonObject { ["/Font"] = new JsonObject { ["/F2"] = raw.DeepClone() } };
            if (kind == "program")
            {
                objects["obj:83 0 R"] = new JsonObject { ["value"] = font.DeepClone() };
                objects["obj:83 0 R"]!["value"]!["/FontDescriptor"] = "86 0 R";
                objects["obj:86 0 R"] = JsonNode.Parse("""{"value":{"/FontFile2":"81 0 R"}}""");
                res["/Font"]!["/F2"] = "83 0 R";
            }
            var form = new JsonObject { ["/Type"] = "/XObject", ["/Subtype"] = "/Form", ["/Resources"] = res.DeepClone() };
            Page(root, 2)["/Resources"] = kind switch
            {
                "form" => new JsonObject { ["/XObject"] = new JsonObject { ["/X"] = "84 0 R" } },
                "pattern" => new JsonObject { ["/Pattern"] = new JsonObject { ["/P"] = new JsonObject { ["/PatternType"] = 1, ["/Resources"] = res.DeepClone() } } },
                "smask" => new JsonObject { ["/ExtGState"] = new JsonObject { ["/E"] = new JsonObject { ["/SMask"] = new JsonObject { ["/G"] = "84 0 R" } } } },
                "type3" => new JsonObject { ["/Font"] = new JsonObject { ["/T"] = new JsonObject { ["/Type"] = "/Font", ["/Subtype"] = "/Type3", ["/Resources"] = res.DeepClone() } } },
                _ => res.DeepClone()
            };
            objects["obj:84 0 R"] = new JsonObject { ["stream"] = new JsonObject { ["dict"] = form } };
            // page 2 also has a distinct font used on page 3; page 3 must remain text.
            var second = Page(root, 2)["/Resources"]!.AsObject();
            second["/Font"] ??= new JsonObject();
            second["/Font"]!["/Other"] = "85 0 R";
            objects["obj:85 0 R"] = JsonNode.Parse("""{"value":{"/Type":"/Font","/Subtype":"/Type1","/BaseFont":"/Courier"}}""");
            Page(root, 3)["/Resources"] = JsonNode.Parse("""{"/Font":{"/F3":"85 0 R"}}""");
            using var boxes = Parse(root);
            CollectionAssert.AreEqual(new[] { 1, 2 }, new RedactDocument(boxes, CancellationToken.None).RasterizedPages(Regions), kind);
        }
    }

    [TestMethod]
    public void Images_CheckFilterChainsMasksColorSpacesAndExactThresholds()
    {
        foreach (var (filter, width, height, space) in new[]
        {
            ("/DCTDecode", 10000, 3000, (JsonNode)JsonValue.Create("/DeviceRGB")!),
            ("/JPXDecode", 10000, 1000, JsonValue.Create("/DeviceRGB")!),
            ("/JBIG2Decode", 40000, 25000, JsonValue.Create("/DeviceGray")!),
            ("/DCTDecode", 10000, 2250, JsonNode.Parse("""["/ICCBased", {"/N":4}]""")!),
            ("/DCTDecode", 10000, 9000, JsonNode.Parse("""["/Indexed","/DeviceRGB",255,"b:00"]""")!),
            ("/DCTDecode", 10000, 3000, JsonNode.Parse("""["/DeviceN",["/A","/B","/C"],"/DeviceRGB",{}]""")!),
            ("/DCTDecode", 10000, 2250, JsonValue.Create("/Unknown")!)
        })
        {
            foreach (var mask in new[] { false, true })
            {
                var root = Fixture(1);
                var image = new JsonObject { ["/Type"] = "/XObject", ["/Subtype"] = "/Image", ["/Width"] = width, ["/Height"] = height,
                    ["/ColorSpace"] = space.DeepClone(), ["/Filter"] = new JsonArray("/ASCII85Decode", filter) };
                var outer = mask ? new JsonObject { ["/Type"] = "/XObject", ["/Subtype"] = "/Image", ["/Width"] = 1, ["/Height"] = 1, ["/SMask"] = "81 0 R" } : image;
                Objects(root)["obj:80 0 R"] = new JsonObject { ["stream"] = new JsonObject { ["dict"] = outer } };
                if (mask) Objects(root)["obj:81 0 R"] = new JsonObject { ["stream"] = new JsonObject { ["dict"] = image } };
                Page(root, 1)["/Resources"] = JsonNode.Parse("""{"/XObject":{"/Im":"80 0 R"}}""");
                using (var boxes = Parse(root)) new RedactDocument(boxes, CancellationToken.None).CheckImages(1);
                image["/Width"] = width + 1;
                using (var boxes = Parse(root))
                    Assert.AreEqual(PdfRedactReason.TooComplex, Assert.ThrowsExactly<PdfRedactException>(() =>
                        new RedactDocument(boxes, CancellationToken.None).CheckImages(1)).Reason);
            }
        }
    }


    [TestMethod]
    public void DescendantArraysAndFontPrograms_AreTraversed_AndResourceDepthIsBounded()
    {
        var root = Fixture(2);
        var objects = Objects(root);
        objects["obj:80 0 R"] = JsonNode.Parse("""{"value":{"/Type":"/Font","/Subtype":"/Type0","/DescendantFonts":["82 0 R"]}}""");
        objects["obj:81 0 R"] = JsonNode.Parse("""{"value":{"/Type":"/Font","/Subtype":"/Type0","/DescendantFonts":["83 0 R"]}}""");
        objects["obj:82 0 R"] = JsonNode.Parse("""{"value":{"/FontDescriptor":{"/FontFile2":"84 0 R"}}}""");
        objects["obj:83 0 R"] = JsonNode.Parse("""{"value":{"/FontDescriptor":{"/FontFile2":"84 0 R"}}}""");
        objects["obj:84 0 R"] = JsonNode.Parse("""{"stream":{"dict":{}}}""");
        Page(root, 1)["/Resources"] = JsonNode.Parse("""{"/Font":{"/F":"80 0 R"}}""");
        Page(root, 2)["/Resources"] = JsonNode.Parse("""{"/Font":{"/F":"81 0 R"}}""");
        using (var boxes = Parse(root))
            CollectionAssert.AreEqual(new[] { 1, 2 }, new RedactDocument(boxes, CancellationToken.None).RasterizedPages(Regions));
        root = Fixture(1); objects = Objects(root);
        Page(root, 1)["/Resources"] = JsonNode.Parse("""{"/XObject":{"/F":"100 0 R"}}""");
        for (var i = 100; i < 140; i++)
            objects[$"obj:{i} 0 R"] = new JsonObject { ["stream"] = new JsonObject { ["dict"] = new JsonObject
                { ["/Subtype"] = "/Form", ["/Resources"] = new JsonObject { ["/XObject"] = new JsonObject { ["/F"] = $"{i+1} 0 R" } } } } };
        using var deep = Parse(root);
        Assert.AreEqual(PdfRedactReason.TooComplex, Assert.ThrowsExactly<PdfRedactException>(() =>
            new RedactDocument(deep, CancellationToken.None).RasterizedPages(Regions)).Reason);
    }

    [TestMethod]
    public void VisitBudget_AliasedColorSpaceAndImageMask_AreEnforced()
    {
        var root = Fixture(1);
        Page(root, 1)["/Resources"] = JsonNode.Parse("""{"/Font":{"/F":{"/Subtype":"/Type1","/Private":"u:VISITS"}}}""");
        var raw = root.ToJsonString().Replace("\"u:VISITS\"", "[" + string.Join(',', Enumerable.Repeat("0", 1_000_000)) + "]");
        using (var boxes = new PdfPageBoxes(Encoding.UTF8.GetBytes(raw), 32 * 1048576, 50000, default))
            Assert.AreEqual(PdfRedactReason.TooComplex, Assert.ThrowsExactly<PdfRedactException>(() =>
                new RedactDocument(boxes, default).RasterizedPages(Regions)).Reason);
        root = Fixture(1);
        var image = JsonNode.Parse("""{"/Subtype":"/Image","/Width":10000,"/Height":3000,"/ColorSpace":"/Alias","/Filter":"/DCTDecode"}""")!.AsObject();
        Objects(root)["obj:80 0 R"] = new JsonObject { ["stream"] = new JsonObject { ["dict"] = image } };
        Page(root, 1)["/Resources"] = JsonNode.Parse("""{"/XObject":{"/I":"80 0 R"},"/ColorSpace":{"/Alias":"/DeviceRGB"}}""");
        using (var boxes = Parse(root)) new RedactDocument(boxes, default).CheckImages(1);
        image["/Width"] = 10001;
        using (var boxes = Parse(root)) Assert.ThrowsExactly<PdfRedactException>(() => new RedactDocument(boxes, default).CheckImages(1));
        image["/ImageMask"] = true; image["/Width"] = 10000; image["/Height"] = 9000;
        using (var boxes = Parse(root)) new RedactDocument(boxes, default).CheckImages(1);
        image["/Filter"] = "/FlateDecode"; image["/Height"] = int.MaxValue;
        using (var boxes = Parse(root)) new RedactDocument(boxes, default).CheckImages(1);
    }

    [TestMethod]
    public void CapacityFactories_KeepCleanDefaults_AndReserveExternalSentinelsBeforeWriting()
    {
        var defaults = new CleanCapacity(1);
        Assert.ThrowsExactly<PdfCleanTooComplexException>(() => defaults.Reserve("unused", 1));
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var path = Path.Combine(files.DirectoryPath, "image.jpg");
        var capacity = new CleanCapacity(8192, PdfRedactException.Complex);
        Assert.AreEqual(4096L, capacity.ReserveWrite(path, 4096, PdfRedactException.Output, sentinel: true));
        Assert.AreEqual(8192L, capacity.Used);
        Assert.AreEqual(PdfRedactReason.OutputTooLarge, Assert.ThrowsExactly<PdfRedactException>(() =>
            capacity.ReserveWrite(path + ".second", 1, PdfRedactException.Output)).Reason);
        capacity.Delete(path); Assert.AreEqual(0L, capacity.Used);
        using var bytes = new MemoryStream();
        using var stream = new CleanLimitedWriteStream(bytes, 3, default, PdfRedactException.Output);
        stream.Write(new byte[3]);
        Assert.ThrowsExactly<PdfRedactException>(() => stream.WriteByte(0));
        Assert.AreEqual(3L, bytes.Length);
    }

    [TestMethod]
    public void Update_UsesFreshObjectsAndOnlyAllowedPageCatalogAndRootKeys()
    {
        var root = Fixture();
        using var boxes = Parse(root);
        var plan = new RedactPage(1, RedactRaster.Create(595, 842), 595, 842, "/tmp/generated.jpg", []);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            new RedactDocument(boxes, CancellationToken.None).WriteFinal(writer, [plan], 3);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        using var doc = JsonDocument.Parse(text);
        var objects = doc.RootElement.GetProperty("qpdf")[1];
        var page = objects.GetProperty("obj:100 0 R").GetProperty("value");
        CollectionAssert.AreEquivalent(new[] { "/Type", "/Parent", "/MediaBox", "/Resources", "/Contents" }, page.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual("100 0 R", objects.GetProperty("obj:2 0 R").GetProperty("value").GetProperty("/Kids")[0].GetString());
        Assert.IsFalse(text.Contains("/Annots") || text.Contains("/PageLabels") || text.Contains("PRIVATE"));
        var data = objects.GetProperty("obj:101 0 R").GetProperty("stream").GetProperty("data").GetBytesFromBase64();
        CollectionAssert.AreEqual(plan.Raster.Content, data);
        Objects(root)["obj:2 0 R"]!["value"]!["/Kids"] = new JsonArray("4 0 R", "3 0 R", "5 0 R");
        using var invalid = Parse(root);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            using var bytes = new MemoryStream(); using var writer = new Utf8JsonWriter(bytes);
            new RedactDocument(invalid, CancellationToken.None).WriteFinal(writer, [plan], 3);
        });
    }
}
