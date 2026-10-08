using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amane.Pdf.Api;

internal sealed record CompressImage(PdfObjectRef Reference, int Width, int Height, int Components, long Length);

internal sealed class PdfCompressProcessor(PdfOptions options, TemporaryPdfFiles files)
{
    internal PdfOptions Options => options;
    internal TemporaryPdfFiles Files => files;
    internal CompressCapacity Capacity { get; } = new(options.CompressJobLimitBytes);
    private const long OutputHeadroom = 4 * 1024 * 1024;
    private readonly List<(PdfObjectRef Reference, string Jpeg, string Entry)> adopted = [];
    private long originalBytes;
    private long adoptedBytes;
    private long adoptedAllocation;
    private long entryAllocation;
    private long updateBytes = Encoding.UTF8.GetByteCount("{\"qpdf\":[{\"jsonversion\":2},{}]}");

    internal static bool IsValid(PdfOptions value)
        => !string.IsNullOrWhiteSpace(value.DjpegPath) && !string.IsNullOrWhiteSpace(value.CjpegPath) &&
            value.JpegAddressSpaceLimitBytes > 0 && value.JpegAddressSpaceLimitBytes < long.MaxValue / 8 &&
            value.CompressJobLimitBytes > 0 && value.CompressJobLimitBytes < long.MaxValue / 8 &&
            value.CompressPnmLimitBytes > 0 && value.CompressPnmLimitBytes < long.MaxValue / 8 &&
            value.CompressMaxPixels > 0 && value.CompressMaxImages > 0 && value.CompressJsonLimitBytes > 0 &&
            value.CompressJsonDepth > 0 && value.CompressSpoolLimitBytes > 0 && value.CompressStdoutLimitBytes > 0 &&
            value.CompressSoftTimeoutSeconds is > 0 and <= int.MaxValue / 1000 &&
            value.CompressSoftTimeoutSeconds < value.QpdfTimeoutSeconds &&
            value.CompressImageTimeoutSeconds is > 0 and <= int.MaxValue / 1000 &&
            value.CompressBatchTimeoutSeconds is > 0 and <= int.MaxValue / 1000;

    internal static int Scale(int longEdge, int target)
        => longEdge <= target ? 8 : Math.Clamp(checked((target * 8 + longEdge - 1) / longEdge), 1, 8);

    internal static long PnmSize(int width, int height, int components, int scale)
    {
        var w = checked(((long)width * scale + 7) / 8);
        var h = checked(((long)height * scale + 7) / 8);
        var header = Encoding.ASCII.GetByteCount($"P{(components == 1 ? 5 : 6)}\n{w} {h}\n255\n");
        return checked(w * h * components + header);
    }

    internal static async Task ValidateStartupAsync(PdfOptions options, CancellationToken token)
    {
        // This is called only by the Linux startup self-test, with its shared timeout.
        using var files = new TemporaryPdfFiles(options.TempRoot);
        var pnm = Path.Combine(files.DirectoryPath, "startup.ppm");
        var jpeg = Path.Combine(files.DirectoryPath, "startup.jpg");
        var decoded = Path.Combine(files.DirectoryPath, "startup-decoded.ppm");
        await using (var file = TemporaryPdfFiles.CreatePrivateFile(pnm))
        {
            await file.WriteAsync(Encoding.ASCII.GetBytes("P6\n16 16\n255\n"), token);
            await file.WriteAsync(new byte[16 * 16 * 3], token);
        }
        foreach (var path in new[] { jpeg, decoded }) using (TemporaryPdfFiles.CreatePrivateFile(path)) { }
        foreach (var (path, args) in new (string, string[])[]
        {
            (options.CjpegPath, ["-quality", "75", "-optimize", "-maxmemory", "64M", "-strict", "-outfile", jpeg, pnm]),
            (options.DjpegPath, ["-scale", "3/8", "-maxmemory", "64M", "-maxscans", "100", "-strict", "-outfile", decoded, jpeg])
        })
        {
            var request = ProcessMemoryLimits.CreateRequest(options.PrlimitPath, path, args,
                options.JpegAddressSpaceLimitBytes, 64 * 1024, null);
            if ((await ExternalProcessRunner.RunAsync(request, token)).ExitCode != 0) throw new InvalidOperationException();
        }
        if (JpegHeader.Read(jpeg, 64 * 1024, 256, token) is not { Width: 16, Height: 16, Components: 3 } ||
            !Encoding.ASCII.GetString(await File.ReadAllBytesAsync(decoded, token)).StartsWith("P6\n6 6\n255\n", StringComparison.Ordinal))
            throw new InvalidOperationException();
    }

