using Amane.Pdf.Api;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<PdfOptions>().BindConfiguration("Pdf")
    .Validate(options => options.MaxFileBytes > 0 && options.MaxFileBytes < long.MaxValue - PdfOptions.MultipartOverheadBytes &&
        options.QpdfTimeoutSeconds > 0 && options.QpdfTimeoutSeconds <= int.MaxValue / 1000 &&
        options.MaxConcurrentProcesses > 0 && !string.IsNullOrWhiteSpace(options.QpdfPath) && !string.IsNullOrWhiteSpace(options.TempRoot),
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
app.MapPost("/api/pdf/rotate", PdfRotateEndpoint.HandleAsync).RequireRateLimiting("pdf");

app.Run();

public partial class Program;
