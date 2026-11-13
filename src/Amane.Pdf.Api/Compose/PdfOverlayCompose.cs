using System.Globalization;
using System.Text.Json;

namespace Amane.Pdf.Api;

internal sealed partial class PdfOverlayBuilder
{
    internal async Task ComposeAsync(TemporaryPdfFiles files, ComposeUpload upload, PdfcpuProcessor pdfcpu,
        OverlayCapacity capacity, CancellationToken token)
    {
        var layout = upload.Layout;
        var pages = layout.Pages.OrderBy(page => page.Index).ToArray();
        var source = files.InputPath;
        string Work(string name) => Path.Combine(files.DirectoryPath, "compose-" + name);
        var metadata = Work("metadata.json");
        var update = Work("update.json");

        async Task Json(string path, Action<Utf8JsonWriter> write)
        {
            var budget = capacity.ReserveWrite(path, settings.OverlayGeneratedLimitBytes);
            await using (var output = TemporaryPdfFiles.CreatePrivateFile(path))
            using (var stream = new OverlayLimitedWriteStream(output, budget, token))
            using (var writer = new Utf8JsonWriter(stream))
                write(writer);
            capacity.Commit(path, budget);
        }

        async Task Pdf(string path, long limit, params string[] args)
        {
            var budget = capacity.ReserveWrite(path, limit, external: true);
            await qpdf.RunOverlayWriteAsync(files, args, path, budget, token);
            capacity.Commit(path, budget);
        }

        async Task<PdfPageBoxes> Metadata(string path, bool generated)
        {
            var limit = generated ? settings.OverlayGeneratedJsonLimitBytes : settings.OverlayJsonLimitBytes;
            await Pdf(metadata, limit, "--json=2", "--json-key=pages", "--json-key=qpdf", "--json-key=encrypt",
                "--json-stream-data=none", "--decode-level=none", path, metadata);
            var bytes = await File.ReadAllBytesAsync(metadata, token);
            capacity.Delete(metadata);
            return new(bytes, limit, generated ? PdfPageBoxes.GeneratedObjectLimit : PdfPageBoxes.InputObjectLimit, token);
        }

        async Task Blank(string path, IReadOnlyList<PdfPageAttributes> dimensions)
        {
            var json = Work("blank.json");
            await Json(json, writer => WriteBlank(writer, dimensions, token, null));
            await Pdf(path, settings.OverlayGeneratedLimitBytes, "--json-input", json, path);
            capacity.Delete(json);
        }

        PdfPageAttributes[] original;
        if (layout.Blank)
        {
            original = pages.Select(page =>
            {
                var box = new PdfBox(0, 0, page.Width / 100d, page.Height / 100d);
                return new PdfPageAttributes(box, box, box, 0, 1, box, box, box, null, null, null);
            }).ToArray();
        }
        else
        {
            await qpdf.ValidateAsync(files, token);
            using var input = await Metadata(source, generated: false);
            if (input.Encrypted != false) throw new PdfInputException();
            PdfFormDetector.RejectForms(input, token);
            if (input.Pages.Count != pages.Length) throw new ComposeProblem(400, "page-mismatch");
            original = pages.Select(page => input.Read(page.Index - 1, token)).ToArray();
            for (var i = 0; i < pages.Length; i++)
                if (Cpt(original[i].DisplayWidth) != pages[i].Width || Cpt(original[i].DisplayHeight) != pages[i].Height)
                    throw new ComposeProblem(400, "page-mismatch");
            await Json(update, writer => input.WriteUpdate(writer, 1, original, false, token));
        }

        var placed = PrepareText(layout, pages);
        try
        {
            var images = await upload.PrepareImagesAsync(files, settings, capacity, token);
            if (layout.Blank) await Blank(source, original);
            var usedImages = layout.Elements.Where(item => item.Image is not null)
                .GroupBy(item => item.Image!).ToDictionary(group => group.Key, group => group.Count());
            var overlays = new List<(int Page, string Path)>();
            foreach (var group in layout.Elements.GroupBy(element => element.Page))
            {
                var page = original[group.Key - 1];
                var blank = Work("canvas.pdf");
                await Blank(blank, [page]);
                var size = new PdfcpuPageSize(page.DisplayWidth, page.DisplayHeight);
                var runStart = 0;
                var items = group.ToArray();
                while (runStart < items.Length)
                {
                    var runEnd = runStart + 1;
                    while (runEnd < items.Length && items[runEnd].Kind == items[runStart].Kind) runEnd++;
                    var output = Work($"layer-{overlays.Count}.pdf");
                    if (items[runStart].Kind == "text")
                    {
                        var texts = items[runStart..runEnd].SelectMany(element => placed[element.Id]).ToArray();
                        if (texts.Length == 0)
                        {
                            runStart = runEnd;
                            continue;
                        }
                        var jsonBudget = capacity.ReserveWrite(files.PdfcpuLayerJsonPath, settings.OverlayGeneratedLimitBytes);
                        var pdfBudget = capacity.ReserveWrite(output, settings.OverlayGeneratedLimitBytes, external: true);
                        await pdfcpu.CreateAsync(files, new(new Dictionary<int, IReadOnlyList<PdfcpuText>> { [1] = texts },
                            [size]), blank, output, jsonBudget, pdfBudget, token, maxTextsPerPage: 600, maxTextLength: 1000);
                        capacity.Delete(files.PdfcpuLayerJsonPath);
                        capacity.Commit(output, pdfBudget);
                    }
                    else
                    {
                        var previous = blank;
                        for (var i = runStart; i < runEnd; i++)
                        {
                            var element = items[i];
                            var id = element.Image!;
                            var image = images[id];
                            var path = i == runEnd - 1 ? output : Work(i % 2 == 0 ? "stamp-a.pdf" : "stamp-b.pdf");
                            var budget = capacity.ReserveWrite(path, settings.OverlayOutputLimitBytes);
                            await pdfcpu.StampComposeImageAsync(files, image, Contain(element, image, size), previous, path, budget, token);
                            capacity.Commit(path, budget);
                            if (previous != blank) capacity.Delete(previous);
                            previous = path;
                            if (--usedImages[id] == 0) capacity.Delete(image.Path);
                        }
                    }
                    overlays.Add((group.Key, output));
                    runStart = runEnd;
                }
                capacity.Delete(blank);
            }

            if (overlays.Count == 0)
            {
                File.Move(source, files.OutputPath);
                capacity.Delete(source);
                capacity.Reserve(files.OutputPath, new FileInfo(files.OutputPath).Length);
                capacity.Delete(update);
                return;
            }

            var combined = layout.Blank ? files.OutputPath : Work("combined.pdf");
            var args = new List<string> { source };
            if (!layout.Blank) args.Add("--update-from-json=" + update);
            args.Add(combined);
            foreach (var (page, path) in overlays)
                args.AddRange(["--overlay", path, "--to=" + page.ToString(CultureInfo.InvariantCulture), "--from=1", "--"]);
            await Pdf(combined, settings.OverlayOutputLimitBytes, args.ToArray());
            foreach (var (_, path) in overlays) capacity.Delete(path);
            capacity.Delete(source);
            capacity.Delete(update);
            if (!layout.Blank)
            {
                using (var result = await Metadata(combined, generated: true))
                {
                    if (result.Pages.Count != pages.Length) throw new InvalidOperationException(Failure);
                    await Json(update, writer => result.WriteUpdate(writer, 1, original, true, token));
                }
                await Pdf(files.OutputPath, settings.OverlayOutputLimitBytes, combined, "--update-from-json=" + update, files.OutputPath);
                capacity.Delete(combined);
                capacity.Delete(update);
            }

            await qpdf.ValidateOutputAsync(files.OutputPath, token);
            using var final = await Metadata(files.OutputPath, generated: true);
            if (final.Encrypted != false || final.Pages.Count != pages.Length ||
                original.Where((page, index) => !page.SameEffective(final.Read(index, token))).Any())
                throw new InvalidOperationException(Failure);
        }
        catch (PdfOverlayInputException error) when (error.Reason == PdfOverlayReason.TooComplex)
        { throw new ComposeProblem(422, "image-too-complex"); }
        catch (PdfcpuCapacityException) { throw new ComposeProblem(422, "image-too-complex"); }
    }

