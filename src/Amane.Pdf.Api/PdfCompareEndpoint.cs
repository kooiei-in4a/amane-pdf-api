using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal enum PdfCompareReason { TextUnavailable, TooLarge }
internal sealed class PdfCompareException(PdfCompareReason reason) : Exception
{
    internal PdfCompareReason Reason { get; } = reason;
}

internal static class PdfCompareEndpoint
{
    private const long ExtractorMemory = 384 * 1024 * 1024;
    private const long ExtractorFileLimit = 8 * 1024 * 1024; // Includes CoreCLR bootstrap and escaped UTF-16.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool IsValid(PdfOptions options) => options.MaxComparePages is >= 1 and <= 300 &&
        options.MaxCompareCharacters is >= 1 and <= 500_000 &&
        !string.IsNullOrWhiteSpace(options.TextExtractorPath) && Path.IsPathFullyQualified(options.TextExtractorPath);

    internal static Task HandleAsync(HttpContext context, QpdfProcessor qpdf, IOptions<PdfOptions> options)
    {
        var settings = options.Value;
        return PdfEndpointHelpers.ExecuteAsync(context, settings, null,
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
        var oldText = await ExtractAsync(files, oldPath, options, token);
        var newText = await ExtractAsync(files, newPath, options, token);
        var result = TextDiff.Compare(oldText, newText, token);
        await using var output = TemporaryPdfFiles.CreatePrivateFile(files.OutputPath);
        await JsonSerializer.SerializeAsync(output, result, JsonOptions, token);
    }

    private static async Task<CompareText> ExtractAsync(TemporaryPdfFiles files, string input, PdfOptions options, CancellationToken token)
    {
        var output = Path.GetFullPath(Path.Combine(files.DirectoryPath, "compare-text.json"));
        var request = ProcessMemoryLimits.CreateRequest(options.PrlimitPath, options.DotnetPath,
            [options.TextExtractorPath, Path.GetFullPath(input), output,
                options.MaxComparePages.ToString(CultureInfo.InvariantCulture),
                options.MaxCompareCharacters.ToString(CultureInfo.InvariantCulture)],
            ExtractorMemory, ExtractorFileLimit,
            new Dictionary<string, string> { ["DOTNET_GCHeapHardLimit"] = "0x10000000", ["DOTNET_EnableDiagnostics"] = "0" },
            memoryLimit: ProcessMemoryLimit.Data) with { WorkingDirectory = Path.GetFullPath(files.DirectoryPath) };
        try
        {
            var result = await ExternalProcessRunner.RunAsync(request, token);
            if (result.ExitCode == 2) throw new PdfCompareException(PdfCompareReason.TextUnavailable);
            if (result.ExitCode is 3 or 137) throw new PdfCompareException(PdfCompareReason.TooLarge);
            if (result.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException("PDF text extraction failed.");

            await using var stream = File.OpenRead(output);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var pages = json.RootElement.GetProperty("pages").EnumerateArray().ToArray();
            if (pages.Length > options.MaxComparePages) throw new PdfCompareException(PdfCompareReason.TooLarge);
            var text = new List<Rune>();
            var pageNumbers = new List<int>();
            for (var page = 0; page < pages.Length; page++)
            {
                token.ThrowIfCancellationRequested();
                foreach (var rune in (pages[page].GetString() ?? "").EnumerateRunes())
                {
                    if (text.Count >= options.MaxCompareCharacters) throw new PdfCompareException(PdfCompareReason.TooLarge);
                    text.Add(rune);
                    pageNumbers.Add(page + 1);
                }
            }
            if (text.Count == 0) throw new PdfCompareException(PdfCompareReason.TextUnavailable);
            return new([.. text], [.. pageNumbers], pages.Length);
        }
        finally { File.Delete(output); }
    }
}
