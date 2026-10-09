using System.Text.Json;

namespace Amane.Pdf.Api;

// JsonElement identity keeps direct dictionaries distinct, while resolving references
// to the same element makes aliases/shared arrays use the same removal plan.
internal sealed class PdfEmbeddedFileStripper
{
    private static readonly HashSet<string> AnnotationKeys = ["/Type", "/Subtype", "/Rect"];
    private readonly JsonDocument document;
    private readonly CancellationToken token;
    private readonly JsonElement objects;
    private readonly Dictionary<PdfObjectRef, JsonElement> references = [];
    private readonly List<JsonElement> dictionaries = [];
    private readonly PdfJsonElementIdentityComparer identity;
    private readonly HashSet<JsonElement> attachments;
    private readonly HashSet<JsonElement> popups;
    private readonly HashSet<JsonElement> nullObjects;
    private readonly HashSet<JsonElement> annotationArrays;
    private readonly HashSet<JsonElement> changedObjects;
    private readonly JsonElement catalogNames;
    internal bool HasChanges => changedObjects.Count != 0;
    internal int RetainedPopupCount { get; }

    internal static JsonDocument Parse(byte[] bytes, CancellationToken token = default)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = int.MaxValue });
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= 64)
                    throw new PdfCleanTooComplexException();
            }
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException) { throw InvalidMetadata(); }
    }

    internal PdfEmbeddedFileStripper(JsonDocument document, CancellationToken token = default)
        : this(document, token, planChanges: true) { }

    private PdfEmbeddedFileStripper(JsonDocument document, CancellationToken token, bool planChanges)
    {
        this.document = document;
        this.token = token;
        identity = new(document);
        attachments = new(identity);
        popups = new(identity);
        nullObjects = new(identity);
        annotationArrays = new(identity);
        changedObjects = new(identity);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("qpdf", out var qpdf) || qpdf.ValueKind != JsonValueKind.Array ||
            qpdf.GetArrayLength() != 2 || qpdf[0].ValueKind != JsonValueKind.Object || qpdf[1].ValueKind != JsonValueKind.Object)
            throw InvalidMetadata();
        objects = qpdf[1];
        foreach (var entry in objects.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (entry.Name != "trailer")
            {
                if (!entry.Name.StartsWith("obj:", StringComparison.Ordinal) ||
                    PdfObjectRef.Parse(entry.Name[4..]) is not { } reference || !references.TryAdd(reference, entry.Value))
                    throw InvalidMetadata();
                if (references.Count > 50_000) throw new PdfCleanTooComplexException();
            }
            Collect(Content(entry.Value));
        }
        var catalog = Catalog();
        catalogNames = Property(catalog, "/Names");
        foreach (var dictionary in dictionaries)
            if (IsSubtype(dictionary, "/FileAttachment")) attachments.Add(dictionary);
        // Classify both directions before allowlisting attachments removes /Popup.
        foreach (var attachment in attachments)
        {
            var popup = Property(attachment, "/Popup");
            if (IsSubtype(popup, "/Popup")) popups.Add(popup);
        }
        foreach (var dictionary in dictionaries)
        {
            if (IsSubtype(dictionary, "/Popup") && attachments.Contains(Property(dictionary, "/Parent")))
                popups.Add(dictionary);
            var annots = Property(dictionary, "/Annots");
            if (annots.ValueKind == JsonValueKind.Array) annotationArrays.Add(annots);
        }
        if (planChanges)
        {
            // Only indirect Popup objects become null. Direct dictionaries are removed
            // from annotation arrays (or disappear when their containing /Popup is dropped).
            foreach (var wrapper in references.Values)
                if (popups.Contains(Content(wrapper))) nullObjects.Add(wrapper);
            RetainedPopupCount = CountPopups(popups);
            foreach (var entry in objects.EnumerateObject())
                if (nullObjects.Contains(entry.Value) || Changes(Content(entry.Value))) changedObjects.Add(entry.Value);
        }
    }

    private void Collect(JsonElement value)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            dictionaries.Add(value);
            foreach (var property in value.EnumerateObject()) Collect(property.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Collect(item);
    }

    private static JsonElement Content(JsonElement wrapper)
    {
        if (wrapper.ValueKind != JsonValueKind.Object) throw InvalidMetadata();
        if (wrapper.TryGetProperty("value", out var value)) return value;
        if (wrapper.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.Object &&
            stream.TryGetProperty("dict", out var dictionary) && dictionary.ValueKind == JsonValueKind.Object)
            return dictionary;
        throw InvalidMetadata();
    }

    private JsonElement Resolve(JsonElement value)
    {
        var seen = new HashSet<PdfObjectRef>();
        while (PdfObjectRef.Parse(value) is { } reference)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(reference)) throw new PdfCleanTooComplexException();
            if (seen.Count > 64) throw new PdfCleanTooComplexException();
            if (!references.TryGetValue(reference, out var wrapper)) return default;
            // A stream cannot stand in for a dictionary, array, name or annotation.
            if (!wrapper.TryGetProperty("value", out value)) return default;
        }
        return value;
    }

    private JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? Resolve(property) : default;
    private bool IsSubtype(JsonElement value, string name)
        => Property(value, "/Subtype") is { ValueKind: JsonValueKind.String } subtype && subtype.GetString() == name;
    private JsonElement Catalog()
    {
        if (!objects.TryGetProperty("trailer", out var trailer)) throw InvalidMetadata();
        var catalog = Property(Content(trailer), "/Root");
        return catalog.ValueKind == JsonValueKind.Object ? catalog : throw InvalidMetadata();
    }
    private bool RemoveKey(JsonElement dictionary, string name)
        => name is "/AF" or "/EF" or "/RF" ||
            (name == "/EmbeddedFiles" && identity.Equals(dictionary, catalogNames)) ||
            (attachments.Contains(dictionary) && !AnnotationKeys.Contains(name));
    private bool RemoveAnnotation(JsonElement value)
    {
        var annotation = Resolve(value);
        return attachments.Contains(annotation) || popups.Contains(annotation);
    }

    private bool Changes(JsonElement value)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().Any(property => RemoveKey(value, property.Name) || Changes(property.Value));
        if (value.ValueKind == JsonValueKind.Array)
        {
            var annots = annotationArrays.Contains(value);
            foreach (var item in value.EnumerateArray())
                if ((annots && RemoveAnnotation(item)) || Changes(item)) return true;
        }
        return false;
    }

    internal void Strip(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("qpdf");
        writer.WriteStartArray();
        document.RootElement.GetProperty("qpdf")[0].WriteTo(writer);
        writer.WriteStartObject();
        foreach (var entry in objects.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (!changedObjects.Contains(entry.Value)) continue;
            writer.WritePropertyName(entry.Name);
            writer.WriteStartObject();
            if (nullObjects.Contains(entry.Value)) writer.WriteNull("value");
            else if (entry.Value.TryGetProperty("value", out var value))
            {
                writer.WritePropertyName("value");
                Write(value, writer);
            }
            else
            {
                writer.WritePropertyName("stream");
                writer.WriteStartObject();
                writer.WritePropertyName("dict");
                Write(Content(entry.Value), writer);
                // Omitting data/datafile retains the input stream bytes.
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.Flush();
        }
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void Write(JsonElement value, Utf8JsonWriter writer)
    {
        token.ThrowIfCancellationRequested();
        if (writer.BytesPending >= 64 * 1024) writer.Flush();
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                if (RemoveKey(value, property.Name)) continue;
                writer.WritePropertyName(property.Name);
                Write(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var annots = annotationArrays.Contains(value);
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
                if (!annots || !RemoveAnnotation(item)) Write(item, writer);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    // Count dictionary occurrences, not incoming references. The same collector and
    // subtype resolver handle indirect objects and nested direct dictionaries in both runs.
    private int CountPopups(HashSet<JsonElement>? excluded = null)
        => dictionaries.Count(dictionary => IsSubtype(dictionary, "/Popup") && (excluded is null || !excluded.Contains(dictionary)));

    internal static void Verify(JsonDocument document, int retainedPopupCount, CancellationToken token = default)
    {
        var graph = new PdfEmbeddedFileStripper(document, token, planChanges: false);
        void Require(bool condition) { if (!condition) throw new InvalidOperationException("PDF output validation failed."); }
        Require(document.RootElement.TryGetProperty("attachments", out var listed) && listed.ValueKind == JsonValueKind.Object &&
            !listed.EnumerateObject().Any());
        var catalog = graph.Catalog();
        Require(!catalog.TryGetProperty("/Metadata", out _));
        var trailer = Content(graph.objects.GetProperty("trailer"));
        var info = graph.Property(trailer, "/Info");
        Require(info.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            (info.ValueKind == JsonValueKind.Object && info.EnumerateObject().All(property => property.Name == "/ModDate")));
        Require(graph.catalogNames.ValueKind != JsonValueKind.Object || !graph.catalogNames.TryGetProperty("/EmbeddedFiles", out _));
        foreach (var dictionary in graph.dictionaries)
        {
            token.ThrowIfCancellationRequested();
            Require(!dictionary.TryGetProperty("/AF", out _) && !dictionary.TryGetProperty("/EF", out _) && !dictionary.TryGetProperty("/RF", out _));
            if (graph.attachments.Contains(dictionary)) Require(dictionary.EnumerateObject().All(property => AnnotationKeys.Contains(property.Name)));
        }
        foreach (var wrapper in graph.references.Values)
            if (wrapper.TryGetProperty("stream", out _))
            {
                var type = graph.Property(Content(wrapper), "/Type");
                Require(type.ValueKind != JsonValueKind.String || type.GetString() != "/EmbeddedFile");
            }
        foreach (var array in graph.annotationArrays) Require(!array.EnumerateArray().Any(graph.RemoveAnnotation));
        // qpdf renumbers objects. Never compare input reference numbers with output.
        // This covers parentless /Popup targets whose input-only relation was removed;
        // unreachable ordinary Popups may also disappear during final writing.
        Require(graph.CountPopups() <= retainedPopupCount);
    }

    private static InvalidOperationException InvalidMetadata() => new("PDF metadata is invalid.");
}