    internal Task<ExternalProcessResult> RunQpdfAsync(string[] arguments, long fsize, int? stdout, CancellationToken token)
        => ExternalProcessRunner.RunAsync(ProcessMemoryLimits.CreateRequest(options.PrlimitPath, options.QpdfPath,
            arguments, options.QpdfAddressSpaceLimitBytes, fsize,
            new Dictionary<string, string> { ["JPEGMEM"] = options.QpdfJpegMemory }, stdout), token);

    internal static void RequireStarted(ExternalProcessResult result)
    {
        if (result.ExitCode is 126 or 127) throw new InvalidOperationException("PDF command could not start.");
    }

    internal async Task<int> CompressAsync(string level, long start, CancellationToken hardToken)
    {
        var inputBytes = new FileInfo(files.InputPath).Length;
        Capacity.Reserve(files.InputPath, inputBytes);
        using var soft = CancellationTokenSource.CreateLinkedTokenSource(hardToken);
        var remaining = TimeSpan.FromSeconds(options.CompressSoftTimeoutSeconds) - Stopwatch.GetElapsedTime(start);
        if (remaining <= TimeSpan.Zero) soft.Cancel(); else soft.CancelAfter(remaining);
        var metadata = new CompressMetadata(this);
        try
        {
            var images = await SelectImagesAsync(metadata, soft.Token);
            var selected = images.OrderByDescending(image => image.Length).Take(options.CompressMaxImages).ToList();
            metadata.DeleteExcept(selected.Select(image => image.Reference).ToHashSet());
            var target = level == "standard" ? 1754 : 1169;
            while (selected.Count > 0)
            {
                soft.Token.ThrowIfCancellationRequested();
                var batch = new List<CompressImage>();
                long sum = 0, slot = 0, pnm = 0;
                foreach (var image in selected.Take(50))
                {
                    var imagePnm = checked(PnmSize(image.Width, image.Height, image.Components,
                        Scale(Math.Max(image.Width, image.Height), target)) + 512);
                    if (imagePnm > options.CompressPnmLimitBytes)
                    {
                        break;
                    }
                    var nextSlot = Math.Max(slot, CompressCapacity.Allocated(checked(image.Length + 4096)));
                    var nextPnm = Math.Max(pnm, CompressCapacity.Allocated(imagePnm));
                    var available = Capacity.Remaining - nextPnm - 4096;
                    if (checked(sum + image.Length) > available || checked((batch.Count + 1L) * nextSlot) > available) break;
                    slot = nextSlot; pnm = nextPnm; sum += image.Length; batch.Add(image);
                }
                if (batch.Count == 0)
                {
                    // Drop this candidate and try smaller images within the same budget.
                    if (selected.Count > 0) selected.RemoveAt(0);
                    continue;
                }
                selected.RemoveRange(0, batch.Count);
                if (!await ProcessBatchAsync(batch, slot, target, level, metadata, soft.Token, hardToken))
                {
                    // One bounded fallback per image, never retry a single-image failure.
                    if (batch.Count > 1)
                        foreach (var image in batch)
                        {
                            soft.Token.ThrowIfCancellationRequested();
                            await ProcessBatchAsync([image], CompressCapacity.Allocated(checked(image.Length + 4096)),
                                target, level, metadata, soft.Token, hardToken);
                        }
                }
            }
        }
        catch (CompressLimitException) { }
        catch (OperationCanceledException) when (!hardToken.IsCancellationRequested && soft.IsCancellationRequested) { }
        hardToken.ThrowIfCancellationRequested();
        await FinishAsync(inputBytes, hardToken);
        return adopted.Count;
    }

