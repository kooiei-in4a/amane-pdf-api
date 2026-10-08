using System.Security.Cryptography;

namespace Amane.Pdf.Api;

public sealed class TemporaryPdfFiles : IDisposable
{
    public string DirectoryPath { get; }
    public string InputPath => Path.Combine(DirectoryPath, "input.pdf");
    public string OutputPath => Path.Combine(DirectoryPath, "output.pdf");
    public string JobPath => Path.Combine(DirectoryPath, "job.json");
    internal string UnlockCheckJobPath => Path.Combine(DirectoryPath, "unlock-check.json");
    internal string UnlockDecryptJobPath => Path.Combine(DirectoryPath, "unlock-decrypt.json");

    internal string SplitPartPath(int index)
    {
        if (index is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(index));
        return Path.Combine(DirectoryPath, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"part-{index:D3}.pdf"));
    }

    public string MergeInputPath(int index)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
        return Path.Combine(DirectoryPath, $"input-{index:D4}.pdf");
    }

    public TemporaryPdfFiles(string root)
    {
        DirectoryPath = Path.Combine(root, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        else
        {
            Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(path, options);
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
