using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public static class PdfCleanEndpoint
{
    public static Task HandleAsync(HttpContext context, QpdfProcessor processor, IOptions<PdfOptions> options)
        => PdfEndpointHelpers.ExecuteAsync(context, options.Value, "cleaned.pdf",
            () => { if (context.Request.Query.Count != 0) throw new BadHttpRequestException("Unexpected query.", 400); },
            (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, options.Value, token),
            (files, token) => CleanAsync(files, processor, options.Value, token));

    private static async Task CleanAsync(TemporaryPdfFiles files, QpdfProcessor processor, PdfOptions options, CancellationToken token)
    {
        var capacity = new CleanCapacity(options.CleanJobLimitBytes);
        capacity.Reserve(files.InputPath, new FileInfo(files.InputPath).Length);
        await processor.ValidateAsync(files, token);
        int pages;
        try { pages = await processor.GetPageCountAsync(files, token); }
        catch (PdfInputException) { throw new InvalidOperationException("PDF page count failed."); }
        var inputJson = Path.Combine(files.DirectoryPath, "input-objects.json");
        var updateJson = Path.Combine(files.DirectoryPath, "update.json");
        await InspectAsync(files.InputPath, inputJson, false);
        bool changed;
        int retainedPopups;
        using (var document = PdfEmbeddedFileStripper.Parse(await File.ReadAllBytesAsync(inputJson, token), token))
        {
            var stripper = new PdfEmbeddedFileStripper(document, token);
            retainedPopups = stripper.RetainedPopupCount;
            changed = stripper.HasChanges;
            if (changed)
            {
                var limit = capacity.ReserveWrite(updateJson, options.CleanJsonLimitBytes);
                await using var file = TemporaryPdfFiles.CreatePrivateFile(updateJson);
                using var bounded = new CleanLimitedWriteStream(file, limit, token);
                using var writer = new System.Text.Json.Utf8JsonWriter(bounded);
                stripper.Strip(writer);
                await writer.FlushAsync(token);
                capacity.Reserve(updateJson, bounded.Length);
            }
        }
        capacity.Delete(inputJson);
        var job = new Dictionary<string, object>
        {
            ["inputFile"] = files.InputPath, ["outputFile"] = files.OutputPath,
            ["removeInfo"] = "", ["removeMetadata"] = ""
        };
        if (changed) job["updateFromJson"] = updateJson;
        var jobLimit = capacity.ReserveWrite(files.JobPath, 1024 * 1024);
        await using (var file = TemporaryPdfFiles.CreatePrivateFile(files.JobPath))
        using (var bounded = new CleanLimitedWriteStream(file, jobLimit, token))
        {
            await System.Text.Json.JsonSerializer.SerializeAsync(bounded, job, cancellationToken: token);
            capacity.Reserve(files.JobPath, bounded.Length);
        }
        var outputLimit = capacity.ReserveWrite(files.OutputPath, options.CleanOutputLimitBytes);
        using (TemporaryPdfFiles.CreatePrivateFile(files.OutputPath)) { }
        await processor.RunCleanWriteAsync(["--job-json-file=" + files.JobPath], files.OutputPath, outputLimit, token);
        capacity.Reserve(files.OutputPath, new FileInfo(files.OutputPath).Length);
        capacity.Delete(files.InputPath);
        capacity.Delete(updateJson);
        capacity.Delete(files.JobPath);
        try
        {
            await processor.ValidateAsync(files.OutputPath, token);
            if (await processor.GetPageCountAsync(files.OutputPath, token) != pages)
                throw new InvalidOperationException("PDF output validation failed.");
        }
        catch (PdfInputException) { throw new InvalidOperationException("PDF output validation failed."); }
        var outputJson = Path.Combine(files.DirectoryPath, "output-inspection.json");
        await InspectAsync(files.OutputPath, outputJson, true);
        using (var document = PdfEmbeddedFileStripper.Parse(await File.ReadAllBytesAsync(outputJson, token), token))
            PdfEmbeddedFileStripper.Verify(document, retainedPopups, token);
        capacity.Delete(outputJson);

        async Task InspectAsync(string pdf, string json, bool attachments)
        {
            var limit = capacity.ReserveWrite(json, options.CleanJsonLimitBytes);
            using (TemporaryPdfFiles.CreatePrivateFile(json)) { }
            string[] keys = attachments ? ["--json-key=qpdf", "--json-key=attachments"] : ["--json-key=qpdf"];
            await processor.RunCleanWriteAsync(["--json=2", .. keys, "--json-stream-data=none", "--decode-level=none", pdf, json],
                json, limit, token);
            capacity.Reserve(json, new FileInfo(json).Length);
        }
    }
}
