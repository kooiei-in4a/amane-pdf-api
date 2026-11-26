using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal readonly record struct RenderFill(int Left, int Top, int Right, int Bottom);
internal sealed record RenderPage(string InputPath, string OutputPath, int Page,
    double ExpectedWidth, double ExpectedHeight, int Width, int Height, long MaxBytes,
    IReadOnlyList<RenderFill> Fills);

// PdfRedactProcessor maps PdfRenderException to PdfRedactException.
// PdfEndpointHelpers returns HTTP 422 with the corresponding redact reason.
internal enum PdfRenderReason { TooComplex, UnsupportedPdf, OutputTooLarge, PageTooLarge, TooManyPages }
internal sealed class PdfRenderException(PdfRenderReason reason) : Exception("PDFの描画に失敗しました。")
{
    internal PdfRenderReason Reason { get; } = reason;
}
internal sealed class PdfRendererStartupException : Exception;

internal sealed class PdfRenderer(IOptions<PdfOptions> options)
{
    private const string Failure = "PDFの描画に失敗しました。";
    internal const long BootstrapFileSizeBytes = 8 * 1024 * 1024;
    private readonly PdfOptions settings = options.Value;

    internal static bool IsValid(PdfOptions value) => value.RenderDataLimitBytes is > 0 and <= 384 * 1048576 &&
        new[] { value.DotnetPath, value.RendererPath, value.PdfiumPath, value.RenderFontDir }
            .All(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.Contains('\0'));

    internal ExternalProcessRequest CreateRequest(string directory, IReadOnlyList<string> arguments, long maxBytes,
        int? stdoutLimit = null)
        => ProcessMemoryLimits.CreateRequest(settings.PrlimitPath, settings.DotnetPath,
            [settings.RendererPath, .. arguments, "--pdfium", settings.PdfiumPath, "--font-dir", settings.RenderFontDir],
            settings.RenderDataLimitBytes, Math.Max(checked(maxBytes + 1), BootstrapFileSizeBytes),
            new Dictionary<string, string> { ["DOTNET_EnableDiagnostics"] = "0" },
            stdoutLimit, memoryLimit: ProcessMemoryLimit.Data) with { WorkingDirectory = directory };

    internal static (int Width, int Height) GetPixelSize(PdfPageAttributes page, int dpi)
    {
        if (!TryGetPixelSize(page, dpi, out var size)) throw new PdfRenderException(PdfRenderReason.PageTooLarge);
        return size;
    }

    internal static (int Width, int Height, int Dpi) FitPixelSize(PdfPageAttributes page, int minDpi, int maxDpi)
    {
        if (TryGetPixelSize(page, maxDpi, out var size)) return (size.Width, size.Height, maxDpi);

        size = GetPixelSize(page, minDpi);
        var highest = maxDpi - 1;
        while (minDpi < highest)
        {
            var dpi = minDpi + (highest - minDpi + 1) / 2;
            if (TryGetPixelSize(page, dpi, out var candidate))
            {
                minDpi = dpi;
                size = candidate;
            }
            else highest = dpi - 1;
        }
        return (size.Width, size.Height, minDpi);
    }

