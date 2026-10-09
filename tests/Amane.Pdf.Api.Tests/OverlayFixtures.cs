using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

internal sealed class OverlayJob : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "overlay-test-" + Guid.NewGuid().ToString("N"));
    internal PdfOptions Settings { get; }
    internal TemporaryPdfFiles Files { get; }
    internal QpdfProcessor Qpdf => new(Options.Create(Settings));
    internal PdfcpuProcessor Pdfcpu => new(Options.Create(Settings));
    internal PdfOverlayBuilder Builder => new(Qpdf, Options.Create(Settings));
    internal OverlayJob()
    {
        Directory.CreateDirectory(Root);
        Settings = new() { TempRoot = Root,
            PdfcpuPath = Environment.GetEnvironmentVariable("Pdf__PdfcpuPath") ?? "pdfcpu",
            PdfcpuConfigDir = Environment.GetEnvironmentVariable("Pdf__PdfcpuConfigDir") ?? "" };
        Files = new(Root);
    }
    internal async Task CreateInput(string json)
    {
        await File.WriteAllTextAsync(Files.JobPath, json);
        Assert.AreEqual(0, await Qpdf.RunAsync(["--json-input", Files.JobPath, Files.InputPath], CancellationToken.None), "Required qpdf must succeed.");
        File.Delete(Files.JobPath);
    }
    internal async Task<PdfPageBoxes> Metadata(string path)
    {
        var result = await ExternalProcessRunner.RunAsync(new(Settings.QpdfPath,
            ["--json=2", "--json-key=qpdf", "--json-key=pages", "--json-stream-data=none", "--decode-level=none", path],
            StdoutLimit: 32 * 1048576), CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode); Assert.IsNotNull(result.Stdout);
        return new(result.Stdout, 32 * 1048576, 75000, CancellationToken.None);
    }
    internal Task Draw(OverlayCanvas canvas, CancellationToken token) =>
        DrawText(canvas, token, new("日本語 Ab12", Anchor: "bc", Dy: 20, FontSize: 12, Color: "#ff0000", Rotation: 15));
    internal Task DrawText(OverlayCanvas canvas, CancellationToken token, PdfcpuText text) => Pdfcpu.CreateAsync(Files,
        new(Enumerable.Range(1, canvas.Pages.Count).ToDictionary(i => i,
            i => (IReadOnlyList<PdfcpuText>)[text]), canvas.Pages),
        canvas.BlankPath, canvas.LayerPath, canvas.JsonBudget, canvas.PdfBudget, token);
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

internal static class OverlayFixtures
{
    internal static JsonObject Metadata()
    {
        return JsonNode.Parse("""
        {"pages":[{"object":"3 0 R"}],"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{
        "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},
        "obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"],"/MediaBox":[-20,-30,600,800],"/CropBox":[100,200,500,700],"/Rotate":630,"/UserUnit":10,"/TrimBox":[1,2,3,4]}},
        "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/Resources":{},"/MediaBox":null,"/CropBox":null,"/Rotate":null}},
        "trailer":{"value":{"/Root":"1 0 R","/Size":4}}}]}
        """)!.AsObject();
    }
    internal static JsonObject Objects(JsonObject root) => root["qpdf"]![1]!.AsObject();
    internal static JsonObject Page(JsonObject root) => Objects(root)["obj:3 0 R"]!["value"]!.AsObject();
    internal static JsonObject Parent(JsonObject root) => Objects(root)["obj:2 0 R"]!["value"]!.AsObject();
    internal static PdfPageBoxes Parse(JsonObject root, int objects = 50000, long bytes = 32 * 1048576, CancellationToken token = default) =>
        new(Encoding.UTF8.GetBytes(root.ToJsonString()), bytes, objects, token);
    internal static string Geometry()
    {
        var root = Metadata(); var objects = Objects(root); var parent = Parent(root);
        parent.Remove("/CropBox"); parent.Remove("/Rotate"); parent.Remove("/TrimBox"); parent.Remove("/UserUnit");
        parent["/Count"] = 7; var kids = new JsonArray(); parent["/Kids"] = kids;
        objects.Remove("obj:3 0 R");
        var rotations = new[] { 0, 90, 180, 270, -90, 630, 0 };
        for (var i = 0; i < 7; i++)
        {
            var page = 3 + 2 * i; kids.Add($"{page} 0 R");
            var unit = i % 3 == 0 ? 0.5 : i % 3 == 1 ? 2 : 1;
            var dictionary = new JsonObject { ["/Type"] = "/Page", ["/Parent"] = "2 0 R",
                ["/MediaBox"] = JsonNode.Parse("[-20,-30,600,800]"), ["/CropBox"] = JsonNode.Parse("[100,200,500,700]"),
                ["/TrimBox"] = JsonNode.Parse("[150,250,450,650]"), ["/BleedBox"] = JsonNode.Parse("[90,190,510,710]"),
                ["/ArtBox"] = JsonNode.Parse("[120,220,480,680]"), ["/UserUnit"] = unit,
                ["/Rotate"] = rotations[i], ["/Resources"] = JsonNode.Parse("{\"/Font\":{\"/F1\":\"17 0 R\"}}"), ["/Contents"] = $"{page + 1} 0 R" };
            objects[$"obj:{page} 0 R"] = new JsonObject { ["value"] = dictionary };
            // Include visible content outside TrimBox, and content outside CropBox.
            var content = "q 0 0 0 rg 110 230 20 20 re f 470 660 20 20 re f 20 20 40 40 re f Q\nBT /F1 12 Tf 20 90 Td (OUTSIDE) Tj ET\n";
            objects[$"obj:{page + 1} 0 R"] = new JsonObject { ["stream"] = new JsonObject {
                ["dict"] = new JsonObject(), ["data"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(content)) } };
        }
        objects["obj:17 0 R"] = JsonNode.Parse("{\"value\":{\"/Type\":\"/Font\",\"/Subtype\":\"/Type1\",\"/BaseFont\":\"/Helvetica\"}}");
        objects["trailer"]!["value"]!["/Size"] = 18; root.Remove("pages");
        return root.ToJsonString();
    }
}
