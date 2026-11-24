using System.Text.Json;

namespace Amane.Pdf.Api;

// The request owns all files, including partially written files on cancellation.
internal sealed class PdfPageJob(TemporaryPdfFiles files, QpdfProcessor qpdf, PdfOptions options, long? jobLimit = null)
{
    private readonly OverlayCapacity capacity = new(jobLimit ?? options.OverlayJobLimitBytes);
    internal string PathFor(string name) => Path.Combine(files.DirectoryPath, name);
    internal void TrackInput() => capacity.Reserve(files.InputPath, new FileInfo(files.InputPath).Length);
    internal long Reserve(string path, long limit) => capacity.ReserveWrite(path, limit, external: true);
    internal void Commit(string path, long budget) => capacity.Commit(path, budget);
    internal void Delete(string path) => capacity.Delete(path);

    internal async Task WritePdfAsync(string path, string[] arguments, long limit, CancellationToken token)
    {
        var budget = Reserve(path, limit);
        await qpdf.RunOverlayWriteAsync(files, arguments, path, budget, token);
        Commit(path, budget);
    }

    internal void WriteJson(string path, Action<Utf8JsonWriter> write, CancellationToken token, long? limit = null)
    {
        var budget = capacity.ReserveWrite(path, limit ?? options.OverlayGeneratedLimitBytes);
        using (var file = TemporaryPdfFiles.CreatePrivateFile(path))
        using (var bounded = new OverlayLimitedWriteStream(file, budget, token))
        using (var writer = new Utf8JsonWriter(bounded))
        {
            write(writer);
        }
        Commit(path, budget);
    }

    internal void WriteFile(string path, Action<Stream> write, CancellationToken token)
    {
        var budget = capacity.ReserveWrite(path, options.OverlayGeneratedLimitBytes);
        using (var file = TemporaryPdfFiles.CreatePrivateFile(path))
        using (var bounded = new OverlayLimitedWriteStream(file, budget, token))
        {
            write(bounded);
        }
        Commit(path, budget);
    }

    internal async Task<PdfPageBoxes> ReadAsync(string path, CancellationToken token, bool generated = false)
    {
        var metadata = PathFor("page-metadata.json");
        var budget = Reserve(metadata, generated ? options.OverlayGeneratedJsonLimitBytes : options.OverlayJsonLimitBytes);
        try
        {
            await qpdf.RunOverlayWriteAsync(files,
                ["--json=2", "--json-key=pages", "--json-key=qpdf", "--json-stream-data=none", "--decode-level=none", path, metadata],
                metadata, budget, token);
            return new PdfPageBoxes(await File.ReadAllBytesAsync(metadata, token), budget,
                generated ? PdfPageBoxes.GeneratedObjectLimit : PdfPageBoxes.InputObjectLimit, token);
        }
        finally { Delete(metadata); }
    }

    internal static void StartUpdate(Utf8JsonWriter writer, bool newPdf = false)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("qpdf");
        writer.WriteStartObject();
        writer.WriteNumber("jsonversion", 2);
        if (newPdf) writer.WriteString("pdfversion", "1.7");
        writer.WriteEndObject();
        writer.WriteStartObject();
    }

    internal static void EndUpdate(Utf8JsonWriter writer)
    {
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
