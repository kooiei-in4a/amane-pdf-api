using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal sealed record OverlayCanvas(string BlankPath, string LayerPath, IReadOnlyList<PdfcpuPageSize> Pages,
    long JsonBudget, long PdfBudget);
internal sealed record OverlayStage(string Name, double Milliseconds, long Bytes = 0, int Objects = 0);

internal sealed class PdfOverlayBuilder(QpdfProcessor qpdf, IOptions<PdfOptions> options)
{
    internal const string Failure = "PDF描画処理に失敗しました。";
    internal const long InputLimitBytes = 50 * 1048576;
    private readonly PdfOptions settings = options.Value;

    internal Task BuildAsync(TemporaryPdfFiles files, int from, int to,
        Func<OverlayCanvas, CancellationToken, Task> draw, CancellationToken token,
        Action<OverlayStage>? observe = null)
        => BuildAsync(files, _ => (from, to), draw, token, observe);

    internal async Task BuildAsync(TemporaryPdfFiles files, Func<int, (int From, int To)> resolveRange,
        Func<OverlayCanvas, CancellationToken, Task> draw, CancellationToken token,
        Action<OverlayStage>? observe = null, double? fillOpacity = null,
        IReadOnlyList<string>? drawInputs = null, bool layerJson = true, FillTemplateFields? fillTemplate = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (fillOpacity is { } opacity && (!double.IsFinite(opacity) || opacity <= 0 || opacity >= 1))
            throw new ArgumentOutOfRangeException(nameof(fillOpacity));
        var capacity = new OverlayCapacity(settings.OverlayJobLimitBytes);
        var metadata = Path.Combine(files.DirectoryPath, "overlay-metadata.json");
        var update = Path.Combine(files.DirectoryPath, "overlay-update.json");
        var blankJson = Path.Combine(files.DirectoryPath, "overlay-blank.json");
        var blank = Path.Combine(files.DirectoryPath, "overlay-blank.pdf");
        var layer = Path.Combine(files.DirectoryPath, "overlay-layer.pdf");
        var overlaid = Path.Combine(files.DirectoryPath, "overlaid.pdf");
        var unicodeUpdate = Path.Combine(files.DirectoryPath, "overlay-unicode.json");
        var unicodeLayer = Path.Combine(files.DirectoryPath, "overlay-unicode.pdf");
        var intermediates = new[] { metadata, update, blankJson, blank, layer, overlaid,
            unicodeUpdate, unicodeLayer, files.PdfcpuLayerJsonPath };
        var inputs = drawInputs?.Select(Path.GetFullPath).ToArray() ?? [];
        if (inputs.Distinct(StringComparer.Ordinal).Count() != inputs.Length || inputs.Any(path =>
            Path.GetDirectoryName(path) != Path.GetFullPath(files.DirectoryPath) ||
            path == Path.GetFullPath(files.InputPath) || path == Path.GetFullPath(files.OutputPath) ||
            intermediates.Any(other => path == Path.GetFullPath(other)) || !File.Exists(path) || new FileInfo(path).Length == 0))
            throw new ArgumentException("描画入力pathが不正です。");
        intermediates = [.. intermediates, .. inputs];
        var inputValidated = false;
        var completed = false;
        var stage = Stopwatch.StartNew();
        void Mark(string name, long bytes = 0, int objects = 0)
        { observe?.Invoke(new(name, stage.Elapsed.TotalMilliseconds, bytes, objects)); stage.Restart(); }
        async Task WriteJson(string path, Action<Utf8JsonWriter> write)
        {
            var budget = capacity.ReserveWrite(path, settings.OverlayGeneratedLimitBytes);
            await using (var file = TemporaryPdfFiles.CreatePrivateFile(path))
            using (var bounded = new OverlayLimitedWriteStream(file, budget, token))
            using (var writer = new Utf8JsonWriter(bounded)) { write(writer); writer.Flush(); }
            capacity.Commit(path, budget);
        }
        async Task WritePdf(string path, string[] arguments)
        {
            var budget = capacity.ReserveWrite(path,
                path == blank ? settings.OverlayGeneratedLimitBytes : settings.OverlayOutputLimitBytes, true);
            await qpdf.RunOverlayWriteAsync(files, arguments, path, budget, token);
            capacity.Commit(path, budget);
        }
        async Task<PdfPageBoxes> ReadMetadata(string path, long fileLimit, int objectLimit)
        {
            var budget = capacity.ReserveWrite(metadata, fileLimit, true);
            await qpdf.RunOverlayWriteAsync(files,
                ["--json=2", "--json-key=pages", "--json-key=qpdf", "--json-key=encrypt", "--json-stream-data=none", "--decode-level=none", path, metadata],
                metadata, budget, token);
            capacity.Commit(metadata, budget);
            var result = new PdfPageBoxes(await File.ReadAllBytesAsync(metadata, token), budget, objectLimit, token);
            var metadataBytes = new FileInfo(metadata).Length;
            capacity.Delete(metadata);
            Mark("metadata", metadataBytes, result.ObjectCount);
            return result;
        }
        try
        {
            var inputBytes = new FileInfo(files.InputPath).Length;
            if (inputBytes > Math.Min(settings.MaxFileBytes, InputLimitBytes)) throw PdfOverlayInputException.TooComplex();
            capacity.Reserve(files.InputPath, inputBytes);
            foreach (var path in inputs) capacity.Reserve(path, new FileInfo(path).Length);
            await qpdf.ValidateAsync(files, token);
            Mark("validate-input", inputBytes);
            int count, from, to;
            PdfPageAttributes[] originals;
            using (var input = await ReadMetadata(files.InputPath, settings.OverlayJsonLimitBytes, PdfPageBoxes.InputObjectLimit))
            {
                count = input.Pages.Count;
                if (input.Encrypted != false) throw new InvalidOperationException(Failure);
                if (fillTemplate is not null) PdfFormDetector.RejectForms(input, token);
                (from, to) = resolveRange(count);
                if (from < 1 || to < from || to > count) throw new ArgumentException("描画ページ範囲が不正です。");
                if (to - from + 1 > settings.OverlayMaxPages) throw PdfOverlayInputException.TooComplex();
                originals = Enumerable.Range(from - 1, to - from + 1).Select(i => input.Read(i, token)).ToArray();
                await WriteJson(update, writer => input.WriteUpdate(writer, from, originals, false, token));
            }
            Mark("input-attributes");
            inputValidated = true;
            await WriteJson(blankJson, writer => WriteBlank(writer, originals, token, fillOpacity));
            await WritePdf(blank, ["--json-input", blankJson, blank]);
            capacity.Delete(blankJson);
            Mark("blank", new FileInfo(blank).Length);
            var jsonBudget = layerJson ? capacity.ReserveWrite(files.PdfcpuLayerJsonPath, settings.OverlayGeneratedLimitBytes) : 0;
            var pdfBudget = capacity.ReserveWrite(layer, settings.OverlayGeneratedLimitBytes, true);
            var canvas = new OverlayCanvas(blank, layer,
                originals.Select(page => new PdfcpuPageSize(page.DisplayWidth, page.DisplayHeight)).ToArray(), jsonBudget, pdfBudget);
            await draw(canvas, token);
            if (layerJson) capacity.Commit(files.PdfcpuLayerJsonPath, jsonBudget);
            capacity.Commit(layer, pdfBudget);
            if (fillTemplate is not null)
            {
                try
                {
                    // Fresh metadata for this layer; all files share the overlay capacity and cleanup.
                    using (var drawn = await ReadMetadata(layer, settings.OverlayGeneratedJsonLimitBytes, PdfPageBoxes.GeneratedObjectLimit))
                        await WriteJson(unicodeUpdate, writer => FillTemplateUnicode.WriteUpdate(writer, drawn, fillTemplate, token));
                    var unicodeBudget = capacity.ReserveWrite(unicodeLayer, settings.OverlayGeneratedLimitBytes, true);
                    await qpdf.RunOverlayWriteAsync(files, [layer, "--update-from-json=" + unicodeUpdate, unicodeLayer],
                        unicodeLayer, unicodeBudget, token);
                    capacity.Commit(unicodeLayer, unicodeBudget);
                    capacity.Delete(layer);
                    File.Move(unicodeLayer, layer);
                    capacity.Delete(unicodeLayer);
                    capacity.Reserve(layer, new FileInfo(layer).Length);
                    capacity.Delete(unicodeUpdate);
                    Mark("to-unicode", new FileInfo(layer).Length);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { throw new InvalidOperationException(Failure); }
            }
            // Treat the delegate's output as generated data: it must pass the same checks.
            await ValidateGenerated(layer, originals.Length, token);
            Mark("draw", new FileInfo(layer).Length);
            observe?.Invoke(new("layer-budget", 0, pdfBudget));
            capacity.Delete(blank); capacity.Delete(files.PdfcpuLayerJsonPath);
            foreach (var path in inputs) capacity.Delete(path);
            await WritePdf(overlaid, [files.InputPath, "--update-from-json=" + update, overlaid,
                "--overlay", layer, "--to=" + from + "-" + to, "--from=1-z", "--"]);
            Mark("overlay", new FileInfo(overlaid).Length);
            capacity.Delete(files.InputPath); capacity.Delete(update); capacity.Delete(layer);
            using (var combined = await ReadMetadata(overlaid, settings.OverlayGeneratedJsonLimitBytes, PdfPageBoxes.GeneratedObjectLimit))
            {
                if (combined.Pages.Count != count) throw new InvalidOperationException(Failure);
                await WriteJson(update, writer => combined.WriteUpdate(writer, from, originals, true, token));
            }
            Mark("restore-attributes");
            await WritePdf(files.OutputPath, [overlaid, "--update-from-json=" + update, files.OutputPath]);
            capacity.Delete(overlaid); capacity.Delete(update);
            Mark("restore", new FileInfo(files.OutputPath).Length);
            // --check remains a strict independent structural check. Metadata
            // verifies encryption, page count and attributes in one bounded read.
            if (await qpdf.RunAsync(["--check", files.OutputPath], token) != 0) throw new InvalidOperationException(Failure);
            Mark("validate-pdf");
            using (var final = await ReadMetadata(files.OutputPath, settings.OverlayGeneratedJsonLimitBytes, PdfPageBoxes.GeneratedObjectLimit))
            {
                if (final.Pages.Count != count || final.Encrypted != false) throw new InvalidOperationException(Failure);
                for (var i = 0; i < originals.Length; i++)
                    if (!originals[i].SameEffective(final.Read(from - 1 + i, token))) throw new InvalidOperationException(Failure);
            }
            Mark("validate-output");
            observe?.Invoke(new("job-reserved-peak", 0, capacity.Peak));
            completed = true;
        }
        catch (PdfOverlayInputException exception) when (inputValidated && exception.Reason == PdfOverlayReason.UnsupportedPdf)
        { throw new InvalidOperationException(Failure); }
        catch (PdfcpuCapacityException) { throw PdfOverlayInputException.TooComplex(); }
        finally
        {
            // Runner waits for termination before control reaches this cleanup. Do not use
            // the cancelled token here: partial files still have to be removed.
            var cleanupFailed = false;
            foreach (var path in intermediates)
            {
                try { capacity.Delete(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { cleanupFailed = true; }
            }
            // Preserve an active failure; the owner still disposes the whole job.
            // A successful build must not return with failed intermediate cleanup.
            if (completed && cleanupFailed) throw new InvalidOperationException(Failure);
        }
    }

    private async Task ValidateGenerated(string path, int count, CancellationToken token)
    {
        try
        {
            await qpdf.ValidateAsync(path, token);
            if (await qpdf.GetPageCountAsync(path, token) != count) throw new InvalidOperationException(Failure);
        }
        catch (PdfInputException) { throw new InvalidOperationException(Failure); }
    }
    private static void WriteBlank(Utf8JsonWriter writer, IReadOnlyList<PdfPageAttributes> pages,
        CancellationToken token, double? fillOpacity)
    {
        writer.WriteStartObject(); writer.WriteStartArray("qpdf"); writer.WriteStartObject();
        writer.WriteNumber("jsonversion", 2); writer.WriteString("pdfversion", "1.7"); writer.WriteEndObject();
        writer.WriteStartObject();
        void Start(string key) { writer.WriteStartObject(key); writer.WriteStartObject("value"); }
        void End() { writer.WriteEndObject(); writer.WriteEndObject(); }
        Start("obj:1 0 R"); writer.WriteString("/Type", "/Catalog"); writer.WriteString("/Pages", "2 0 R"); End();
        Start("obj:2 0 R"); writer.WriteString("/Type", "/Pages"); writer.WriteNumber("/Count", pages.Count);
        writer.WriteStartArray("/Kids");
        for (var i = 0; i < pages.Count; i++) writer.WriteStringValue($"{i + 3} 0 R");
        writer.WriteEndArray(); End();
        for (var i = 0; i < pages.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            Start($"obj:{i + 3} 0 R"); writer.WriteString("/Type", "/Page"); writer.WriteString("/Parent", "2 0 R");
            writer.WritePropertyName("/MediaBox"); new PdfBox(0, 0, pages[i].DisplayWidth, pages[i].DisplayHeight).Write(writer);
            writer.WriteStartObject("/Resources");
            if (fillOpacity is { } alpha)
            {
                writer.WriteStartObject("/ExtGState"); writer.WriteStartObject("/AmaneAlpha");
                writer.WriteString("/Type", "/ExtGState"); writer.WriteNumber("/ca", alpha); writer.WriteNumber("/CA", alpha);
                writer.WriteEndObject(); writer.WriteEndObject();
            }
            writer.WriteEndObject();
            if (fillOpacity is not null) writer.WriteString("/Contents", $"{pages.Count + 3 + i} 0 R");
            End();
            if (fillOpacity is not null)
            {
                // pdfcpu appends to an existing stream. Sharing one across pages
                // would also share the appended text, so every page owns its stream.
                writer.WriteStartObject($"obj:{pages.Count + 3 + i} 0 R"); writer.WriteStartObject("stream");
                writer.WriteStartObject("dict"); writer.WriteEndObject();
                writer.WriteBase64String("data", "/AmaneAlpha gs\n"u8);
                writer.WriteEndObject(); writer.WriteEndObject();
            }
            writer.Flush();
        }
        Start("trailer"); writer.WriteString("/Root", "1 0 R");
        writer.WriteNumber("/Size", pages.Count * (fillOpacity is null ? 1 : 2) + 3); End();
        writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
    }
}