    private async Task<List<CompressImage>> SelectImagesAsync(CompressMetadata metadata, CancellationToken token)
    {
        var pagesPath = Path.Combine(files.DirectoryPath, "pages.json");
        var size = Math.Min(options.CompressJsonLimitBytes, Capacity.Remaining / 4096 * 4096);
        if (size < 4096) throw new CompressLimitException();
        Capacity.Reserve(pagesPath, size);
        PdfObjectRef[] pages;
        try
        {
            using (TemporaryPdfFiles.CreatePrivateFile(pagesPath)) { }
            var result = await RunQpdfAsync(["--json=2", "--json-key=pages", files.InputPath, pagesPath], size, null, token);
            RequireStarted(result);
            // qpdf may return 0 when a write hits FSIZE with SIGXFSZ ignored.
            // Retain 153 defensively; normal ignored-SIGXFSZ writes use the size check.
            if (result.ExitCode == 153 || new FileInfo(pagesPath).Length >= size) throw new CompressLimitException();
            if (result.ExitCode != 0) throw new InvalidOperationException("PDF pages metadata failed.");
            using var document = CompressJson.Parse(await File.ReadAllBytesAsync(pagesPath, token), options.CompressJsonDepth);
            if (!document.RootElement.TryGetProperty("pages", out var array) || array.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("PDF pages metadata is invalid.");
            pages = array.EnumerateArray().Select(page => page.TryGetProperty("object", out var value)
                ? PdfObjectRef.Parse(value) : null).Select(reference => reference ?? throw new InvalidOperationException("PDF page reference is invalid.")).Distinct().ToArray();
        }
        finally { Capacity.Delete(pagesPath); }
        var references = new HashSet<PdfObjectRef>();
        // Bound transient page dictionaries, including a parent and two resource levels.
        // Low spool settings use smaller batches; deeper alias chains still obey the limit.
        var pageBatchSize = (int)Math.Clamp(options.CompressSpoolLimitBytes / (4 * 4096), 1, 500);
        foreach (var batch in pages.Chunk(pageBatchSize))
        {
            try
            {
                await PrimePageResourcesAsync(metadata, batch, token);
                foreach (var page in batch)
                {
                    var current = page;
                    var seen = new HashSet<PdfObjectRef>();
                    while (seen.Add(current) && seen.Count <= options.CompressJsonDepth)
                    {
                        token.ThrowIfCancellationRequested();
                        var obj = await metadata.GetAsync(current, token);
                        if (obj is not { } item || !item.TryGetProperty("value", out var dictionary) || dictionary.ValueKind != JsonValueKind.Object) break;
                        if (dictionary.TryGetProperty("/Resources", out var resources))
                        {
                            var resolved = await metadata.DictionaryAsync(resources, token);
                            if (resolved is { } resourceDict && resourceDict.TryGetProperty("/XObject", out var xobject))
                            {
                                var xobjects = await metadata.DictionaryAsync(xobject, token);
                                if (xobjects is { } map)
                                    foreach (var property in map.EnumerateObject())
                                        if (PdfObjectRef.Parse(property.Value) is { } reference) references.Add(reference);
                            }
                            break; // Form resources are deliberately never traversed.
                        }
                        if (!dictionary.TryGetProperty("/Parent", out var parent) || PdfObjectRef.Parse(parent) is not { } next) break;
                        current = next;
                    }
                }
            }
            finally
            {
                // Only object references have been collected; image/dependency dictionaries
                // are loaded below, after all transient page metadata has been released.
                metadata.DeleteExcept(new HashSet<PdfObjectRef>());
            }
        }
        await metadata.FetchAsync(references, token);
        await PrimeImageReferencesAsync(metadata, references, token);
        var images = new List<CompressImage>();
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            var obj = await metadata.GetAsync(reference, token);
            if (obj is not { } item || !item.TryGetProperty("stream", out var stream) || !stream.TryGetProperty("dict", out var dictionary) ||
                dictionary.ValueKind != JsonValueKind.Object) continue;
            var subtype = await metadata.PropertyAsync(dictionary, "/Subtype", token);
            if (subtype is not { ValueKind: JsonValueKind.String } || subtype.Value.GetString() != "/Image") continue;
            var filter = await metadata.PropertyAsync(dictionary, "/Filter", token);
            if (filter is { ValueKind: JsonValueKind.Array } && filter.Value.GetArrayLength() == 1)
                filter = await metadata.ResolveAsync(filter.Value[0], token);
            if (filter is not { ValueKind: JsonValueKind.String } || filter.Value.GetString() != "/DCTDecode") continue;
            if (!await AbsentAsync("/DecodeParms") || !await AbsentAsync("/Decode")) continue;
            if (dictionary.TryGetProperty("/ImageMask", out var maskFlag))
            {
                var flag = await metadata.ResolveAsync(maskFlag, token);
                if (flag is null || flag.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) continue;
            }
            if (dictionary.TryGetProperty("/Mask", out var mask))
            {
                if (PdfObjectRef.Parse(mask) is { } maskRef)
                {
                    var maskObj = await metadata.GetAsync(maskRef, token);
                    if (maskObj is null || !maskObj.Value.TryGetProperty("stream", out _)) continue;
                }
                else if (mask.ValueKind != JsonValueKind.Null) continue;
            }
            if (dictionary.TryGetProperty("/SMask", out var smask) && smask.ValueKind != JsonValueKind.Null &&
                !(smask.ValueKind == JsonValueKind.String && smask.GetString() == "/None"))
            {
                if (PdfObjectRef.Parse(smask) is not { } smaskRef) continue;
                var smaskObj = await metadata.GetAsync(smaskRef, token);
                if (smaskObj is not { } smaskItem || !smaskItem.TryGetProperty("stream", out var smaskStream) ||
                    !smaskStream.TryGetProperty("dict", out var smaskDict) || smaskDict.ValueKind != JsonValueKind.Object ||
                    smaskDict.TryGetProperty("/Matte", out _)) continue;
            }
            var width = await IntegerAsync("/Width"); var height = await IntegerAsync("/Height");
            var bits = await IntegerAsync("/BitsPerComponent"); var length = await IntegerAsync("/Length");
            if (width is not > 0 or > 65535 || height is not > 0 or > 65535 || bits != 8 || length is not >= 32768 ||
                length > new FileInfo(files.InputPath).Length ||
                checked(width.Value * height.Value) > options.CompressMaxPixels) continue;
            var color = await metadata.PropertyAsync(dictionary, "/ColorSpace", token);
            var components = await ComponentsAsync(color, metadata, token);
            if (components == 0) continue;
            images.Add(new(reference, (int)width.Value, (int)height.Value, components, length.Value));

            async Task<bool> AbsentAsync(string key)
            {
                if (!dictionary.TryGetProperty(key, out var value)) return true;
                var resolved = await metadata.ResolveAsync(value, token);
                return resolved is { ValueKind: JsonValueKind.Null };
            }
            async Task<long?> IntegerAsync(string key)
            {
                var value = await metadata.PropertyAsync(dictionary, key, token);
                return value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt64(out var number) ? number : null;
            }
        }
        return images;
    }

