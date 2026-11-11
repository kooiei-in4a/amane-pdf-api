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

    [TestMethod]
    public void Signatures_FullScanAtInputObjectAndByteLimitsStaysFast()
    {
        // Overlay endpoints now scan every input object. Worst case: 50,000 objects filling
        // the 32 MiB metadata budget with name-valued keys, each of which Scan has to decode.
        var json = OverlayFixtures.Metadata().ToJsonString();
        var existing = JsonNode.Parse(json)!["qpdf"]![1]!.AsObject().Count(pair => pair.Key.StartsWith("obj:", StringComparison.Ordinal));
        var added = PdfPageBoxes.InputObjectLimit - existing;
        const int limit = 32 * 1048576;
        var keys = ((limit - json.Length - 65536) / added - 32) / 12;
        var body = new System.Text.StringBuilder(limit);
        for (var i = 0; i < added; i++)
        {
            body.Append("\"obj:").Append(1000 + i).Append(" 0 R\":{\"value\":{");
            for (var k = 0; k < keys; k++) body.Append(k == 0 ? "" : ",").Append("\"/K").Append(k).Append("\":\"/V\"");
            body.Append("}},");
        }
        var at = json.IndexOf("\"obj:1 0 R\":", StringComparison.Ordinal);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json.Insert(at, body.ToString()));
        Assert.IsTrue(bytes.Length is > limit * 9 / 10 and <= limit, bytes.Length.ToString());
        using var metadata = new PdfPageBoxes(bytes, limit, PdfPageBoxes.InputObjectLimit, default);
        Assert.AreEqual(PdfPageBoxes.InputObjectLimit, metadata.ObjectCount);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.IsFalse(PdfFormDetector.ContainsSignatureStructure(metadata, default));
        var elapsed = watch.Elapsed;
        Console.WriteLine($"signature scan: {bytes.Length} bytes, {metadata.ObjectCount} objects, {elapsed.TotalMilliseconds:F0} ms");
        Assert.IsTrue(elapsed < TimeSpan.FromSeconds(2), elapsed.ToString());
    }

}
