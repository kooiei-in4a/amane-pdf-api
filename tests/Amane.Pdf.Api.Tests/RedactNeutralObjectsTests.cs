using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class RedactNeutralObjectsTests
{
    private const string WholePage = """[{"page":1,"x":0,"y":0,"width":1,"height":1}]""";

    [TestMethod]
    [DataRow("icc", "/ICCBased")] [DataRow("state", "/ExtGState")] [DataRow("procset", "/ProcSet")]
    [DataRow("ocg", "SETTING_OC_GROUP")] [DataRow("ocmd", "/OCMD")]
    [DataRow("function0", "/FunctionType 0")] [DataRow("function2", "/FunctionType 2")] [DataRow("function4", "/FunctionType 4")]
    [DataRow("function3", "/FunctionType 3")] [DataRow("cs-indirect", "/ICCBased")]
    [DataRow("inherited", "/ImB")] [DataRow("color-map", "/ICCBased")]
    [DataRow("state-alias", "/ExtGState")] [DataRow("state-functions", "/TR2")]
    public async Task NeutralSharing_KeepsSettingsButNotTargetPixels_WithoutExpandingR(string kind, string marker)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var (pdf, pixels) = Fixture(kind);
        await test.AssertValidPdfAsync(pdf, 2);
        using var response = await PdfRedactTests.Post(test, pdf, WholePage);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Rasterized-Pages").Single());
        var result = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(result, 2);
        var expanded = await PdfRedactTests.Expanded(test, result);
        Assert.IsFalse(expanded.Contains(Encoding.Latin1.GetString(pixels), StringComparison.Ordinal));
        StringAssert.Contains(expanded, marker);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("smask")] [DataRow("state-ht")] [DataRow("state-unknown")]
    [DataRow("bad-procset")] [DataRow("icc-role-conflict")]
    [DataRow("ocg-private")] [DataRow("icc-metadata")]
    [DataRow("state-function-empty")] [DataRow("state-function-domain")]
    public async Task NeutralParent_DoesNotExemptBodyChildrenOrConflictingRoles(string kind)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var (pdf, _) = Fixture(kind);
        await test.AssertValidPdfAsync(pdf, 2);
        using var response = await PdfRedactTests.Post(test, pdf, WholePage);
        await PdfRedactTests.Problem(test, response, 422, "unsupported-pdf");
    }

    private static (byte[] Pdf, byte[] Target) Fixture(string kind)
    {
        byte[] Text(string text) => CompressFixtures.Text(text);
        byte[] Stream(string dictionary, string data) => CompressFixtures.Stream(dictionary, Text(data));
        var profileJson = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/redact-neutral-profile.json")))!;
        var profile = Convert.FromBase64String(profileJson["srgb"]!.GetValue<string>());
        var target = Enumerable.Range(0, kind.StartsWith("function", StringComparison.Ordinal) ? 400 : 1200).Select(i => (byte)(i % 251)).ToArray();
        var color = kind.StartsWith("function", StringComparison.Ordinal) ? "[/Separation /Spot /DeviceRGB 8 0 R]" : "/DeviceRGB";
        var common = ""; var before = ""; var after = ""; var extra = ""; var targetOnly = "";
        var setting = Text("null"); var auxiliary = Text("null");
        if (kind is "icc" or "icc-metadata" or "icc-role-conflict" or "cs-indirect" or "color-map")
        {
            color = kind == "cs-indirect" ? "8 0 R" : kind == "color-map" ? "/OC" : "[/ICCBased 8 0 R]";
            if (kind == "color-map") common = "/ColorSpace << /OC 8 0 R >>";
            setting = kind is "cs-indirect" or "color-map" ? Text("[/ICCBased 10 0 R]") : CompressFixtures.Stream("/N 3 /Alternate /DeviceRGB" +
                (kind == "icc-metadata" ? " /Metadata 10 0 R" : ""), profile);
            auxiliary = kind is "cs-indirect" or "color-map" ? CompressFixtures.Stream("/N 3 /Alternate /DeviceRGB", profile) :
                Stream("/Type /Metadata /Subtype /XML", "SETTING_METADATA");
            if (kind == "icc-metadata") extra = "/PieceInfo << /App << /Private 10 0 R >> >>";
            if (kind == "icc-role-conflict") { targetOnly = "/Bad 8 0 R"; after = " /Bad Do"; }
        }
        if (kind is "state" or "state-alias" or "state-functions" or "state-function-empty" or "state-function-domain" or "state-ht" or "state-unknown" or "smask")
        {
            common = kind == "state-alias" ? "/ExtGState << /OC 8 0 R >>" : "/ExtGState << /GS0 8 0 R >>";
            before = kind == "state-alias" ? "/OC gs " : "/GS0 gs ";
            setting = Text("<< /Type /ExtGState /CA 1 /ca 1" + (kind switch
                { "state-functions" => " /TR2 10 0 R", "state-function-empty" or "state-function-domain" => " /TR 10 0 R",
                    "state-ht" => " /HT /Default", "state-unknown" => " /Unknown 1", "smask" => " /SMask << /S /Alpha /G 10 0 R >>", _ => "" }) + " >>");
            if (kind == "state-functions") auxiliary = Stream("/FunctionType 4 /Domain [0 1] /Range [0 1]", "{ }");
            if (kind == "state-function-empty") auxiliary = Stream("", "SETTING_NON_FUNCTION");
            if (kind == "state-function-domain") auxiliary = Stream("/FunctionType 4", "{ }");
            if (kind == "smask")
            {
                targetOnly = "/Form 10 0 R"; after = " /Form Do";
                auxiliary = Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << >> /Group << /S /Transparency /CS /DeviceRGB >>",
                    "% TARGET_FORM\n1 1 1 rg 0 0 100 100 re f");
            }
        }
        if (kind is "procset" or "bad-procset")
        {
            common = "/ProcSet 8 0 R";
            setting = Text(kind == "procset" ? "[/PDF /ImageC]" : "[/PDF 10 0 R]");
            auxiliary = Text("/ImageC");
        }
        if (kind is "ocg" or "ocmd" or "ocg-private")
        {
            common = "/Properties << /MC0 8 0 R >>"; before = "/OC /MC0 BDC "; after = " EMC";
            setting = Text(kind == "ocmd" ? "<< /Type /OCMD /OCGs [10 0 R] /P /AnyOn >>" :
                "<< /Type /OCG /Name (SETTING_OC_GROUP)" + (kind == "ocg-private" ? " /Private 1" : "") + " >>");
            auxiliary = Text("<< /Type /OCG /Name (SETTING_OC_GROUP) >>");
        }
        if (kind.StartsWith("function", StringComparison.Ordinal))
        {
            var f2 = Text("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >>");
            setting = kind switch
            {
                "function0" => CompressFixtures.Stream("/FunctionType 0 /Domain [0 1] /Range [0 1 0 1 0 1] /Size [2] /BitsPerSample 8", [0, 0, 0, 255, 255, 255]),
                "function2" => f2,
                "function4" => Stream("/FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1]", "{ dup dup }"),
                _ => Text("<< /FunctionType 3 /Domain [0 1] /Functions [10 0 R] /Bounds [] /Encode [0 1] >>")
            };
            auxiliary = f2;
        }
        var inherited = kind == "inherited";
        var resource1 = $"/Resources << /XObject << /Im0 5 0 R {targetOnly} >> {common} >>";
        var resource2 = $"/Resources << /XObject << /Im0 6 0 R >> {common} >>";
        var content1 = before + $"q 100 0 0 100 0 0 cm /{(inherited ? "ImA" : "Im0")} Do Q" + after;
        var content2 = before + $"q 100 0 0 100 0 0 cm /{(inherited ? "ImB" : "Im0")} Do Q" + (kind is "ocg" or "ocmd" or "ocg-private" ? " EMC" : "");
        var imageDict = $"/Type /XObject /Subtype /Image /Width 20 /Height 20 /ColorSpace {color} /BitsPerComponent 8";
        return (CompressFixtures.Objects([
            Text("<< /Type /Catalog /Pages 2 0 R >>"),
            Text("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2" + (inherited ? " /Resources 10 0 R" : "") + " >>"),
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] {(inherited ? "" : resource1)} /Contents 7 0 R >>"),
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] {(inherited ? "" : resource2)} {extra} /Contents 9 0 R >>"),
            CompressFixtures.Stream(imageDict, target), CompressFixtures.Stream(imageDict, Enumerable.Repeat((byte)80, target.Length).ToArray()),
            Stream("", content1), setting, Stream("", content2),
            inherited ? Text("<< /XObject << /ImA 5 0 R /ImB 6 0 R >> >>") : auxiliary
        ]), target);
    }

    [TestMethod]
    public void Function_RequiresSupportedTypeAndDomain_AndStreamsForSampledOrCalculatorFunctions()
    {
        foreach (var type in new[] { 0, 2, 3, 4 })
        {
            foreach (var stream in new[] { false, true })
            {
                var dictionary = new JsonObject { ["/FunctionType"] = type, ["/Domain"] = new JsonArray(0, 1) };
                var wrapper = stream ? new JsonObject { ["stream"] = new JsonObject { ["dict"] = dictionary } } :
                    new JsonObject { ["value"] = dictionary };
                using var document = JsonDocument.Parse(wrapper.ToJsonString());
                Assert.AreEqual(stream || type is 2 or 3, RedactObjectRoles.IsNeutral(document.RootElement, RedactObjectRole.Function));
            }
        }
        foreach (var dictionary in new[] { "{}", "{\"/Domain\":[0,1]}", "{\"/FunctionType\":4}",
                     "{\"/FunctionType\":1,\"/Domain\":[0,1]}", "{\"/FunctionType\":2.5,\"/Domain\":[0,1]}",
                     "{\"/FunctionType\":\"4\",\"/Domain\":[0,1]}" })
        {
            using var document = JsonDocument.Parse("{\"stream\":{\"dict\":" + dictionary + "}}");
            Assert.IsFalse(RedactObjectRoles.IsNeutral(document.RootElement, RedactObjectRole.Function));
        }
    }

    [TestMethod]
    public void Allowlist_UsesRoleAndStructure_AndRejectsUnknownOrContentBearingShapes()
    {
        foreach (var role in new[] { RedactObjectRole.ExtGState, RedactObjectRole.ProcSet, RedactObjectRole.ColorSpace,
                     RedactObjectRole.Icc, RedactObjectRole.Oc, RedactObjectRole.Function })
        {
            var raw = role switch
            {
                RedactObjectRole.ExtGState => """{"value":{"/Type":"/ExtGState","/HTO":[0,0],"/HTP":[0,0]}}""",
                RedactObjectRole.ProcSet => """{"value":["/PDF","/ImageC"]}""",
                RedactObjectRole.ColorSpace => """{"value":["/Indexed","/DeviceRGB",0,"b:000000"]}""",
                RedactObjectRole.Icc => """{"stream":{"dict":{"/N":3}}}""",
                RedactObjectRole.Oc => """{"value":{"/Type":"/OCG","/Name":"u:SETTING"}}""",
                _ => """{"stream":{"dict":{"/FunctionType":4,"/Domain":[0,1]}}}"""
            };
            using var document = JsonDocument.Parse(raw);
            Assert.IsTrue(RedactObjectRoles.IsNeutral(document.RootElement, role));
            Assert.IsFalse(RedactObjectRoles.IsNeutral(document.RootElement, role | RedactObjectRole.Body));
            if (role == RedactObjectRole.ProcSet) continue;
            var broken = JsonNode.Parse(raw)!;
            var dictionary = (broken["value"] ?? broken["stream"]!["dict"])!;
            if (role == RedactObjectRole.ColorSpace) dictionary[3] = "80 0 R";
            else dictionary["/Subtype"] = "/Image";
            using var invalid = JsonDocument.Parse(broken.ToJsonString());
            Assert.IsFalse(RedactObjectRoles.IsNeutral(invalid.RootElement, role));
        }
    }

    [TestMethod]
    public void ColorSpaceFamilies_KeepOnlyTheApprovedArrayShapes()
    {
        foreach (var array in new[] { "[\"/ICCBased\",\"80 0 R\"]", "[\"/Indexed\",\"/DeviceRGB\",0,\"b:000000\"]",
                     "[\"/Separation\",\"/Spot\",\"/DeviceRGB\",\"80 0 R\"]", "[\"/DeviceN\",[\"/Spot\"],\"/DeviceRGB\",\"80 0 R\",{}]",
                     "[\"/Pattern\",\"/DeviceRGB\"]", "[\"/CalGray\",{}]", "[\"/CalRGB\",{}]", "[\"/Lab\",{}]" })
        {
            using var valid = JsonDocument.Parse("{\"value\":" + array + "}");
            Assert.IsTrue(RedactObjectRoles.IsNeutral(valid.RootElement, RedactObjectRole.ColorSpace));
        }
        foreach (var array in new[] { "[]", "[\"/Unknown\"]", "[\"/Indexed\",\"/DeviceRGB\",0,\"80 0 R\"]",
                     "[\"/DeviceN\",[\"/Spot\"],\"/DeviceRGB\",\"80 0 R\",\"81 0 R\"]" })
        {
            using var invalid = JsonDocument.Parse("{\"value\":" + array + "}");
            Assert.IsFalse(RedactObjectRoles.IsNeutral(invalid.RootElement, RedactObjectRole.ColorSpace));
        }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void EveryArrivalIsCollected_EvenWhenANeutralRoleIsVisitedFirst(bool reverse)
    {
        var root = RedactDocumentTests.Fixture(2);
        var objects = RedactDocumentTests.Objects(root);
        objects["obj:80 0 R"] = JsonNode.Parse("""{"stream":{"dict":{"/N":3}}}""");
        objects["obj:81 0 R"] = JsonNode.Parse("""{"stream":{"dict":{"/Subtype":"/Image","/ColorSpace":["/ICCBased","80 0 R"]}}}""");
        var map = new JsonObject();
        if (reverse) map["/Bad"] = "80 0 R";
        map["/Image"] = "81 0 R";
        if (!reverse) map["/Bad"] = "80 0 R";
        RedactDocumentTests.Page(root, 1)["/Resources"] = new JsonObject { ["/XObject"] = map };
        RedactDocumentTests.Page(root, 2)["/PieceInfo"] = JsonNode.Parse("""{"/Private":"80 0 R"}""");
        using var boxes = RedactDocumentTests.Parse(root);
        var document = new RedactDocument(boxes, default);
        var regions = RedactRegions.Parse(Encoding.UTF8.GetBytes(WholePage));
        var pages = document.RasterizedPages(regions);
        Assert.ThrowsExactly<PdfRedactException>(() => document.CheckSurvivingReferences(regions, pages));
    }
}
