using System.ComponentModel;
using System.Text;
using Amane.Pdf.Api;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class ExternalProcessRunnerTests
{
    [TestMethod]
    public async Task Arguments_ArePassedWithoutShellInterpretation()
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var script = CreateScript(files, "printf '%s\\0' \"$@\"");
        string[] arguments = ["a b", "$HOME", "; printf unexpected", "$(printf unexpected)",
            "`printf unexpected`", "--x=y", "日本語", "", "line1\nline2", "a\"b'c"];

        var result = await RunAsync(new(script, arguments, StdoutLimit: 1024));

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(result.Stdout);
        CollectionAssert.AreEqual(arguments.Append(string.Empty).ToArray(), Encoding.UTF8.GetString(result.Stdout).Split('\0'));
    }

    [TestMethod]
    public async Task Environment_AddsAndOverridesOnlySpecifiedVariables()
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var script = CreateScript(files, "printf '%s\\0%s' \"$PATH\" \"${AMANE_RUNNER_TEST_VALUE-unset}\"");
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        Assert.IsNotNull(inheritedPath);
        Assert.IsNull(Environment.GetEnvironmentVariable("AMANE_RUNNER_TEST_VALUE"));

        var inherited = await RunAsync(new(script, [], StdoutLimit: 16384));
        Assert.IsNotNull(inherited.Stdout);
        Assert.AreEqual(inheritedPath + "\0unset", Encoding.UTF8.GetString(inherited.Stdout));

        var added = await RunAsync(new(script, [],
            Environment: new Dictionary<string, string> { ["AMANE_RUNNER_TEST_VALUE"] = "日本語 value" }, StdoutLimit: 16384));
        Assert.IsNotNull(added.Stdout);
        Assert.AreEqual(inheritedPath + "\0日本語 value", Encoding.UTF8.GetString(added.Stdout));

        var overridden = await RunAsync(new(script, [], Environment: new Dictionary<string, string>
        {
            ["PATH"] = "/runner-test-path",
            ["AMANE_RUNNER_TEST_VALUE"] = "overridden"
        }, StdoutLimit: 16384));
        Assert.IsNotNull(overridden.Stdout);
        Assert.AreEqual("/runner-test-path\0overridden", Encoding.UTF8.GetString(overridden.Stdout));
        Assert.AreEqual(inheritedPath, Environment.GetEnvironmentVariable("PATH"));
        Assert.IsNull(Environment.GetEnvironmentVariable("AMANE_RUNNER_TEST_VALUE"));
    }

    [TestMethod]
    [DataRow("A=B")]
    [DataRow("")]
    [DataRow("a-b")]
    [DataRow("lowercase")]
    [DataRow("1KEY")]
    [DataRow("KEY\n")]
    [DataRow("日本語")]
    public async Task Environment_RejectsInvalidKeysBeforeStarting(string key)
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var marker = Path.Combine(files.DirectoryPath, "started");
        var script = CreateScript(files, "printf started > \"$1\"");

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(new(script, [marker],
            Environment: new Dictionary<string, string> { [key] = "environment-value-sentinel" })));

        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    public async Task WorkingDirectory_IsApplied()
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var script = CreateScript(files, "pwd\nprintf created > relative-output");

        var result = await RunAsync(new(script, [], WorkingDirectory: files.DirectoryPath, StdoutLimit: 1024));

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsNotNull(result.Stdout);
        Assert.AreEqual(files.DirectoryPath + "\n", Encoding.UTF8.GetString(result.Stdout));
        Assert.AreEqual("created", await File.ReadAllTextAsync(Path.Combine(files.DirectoryPath, "relative-output")));
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(0, 1)]
    [DataRow(64, 0)]
    [DataRow(64, 63)]
    [DataRow(64, 64)]
    [DataRow(64, 65)]
    [DataRow(64, 257)]
    public async Task StdoutLimit_ReturnsOnlyCompleteOutputWithinLimit(int limit, int outputLength)
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var script = CreateScript(files, "printf '%s' \"$1\"\nprintf stderr-sentinel >&2");
        var output = new string('x', outputLength);

        var result = await RunAsync(new(script, [output], StdoutLimit: limit));

        Assert.AreEqual(0, result.ExitCode);
        if (outputLength > limit) Assert.IsNull(result.Stdout);
        else
        {
            Assert.IsNotNull(result.Stdout);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(output), result.Stdout);
        }
    }

    [TestMethod]
    [DataRow(8)]
    [DataRow(9)]
    public async Task StdoutLimit_IsMeasuredInBytes(int limit)
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var script = CreateScript(files, "printf '%s' \"$1\"");
        var output = Encoding.UTF8.GetBytes("日本語");

        var result = await RunAsync(new(script, ["日本語"], StdoutLimit: limit));

        Assert.AreEqual(0, result.ExitCode);
        if (output.Length > limit) Assert.IsNull(result.Stdout);
        else
        {
            Assert.IsNotNull(result.Stdout);
            CollectionAssert.AreEqual(output, result.Stdout);
        }
    }

    [TestMethod]
    public async Task StdoutLimit_RejectsNegativeLimitBeforeStarting()
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var marker = Path.Combine(files.DirectoryPath, "started");
        var script = CreateScript(files, "printf started > \"$1\"");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RunAsync(new(script, [marker], StdoutLimit: -1)));

        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(17)]
    public async Task Run_ReturnsExitCodeAndDiscardsOutputByDefault(int exitCode)
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var script = CreateScript(files, "printf stdout-sentinel\nprintf stderr-sentinel >&2\nexit \"$1\"");

        var result = await RunAsync(new(script, [exitCode.ToString()]));

        Assert.AreEqual(exitCode, result.ExitCode);
        Assert.IsNull(result.Stdout);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LargeStdoutAndStderr_AreDrainedWithoutBlocking(bool captureStdout)
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var script = CreateScript(files, "head -c 262144 /dev/zero &\nhead -c 262144 /dev/zero >&2 &\nwait\nexit 17");

        var result = await RunAsync(new(script, [], StdoutLimit: captureStdout ? 64 : null));

        Assert.AreEqual(17, result.ExitCode);
        Assert.IsNull(result.Stdout);
    }

    [TestMethod]
    public async Task StartFailure_PropagatesWithoutCapturingOutput()
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());

        await Assert.ThrowsAsync<Win32Exception>(() => RunAsync(new(Path.Combine(files.DirectoryPath, "missing"), [])));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreCancelled_DoesNotStartOrAttemptToStartProcess(bool missingExecutable)
    {
        if (!RequireLinux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var marker = Path.Combine(files.DirectoryPath, "started");
        var script = CreateScript(files, "printf started > \"$1\"");
        var executable = missingExecutable ? Path.Combine(files.DirectoryPath, "missing") : script;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ExternalProcessRunner.RunAsync(new(executable, [marker]), cancellation.Token));

        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancellation_ThrowsAndStopsParentAndChild(bool captureStdout)
    {
        if (!RequireLinux()) return;
        using var blocker = new BlockingQpdf("*");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var job = ExternalProcessRunner.RunAsync(new(blocker.Executable, [], StdoutLimit: captureStdout ? 64 : null), cancellation.Token);
        try
        {
            await blocker.WaitForJobsAsync(1);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => job.WaitAsync(TimeSpan.FromSeconds(10)));
            await blocker.AssertStoppedAsync();
        }
        finally
        {
            cancellation.Cancel();
            try { await job.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await ExternalProcessRunner.RunAsync(request, timeout.Token);
    }

    private static string CreateScript(TemporaryPdfFiles files, string body)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var path = Path.Combine(files.DirectoryPath, "process.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static bool RequireLinux()
    {
        if (OperatingSystem.IsLinux()) return true;
        Assert.Inconclusive("Linuxのprocessとprocess treeを検証します。");
        return false;
    }
}
