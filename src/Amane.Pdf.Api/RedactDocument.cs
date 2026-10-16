using System.Text.Json;

namespace Amane.Pdf.Api;

// This object borrows the single bounded DOM owned by PdfPageBoxes.
internal sealed class RedactDocument(PdfPageBoxes boxes, CancellationToken token)
{
    internal const long DctSamples = 90_000_000, JpxSamples = 30_000_000, Jbig2Pixels = 1_000_000_000;
    private readonly PdfJsonElementIdentityComparer identity = new(boxes.Document);
    private int visits;
    private Dictionary<int, HashSet<string>>? pageFonts;

    private void Tick(int depth)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 32 || ++visits > 1_000_000) throw PdfRedactException.Complex();
    }

    private JsonElement Content(JsonElement value)
    {
        var seen = new HashSet<PdfObjectRef>();
        while (PdfObjectRef.Parse(value) is { } reference)
        {
            Tick(seen.Count);
            if (!seen.Add(reference) || !boxes.Objects.TryGetValue(reference, out var wrapper)) throw PdfRedactException.Unsupported();
            value = wrapper.TryGetProperty("value", out var raw) ? raw : wrapper.GetProperty("stream").GetProperty("dict");
        }
        return value;
    }

    private JsonElement Property(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var raw) ? Content(raw) : default;
    private string? Name(JsonElement value, string key) => Property(value, key) is { ValueKind: JsonValueKind.String } name ? name.GetString() : null;

    internal int[] RasterizedPages(RedactRegions regions)
    {
        var fonts = new Dictionary<int, HashSet<string>>();
        for (var index = 0; index < boxes.Pages.Count; index++) fonts[index + 1] = Scan(index, false);
        pageFonts = fonts;
        var targetKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in regions.Pages) targetKeys.UnionWith(fonts[page]);
        var result = fonts.Where(p => regions.Pages.Contains(p.Key) || p.Value.Overlaps(targetKeys)).Select(p => p.Key).Order().ToArray();
        if (result.Length > RedactRegions.MaxRasterizedPages) throw new PdfRedactException(PdfRedactReason.TooManyPages);
        return result;
    }

    internal void CheckImages(int pageNumber) => Scan(pageNumber - 1, true);

    private HashSet<string> Scan(int pageIndex, bool images)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var resourcesSeen = new HashSet<JsonElement>(identity);
        var imagesSeen = new HashSet<JsonElement>(identity);
        void FontGraph(JsonElement value, int depth, HashSet<PdfObjectRef> seen)
        {
            Tick(depth);
            if (PdfObjectRef.Parse(value) is { } reference)
            {
                keys.Add(reference.Text);
                if (!seen.Add(reference)) return;
                if (!boxes.Objects.TryGetValue(reference, out var wrapper)) throw PdfRedactException.Unsupported();
                FontGraph(wrapper.TryGetProperty("value", out var raw) ? raw : wrapper.GetProperty("stream").GetProperty("dict"), depth + 1, seen);
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                    if (property.Name != "/Parent") FontGraph(property.Value, depth + 1, seen);
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) FontGraph(item, depth + 1, seen);
            }
        }

        void Font(JsonElement font, int depth)
        {
            Tick(depth);
            var dictionary = Content(font);
            if (dictionary.ValueKind != JsonValueKind.Object) throw PdfRedactException.Unsupported();
            if (!images)
            {
                keys.Add(PdfObjectRef.Parse(font)?.Text ?? "direct:" + dictionary.GetRawText());
                FontGraph(font, depth + 1, []);
            }
            if (Name(dictionary, "/Subtype") == "/Type3")
                Resources(Property(dictionary, "/Resources"), depth + 1);
        }

        void Image(JsonElement image, JsonElement colorSpaces, int depth)
        {
            Tick(depth);
            image = Content(image);
            if (image.ValueKind != JsonValueKind.Object || !imagesSeen.Add(image)) return;
            CheckImage(image, colorSpaces);
            foreach (var key in new[] { "/SMask", "/Mask" })
                if (image.TryGetProperty(key, out var mask)) Image(mask, colorSpaces, depth + 1);
        }

        void Resources(JsonElement resources, int depth)
        {
            Tick(depth);
            resources = Content(resources);
            if (resources.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
            if (resources.ValueKind != JsonValueKind.Object) throw PdfRedactException.Unsupported();
            if (!resourcesSeen.Add(resources)) return;
            var fonts = Property(resources, "/Font");
            if (fonts.ValueKind == JsonValueKind.Object)
                foreach (var font in fonts.EnumerateObject()) Font(font.Value, depth + 1);
            var colorSpaces = Property(resources, "/ColorSpace");
            foreach (var kind in new[] { "/XObject", "/Pattern" })
            {
                var group = Property(resources, kind);
                if (group.ValueKind != JsonValueKind.Object) continue;
                foreach (var child in group.EnumerateObject())
                {
                    Tick(depth);
                    var dictionary = Content(child.Value);
                    if (dictionary.ValueKind != JsonValueKind.Object) throw PdfRedactException.Unsupported();
                    if (kind == "/XObject" && Name(dictionary, "/Subtype") == "/Image")
                    {
                        if (images) Image(dictionary, colorSpaces, depth + 1);
                    }
                    else if (kind == "/Pattern" || Name(dictionary, "/Subtype") == "/Form")
                        Resources(Property(dictionary, "/Resources"), depth + 1);
                }
            }
            var states = Property(resources, "/ExtGState");
            if (states.ValueKind == JsonValueKind.Object)
                foreach (var state in states.EnumerateObject())
                {
                    var dictionary = Content(state.Value);
                    var font = Property(dictionary, "/Font");
                    if (font.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                    {
                        if (font.ValueKind != JsonValueKind.Array || font.GetArrayLength() != 2) throw PdfRedactException.Unsupported();
                        Font(font[0], depth + 1);
                    }
                    var group = Property(Property(dictionary, "/SMask"), "/G");
                    if (group.ValueKind == JsonValueKind.Object)
                        Resources(Property(group, "/Resources"), depth + 1);
                }
        }
        var page = boxes.Dictionary(boxes.Pages[pageIndex]);
        Resources(boxes.Inherited(page, "/Resources", token) ?? default, 0);
        return keys;
    }

    private void CheckImage(JsonElement image, JsonElement colorSpaces)
    {
        long Positive(string key)
        {
            var value = Property(image, key);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number is <= 0 or > int.MaxValue)
                throw PdfRedactException.Unsupported();
            return number;
        }
        var width = Positive("/Width"); var height = Positive("/Height");
        var filter = Property(image, "/Filter");
        if (filter.ValueKind == JsonValueKind.Array && filter.GetArrayLength() > 0) filter = Content(filter[filter.GetArrayLength() - 1]);
        var last = filter.ValueKind == JsonValueKind.String ? filter.GetString() : null;
        var components = Property(image, "/ImageMask").ValueKind == JsonValueKind.True ? 1 : Components(Property(image, "/ColorSpace"), colorSpaces, 0);
        var maximum = last switch { "/DCTDecode" or "/DCT" => DctSamples, "/JPXDecode" => JpxSamples, "/JBIG2Decode" => Jbig2Pixels, _ => long.MaxValue };
        if (maximum == long.MaxValue) return;
        if (last == "/JBIG2Decode") components = 1;
        if (width > maximum / height / components) throw PdfRedactException.Complex();
    }

    private long Components(JsonElement space, JsonElement names, int depth)
    {
        Tick(depth);
        space = Content(space);
        if (space.ValueKind == JsonValueKind.String)
        {
            var name = space.GetString();
            if (name is "/DeviceGray" or "/G") return 1;
            if (name is "/DeviceRGB" or "/RGB") return 3;
            if (name is "/DeviceCMYK" or "/CMYK") return 4;
            if (names.ValueKind == JsonValueKind.Object && names.TryGetProperty(name!, out var alias))
                return Components(alias, names, depth + 1);
        }
        if (space.ValueKind == JsonValueKind.Array && space.GetArrayLength() > 0)
        {
            var name = Content(space[0]);
            if (name.ValueKind == JsonValueKind.String)
                switch (name.GetString())
                {
                    case "/CalGray": case "/Indexed": case "/I": case "/Separation": return 1;
                    case "/CalRGB": case "/Lab": return 3;
                    case "/ICCBased":
                        var n = space.GetArrayLength() > 1 ? Property(Content(space[1]), "/N") : default;
                        return n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var count) && count > 0 ? count : 4;
                    case "/DeviceN":
                        var list = space.GetArrayLength() > 1 ? Content(space[1]) : default;
                        return list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 ? list.GetArrayLength() : 4;
                }
        }
        return 4;
    }

    // Shared images and Forms can contain data clipped out of surviving pages.
    // Reject every surviving reference into the target page's object graph,
    // including private references, instead of expanding the approved page set.
    internal void CheckSurvivingReferences(RedactRegions regions, IReadOnlyList<int> rasterized)
    {
        var denied = rasterized.Select(number => boxes.Pages[number - 1]).ToHashSet();
        foreach (var number in rasterized)
        {
            var page = boxes.Dictionary(boxes.Pages[number - 1]);
            if (!page.TryGetProperty("/Contents", out var contents)) continue;
            if (PdfObjectRef.Parse(contents) is { } reference) denied.Add(reference);
            var resolved = Content(contents);
            if (resolved.ValueKind == JsonValueKind.Array)
                foreach (var item in resolved.EnumerateArray())
                    if (PdfObjectRef.Parse(item) is { } stream) denied.Add(stream);
        }
        if (pageFonts is null) throw Invalid();
        foreach (var number in regions.Pages)
            foreach (var key in pageFonts[number])
                if (PdfObjectRef.Parse(key) is { } font) denied.Add(font);
        var (catalog, root) = Roots();
        var rewritten = boxes.Pages.Append(catalog).Append(root).ToHashSet();
        var collected = new HashSet<PdfObjectRef>();
        var seen = new HashSet<PdfObjectRef>();
        void Walk(JsonElement value, int depth, bool collect)
        {
            Tick(depth);
            if (PdfObjectRef.Parse(value) is { } reference)
            {
                if (!collect && denied.Contains(reference)) throw PdfRedactException.Unsupported();
                // Actual page-tree dictionaries are rewritten or inspected separately.
                // A dictionary merely declaring /Type /Catalog or /Pages is still walked.
                if (rewritten.Contains(reference)) return;
                if (collect) denied.Add(reference);
                if (!(collect ? collected : seen).Add(reference)) return;
                if (!boxes.Objects.TryGetValue(reference, out var wrapper)) throw PdfRedactException.Unsupported();
                var content = wrapper.TryGetProperty("value", out var raw) ? raw : wrapper.GetProperty("stream").GetProperty("dict");
                Walk(content, depth + 1, collect);
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject()) Walk(property.Value, depth + 1, collect);
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) Walk(item, depth + 1, collect);
        }
        foreach (var number in regions.Pages)
        {
            var page = boxes.Dictionary(boxes.Pages[number - 1]);
            foreach (var property in page.EnumerateObject())
                if (property.Name != "/Parent") Walk(property.Value, 0, true);
            // Include effective resources even when they were inherited.
            Walk(boxes.Inherited(page, "/Resources", token) ?? default, 0, true);
        }
        for (var i = 0; i < boxes.Pages.Count; i++)
            if (!rasterized.Contains(i + 1))
                foreach (var property in boxes.Dictionary(boxes.Pages[i]).EnumerateObject())
                    if (property.Name is not ("/Parent" or "/Annots")) Walk(property.Value, 0, false);
    }

    private (PdfObjectRef Catalog, PdfObjectRef Root) Roots()
    {
        var objects = boxes.Document.RootElement.GetProperty("qpdf")[1];
        var catalog = PdfObjectRef.Parse(objects.GetProperty("trailer").GetProperty("value").GetProperty("/Root")) ?? throw Invalid();
        var root = PdfObjectRef.Parse(boxes.Dictionary(catalog).GetProperty("/Pages")) ?? throw Invalid();
        return (catalog, root);
    }

    internal void WriteFinal(Utf8JsonWriter writer, IReadOnlyList<RedactPage> replacements, int expectedCount)
    {
        var (catalog, root) = Roots();
        var tree = boxes.Dictionary(root);
        var kids = Property(tree, "/Kids");
        Require(boxes.Pages.Count == expectedCount && kids.ValueKind == JsonValueKind.Array && kids.GetArrayLength() == expectedCount);
        for (var i = 0; i < boxes.Pages.Count; i++)
            Require(PdfObjectRef.Parse(kids[i]) == boxes.Pages[i] &&
                PdfObjectRef.Parse(boxes.Dictionary(boxes.Pages[i]).GetProperty("/Parent")) == root);
        var maximum = boxes.Document.RootElement.GetProperty("qpdf")[0].GetProperty("maxobjectid").GetInt32();
        Require(boxes.Objects.Keys.All(reference => reference.Number <= maximum));
        if (maximum > int.MaxValue - 3 * replacements.Count) throw PdfRedactException.Complex();
        var ids = replacements.Select((page, i) => (page.Number, Id: maximum + 1 + i * 3)).ToDictionary(x => x.Number, x => x.Id);
        writer.WriteStartObject(); writer.WriteStartArray("qpdf");
        writer.WriteStartObject(); writer.WriteNumber("jsonversion", 2); writer.WriteEndObject(); writer.WriteStartObject();
        void Start(PdfObjectRef reference, string kind) { writer.WriteStartObject(reference.Key); writer.WriteStartObject(kind); }
        void End() { writer.WriteEndObject(); writer.WriteEndObject(); }
        Start(catalog, "value"); writer.WriteString("/Type", "/Catalog"); writer.WriteString("/Pages", root.Text); End();
        Start(root, "value"); writer.WriteString("/Type", "/Pages"); writer.WriteNumber("/Count", expectedCount); writer.WriteStartArray("/Kids");
        for (var i = 0; i < boxes.Pages.Count; i++)
            writer.WriteStringValue(ids.TryGetValue(i + 1, out var id) ? new PdfObjectRef(id, 0).Text : boxes.Pages[i].Text);
        writer.WriteEndArray(); End();
        foreach (var page in replacements)
        {
            token.ThrowIfCancellationRequested();
            var id = ids[page.Number];
            Start(new(id, 0), "value");
            writer.WriteString("/Type", "/Page"); writer.WriteString("/Parent", root.Text);
            writer.WriteStartArray("/MediaBox"); writer.WriteNumberValue(0); writer.WriteNumberValue(0);
            writer.WriteRawValue(page.Raster.WidthPoints); writer.WriteRawValue(page.Raster.HeightPoints); writer.WriteEndArray();
            writer.WriteStartObject("/Resources"); writer.WriteStartObject("/XObject");
            writer.WriteString("/Im0", new PdfObjectRef(id + 2, 0).Text); writer.WriteEndObject(); writer.WriteEndObject();
            writer.WriteString("/Contents", new PdfObjectRef(id + 1, 0).Text); End();
            Start(new(id + 1, 0), "stream"); writer.WriteStartObject("dict"); writer.WriteEndObject();
            writer.WriteBase64String("data", page.Raster.Content); End();
            Start(new(id + 2, 0), "stream"); writer.WriteStartObject("dict");
            writer.WriteString("/Type", "/XObject"); writer.WriteString("/Subtype", "/Image");
            writer.WriteNumber("/Width", page.Raster.Width); writer.WriteNumber("/Height", page.Raster.Height);
            writer.WriteString("/ColorSpace", "/DeviceRGB"); writer.WriteNumber("/BitsPerComponent", 8);
            writer.WriteString("/Filter", "/DCTDecode"); writer.WriteEndObject(); writer.WriteString("datafile", page.JpegPath); End();
            writer.Flush();
        }
        for (var i = 0; i < boxes.Pages.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (ids.ContainsKey(i + 1)) continue;
            var dictionary = boxes.Dictionary(boxes.Pages[i]);
            if (!dictionary.TryGetProperty("/Annots", out _)) continue;
            Start(boxes.Pages[i], "value");
            foreach (var property in dictionary.EnumerateObject())
                if (property.Name != "/Annots") property.WriteTo(writer);
            End(); writer.Flush();
        }
        writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
    }

    internal Dictionary<PdfObjectRef, byte[]> Verify(IReadOnlyList<RedactPage> replacements, int count)
    {
        var (catalog, root) = Roots();
        var qpdf = boxes.Document.RootElement.GetProperty("qpdf")[1];
        Keys(qpdf.GetProperty("trailer").GetProperty("value"), ["/Root", "/Size", "/ID"], false);
        var dictionary = boxes.Dictionary(catalog);
        Keys(dictionary, ["/Type", "/Pages"], true); Require(Name(dictionary, "/Type") == "/Catalog");
        var tree = boxes.Dictionary(root);
        Keys(tree, ["/Type", "/Kids", "/Count"], true);
        Require(Name(tree, "/Type") == "/Pages" && tree.GetProperty("/Count").GetInt32() == count && boxes.Pages.Count == count);
        var kids = tree.GetProperty("/Kids"); Require(kids.GetArrayLength() == count);
        var contents = new Dictionary<PdfObjectRef, byte[]>();
        foreach (var pair in boxes.Objects)
            if (pair.Value.TryGetProperty("value", out var value) && Name(value, "/Type") == "/Page")
                Require(boxes.Pages.Contains(pair.Key));
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var page = boxes.Dictionary(boxes.Pages[i]);
            Require(PdfObjectRef.Parse(kids[i]) == boxes.Pages[i] && !page.TryGetProperty("/Annots", out _));
        }
        foreach (var expected in replacements)
        {
            var page = boxes.Dictionary(boxes.Pages[expected.Number - 1]);
            Keys(page, ["/Type", "/Parent", "/MediaBox", "/Resources", "/Contents"], true);
            Require(Name(page, "/Type") == "/Page" && PdfObjectRef.Parse(page.GetProperty("/Parent")) == root);
            var box = page.GetProperty("/MediaBox");
            Require(box.GetArrayLength() == 4 && box[0].GetDecimal() == 0 && box[1].GetDecimal() == 0 &&
                box[2].GetDecimal() == decimal.Parse(expected.Raster.WidthPoints, System.Globalization.CultureInfo.InvariantCulture) &&
                box[3].GetDecimal() == decimal.Parse(expected.Raster.HeightPoints, System.Globalization.CultureInfo.InvariantCulture));
            var resources = page.GetProperty("/Resources"); Keys(resources, ["/XObject"], true);
            var xobjects = resources.GetProperty("/XObject"); Keys(xobjects, ["/Im0"], true);
            var imageRef = PdfObjectRef.Parse(xobjects.GetProperty("/Im0")) ?? throw Invalid();
            var wrapper = boxes.Objects[imageRef];
            Require(wrapper.TryGetProperty("stream", out var stream));
            var image = stream.GetProperty("dict");
            Keys(image, ["/Type", "/Subtype", "/Width", "/Height", "/ColorSpace", "/BitsPerComponent", "/Filter", "/Length"], false);
            Require(Name(image, "/Type") == "/XObject" && Name(image, "/Subtype") == "/Image" &&
                image.GetProperty("/Width").GetInt32() == expected.Raster.Width && image.GetProperty("/Height").GetInt32() == expected.Raster.Height &&
                Name(image, "/ColorSpace") == "/DeviceRGB" && image.GetProperty("/BitsPerComponent").GetInt32() == 8 &&
                Name(image, "/Filter") == "/DCTDecode");
            var contentRef = PdfObjectRef.Parse(page.GetProperty("/Contents")) ?? throw Invalid();
            Require(boxes.Objects[contentRef].TryGetProperty("stream", out _));
            Require(contents.TryAdd(contentRef, expected.Raster.Content));
        }
        PdfEmbeddedFileStripper.Verify(boxes.Document, 0, token);
        return contents;
    }

    internal static void VerifyContents(JsonDocument document, IReadOnlyDictionary<PdfObjectRef, byte[]> expected)
    {
        var objects = document.RootElement.GetProperty("qpdf")[1];
        foreach (var pair in expected)
        {
            var stream = objects.GetProperty(pair.Key.Key).GetProperty("stream");
            Require(stream.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String);
            var dictionary = stream.GetProperty("dict");
            Require(!dictionary.TryGetProperty("/Filter", out _) && !dictionary.TryGetProperty("/DecodeParms", out _));
            Require(data.GetBytesFromBase64().AsSpan().SequenceEqual(pair.Value));
        }
    }

    private static void Keys(JsonElement value, string[] keys, bool exact)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var names = value.EnumerateObject().Select(x => x.Name).ToArray();
        Require(names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            names.All(x => keys.Contains(x, StringComparer.Ordinal)) && (!exact || names.Length == keys.Length));
    }
    private static void Require(bool valid) { if (!valid) throw Invalid(); }
    private static InvalidOperationException Invalid() => new("PDF output validation failed.");
}
