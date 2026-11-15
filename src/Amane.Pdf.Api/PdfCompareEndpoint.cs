using System.Globalization;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal enum PdfCompareReason { TextUnavailable, TooLarge, UnsupportedPdf }
internal sealed class PdfCompareException(PdfCompareReason reason) : Exception
{
    internal PdfCompareReason Reason { get; } = reason;
}

internal static class PdfCompareEndpoint
{
    private const long WorkerMemory = 384 * 1024 * 1024;
    private const long WorkerFileLimit = 8 * 1024 * 1024; // Includes CoreCLR bootstrap and escaped UTF-16.

    internal static bool IsValid(PdfOptions options) => options.MaxComparePages is >= 1 and <= 300 &&
        options.MaxCompareCharacters is >= 1 and <= 500_000 &&
        !string.IsNullOrWhiteSpace(options.TextExtractorPath) && Path.IsPathFullyQualified(options.TextExtractorPath);

    internal static async Task HandleAsync(HttpContext context, QpdfProcessor qpdf, RenderSlot slot, IOptions<PdfOptions> options)
    {
        // The workers share the renderer's 384 MiB DATA budget. Run them one at a time.
        using var lease = slot.TryAcquire();
        if (lease is null)
        {
            await Results.Problem(statusCode: 503, title: "PDF処理の同時実行上限に達しました。").ExecuteAsync(context);
            return;
        }
        var settings = options.Value;
        await PdfEndpointHelpers.ExecuteAsync(context, settings, null,
            () => PdfEndpointHelpers.ValidateQueryKeys(context.Request.Query),
            (files, token) => MultipartPdfUpload.ReadCompareFilesAsync(context.Request, files, settings, token),
            (files, token) => CompareAsync(files, qpdf, settings, token),
            maxRequestBytes: settings.MaxMergeRequestBytes, contentType: "application/json");
    }

    private static async Task CompareAsync(TemporaryPdfFiles files, QpdfProcessor qpdf, PdfOptions options, CancellationToken token)
    {
        var oldPath = files.MergeInputPath(1);
        var newPath = files.MergeInputPath(2);
        await qpdf.ValidateAsync(oldPath, token);
        await qpdf.ValidateAsync(newPath, token);
        var oldText = Path.Combine(files.DirectoryPath, "compare-old.json");
        var newText = Path.Combine(files.DirectoryPath, "compare-new.json");
        await RunWorkerAsync([oldPath, oldText], oldText, files, options, token);
        await RunWorkerAsync([newPath, newText], newText, files, options, token);
        await RunWorkerAsync(["--diff", oldText, newText, files.OutputPath], files.OutputPath, files, options, token);
    }

    private static async Task RunWorkerAsync(string[] args, string output, TemporaryPdfFiles files,
        PdfOptions options, CancellationToken token)
    {
        var request = ProcessMemoryLimits.CreateRequest(options.PrlimitPath, options.DotnetPath,
            [options.TextExtractorPath, .. args, options.MaxComparePages.ToString(CultureInfo.InvariantCulture),
                options.MaxCompareCharacters.ToString(CultureInfo.InvariantCulture)],
            WorkerMemory, WorkerFileLimit,
            new Dictionary<string, string> { ["DOTNET_GCHeapHardLimit"] = "0x10000000", ["DOTNET_EnableDiagnostics"] = "0" },
            memoryLimit: ProcessMemoryLimit.Data) with { WorkingDirectory = files.DirectoryPath };

        var result = await ExternalProcessRunner.RunAsync(request, token);
        if (result.ExitCode == 2) throw new PdfCompareException(PdfCompareReason.TextUnavailable);
        if (result.ExitCode is 3 or 137) throw new PdfCompareException(PdfCompareReason.TooLarge);
        if (result.ExitCode == 4) throw new PdfCompareException(PdfCompareReason.UnsupportedPdf);
        if (result.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException("PDF comparison worker failed.");
    }
}
