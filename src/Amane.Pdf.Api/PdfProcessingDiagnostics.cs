using System.Diagnostics;

namespace Amane.Pdf.Api;

internal enum PdfStage { RequestValidation, Upload, Process, InputValidation, Render, OutputValidation, Send, Cleanup }
internal enum PdfOutcome { Success, Rejected, Timeout, ClientCancelled, Stopping, InternalError, SendAborted }

// Request-local, bounded aggregates. Never accept request text, paths or exceptions as log fields.
internal sealed class PdfProcessingDiagnostics : IDisposable
{
    private static readonly AsyncLocal<PdfProcessingDiagnostics?> Current = new();
    private readonly PdfProcessingDiagnostics? previous = Current.Value;
    private readonly ILogger? logger;
    private readonly string operation;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly (double Milliseconds, int Count, int Incomplete)[] stages = new (double, int, int)[8];
    private bool disposed;
    internal PdfOutcome Outcome { get; set; } = PdfOutcome.InternalError;

    internal PdfProcessingDiagnostics(HttpContext context)
    {
        try { logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PdfProcessing"); }
        catch (Exception) { /* A provider may also fail while creating its logger. */ }
        // Routing is case insensitive; only constants from this allowlist may reach a log.
        operation = context.Request.Path.Value?.ToLowerInvariant() switch
        {
            "/api/pdf/protect" => "protect", "/api/pdf/restrict" => "restrict",
            "/api/pdf/unlock" => "unlock", "/api/pdf/optimize" => "optimize",
            "/api/pdf/redact" => "redact", "/api/pdf/compress" => "compress",
            "/api/pdf/clean" => "clean", "/api/pdf/from-images" => "from-images",
            "/api/pdf/page-numbers" => "page-numbers", "/api/pdf/fill-template" => "fill-template",
            "/api/pdf/watermark" => "watermark", "/api/pdf/stamp-image" => "stamp-image",
            "/api/pdf/merge" => "merge", "/api/pdf/split" => "split", "/api/pdf/rotate" => "rotate",
            "/api/pdf/extract" => "extract", "/api/pdf/delete-pages" => "delete-pages",
            "/api/pdf/reorder" => "reorder", _ => "unknown"
        };
        Current.Value = this;
    }

    internal static Measurement Measure(PdfStage stage) => new(Current.Value, stage);

    internal sealed class Measurement(PdfProcessingDiagnostics? owner, PdfStage stage) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private bool completed;
        private bool disposed;
        internal void Complete() => completed = true;
        public void Dispose()
        {
            if (disposed || owner is null) return;
            disposed = true;
            lock (owner.stages)
            {
                ref var value = ref owner.stages[(int)stage];
                value.Milliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                value.Count++;
                if (!completed) value.Incomplete++;
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Current.Value = previous;
        if (logger is null) return;
        // A faulty log provider must not change a response or interrupt file cleanup.
        try
        {
            for (var i = 0; i < stages.Length; i++)
            {
                var value = stages[i];
                if (value.Count == 0) continue;
                logger.LogInformation(new EventId(8101, "PdfStage"),
                    "PDF stage Operation={Operation} Stage={Stage} Outcome={Outcome} ElapsedMilliseconds={ElapsedMilliseconds} Count={Count} IncompleteCount={IncompleteCount}",
                    operation, ((PdfStage)i).ToString(), value.Incomplete == 0 ? "Completed" : "Incomplete",
                    value.Milliseconds, value.Count, value.Incomplete);
            }
            logger.LogInformation(new EventId(8102, "PdfRequest"),
                "PDF request Operation={Operation} Outcome={Outcome} ElapsedMilliseconds={ElapsedMilliseconds}",
                operation, Outcome.ToString(), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception) { /* Diagnostics are best effort; do not expose the provider exception. */ }
    }
}
