using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfEmbeddedFileStripperTests
{
    private static JsonObject Graph()
        => JsonNode.Parse("""
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.7","calledgetallpages":true,"pushedinheritedpageresources":false},{
          "obj:1 0 R":{"value":{"/Type":"/Catalog","/Names":"2 0 R","/Pages":"3 0 R"}},
          "obj:2 0 R":{"value":{"/EmbeddedFiles":{"/Names":[]},"/Dests":{"/Names":["u:keep",["3 0 R","/Fit"]]}}},
          "obj:3 0 R":{"value":{"/Annots":"4 0 R","/Probe":"8 0 R"}},
          "obj:4 0 R":{"value":["5 0 R","6 0 R","7 0 R",null,12,"u:keep",{"/Type":"/NotAnnot"}]},
          "obj:5 0 R":{"value":{"/Type":"/Annot","/Subtype":"/FileAttachment","/Rect":[0,0,1,1],"/FS":"9 0 R","/Popup":"6 0 R","/Contents":"u:description-secret","/RC":"u:rich-secret","/T":"u:author-secret","/Subj":"u:subject-secret","/NM":"u:name-secret","/Custom":"u:secret"}},
          "obj:6 0 R":{"value":{"/Type":"/Annot","/Subtype":"/Popup","/Contents":"u:popup-secret"}},
          "obj:7 0 R":{"value":{"/Type":"/Annot","/Subtype":"/Text","/IRT":"5 0 R","/T":"u:keep-author","/Popup":"8 0 R"}},
          "obj:8 0 R":{"value":{"/Subtype":"/Popup","/Parent":"7 0 R","/Contents":"u:keep-popup"}},
          "obj:9 0 R":{"value":{"/Type":"/Filespec","/F":"u:keep-filename","/EF":{"/F":null},"/RF":[]}},
          "obj:10 0 R":{"value":{"/K":{"/Type":"/OBJR","/Obj":"5 0 R"},"/AF":[],"/Nested":[{"/AF":[],"/EF":{},"/RF":null}]}},
          "trailer":{"value":{"/Root":"1 0 R","/Size":11}}
        }],"attachments":{}}
        """)!.AsObject();
    private static JsonObject Objects(JsonObject graph) => graph["qpdf"]![1]!.AsObject();
    private static JsonNode Value(JsonObject graph, int number) => Objects(graph)[$"obj:{number} 0 R"]!["value"]!;
    private static (JsonObject Output, JsonObject Update, int Retained) Strip(JsonObject graph)
    {
        using var document = PdfEmbeddedFileStripper.Parse(Encoding.UTF8.GetBytes(graph.ToJsonString()));
        var stripper = new PdfEmbeddedFileStripper(document);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) stripper.Strip(writer);
        var update = JsonNode.Parse(stream.ToArray())!.AsObject();
        var output = graph.DeepClone().AsObject();
        foreach (var entry in Objects(update)) Objects(output)[entry.Key] = entry.Value!.DeepClone();
        return (output, update, stripper.RetainedPopupCount);
    }
    private static void Verify(JsonObject graph, int retained)
    {
        using var document = PdfEmbeddedFileStripper.Parse(Encoding.UTF8.GetBytes(graph.ToJsonString()));
        PdfEmbeddedFileStripper.Verify(document, retained);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Strip_AllowlistAndBothPopupDirections_PreserveOrdinaryAnnotations(bool parentOnly)
    {
        var graph = Graph();
        if (parentOnly)
        {
            Value(graph, 5).AsObject().Remove("/Popup");
            Value(graph, 6)["/Parent"] = "5 0 R";
        }
        var (output, update, retained) = Strip(graph);
        Assert.AreEqual(1, retained);
        Verify(output, retained);
        CollectionAssert.AreEquivalent(new[] { "/Type", "/Subtype", "/Rect" }, Value(output, 5).AsObject().Select(item => item.Key).ToArray());
        Assert.IsNull(Objects(output)["obj:6 0 R"]!["value"]);
        Assert.AreEqual(5, Value(output, 4).AsArray().Count);
        Assert.AreEqual("7 0 R", Value(output, 4)[0]!.GetValue<string>());
        Assert.AreEqual(graph["qpdf"]![0]!.ToJsonString(), update["qpdf"]![0]!.ToJsonString());
        Assert.AreEqual(Value(graph, 7).ToJsonString(), Value(output, 7).ToJsonString());
        Assert.AreEqual(Value(graph, 8).ToJsonString(), Value(output, 8).ToJsonString());
        Assert.IsNotNull(Value(output, 2)["/Dests"]);
        Assert.AreEqual("u:keep-filename", Value(output, 9)["/F"]!.GetValue<string>());
        Assert.IsFalse(output.ToJsonString().Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AliasesAndSharedAnnots_ResolveSubtypeParentPopupAndNames()
    {
        var graph = Graph();
        foreach (var (number, value) in new[] { (11, "/FileAttachment"), (12, "/Popup"), (13, "5 0 R"), (14, "6 0 R"), (15, "4 0 R"), (16, "2 0 R") })
            Objects(graph)[$"obj:{number} 0 R"] = new JsonObject { ["value"] = value };
        Value(graph, 5)["/Subtype"] = "11 0 R";
        Value(graph, 6)["/Subtype"] = "12 0 R";
        Value(graph, 6)["/Parent"] = "13 0 R";
        Value(graph, 5)["/Popup"] = "14 0 R";
        Value(graph, 1)["/Names"] = "16 0 R";
        Value(graph, 3)["/Annots"] = "15 0 R";
        Value(graph, 10)["/Annots"] = "4 0 R";
        Value(graph, 4).AsArray().Insert(0, "13 0 R");
        Value(graph, 4).AsArray().Add("14 0 R");
        var (output, _, retained) = Strip(graph);
        Verify(output, retained);
        Assert.AreEqual(5, Value(output, 4).AsArray().Count);
        Assert.IsNull(Objects(output)["obj:6 0 R"]!["value"]);
    }

    [TestMethod]
    public void DirectDictionariesAndArrays_RemoveTargetsWithoutNullReplacingOrdinaryDictionaries()
    {
        var graph = Graph();
        var directAttachment = Value(graph, 5).DeepClone();
        var directPopup = Value(graph, 6).DeepClone();
        directPopup["/Parent"] = "5 0 R";
        directAttachment["/Popup"] = directPopup.DeepClone();
        Value(graph, 3)["/Annots"] = new JsonArray(directAttachment, directPopup, Value(graph, 8).DeepClone(), null, 12);
        Value(graph, 5)["/Popup"] = Value(graph, 6).DeepClone();
        // The original indirect popup is unrelated now and must stay unchanged.
        Value(graph, 4).AsArray().RemoveAt(1);
        var (output, _, retained) = Strip(graph);
        Verify(output, retained);
        Assert.AreEqual(3, Value(output, 3)["/Annots"]!.AsArray().Count);
        Assert.AreEqual(Value(graph, 6).ToJsonString(), Value(output, 6).ToJsonString());
        Assert.AreEqual(3, retained);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("12")]
    [DataRow("\"u:keep\"")]
    public void NonTargetWrongTypes_ArePreserved(string names)
    {
        var graph = Graph();
        Value(graph, 1)["/Names"] = JsonNode.Parse(names);
        Value(graph, 3)["/Annots"] = JsonNode.Parse(names);
        var (output, _, retained) = Strip(graph);
        Verify(output, retained);
        Assert.AreEqual(Value(graph, 1)["/Names"]?.ToJsonString(), Value(output, 1)["/Names"]?.ToJsonString());
        Assert.AreEqual(Value(graph, 3)["/Annots"]?.ToJsonString(), Value(output, 3)["/Annots"]?.ToJsonString());
    }

    [TestMethod]
    public void StreamDictionaryUpdate_OmitsDataAndRemovesAllThreeKeys()
    {
        var graph = Graph();
        Objects(graph)["obj:20 0 R"] = JsonNode.Parse("""{"stream":{"dict":{"/Length":12,"/AF":[],"/EF":{},"/RF":null}}}""");
        var (output, update, retained) = Strip(graph);
        Verify(output, retained);
        Assert.AreEqual("{\"stream\":{\"dict\":{\"/Length\":12}}}", Objects(update)["obj:20 0 R"]!.ToJsonString());
    }

    [TestMethod]
    [DataRow("/AF")]
    [DataRow("/EF")]
    [DataRow("/RF")]
    [DataRow("/Contents")]
    [DataRow("/T")]
    [DataRow("/Popup")]
    public void Verify_RejectsResidualKeys(string key)
    {
        var (output, _, retained) = Strip(Graph());
        Value(output, 5)[key] = "u:secret";
        Assert.Throws<InvalidOperationException>(() => Verify(output, retained));
    }

    [TestMethod]
    public void Verify_RejectsEveryOutputInvariantAndUsesResolvedPopupCounts()
    {
        Action<JsonObject>[] faults = [
            graph => graph["attachments"]!["keep"] = new JsonObject(),
            graph => Value(graph, 1)["/Metadata"] = "20 0 R",
            graph => Objects(graph)["trailer"]!["value"]!["/Info"] = new JsonObject { ["/Author"] = "u:secret" },
            graph => Value(graph, 2)["/EmbeddedFiles"] = new JsonObject(),
            graph => Value(graph, 4).AsArray().Add("5 0 R"),
            graph => Objects(graph)["obj:20 0 R"] = JsonNode.Parse("""{"stream":{"dict":{"/Type":"21 0 R"}}}"""),
            graph => Objects(graph)["obj:20 0 R"] = JsonNode.Parse("""{"value":{"/Subtype":"22 0 R"}}""")
        ];
        foreach (var fault in faults)
        {
            var (output, _, retained) = Strip(Graph());
            Objects(output)["obj:21 0 R"] = new JsonObject { ["value"] = "/EmbeddedFile" };
            Objects(output)["obj:22 0 R"] = new JsonObject { ["value"] = "/Popup" };
            fault(output);
            Assert.Throws<InvalidOperationException>(() => Verify(output, retained));
        }
        var (valid, _, count) = Strip(Graph());
        Objects(valid)["trailer"]!["value"]!["/Info"] = new JsonObject { ["/ModDate"] = "u:kept" };
        Verify(valid, count);
    }

    [TestMethod]
    public void LimitsAndMalformedJson_HaveSpecificFixedExceptions()
    {
        Assert.Throws<PdfCleanTooComplexException>(() => PdfEmbeddedFileStripper.Parse(Encoding.UTF8.GetBytes(new string('[', 65) + new string(']', 65))));
        Assert.Throws<InvalidOperationException>(() => PdfEmbeddedFileStripper.Parse("{"u8.ToArray()));
        var graph = Graph();
        for (var number = 11; number <= 75; number++) Objects(graph)[$"obj:{number} 0 R"] = new JsonObject { ["value"] = $"{number + 1} 0 R" };
        Value(graph, 5)["/Subtype"] = "11 0 R";
        using var document = PdfEmbeddedFileStripper.Parse(Encoding.UTF8.GetBytes(graph.ToJsonString()));
        Assert.Throws<PdfCleanTooComplexException>(() => new PdfEmbeddedFileStripper(document));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfEmbeddedFileStripper.Parse("{}"u8.ToArray(), canceled.Token));
    }

    [TestMethod]
    public void ReferenceCycleAndObjectLimit_AreTooComplex()
    {
        var graph = Graph();
        Objects(graph)["obj:11 0 R"] = new JsonObject { ["value"] = "12 0 R" };
        Objects(graph)["obj:12 0 R"] = new JsonObject { ["value"] = "11 0 R" };
        Value(graph, 5)["/Subtype"] = "11 0 R";
        using (var cyclic = PdfEmbeddedFileStripper.Parse(Encoding.UTF8.GetBytes(graph.ToJsonString())))
            Assert.Throws<PdfCleanTooComplexException>(() => new PdfEmbeddedFileStripper(cyclic));
        var objects = new Dictionary<string, object>();
        for (var number = 1; number <= 100_001; number++) objects[$"obj:{number} 0 R"] = new { value = (object?)null };
        objects["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = "1 0 R" } };
        using var over = PdfEmbeddedFileStripper.Parse(JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2 }, objects } }));
        Assert.Throws<PdfCleanTooComplexException>(() => new PdfEmbeddedFileStripper(over));
    }

    [TestMethod]
    public void IdentityComparer_DistinguishesEqualDirectValuesAndSurvivesGc_WithCopiedInput()
    {
        using var input = new MemoryStream("  {\"items\":[{},{}],\"names\":{\"/Dests\":{}}}  "u8.ToArray());
        using var document = JsonDocument.Parse(input);
        var identity = new PdfJsonElementIdentityComparer(document);
        var items = document.RootElement.GetProperty("items");
        var first = items[0];
        var second = items[1];
        Assert.IsTrue(identity.Equals(first, items[0]));
        Assert.IsFalse(identity.Equals(first, second));
        Assert.AreNotEqual(identity.GetHashCode(first), identity.GetHashCode(second));
        var set = new HashSet<JsonElement>(identity) { first, second };
        GC.Collect();
        Assert.IsTrue(set.Contains(items[0]));
        Assert.IsTrue(set.Contains(items[1]));
        Assert.IsFalse(set.Contains(default));
        Assert.IsTrue(identity.Equals(default, default));
        var foreign = first.Clone();
        Assert.Throws<InvalidOperationException>(() => identity.GetHashCode(foreign));
        Assert.Throws<InvalidOperationException>(() => identity.Equals(first, foreign));
    }

    [TestMethod]
    [DataRow(5000, false)]
    [DataRow(5000, true)]
    [DataRow(20000, true)]
    public void ManyPagesAndAnnotations_StripAndVerifyFinishWithinFiveSeconds(int pages, bool af)
    {
        var bytes = ManyPages(pages, af);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timer = Stopwatch.StartNew();
        using var document = PdfEmbeddedFileStripper.Parse(bytes, deadline.Token);
        var stripper = new PdfEmbeddedFileStripper(document, deadline.Token);
        using var update = new MemoryStream();
        using (var writer = new Utf8JsonWriter(update)) stripper.Strip(writer);
        using var updated = PdfEmbeddedFileStripper.Parse(update.ToArray(), deadline.Token);
        var entries = updated.RootElement.GetProperty("qpdf")[1].EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("qpdf");
            writer.WriteStartArray();
            document.RootElement.GetProperty("qpdf")[0].WriteTo(writer);
            writer.WriteStartObject();
            foreach (var entry in document.RootElement.GetProperty("qpdf")[1].EnumerateObject())
            {
                deadline.Token.ThrowIfCancellationRequested();
                writer.WritePropertyName(entry.Name);
                (entries.TryGetValue(entry.Name, out var replacement) ? replacement : entry.Value).WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("attachments");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var result = PdfEmbeddedFileStripper.Parse(output.ToArray(), deadline.Token);
        PdfEmbeddedFileStripper.Verify(result, stripper.RetainedPopupCount, deadline.Token);
        timer.Stop();
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5), $"{pages} pages: {timer.Elapsed.TotalSeconds:F3}s");
        Assert.AreEqual(pages, stripper.RetainedPopupCount);
        Assert.IsTrue(entries.Count >= pages / 10 * 3);
        Console.WriteLine($"{pages} pages, AF={af}: Strip + Verify {timer.Elapsed.TotalSeconds:F3}s; update {update.Length} bytes.");
    }

    private static byte[] ManyPages(int pages, bool af)
    {
        var objects = new Dictionary<string, object>();
        void Value(int number, object? value) => objects[$"obj:{number} 0 R"] = new { value };
        Value(1, new Dictionary<string, object> { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R" });
        Value(2, new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Count"] = pages });
        Value(3, new Dictionary<string, object> { ["/Type"] = "/Filespec", ["/EF"] = new Dictionary<string, object>() });
        for (var index = 0; index < pages; index++)
        {
            var page = 4 + index * 3;
            var link = page + 1;
            var array = page + 2;
            var attachment = 4 + pages * 3 + index * 2;
            var popup = attachment + 1;
            var dictionary = new Dictionary<string, object> { ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/Annots"] = $"{array} 0 R", ["/MediaBox"] = new[] { 0, 0, 100, 100 } };
            if (af) dictionary["/AF"] = new[] { "3 0 R" };
            Value(page, dictionary);
            Value(link, new Dictionary<string, object> { ["/Subtype"] = "/Link", ["/Rect"] = new[] { 0, 0, 10, 10 }, ["/A"] = new Dictionary<string, object> { ["/S"] = "/URI", ["/URI"] = "u:https://example.test/" } });
            var annotations = new List<object> { $"{link} 0 R", new Dictionary<string, object> { ["/Subtype"] = "/Popup", ["/Parent"] = $"{link} 0 R" } };
            if (index % 10 == 0)
            {
                Value(attachment, new Dictionary<string, object> { ["/Type"] = "/Annot", ["/Subtype"] = "/FileAttachment", ["/Rect"] = new[] { 0, 0, 10, 10 }, ["/FS"] = "3 0 R", ["/Popup"] = $"{popup} 0 R", ["/Contents"] = "u:attachment-description" });
                Value(popup, new Dictionary<string, object> { ["/Subtype"] = "/Popup", ["/Contents"] = "u:popup-description" });
                annotations.Add($"{attachment} 0 R");
                annotations.Add($"{popup} 0 R");
            }
            Value(array, annotations);
        }
        objects["trailer"] = new { value = new Dictionary<string, object> { ["/Root"] = "1 0 R" } };
        return JsonSerializer.SerializeToUtf8Bytes(new { qpdf = new object[] { new { jsonversion = 2, pdfversion = "1.7" }, objects } });
    }
}
