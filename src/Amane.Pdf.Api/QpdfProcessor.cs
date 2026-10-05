using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

public sealed class QpdfProcessor(IOptions<PdfOptions> options)
{
    public async Task ValidateAsync(TemporaryPdfFiles files, CancellationToken cancellationToken)
    {
        if (new FileInfo(files.InputPath).Length == 0) throw new PdfInputException();

        // This inspection also detects encryption when the input password is unknown or empty.
        var encrypted = await RunAsync(["--is-encrypted", files.InputPath], cancellationToken);
        if (encrypted is 0 or 3) throw new PdfInputException();
        if (encrypted != 2) throw new InvalidOperationException("PDF inspection failed.");

        var check = await RunAsync(["--check", files.InputPath], cancellationToken);
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

    public async Task<int> GetPageCountAsync(TemporaryPdfFiles files, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunWithBoundedStdoutAsync(["--show-npages", files.InputPath], 64, cancellationToken);
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
                pageCount = checked(pageCount * 10 + output[i] - (byte)'0');
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

    public async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var process = Start(arguments, cancellationToken);
        // qpdf output may contain input data or paths. Drain it without storing or logging it.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        await WaitForExitAsync(process, stdout, stderr, cancellationToken);
        return process.ExitCode;
    }

    private async Task<(int ExitCode, byte[]? Output)> RunWithBoundedStdoutAsync(string[] arguments, int outputLimit,
        CancellationToken cancellationToken)
    {
        using var process = Start(arguments, cancellationToken);
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, outputLimit);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        await WaitForExitAsync(process, stdout, stderr, cancellationToken);
        return (process.ExitCode, await stdout);
    }

    private Process Start(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(options.Value.QpdfPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo };
        cancellationToken.ThrowIfCancellationRequested();
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
                    // qpdf exited between the check and Kill.
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
