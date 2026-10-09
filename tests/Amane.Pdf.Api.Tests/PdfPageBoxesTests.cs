using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfPageBoxesTests
{
    [TestMethod]
    public void InheritedNullAndIndirectValues_RespectNonInheritedAttributes()
    {
        var root = OverlayFixtures.Metadata(); var page = OverlayFixtures.Page(root);
        page["/CropBox"] = "4 7 R";
        OverlayFixtures.Objects(root)["obj:4 7 R"] = JsonNode.Parse("{\"value\":[100,200,500,700]}");
        using var metadata = OverlayFixtures.Parse(root);
        var value = metadata.Read(0, default);
        Assert.AreEqual(new PdfBox(-20, -30, 600, 800), value.Media);
        Assert.AreEqual(new PdfBox(100, 200, 500, 700), value.Crop);
        Assert.AreEqual(value.Crop, value.Trim); Assert.AreEqual(1d, value.UserUnit);
        Assert.AreEqual(630, value.Rotate); Assert.AreEqual(270, value.NormalizedRotate);
        Assert.AreEqual(500d, value.DisplayWidth); Assert.AreEqual(400d, value.DisplayHeight);
        Assert.IsNull(value.DirectMedia); Assert.IsNotNull(value.DirectCrop);
    }

    [TestMethod]
    [DataRow("/MediaBox", "[0,0,0,10]")]
    [DataRow("/CropBox", "[700,0,800,10]")]
    [DataRow("/MediaBox", "[0,0,1e309,20]")]
    [DataRow("/MediaBox", "[0,0,20]")]
    [DataRow("/Rotate", "45")]
    [DataRow("/Rotate", "2147483700")]
    [DataRow("/UserUnit", "0")]
    [DataRow("/UserUnit", "75001")]
    [DataRow("/UserUnit", "100")]
    [DataRow("/TrimBox", "\"wrong\"")]
    public void UnsupportedGeometry_IsTyped(string key, string json)
    {
        var root = OverlayFixtures.Metadata(); OverlayFixtures.Page(root)[key] = JsonNode.Parse(json);
        using var metadata = OverlayFixtures.Parse(root);
        Assert.AreEqual(PdfOverlayReason.UnsupportedPdf, Assert.ThrowsExactly<PdfOverlayInputException>(() => metadata.Read(0, default)).Reason);
    }

    [TestMethod]
    [DataRow("parent-cycle")]
    [DataRow("reference-cycle")]
    [DataRow("parent-depth")]
    [DataRow("reference-depth")]
    public void Chains_AreBoundedAndDetectCycles(string kind)
    {
        var root = OverlayFixtures.Metadata(); var objects = OverlayFixtures.Objects(root);
        if (kind == "parent-cycle") OverlayFixtures.Parent(root)["/Parent"] = "3 0 R";
        else if (kind == "reference-cycle")
        {
            OverlayFixtures.Page(root)["/MediaBox"] = "4 0 R";
            objects["obj:4 0 R"] = JsonNode.Parse("{\"value\":\"4 0 R\"}");
        }
        else
        {
            for (var i = 4; i < 40; i++) objects[$"obj:{i} 0 R"] = kind == "parent-depth"
                ? new JsonObject { ["value"] = new JsonObject { ["/Parent"] = $"{i + 1} 0 R" } }
                : new JsonObject { ["value"] = $"{i + 1} 0 R" };
            if (kind == "parent-depth") OverlayFixtures.Parent(root)["/Parent"] = "4 0 R";
            else OverlayFixtures.Page(root)["/MediaBox"] = "4 0 R";
        }
        using var metadata = OverlayFixtures.Parse(root);
        Assert.AreEqual(PdfOverlayReason.TooComplex, Assert.ThrowsExactly<PdfOverlayInputException>(() => metadata.Read(0, default)).Reason);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Restore_ReevaluatesCurrentParents_AndNeverCopiesOldReferences(bool changedParent)
    {
        var root = OverlayFixtures.Metadata();
        using var input = OverlayFixtures.Parse(root); var original = input.Read(0, default);
        var parent = OverlayFixtures.Parent(root); var page = OverlayFixtures.Page(root);
        if (changedParent) { parent.Remove("/MediaBox"); parent.Remove("/CropBox"); parent.Remove("/Rotate"); }
        page["/MediaBox"] = JsonNode.Parse("[100,200,500,700]"); page["/CropBox"] = page["/MediaBox"]!.DeepClone();
        page["/TrimBox"] = page["/MediaBox"]!.DeepClone(); page["/Rotate"] = 270;
        page["/Contents"] = "99 0 R"; page["/Resources"] = "100 0 R";
        using var combined = OverlayFixtures.Parse(root);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) combined.WriteUpdate(writer, 1, [original], true, default);
        using var update = JsonDocument.Parse(buffer.ToArray());
        var restored = update.RootElement.GetProperty("qpdf")[1].GetProperty("obj:3 0 R").GetProperty("value");
        Assert.AreEqual(changedParent, restored.TryGetProperty("/MediaBox", out _));
        Assert.AreEqual(changedParent, restored.TryGetProperty("/CropBox", out _));
        Assert.AreEqual(changedParent, restored.TryGetProperty("/Rotate", out _));
        Assert.IsFalse(restored.TryGetProperty("/TrimBox", out _));
        Assert.AreEqual("99 0 R", restored.GetProperty("/Contents").GetString());
        Assert.AreEqual("100 0 R", restored.GetProperty("/Resources").GetString());
        if (changedParent) Assert.AreEqual(630, restored.GetProperty("/Rotate").GetInt32());
    }

    [TestMethod]
    public void MetadataLimitsAndMalformedJson_AreDistinct_AndCancelIsObserved()
    {
        var root = OverlayFixtures.Metadata(); var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        using var exact = new PdfPageBoxes(bytes, bytes.Length, 3, default);
        Assert.AreEqual(PdfOverlayReason.TooComplex, Assert.ThrowsExactly<PdfOverlayInputException>(() => new PdfPageBoxes(bytes, bytes.Length - 1, 3, default)).Reason);
        Assert.ThrowsExactly<PdfOverlayInputException>(() => OverlayFixtures.Parse(root, objects: 2));
        Assert.ThrowsExactly<InvalidOperationException>(() => new PdfPageBoxes("{"u8.ToArray(), 100, 3, default));
        Assert.ThrowsExactly<PdfOverlayInputException>(() => new PdfPageBoxes(Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65)), 1000, 3, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => OverlayFixtures.Parse(root, token: cancel.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => exact.Read(0, cancel.Token));
    }

    [TestMethod]
    [DataRow(50000)]
    [DataRow(75000)]
    public void ObjectLimit_ExactAndOneMore(int limit)
    {
        var root = OverlayFixtures.Metadata(); var objects = OverlayFixtures.Objects(root);
        for (var i = 4; i <= limit; i++) objects[$"obj:{i} 0 R"] = new JsonObject { ["value"] = i };
        using var exact = OverlayFixtures.Parse(root, objects: limit);
        Assert.AreEqual(limit, exact.ObjectCount);
        objects[$"obj:{limit + 1} 0 R"] = new JsonObject { ["value"] = 0 };
        Assert.ThrowsExactly<PdfOverlayInputException>(() => OverlayFixtures.Parse(root, objects: limit));
    }

    [TestMethod]
    public void IndirectLocalValues_RestoreAsNumbers_WithCurrentObjectIds()
    {
        var root = OverlayFixtures.Metadata(); var page = OverlayFixtures.Page(root);
        page["/MediaBox"] = "4 7 R"; page["/Rotate"] = "5 3 R";
        OverlayFixtures.Objects(root)["obj:4 7 R"] = JsonNode.Parse("""{"value":[-20,-30,600,800]}""");
        OverlayFixtures.Objects(root)["obj:5 3 R"] = JsonNode.Parse("""{"value":-90}""");
        using var input = OverlayFixtures.Parse(root); var original = input.Read(0, default);
        var output = OverlayFixtures.Metadata();
        OverlayFixtures.Objects(output)["obj:20 0 R"] = OverlayFixtures.Page(output).DeepClone() is { } value ?
            new JsonObject { ["value"] = value } : null;
        output["pages"]![0]!["object"] = "20 0 R";
        using var combined = OverlayFixtures.Parse(output);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) combined.WriteUpdate(writer, 1, [original], true, default);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.IsFalse(text.Contains("4 7 R") || text.Contains("5 3 R"));
        using var update = JsonDocument.Parse(stream.ToArray());
        var restored = update.RootElement.GetProperty("qpdf")[1].GetProperty("obj:20 0 R").GetProperty("value");
        Assert.AreEqual(JsonValueKind.Array, restored.GetProperty("/MediaBox").ValueKind);
        Assert.AreEqual(-90, restored.GetProperty("/Rotate").GetInt32());
    }

    [TestMethod]
    public void ChangedParentCropOutsideRestoredMedia_MaterializesOriginalCrop()
    {
        var root = OverlayFixtures.Metadata();
        using var input = OverlayFixtures.Parse(root); var original = input.Read(0, default);
        OverlayFixtures.Parent(root)["/CropBox"] = JsonNode.Parse("[1000,1000,2000,2000]");
        using var combined = OverlayFixtures.Parse(root);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) combined.WriteUpdate(writer, 1, [original], true, default);
        using var update = JsonDocument.Parse(stream.ToArray());
        Assert.AreEqual(100, update.RootElement.GetProperty("qpdf")[1].GetProperty("obj:3 0 R").GetProperty("value").GetProperty("/CropBox")[0].GetInt32());
    }
}
