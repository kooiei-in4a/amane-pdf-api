using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfComposeTests
{
    private const string Endpoint = "/api/pdf/compose";
    private const string ImageId = "as_01";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void MatchesEveryLayoutVector()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "layout-v1-vectors.json")));
        var vectors = fixture.RootElement.GetProperty("vectors");
        Assert.AreEqual(14, vectors.GetArrayLength());
        foreach (var vector in vectors.EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString();
            var expected = vector.GetProperty("expected");
            var actual = ComposeText.Wrap(vector.GetProperty("text").GetString()!, vector.GetProperty("size").GetInt32(),
                vector.GetProperty("boxWidth").GetInt32(), vector.GetProperty("boxHeight").GetInt32(),
                vector.GetProperty("lineHeight").GetInt32(), "gothic");
            Assert.AreEqual(expected.TryGetProperty("error", out var error) ? error.GetString() : null, actual.Error, name);
            if (expected.TryGetProperty("index", out var index)) Assert.AreEqual(index.GetInt32(), actual.Index, name);
            if (expected.TryGetProperty("lines", out var lines))
                CollectionAssert.AreEqual(lines.EnumerateArray().Select(line => line.GetString()).ToArray(), actual.Lines, name);
            if (expected.TryGetProperty("lineWidths_milliEm", out var widths))
                CollectionAssert.AreEqual(widths.EnumerateArray().Select(width => width.GetInt32()).ToArray(), actual.Widths, name);
        }
    }

    [TestMethod]
    public async Task ImageManifestAndPartsMustMatchBeforeRendering()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        var png = StampFixtures.Png();
        foreach (var fault in new[] { "missing-hash", "mismatch", "unreferenced", "missing-part", "extra-part", "duplicate-part", "duplicate-id", "no-images-key" })
        {
            var doc = ImageLayout(png);
            var manifest = doc["images"]!.AsArray();
            if (fault == "missing-hash") manifest[0]!.AsObject().Remove("sha256");
            if (fault == "mismatch") manifest[0]!["sha256"] = new string('a', 64);
            if (fault == "unreferenced") doc["elements"]!.AsArray().Clear();
            if (fault == "duplicate-id") manifest.Add(manifest[0]!.DeepClone());
            if (fault == "no-images-key") doc.Remove("images");
            using var form = Form(doc.ToJsonString());
            if (fault != "missing-part") form.Add(new ByteArrayContent(png), "image", ImageId);
            if (fault == "extra-part") form.Add(new ByteArrayContent(png), "image", "as_extra");
            if (fault == "duplicate-part") form.Add(new ByteArrayContent(png), "image", ImageId);
            using var response = await test.Client.PostAsync(Endpoint, form);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, fault);
            test.AssertClean();
        }
        Assert.IsFalse(test.Logs.Any(line => line.Contains(ImageId, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task StrictJsonAndExactSizeBoundaries()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        var valid = ImageLayout(StampFixtures.Png()).ToJsonString();
        foreach (var invalid in new[] { "\ufeff" + valid, valid.Replace("\"blank\":true", "\"blank\":true,\"blank\":true"),
            valid.Replace("\"blank\":true", "\"blank\":true,\"extra\":0"), valid.Replace("59528", "59528.0"),
            valid.Replace("59528", "59528e0"), valid.Replace("\"as_01\"", "\"\\ud800\"") })
        {
            using var form = Form(invalid);
            using var response = await test.Client.PostAsync(Endpoint, form);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            test.AssertClean();
        }
        foreach (var extra in new[] { 0, 1 })
        {
            using var form = Form(valid.PadRight(ComposeLayout.JsonBytes + extra));
            using var response = await test.Client.PostAsync(Endpoint, form);
            Assert.AreEqual(extra == 0 ? 400 : 413, (int)response.StatusCode);
            test.AssertClean();
        }
    }

    [TestMethod]
    public async Task BlankRendersAndReportsOverflowIds()
    {
        await using var test = new PdfTestContext();
        var png = StampFixtures.Png(transparency: true);
        var doc = ImageLayout(png);
        var elements = doc["elements"]!.AsArray();
        elements.Add(Text("el_text", "一行目\n二行目", 30000));
        using var good = Form(doc.ToJsonString());
        good.Add(new ByteArrayContent(png), "image", ImageId);
        using var result = await test.Client.PostAsync(Endpoint, good);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
        Assert.IsTrue((await result.Content.ReadAsByteArrayAsync()).AsSpan().StartsWith("%PDF-"u8));
        test.AssertClean();

        elements[1]!["rect"]![2] = 100;
        using var bad = Form(doc.ToJsonString());
        bad.Add(new ByteArrayContent(png), "image", ImageId);
        using var response = await test.Client.PostAsync(Endpoint, bad);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual("text-overflow", body["reason"]!.GetValue<string>());
        Assert.AreEqual("el_text", body["elements"]![0]!.GetValue<string>());
        test.AssertClean();
    }

    [TestMethod]
    public void RepeatedFieldTextIsNotLimitedByAnAggregateOfExpandedElements()
    {
        var doc = ImageLayout([]);
        doc["images"]!.AsArray().Clear();
        var elements = doc["elements"]!.AsArray();
        elements.Clear();
        for (var i = 0; i < 11; i++) elements.Add(Text($"el_{i}", new string('A', 1000), 30000));
        Assert.AreEqual(11, ComposeLayout.Parse(Encoding.UTF8.GetBytes(doc.ToJsonString()), false, ["gothic"]).Elements.Length);
    }

    private static JsonObject ImageLayout(byte[] bytes) => JsonSerializer.SerializeToNode(new
    {
        layout = ComposeLayout.Version, blank = true,
        pages = new[] { new { index = 1, width = 59528, height = 84189 } },
        fonts = new[] { new { id = "gothic", sha256 = ComposeLayout.GothicHash } },
        images = new[] { new { id = ImageId, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } },
        elements = new[] { new { id = "el_image", page = 1, kind = "image", rect = new[] { 1000, 1000, 6400, 6400 }, image = ImageId } }
    }, Web)!.AsObject();

    private static JsonNode Text(string id, string text, int width) => JsonSerializer.SerializeToNode(new
    {
        id, page = 1, kind = "text", rect = new[] { 1000, 10000, width, 8000 }, text,
        font = "gothic", size = 1200, color = "#000000", align = "left", valign = "top", lineHeight = 120
    }, Web)!;

    private static MultipartFormDataContent Form(string layout)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(layout, Encoding.UTF8, "application/json"), "layout");
        return form;
    }
}
