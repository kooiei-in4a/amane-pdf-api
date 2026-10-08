using System.Globalization;
using System.Text.Json;

namespace Amane.Pdf.Api;

internal readonly record struct PdfObjectRef(int Number, int Generation)
{
    internal string Text => $"{Number} {Generation} R";
    internal string Key => "obj:" + Text;
    internal string Argument => $"--json-object={Number},{Generation}";
    internal static PdfObjectRef? Parse(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? Parse(value.GetString()) : null;
    internal static PdfObjectRef? Parse(string? value)
    {
        var parts = value?.Split(' ');
        return parts is { Length: 3 } && parts[2] == "R" &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var generation) && generation is >= 0 and <= 65535
            ? new(number, generation) : null;
    }
}

internal static class CompressJson
{
    internal static JsonDocument Parse(byte[] bytes, int depth)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = int.MaxValue });
            while (reader.Read())
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= depth)
                    throw new CompressLimitException();
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = depth });
        }
        catch (JsonException) { throw new InvalidOperationException("PDF metadata is invalid."); }
    }

    internal static JsonElement Objects(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("qpdf", out var qpdf) || qpdf.ValueKind != JsonValueKind.Array ||
            qpdf.GetArrayLength() != 2 || qpdf[1].ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("PDF metadata is invalid.");
        return qpdf[1];
    }
}

// Only one bounded metadata batch is held as a DOM. Individual objects are spooled
// to private files, with aggregate allocation accounting, and loaded when needed.
internal sealed class CompressMetadata(PdfCompressProcessor owner)
{
    private readonly Dictionary<PdfObjectRef, string?> paths = [];
    private long spoolBytes;
    internal IEnumerable<string> Files => paths.Values.OfType<string>();

    internal async Task FetchAsync(IEnumerable<PdfObjectRef> references, CancellationToken token)
    {
        var missing = references.Distinct().Where(reference => !paths.ContainsKey(reference)).ToArray();
        foreach (var batch in missing.Chunk(500))
        {
            token.ThrowIfCancellationRequested();
            var output = Path.Combine(owner.Files.DirectoryPath, "metadata.json");
            var size = Math.Min(owner.Options.CompressJsonLimitBytes, owner.Capacity.Remaining / 4096 * 4096);
            if (size < 4096) throw new CompressLimitException();
            owner.Capacity.Reserve(output, size);
            try
            {
                using (TemporaryPdfFiles.CreatePrivateFile(output)) { }
                var result = await owner.RunQpdfAsync([
                    "--json=2", "--json-key=qpdf", "--json-stream-data=none", "--decode-level=none",
                    .. batch.Select(reference => reference.Argument), owner.Files.InputPath, output
                ], size, null, token);
                PdfCompressProcessor.RequireStarted(result);
                // With SIGXFSZ ignored, qpdf can return 0 after a truncated write.
                // Treat a saturated metadata budget conservatively, before parsing.
                // Retain 153 defensively; normal ignored-SIGXFSZ writes use the size check.
                if (result.ExitCode == 153 || new FileInfo(output).Length >= size)
                    throw new CompressLimitException();
                if (result.ExitCode != 0) throw new InvalidOperationException("PDF metadata failed.");
                owner.Capacity.Reserve(output, new FileInfo(output).Length);
                using var document = CompressJson.Parse(await File.ReadAllBytesAsync(output, token), owner.Options.CompressJsonDepth);
                var objects = CompressJson.Objects(document);
                foreach (var reference in batch)
                {
                    if (!objects.TryGetProperty(reference.Key, out var value)) { paths[reference] = null; continue; }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { MaxDepth = owner.Options.CompressJsonDepth });
                    var allocation = CompressCapacity.Allocated(bytes.Length);
                    if (checked(spoolBytes + allocation) > owner.Options.CompressSpoolLimitBytes) throw new CompressLimitException();
                    var path = Path.Combine(owner.Files.DirectoryPath, $"dict-{reference.Number}-{reference.Generation}.json");
                    owner.Capacity.Reserve(path, bytes.Length);
                    await using (var file = TemporaryPdfFiles.CreatePrivateFile(path)) await file.WriteAsync(bytes, token);
                    spoolBytes += allocation;
                    paths.Add(reference, path);
                }
            }
            finally { owner.Capacity.Delete(output); }
        }
    }

    internal async Task<JsonElement?> GetAsync(PdfObjectRef reference, CancellationToken token)
    {
        await FetchAsync([reference], token);
        if (paths[reference] is not { } path) return null;
        using var document = CompressJson.Parse(await File.ReadAllBytesAsync(path, token), owner.Options.CompressJsonDepth);
        return document.RootElement.Clone();
    }

    // Prime scalar/dictionary aliases a level at a time, without retaining their DOMs.
    internal async Task FetchChainsAsync(IEnumerable<PdfObjectRef> references, CancellationToken token)
    {
        var frontier = references.ToHashSet();
        var seen = new HashSet<PdfObjectRef>();
        for (var depth = 0; depth < owner.Options.CompressJsonDepth && frontier.Count > 0; depth++)
        {
            frontier.ExceptWith(seen);
            seen.UnionWith(frontier);
            await FetchAsync(frontier, token);
            var next = new HashSet<PdfObjectRef>();
            foreach (var reference in frontier)
            {
                token.ThrowIfCancellationRequested();
                var obj = await GetAsync(reference, token);
                if (obj is { } item && item.TryGetProperty("value", out var value) && PdfObjectRef.Parse(value) is { } alias)
                    next.Add(alias);
            }
            frontier = next;
        }
    }

    internal async Task<JsonElement?> ResolveAsync(JsonElement value, CancellationToken token)
    {
        var seen = new HashSet<PdfObjectRef>();
        while (PdfObjectRef.Parse(value) is { } reference)
        {
            if (!seen.Add(reference) || seen.Count > owner.Options.CompressJsonDepth) return null;
            var obj = await GetAsync(reference, token);
            if (obj is not { } item || !item.TryGetProperty("value", out value)) return null;
        }
        return value;
    }

    internal async Task<JsonElement?> PropertyAsync(JsonElement dictionary, string key, CancellationToken token)
        => dictionary.TryGetProperty(key, out var value) ? await ResolveAsync(value, token) : null;

    internal async Task<JsonElement?> DictionaryAsync(JsonElement value, CancellationToken token)
    {
        var resolved = await ResolveAsync(value, token);
        return resolved is { ValueKind: JsonValueKind.Object } ? resolved : null;
    }

    internal void DeleteExcept(ISet<PdfObjectRef> keep)
    {
        foreach (var (reference, path) in paths.ToArray())
        {
            if (keep.Contains(reference)) continue;
            if (path is not null)
            {
                spoolBytes -= CompressCapacity.Allocated(new FileInfo(path).Length);
                owner.Capacity.Delete(path);
            }
            paths.Remove(reference);
        }
    }
}