    private static bool TryGetPixelSize(PdfPageAttributes page, int dpi, out (int Width, int Height) size)
    {
        size = default;
        var width = Math.Ceiling(page.DisplayWidth * dpi / 72);
        var height = Math.Ceiling(page.DisplayHeight * dpi / 72);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 ||
            width > RedactRaster.MaxSide || height > RedactRaster.MaxSide || width * height > RedactRaster.MaxPixels)
            return false;
        size = ((int)width, (int)height);
        return true;
    }

    internal static void CheckImages(RedactDocument document, int page)
    {
        try { document.CheckImages(page); }
        catch (PdfRedactException exception)
        {
            throw new PdfRenderException(exception.Reason == PdfRedactReason.UnsupportedPdf
                ? PdfRenderReason.UnsupportedPdf : PdfRenderReason.TooComplex);
        }
    }

    internal Task RenderJpegAsync(TemporaryPdfFiles files, RenderPage page, CancellationToken token)
        => RenderAsync(files, page, png: false, token);

    internal Task RenderPngAsync(TemporaryPdfFiles files, RenderPage page, CancellationToken token)
        => RenderAsync(files, page, png: true, token);

    internal async Task<int[]> InspectSearchablePagesAsync(TemporaryPdfFiles files, int[] pages, CancellationToken token)
    {
        var result = await ExternalProcessRunner.RunAsync(CreateRequest(Path.GetFullPath(files.DirectoryPath),
            ["inspect-text", "--input", Path.GetFullPath(files.InputPath), "--pages",
                string.Join(',', pages.Order().Select(page => Number(page)))],
            maxBytes: 0, stdoutLimit: 128), token);
        token.ThrowIfCancellationRequested();
        CheckExitCode(result.ExitCode);
        if (result.Stdout is null) throw new InvalidOperationException(Failure);
        var output = Encoding.ASCII.GetString(result.Stdout).Trim();
        if (output.Length == 0) return [];
        var skipped = new List<int>();
        foreach (var item in output.Split(','))
        {
            if (!int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var page) ||
                !pages.Contains(page) || (skipped.Count > 0 && skipped[^1] >= page))
                throw new InvalidOperationException(Failure);
            skipped.Add(page);
        }
        return [.. skipped];
    }

    private async Task RenderAsync(TemporaryPdfFiles files, RenderPage page, bool png, CancellationToken token)
    {
        using var stage = PdfProcessingDiagnostics.Measure(PdfStage.Render);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var directory = Path.GetFullPath(files.DirectoryPath);
        foreach (var path in new[] { page.InputPath, page.OutputPath })
            if (!Path.IsPathFullyQualified(path) || Path.GetDirectoryName(Path.GetFullPath(path)) != directory)
                throw new ArgumentException(Failure);
        if (page.InputPath == page.OutputPath || File.Exists(page.OutputPath) || Directory.Exists(page.OutputPath) ||
            page.MaxBytes is <= 0 or long.MaxValue) throw new ArgumentException(Failure);
        var arguments = new List<string>
        {
            png ? "render-png" : "render-jpeg", "--input", page.InputPath, "--page", Number(page.Page),
            "--expect-size", string.Create(CultureInfo.InvariantCulture, $"{page.ExpectedWidth},{page.ExpectedHeight}"),
            "--width", Number(page.Width), "--height", Number(page.Height),
            "--max-bytes", Number(page.MaxBytes), "--output", page.OutputPath
        };
        if (!png) arguments.AddRange(["--quality", "85"]);
        foreach (var fill in page.Fills)
        {
            arguments.Add("--fill");
            arguments.Add(string.Create(CultureInfo.InvariantCulture, $"{fill.Left},{fill.Top},{fill.Right},{fill.Bottom}"));
        }
        try
        {
            var result = await ExternalProcessRunner.RunAsync(CreateRequest(directory, arguments, page.MaxBytes), token);
            token.ThrowIfCancellationRequested();
            CheckExitCode(result.ExitCode);
            if (!File.Exists(page.OutputPath) || new FileInfo(page.OutputPath).Length <= 0)
                throw new InvalidOperationException(Failure);
            if (new FileInfo(page.OutputPath).Length > page.MaxBytes)
                throw new PdfRenderException(PdfRenderReason.OutputTooLarge);
        }
        catch
        {
            // The runner kills and reaps first; deleting cannot race a still-running renderer.
            File.Delete(page.OutputPath);
            throw;
        }
        stage.Complete();
    }

    internal static void CheckExitCode(int code)
    {
        if (code == 0) return;
        throw code switch
        {
            3 or 4 or 12 => new PdfRenderException(PdfRenderReason.UnsupportedPdf),
            5 => new PdfRenderException(PdfRenderReason.TooComplex),
            10 => new PdfRenderException(PdfRenderReason.OutputTooLarge),
            _ => new InvalidOperationException(Failure)
        };
    }

    internal static async Task ValidateStartupAsync(PdfOptions value, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (!IsValid(value)) throw new InvalidOperationException();
            using var files = new TemporaryPdfFiles(value.TempRoot);
            var renderer = new PdfRenderer(Options.Create(value));
            var result = await ExternalProcessRunner.RunAsync(renderer.CreateRequest(
                Path.GetFullPath(files.DirectoryPath), ["self-test"], 0), token);
            if (result.ExitCode != 0) throw new InvalidOperationException();
        }
        catch (Exception) { throw new PdfRendererStartupException(); }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
