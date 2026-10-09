using System.Text.Json;

namespace Amane.Pdf.Api;

internal readonly record struct PdfBox(double Left, double Bottom, double Right, double Top)
{
    internal double Width => Right - Left;
    internal double Height => Top - Bottom;
    internal PdfBox Intersect(PdfBox other)
    {
        var box = new PdfBox(Math.Max(Left, other.Left), Math.Max(Bottom, other.Bottom),
            Math.Min(Right, other.Right), Math.Min(Top, other.Top));
        if (box.Width <= 0 || box.Height <= 0) throw PdfOverlayInputException.Unsupported();
        return box;
    }
    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartArray(); writer.WriteNumberValue(Left); writer.WriteNumberValue(Bottom);
        writer.WriteNumberValue(Right); writer.WriteNumberValue(Top); writer.WriteEndArray();
    }
}

internal sealed record PdfPageAttributes(PdfBox Media, PdfBox Crop, PdfBox Trim, int Rotate,
    double UserUnit, PdfBox Bleed, PdfBox Art,
    PdfBox? DirectMedia, PdfBox? DirectCrop, PdfBox? DirectTrim, int? DirectRotate)
{
    internal int NormalizedRotate => (Rotate % 360 + 360) % 360;
    internal double DisplayWidth => (NormalizedRotate is 90 or 270 ? Crop.Height : Crop.Width) * UserUnit;
    internal double DisplayHeight => (NormalizedRotate is 90 or 270 ? Crop.Width : Crop.Height) * UserUnit;
    internal bool SameEffective(PdfPageAttributes other) => Media == other.Media && Crop == other.Crop &&
        Trim == other.Trim && Rotate == other.Rotate && UserUnit == other.UserUnit && Bleed == other.Bleed && Art == other.Art;
}

// All JsonElements belong to this bounded DOM; only numeric snapshots escape it.
internal sealed class PdfPageBoxes : IDisposable
{
    internal const int InputObjectLimit = 50_000;
    internal const int GeneratedObjectLimit = 75_000;
    internal const int JsonDepthLimit = 64;
    internal const int ReferenceDepthLimit = 32;
    private readonly JsonDocument document;
    private readonly Dictionary<PdfObjectRef, JsonElement> objects = [];
    internal IReadOnlyList<PdfObjectRef> Pages { get; }
    internal bool? Encrypted { get; }
    internal int ObjectCount => objects.Count;

    internal PdfPageBoxes(ReadOnlyMemory<byte> bytes, long byteLimit, int objectLimit, CancellationToken token)
    {
        if (bytes.Length > byteLimit) throw PdfOverlayInputException.TooComplex();
        CheckJson(bytes.Span, token);
        try { document = JsonDocument.Parse(bytes, new() { MaxDepth = JsonDepthLimit }); }
        catch (JsonException) { throw new InvalidOperationException(PdfOverlayBuilder.Failure); }
        try
        {
            var root = document.RootElement;
            Encrypted = root.TryGetProperty("encrypt", out var encryption) ? encryption.GetProperty("encrypted").GetBoolean() : null;
            var qpdf = root.GetProperty("qpdf");
            if (qpdf.GetArrayLength() != 2 || qpdf[0].GetProperty("jsonversion").GetInt32() != 2)
                throw new InvalidOperationException();
            foreach (var property in qpdf[1].EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                if (property.Name == "trailer") continue;
                var reference = property.Name.StartsWith("obj:", StringComparison.Ordinal)
                    ? PdfObjectRef.Parse(property.Name[4..]) : null;
                if (reference is null || !objects.TryAdd(reference.Value, property.Value))
                    throw new InvalidOperationException();
                if (objects.Count > objectLimit) throw PdfOverlayInputException.TooComplex();
            }
            var pages = new List<PdfObjectRef>();
            var seen = new HashSet<PdfObjectRef>();
            foreach (var page in root.GetProperty("pages").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var reference = PdfObjectRef.Parse(page.GetProperty("object"));
                if (reference is null || !objects.ContainsKey(reference.Value) || !seen.Add(reference.Value))
                    throw new InvalidOperationException();
                pages.Add(reference.Value);
            }
            Pages = pages;
        }
        catch (PdfOverlayInputException) { document.Dispose(); throw; }
        catch (OperationCanceledException) { document.Dispose(); throw; }
        catch (Exception) { document.Dispose(); throw new InvalidOperationException(PdfOverlayBuilder.Failure); }
    }

