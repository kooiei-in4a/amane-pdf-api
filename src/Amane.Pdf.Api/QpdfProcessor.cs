using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public sealed class QpdfProcessor(IOptions<PdfOptions> options)
{
    public Task ValidateAsync(TemporaryPdfFiles files, CancellationToken cancellationToken)
        => ValidateAsync(files.InputPath, cancellationToken);

    // Only API-generated paths inside a private job directory are supplied by endpoints.
    public async Task ValidateAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length == 0) throw new PdfInputException();

        // This inspection also detects encryption when the input password is unknown or empty.
        var encrypted = await RunAsync(["--is-encrypted", path], cancellationToken);
        if (encrypted is 0 or 3) throw new PdfInputException();
        if (encrypted != 2) throw new InvalidOperationException("PDF inspection failed.");

        var check = await RunAsync(["--check", path], cancellationToken);
        if (check is 2 or 3) throw new PdfInputException();
        if (check != 0) throw new InvalidOperationException("PDF inspection failed.");
    }

    public async Task ProtectAsync(TemporaryPdfFiles files, string password, CancellationToken cancellationToken)
    {
        var ownerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        while (ownerPassword == password)
        {
            ownerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }
        var job = new Dictionary<string, object>
        {
            ["inputFile"] = files.InputPath,
            ["outputFile"] = files.OutputPath,
            ["passwordMode"] = "unicode",
            ["encrypt"] = new Dictionary<string, object>
            {
                ["userPassword"] = password,
                ["ownerPassword"] = ownerPassword,
                ["256bit"] = new Dictionary<string, object>()
            }
        };
        await using (var stream = TemporaryPdfFiles.CreatePrivateFile(files.JobPath))
        {
            await JsonSerializer.SerializeAsync(stream, job, cancellationToken: cancellationToken);
        }
        var exitCode = await RunAsync(["--job-json-file=" + files.JobPath], cancellationToken);
        if (exitCode == 3) throw new PdfInputException();
        if (exitCode != 0)
        {
            throw new InvalidOperationException("PDF processing failed.");
        }
    }

    public async Task UnlockAsync(TemporaryPdfFiles files, string password, CancellationToken cancellationToken)
    {
        if (new FileInfo(files.InputPath).Length == 0) throw new PdfUnlockException(PdfUnlockReason.InvalidPdf);

        // Probe without the supplied password first, so owner-only restrictions are always rejected.
        var required = await RunAsync(["--requires-password", files.InputPath], cancellationToken);
        if (required == 3) throw new PdfUnlockException(PdfUnlockReason.NoOpenPassword);
        if (required == 2)
        {
            // Exit 2 also covers unreadable/damaged input; it does not prove lack of encryption.
            var plainCheck = await RunAsync(["--check", files.InputPath], cancellationToken);
            if (plainCheck == 0) throw new PdfUnlockException(PdfUnlockReason.NotEncrypted);
            if (plainCheck is 2 or 3) throw new PdfUnlockException(PdfUnlockReason.InvalidPdf);
            throw new InvalidOperationException("PDF inspection failed.");
        }
        if (required != 0) throw new InvalidOperationException("PDF inspection failed.");

        var authenticated = await RunUnlockJobAsync(files, password, files.JobPath, "requiresPassword", cancellationToken);
        if (authenticated == 0) throw new PdfUnlockException(PdfUnlockReason.WrongPassword);
        // Broken Root/Pages references can fail here, after the empty-password probe returned 0.
        if (authenticated == 2) throw new PdfUnlockException(PdfUnlockReason.InvalidPdf);
        if (authenticated != 3) throw new InvalidOperationException("PDF inspection failed.");

        var check = await RunUnlockJobAsync(files, password, files.UnlockCheckJobPath, "check", cancellationToken);
        if (check is 2 or 3) throw new PdfUnlockException(PdfUnlockReason.InvalidPdf);
        if (check != 0) throw new InvalidOperationException("PDF inspection failed.");

        var decrypted = await RunUnlockJobAsync(files, password, files.UnlockDecryptJobPath, "decrypt", cancellationToken);
        if (decrypted == 3) throw new PdfUnlockException(PdfUnlockReason.InvalidPdf);
        if (decrypted != 0) throw new InvalidOperationException("PDF processing failed.");

        try
        {
            await ValidateAsync(files.OutputPath, cancellationToken);
        }
        catch (PdfInputException)
        {
            // Input was already checked. Invalid/encrypted output is an internal failure, not a 422.
            throw new InvalidOperationException("PDF output validation failed.");
        }
    }

    private async Task<int> RunUnlockJobAsync(TemporaryPdfFiles files, string password, string jobPath,
        string operation, CancellationToken cancellationToken)
    {
        var job = new Dictionary<string, object>
        {
            ["inputFile"] = files.InputPath,
            ["password"] = password,
            ["passwordMode"] = "unicode",
            [operation] = ""
        };
        if (operation == "decrypt") job["outputFile"] = files.OutputPath;
        await using (var stream = TemporaryPdfFiles.CreatePrivateFile(jobPath))
        {
            await JsonSerializer.SerializeAsync(stream, job, cancellationToken: cancellationToken);
        }
        // In particular, --check stdout can contain passwords. RunAsync discards both output streams.
        return await RunAsync(["--job-json-file=" + jobPath], cancellationToken);
    }

    public Task<int> GetPageCountAsync(TemporaryPdfFiles files, CancellationToken cancellationToken)
        => GetPageCountAsync(files.InputPath, cancellationToken);

    internal async Task<int> GetPageCountAsync(string path, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunWithBoundedStdoutAsync(["--show-npages", path], 64, cancellationToken);
        if (exitCode == 3) throw new PdfInputException();
        if (exitCode != 0 || output is null) throw new InvalidOperationException("PDF page count failed.");

        var length = output.Length;
        if (length > 0 && output[length - 1] == (byte)'\n') length--;
        if (length > 0 && output[length - 1] == (byte)'\r') length--;
        if (length == 0) throw new InvalidOperationException("PDF page count failed.");

        var pageCount = 0;
        for (var i = 0; i < length; i++)
        {
            if (output[i] is < (byte)'0' or > (byte)'9') throw new InvalidOperationException("PDF page count failed.");
            try
            {
                pageCount = checked(pageCount * 10 + (output[i] - (byte)'0'));
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException("PDF page count failed.");
            }
        }
        if (pageCount <= 0) throw new InvalidOperationException("PDF page count failed.");
        return pageCount;
    }

    public async Task RotateAsync(TemporaryPdfFiles files, int angle, string? pageRange, CancellationToken cancellationToken)
    {
        if (angle is not (90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(angle));
        var rotate = $"--rotate=+{angle}" + (pageRange is null ? string.Empty : ":" + pageRange);
        var exitCode = await RunAsync([rotate, files.InputPath, files.OutputPath], cancellationToken);
        if (exitCode == 3) throw new PdfInputException();
        if (exitCode != 0) throw new InvalidOperationException("PDF processing failed.");
    }

    public async Task OptimizeAsync(TemporaryPdfFiles files, CancellationToken cancellationToken)
    {
        var exitCode = await RunAsync([
            "--object-streams=generate",
            "--recompress-flate",
            "--compression-level=9",
            files.InputPath,
            files.OutputPath
        ], cancellationToken);
        if (exitCode == 3) throw new PdfInputException();
        if (exitCode != 0) throw new InvalidOperationException("PDF processing failed.");
    }

    public async Task SelectPagesAsync(TemporaryPdfFiles files, string pageRange, CancellationToken cancellationToken)
    {
        var exitCode = await RunAsync([files.InputPath, "--pages", ".", pageRange, "--", files.OutputPath], cancellationToken);
        if (exitCode == 3) throw new PdfInputException();
        if (exitCode != 0) throw new InvalidOperationException("PDF processing failed.");
    }

    internal async Task SplitPartAsync(string inputPath, string pageRange, string outputPath,
        long partBudget, CancellationToken cancellationToken)
    {
        var request = ProcessMemoryLimits.CreateRequest(options.Value.PrlimitPath, options.Value.QpdfPath,
            [inputPath, "--pages", ".", pageRange, "--", outputPath], options.Value.QpdfAddressSpaceLimitBytes,
            checked(partBudget + 1), new Dictionary<string, string> { ["JPEGMEM"] = options.Value.QpdfJpegMemory });
        var result = await ExternalProcessRunner.RunAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode is 126 or 127) throw new InvalidOperationException("PDF processing failed.");
        var output = new FileInfo(outputPath);
        if (output.Exists && output.Length > partBudget) throw new PdfSplitOutputTooLargeException();
        if (result.ExitCode == 3) throw new PdfInputException();
        if (result.ExitCode != 0 || !output.Exists || output.Length == 0)
            throw new InvalidOperationException("PDF processing failed.");
        try { await ValidateAsync(outputPath, cancellationToken); }
        catch (PdfInputException) { throw new InvalidOperationException("PDF output validation failed."); }
    }

    public async Task MergeAsync(TemporaryPdfFiles files, IReadOnlyList<string> inputPaths, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "--empty", "--pages" };
        foreach (var path in inputPaths)
        {
            arguments.Add(path);
            arguments.Add("1-z");
        }
        arguments.Add("--");
        arguments.Add(files.OutputPath);
        var exitCode = await RunAsync([.. arguments], cancellationToken);
        if (exitCode == 3) throw new PdfInputException();
        if (exitCode != 0) throw new InvalidOperationException("PDF processing failed.");
    }

    private ExternalProcessRequest CreateRequest(string[] arguments, int? outputLimit = null)
        => ProcessMemoryLimits.CreateRequest(options.Value.PrlimitPath, options.Value.QpdfPath, arguments,
            options.Value.QpdfAddressSpaceLimitBytes, options.Value.QpdfJpegMemory, outputLimit);

    public async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await ExternalProcessRunner.RunAsync(CreateRequest(arguments), cancellationToken);
        return result.ExitCode;
    }

    internal async Task RunCleanWriteAsync(string[] arguments, string outputPath, long sizeLimit, CancellationToken token)
    {
        var request = ProcessMemoryLimits.CreateRequest(options.Value.PrlimitPath, options.Value.QpdfPath,
            arguments, options.Value.QpdfAddressSpaceLimitBytes, sizeLimit,
            new Dictionary<string, string> { ["JPEGMEM"] = options.Value.QpdfJpegMemory });
        var result = await ExternalProcessRunner.RunAsync(request, token);
        token.ThrowIfCancellationRequested();
        if (result.ExitCode is 126 or 127) throw new InvalidOperationException("PDF processing failed.");
        var output = new FileInfo(outputPath);
        // Ignored SIGXFSZ can leave a truncated file even with exit 0.
        if (output.Exists && output.Length >= sizeLimit) throw new PdfCleanTooComplexException();
        if (result.ExitCode != 0 || !output.Exists || output.Length == 0)
            throw new InvalidOperationException("PDF processing failed.");
    }

    private async Task<(int ExitCode, byte[]? Output)> RunWithBoundedStdoutAsync(string[] arguments, int outputLimit,
        CancellationToken cancellationToken)
    {
        var result = await ExternalProcessRunner.RunAsync(
            CreateRequest(arguments, outputLimit), cancellationToken);
        return (result.ExitCode, result.Stdout);
    }
}
