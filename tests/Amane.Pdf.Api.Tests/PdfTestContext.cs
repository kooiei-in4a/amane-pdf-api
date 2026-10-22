using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        // Request-stage fault injection happens after the real startup self-test.
        // Startup failure tests use WebApplicationFactory directly.
        settings.Remove("Pdf:QpdfPath", out var requestQpdfPath);
        settings.Remove("Pdf:DjpegPath", out var requestDjpegPath);
        settings.Remove("Pdf:CjpegPath", out var requestCjpegPath);
        settings.Remove("Pdf:QpdfTimeoutSeconds", out var requestTimeout);
        settings["Pdf:TempRoot"] = TempRoot;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureLogging(logging => logging.AddProvider(new CaptureLoggerProvider(Logs)));
        });
        Client = Factory.CreateClient();
        if (requestQpdfPath is not null)
            Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = requestQpdfPath;
        if (requestDjpegPath is not null)
            Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.DjpegPath = requestDjpegPath;
        if (requestCjpegPath is not null)
            Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.CjpegPath = requestCjpegPath;
        // Short request budgets are fault injection too; production startup validates soft < hard.
        if (requestTimeout is not null)
            Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfTimeoutSeconds = int.Parse(requestTimeout,
                System.Globalization.CultureInfo.InvariantCulture);
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

    public async Task AssertRejectedBeforeReadingBodyAsync(string pathAndQuery, HttpContent content)
    {
        // Observe server reads, rather than client-side multipart serialization.
        using var body = new UnreadableBody();
        var uri = new Uri("http://localhost" + pathAndQuery);
        var response = await Factory.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = uri.AbsolutePath;
            context.Request.QueryString = new QueryString(uri.Query);
            context.Request.ContentType = content.Headers.ContentType!.ToString();
            context.Request.ContentLength = content.Headers.ContentLength;
            context.Request.Body = body;
        });
        Assert.AreEqual(0, body.ReadCount, pathAndQuery);
        Assert.AreEqual(400, response.Response.StatusCode, pathAndQuery);
        AssertClean();
    }

    private sealed class UnreadableBody : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("Request body must not be read.");
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("Request body must not be read.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static MultipartFormDataContent MergeForm(params byte[][] inputs)
    {
        var form = new MultipartFormDataContent();
        foreach (var input in inputs)
            form.Add(new ByteArrayContent(input), "file", "../../private-name.pdf");
        return form;
    }

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

    // Synthetic credentials and encrypted PDFs are generated only inside a private temporary directory.
    public async Task<byte[]> CreateEncryptedPdfAsync(string userPassword, string ownerPassword,
        string algorithm = "aes256", byte[]? input = null, string passwordMode = "unicode")
    {
        using var files = new TemporaryPdfFiles(Root);
        await using (var stream = TemporaryPdfFiles.CreatePrivateFile(files.InputPath))
        {
            await stream.WriteAsync(input ?? Fixture);
        }
        var (bits, settings) = algorithm switch
        {
            "aes256" => ("256bit", new Dictionary<string, object> { ["print"] = "none" }),
            "aes128" => ("128bit", new Dictionary<string, object> { ["useAes"] = "y", ["print"] = "none" }),
            "rc4-128" => ("128bit", new Dictionary<string, object> { ["useAes"] = "n", ["print"] = "none" }),
            "rc4-40" => ("40bit", new Dictionary<string, object> { ["print"] = "none" }),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
        var job = new Dictionary<string, object>
        {
            ["inputFile"] = files.InputPath,
            ["outputFile"] = files.OutputPath,
            ["objectStreams"] = "disable",
            ["passwordMode"] = passwordMode,
            ["encrypt"] = new Dictionary<string, object>
            {
                ["userPassword"] = userPassword,
                ["ownerPassword"] = ownerPassword,
                [bits] = settings
            }
        };
        if (algorithm.StartsWith("rc4", StringComparison.Ordinal)) job["allowWeakCrypto"] = "";
        Assert.AreEqual(0, await RunQpdfJobAsync(files.JobPath, job), "Encrypted fixture generation failed.");
        return await File.ReadAllBytesAsync(files.OutputPath);
    }

    public static async Task<int> RunQpdfJobAsync(string jobPath, Dictionary<string, object> job)
    {
        await using (var stream = TemporaryPdfFiles.CreatePrivateFile(jobPath))
        {
            await JsonSerializer.SerializeAsync(stream, job);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await ExternalProcessRunner.RunAsync(new("qpdf", ["--job-json-file=" + jobPath]), timeout.Token);
        return result.ExitCode;
    }

    public async Task<byte[]> CreateRotationMarkedPdfAsync(int pageCount)
    {
        if (pageCount is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var input = Path.Combine(Root, "marker-source.pdf");
        var output = Path.Combine(Root, $"{pageCount}-marked-pages.pdf");
        await File.WriteAllBytesAsync(input, await CreatePagedPdfAsync(pageCount));
        var arguments = Enumerable.Range(2, pageCount - 1)
            .Select(page => $"--rotate=+{((page - 1) % 4) * 90}:{page}")
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
        var result = await QpdfBytesAsync(arguments);
        return (result.ExitCode, Encoding.UTF8.GetString(result.Output));
    }

    public async Task<(int ExitCode, byte[] Output)> QpdfBytesAsync(params string[] arguments)
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
        using var output = new MemoryStream();
        var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
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
        await readOutput;
        return (process.ExitCode, output.ToArray());
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
