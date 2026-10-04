var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => Results.Text("amane-pdf-api"));
app.MapGet("/healthz", () => Results.Text("Healthy"));

app.Run();

public partial class Program;
