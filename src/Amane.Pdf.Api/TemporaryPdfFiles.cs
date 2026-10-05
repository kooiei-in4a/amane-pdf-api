using System.Security.Cryptography;

namespace Amane.Pdf.Api;

public sealed class TemporaryPdfFiles : IDisposable
{
    public string DirectoryPath { get; }
    public string InputPath => Path.Combine(DirectoryPath, "input.pdf");
    public string OutputPath => Path.Combine(DirectoryPath, "output.pdf");
    public string JobPath => Path.Combine(DirectoryPath, "job.json");

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
