using Amane.Pdf.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<PdfOptions>(builder.Configuration.GetSection("Pdf"));
builder.Services.AddSingleton<QpdfProcessor>();

var app = builder.Build();

app.MapGet("/", () => Results.Text("amane-pdf-api"));
app.MapGet("/healthz", () => Results.Text("Healthy"));
app.MapPost("/api/pdf/protect", PdfProtectEndpoint.HandleAsync);

app.Run();

public partial class Program;
