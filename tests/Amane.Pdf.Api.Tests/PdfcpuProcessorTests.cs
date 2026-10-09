using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfcpuProcessorTests
{
    [TestMethod]
    public async Task JapaneseText_IsPrivateJson_WithFixedEmbeddedFont_AndValidOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        const string text = "-日本語, \"引用\"\n次の行";
        await job.CreateAsync(new(text, Dy: 12));
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(job.Files.PdfcpuLayerJsonPath));
        var element = json.RootElement.GetProperty("pages").GetProperty("1").GetProperty("content").GetProperty("text")[0];
        Assert.AreEqual(text, element.GetProperty("value").GetString());
        Assert.AreEqual(PdfcpuProcessor.JapaneseFont, element.GetProperty("font").GetProperty("name").GetString());
        Assert.AreEqual((UnixFileMode)0x180, File.GetUnixFileMode(job.Files.PdfcpuLayerJsonPath));
        Assert.AreEqual((UnixFileMode)0x180, File.GetUnixFileMode(job.Files.OutputPath));
        Assert.AreEqual((UnixFileMode)0x1c0, File.GetUnixFileMode(job.Files.DirectoryPath));
        var metadata = await ExternalProcessRunner.RunAsync(new(job.Options.QpdfPath,
            ["--json=2", "--json-stream-data=none", "--decode-level=none", job.Files.OutputPath], StdoutLimit: 65536), CancellationToken.None);
        Assert.AreEqual(0, metadata.ExitCode);
        var output = Encoding.UTF8.GetString(metadata.Stdout!);
        StringAssert.Contains(output, "/FontFile2");
        StringAssert.Contains(output, "/ToUnicode");
        StringAssert.Contains(output, "+BIZUDPGothic-Regular");
    }

    [TestMethod]
    public async Task JsonBudget_ExactBytesSucceeds_OneLessFailsBeforePrivateFileOrProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        long bytes;
        using (var source = new Job())
        {
            await source.CreateAsync(new("日本語\n\"quoted\"", Dy: 12));
            bytes = new FileInfo(source.Files.PdfcpuLayerJsonPath).Length;
        }
        using var exact = new Job();
        await exact.CreateAsync(new("日本語\n\"quoted\"", Dy: 12), bytes);
        using var smaller = new Job();
        await Assert.ThrowsExactlyAsync<PdfcpuCapacityException>(() => smaller.CreateAsync(new("日本語\n\"quoted\"", Dy: 12), bytes - 1));
        Assert.IsFalse(File.Exists(smaller.Files.PdfcpuLayerJsonPath));
        Assert.IsFalse(File.Exists(smaller.Files.OutputPath));
    }

    [TestMethod]
    [DataRow("percent")]
    [DataRow("surrogate")]
    [DataRow("anchor")]
    [DataRow("anchor-and-position")]
    [DataRow("font-size")]
    [DataRow("nan")]
    [DataRow("color")]
    public async Task InvalidText_IsRejectedWithoutCallingPdfcpu(string invalid)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        var text = invalid switch
        {
            "percent" => new PdfcpuText("%p"),
            "surrogate" => new PdfcpuText("\ud800"),
            "anchor" => new PdfcpuText("text", Anchor: "file:/secret"),
            "anchor-and-position" => new PdfcpuText("text", X: 1),
            "font-size" => new PdfcpuText("text", FontSize: 0),
            "nan" => new PdfcpuText("text", Rotation: double.NaN),
            _ => new PdfcpuText("text", Color: "file:/secret")
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => job.CreateAsync(text));
        Assert.IsFalse(File.Exists(job.Files.PdfcpuLayerJsonPath));
        Assert.IsFalse(File.Exists(job.Files.OutputPath));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(126)]
    [DataRow(127)]
    public async Task NoOutput_IsFixedInternalFailure(int exit)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        job.Options.PdfcpuPath = job.Script($"exit {exit}");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => job.CreateAsync(new("秘密")));
        Assert.AreEqual("PDF描画処理に失敗しました。", error.Message);
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    public async Task RealPdfcpu_FsizeDeletesPartialOutput_AndRemainsInternalFailure()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => job.CreateAsync(new("日本語"), pdfBudget: 4096));
        Assert.AreEqual("PDF描画処理に失敗しました。", error.Message);
        Assert.IsFalse(File.Exists(job.Files.OutputPath));
    }

    [TestMethod]
    public async Task ObservedOutputOverflow_IsCapacityFailure_EvenWithExitZero()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        job.Options.PdfcpuPath = job.Script("head -c 8192 /dev/zero > \"$7\"\nexit 0");
        await Assert.ThrowsExactlyAsync<PdfcpuCapacityException>(() => job.CreateAsync(new("日本語"), pdfBudget: 4096));
        Assert.AreEqual(4097, new FileInfo(job.Files.OutputPath).Length);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("broken")]
    [DataRow("encrypted")]
    [DataRow("pages")]
    public async Task InvalidOrWrongPageOutput_IsNeverReturned(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        await job.BlankAsync();
        var candidate = Path.Combine(job.Files.DirectoryPath, "candidate.pdf");
        if (kind == "encrypted")
            Assert.AreEqual(0, (await ExternalProcessRunner.RunAsync(new(job.Options.QpdfPath,
                [job.Blank, "--encrypt", "synthetic-password", "synthetic-owner", "256", "--", candidate]), CancellationToken.None)).ExitCode);
        else if (kind == "pages")
            Assert.AreEqual(0, (await ExternalProcessRunner.RunAsync(new(job.Options.QpdfPath,
                ["--empty", "--pages", job.Blank, "1", job.Blank, "1", "--", candidate]), CancellationToken.None)).ExitCode);
        else await File.WriteAllTextAsync(candidate, kind == "empty" ? "" : "broken PDF");
        job.Options.PdfcpuPath = job.Script("cp " + Quote(candidate) + " \"$7\"");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => job.CreateAsync(new("日本語")));
    }

    [TestMethod]
    public async Task Request_AppliesRealLimitsAndEnvironment_AndKeepsTextOutOfArgv()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        var tool = job.Options.PdfcpuPath;
        var previous = new Dictionary<string, string?>();
        foreach (var (key, value) in new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "/must-not-use",
            ["PDFCPU_CONFIG_ROOT"] = "/must-not-use", ["GOGC"] = "1", ["GODEBUG"] = "gctrace=1",
            ["GOMAXPROCS"] = "42", ["GOTRACEBACK"] = "crash" })
        {
            previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        try
        {
            job.Options.PdfcpuPath = job.Script("[ \"$HOME\" = \"$PWD/pdfcpu-home\" ] || exit 9\n" +
                "[ \"$XDG_CONFIG_HOME\" = \"$HOME\" ] || exit 9\n" +
                "[ \"$GOMEMLIMIT\" = 200MiB ] && [ \"$GOGC\" = 100 ] && [ -z \"$GODEBUG\" ] || exit 9\n" +
                "[ \"$GOMAXPROCS\" = 1 ] && [ \"$GOTRACEBACK\" = none ] || exit 9\n" +
                "grep -Eq '^Max address space +1073741824 +1073741824 +bytes' /proc/$$/limits || exit 9\n" +
                "grep -Eq '^Max core file size +0 +0 +bytes' /proc/$$/limits || exit 9\n" +
                "grep -Eq '^Max file size +8388609 +8388609 +bytes' /proc/$$/limits || exit 9\n" +
                "[ \"$(stat -c %a \"$5\")\" = 600 ] || exit 9\n" +
                "printf '%s\\n' \"$@\" > \"$PWD/arguments.txt\"\nkill -XFSZ $$\nexec " + Quote(tool) + " \"$@\"");
            await job.CreateAsync(new("ARGV-SECRET-日本語", Dy: 12));
            var argv = await File.ReadAllTextAsync(Path.Combine(job.Files.DirectoryPath, "arguments.txt"));
            Assert.IsFalse(argv.Contains("ARGV-SECRET", StringComparison.Ordinal));
            StringAssert.StartsWith(argv, "-c\n" + job.Options.PdfcpuConfigDir + "\n--offline\ncreate\n");
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.Combine(job.Files.DirectoryPath, "pdfcpu-home")).Any());
            Assert.AreEqual("gctrace=1", Environment.GetEnvironmentVariable("GODEBUG"));
        }
        finally { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); }
    }

    [TestMethod]
    public async Task Cancellation_StopsProcessTree_AndDisposalRemovesJob()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var blocker = new BlockingQpdf("-c");
        var job = new Job();
        var directory = job.Files.DirectoryPath;
        try
        {
            job.Options.PdfcpuPath = blocker.Executable;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<OperationCanceledException>(() => job.CreateAsync(new("日本語"), token: timeout.Token));
            await blocker.AssertStoppedAsync();
        }
        finally { job.Dispose(); }
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    [DataRow("input")]
    [DataRow("outside")]
    [DataRow("existing")]
    public async Task Paths_RejectOriginalInputAndOutsideJob_AndPreserveExistingOutput(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        await job.BlankAsync();
        var blank = job.Blank;
        var output = job.Files.OutputPath;
        if (kind == "input")
        {
            File.Copy(blank, job.Files.InputPath);
            blank = Path.Combine(job.Files.DirectoryPath, ".", "input.pdf");
        }
        if (kind == "outside") output = Path.Combine(job.Root, "outside.pdf");
        if (kind == "existing") await File.WriteAllTextAsync(output, "existing-output");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new PdfcpuProcessor(Options.Create(job.Options)).CreateAsync(
            job.Files, new(new Dictionary<int, IReadOnlyList<PdfcpuText>> { [1] = [new("日本語")] }), blank, output,
            PdfcpuProcessor.LayerFileLimitBytes, PdfcpuProcessor.LayerFileLimitBytes, CancellationToken.None));
        Assert.IsFalse(File.Exists(job.Files.PdfcpuLayerJsonPath));
        if (kind == "existing") Assert.AreEqual("existing-output", await File.ReadAllTextAsync(output));
        else Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task Startup_AcceptsExistingRelativeTempRoot_AndDeletesItsJob()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        job.Options.TempRoot = Path.GetRelativePath(Environment.CurrentDirectory, job.Root);
        await PdfcpuProcessor.ValidateStartupAsync(job.Options, CancellationToken.None);
        Assert.AreEqual(1, Directory.GetDirectories(job.Root).Length);
    }

    [TestMethod]
    [DataRow("Pdf:PdfcpuPath", "")]
    [DataRow("Pdf:PdfcpuConfigDir", "relative")]
    [DataRow("Pdf:PdfcpuAddressSpaceLimitBytes", "0")]
    [DataRow("Pdf:PdfcpuMemoryLimit", "0MiB")]
    [DataRow("Pdf:PdfcpuMemoryLimit", "200M")]
    [DataRow("Pdf:PdfcpuMemoryLimit", "２００MiB")]
    [DataRow("Pdf:PdfcpuMemoryLimit", "9223372036854775807MiB")]
    [DataRow("Pdf:PdfcpuMemoryLimit", "1025MiB")]
    public void InvalidOptions_PreventStartup(string key, string value)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [TestMethod]
    [DataRow("binary")]
    [DataRow("config")]
    [DataRow("font")]
    [DataRow("as")]
    [DataRow("version")]
    [DataRow("version-overflow")]
    [DataRow("font-overflow")]
    [DataRow("draw")]
    public async Task StartupFailures_HaveDedicatedExceptionAndLog_WithoutInternalDetails(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        if (kind == "binary") job.Options.PdfcpuPath = "/missing/PDFCPU-INTERNAL-PATH";
        if (kind == "config") job.Options.PdfcpuConfigDir = "/missing/PDFCPU-INTERNAL-PATH";
        if (kind == "font")
        {
            job.Options.PdfcpuConfigDir = Path.Combine(job.Files.DirectoryPath, "config");
            Directory.CreateDirectory(Path.Combine(job.Options.PdfcpuConfigDir, "pdfcpu"));
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("Pdf__PdfcpuConfigDir")!, "pdfcpu", "config.yml"),
                Path.Combine(job.Options.PdfcpuConfigDir, "pdfcpu", "config.yml"));
        }
        if (kind == "as") { job.Options.PdfcpuAddressSpaceLimitBytes = 256 * 1024 * 1024; job.Options.PdfcpuMemoryLimit = "200MiB"; }
        if (kind == "version") job.Options.PdfcpuPath = job.Script("echo 'version: 0.0.0'\necho 'STDERR-SECRET' >&2");
        if (kind == "version-overflow") job.Options.PdfcpuPath = job.Script("head -c 4097 /dev/zero");
        if (kind == "font-overflow") job.Options.PdfcpuPath = job.Script("case \"$4\" in version) echo 'version: 0.16.1';; *) head -c 65537 /dev/zero;; esac");
        if (kind == "draw") job.Options.PdfcpuPath = job.Script("case \"$4\" in version) echo 'version: 0.16.1';; fonts) echo 'BIZUDPGothic-Regular (13932 glyphs)';; *) echo 'STDERR-SECRET' >&2; exit 1;; esac");
        var error = await Assert.ThrowsExactlyAsync<PdfcpuStartupException>(() => PdfcpuProcessor.ValidateStartupAsync(job.Options, CancellationToken.None));
        Assert.AreEqual("pdfcpu・日本語フォントの自己テストに失敗しました。", error.Message);
        Assert.IsNull(error.InnerException);
        Assert.AreEqual(1, Directory.GetDirectories(job.Root).Length); // only the caller's job
        var logs = new List<string>();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pdf:PdfcpuPath"] = job.Options.PdfcpuPath, ["Pdf:PdfcpuConfigDir"] = job.Options.PdfcpuConfigDir,
                ["Pdf:PdfcpuAddressSpaceLimitBytes"] = job.Options.PdfcpuAddressSpaceLimitBytes.ToString(CultureInfo.InvariantCulture),
                ["Pdf:PdfcpuMemoryLimit"] = job.Options.PdfcpuMemoryLimit, ["Pdf:TempRoot"] = job.Root
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(new Capture(logs)));
        });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.IsTrue(logs.Contains(error.Message));
        var all = string.Join('\n', logs);
        Assert.IsFalse(all.Contains("メモリ制限の自己テストに失敗", StringComparison.Ordinal));
        Assert.IsFalse(all.Contains("PDFCPU-INTERNAL-PATH", StringComparison.Ordinal));
        Assert.IsFalse(all.Contains("STDERR-SECRET", StringComparison.Ordinal));
        Assert.AreEqual(1, Directory.GetDirectories(job.Root).Length);
    }

    [TestMethod]
    public async Task Program_UsesOneFixedThirtySecondDeadline_AcrossBothSelfTests()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new Job();
        using var blocker = new BlockingQpdf("-c");
        var qpdf = job.Script("if [ \"$1\" = --version ]; then sleep 20; fi\nexec qpdf \"$@\"");
        var logs = new List<string>();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pdf:QpdfPath"] = qpdf, ["Pdf:QpdfTimeoutSeconds"] = "60",
                ["Pdf:PdfcpuPath"] = blocker.Executable, ["Pdf:TempRoot"] = job.Root
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(new Capture(logs)));
        });
        var watch = Stopwatch.StartNew();
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.IsTrue(watch.Elapsed > TimeSpan.FromSeconds(25) && watch.Elapsed < TimeSpan.FromSeconds(40));
        Assert.IsTrue(logs.Contains("pdfcpu・日本語フォントの自己テストに失敗しました。"));
        await blocker.AssertStoppedAsync();
        Assert.AreEqual(1, Directory.GetDirectories(job.Root).Length);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private sealed class Job : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pdfcpu-test-" + Guid.NewGuid().ToString("N"));
        internal PdfOptions Options { get; }
        internal TemporaryPdfFiles Files { get; }
        internal string Blank => Path.Combine(Files.DirectoryPath, "blank.pdf");
        internal Job()
        {
            Directory.CreateDirectory(Root);
            Options = new() { TempRoot = Root, PdfcpuPath = Environment.GetEnvironmentVariable("Pdf__PdfcpuPath") ?? "pdfcpu",
                PdfcpuConfigDir = Environment.GetEnvironmentVariable("Pdf__PdfcpuConfigDir") ?? "" };
            Files = new(Root);
        }
        internal async Task BlankAsync()
        {
            if (File.Exists(Blank)) return;
            await File.WriteAllTextAsync(Files.JobPath, PdfcpuProcessor.StartupBlankJson);
            Assert.AreEqual(0, (await ExternalProcessRunner.RunAsync(new(Options.QpdfPath,
                ["--json-input", Files.JobPath, Blank]), CancellationToken.None)).ExitCode);
        }
        internal async Task CreateAsync(PdfcpuText text, long jsonBudget = PdfcpuProcessor.LayerFileLimitBytes,
            long pdfBudget = PdfcpuProcessor.LayerFileLimitBytes, CancellationToken token = default)
        {
            await BlankAsync();
            await new PdfcpuProcessor(Microsoft.Extensions.Options.Options.Create(Options)).CreateAsync(Files,
                new(new Dictionary<int, IReadOnlyList<PdfcpuText>> { [1] = [text] }), Blank, Files.OutputPath, jsonBudget, pdfBudget, token);
        }
        internal string Script(string body)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            var path = Path.Combine(Root, "tool.sh");
            File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
        public void Dispose() { Files.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class Capture(List<string> logs) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (logs) logs.Add(formatter(state, exception) + (exception is null ? "" : exception.ToString())); }
    }
}