    private async Task PrimePageResourcesAsync(CompressMetadata metadata, PdfObjectRef[] pages, CancellationToken token)
    {
        var frontier = pages.ToHashSet();
        var seen = new HashSet<PdfObjectRef>();
        var owners = new HashSet<PdfObjectRef>();
        var resources = new HashSet<PdfObjectRef>();
        for (var depth = 0; depth < options.CompressJsonDepth && frontier.Count > 0; depth++)
        {
            frontier.ExceptWith(seen);
            seen.UnionWith(frontier);
            await metadata.FetchAsync(frontier, token);
            var next = new HashSet<PdfObjectRef>();
            foreach (var reference in frontier)
            {
                token.ThrowIfCancellationRequested();
                var obj = await metadata.GetAsync(reference, token);
                if (obj is not { } item || !item.TryGetProperty("value", out var dict) || dict.ValueKind != JsonValueKind.Object) continue;
                if (dict.TryGetProperty("/Resources", out var value))
                {
                    owners.Add(reference);
                    AddReference(resources, value);
                }
                else if (dict.TryGetProperty("/Parent", out var parent)) AddReference(next, parent);
            }
            frontier = next;
        }
        await metadata.FetchChainsAsync(resources, token);
        var xobjects = new HashSet<PdfObjectRef>();
        foreach (var reference in owners)
        {
            token.ThrowIfCancellationRequested();
            var obj = (await metadata.GetAsync(reference, token))!.Value;
            var dict = await metadata.DictionaryAsync(obj.GetProperty("value").GetProperty("/Resources"), token);
            if (dict is { } resource && resource.TryGetProperty("/XObject", out var value)) AddReference(xobjects, value);
        }
        await metadata.FetchChainsAsync(xobjects, token);
    }