    private static void CheckJson(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = int.MaxValue });
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.CurrentDepth >= JsonDepthLimit && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    throw PdfOverlayInputException.TooComplex();
            }
        }
        catch (JsonException) { throw new InvalidOperationException(PdfOverlayBuilder.Failure); }
    }

    internal JsonElement Dictionary(PdfObjectRef reference)
    {
        if (!objects.TryGetValue(reference, out var obj) || obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            throw PdfOverlayInputException.Unsupported();
        return value;
    }
    private JsonElement Resolve(JsonElement value, CancellationToken token)
    {
        var seen = new HashSet<PdfObjectRef>();
        while (PdfObjectRef.Parse(value) is { } reference)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(reference) || seen.Count > ReferenceDepthLimit) throw PdfOverlayInputException.TooComplex();
            if (!objects.TryGetValue(reference, out var obj) || obj.ValueKind != JsonValueKind.Object ||
                !obj.TryGetProperty("value", out value)) throw PdfOverlayInputException.Unsupported();
        }
        token.ThrowIfCancellationRequested();
        return value;
    }
    private JsonElement? Direct(JsonElement dictionary, string key, CancellationToken token)
    {
        if (!dictionary.TryGetProperty(key, out var value)) return null;
        value = Resolve(value, token);
        return value.ValueKind == JsonValueKind.Null ? null : value;
    }
    internal JsonElement? Inherited(JsonElement page, string key, CancellationToken token, bool skipDirect = false)
    {
        var seen = new HashSet<PdfObjectRef>();
        var depth = 0;
        JsonElement? result = null;
        // Validate the entire Parent chain even when the attribute is local.
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (++depth > ReferenceDepthLimit) throw PdfOverlayInputException.TooComplex();
            if (!skipDirect && result is null) result = Direct(page, key, token);
            skipDirect = false;
            var parent = Direct(page, "/Parent", token);
            if (parent is null) break;
            // Direct resolves references, so detect cycles using the original Parent reference.
            var raw = page.GetProperty("/Parent");
            if (PdfObjectRef.Parse(raw) is { } reference && !seen.Add(reference)) throw PdfOverlayInputException.TooComplex();
            if (parent.Value.ValueKind != JsonValueKind.Object) throw PdfOverlayInputException.Unsupported();
            page = parent.Value;
        }
        return result;
    }
    internal PdfBox Box(JsonElement value, CancellationToken token)
    {
        value = Resolve(value, token);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4) throw PdfOverlayInputException.Unsupported();
        var box = new PdfBox(Number(value[0], token), Number(value[1], token), Number(value[2], token), Number(value[3], token));
        if (!double.IsFinite(box.Width) || !double.IsFinite(box.Height) || box.Width <= 0 || box.Height <= 0)
            throw PdfOverlayInputException.Unsupported();
        return box;
    }
    private double Number(JsonElement value, CancellationToken token)
    {
        value = Resolve(value, token);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw PdfOverlayInputException.Unsupported();
        return number;
    }
    internal int Rotation(JsonElement? value, CancellationToken token)
    {
        if (value is null) return 0;
        var number = Number(value.Value, token);
        if (number < int.MinValue || number > int.MaxValue || number % 90 != 0) throw PdfOverlayInputException.Unsupported();
        return (int)number;
    }
    internal PdfBox? OptionalBox(JsonElement? value, CancellationToken token) => value is { } box ? Box(box, token) : null;
    internal PdfPageAttributes Read(int pageIndex, CancellationToken token)
    {
        var page = Dictionary(Pages[pageIndex]);
        var media = OptionalBox(Inherited(page, "/MediaBox", token), token) ?? throw PdfOverlayInputException.Unsupported();
        var crop = (OptionalBox(Inherited(page, "/CropBox", token), token) ?? media).Intersect(media);
        var trim = OptionalBox(Direct(page, "/TrimBox", token), token) ?? crop;
        var rotate = Rotation(Inherited(page, "/Rotate", token), token);
        var unitValue = Direct(page, "/UserUnit", token);
        var unit = unitValue is { } value ? Number(value, token) : 1;
        if (unit is <= 0 or > 75000) throw PdfOverlayInputException.Unsupported();
        var result = new PdfPageAttributes(media, crop, trim, rotate, unit,
            OptionalBox(Direct(page, "/BleedBox", token), token) ?? crop,
            OptionalBox(Direct(page, "/ArtBox", token), token) ?? crop,
            OptionalBox(Direct(page, "/MediaBox", token), token), OptionalBox(Direct(page, "/CropBox", token), token),
            OptionalBox(Direct(page, "/TrimBox", token), token),
            Direct(page, "/Rotate", token) is { } directRotate ? Rotation(directRotate, token) : null);
        if (!double.IsFinite(result.DisplayWidth) || !double.IsFinite(result.DisplayHeight) ||
            result.DisplayWidth is <= 0 or > 14400 || result.DisplayHeight is <= 0 or > 14400)
            throw PdfOverlayInputException.Unsupported();
        return result;
    }

    internal void WriteUpdate(Utf8JsonWriter writer, int from, IReadOnlyList<PdfPageAttributes> originals,
        bool restore, CancellationToken token)
    {
        writer.WriteStartObject(); writer.WriteStartArray("qpdf"); writer.WriteStartObject();
        writer.WriteNumber("jsonversion", 2); writer.WriteEndObject(); writer.WriteStartObject();
        for (var i = 0; i < originals.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var original = originals[i];
            var reference = Pages[from - 1 + i];
            var page = Dictionary(reference);
            writer.WriteStartObject(reference.Key); writer.WriteStartObject("value");
            foreach (var property in page.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                if (property.Name is not ("/MediaBox" or "/CropBox" or "/TrimBox" or "/Rotate")) property.WriteTo(writer);
            }
            if (!restore)
            {
                foreach (var name in new[] { "/MediaBox", "/CropBox", "/TrimBox" })
                { writer.WritePropertyName(name); original.Crop.Write(writer); }
                writer.WriteNumber("/Rotate", original.NormalizedRotate);
            }
            else
            {
                var media = original.DirectMedia ?? original.Media;
                if (original.DirectMedia is not null ||
                    OptionalBox(Inherited(page, "/MediaBox", token, true), token) != original.Media)
                { writer.WritePropertyName("/MediaBox"); media.Write(writer); }
                var cropMatches = false;
                if (original.DirectCrop is null)
                {
                    var inheritedCrop = OptionalBox(Inherited(page, "/CropBox", token, true), token) ?? media;
                    cropMatches = inheritedCrop.Right > media.Left && inheritedCrop.Left < media.Right &&
                        inheritedCrop.Top > media.Bottom && inheritedCrop.Bottom < media.Top &&
                        inheritedCrop.Intersect(media) == original.Crop;
                }
                if (original.DirectCrop is not null || !cropMatches)
                { writer.WritePropertyName("/CropBox"); (original.DirectCrop ?? original.Crop).Write(writer); }
                if (original.DirectTrim is { } trim) { writer.WritePropertyName("/TrimBox"); trim.Write(writer); }
                if (original.DirectRotate is not null || Rotation(Inherited(page, "/Rotate", token, true), token) != original.Rotate)
                    writer.WriteNumber("/Rotate", original.DirectRotate ?? original.Rotate);
            }
            writer.WriteEndObject(); writer.WriteEndObject(); writer.Flush();
        }
        writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
    }
    public void Dispose() => document.Dispose();
}
