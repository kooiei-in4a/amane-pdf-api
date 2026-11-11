namespace Amane.Pdf.Api;

internal readonly record struct BoundedOutputResult(int ExitCode, long Length, bool ExceedsBudget)
{
    internal bool StartFailed => ExitCode is 126 or 127;
    internal bool InternalFailure => StartFailed || ExitCode is < 0 or >= 128;
    internal bool Succeeded => ExitCode == 0 && !ExceedsBudget && Length > 0;
}

internal static class BoundedOutput
{
    internal static long FileSizeLimit(long budget) => checked(budget + 1);

    internal static async Task<BoundedOutputResult> RunAsync(Func<long, ExternalProcessRequest> createRequest,
        string outputPath, long budget, CancellationToken token)
    {
        var result = await ExternalProcessRunner.RunAsync(createRequest(FileSizeLimit(budget)), token);
        token.ThrowIfCancellationRequested();
        var output = new FileInfo(outputPath);
        var length = output.Exists ? output.Length : 0;
        // A tool can return 0 after SIGXFSZ was ignored and its output was truncated.
        // Keep the size and exit classifications independent for caller-specific error precedence.
        return new(result.ExitCode, length, length > budget);
    }
}
