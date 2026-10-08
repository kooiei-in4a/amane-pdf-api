using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Amane.Pdf.Api;

internal sealed record ExternalProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    int? StdoutLimit = null);

internal sealed record ExternalProcessResult(int ExitCode, byte[]? Stdout);

// Only foreground commands whose parent waits for its children are supported.
// A parent that exits first may leave children holding the redirected pipes; draining
// those pipes is not cancellable, and Kill(entireProcessTree: true) cannot find orphans.
// Managing detached/background children or process groups is outside this runner's scope.
internal static class ExternalProcessRunner
{
    public static async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken)
    {
        if (request.StdoutLimit is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "StdoutLimit must not be negative.");

        using var process = Start(request, cancellationToken);
        // Output may contain input data or paths. Do not log it or retain stderr.
        // Without a limit, stdout is discarded. If the limit is exceeded, it is
        // drained to EOF and returns null, preserving the existing qpdf behavior.
        Task<byte[]?>? capturedStdout = request.StdoutLimit is int limit
            ? ReadBoundedAsync(process.StandardOutput.BaseStream, limit)
            : null;
        Task stdout = capturedStdout is null
            ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null)
            : capturedStdout;
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        await WaitForExitAsync(process, stdout, stderr, cancellationToken);
        return new(process.ExitCode, capturedStdout is null ? null : await capturedStdout);
    }

    private static Process Start(ExternalProcessRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(request.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        if (request.WorkingDirectory is not null) startInfo.WorkingDirectory = request.WorkingDirectory;
        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                if (!Regex.IsMatch(key, @"\A[A-Z_][A-Z0-9_]*\z", RegexOptions.CultureInvariant))
                    throw new ArgumentException("Invalid environment variable key.", nameof(request));
                startInfo.Environment[key] = value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task WaitForExitAsync(Process process, Task stdout, Task stderr, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between the check and Kill.
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit)
    {
        var output = new byte[limit];
        var stored = 0;
        var exceeded = false;
        var buffer = new byte[64];
        int read;
        while ((read = await stream.ReadAsync(buffer)) != 0)
        {
            var copied = Math.Min(read, output.Length - stored);
            if (copied > 0)
            {
                buffer.AsSpan(0, copied).CopyTo(output.AsSpan(stored));
                stored += copied;
            }
            if (copied < read) exceeded = true;
        }
        return exceeded ? null : output[..stored];
    }
}
