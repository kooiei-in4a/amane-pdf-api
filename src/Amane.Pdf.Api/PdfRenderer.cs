using System.Globalization;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal readonly record struct RenderFill(int Left, int Top, int Right, int Bottom);
internal sealed record RenderPage(string InputPath, string OutputPath, int Page,
    double ExpectedWidth, double ExpectedHeight, int Width, int Height, long MaxBytes,
    IReadOnlyList<RenderFill> Fills);

// PR B maps these failures to the corresponding redact 422 reason.
internal enum PdfRenderReason { TooComplex, UnsupportedPdf, OutputTooLarge }
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

    internal ExternalProcessRequest CreateRequest(string directory, IReadOnlyList<string> arguments, long maxBytes)
        => ProcessMemoryLimits.CreateRequest(settings.PrlimitPath, settings.DotnetPath,
            [settings.RendererPath, .. arguments, "--pdfium", settings.PdfiumPath, "--font-dir", settings.RenderFontDir],
            settings.RenderDataLimitBytes, Math.Max(checked(maxBytes + 1), BootstrapFileSizeBytes),
            new Dictionary<string, string> { ["DOTNET_EnableDiagnostics"] = "0" },
            memoryLimit: ProcessMemoryLimit.Data) with { WorkingDirectory = directory };

    internal async Task RenderJpegAsync(TemporaryPdfFiles files, RenderPage page, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var directory = Path.GetFullPath(files.DirectoryPath);
        foreach (var path in new[] { page.InputPath, page.OutputPath })
            if (!Path.IsPathFullyQualified(path) || Path.GetDirectoryName(Path.GetFullPath(path)) != directory)
                throw new ArgumentException(Failure);
        if (page.InputPath == page.OutputPath || File.Exists(page.OutputPath) || Directory.Exists(page.OutputPath) ||
            page.MaxBytes is <= 0 or long.MaxValue) throw new ArgumentException(Failure);
        var arguments = new List<string>
        {
            "render-jpeg", "--input", page.InputPath, "--page", Number(page.Page),
            "--expect-size", string.Create(CultureInfo.InvariantCulture, $"{page.ExpectedWidth},{page.ExpectedHeight}"),
            "--width", Number(page.Width), "--height", Number(page.Height), "--quality", "85",
            "--max-bytes", Number(page.MaxBytes), "--output", page.OutputPath
        };
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