    private async Task PrimeImageReferencesAsync(CompressMetadata metadata, IEnumerable<PdfObjectRef> images, CancellationToken token)
    {
        string[] keys = ["/Subtype", "/Filter", "/DecodeParms", "/Decode", "/ImageMask", "/Mask", "/SMask",
            "/Width", "/Height", "/BitsPerComponent", "/Length", "/ColorSpace"];
        var references = new HashSet<PdfObjectRef>();
        foreach (var reference in images)
        {
            token.ThrowIfCancellationRequested();
            var dict = await ImageDictionaryAsync(reference);
            if (dict is not { } dictionary) continue;
            foreach (var key in keys)
                if (dictionary.TryGetProperty(key, out var value)) AddReference(references, value);
        }
        await metadata.FetchChainsAsync(references, token);
        references.Clear();
        foreach (var reference in images)
        {
            token.ThrowIfCancellationRequested();
            var dict = await ImageDictionaryAsync(reference);
            if (dict is not { } dictionary) continue;
            var filter = await metadata.PropertyAsync(dictionary, "/Filter", token);
            if (filter is { ValueKind: JsonValueKind.Array } && filter.Value.GetArrayLength() == 1) AddReference(references, filter.Value[0]);
            var color = await metadata.PropertyAsync(dictionary, "/ColorSpace", token);
            if (color is { ValueKind: JsonValueKind.Array } && color.Value.GetArrayLength() == 2)
            {
                AddReference(references, color.Value[0]);
                AddReference(references, color.Value[1]);
            }
        }
        await metadata.FetchChainsAsync(references, token);
        references.Clear();
        foreach (var reference in images)
        {
            token.ThrowIfCancellationRequested();
            var dict = await ImageDictionaryAsync(reference);
            if (dict is not { } dictionary) continue;
            var color = await metadata.PropertyAsync(dictionary, "/ColorSpace", token);
            if (color is not { ValueKind: JsonValueKind.Array } || color.Value.GetArrayLength() != 2) continue;
            var name = await metadata.ResolveAsync(color.Value[0], token);
            if (name is not { ValueKind: JsonValueKind.String } || name.Value.GetString() != "/ICCBased" ||
                PdfObjectRef.Parse(color.Value[1]) is not { } profileRef) continue;
            var profile = await metadata.GetAsync(profileRef, token);
            if (profile is { } obj && obj.TryGetProperty("stream", out var stream) &&
                stream.TryGetProperty("dict", out var profileDict) && profileDict.ValueKind == JsonValueKind.Object &&
                profileDict.TryGetProperty("/N", out var n)) AddReference(references, n);
        }
        await metadata.FetchChainsAsync(references, token);

        async Task<JsonElement?> ImageDictionaryAsync(PdfObjectRef reference)
        {
            var obj = await metadata.GetAsync(reference, token);
            return obj is { } item && item.TryGetProperty("stream", out var stream) &&
                stream.TryGetProperty("dict", out var dict) && dict.ValueKind == JsonValueKind.Object ? dict : null;
        }
    }

    private static void AddReference(ISet<PdfObjectRef> references, JsonElement value)
    {
        if (PdfObjectRef.Parse(value) is { } reference) references.Add(reference);
    }

