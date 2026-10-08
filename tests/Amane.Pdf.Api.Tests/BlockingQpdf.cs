using System.Diagnostics;

namespace Amane.Pdf.Api.Tests;

internal sealed class BlockingQpdf : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "amane-qpdf-block-" + Guid.NewGuid().ToString("N"));
    public string Executable => Path.Combine(Root, "qpdf.sh");
    public string[] Calls => File.ReadAllLines(Path.Combine(Root, "calls"));

    public BlockingQpdf(string blockPattern = "--job-json-file=*", double checkDelaySeconds = 0,
        string delayPattern = "--check")
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(Root);
        var delay = checkDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Executable, $"#!/bin/sh\n" +
            $"pause() {{\n  sleep \"$1\" &\n  child=$!\n  printf '%s %s\\n' \"$$\" \"$child\" > '{Root}/'\"$$\"'.pids'\n  wait \"$child\"\n}}\n" +
            $"printf '%s\\n' \"$1\" >> '{Root}/calls'\n" +
            (checkDelaySeconds > 0 ? $"case \"$1\" in\n{delayPattern}) pause {delay};;\nesac\n" : string.Empty) +
            $"case \"$1\" in\n{blockPattern}) pause 300;;\n*) exec qpdf \"$@\";;\nesac\n");
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
