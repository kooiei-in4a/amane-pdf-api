using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfProcessingDiagnosticsTests
{
    private const string Secret = "PRIVATE_SENTINEL_password_filename_pdf_exception";

    [TestMethod]
    public async Task RealPdfValidation_RecordsInputAndOutput_AndPreservesBytesAndCleanup()
    {
        using var test = new Harness("/api/pdf/optimize");
        var qpdf = new QpdfProcessor(Options.Create(test.Options));
        await test.Run(async (files, token) =>
        {
            await qpdf.ValidateAsync(files, token);
            await qpdf.OptimizeAsync(files, token);
            await qpdf.ValidateOutputAsync(files.OutputPath, token);
        });
        Assert.AreEqual(200, test.Context.Response.StatusCode);
        Assert.IsTrue(test.Context.Response.Body.Length > 0);
        Assert.AreEqual("attachment; filename=output.pdf", test.Context.Response.Headers.ContentDisposition.ToString());
        test.AssertRequest("Success");
        foreach (var stage in new[] { "RequestValidation", "Upload", "Process", "InputValidation", "OutputValidation", "Send", "Cleanup" })
            test.AssertStage(stage, "Completed", 1);
        test.AssertPrivateAndClean();
    }

    [TestMethod]
    [DataRow("validation", 400, "Rejected", "RequestValidation")]
    [DataRow("upload", 400, "Rejected", "Upload")]
    [DataRow("input", 422, "Rejected", "InputValidation")]
    [DataRow("internal", 500, "InternalError", "Process")]
    [DataRow("output", 500, "InternalError", "OutputValidation")]
    [DataRow("timeout", 504, "Timeout", "Process")]
    [DataRow("client", 200, "ClientCancelled", "Process")]
    [DataRow("stopping", 200, "Stopping", "Process")]
    public async Task Failures_PreserveResponses_AndDistinguishOutcomeWithoutSensitiveData(
        string failure, int status, string outcome, string failedStage)
    {
        using var test = new Harness("/api/pdf/protect");
        using var cancelled = new CancellationTokenSource();
        test.Context.RequestAborted = cancelled.Token;
        test.Options.QpdfTimeoutSeconds = 1;
        await test.Run(async (files, token) =>
        {
            switch (failure)
            {
                case "input":
                    await File.WriteAllBytesAsync(files.InputPath, [], token);
                    await new QpdfProcessor(Options.Create(test.Options)).ValidateAsync(files, token);
                    break;
                case "output":
                    await File.WriteAllBytesAsync(files.OutputPath, [], token);
                    try { await new QpdfProcessor(Options.Create(test.Options)).ValidateOutputAsync(files.OutputPath, token); }
                    catch (PdfInputException) { throw new InvalidOperationException(Secret); }
                    break;
                case "client": cancelled.Cancel(); token.ThrowIfCancellationRequested(); break;
                case "stopping": test.Lifetime.StopApplication(); token.ThrowIfCancellationRequested(); break;
                case "timeout": await Task.Delay(Timeout.Infinite, token); break;
                default: throw new InvalidOperationException(Secret);
            }
        }, failure == "validation" ? () => throw new BadHttpRequestException(Secret) : null,
           failure == "upload");
        Assert.AreEqual(status, test.Context.Response.StatusCode);
        test.AssertRequest(outcome);
        test.AssertStage(failedStage, "Incomplete", 1);
        test.AssertPrivateAndClean();
    }

    [TestMethod]
    public async Task SendFailureAfterHeaders_IsAbortedAndRecorded()
    {
        using var test = new Harness("/api/pdf/split");
        var feature = new StartedResponseFeature();
        test.Context.Features.Set<IHttpResponseFeature>(feature);
        test.Context.Response.Body = new ThrowingStream();
        await PdfEndpointHelpers.ExecuteAsync(test.Context, test.Options, "output.pdf", null,
            (_, _) => Task.CompletedTask, (files, token) => File.WriteAllBytesAsync(files.OutputPath, [1, 2], token),
            onSend: _ => feature.Started = true);
        test.AssertStage("Send", "Incomplete", 1);
        test.AssertRequest("SendAborted");
        test.AssertPrivateAndClean();
    }

    [TestMethod]
    public async Task ConcurrentRequests_AggregateRepeatedStagesWithoutCrossTalkOrUnboundedLogs()
    {
        using var first = new Harness("/api/pdf/redact");
        using var second = new Harness("/api/pdf/clean");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        async Task Work(TemporaryPdfFiles files, CancellationToken token, PdfStage kind, int count)
        {
            if (Interlocked.Increment(ref entered) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            for (var i = 0; i < count; i++)
            {
                using var stage = PdfProcessingDiagnostics.Measure(kind);
                await Task.Yield();
                stage.Complete();
            }
            await File.WriteAllBytesAsync(files.OutputPath, [1], token);
        }
        await Task.WhenAll(first.Run((f, t) => Work(f, t, PdfStage.Render, 500)),
                           second.Run((f, t) => Work(f, t, PdfStage.OutputValidation, 1)));
        first.AssertStage("Render", "Completed", 500);
        second.AssertStage("OutputValidation", "Completed", 1);
        Assert.IsFalse(first.Records.Any(r => r.Fields.GetValueOrDefault("Stage") as string == "OutputValidation"));
        Assert.IsFalse(second.Records.Any(r => r.Fields.GetValueOrDefault("Stage") as string == "Render"));
        Assert.IsTrue(first.Records.All(r => Equals(r.Fields["Operation"], "redact")));
        Assert.IsTrue(second.Records.All(r => Equals(r.Fields["Operation"], "clean")));
        Assert.IsTrue(first.Records.Count <= 9);
        first.AssertPrivateAndClean(); second.AssertPrivateAndClean();
    }

    [TestMethod]
    public async Task SameOperationConcurrentRequests_CanBeGroupedByInternalId_InSharedLogs()
    {
        var shared = new ConcurrentQueue<LogRecord>();
        using var first = new Harness("/api/pdf/redact", records: shared);
        using var second = new Harness("/api/pdf/redact", records: shared);
        second.Options.QpdfTimeoutSeconds = 1;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        async Task Work(TemporaryPdfFiles files, CancellationToken token, bool timeout)
        {
            if (Interlocked.Increment(ref entered) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            for (var i = 0; i < (timeout ? 1 : 500); i++)
            {
                using var stage = PdfProcessingDiagnostics.Measure(PdfStage.Render);
                if (timeout) await Task.Delay(Timeout.Infinite, token);
                await Task.Yield();
                stage.Complete();
            }
            await File.WriteAllBytesAsync(files.OutputPath, [1], token);
        }
        await Task.WhenAll(first.Run((f, t) => Work(f, t, false)), second.Run((f, t) => Work(f, t, true)));
        Assert.AreEqual(200, first.Context.Response.StatusCode);
        Assert.AreEqual(504, second.Context.Response.StatusCode);
        var requests = shared.Where(r => r.Id == 8102).ToArray();
        Assert.AreEqual(2, requests.Length);
        var successId = requests.Single(r => Equals(r.Fields["Outcome"], "Success")).Fields["RequestId"];
        var timeoutId = requests.Single(r => Equals(r.Fields["Outcome"], "Timeout")).Fields["RequestId"];
        Assert.AreNotEqual(successId, timeoutId);
        var success = shared.Where(r => Equals(r.Fields["RequestId"], successId)).ToArray();
        var timedOut = shared.Where(r => Equals(r.Fields["RequestId"], timeoutId)).ToArray();
        Assert.AreEqual(shared.Count, success.Length + timedOut.Length);
        Assert.IsTrue(success.Length <= 9 && timedOut.Length <= 9);
        var successfulRender = success.Single(r => Equals(r.Fields.GetValueOrDefault("Stage"), "Render"));
        Assert.AreEqual(500, successfulRender.Fields["Count"]);
        Assert.AreEqual("Completed", successfulRender.Fields["Outcome"]);
        var timedOutRender = timedOut.Single(r => Equals(r.Fields.GetValueOrDefault("Stage"), "Render"));
        Assert.AreEqual(1, timedOutRender.Fields["Count"]);
        Assert.AreEqual("Incomplete", timedOutRender.Fields["Outcome"]);
        Assert.IsTrue(shared.All(r => Equals(r.Fields["Operation"], "redact")));
        first.AssertPrivateAndClean(); second.AssertPrivateAndClean();
    }

    [TestMethod]
    public async Task CleanupFailureAfterCompletedSend_IsInternalError_WithIncompleteCleanup()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var test = new Harness("/api/pdf/optimize");
        var feature = new StartedResponseFeature();
        test.Context.Features.Set<IHttpResponseFeature>(feature);
        test.Context.Response.Body = new MemoryStream();
        string? job = null;
        try
        {
            await PdfEndpointHelpers.ExecuteAsync(test.Context, test.Options, "output.pdf", null,
                (_, _) => Task.CompletedTask, async (files, token) =>
                {
                    job = files.DirectoryPath;
                    await File.WriteAllBytesAsync(files.OutputPath, [1, 2, 3], token);
                    // Prevent removing the job directory from its parent, while allowing the send.
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(test.Options.TempRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }, onSend: _ => feature.Started = true);
            Assert.AreEqual(200, test.Context.Response.StatusCode);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, ((MemoryStream)test.Context.Response.Body).ToArray());
            Assert.IsTrue(Directory.Exists(job), "The real directory removal must fail.");
            test.AssertStage("Send", "Completed", 1);
            test.AssertStage("Cleanup", "Incomplete", 1);
            test.AssertRequest("InternalError");
        }
        finally
        {
            File.SetUnixFileMode(test.Options.TempRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (Directory.Exists(job)) Directory.Delete(job, true);
        }
        test.AssertPrivateAndClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LogProviderFailure_DoesNotChangeSuccessfulResponseOrCleanup(bool creation)
    {
        using var test = new Harness("/" + Secret, throwLogs: !creation, throwCreate: creation);
        await test.Run((files, token) => File.WriteAllBytesAsync(files.OutputPath, [1, 2, 3], token));
        Assert.AreEqual(200, test.Context.Response.StatusCode);
        Assert.AreEqual(3L, test.Context.Response.Body.Length);
        Assert.AreEqual(0, Directory.GetDirectories(test.Options.TempRoot).Length);
    }

    [TestMethod]
    public async Task UnknownOperation_DoesNotCopyRequestPathIntoLogs()
    {
        using var test = new Harness("/" + Secret);
        await test.Run((files, token) => File.WriteAllBytesAsync(files.OutputPath, [1], token));
        Assert.IsTrue(test.Records.All(r => Equals(r.Fields["Operation"], "unknown")));
        test.AssertRequest("Success");
        test.AssertPrivateAndClean();
    }

    private sealed class Harness : IDisposable
    {
        internal readonly ConcurrentQueue<LogRecord> Records;
        internal readonly Lifetime Lifetime = new();
        internal readonly PdfOptions Options = new() { TempRoot = Path.Combine(Path.GetTempPath(), "diagnostics-" + Guid.NewGuid().ToString("N")) };
        private readonly ServiceProvider services;
        internal readonly DefaultHttpContext Context;
        internal Harness(string path, bool throwLogs = false, bool throwCreate = false,
            ConcurrentQueue<LogRecord>? records = null)
        {
            Records = records ?? new();
            Directory.CreateDirectory(Options.TempRoot);
            services = new ServiceCollection().AddLogging(b => b.AddProvider(new Capture(Records, throwLogs, throwCreate)))
                .AddSingleton<IHostApplicationLifetime>(Lifetime).AddProblemDetails().BuildServiceProvider();
            Context = new DefaultHttpContext { RequestServices = services };
            Context.Request.Path = path;
            Context.Request.QueryString = new QueryString("?password=" + Secret);
            Context.Request.Headers["X-Request-ID"] = Secret;
            Context.TraceIdentifier = Secret;
            Context.Response.Body = new MemoryStream();
        }
        internal Task Run(Func<TemporaryPdfFiles, CancellationToken, Task> process, Action? validate = null, bool uploadError = false)
            => PdfEndpointHelpers.ExecuteAsync(Context, Options, "output.pdf", validate, async (files, token) =>
            {
                if (uploadError) throw new InvalidDataException(Secret);
                await File.WriteAllBytesAsync(files.InputPath, PdfTestContext.Fixture, token);
            }, process);
        internal void AssertRequest(string outcome)
        {
            var record = Records.Single(r => r.Id == 8102);
            Assert.AreEqual(outcome, record.Fields["Outcome"]);
            Assert.IsTrue((double)record.Fields["ElapsedMilliseconds"]! >= 0);
        }
        internal void AssertStage(string stage, string outcome, int count)
        {
            var record = Records.Single(r => r.Id == 8101 && Equals(r.Fields["Stage"], stage));
            Assert.AreEqual(outcome, record.Fields["Outcome"]);
            Assert.AreEqual(count, record.Fields["Count"]);
            Assert.IsTrue((double)record.Fields["ElapsedMilliseconds"]! >= 0);
        }
        internal void AssertPrivateAndClean()
        {
            Assert.AreEqual(0, Directory.GetDirectories(Options.TempRoot).Length);
            foreach (var record in Records)
            {
                Assert.IsFalse(record.Text.Contains(Secret));
                Assert.IsFalse(record.Text.Contains(Options.TempRoot));
                Assert.IsNull(record.Exception);
                Assert.IsTrue(record.Fields["RequestId"] is string id && Guid.TryParseExact(id, "N", out _));
                foreach (var key in record.Fields.Keys)
                    CollectionAssert.Contains(new[] { "RequestId", "Operation", "Stage", "Outcome", "ElapsedMilliseconds", "Count", "IncompleteCount", "{OriginalFormat}" }, key);
            }
        }
        public void Dispose() { services.Dispose(); Lifetime.Dispose(); Context.Response.Body.Dispose(); Directory.Delete(Options.TempRoot, true); }
    }

    private sealed record LogRecord(int Id, Dictionary<string, object?> Fields, string Text, Exception? Exception);
    private sealed class Capture(ConcurrentQueue<LogRecord> records, bool throwLogs, bool throwCreate) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            if (throwCreate && categoryName == "PdfProcessing") throw new InvalidOperationException(Secret);
            return new Logger(records, categoryName, throwLogs);
        }
        public void Dispose() { }
        private sealed class Logger(ConcurrentQueue<LogRecord> records, string category, bool throwLogs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category != "PdfProcessing") return;
                if (throwLogs) throw new InvalidOperationException(Secret);
                records.Enqueue(new(id.Id, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(), formatter(state, exception), exception));
            }
        }
    }
    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource source = new();
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => source.Token;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => source.Cancel();
        public void Dispose() => source.Dispose();
    }
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        internal bool Started;
        public override bool HasStarted => Started;
    }
    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            => ValueTask.FromException(new IOException(Secret));
    }
}
