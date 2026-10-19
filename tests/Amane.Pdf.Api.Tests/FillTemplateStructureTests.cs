using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class FillTemplateStructureTests
{
    [TestMethod]
    public void Forms_RecursivelyScanDirectArraysDictionariesAndStreamDictionaries()
    {
        foreach (var marker in new[] { "{\"/Sub#74ype\":\"/Wid#67et\"}", "{\"/FT\":\"/Sig\"}",
                     "{\"/Type\":\"/Sig\"}", "{\"/Type\":\"/DocTimeStamp\"}" })
            foreach (var location in new[] { "value", "stream" })
            {
                var root = OverlayFixtures.Metadata();
                OverlayFixtures.Objects(root)["obj:4 0 R"] = JsonNode.Parse(location == "value"
                    ? "{\"value\":{\"/Nested\":[{\"/Direct\":[" + marker + "]}]}}"
                    : "{\"stream\":{\"dict\":{\"/Nested\":[" + marker + "]}}}");
                using var metadata = OverlayFixtures.Parse(root);
                Assert.AreEqual("form-fields", Assert.ThrowsExactly<PdfFillTemplateException>(() => PdfFormDetector.RejectForms(metadata, default)).Code);
            }
        foreach (var key in new[] { "/AcroForm", "/Perms", "/Acro#46orm" })
        {
            var root = OverlayFixtures.Metadata();
            OverlayFixtures.Objects(root)["obj:1 0 R"]!["value"]![key] = new JsonObject();
            using var metadata = OverlayFixtures.Parse(root);
            Assert.ThrowsExactly<PdfFillTemplateException>(() => PdfFormDetector.RejectForms(metadata, default));
        }
        var safe = OverlayFixtures.Metadata();
        OverlayFixtures.Page(safe)["/Data"] = "u:/Subtype /Widget /AcroForm";
        using var clean = OverlayFixtures.Parse(safe);
        PdfFormDetector.RejectForms(clean, default);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PdfFormDetector.RejectForms(clean, cancel.Token));
    }

    [TestMethod]
    public void Unicode_ResolvesFreshObjectNumber_AndRequiresExactlyOneType0Font()
    {
        foreach (var id in new[] { 11, 39 })
        {
            var root = OverlayFixtures.Metadata(); var objects = OverlayFixtures.Objects(root);
            objects["obj:4 0 R"] = JsonNode.Parse("{\"value\":{\"/Type\":\"/Font\",\"/Subtype\":\"/Type0\",\"/ToUnicode\":\"" + id + " 0 R\"}}");
            objects[$"obj:{id} 0 R"] = JsonNode.Parse("""{"stream":{"dict":{"/Length":123,"/Filter":"/FlateDecode","/DecodeParms":{},"/Keep":true}}}""");
            using var metadata = OverlayFixtures.Parse(root);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) FillTemplateUnicode.WriteUpdate(writer, metadata, FillTemplateFieldsTests.Parse(), default);
            using var update = JsonDocument.Parse(buffer.ToArray());
            var stream = update.RootElement.GetProperty("qpdf")[1].GetProperty($"obj:{id} 0 R").GetProperty("stream");
            Assert.AreEqual(1, stream.GetProperty("dict").EnumerateObject().Count());
            Assert.IsTrue(stream.GetProperty("dict").GetProperty("/Keep").GetBoolean());
            StringAssert.Contains(Encoding.ASCII.GetString(stream.GetProperty("data").GetBytesFromBase64()), "begincmap");
        }
        foreach (var count in new[] { 0, 2 })
        {
            var root = OverlayFixtures.Metadata();
            for (var i = 0; i < count; i++) OverlayFixtures.Objects(root)[$"obj:{i + 4} 0 R"] = JsonNode.Parse("""{"value":{"/Subtype":"/Type0","/ToUnicode":"20 0 R"}}""");
            using var metadata = OverlayFixtures.Parse(root); using var buffer = new MemoryStream(); using var writer = new Utf8JsonWriter(buffer);
            Assert.ThrowsExactly<InvalidOperationException>(() => FillTemplateUnicode.WriteUpdate(writer, metadata, FillTemplateFieldsTests.Parse(), default));
        }
    }
}
