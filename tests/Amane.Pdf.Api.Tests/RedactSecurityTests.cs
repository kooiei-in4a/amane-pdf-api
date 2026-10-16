using System.Net;
using System.Text.Json.Nodes;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class RedactSecurityTests
{
    private const string WholePage = """[{"page":1,"x":0,"y":0,"width":1,"height":1}]""";

    [TestMethod]
    [DataRow("image-clip")] [DataRow("form-clip")]
    [DataRow("private-image")] [DataRow("private-form")] [DataRow("private-resources")]
    [DataRow("private-thumbnail")] [DataRow("private-mask")] [DataRow("private-custom")]
    public async Task SharedOrPrivateTargetObjects_Are422WithoutPdfOrSuccessHeaders(string kind)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var input = SharedObjectPdf(kind);
        await test.AssertValidPdfAsync(input, 2);
        using var response = await PdfRedactTests.Post(test, input, WholePage);
        await PdfRedactTests.Problem(test, response, 422, "unsupported-pdf");
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task IndependentImageOrForm_DoesNotExpandRasterizedSet(bool form)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        using var response = await PdfRedactTests.Post(test, SharedObjectPdf(form ? "independent-form" : "independent-image"), WholePage);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Rasterized-Pages").Single());
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 2);
        test.AssertClean();
    }

    // A single scan or fontless vector Form is displayed through different viewports.
    // Private cases retain it through PieceInfo, Resources, Thumb, a mask or a custom graph.
    private static byte[] SharedObjectPdf(string kind)
    {
        var form = kind.Contains("form", StringComparison.Ordinal);
        var independent = kind.StartsWith("independent", StringComparison.Ordinal);
        var clipped = kind.EndsWith("clip", StringComparison.Ordinal) || independent;
        var row = Enumerable.Range(0, 200).SelectMany(x => x < 100 ? new byte[] { 255, 0, 0 } : [0, 0, 255]).ToArray();
        var pixels = Enumerable.Range(0, 100).SelectMany(_ => row).ToArray();
        var image = CompressFixtures.Stream("/Type /XObject /Subtype /Image /Width 200 /Height 100 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels);
        var target = form ? CompressFixtures.Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 100] /Resources << >>",
            CompressFixtures.Text("% MK_VECTOR_FORM\n1 0 0 rg 0 0 100 100 re f 0 0 1 rg 100 0 100 100 re f")) :
            kind == "private-mask" ? CompressFixtures.Stream("/Type /XObject /Subtype /Image /Width 200 /Height 100 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 11 0 R", pixels) : image;
        var privateRef = kind switch { "private-resources" => 8, "private-thumbnail" => 9, "private-mask" => 11, "private-custom" => 10, _ => 5 };
        var resources = clipped ? $"<< /XObject << /XO {(independent ? 12 : 5)} 0 R >> >>" : "<< >>";
        var extra = clipped ? "" : $"/PieceInfo << /App << /LastModified (D:20260101) /Private {privateRef} 0 R >> >>";
        return CompressFixtures.Objects([
            CompressFixtures.Text("<< /Type /Catalog /Pages 2 0 R >>"),
            CompressFixtures.Text("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            CompressFixtures.Text("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 8 0 R /Thumb 9 0 R /PieceInfo << /App << /Private 10 0 R >> >> /Contents 6 0 R >>"),
            CompressFixtures.Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources {resources} {extra} /Contents 7 0 R >>"),
            target,
            CompressFixtures.Stream("", CompressFixtures.Text(form ? "q /XO Do Q" : "q 200 0 0 100 0 0 cm /XO Do Q")),
            CompressFixtures.Stream("", CompressFixtures.Text(clipped ? form ? "q 1 0 0 1 -100 0 cm /XO Do Q" : "q 200 0 0 100 -100 0 cm /XO Do Q" : "0 0 1 rg 10 10 50 50 re f")),
            CompressFixtures.Text("<< /XObject << /XO 5 0 R >> >>"),
            image,
            CompressFixtures.Text("<< /Nested [<< /Original 5 0 R >>] >>"),
            CompressFixtures.Stream("/Type /XObject /Subtype /Image /Width 200 /Height 100 /ColorSpace /DeviceGray /BitsPerComponent 8", Enumerable.Repeat((byte)255, 20000).ToArray()),
            target
        ]);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ExtGStateFont_SharingEitherDirection_RasterizesBothPages(bool reverse)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var state = "<< /ExtGState << /GS0 << /Font [5 0 R 12] >> >> >>";
        var normal = "<< /Font << /F1 5 0 R >> >>";
        var input = CompressFixtures.Objects([
            CompressFixtures.Text("<< /Type /Catalog /Pages 2 0 R >>"),
            CompressFixtures.Text("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            CompressFixtures.Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources {(reverse ? normal : state)} /Contents 6 0 R >>"),
            CompressFixtures.Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources {(reverse ? state : normal)} /Contents 7 0 R >>"),
            CompressFixtures.Text("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 8 0 R >>"),
            CompressFixtures.Stream("", CompressFixtures.Text($"BT {(reverse ? "/F1 12 Tf" : "/GS0 gs")} 10 50 Td (SECRET) Tj ET")),
            CompressFixtures.Stream("", CompressFixtures.Text($"BT {(reverse ? "/GS0 gs" : "/F1 12 Tf")} 10 50 Td (PUBLIC) Tj ET")),
            CompressFixtures.Stream("", CompressFixtures.Text("% MK_TOUNICODE_FROM_TARGET_FONT\n/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /X def 1 begincodespacerange <00> <FF> endcodespacerange endcmap CMapName currentdict /CMap defineresource pop end end"))
        ]);
        await test.AssertValidPdfAsync(input, 2);
        using var response = await PdfRedactTests.Post(test, input, WholePage);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("1,2", response.Headers.GetValues("X-Pdf-Rasterized-Pages").Single());
        var expanded = await PdfRedactTests.Expanded(test, await response.Content.ReadAsByteArrayAsync());
        Assert.IsFalse(expanded.Contains("MK_TOUNICODE") || expanded.Contains("/Helvetica") || expanded.Contains("SECRET"));
        test.AssertClean();
    }

    [TestMethod]
    public void ProtectedGraph_TraversesArbitraryContainersAndInheritedResources_WithoutWalkingPageTree()
    {
        foreach (var inherited in new[] { false, true })
        {
            var root = RedactDocumentTests.Fixture(2);
            var objects = RedactDocumentTests.Objects(root);
            objects["obj:80 0 R"] = JsonNode.Parse("""{"value":{"/XObject":{"/Im":"81 0 R"}}}""");
            objects["obj:81 0 R"] = JsonNode.Parse("""{"stream":{"dict":{"/Subtype":"/Image","/SMask":"82 0 R"}}}""");
            objects["obj:82 0 R"] = JsonNode.Parse("""{"stream":{"dict":{}}}""");
            if (inherited)
            {
                RedactDocumentTests.Page(root, 1).Remove("/Resources");
                objects["obj:2 0 R"]!["value"]!["/Resources"] = "80 0 R";
            }
            else RedactDocumentTests.Page(root, 1)["/PieceInfo"] = JsonNode.Parse("""{"/App":{"/Private":["80 0 R"]}}""");
            RedactDocumentTests.Page(root, 2)["/PieceInfo"] = JsonNode.Parse("""{"/App":{"/Private":"82 0 R"}}""");
            using var boxes = RedactDocumentTests.Parse(root);
            var document = new RedactDocument(boxes, default);
            var regions = RedactRegions.Parse(System.Text.Encoding.UTF8.GetBytes(WholePage));
            var rasterized = document.RasterizedPages(regions);
            CollectionAssert.AreEqual(new[] { 1 }, rasterized);
            Assert.AreEqual(PdfRedactReason.UnsupportedPdf, Assert.ThrowsExactly<PdfRedactException>(() =>
                document.CheckSurvivingReferences(regions, rasterized)).Reason);
        }
    }
}
