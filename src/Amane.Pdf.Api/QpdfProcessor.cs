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

    public async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(options.Value.QpdfPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = startInfo };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        // qpdf output may contain input data or paths. Drain it without storing or logging it.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
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
        return process.ExitCode;
    }
}
