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
        string? update = null;
        using (var input = await job.ReadAsync(files.InputPath, token))
        {
            pageCount = input.Pages.Count;
            if (PdfFormDetector.ContainsSignatureStructure(input, token))
                throw new PdfOverlayInputException(PdfOverlayReason.SignatureStructure);
            ThrowIfAppearancesMustBeGenerated(input, token);
            // qpdf keeps popups because they have no appearance. Their parent's appearance is burned in.
            var pages = ChangedAnnotations(input, subtype => subtype != "/Popup", token);
            if (pages.Count > 0)
            {
                update = job.PathFor("flatten-annotations.json");
                job.WriteJson(update, writer => WriteAnnotationUpdate(writer, input, pages), token);
            }
        }
        var budget = job.Reserve(files.OutputPath, options.OverlayOutputLimitBytes);
        await qpdf.FlattenAsync(files, update, budget, token);
        job.Commit(files.OutputPath, budget);
        if (update is not null) job.Delete(update);
        job.Delete(files.InputPath);
        try { await qpdf.ValidateOutputAsync(files.OutputPath, token); }
        catch (PdfInputException) { throw new InvalidOperationException("PDF output validation failed."); }
        using var output = await job.ReadAsync(files.OutputPath, token, generated: true);
        if (output.Pages.Count != pageCount || output.Catalog(token).TryGetProperty("/AcroForm", out _))
            throw new InvalidOperationException("PDF flattening did not remove the form or changed the page count.");
        // Links have no appearance to burn in and keep working on the unchanged pages.
        if (ChangedAnnotations(output, subtype => subtype == "/Link", token).Count > 0)
            throw new PdfNeedsAppearanceException();
    }

    internal static void ThrowIfAppearancesMustBeGenerated(PdfPageBoxes input, CancellationToken token)
    {
        var catalog = input.Catalog(token);
        if (catalog.TryGetProperty("/AcroForm", out var rawForm) &&
            input.Resolve(rawForm, token) is { ValueKind: JsonValueKind.Object } form &&
            ((form.TryGetProperty("/NeedAppearances", out var needed) &&
                input.Resolve(needed, token).ValueKind == JsonValueKind.True) ||
             (form.TryGetProperty("/XFA", out var xfa) && input.Resolve(xfa, token).ValueKind != JsonValueKind.Null)))
            throw new PdfNeedsAppearanceException();
    }

    // Returns the pages whose annotation list changes when only annotations whose subtype matches keep remain.
    internal static IReadOnlyList<(PdfObjectRef Page, JsonElement[] Kept)> ChangedAnnotations(PdfPageBoxes boxes,
        Func<string?, bool> keep, CancellationToken token)
    {
        var changed = new List<(PdfObjectRef, JsonElement[])>();
        foreach (var page in boxes.Pages)
        {
            token.ThrowIfCancellationRequested();
            if (!boxes.Dictionary(page).TryGetProperty("/Annots", out var raw)) continue;
            var annotations = boxes.Resolve(raw, token);
            if (annotations.ValueKind == JsonValueKind.Null) continue;
            if (annotations.ValueKind != JsonValueKind.Array) throw PdfOverlayInputException.Unsupported();
            var all = annotations.EnumerateArray().ToArray();
            var kept = all.Where(annotation => keep(Subtype(boxes, annotation, token))).ToArray();
            if (kept.Length != all.Length) changed.Add((page, kept));
        }
        return changed;
    }

    private static void WriteAnnotationUpdate(Utf8JsonWriter writer, PdfPageBoxes boxes,
        IReadOnlyList<(PdfObjectRef Page, JsonElement[] Kept)> pages)
    {
        PdfPageJob.StartUpdate(writer);
        foreach (var (page, kept) in pages)
        {
            writer.WriteStartObject(page.Key);
            writer.WriteStartObject("value");
            foreach (var property in boxes.Dictionary(page).EnumerateObject())
                if (property.Name != "/Annots") property.WriteTo(writer);
            WriteAnnotations(writer, kept);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }
        PdfPageJob.EndUpdate(writer);
    }

    internal static void WriteAnnotations(Utf8JsonWriter writer, JsonElement[] annotations)
    {
        if (annotations.Length == 0) return;
        writer.WriteStartArray("/Annots");
        foreach (var annotation in annotations) annotation.WriteTo(writer);
        writer.WriteEndArray();
    }

    private static string? Subtype(PdfPageBoxes boxes, JsonElement annotation, CancellationToken token)
    {
        var value = boxes.Resolve(annotation, token);
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("/Subtype", out var subtype)) return null;
        subtype = boxes.Resolve(subtype, token);
        return subtype.ValueKind == JsonValueKind.String ? subtype.GetString() : null;
    }
}