    private static async Task<int> ComponentsAsync(JsonElement? color, CompressMetadata metadata, CancellationToken token)
    {
        if (color is { ValueKind: JsonValueKind.String })
            return color.Value.GetString() switch { "/DeviceGray" => 1, "/DeviceRGB" => 3, _ => 0 };
        if (color is not { ValueKind: JsonValueKind.Array } || color.Value.GetArrayLength() != 2) return 0;
        var name = await metadata.ResolveAsync(color.Value[0], token);
        if (name is not { ValueKind: JsonValueKind.String }) return 0;
        if (name.Value.GetString() is "/CalGray" or "/CalRGB")
            return await metadata.DictionaryAsync(color.Value[1], token) is not null ? name.Value.GetString() == "/CalGray" ? 1 : 3 : 0;
        if (name.Value.GetString() != "/ICCBased" || PdfObjectRef.Parse(color.Value[1]) is not { } reference) return 0;
        var profile = await metadata.GetAsync(reference, token);
        if (profile is not { } obj || !obj.TryGetProperty("stream", out var stream) || !stream.TryGetProperty("dict", out var dictionary) ||
            dictionary.ValueKind != JsonValueKind.Object) return 0;
        var n = await metadata.PropertyAsync(dictionary, "/N", token);
        return n is { ValueKind: JsonValueKind.Number } && n.Value.TryGetInt32(out var count) && count is 1 or 3 ? count : 0;
    }

