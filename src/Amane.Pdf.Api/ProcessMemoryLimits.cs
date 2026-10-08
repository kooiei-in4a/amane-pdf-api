using System.Globalization;

namespace Amane.Pdf.Api;

// The same argument/environment construction can be used for djpeg/cjpeg.
internal static class ProcessMemoryLimits
{
    internal static bool IsValid(PdfOptions options)
        => !string.IsNullOrWhiteSpace(options.PrlimitPath) && options.QpdfAddressSpaceLimitBytes > 0 &&
            IsValidJpegMemory(options.QpdfJpegMemory);

    private static bool IsValidJpegMemory(string? value)
    {
        // libjpeg-turbo reads JPEGMEM through a 30-byte buffer.
        if (string.IsNullOrEmpty(value) || value.Length >= 30) return false;
        var digits = value.AsSpan();
        var multiplier = 1000L;
        if (digits[^1] is 'M' or 'm')
        {
            digits = digits[..^1];
            multiplier = 1_000_000;
        }
        if (digits.IsEmpty) return false;
        foreach (var digit in digits)
            if (digit is < '0' or > '9') return false;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number > 0 && number <= (OperatingSystem.IsWindows() || IntPtr.Size == 4 ? int.MaxValue : long.MaxValue) / multiplier;
    }

    internal static ExternalProcessRequest CreateRequest(string prlimitPath, string executablePath,
        IReadOnlyList<string> arguments, long addressSpaceLimitBytes, string jpegMemory, int? stdoutLimit = null)
    {
        var environment = new Dictionary<string, string> { ["JPEGMEM"] = jpegMemory };
        if (!OperatingSystem.IsLinux())
            return new(executablePath, arguments, Environment: environment, StdoutLimit: stdoutLimit);

        var limit = addressSpaceLimitBytes.ToString(CultureInfo.InvariantCulture);
        return new(prlimitPath, [$"--as={limit}:{limit}", "--", executablePath, .. arguments],
            Environment: environment, StdoutLimit: stdoutLimit);
    }

    internal static async Task ValidateStartupAsync(PdfOptions options, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.QpdfTimeoutSeconds));
        try
        {
            foreach (var (executable, arguments) in new (string, string[])[]
                { ("/bin/true", []), (options.QpdfPath, ["--version"]) })
            {
                var request = CreateRequest(options.PrlimitPath, executable, arguments,
                    options.QpdfAddressSpaceLimitBytes, options.QpdfJpegMemory);
                if ((await ExternalProcessRunner.RunAsync(request, timeout.Token)).ExitCode != 0)
                    throw new InvalidOperationException();
            }
        }
        catch (Exception)
        {
            // Do not retain process output, paths, or an inner exception in startup logs.
            throw new InvalidOperationException("PDF処理のメモリ制限の自己テストに失敗しました。");
        }
    }
}
