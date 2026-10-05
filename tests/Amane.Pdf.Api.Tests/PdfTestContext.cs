using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Amane.Pdf.Api.Tests;

internal sealed class PdfTestContext : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "amane-pdf-test-" + Guid.NewGuid().ToString("N"));
    public string TempRoot => Path.Combine(Root, "jobs");
    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }
    public List<string> Logs { get; } = [];
    public static byte[] Fixture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.pdf"));

    public PdfTestContext(Dictionary<string, string?>? settings = null)
    {
        Directory.CreateDirectory(Root);
        settings ??= [];
        settings["Pdf:TempRoot"] = TempRoot;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureLogging(logging => logging.AddProvider(new CaptureLoggerProvider(Logs)));
        });
        Client = Factory.CreateClient();
    }

    public static MultipartFormDataContent Form(byte[]? file = null, string? password = "test-password", string fileName = "sample.pdf")
    {
        var form = new MultipartFormDataContent();
        if (file is not null)
        {
            var content = new ByteArrayContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(content, "file", fileName);
        }
        if (password is not null)
        {
            form.Add(new StringContent(password), "password");
        }
        return form;
    }

    public static MultipartFormDataContent FileForm(byte[]? file = null, string fileName = "sample.pdf")
        => Form(file, password: null, fileName);

    public async Task<byte[]> CreatePagedPdfAsync(int pageCount)
    {
        var input = Path.Combine(Root, "page-source.pdf");
        var output = Path.Combine(Root, $"{pageCount}-pages.pdf");
        await File.WriteAllBytesAsync(input, Fixture);
        var arguments = new List<string> { "--empty", "--pages" };
        for (var i = 0; i < pageCount; i++)
        {
            arguments.Add(input);
            arguments.Add("1");
        }
        arguments.Add("--");
        arguments.Add(output);
        Assert.AreEqual(0, (await QpdfAsync([.. arguments])).ExitCode);
        return await File.ReadAllBytesAsync(output);
    }

    public async Task<byte[]> CreateRotationMarkedPdfAsync(int pageCount)
    {
        if (pageCount is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var input = Path.Combine(Root, "marker-source.pdf");
        var output = Path.Combine(Root, $"{pageCount}-marked-pages.pdf");
        await File.WriteAllBytesAsync(input, await CreatePagedPdfAsync(pageCount));
        var arguments = Enumerable.Range(2, pageCount - 1)
            .Select(page => $"--rotate=+{(page - 1) * 90}:{page}")
            .Append(input)
            .Append(output)
            .ToArray();
        Assert.AreEqual(0, (await QpdfAsync(arguments)).ExitCode);
        return await File.ReadAllBytesAsync(output);
    }

    public async Task AssertValidPdfAsync(byte[] pdf, int expectedPageCount)
    {
        var path = Path.Combine(Root, "validation-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, pdf);
        Assert.AreEqual(0, (await QpdfAsync("--check", path)).ExitCode);
        var count = await QpdfAsync("--show-npages", path);
        Assert.AreEqual(0, count.ExitCode);
        Assert.AreEqual(expectedPageCount, int.Parse(count.Output.Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<int[]> ReadPageRotationsAsync(byte[] pdf)
    {
        var path = Path.Combine(Root, "rotation-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, pdf);
        var result = await QpdfAsync("--json", path);
        Assert.AreEqual(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var objects = document.RootElement.GetProperty("qpdf")[1];
        return [.. document.RootElement.GetProperty("pages").EnumerateArray().Select(page =>
        {
            var key = "obj:" + page.GetProperty("object").GetString();
            var value = objects.GetProperty(key).GetProperty("value");
            return value.TryGetProperty("/Rotate", out var rotation) ? rotation.GetInt32() : 0;
        })];
    }

    public void AssertClean() => Assert.IsFalse(Directory.Exists(TempRoot) && Directory.EnumerateFileSystemEntries(TempRoot).Any());

    public async Task<(int ExitCode, string Output)> QpdfAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("qpdf")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        await error;
        return (process.ExitCode, await output);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        Directory.Delete(Root, recursive: true);
    }

    private sealed class CaptureLoggerProvider(List<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(logs);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(List<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (logs) logs.Add(formatter(state, exception));
        }
    }
}