    private static int Cpt(double value) => (int)decimal.Floor((decimal)value * 100);

    private static Dictionary<string, PdfcpuText[]> PrepareText(ComposeLayout layout, ComposePage[] pages)
    {
        var placed = new Dictionary<string, PdfcpuText[]>(StringComparer.Ordinal);
        var unsupported = new List<string>();
        var overflow = new List<string>();
        foreach (var element in layout.Elements.Where(element => element.Kind == "text"))
        {
            var lines = ComposeText.Wrap(element.Text!, element.Size!.Value, element.Rect[2], element.Rect[3],
                element.LineHeight!.Value, element.Font!);
            if (lines.Error == "unsupported-text") unsupported.Add(element.Id);
            else if (lines.Error == "text-overflow") overflow.Add(element.Id);
            else placed.Add(element.Id, ComposeText.Place(element, lines, pages[element.Page - 1]).ToArray());
        }
        if (unsupported.Count != 0) throw new ComposeProblem(400, "unsupported-text", elements: unsupported.Take(50).ToArray());
        if (overflow.Count != 0) throw new ComposeProblem(422, "text-overflow", elements: overflow.Take(50).ToArray());
        if (placed.Values.Sum(lines => lines.Length) > 4000) throw ComposeProblem.Limit("drawLines");
        if (layout.Elements.Where(element => element.Kind == "text").GroupBy(element => element.Page)
            .Any(group => group.Sum(element => placed[element.Id].Length) > 600)) throw ComposeProblem.Limit("drawLinesPerPage");
        return placed;
    }

    private static StampPlacement Contain(ComposeElement element, StampImage image, PdfcpuPageSize page)
    {
        var r = element.Rect;
        var scale = Math.Min(r[2] / (100d * image.Width), r[3] / (100d * image.Height));
        var width = scale * image.Width;
        var height = scale * image.Height;
        var x = r[0] / 100d + (r[2] / 100d - width) / 2;
        var y = page.Height - r[1] / 100d - (r[3] / 100d + height) / 2;
        return new(x, Math.Max(0, y), width, height, scale, page.Width, page.Height);
    }
}
