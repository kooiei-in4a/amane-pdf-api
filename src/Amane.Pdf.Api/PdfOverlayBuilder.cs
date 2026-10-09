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

    internal async Task BuildAsync(TemporaryPdfFiles files, int from, int to,
        Func<OverlayCanvas, CancellationToken, Task> draw, CancellationToken token,
        Action<OverlayStage>? observe = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var capacity = new OverlayCapacity(settings.OverlayJobLimitBytes);
        var metadata = Path.Combine(files.DirectoryPath, "overlay-metadata.json");
        var update = Path.Combine(files.DirectoryPath, "overlay-update.json");
        var blankJson = Path.Combine(files.DirectoryPath, "overlay-blank.json");
        var blank = Path.Combine(files.DirectoryPath, "overlay-blank.pdf");
        var layer = Path.Combine(files.DirectoryPath, "overlay-layer.pdf");
        var overlaid = Path.Combine(files.DirectoryPath, "overlaid.pdf");
        var intermediates = new[] { metadata, update, blankJson, blank, layer, overlaid, files.PdfcpuLayerJsonPath };
        var inputValidated = false;
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
        async Task<PdfPageBoxes> ReadMetadata(string path, int objectLimit)
        {
            var budget = capacity.ReserveWrite(metadata, objectLimit == PdfPageBoxes.InputObjectLimit ?
                settings.OverlayJsonLimitBytes : settings.OverlayGeneratedJsonLimitBytes, true);
            await qpdf.RunOverlayWriteAsync(files,
                ["--json=2", "--json-key=pages", "--json-key=qpdf", "--json-key=encrypt", "--json-stream-data=none", "--decode-level=none", path, metadata],
                metadata, budget, token);
            capacity.Commit(metadata, budget);
            var result = new PdfPageBoxes(await File.ReadAllBytesAsync(metadata, token), budget, objectLimit, token);
            observe?.Invoke(new("metadata", stage.Elapsed.TotalMilliseconds, new FileInfo(metadata).Length, result.ObjectCount));
            capacity.Delete(metadata);
            return result;
        }
        try
        {
            var inputBytes = new FileInfo(files.InputPath).Length;
            if (inputBytes > Math.Min(settings.MaxFileBytes, InputLimitBytes)) throw PdfOverlayInputException.TooComplex();
            capacity.Reserve(files.InputPath, inputBytes);
            await qpdf.ValidateAsync(files, token);
            Mark("validate-input", inputBytes);
            int count;
            PdfPageAttributes[] originals;
            using (var input = await ReadMetadata(files.InputPath, PdfPageBoxes.InputObjectLimit))
            {
                count = input.Pages.Count;
                if (input.Encrypted != false) throw new InvalidOperationException(Failure);
                if (from < 1 || to < from || to > count) throw new ArgumentException("描画ページ範囲が不正です。");
                if (to - from + 1 > settings.OverlayMaxPages) throw PdfOverlayInputException.TooComplex();
                originals = Enumerable.Range(from - 1, to - from + 1).Select(i => input.Read(i, token)).ToArray();
                await WriteJson(update, writer => input.WriteUpdate(writer, from, originals, false, token));
            }
            Mark("input-attributes");
            inputValidated = true;
            await WriteJson(blankJson, writer => WriteBlank(writer, originals, token));
            await WritePdf(blank, ["--json-input", blankJson, blank]);
            capacity.Delete(blankJson);
            Mark("blank", new FileInfo(blank).Length);
            var jsonBudget = capacity.ReserveWrite(files.PdfcpuLayerJsonPath, settings.OverlayGeneratedLimitBytes);
            var pdfBudget = capacity.ReserveWrite(layer, settings.OverlayGeneratedLimitBytes, true);
            var canvas = new OverlayCanvas(blank, layer,
                originals.Select(page => new PdfcpuPageSize(page.DisplayWidth, page.DisplayHeight)).ToArray(), jsonBudget, pdfBudget);
            await draw(canvas, token);
            capacity.Commit(files.PdfcpuLayerJsonPath, jsonBudget);
            capacity.Commit(layer, pdfBudget);
            // Treat the delegate's output as generated data: it must pass the same checks.
            await ValidateGenerated(layer, originals.Length, token);
            Mark("draw", new FileInfo(layer).Length);
            observe?.Invoke(new("layer-budget", 0, pdfBudget));
            capacity.Delete(blank); capacity.Delete(files.PdfcpuLayerJsonPath);
            await WritePdf(overlaid, [files.InputPath, "--update-from-json=" + update, overlaid,
                "--overlay", layer, "--to=" + from + "-" + to, "--from=1-z", "--"]);
            Mark("overlay", new FileInfo(overlaid).Length);
            capacity.Delete(files.InputPath); capacity.Delete(update); capacity.Delete(layer);
            using (var combined = await ReadMetadata(overlaid, PdfPageBoxes.GeneratedObjectLimit))
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
            using (var final = await ReadMetadata(files.OutputPath, PdfPageBoxes.GeneratedObjectLimit))
            {
                if (final.Pages.Count != count || final.Encrypted != false) throw new InvalidOperationException(Failure);
                for (var i = 0; i < originals.Length; i++)
                    if (!originals[i].SameEffective(final.Read(from - 1 + i, token))) throw new InvalidOperationException(Failure);
            }
            Mark("validate-output");
            observe?.Invoke(new("job-reserved-peak", 0, capacity.Peak));
        }
        catch (PdfOverlayInputException exception) when (inputValidated && exception.Reason == PdfOverlayReason.UnsupportedPdf)
        { throw new InvalidOperationException(Failure); }
        catch (PdfcpuCapacityException) { throw PdfOverlayInputException.TooComplex(); }
        finally
        {
            // Runner waits for termination before control reaches this cleanup. Do not use
            // the cancelled token here: partial files still have to be removed.
            foreach (var path in intermediates) capacity.Delete(path);
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
    private static void WriteBlank(Utf8JsonWriter writer, IReadOnlyList<PdfPageAttributes> pages, CancellationToken token)
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
            writer.WriteStartObject("/Resources"); writer.WriteEndObject(); End(); writer.Flush();
        }
        Start("trailer"); writer.WriteString("/Root", "1 0 R"); writer.WriteNumber("/Size", pages.Count + 3); End();
        writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
    }
}
