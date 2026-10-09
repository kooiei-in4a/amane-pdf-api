using System.Text.Json;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfOverlayBuilderTests
{
    [TestMethod]
    public async Task Geometry_RealText_AdjustmentPrecedesOverlay_AndRestoresOriginalAttributes()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob(); await job.CreateInput(OverlayFixtures.Geometry());
        using var input = await job.Metadata(job.Files.InputPath);
        var original = Enumerable.Range(0, 7).Select(i => input.Read(i, default)).ToArray();
        var export = Environment.GetEnvironmentVariable("OVERLAY_RENDER_DIR");
        if (export is not null) { Directory.CreateDirectory(export); File.Copy(job.Files.InputPath, Path.Combine(export, "original.pdf"), true); }
        await job.Builder.BuildAsync(job.Files, 1, 6, async (canvas, token) =>
        {
            Assert.AreEqual(6, canvas.Pages.Count);
            await job.Draw(canvas, token);
            if (export is not null) File.Copy(canvas.LayerPath, Path.Combine(export, "layer.pdf"), true);
        }, CancellationToken.None);
        using var final = await job.Metadata(job.Files.OutputPath);
        Assert.AreEqual(7, final.Pages.Count);
        for (var i = 0; i < 7; i++) Assert.IsTrue(original[i].SameEffective(final.Read(i, default)), $"page {i + 1}");
        // Original content is wrapped without translation after Box adjustment.
        var result = await ExternalProcessRunner.RunAsync(new(job.Settings.QpdfPath,
            ["--qdf", "--object-streams=disable", job.Files.OutputPath, Path.Combine(job.Root, "final.pdf")]), default);
        Assert.AreEqual(0, result.ExitCode);
        var qdf = await File.ReadAllTextAsync(Path.Combine(job.Root, "final.pdf"));
        StringAssert.Contains(qdf, "OUTSIDE");
        StringAssert.Contains(qdf, "/BBox [");
        Assert.IsFalse(File.Exists(Path.Combine(job.Files.DirectoryPath, "normalized.pdf")));
        CollectionAssert.AreEquivalent(new[] { job.Files.OutputPath }, Directory.GetFiles(job.Files.DirectoryPath));
        if (export is not null) File.Copy(job.Files.OutputPath, Path.Combine(export, "final.pdf"), true);
    }

    [TestMethod]
    public async Task ComplexFixture_PreservesImageBookmarkLinkAndForm()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob();
        var source = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "clean-source.json")))!.AsObject();
        var objects = OverlayFixtures.Objects(source);
        objects["obj:3 0 R"]!["value"]!["/Annots"] = System.Text.Json.Nodes.JsonNode.Parse("""["32 0 R","35 0 R"]""");
        objects["obj:35 0 R"] = System.Text.Json.Nodes.JsonNode.Parse("""
            {"value":{"/Type":"/Annot","/Subtype":"/Link","/Rect":[20,40,80,60],"/A":{"/S":"/URI","/URI":"u:https://example.test/retained"}}}
            """);
        objects["trailer"]!["value"]!["/Size"] = 36;
        await job.CreateInput(source.ToJsonString());
        var export = Environment.GetEnvironmentVariable("OVERLAY_RENDER_DIR");
        if (export is not null) { Directory.CreateDirectory(export); File.Copy(job.Files.InputPath, Path.Combine(export, "complex-original.pdf"), true); }
        var originalImages = await ImageData(job, job.Files.InputPath);
        await job.Builder.BuildAsync(job.Files, 1, 1, async (canvas, token) =>
        {
            await job.DrawText(canvas, token, new("日本語 Ab12", Anchor: "tc", Dy: -30, Color: "#ff0000", Rotation: 15));
            if (export is not null) File.Copy(canvas.LayerPath, Path.Combine(export, "complex-layer.pdf"), true);
        }, default);
        CollectionAssert.AreEqual(originalImages, await ImageData(job, job.Files.OutputPath));
        var result = await ExternalProcessRunner.RunAsync(new(job.Settings.QpdfPath,
            ["--json=2", "--json-stream-data=none", job.Files.OutputPath], StdoutLimit: 1048576), default);
        Assert.AreEqual(0, result.ExitCode);
        var json = System.Text.Encoding.UTF8.GetString(result.Stdout!);
        foreach (var marker in new[] { "OUTLINE_RETAINED", "FORM_NAME_RETAINED", "FORM_VALUE_RETAINED", "https://example.test/retained", "/Subtype\": \"/Image", "/Subtype\": \"/Widget" }) StringAssert.Contains(json, marker);
        using var metadata = JsonDocument.Parse(json);
        var pageRef = metadata.RootElement.GetProperty("pages")[0].GetProperty("object").GetString();
        Assert.AreEqual(pageRef, metadata.RootElement.GetProperty("outlines")[0].GetProperty("dest")[0].GetString());
        var fields = metadata.RootElement.GetProperty("acroform").GetProperty("fields");
        Assert.AreEqual(1, fields.GetArrayLength());
        if (export is not null) File.Copy(job.Files.OutputPath, Path.Combine(export, "complex-final.pdf"), true);
    }

    private static async Task<string[]> ImageData(OverlayJob job, string path)
    {
        var result = await ExternalProcessRunner.RunAsync(new(job.Settings.QpdfPath,
            ["--json=2", "--json-key=qpdf", "--json-stream-data=inline", "--decode-level=all", path], StdoutLimit: 8388608), default);
        Assert.AreEqual(0, result.ExitCode); Assert.IsNotNull(result.Stdout);
        using var json = JsonDocument.Parse(result.Stdout);
        return json.RootElement.GetProperty("qpdf")[1].EnumerateObject().Where(property =>
            property.Value.TryGetProperty("stream", out var stream) &&
            stream.GetProperty("dict").TryGetProperty("/Subtype", out var subtype) && subtype.GetString() == "/Image")
            .Select(property =>
            {
                var stream = property.Value.GetProperty("stream"); var dictionary = stream.GetProperty("dict");
                return dictionary.GetProperty("/Width").GetRawText() + ":" + dictionary.GetProperty("/Height").GetRawText() + ":" +
                    dictionary.GetProperty("/ColorSpace").GetRawText() + ":" +
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(stream.GetProperty("data").GetString()!)));
            }).Order().ToArray();
    }

    [TestMethod]
    public async Task InheritedAttributes_RealTools_PreserveEffectiveValues()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob();
        var root = OverlayFixtures.Metadata(); root.Remove("pages");
        await job.CreateInput(root.ToJsonString());
        using var input = await job.Metadata(job.Files.InputPath); var original = input.Read(0, default);
        await job.Builder.BuildAsync(job.Files, 1, 1, job.Draw, default);
        using var final = await job.Metadata(job.Files.OutputPath);
        Assert.IsTrue(original.SameEffective(final.Read(0, default)));
        Assert.AreEqual(1d, final.Read(0, default).UserUnit);
        Assert.AreEqual(630, final.Read(0, default).Rotate);
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(3)] [DataRow(126)] [DataRow(127)]
    public async Task QpdfOutputMissing_IsInternalFailure_AndIntermediatesAreRemoved(int exit)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob(); await job.CreateInput(OverlayFixtures.Geometry());
        var qpdf = job.Settings.QpdfPath;
        job.Settings.QpdfPath = job.Script($"case \"$1\" in --json=2) exit {exit};; esac\nexec {qpdf} \"$@\"");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => job.Builder.BuildAsync(job.Files, 1, 1, job.Draw, default));
        Assert.AreEqual(PdfOverlayBuilder.Failure, error.Message); Assert.IsNull(error.InnerException);
        CollectionAssert.AreEquivalent(new[] { job.Files.InputPath }, Directory.GetFiles(job.Files.DirectoryPath));
    }

    [TestMethod]
    public async Task PdfcpuNoOutputExitOne_DoesNotInferCapacityFromStderr()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob(); await job.CreateInput(OverlayFixtures.Geometry());
        job.Settings.PdfcpuPath = job.Script("echo 'file size limit exceeded SECRET' >&2\nexit 1");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => job.Builder.BuildAsync(job.Files, 1, 1, job.Draw, default));
        CollectionAssert.AreEquivalent(new[] { job.Files.InputPath }, Directory.GetFiles(job.Files.DirectoryPath));
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("missing")]
    [DataRow("broken")]
    [DataRow("encrypted")]
    [DataRow("pages")]
    [DataRow("warning")]
    [DataRow("json")]
    [DataRow("attributes")]
    public async Task InvalidGeneratedOutput_IsFixedInternalFailure(string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob(); await job.CreateInput(OverlayFixtures.Geometry());
        var candidate = Path.Combine(job.Root, "candidate.pdf");
        if (kind == "encrypted")
            Assert.AreEqual(0, await job.Qpdf.RunAsync([job.Files.InputPath, "--encrypt", "synthetic", "synthetic-owner", "256", "--", candidate], default));
        else if (kind == "pages")
            Assert.AreEqual(0, await job.Qpdf.RunAsync([job.Files.InputPath, "--pages", ".", "1", "--", candidate], default));
        else if (kind == "empty") File.WriteAllBytes(candidate, []);
        else File.WriteAllText(candidate, "broken");
        var qpdf = job.Settings.QpdfPath;
        if (kind == "attributes")
        {
            var metadata = await ExternalProcessRunner.RunAsync(new(qpdf,
                ["--json=2", "--json-stream-data=none", job.Files.InputPath], StdoutLimit: 1048576), default);
            Assert.AreEqual(0, metadata.ExitCode);
            var json = System.Text.Json.Nodes.JsonNode.Parse(metadata.Stdout!)!;
            var page = json["pages"]![0]!["object"]!.GetValue<string>();
            json["qpdf"]![1]!["obj:" + page]!["value"]!["/MediaBox"] = "invalid-generated-attribute";
            File.WriteAllText(candidate, json.ToJsonString());
        }
        var body = kind switch
        {
            "warning" => "case \"$1:$2\" in --check:*/output.pdf) exit 3;; esac\n",
            "json" => "case \"$1\" in --json=2) for last do :; done; printf '{' > \"$last\"; exit 0;; esac\n",
            "attributes" => $"case \"$1\" in --json=2) final=false; for arg do case \"$arg\" in */output.pdf) final=true;; esac; done; if $final; then for last do :; done; cp '{candidate}' \"$last\"; exit 0; fi;; esac\n",
            _ => $"case \"$1\" in */overlaid.pdf) for last do :; done; " +
                (kind == "missing" ? "rm -f \"$last\";" : $"cp '{candidate}' \"$last\";") + " exit 0;; esac\n"
        };
        job.Settings.QpdfPath = job.Script(body + $"exec '{qpdf}' \"$@\"");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => job.Builder.BuildAsync(job.Files, 1, 1, job.Draw, default));
        Assert.AreEqual(PdfOverlayBuilder.Failure, error.Message); Assert.IsNull(error.InnerException);
        Assert.IsTrue(Directory.GetFiles(job.Files.DirectoryPath).All(path => path == job.Files.InputPath || path == job.Files.OutputPath));
    }

    [TestMethod]
    public async Task CleanupFailure_PreservesOriginalFailure_AndAttemptsEveryFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob(); await job.CreateInput(OverlayFixtures.Geometry());
        var original = new InvalidOperationException("synthetic draw failure");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => job.Builder.BuildAsync(job.Files, 1, 1,
            (canvas, _) =>
            {
                // File.Delete on a directory fails, even for a root test driver.
                Directory.CreateDirectory(Path.Combine(job.Files.DirectoryPath, "overlay-metadata.json"));
                File.WriteAllText(canvas.LayerPath, "partial");
                File.WriteAllText(job.Files.PdfcpuLayerJsonPath, "partial");
                throw original;
            }, default));
        Assert.AreSame(original, error);
        CollectionAssert.AreEquivalent(new[] { job.Files.InputPath }, Directory.GetFiles(job.Files.DirectoryPath));
    }

    [TestMethod]
    public async Task CleanupFailure_AfterValidation_DoesNotReturnOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var job = new OverlayJob(); await job.CreateInput(OverlayFixtures.Geometry());
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => job.Builder.BuildAsync(job.Files, 1, 1,
            job.Draw, default, stage =>
            {
                if (stage.Name == "job-reserved-peak")
                    Directory.CreateDirectory(Path.Combine(job.Files.DirectoryPath, "overlay-metadata.json"));
            }));
        Assert.AreEqual(PdfOverlayBuilder.Failure, error.Message);
        CollectionAssert.AreEquivalent(new[] { job.Files.OutputPath }, Directory.GetFiles(job.Files.DirectoryPath));
    }
}
