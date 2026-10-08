using Amane.Pdf.Api;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<PdfOptions>().BindConfiguration("Pdf")
    .Validate(options => options.MaxFileBytes > 0 && options.MaxFileBytes < long.MaxValue - PdfOptions.MultipartOverheadBytes &&
        options.MaxMergeFiles is >= 2 and <= 10 &&
        options.MaxMergeInputBytes > 0 && options.MaxMergeInputBytes < long.MaxValue - PdfOptions.MultipartOverheadBytes &&
        options.QpdfTimeoutSeconds > 0 && options.QpdfTimeoutSeconds <= int.MaxValue / 1000 &&
        options.MaxConcurrentProcesses > 0 && ProcessMemoryLimits.IsValid(options) && PdfSplitCapacity.IsValid(options) &&
        (!OperatingSystem.IsLinux() || PdfCompressProcessor.IsValid(options)) &&
        !string.IsNullOrWhiteSpace(options.QpdfPath) && !string.IsNullOrWhiteSpace(options.TempRoot),
        "PDF設定値が不正です。")
    .ValidateOnStart();
builder.Services.AddSingleton<QpdfProcessor>();
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>().Configure<IOptions<PdfOptions>>((limiter, pdf) =>
{
    limiter.AddConcurrencyLimiter("pdf", options =>
    {
        options.PermitLimit = pdf.Value.MaxConcurrentProcesses;
        options.QueueLimit = 0;
    });
    limiter.OnRejected = async (context, cancellationToken) =>
        await Results.Problem(statusCode: 503, title: "PDF処理の同時実行上限に達しました。").ExecuteAsync(context.HttpContext);
});

var app = builder.Build();
app.UseRateLimiter();

app.MapGet("/", () => Results.Text("amane-pdf-api"));
app.MapGet("/healthz", () => Results.Text("Healthy"));
app.MapPost("/api/pdf/protect", PdfProtectEndpoint.HandleAsync).RequireRateLimiting("pdf");
app.MapPost("/api/pdf/unlock", PdfUnlockEndpoint.HandleAsync).RequireRateLimiting("pdf");
app.MapPost("/api/pdf/optimize", PdfOptimizeEndpoint.HandleAsync).RequireRateLimiting("pdf");
if (OperatingSystem.IsLinux())
    app.MapPost("/api/pdf/compress", PdfCompressEndpoint.HandleAsync).RequireRateLimiting("pdf");
app.MapPost("/api/pdf/merge", PdfMergeEndpoint.HandleAsync).RequireRateLimiting("pdf");
app.MapPost("/api/pdf/split", PdfSplitEndpoint.HandleAsync).RequireRateLimiting("pdf");
app.MapPost("/api/pdf/rotate", PdfRotateEndpoint.HandleAsync).RequireRateLimiting("pdf");
app.MapPost("/api/pdf/extract", PdfPageSelectionEndpoints.ExtractAsync).RequireRateLimiting("pdf");
app.MapPost("/api/pdf/delete-pages", PdfPageSelectionEndpoints.DeletePagesAsync).RequireRateLimiting("pdf");
app.MapPost("/api/pdf/reorder", PdfPageSelectionEndpoints.ReorderAsync).RequireRateLimiting("pdf");

try
{
    var pdfOptions = app.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
    await ProcessMemoryLimits.ValidateStartupAsync(pdfOptions, app.Lifetime.ApplicationStopping);
}
catch (OptionsValidationException)
{
    app.Logger.LogCritical("PDF設定値が不正です。");
    await app.DisposeAsync();
    return 1;
}
catch (InvalidOperationException)
{
    app.Logger.LogCritical("PDF処理のメモリ制限の自己テストに失敗しました。");
    await app.DisposeAsync();
    return 1;
}
await app.RunAsync();
return 0;

public partial class Program;
