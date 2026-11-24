using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal static class PdfLayoutEndpoint
{
    private const double A4Width = 210 * 72.0 / 25.4;
    private const double A4Height = 297 * 72.0 / 25.4;

    internal static Task NUpAsync(HttpContext context, QpdfProcessor qpdf, IOptions<PdfOptions> options)
        => HandleAsync(context, qpdf, options.Value, "n-up.pdf", "pages-per-sheet");

    internal static Task ResizeAsync(HttpContext context, QpdfProcessor qpdf, IOptions<PdfOptions> options)
        => HandleAsync(context, qpdf, options.Value, "resized.pdf", "size");

    private static Task HandleAsync(HttpContext context, QpdfProcessor qpdf, PdfOptions options,
        string name, string key)
    {
        var pagesPerSheet = 0;
        return PdfEndpointHelpers.ExecuteAsync(context, options, name, () =>
        {
            var query = context.Request.Query;
            PdfEndpointHelpers.ValidateQueryKeys(query, key);
            pagesPerSheet = (key, query[key].ToString()) switch
            {
                ("size", "a4") => 1,
                ("pages-per-sheet", "2") => 2,
                ("pages-per-sheet", "4") => 4,
                _ => throw new BadHttpRequestException("Invalid page layout query.")
            };
        }, (files, token) => MultipartPdfUpload.ReadFileAsync(context.Request, files, options, token),
        (files, token) => LayoutAsync(files, qpdf, options, pagesPerSheet, token));
    }

    private static async Task LayoutAsync(TemporaryPdfFiles files, QpdfProcessor qpdf, PdfOptions options,
        int pagesPerSheet, CancellationToken token)
    {
        var job = new PdfPageJob(files, qpdf, options);
        job.TrackInput();
        await qpdf.ValidateAsync(files, token);
        var update = job.PathFor("page-update.json");
        PdfPageAttributes[] pages;
        using (var input = await job.ReadAsync(files.InputPath, token))
        {
            if (input.Pages.Count == 0 || input.Pages.Count > options.OverlayMaxPages)
                throw PdfOverlayInputException.TooComplex();
            if (PdfFormDetector.ContainsSignatureStructure(input, token))
                throw new PdfOverlayInputException(PdfOverlayReason.SignatureStructure);
            // Optional-content defaults depend on the original catalog.
            if (input.Catalog(token).TryGetProperty("/OCProperties", out _))
                throw PdfOverlayInputException.Unsupported();
            PdfFlattenEndpoint.ThrowIfAppearancesMustBeGenerated(input, token);
            pages = Enumerable.Range(0, input.Pages.Count).Select(i => input.Read(i, token)).ToArray();
            // Form values and comments are burned into the source pages. Links and popups
            // cannot be burned in and would not work on the new sheets.
            var annotations = PdfFlattenEndpoint.ChangedAnnotations(input,
                subtype => subtype is not ("/Link" or "/Popup"), token).ToDictionary(page => page.Page, page => page.Kept);
            job.WriteJson(update, writer => WriteSourceUpdate(writer, input, pages, annotations), token);
        }

        var normalized = job.PathFor("page-sources.pdf");
        await job.WritePdfAsync(normalized, [files.InputPath, "--update-from-json=" + update,
            "--flatten-annotations=all", "--remove-acroform", normalized], options.OverlayOutputLimitBytes, token);
        job.Delete(update);
        job.Delete(files.InputPath);

        var sheets = pages.Chunk(pagesPerSheet).ToArray();
        var sizes = sheets.Select(sheet => SheetSize(pagesPerSheet, sheet[0])).ToArray();
        var blankJson = job.PathFor("page-blank.json");
        var blank = job.PathFor("page-blank.pdf");
        job.WriteJson(blankJson, writer => WriteBlank(writer, sizes), token);
        await job.WritePdfAsync(blank, ["--json-input", blankJson, blank], options.OverlayGeneratedLimitBytes, token);
        job.Delete(blankJson);

        // Repeated destination pages import several forms in a single overlay pass.
        // The next update supplies each form's own cell placement.
        var destinations = string.Join(',', sheets.SelectMany((sheet, index) =>
            Enumerable.Repeat((index + 1).ToString(CultureInfo.InvariantCulture), sheet.Length)));
        var imported = job.PathFor("page-forms.pdf");
        await job.WritePdfAsync(imported, [blank, imported,
            "--overlay", normalized, "--to=" + destinations, "--from=1-z", "--"], options.OverlayOutputLimitBytes, token);
        job.Delete(blank);
        job.Delete(normalized);

        using (var metadata = await job.ReadAsync(imported, token, generated: true))
        {
            if (metadata.Pages.Count != sheets.Length) throw new InvalidOperationException("Incorrect layout page count.");
            // qpdf's overlay copies annotations from source pages. Only annotations without
            // a usable appearance can remain here, and they would not match the new placement.
            if (PdfFlattenEndpoint.ChangedAnnotations(metadata, _ => false, token).Count > 0)
                throw new PdfNeedsAppearanceException();
            job.WriteJson(update, writer => WriteSheetContents(writer, metadata, sheets, sizes, pagesPerSheet, token), token);
        }
        await job.WritePdfAsync(files.OutputPath, [imported, "--update-from-json=" + update, files.OutputPath],
            options.OverlayOutputLimitBytes, token);
        job.Delete(imported);
        job.Delete(update);
        try { await qpdf.ValidateOutputAsync(files.OutputPath, token); }
        catch (PdfInputException) { throw new InvalidOperationException("PDF output validation failed."); }
    }

    private static void WriteSourceUpdate(Utf8JsonWriter writer, PdfPageBoxes input, PdfPageAttributes[] pages,
        Dictionary<PdfObjectRef, JsonElement[]> annotations)
    {
        PdfPageJob.StartUpdate(writer);
        for (var i = 0; i < pages.Length; i++)
        {
            var reference = input.Pages[i];
            writer.WriteStartObject(reference.Key);
            writer.WriteStartObject("value");
            foreach (var property in input.Dictionary(reference).EnumerateObject())
                if (property.Name is not ("/TrimBox" or "/Rotate" or "/UserUnit") &&
                    (property.Name != "/Annots" || !annotations.ContainsKey(reference)))
                    property.WriteTo(writer);
            if (annotations.TryGetValue(reference, out var kept)) PdfFlattenEndpoint.WriteAnnotations(writer, kept);
            writer.WritePropertyName("/TrimBox");
            pages[i].Crop.Write(writer);
            writer.WriteNumber("/Rotate", 0);
            writer.WriteNumber("/UserUnit", 1);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }
        PdfPageJob.EndUpdate(writer);
    }

    private static (double Width, double Height) SheetSize(int perSheet, PdfPageAttributes first)
        => perSheet == 2 || (perSheet == 1 && first.DisplayWidth > first.DisplayHeight)
            ? (A4Height, A4Width) : (A4Width, A4Height);

    private static void WriteBlank(Utf8JsonWriter writer, (double Width, double Height)[] sizes)
    {
        static string Reference(int number) => string.Create(CultureInfo.InvariantCulture, $"{number} 0 R");

        PdfPageJob.StartUpdate(writer, newPdf: true);
        var kids = new string[sizes.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            var (width, height) = sizes[i];
            kids[i] = Reference(i + 3);
            writer.WriteStartObject("obj:" + kids[i]);
            writer.WriteStartObject("value");
            writer.WriteString("/Type", "/Page");
            writer.WriteString("/Parent", "2 0 R");
            writer.WritePropertyName("/MediaBox");
            new PdfBox(0, 0, width, height).Write(writer);
            writer.WriteStartObject("/Resources");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }
        writer.WritePropertyName("obj:1 0 R");
        JsonSerializer.Serialize(writer, new { value = new Dictionary<string, string> { ["/Type"] = "/Catalog", ["/Pages"] = "2 0 R" } });
        writer.WritePropertyName("obj:2 0 R");
        JsonSerializer.Serialize(writer, new { value = new Dictionary<string, object> { ["/Type"] = "/Pages", ["/Kids"] = kids, ["/Count"] = kids.Length } });
        writer.WritePropertyName("trailer");
        JsonSerializer.Serialize(writer, new { value = new Dictionary<string, object> { ["/Root"] = "1 0 R", ["/Size"] = kids.Length + 3 } });
        PdfPageJob.EndUpdate(writer);
    }

    private static void WriteSheetContents(Utf8JsonWriter writer, PdfPageBoxes metadata, PdfPageAttributes[][] sheets,
        (double Width, double Height)[] sizes, int pagesPerSheet, CancellationToken token)
    {
        PdfPageJob.StartUpdate(writer);
        for (var i = 0; i < sheets.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var page = metadata.Dictionary(metadata.Pages[i]);
            var resources = metadata.Resolve(page.GetProperty("/Resources"), token);
            var forms = metadata.Resolve(resources.GetProperty("/XObject"), token);
            var stream = PdfObjectRef.Parse(page.GetProperty("/Contents")) ??
                throw new InvalidOperationException("Layout page contents are unavailable.");
            var (width, height) = sizes[i];
            var columns = pagesPerSheet == 1 ? 1 : 2;
            var rows = pagesPerSheet == 4 ? 2 : 1;
            var cellWidth = width / columns;
            var cellHeight = height / rows;
            var contents = new StringBuilder();
            for (var cell = 0; cell < sheets[i].Length; cell++)
            {
                // qpdf starts at /Fx0 for the blank destination and allocates /Fx1 onward in source order.
                var name = "/Fx" + (cell + 1).ToString(CultureInfo.InvariantCulture);
                if (!forms.TryGetProperty(name, out var form) || PdfObjectRef.Parse(form) is null)
                    throw new InvalidOperationException("A source page form is unavailable.");
                var x = cell % columns * cellWidth;
                var y = (rows - 1 - cell / columns) * cellHeight;
                contents.Append(Place(sheets[i][cell], name, x, y, cellWidth, cellHeight));
            }

            writer.WriteStartObject(stream.Key);
            writer.WriteStartObject("stream");
            writer.WriteStartObject("dict");
            writer.WriteEndObject();
            writer.WriteBase64String("data", Encoding.ASCII.GetBytes(contents.ToString()));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }
        PdfPageJob.EndUpdate(writer);
    }

    private static string Place(PdfPageAttributes page, string name, double x, double y, double width, double height)
    {
        var box = page.Crop;
        var turned = page.NormalizedRotate is 90 or 270;
        var sourceWidth = turned ? box.Height : box.Width;
        var sourceHeight = turned ? box.Width : box.Height;
        var scale = Math.Min(width / sourceWidth, height / sourceHeight);
        var left = x + (width - sourceWidth * scale) / 2;
        var bottom = y + (height - sourceHeight * scale) / 2;
        var matrix = page.NormalizedRotate switch
        {
            90 => new[] { 0, -scale, scale, 0, left - box.Bottom * scale, bottom + box.Right * scale },
            180 => [-scale, 0, 0, -scale, left + box.Right * scale, bottom + box.Top * scale],
            270 => [0, scale, -scale, 0, left + box.Top * scale, bottom - box.Left * scale],
            _ => new[] { scale, 0, 0, scale, left - box.Left * scale, bottom - box.Bottom * scale }
        };
        // PDF real tokens use decimal notation. Reject transforms outside useful PDF precision.
        if (scale < 1e-12 || matrix.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1e12))
            throw PdfOverlayInputException.Unsupported();
        return "q\n" + string.Join(' ', matrix.Select(value => value.ToString("0.############", CultureInfo.InvariantCulture))) +
            " cm\n" + name + " Do\nQ\n";
    }
}
