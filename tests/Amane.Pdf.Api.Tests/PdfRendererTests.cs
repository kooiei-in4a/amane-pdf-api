using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfRendererTests
{
    [TestMethod]
    public void Slot_HasOneImmediatePermit_AndLeaseCanOnlyReleaseOnce()
    {
        using var slot = new RenderSlot();
        var first = slot.TryAcquire();
        Assert.IsNotNull(first);
        Assert.IsNull(slot.TryAcquire());
        first.Dispose(); first.Dispose();
        using var second = slot.TryAcquire();
        Assert.IsNotNull(second);
        Assert.IsNull(slot.TryAcquire());
    }

    [TestMethod]
    [DataRow(3, "UnsupportedPdf")]
    [DataRow(4, "UnsupportedPdf")]
    [DataRow(12, "UnsupportedPdf")]
    [DataRow(5, "TooComplex")]
    [DataRow(10, "OutputTooLarge")]
    public void KnownExitCodes_HaveReasonsForRedact(int code, string reason)
    {
        var exception = Assert.ThrowsExactly<PdfRenderException>(() => PdfRenderer.CheckExitCode(code));
        Assert.AreEqual(reason, exception.Reason.ToString());
        Assert.IsNull(exception.InnerException);
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(6)] [DataRow(20)] [DataRow(126)] [DataRow(127)]
    [DataRow(137)] [DataRow(139)] [DataRow(-1)]
    public void OtherExitCodes_AreInternalFailures(int code)
        => Assert.ThrowsExactly<InvalidOperationException>(() => PdfRenderer.CheckExitCode(code));

    [TestMethod]
    [DataRow(0)] [DataRow(90)] [DataRow(180)] [DataRow(270)]
    [DataRow(90, true)] [DataRow(0, false, true)]
    public async Task RealRenderer_RotationCropIntersectionUserUnit_AndPixelFills(int rotation,
        bool userUnit = false, bool outside = false)
    {
        if (!PdfCompressTests.Linux()) return;
        using var job = new RendererJob();
        var (left, bottom, right, top) = userUnit ? (0, 0, 300, 400)
            : outside ? (0, 0, 300, 400) : (50, 100, 550, 700);
        var attributes = userUnit ? "/MediaBox [0 0 300 400] /UserUnit 2"
            : outside ? "/MediaBox [0 0 600 800] /CropBox [-50 -50 300 400]"
            : "/MediaBox [0 0 600 800] /CropBox [50 100 550 700]";
        var content = $"0.8 g {left} {bottom} {right-left} {top-bottom} re f " +
            $"1 0 0 rg {left} {bottom} 50 50 re f 0 1 0 rg {right-50} {bottom} 50 50 re f " +
            $"0 0 1 rg {left} {top-50} 50 50 re f 1 1 0 rg {right-50} {top-50} 50 50 re f";
        await job.Input(Pdf(attributes + $" /Rotate {rotation}", content));
        var rotated = rotation is 90 or 270;
        var w = rotated ? top - bottom : right - left;
        var h = rotated ? right - left : top - bottom;
        var page = job.Page with { ExpectedWidth = w, ExpectedHeight = h, Width = w * 2, Height = h * 2,
            Fills = [new(w * 2 * 4 / 10, h * 2 * 3 / 10, w * 2 * 6 / 10, h * 2 * 4 / 10)] };
        await job.Renderer.RenderJpegAsync(job.Files, page, CancellationToken.None);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(page.OutputPath));
        var image = await Decode(page.OutputPath, page.Width, page.Height);
        int[][] colors = [[0, 0, 255], [255, 255, 0], [255, 0, 0], [0, 255, 0]]; // TL,TR,BL,BR at rotation 0
        var order = rotation switch { 90 => new[] { 2, 0, 3, 1 }, 180 => [3, 2, 1, 0], 270 => [1, 3, 0, 2], _ => [0, 1, 2, 3] };
        var corners = new[] { (15, 15), (page.Width - 16, 15), (15, page.Height - 16), (page.Width - 16, page.Height - 16) };
        for (var i = 0; i < corners.Length; i++) Pixel(image, page.Width, corners[i].Item1, corners[i].Item2, colors[order[i]], 8);
        var fill = page.Fills[0];
        // Every interior pixel, plus all four outer edges: JPEG can affect boundary pixels.
        for (var y = fill.Top + 2; y < fill.Bottom - 2; y++)
            for (var x = fill.Left + 2; x < fill.Right - 2; x++) Pixel(image, page.Width, x, y, [0, 0, 0], 3);
        var cx = (fill.Left + fill.Right) / 2; var cy = (fill.Top + fill.Bottom) / 2;
        foreach (var (x, y) in new[] { (fill.Left - 3, cy), (fill.Right + 2, cy), (cx, fill.Top - 3), (cx, fill.Bottom + 2) })
            Pixel(image, page.Width, x, y, [204, 204, 204], 5);
    }

    [TestMethod]
    public async Task RealRenderer_SizeMismatchOverflowAndExistingOutput_DoNotWrite()
    {
        if (!PdfCompressTests.Linux()) return;
        using var job = new RendererJob();
        await job.Input(Pdf());
        foreach (var (page, expected) in new[]
        {
            (job.Page with { ExpectedWidth = 100.02 }, PdfRenderReason.UnsupportedPdf),
            (job.Page with { MaxBytes = 1 }, PdfRenderReason.OutputTooLarge),
            (job.Page with { Page = 2 }, PdfRenderReason.UnsupportedPdf)
        })
        {
            var exception = await Assert.ThrowsExactlyAsync<PdfRenderException>(() => job.Renderer.RenderJpegAsync(job.Files, page, CancellationToken.None));
            Assert.AreEqual(expected, exception.Reason);
            Assert.IsFalse(File.Exists(page.OutputPath));
        }
        // The 0.01pt tolerance permits small numeric differences.
        await job.Renderer.RenderJpegAsync(job.Files, job.Page with { ExpectedWidth = 100.005 }, CancellationToken.None);
        var original = await File.ReadAllBytesAsync(job.Page.OutputPath);
        File.Delete(job.Page.OutputPath);
        await job.Renderer.RenderJpegAsync(job.Files, job.Page with
            { ExpectedWidth = 100.01, MaxBytes = original.Length }, CancellationToken.None);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(job.Page.OutputPath));
        File.Delete(job.Page.OutputPath);
        var overflow = await Assert.ThrowsExactlyAsync<PdfRenderException>(() => job.Renderer.RenderJpegAsync(
            job.Files, job.Page with { MaxBytes = original.Length - 1 }, CancellationToken.None));
        Assert.AreEqual(PdfRenderReason.OutputTooLarge, overflow.Reason);
        Assert.IsFalse(File.Exists(job.Page.OutputPath));
        await job.Renderer.RenderJpegAsync(job.Files, job.Page, CancellationToken.None);
        var result = await job.Raw();
        Assert.AreEqual(2, result);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(job.Page.OutputPath));
    }

    [TestMethod]
    public async Task RealRenderer_NativeFailures_ReturnDocumentPageBitmapAndJpegCodes()
    {
        if (!PdfCompressTests.Linux()) return;
        using var job = new RendererJob();
        await job.Input("not a PDF"u8.ToArray());
        Assert.AreEqual(3, await job.Raw());
        await job.Input(Pdf());
        Assert.AreEqual(4, await job.Raw(["--page", "2"]));
        Assert.AreEqual(12, await job.Raw(["--expect-size", "101,100"]));
        // JPEG dimensions exceed libjpeg's supported range, with a small legal bitmap.
        Assert.AreEqual(6, await job.Raw(["--width", "65536", "--height", "1"]));
        job.Settings.RenderDataLimitBytes = 64 * 1048576;
        Assert.AreEqual(5, await job.Raw(["--width", "4000", "--height", "4000"]));
        job.Settings.RenderDataLimitBytes = 384 * 1048576;
        Assert.AreEqual(0, await job.Raw(["--width", "4000", "--height", "4000"]));
    }

    [TestMethod]
    [DataRow("--page", "0")] [DataRow("--page", "2147483648")]
    [DataRow("--width", "0")] [DataRow("--width", "16000001")]
    [DataRow("--height", "-1")] [DataRow("--quality", "84")]
    [DataRow("--input", "relative.pdf")] [DataRow("--output", "relative.jpg")]
    [DataRow("--pdfium", "libpdfium.so")] [DataRow("--font-dir", "fonts")]
    [DataRow("--expect-size", "NaN,100")] [DataRow("--expect-size", "100,Infinity")]
    [DataRow("--max-bytes", "0")] [DataRow("--max-bytes", "9223372036854775807")]
    [DataRow("--fill", "0,0,101,100")] [DataRow("--fill", "0,0,0,1")]
    [DataRow("--fill", "0,-1,1,1")] [DataRow("--unknown", "1")]
    public async Task RealRenderer_InvalidArguments_AreSilentAndLeaveNoFile(string option, string value)
    {
        if (!PdfCompressTests.Linux()) return;
        using var job = new RendererJob();
        await job.Input(Pdf());
        Assert.AreEqual(2, await job.Raw([option, value]));
        Assert.IsFalse(File.Exists(job.Page.OutputPath));
    }

    [TestMethod]
    public async Task RealRenderer_DuplicateMissingAndExcessFills_AreRejected()
    {
        if (!PdfCompressTests.Linux()) return;
        using var job = new RendererJob();
        await job.Input(Pdf());
        Assert.AreEqual(2, await job.Raw(extra: ["--quality", "85"]));
        Assert.AreEqual(2, await job.Raw(extra: ["--fill"]));
        Assert.AreEqual(2, await job.Raw(extra: Enumerable.Range(0, 101).SelectMany(_ => new[] { "--fill", "0,0,1,1" }).ToArray()));
        Assert.AreEqual(0, await job.Raw(extra: Enumerable.Range(0, 100).SelectMany(_ => new[] { "--fill", "0,0,1,1" }).ToArray()));
    }

    [TestMethod]
    public async Task SelfTest_RequiresJapaneseFont_AndCreatesNoFiles()
    {
        if (!PdfCompressTests.Linux()) return;
        using var job = new RendererJob();
        Assert.AreEqual(0, await job.Raw(selfTest: true));
        var empty = Path.Combine(job.Root, "empty-fonts"); Directory.CreateDirectory(empty);
        job.Settings.RenderFontDir = empty;
        Assert.AreEqual(20, await job.Raw(selfTest: true));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(job.Files.DirectoryPath).Any());
        var exception = await Assert.ThrowsExactlyAsync<PdfRendererStartupException>(() => PdfRenderer.ValidateStartupAsync(job.Settings, CancellationToken.None));
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(job.Root, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task CancelAndTimeout_KillRealRenderer_WithExactLimits_AndRemoveTemporaryFiles(bool timeout)
    {
        if (!PdfCompressTests.Linux()) return;
        using var job = new RendererJob();
        var fifo = await ExternalProcessRunner.RunAsync(new("mkfifo", [job.Files.InputPath]), CancellationToken.None);
        Assert.AreEqual(0, fifo.ExitCode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var render = job.Renderer.RenderJpegAsync(job.Files, job.Page with { MaxBytes = 1_000_000 }, deadline.Token);
        var pid = 0;
        for (var attempt = 0; attempt < 200 && pid == 0; attempt++)
        {
            foreach (var path in Directory.EnumerateDirectories("/proc").Where(p => int.TryParse(Path.GetFileName(p), out _)))
            {
                try
                {
                    var cmd = await File.ReadAllTextAsync(Path.Combine(path, "cmdline"));
                    var limits = await File.ReadAllTextAsync(Path.Combine(path, "limits"));
                    if (!cmd.Contains("Amane.Pdf.Renderer.dll\0render-jpeg", StringComparison.Ordinal) ||
                        !cmd.Contains(job.Files.InputPath, StringComparison.Ordinal) ||
                        !Regex.IsMatch(limits, @"Max file size +1000001 +1000001 +bytes")) continue;
                    Assert.IsTrue(Regex.IsMatch(limits, @"Max data size +402653184 +402653184 +bytes"));
                    Assert.IsTrue(Regex.IsMatch(limits, @"Max core file size +0 +0 +bytes"));
                    Assert.IsTrue(Regex.IsMatch(limits, @"Max address space +unlimited +unlimited"));
                    var environment = await File.ReadAllTextAsync(Path.Combine(path, "environ"));
                    Assert.IsTrue(environment.Contains("DOTNET_EnableDiagnostics=0\0", StringComparison.Ordinal));
                    pid = int.Parse(Path.GetFileName(path), CultureInfo.InvariantCulture);
                    break;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (pid == 0) await Task.Delay(10);
        }
        try
        {
            Assert.AreNotEqual(0, pid, "The real renderer must reach PDF loading with its exact limits.");
            if (timeout) deadline.CancelAfter(50); else deadline.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => render);
            Assert.IsFalse(Directory.Exists($"/proc/{pid}"));
            Assert.IsFalse(File.Exists(job.Page.OutputPath));
        }
        finally
        {
            deadline.Cancel();
            try { await render; } catch (OperationCanceledException) { }
        }
        job.Files.Dispose();
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(job.Root).Any());
    }

    [TestMethod]
    public void EmptyFontDirectory_PreventsApiStartup_WithOnlyFixedRendererLog()
    {
        if (!PdfCompressTests.Linux()) return;
        using var job = new RendererJob();
        var logs = new List<string>();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Pdf:RenderFontDir"] = job.Files.DirectoryPath, ["Pdf:TempRoot"] = job.Root }));
            builder.ConfigureLogging(logging => logging.AddProvider(new RendererLogger(logs)));
        });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        CollectionAssert.Contains(logs, "描画プログラム（PDFium）の自己テストに失敗しました。");
        Assert.IsFalse(logs.Any(log => log.Contains(job.Root, StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("Pdf:RenderDataLimitBytes", "0")] [DataRow("Pdf:RenderDataLimitBytes", "402653185")]
    [DataRow("Pdf:DotnetPath", "dotnet")] [DataRow("Pdf:RendererPath", "renderer.dll")]
    [DataRow("Pdf:PdfiumPath", "libpdfium.so")] [DataRow("Pdf:RenderFontDir", "")]
    public void InvalidSettings_PreventStartup(string key, string value)
    {
        if (!PdfCompressTests.Linux()) return;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    private static byte[] Pdf(string attributes = "/MediaBox [0 0 100 100]", string content = "0.8 g 0 0 100 100 re f")
        => CompressFixtures.Objects([
            CompressFixtures.Text("<< /Type /Catalog /Pages 2 0 R >>"),
            CompressFixtures.Text("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            CompressFixtures.Text($"<< /Type /Page /Parent 2 0 R {attributes} /Resources << >> /Contents 4 0 R >>"),
            CompressFixtures.Stream("", CompressFixtures.Text(content))]);

    private static async Task<byte[]> Decode(string jpeg, int width, int height)
    {
        var result = await ExternalProcessRunner.RunAsync(new("djpeg", ["-pnm", "-rgb", jpeg], StdoutLimit: width * height * 3 + 100), CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode); Assert.IsNotNull(result.Stdout);
        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        Assert.IsTrue(result.Stdout.AsSpan().StartsWith(header));
        return result.Stdout[header.Length..];
    }

    private static void Pixel(byte[] image, int width, int x, int y, int[] expected, int tolerance)
    {
        for (var channel = 0; channel < 3; channel++)
            Assert.IsTrue(Math.Abs(image[(y * width + x) * 3 + channel] - expected[channel]) <= tolerance,
                $"Unexpected pixel ({x},{y}), channel {channel}.");
    }

    private sealed class RendererLogger(List<string> logs) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string category) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (logs) logs.Add(formatter(state, exception)); }
    }

    private sealed class RendererJob : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "renderer-test-" + Guid.NewGuid().ToString("N"));
        internal TemporaryPdfFiles Files { get; }
        internal PdfOptions Settings { get; }
        internal PdfRenderer Renderer => new(Options.Create(Settings));
        internal RenderPage Page => new(Files.InputPath, Path.Combine(Files.DirectoryPath, "rendered.jpg"), 1, 100, 100, 100, 100, 10 * 1048576, []);
        internal RendererJob()
        {
            Files = new(Root);
            Settings = new ConfigurationBuilder().AddEnvironmentVariables().Build().GetSection("Pdf").Get<PdfOptions>() ?? new();
            Settings.TempRoot = Root;
        }
        internal Task Input(byte[] input) => File.WriteAllBytesAsync(Files.InputPath, input);
        internal async Task<int> Raw(string[]? replacements = null, string[]? extra = null, bool selfTest = false)
        {
            var arguments = selfTest ? new List<string> { "self-test" } : new List<string>
            {
                "render-jpeg", "--input", Page.InputPath, "--page", "1", "--expect-size", "100,100",
                "--width", "100", "--height", "100", "--quality", "85", "--max-bytes", "10485760", "--output", Page.OutputPath
            };
            var request = Renderer.CreateRequest(Files.DirectoryPath, arguments, 10485760);
            var all = request.Arguments.ToList();
            if (replacements is not null)
                for (var i = 0; i < replacements.Length; i += 2)
                {
                    var index = all.IndexOf(replacements[i]);
                    if (index >= 0) all[index + 1] = replacements[i + 1];
                    else all.AddRange([replacements[i], replacements[i + 1]]);
                }
            if (extra is not null) all.AddRange(extra);
            // Unlike the production runner, explicitly assert both streams are empty.
            using var process = new Process { StartInfo = new ProcessStartInfo(request.ExecutablePath)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Files.DirectoryPath } };
            foreach (var argument in all) process.StartInfo.ArgumentList.Add(argument);
            foreach (var pair in request.Environment!) process.StartInfo.Environment[pair.Key] = pair.Value;
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            Assert.AreEqual("", await stdout); Assert.AreEqual("", await stderr);
            return process.ExitCode;
        }
        public void Dispose()
        {
            if (Directory.Exists(Files.DirectoryPath)) Files.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
