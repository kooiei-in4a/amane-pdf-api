using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal sealed class PdfNeedsAppearanceException : Exception;

internal static class PdfFlattenEndpoint
{
    internal static Task HandleAsync(HttpContext context, QpdfProcessor qpdf, IOptions<PdfOptions> options)
        => PdfEndpointHelpers.ExecuteAsync(context, options.Value, "flattened.pdf",
            () => PdfEndpointHelpers.ValidateQueryKeys(context.Request.Query),
            (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, options.Value, token),
            (files, token) => FlattenAsync(files, qpdf, options.Value, token));

    private static async Task FlattenAsync(TemporaryPdfFiles files, QpdfProcessor qpdf, PdfOptions options,
        CancellationToken token)
    {
        var job = new PdfPageJob(files, qpdf, options);
        job.TrackInput();
        await qpdf.ValidateAsync(files, token);
        int pageCount;
        using (var input = await job.ReadAsync(files.InputPath, token))
        {
            pageCount = input.Pages.Count;
            if (PdfFormDetector.ContainsSignatureStructure(input, token))
                throw new PdfOverlayInputException(PdfOverlayReason.SignatureStructure);
            var catalog = input.Catalog(token);
            if (catalog.TryGetProperty("/AcroForm", out var rawForm) &&
                input.Resolve(rawForm, token) is { ValueKind: JsonValueKind.Object } form &&
                ((form.TryGetProperty("/NeedAppearances", out var needed) &&
                    input.Resolve(needed, token).ValueKind == JsonValueKind.True) ||
                 (form.TryGetProperty("/XFA", out var xfa) && input.Resolve(xfa, token).ValueKind != JsonValueKind.Null)))
                throw new PdfNeedsAppearanceException();
        }
        var budget = job.Reserve(files.OutputPath, options.OverlayOutputLimitBytes);
        await qpdf.FlattenAsync(files, budget, token);
        job.Commit(files.OutputPath, budget);
        job.Delete(files.InputPath);
        await qpdf.ValidateOutputAsync(files.OutputPath, token);
        using var output = await job.ReadAsync(files.OutputPath, token, generated: true);
        if (output.Pages.Count != pageCount || output.Catalog(token).TryGetProperty("/AcroForm", out _))
            throw new InvalidOperationException("PDF flattening did not remove the form or changed the page count.");
        foreach (var page in output.Pages)
        {
            if (!output.Dictionary(page).TryGetProperty("/Annots", out var annotations)) continue;
            var remaining = output.Resolve(annotations, token);
            if (remaining.ValueKind == JsonValueKind.Null ||
                (remaining.ValueKind == JsonValueKind.Array && remaining.GetArrayLength() == 0)) continue;
            throw new PdfNeedsAppearanceException();
        }
    }
}
