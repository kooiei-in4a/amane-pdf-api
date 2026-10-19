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
        var packedBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fill-template-form-object-stream.json"));
        using (var packed = new PdfPageBoxes(packedBytes, 32 * 1048576, 50000, default))
        {
            StringAssert.Contains(packed.Document.RootElement.GetRawText(), "/ObjStm");
            Assert.ThrowsExactly<PdfFillTemplateException>(() => PdfFormDetector.RejectForms(packed, default));
        }
        var safe = OverlayFixtures.Metadata();
        OverlayFixtures.Page(safe)["/Data"] = "u:/Subtype /Widget /AcroForm";
        using var clean = OverlayFixtures.Parse(safe);
        PdfFormDetector.RejectForms(clean, default);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PdfFormDetector.RejectForms(clean, cancel.Token));
    }

}
