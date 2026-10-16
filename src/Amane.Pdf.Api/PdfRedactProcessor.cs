using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal sealed record RedactResult(int[] RedactedPages, int[] RasterizedPages);

internal sealed class PdfRedactProcessor(QpdfProcessor qpdf, PdfRenderer renderer, IOptions<PdfOptions> options)
{
    private readonly PdfOptions settings = options.Value;
    internal static bool IsValid(PdfOptions value) =>
        value.MaxRedactInputBytes is > 0 and <= 30 * 1048576 &&
        value.MaxRedactReplacementBytes is > 0 and <= 30 * 1048576 &&
        value.RedactOutputLimitBytes is > 0 and <= 62 * 1048576 &&
        value.RedactJsonLimitBytes is > 0 and <= 32 * 1048576 &&
        value.RedactJobLimitBytes is > 0 and <= 124 * 1048576 &&
        value.RedactJobLimitBytes >= CleanCapacity.Allocated(value.MaxRedactInputBytes);

    internal async Task<RedactResult> RedactAsync(TemporaryPdfFiles files, RedactRegions regions, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var capacity = new CleanCapacity(settings.RedactJobLimitBytes, PdfRedactException.Complex);
        int count;
        RedactPage[] plans;
        try
        {
            capacity.Reserve(files.InputPath, new FileInfo(files.InputPath).Length);
            await qpdf.ValidateAsync(files, token);
            await Inspect(files.InputPath, false);
            bool changed;
            using (var input = Metadata())
            {
                count = input.Pages.Count;
                regions.CheckPageCount(count);
                var stripper = new PdfEmbeddedFileStripper(input.Document, token);
                changed = stripper.HasChanges;
                if (changed) await WriteJson(files.RedactStripPath, settings.RedactJsonLimitBytes, stripper.Strip);
            }
            capacity.Delete(files.RedactJsonPath);
            var job = new Dictionary<string, object>
            {
                ["inputFile"] = Path.GetFullPath(files.InputPath), ["outputFile"] = files.RedactFlatPath,
                ["flattenAnnotations"] = "all", ["generateAppearances"] = "", ["removeAcroform"] = "", ["removeStructure"] = ""
            };
            if (changed) job["updateFromJson"] = files.RedactStripPath;
            await WriteJson(files.JobPath, 1024 * 1024, writer => JsonSerializer.Serialize(writer, job));
            await WriteQpdf(["--job-json-file=" + Path.GetFullPath(files.JobPath)], files.RedactFlatPath,
                settings.RedactJobLimitBytes, allowWarning: true);
            capacity.Delete(files.InputPath); capacity.Delete(files.RedactStripPath); capacity.Delete(files.JobPath);
            await WriteQpdf(["--empty", "--remove-unreferenced-resources=yes", "--pages", files.RedactFlatPath, "1-z", "--", files.RedactPrunedPath],
                files.RedactPrunedPath, settings.RedactJobLimitBytes, prune: true);
            capacity.Delete(files.RedactFlatPath);
            await Inspect(files.RedactPrunedPath, false);
            using (var pruned = Metadata())
            {
                var document = new RedactDocument(pruned, token);
                if (pruned.Pages.Count != count) throw Invalid();
                var selected = document.RasterizedPages(regions);
                document.CheckSurvivingReferences(regions, selected);
                plans = selected.Select(number =>
                {
                    document.CheckImages(number);
                    var page = pruned.Read(number - 1, token);
                    var raster = RedactRaster.Create(page.DisplayWidth, page.DisplayHeight);
                    return new RedactPage(number, raster, page.DisplayWidth / page.UserUnit, page.DisplayHeight / page.UserUnit,
                        files.RedactPagePath(number), regions.Items.Where(region => region.Page == number).Select(raster.Fill).ToArray());
                }).ToArray();
                await WriteJson(files.RedactFinalPath, settings.RedactJsonLimitBytes, writer => document.WriteFinal(writer, plans, count));
            }
            capacity.Delete(files.RedactJsonPath);
            var jpegRemaining = settings.MaxRedactReplacementBytes;
            foreach (var plan in plans)
            {
                var budget = capacity.ReserveWrite(plan.JpegPath, jpegRemaining, PdfRedactException.Output, sentinel: true);
                await renderer.RenderJpegAsync(files, new(files.RedactPrunedPath, plan.JpegPath, plan.Number,
                    plan.ExpectedWidth, plan.ExpectedHeight, plan.Raster.Width, plan.Raster.Height, budget, plan.Fills), token);
                var size = new FileInfo(plan.JpegPath).Length;
                capacity.Reserve(plan.JpegPath, size); jpegRemaining -= size;
            }
            await WriteQpdf([files.RedactPrunedPath, "--update-from-json=" + files.RedactFinalPath, Path.GetFullPath(files.OutputPath)],
                files.OutputPath, settings.RedactOutputLimitBytes, output: true);
            capacity.Delete(files.RedactPrunedPath); capacity.Delete(files.RedactFinalPath);
            foreach (var plan in plans) capacity.Delete(plan.JpegPath);
        }
        catch (PdfRenderException exception)
        {
            throw new PdfRedactException(exception.Reason switch
            {
                PdfRenderReason.TooComplex => PdfRedactReason.TooComplex,
                PdfRenderReason.UnsupportedPdf => PdfRedactReason.UnsupportedPdf,
                PdfRenderReason.OutputTooLarge => PdfRedactReason.OutputTooLarge,
                _ => throw Invalid()
            });
        }
        catch (PdfOverlayInputException exception)
        {
            throw new PdfRedactException(exception.Reason == PdfOverlayReason.TooComplex
                ? PdfRedactReason.TooComplex : PdfRedactReason.UnsupportedPdf);
        }
        catch (PdfCleanTooComplexException) { throw PdfRedactException.Complex(); }

        // Any failure of output verification is internal, including resource/parser
        // failures. No success headers or output are sent until every check passes.
        try
        {
            await qpdf.ValidateAsync(files.OutputPath, token);
            await Inspect(files.OutputPath, true);
            Dictionary<PdfObjectRef, byte[]> contents;
            using (var result = Metadata())
                contents = new RedactDocument(result, token).Verify(plans, count);
            capacity.Delete(files.RedactJsonPath);
            var args = new List<string> { "--json=2", "--json-key=qpdf", "--json-stream-data=inline", "--decode-level=generalized" };
            args.AddRange(contents.Keys.Select(reference => reference.Argument));
            args.Add(Path.GetFullPath(files.OutputPath));
            var captured = await ExternalProcessRunner.RunAsync(ProcessMemoryLimits.CreateRequest(settings.PrlimitPath,
                settings.QpdfPath, args, settings.QpdfAddressSpaceLimitBytes, settings.QpdfJpegMemory, 64 * 1024), token);
            if (captured.ExitCode != 0 || captured.Stdout is null) throw Invalid();
            using var document = PdfEmbeddedFileStripper.Parse(captured.Stdout, token);
            RedactDocument.VerifyContents(document, contents);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw Invalid(); }
        return new(regions.Pages, plans.Select(page => page.Number).ToArray());

        PdfPageBoxes Metadata() => new(File.ReadAllBytes(files.RedactJsonPath), settings.RedactJsonLimitBytes,
            PdfPageBoxes.InputObjectLimit, token);

        async Task WriteJson(string path, long limit, Action<Utf8JsonWriter> write)
        {
            var budget = capacity.ReserveWrite(path, limit);
            await using var stream = TemporaryPdfFiles.CreatePrivateFile(path);
            using var bounded = new CleanLimitedWriteStream(stream, budget, token, PdfRedactException.Complex);
            using var writer = new Utf8JsonWriter(bounded);
            write(writer); await writer.FlushAsync(token);
            capacity.Reserve(path, bounded.Length);
        }

        async Task Inspect(string pdf, bool output)
        {
            var keys = output ? new[] { "--json-key=qpdf", "--json-key=pages", "--json-key=attachments" }
                : ["--json-key=qpdf", "--json-key=pages"];
            await WriteQpdf(["--json=2", .. keys, "--json-stream-data=none", "--decode-level=none", Path.GetFullPath(pdf), files.RedactJsonPath],
                files.RedactJsonPath, settings.RedactJsonLimitBytes);
        }

        async Task WriteQpdf(string[] arguments, string path, long limit, bool allowWarning = false, bool prune = false, bool output = false)
        {
            Func<Exception> failure = output ? PdfRedactException.Output : PdfRedactException.Complex;
            var budget = capacity.ReserveWrite(path, limit, failure);
            using (TemporaryPdfFiles.CreatePrivateFile(path)) { }
            var request = ProcessMemoryLimits.CreateRequest(settings.PrlimitPath, settings.QpdfPath, arguments,
                settings.QpdfAddressSpaceLimitBytes, budget, new Dictionary<string, string> { ["JPEGMEM"] = settings.QpdfJpegMemory })
                with { WorkingDirectory = Path.GetFullPath(files.DirectoryPath) };
            var result = await ExternalProcessRunner.RunAsync(request, token);
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (prune && result.ExitCode != 0) throw PdfRedactException.Unsupported();
            if (file.Exists && file.Length >= budget) throw failure();
            if (result.ExitCode != 0 && !(allowWarning && result.ExitCode == 3) || !file.Exists || file.Length == 0) throw Invalid();
            capacity.Reserve(path, file.Length);
        }
    }

    private static InvalidOperationException Invalid() => new("PDF redaction failed.");
}
