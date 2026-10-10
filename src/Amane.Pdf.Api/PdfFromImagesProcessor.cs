using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal sealed class PdfImagesStartupException : Exception;
internal sealed record FromImagesStage(string Name, long Bytes, long ReservedPeak);

internal sealed class PdfFromImagesProcessor(PdfOptions options, TemporaryPdfFiles files,
    QpdfProcessor qpdf, PdfcpuProcessor pdfcpu)
{
    internal FromImagesCapacity Capacity { get; } = new(options.ImageJobLimitBytes);
    internal async Task ProcessAsync(IReadOnlyList<string> inputs, bool standard, CancellationToken token,
        Action<FromImagesStage>? observe = null)
    {
        foreach (var input in inputs) Capacity.Reserve(input, new FileInfo(input).Length);
        observe?.Invoke(new("uploads", inputs.Sum(input => new FileInfo(input).Length), Capacity.Peak));
        var pages = new List<string>(); long pageBytes = 0;
        for (var i = 0; i < inputs.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var input = inputs[i]; var index = i + 1; string normalized; ImageInfo image;
            if (ImageInputInspector.IsPng(input))
            {
                normalized = files.ImagePath(index, "normalized.png");
                var budget = Capacity.ReserveWrite(normalized, new FileInfo(input).Length, external: false);
                image = await PngSanitizer.WriteAsync(input, normalized, budget, options.MaxUploadImagePixels,
                    options.ImagePngWorkingLimitBytes, token);
                Capacity.Commit(normalized, budget);
            }
            else
            {
                image = ImageInputInspector.ReadJpeg(input, options.MaxImageInputBytes, options.MaxUploadImagePixels, token);
                (normalized, image) = await new JpegNormalizer(options, files, Capacity)
                    .NormalizeAsync(input, image, index, standard, token);
            }
            observe?.Invoke(new("normalized", new FileInfo(normalized).Length, Capacity.Peak));
            Capacity.Delete(input); // Release upload before retaining both normalized image and page.
            var page = files.ImagePath(index, "page.pdf");
            var pageBudget = Capacity.ReserveWrite(page, options.ImageOutputLimitBytes - pageBytes, external: true);
            await pdfcpu.ImportImageAsync(files, normalized, image, page, pageBudget, token);
            Capacity.Commit(page, pageBudget);
            await qpdf.CheckImagePdfAsync(files, page, 1, pageBudget, token);
            pageBytes = checked(pageBytes + new FileInfo(page).Length);
            observe?.Invoke(new("page", new FileInfo(page).Length, Capacity.Peak));
            Capacity.Delete(normalized);
            pages.Add(page);
        }
        var output = Path.GetFullPath(files.OutputPath);
        var finalBudget = Capacity.ReserveWrite(output, options.ImageOutputLimitBytes, external: true);
        await qpdf.MergeImagePagesAsync(files, pages, finalBudget, token);
        Capacity.Commit(output, finalBudget);
        observe?.Invoke(new("final", new FileInfo(output).Length, Capacity.Peak));
    }

    internal static async Task ValidateStartupAsync(PdfOptions options, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            using var files = new TemporaryPdfFiles(Path.GetFullPath(options.TempRoot));
            var input = files.ImagePath(1, "upload.bin");
            var seed = files.ImagePath(1, "decoded.pnm");
            await using (var stream = TemporaryPdfFiles.CreatePrivateFile(seed))
            {
                await stream.WriteAsync("P6\n32 48\n255\n"u8.ToArray(), token);
                await stream.WriteAsync(Enumerable.Repeat((byte)96, 32 * 48 * 3).ToArray(), token);
            }
            var result = await ExternalProcessRunner.RunAsync(ProcessMemoryLimits.CreateRequest(options.PrlimitPath,
                options.CjpegPath, ["-strict", "-sample", "2x2", "-maxmemory", "64M", "-outfile", input, seed],
                options.JpegAddressSpaceLimitBytes, 8192, null), token);
            if (result.ExitCode != 0) throw new InvalidOperationException();
            File.Delete(seed);
            // Orientation 6 exercises a real rotation and removal of APP1 metadata.
            var jpeg = await File.ReadAllBytesAsync(input, token);
            var exif = Convert.FromHexString("FFE1002245786966000049492A0008000000010012010300010000000600000000000000");
            await File.WriteAllBytesAsync(input, [.. jpeg[..2], .. exif, .. jpeg[2..]], token);
            File.SetUnixFileMode(input, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var settings = Options.Create(options);
            var qpdf = new QpdfProcessor(settings); var pdfcpu = new PdfcpuProcessor(settings);
            // Reuse one private job, deleting each completed fixture before the next.
            foreach (var standard in new[] { true, false })
            {
                if (!File.Exists(input)) throw new InvalidOperationException();
                var copy = await File.ReadAllBytesAsync(input, token); // Tiny synthetic fixture only.
                await new PdfFromImagesProcessor(options, files, qpdf, pdfcpu).ProcessAsync([input], standard, token);
                foreach (var path in Directory.GetFiles(files.DirectoryPath)) File.Delete(path);
                await using var stream = TemporaryPdfFiles.CreatePrivateFile(input);
                await stream.WriteAsync(copy, token);
            }
            File.Delete(input);
            await using (var stream = TemporaryPdfFiles.CreatePrivateFile(input))
            {
                // Synthetic RGBA PNG, no user data or external resources.
                await stream.WriteAsync(Convert.FromBase64String(StartupPng), token);
            }
            foreach (var path in Directory.GetFiles(files.DirectoryPath).Where(path => Path.GetFullPath(path) != input))
                File.Delete(path);
            await new PdfFromImagesProcessor(options, files, qpdf, pdfcpu).ProcessAsync([input], false, token);
        }
        catch (Exception) { throw new PdfImagesStartupException(); }
    }
    internal const string StartupPng = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAYCAYAAADzoH0MAAAAIUlEQVR4nGO8IyLSwEABYKJE86gBowaMGjBqwKgBg80AANH/AbRalHV6AAAAAElFTkSuQmCC";
}