    private async Task<bool> ProcessBatchAsync(List<CompressImage> batch, long slot, int target, string level,
        CompressMetadata metadata, CancellationToken softToken, CancellationToken hardToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var directory = Path.Combine(files.DirectoryPath, "raw-" + Guid.NewGuid().ToString("N"));
        var prefix = Path.Combine(directory, "image");
        try { Capacity.Reserve(directory, checked(batch.Count * slot)); }
        catch (CompressLimitException) { return false; }
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var extraction = CancellationTokenSource.CreateLinkedTokenSource(softToken);
            extraction.CancelAfter(TimeSpan.FromSeconds(options.CompressBatchTimeoutSeconds));
            ExternalProcessResult result;
            try
            {
                result = await RunQpdfAsync(["--json=2", "--json-key=qpdf", "--json-stream-data=file", "--decode-level=none",
                    "--json-stream-prefix=" + prefix, .. batch.Select(image => image.Reference.Argument), files.InputPath],
                    slot, options.CompressStdoutLimitBytes, extraction.Token);
            }
            catch (OperationCanceledException) when (!softToken.IsCancellationRequested && !hardToken.IsCancellationRequested) { return false; }
            RequireStarted(result);
            if (result.ExitCode != 0 || result.Stdout is null) return false;
            using var document = CompressJson.Parse(result.Stdout, options.CompressJsonDepth);
            var objects = CompressJson.Objects(document);
            var rawPaths = new Dictionary<PdfObjectRef, string>();
            foreach (var image in batch)
            {
                if (!objects.TryGetProperty(image.Reference.Key, out var obj) || !obj.TryGetProperty("stream", out var stream) ||
                    !stream.TryGetProperty("datafile", out var datafile) || datafile.ValueKind != JsonValueKind.String) return false;
                var path = Path.GetFullPath(datafile.GetString()!);
                var info = new FileInfo(path);
                if (path != prefix + "-" + image.Reference.Number.ToString(CultureInfo.InvariantCulture) || Path.GetDirectoryName(path) != directory ||
                    info.LinkTarget is not null || rawPaths.ContainsValue(path)) throw new InvalidOperationException("PDF image path is invalid.");
                if (!info.Exists || info.Length != image.Length || info.Length > slot) return false;
                rawPaths.Add(image.Reference, path);
            }
            if (Directory.GetFiles(directory).Length != batch.Count) throw new InvalidOperationException("PDF image extraction is invalid.");
            var remaining = batch.Count;
            foreach (var image in batch)
            {
                softToken.ThrowIfCancellationRequested();
                var raw = rawPaths[image.Reference];
                await ConvertAsync(image, raw, target, level, metadata, softToken, hardToken);
                File.Delete(raw);
                Capacity.Reserve(directory, --remaining * slot);
            }
            return true;
        }
        finally
        {
            Directory.Delete(directory, true);
            Capacity.Release(directory);
        }
    }

    private async Task ConvertAsync(CompressImage image, string raw, int target, string level,
        CompressMetadata metadata, CancellationToken softToken, CancellationToken hardToken)
    {
        using var conversion = CancellationTokenSource.CreateLinkedTokenSource(softToken);
        conversion.CancelAfter(TimeSpan.FromSeconds(options.CompressImageTimeoutSeconds));
        var token = conversion.Token;
        var pnm = Path.Combine(files.DirectoryPath, "image.pnm");
        var jpeg = Path.Combine(files.DirectoryPath, "image.jpg");
        string? pendingEntry = null;
        var keepEntry = false;
        try
        {
            var original = JpegHeader.Read(raw, image.Length, options.CompressMaxPixels, token);
            if (original is null || original.Width != image.Width || original.Height != image.Height || original.Components != image.Components) return;
            var scale = Scale(Math.Max(image.Width, image.Height), target);
            var pnmLimit = checked(PnmSize(image.Width, image.Height, image.Components, scale) + 512);
            if (pnmLimit > options.CompressPnmLimitBytes) return;
            Capacity.Reserve(pnm, pnmLimit);
            var jpegLimit = Math.Min(checked(image.Length * 9 / 10), Capacity.Remaining / 4096 * 4096);
            if (jpegLimit < 4096) return;
            Capacity.Reserve(jpeg, jpegLimit);
            foreach (var path in new[] { pnm, jpeg }) using (TemporaryPdfFiles.CreatePrivateFile(path)) { }
            foreach (var (executable, arguments, fsize) in new (string, string[], long)[]
            {
                (options.DjpegPath, ["-scale", $"{scale}/8", "-maxmemory", "64M", "-maxscans", "100", "-strict", "-outfile", pnm, raw], pnmLimit),
                (options.CjpegPath, ["-quality", level == "standard" ? "75" : "60", "-optimize", "-maxmemory", "64M", "-strict", "-outfile", jpeg, pnm], jpegLimit)
            })
            {
                var result = await ExternalProcessRunner.RunAsync(ProcessMemoryLimits.CreateRequest(options.PrlimitPath, executable,
                    arguments, options.JpegAddressSpaceLimitBytes, fsize, null), token);
                RequireStarted(result);
                if (result.ExitCode != 0) return;
            }
            var bytes = new FileInfo(jpeg).Length;
            if (bytes > jpegLimit || checked(10 * bytes) > checked(9 * image.Length)) return;
            var rewritten = JpegHeader.Read(jpeg, jpegLimit, options.CompressMaxPixels, token);
            if (rewritten is null || rewritten.Components != image.Components ||
                rewritten.Width != ((long)image.Width * scale + 7) / 8 || rewritten.Height != ((long)image.Height * scale + 7) / 8) return;
            var obj = await metadata.GetAsync(image.Reference, token) ?? throw new InvalidOperationException("PDF dictionary is missing.");
            var dict = JsonNode.Parse(obj.GetProperty("stream").GetProperty("dict").GetRawText(),
                documentOptions: new JsonDocumentOptions { MaxDepth = options.CompressJsonDepth })!.AsObject();
            dict["/Width"] = rewritten.Width; dict["/Height"] = rewritten.Height; dict.Remove("/Length");
            var retained = Path.Combine(files.DirectoryPath, $"adopt-{image.Reference.Number}-{image.Reference.Generation}.jpg");
            var entry = Path.Combine(files.DirectoryPath, $"update-{image.Reference.Number}-{image.Reference.Generation}.json");
            pendingEntry = entry;
            var node = new JsonObject { [image.Reference.Key] = new JsonObject { ["stream"] = new JsonObject { ["dict"] = dict, ["datafile"] = retained } } };
            var entryBytes = JsonSerializer.SerializeToUtf8Bytes(node, new JsonSerializerOptions { MaxDepth = options.CompressJsonDepth });
            // Entries include outer braces, which are omitted when joining the update.
            var nextJ = checked(updateBytes + entryBytes.Length - 2 + (adopted.Count == 0 ? 0 : 1));
            if (nextJ > options.CompressJsonLimitBytes) return;
            var nextA = checked(adoptedBytes + bytes); var nextR = checked(originalBytes + image.Length);
            var outputLimit = checked(new FileInfo(files.InputPath).Length - nextR + nextA + OutputHeadroom);
            var nextAllocation = checked(adoptedAllocation + CompressCapacity.Allocated(bytes));
            var nextEntries = checked(entryAllocation + CompressCapacity.Allocated(entryBytes.Length));
            if (!Capacity.CanFinalize(new FileInfo(files.InputPath).Length, nextAllocation, nextEntries, nextJ, outputLimit)) return;
            Capacity.Reserve(jpeg, bytes);
            Capacity.Reserve(entry, entryBytes.Length);
            await using (var file = TemporaryPdfFiles.CreatePrivateFile(entry)) await file.WriteAsync(entryBytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(jpeg, retained);
            Capacity.Release(jpeg); Capacity.Reserve(retained, bytes);
            adopted.Add((image.Reference, retained, entry));
            keepEntry = true;
            adoptedBytes = nextA; originalBytes = nextR; updateBytes = nextJ;
            adoptedAllocation = nextAllocation; entryAllocation = nextEntries;
        }
        catch (CompressLimitException) { }
        catch (OperationCanceledException) when (!hardToken.IsCancellationRequested && !softToken.IsCancellationRequested) { }
        finally
        {
            Capacity.Delete(pnm); Capacity.Delete(jpeg);
            if (!keepEntry && pendingEntry is not null) Capacity.Delete(pendingEntry);
        }
    }

    private async Task FinishAsync(long inputBytes, CancellationToken token)
    {
        var keep = adopted.Select(item => item.Jpeg).Append(files.InputPath).Concat(adopted.Select(item => item.Entry)).ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(files.DirectoryPath)) if (!keep.Contains(path)) Capacity.Delete(path);
        var update = Path.Combine(files.DirectoryPath, "update.json");
        if (adopted.Count > 0)
        {
            Capacity.Reserve(update, updateBytes);
            await using (var file = TemporaryPdfFiles.CreatePrivateFile(update))
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes("{\"qpdf\":[{\"jsonversion\":2},{"), token);
                for (var i = 0; i < adopted.Count; i++)
                {
                    var bytes = await File.ReadAllBytesAsync(adopted[i].Entry, token);
                    if (i > 0) await file.WriteAsync(new byte[] { (byte)',' }, token);
                    await file.WriteAsync(bytes.AsMemory(1, bytes.Length - 2), token);
                    Capacity.Delete(adopted[i].Entry);
                }
                await file.WriteAsync(Encoding.UTF8.GetBytes("}]}"), token);
            }
            if (new FileInfo(update).Length != updateBytes) throw new InvalidOperationException("PDF update size is invalid.");
        }
        var fsize = checked(inputBytes - originalBytes + adoptedBytes + OutputHeadroom);
        try { Capacity.Reserve(files.OutputPath, fsize); }
        catch (CompressLimitException) { throw new InvalidOperationException("PDF output capacity is insufficient."); }
        using (TemporaryPdfFiles.CreatePrivateFile(files.OutputPath)) { }
        var arguments = new List<string> { files.InputPath };
        if (adopted.Count > 0) arguments.Add("--update-from-json=" + update);
        arguments.AddRange(["--object-streams=generate", "--compression-level=9", files.OutputPath]);
        var result = await RunQpdfAsync([.. arguments], fsize, null, token);
        RequireStarted(result);
        // A truncated EOF can pass qpdf --check. Reject a saturated output budget
        // even on exit 0, including an otherwise valid PDF exactly at the limit.
        if (result.ExitCode != 0 || new FileInfo(files.OutputPath).Length >= fsize) throw new InvalidOperationException("PDF output failed.");
        try { await new QpdfProcessor(Microsoft.Extensions.Options.Options.Create(options)).ValidateAsync(files.OutputPath, token); }
        catch (PdfInputException) { throw new InvalidOperationException("PDF output validation failed."); }
        token.ThrowIfCancellationRequested();
    }
}
