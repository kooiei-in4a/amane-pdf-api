using System.Diagnostics;

namespace Amane.Pdf.Api.Tests;

internal sealed class BlockingQpdf : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "amane-qpdf-block-" + Guid.NewGuid().ToString("N"));
    public string Executable => Path.Combine(Root, "qpdf.sh");

    public BlockingQpdf(string blockPattern = "--job-json-file=*")
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(Root);
        File.WriteAllText(Executable, $"#!/bin/sh\ncase \"$1\" in\n{blockPattern})\n  sleep 300 &\n  child=$!\n  printf '%s %s\\n' \"$$\" \"$child\" > '{Root}/'\"$$\"'.pids'\n  wait \"$child\"\n  ;;\n*) exec qpdf \"$@\";;\nesac\n");
        File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async Task WaitForJobsAsync(int count)
    {
        var deadline = Stopwatch.StartNew();
        while (Directory.GetFiles(Root, "*.pids").Length < count)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("qpdf test process did not start.");
            await Task.Delay(20);
        }
    }

    public async Task AssertStoppedAsync()
    {
        var deadline = Stopwatch.StartNew();
        while (ProcessIds().Any(IsRunning))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail("qpdf or its child process is still running.");
            await Task.Delay(20);
        }
    }

    private IEnumerable<int> ProcessIds() => Directory.GetFiles(Root, "*.pids")
        .SelectMany(path => File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(int.Parse);

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var pid in ProcessIds())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        Directory.Delete(Root, recursive: true);
    }
}
