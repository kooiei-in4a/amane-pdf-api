using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfStampImageTests
{
    private const string Endpoint = "/api/pdf/stamp-image?";
    private const string Query = "page=1&x=0.72&y=0.08&width=0.12";

    [TestMethod]
    public async Task PlacesAtDisplayedPositionForEveryRotationCropAndUserUnit_OnlyTargetPageChanges()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = new OverlayJob(); await source.CreateInput(OverlayFixtures.Geometry());
        await using var test = new PdfTestContext();
        var input = await File.ReadAllBytesAsync(source.Files.InputPath);
        using var before = await Metadata(test, input);
        using var boxes = new PdfPageBoxes(Encoding.UTF8.GetBytes(before.RootElement.GetRawText()), 32 * 1048576, 50000, default);
        for (var page = 1; page <= 7; page++)
        {
            using var form = Form(input, StampFixtures.Png(), imageFirst: page % 2 == 0);
            using var response = await test.Client.PostAsync(Endpoint + Query.Replace("page=1", "page=" + page), form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("stamped.pdf", response.Content.Headers.ContentDisposition?.FileName);
            var output = await response.Content.ReadAsByteArrayAsync();
            await test.AssertValidPdfAsync(output, 7);
            using var after = await Metadata(test, output);
            using var final = new PdfPageBoxes(Encoding.UTF8.GetBytes(after.RootElement.GetRawText()), 32 * 1048576, 50000, default);
            for (var p = 0; p < 7; p++)
            {
                Assert.IsTrue(boxes.Read(p, default).SameEffective(final.Read(p, default)));
                if (p != page - 1) Assert.AreEqual(Content(before, p), Content(after, p), "Non-target page content");
            }
            var images = Images(after, page - 1);
            Assert.AreEqual(1, images.Count);
            var attr = boxes.Read(page - 1, default);
            var expected = new[] { (0.72 * attr.DisplayWidth, 0.08 * attr.DisplayHeight),
                (0.84 * attr.DisplayWidth, 0.08 * attr.DisplayHeight),
                (0.72 * attr.DisplayWidth, 0.08 * attr.DisplayHeight + 0.09 * attr.DisplayWidth) };
            var corners = new[] { (0d, 1d), (1d, 1d), (0d, 0d) };
            for (var i = 0; i < corners.Length; i++)
            {
                var point = Display(Apply(images[0].Matrix, corners[i].Item1, corners[i].Item2), attr);
                Assert.AreEqual(expected[i].Item1, point.X, 1, "Display x");
                Assert.AreEqual(expected[i].Item2, point.Y, 1, "Display y");
            }
            Assert.IsTrue(images[0].Dictionary.TryGetProperty("/SMask", out _));
            Assert.IsFalse(after.RootElement.GetRawText().Contains("PRIVATE_STAMP_METADATA"));
            test.AssertClean();
        }
    }

    [TestMethod]
    public async Task AcceptsAllPngDepthsAndAdam7IncludingPaletteTransparency()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        foreach (var color in new[] { 0, 2, 3, 4, 6 })
        foreach (var depth in color switch { 0 => new[] { 1, 2, 4, 8, 16 }, 3 => [1, 2, 4, 8], _ => [8, 16] })
        foreach (var interlaced in new[] { false, true })
        foreach (var transparency in color is 0 or 2 ? new[] { false, true } : [false])
        {
            using var form = Form(PdfTestContext.Fixture, StampFixtures.Png(17, 23, depth, color, interlaced, transparency: transparency));
            using var response = await test.Client.PostAsync(Endpoint + Query, form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"color {color}, depth {depth}, Adam7 {interlaced}");
            using var output = await Metadata(test, await response.Content.ReadAsByteArrayAsync());
            var image = Images(output, 0).Single().Dictionary;
            if (color is 3 or 4 or 6 || transparency) Assert.IsTrue(image.TryGetProperty("/SMask", out _));
            test.AssertClean();
        }
    }

    [TestMethod]
    public async Task InvalidQueryPrecedesUploadAndTools()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        foreach (var query in new[] { "", "page=1&x=0&y=0", Query + "&x=0", Query + "&unknown=1",
                     Query.Replace("0.72", "0.١٢"), Query.Replace("0.08", "0.12345"), Query.Replace("page=1", "page=2147483648") })
        {
            using var badBody = new StringContent("PRIVATE_STAMP_INVALID_UPLOAD");
            using var response = await test.Client.PostAsync(Endpoint + query, badBody);
            await Problem(test, response, 400);
        }
    }

    [TestMethod]
    public async Task InvalidMultipartImageAndPlacement_ReturnExpectedProblems()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        foreach (var kind in new[] { "missing-file", "missing-image", "duplicate-image", "duplicate-file", "unknown", "no-filename" })
        {
            using var form = new MultipartFormDataContent();
            if (kind != "missing-file") form.Add(new ByteArrayContent(PdfTestContext.Fixture), "file", "input.pdf");
            if (kind != "missing-image")
            {
                if (kind == "no-filename") form.Add(new ByteArrayContent(StampFixtures.Png()), "image");
                else form.Add(new ByteArrayContent(StampFixtures.Png()), "image", "stamp.png");
            }
            if (kind.StartsWith("duplicate")) form.Add(new ByteArrayContent([]), kind == "duplicate-file" ? "file" : "image", "duplicate");
            if (kind == "unknown") form.Add(new StringContent("PRIVATE_STAMP"), "password");
            using var response = await test.Client.PostAsync(Endpoint + Query, form);
            await Problem(test, response, 400);
        }
        var corrupt = StampFixtures.Png(); corrupt[29] ^= 1;
        foreach (var image in new byte[][] { [], "not PNG"u8.ToArray(), [255, 216, 255, 217], corrupt,
                     StampFixtures.Png(2001, 1), StampFixtures.Png(1, 2001), StampFixtures.Png(fault: "adler"),
                     StampFixtures.Png(fault: "truncated"), StampFixtures.Png(fault: "filter") })
        {
            using var form = Form(PdfTestContext.Fixture, image);
            using var response = await test.Client.PostAsync(Endpoint + Query, form);
            await Problem(test, response, 422, "unsupported-image");
        }
        foreach (var query in new[] { Query.Replace("page=1", "page=2"), Query.Replace("0.72", "0.89"), Query.Replace("0.08", "1") })
        {
            using var form = Form(PdfTestContext.Fixture, StampFixtures.Png());
            using var response = await test.Client.PostAsync(Endpoint + query, form);
            await Problem(test, response, 400);
        }
    }

    [TestMethod]
    public async Task IndividualAndTotalUploadLimits_WorkWithAndWithoutContentLength()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString() });
        foreach (var chunked in new[] { false, true })
        foreach (var oversizedImage in new[] { false, true })
        {
            using var form = Form(oversizedImage ? PdfTestContext.Fixture : [.. PdfTestContext.Fixture, (byte)'X'],
                oversizedImage ? new byte[1048577] : StampFixtures.Png());
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + Query)
                { Content = chunked ? new StreamingContent(form) : form };
            using var response = await test.Client.SendAsync(request);
            await Problem(test, response, 413);
        }
        foreach (var chunked in new[] { false, true })
        {
            using var form = Form(PdfTestContext.Fixture, StampFixtures.Png());
            var bytes = await form.ReadAsByteArrayAsync();
            var oversized = new byte[test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.MaxStampRequestBytes + 1];
            bytes.CopyTo(oversized, 0); Array.Fill(oversized, (byte)'X', bytes.Length, oversized.Length - bytes.Length);
            using var body = new ByteArrayContent(oversized); body.Headers.ContentType = form.Headers.ContentType;
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + Query) { Content = chunked ? new StreamingContent(body) : body };
            using var response = await test.Client.SendAsync(request); await Problem(test, response, 413);
        }
    }

    [TestMethod]
    public async Task MaxPdfPlusMaxPng_IsAccepted_WithoutRaisingOtherEndpointsLimit()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        // Padding precedes xref, keeping PDF's EOF within the reader's final window.
        var fixture = PdfTestContext.Fixture;
        var text = Encoding.ASCII.GetString(fixture);
        var match = Regex.Match(text, @"startxref\s+([0-9]+)");
        var oldOffset = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var originalSuffix = text[oldOffset..];
        var size = 50 * 1048576;
        var suffix = originalSuffix;
        for (var i = 0; i < 3; i++) suffix = originalSuffix.Replace("startxref\n" + oldOffset, "startxref\n" + (size - suffix.Length));
        var pdf = new byte[size]; Array.Fill(pdf, (byte)' ');
        fixture.AsSpan(0, oldOffset).CopyTo(pdf); Encoding.ASCII.GetBytes(suffix).CopyTo(pdf, size - suffix.Length);
        var png = new byte[1048576]; StampFixtures.Png().CopyTo(png, 0);
        using var form = Form(pdf, png);
        using var response = await test.Client.PostAsync(Endpoint + Query, form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
        test.AssertClean();
        using var other = PdfTestContext.FileForm([.. pdf, (byte)'X']);
        using var rejected = await test.Client.PostAsync("/api/pdf/optimize", other);
        await Problem(test, rejected, 413);
    }

    [TestMethod]
    public async Task SmallValidPngWithHugeLayer_ReturnsImageTooComplex()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var png = StampFixtures.Png(2000, 2000, expensive: true);
        Assert.IsTrue(png.Length < 1048576);
        using var form = Form(PdfTestContext.Fixture, png);
        using var response = await test.Client.PostAsync(Endpoint + Query, form);
        await Problem(test, response, 422, "image-too-complex");
    }

    [TestMethod]
    public async Task PdfErrorsAndToolFailuresKeepDistinctContracts()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        foreach (var pdf in new byte[][] { [], "%PDF-1.4\n%%EOF"u8.ToArray(), await test.CreateEncryptedPdfAsync("synthetic", "owner") })
        {
            using var form = Form(pdf, StampFixtures.Png()); using var response = await test.Client.PostAsync(Endpoint + Query, form);
            await Problem(test, response, 422);
        }
        using var source = new OverlayJob();
        var root = JsonNode.Parse(PdfcpuProcessor.StartupBlankJson)!.AsObject(); OverlayFixtures.Page(root)["/Rotate"] = 45;
        await source.CreateInput(root.ToJsonString());
        using (var form = Form(await File.ReadAllBytesAsync(source.Files.InputPath), StampFixtures.Png()))
        using (var response = await test.Client.PostAsync(Endpoint + Query, form)) await Problem(test, response, 422, "unsupported-pdf");
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.OverlayJsonLimitBytes = 4096;
        using (var form = Form(await test.CreatePagedPdfAsync(20), StampFixtures.Png()))
        using (var response = await test.Client.PostAsync(Endpoint + Query, form)) await Problem(test, response, 422, "too-complex");
        options.OverlayJsonLimitBytes = 32 * 1048576;
        var realTool = options.PdfcpuPath;
        foreach (var exit in new[] { 1, 2, 126, 127, 137 })
        {
            options.PdfcpuPath = source.Script("echo PRIVATE_STAMP_STDERR >&2\nexit " + exit);
            using var form = Form(PdfTestContext.Fixture, StampFixtures.Png()); using var response = await test.Client.PostAsync(Endpoint + Query, form);
            await Problem(test, response, exit == 1 ? 422 : 500, exit == 1 ? "unsupported-image" : null);
        }
        options.PdfcpuPath = realTool; options.QpdfPath = "/missing/private-tool";
        using var failure = Form(PdfTestContext.Fixture, StampFixtures.Png()); using var failed = await test.Client.PostAsync(Endpoint + Query, failure);
        await Problem(test, failed, 500);
    }

    [TestMethod]
    public async Task TimeoutCancelAndBusySlot_DeleteImageAndWaitForProcessTree()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var blocker = new BlockingQpdf("--json=2");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocker.Executable,
            ["Pdf:QpdfTimeoutSeconds"] = "2", ["Pdf:MaxConcurrentProcesses"] = "1" });
        using var firstForm = Form(PdfTestContext.Fixture, StampFixtures.Png());
        var first = test.Client.PostAsync(Endpoint + Query, firstForm); await blocker.WaitForJobsAsync(1);
        Assert.IsTrue(Directory.GetFiles(test.TempRoot, "stamp.png", SearchOption.AllDirectories).Length == 1);
        using (var busyForm = PdfTestContext.FileForm(PdfTestContext.Fixture))
        using (var busy = await test.Client.PostAsync("/api/pdf/optimize", busyForm)) Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        using (var response = await first) await Problem(test, response, 504);
        await blocker.AssertStoppedAsync();
        using var cancellation = new CancellationTokenSource(); using var cancelledForm = Form(PdfTestContext.Fixture, StampFixtures.Png());
        var cancelled = test.Client.PostAsync(Endpoint + Query, cancelledForm, cancellation.Token);
        await blocker.WaitForJobsAsync(2); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        await blocker.AssertStoppedAsync(); test.AssertClean();
        using var nextForm = PdfTestContext.FileForm(PdfTestContext.Fixture);
        using var next = await test.Client.PostAsync("/api/pdf/optimize", nextForm); Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
    }

    [TestMethod]
    [DataRow("input", "timeout")]
    [DataRow("draw", "timeout")]
    [DataRow("overlay", "timeout")]
    [DataRow("input", "cancel")]
    [DataRow("draw", "cancel")]
    [DataRow("overlay", "cancel")]
    [DataRow("input", "stopping")]
    [DataRow("draw", "stopping")]
    [DataRow("overlay", "stopping")]
    public async Task EveryStageWaitsForChildrenAndDeletesStamp(string stage, string kind)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var blocker = new BlockingQpdf(stage switch { "draw" => "-c", "overlay" => "*/input.pdf", _ => "--json=2" });
        using var drawJob = new OverlayJob();
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfTimeoutSeconds"] = "2" });
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        // FSIZE=1 deliberately forbids the blocker's regular PID file in stamp.
        // Observe /proc instead, so fault injection respects the production limits.
        if (stage == "draw") options.PdfcpuPath = drawJob.Script("sleep 300 &\nwait \"$!\"");
        else options.QpdfPath = blocker.Executable;
        using var cancel = new CancellationTokenSource(); using var form = Form(PdfTestContext.Fixture, StampFixtures.Png());
        var request = test.Client.PostAsync(Endpoint + Query, form, cancel.Token);
        int[] drawPids = [];
        if (stage == "draw")
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (drawPids.Length < 2)
            {
                foreach (var path in Directory.EnumerateDirectories("/proc").Where(p => int.TryParse(Path.GetFileName(p), out _)))
                {
                    try
                    {
                        if (!File.ReadAllText(path + "/cmdline").Split('\0').Contains(options.PdfcpuPath)) continue;
                        var pid = int.Parse(Path.GetFileName(path), CultureInfo.InvariantCulture);
                        var children = File.ReadAllText(path + "/task/" + pid + "/children").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (children.Length > 0) drawPids = [pid, .. children.Select(int.Parse)];
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                Assert.IsTrue(deadline.Elapsed < TimeSpan.FromSeconds(5), "Stamp wrapper did not start.");
                if (drawPids.Length < 2) await Task.Delay(10);
            }
        }
        else await blocker.WaitForJobsAsync(1);
        if (kind == "cancel") cancel.Cancel();
        if (kind == "stopping") test.Factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        if (kind == "timeout")
        { using var response = await request; await Problem(test, response, 504); }
        else
        {
            try { using var response = await request; }
            catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or IOException) { }
        }
        // Stopping must finish cleanup before the client observes the abort, without polling.
        if (kind == "stopping") test.AssertClean();
        if (stage == "draw")
        {
            foreach (var pid in drawPids)
            {
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    try { using var process = System.Diagnostics.Process.GetProcessById(pid); if (process.HasExited) break; }
                    catch (ArgumentException) { break; }
                    if (wait.Elapsed.TotalSeconds >= 5) Assert.Fail("Child was not reaped");
                    await Task.Delay(20);
                }
            }
        }
        else await blocker.AssertStoppedAsync();
        test.AssertClean();
    }

    internal static MultipartFormDataContent Form(byte[] pdf, byte[] image, bool imageFirst = false)
    {
        var form = new MultipartFormDataContent();
        if (imageFirst) form.Add(new ByteArrayContent(image), "image", "../../PRIVATE_STAMP.png");
        form.Add(new ByteArrayContent(pdf), "file", "../../PRIVATE_STAMP.pdf");
        if (!imageFirst) form.Add(new ByteArrayContent(image), "image", "../../PRIVATE_STAMP.png");
        return form;
    }

    private static async Task Problem(PdfTestContext test, HttpResponseMessage response, int status, string? reason = null)
    {
        var body = await response.Content.ReadAsStringAsync(); Assert.AreEqual(status, (int)response.StatusCode, body);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        if (reason is not null) Assert.AreEqual(reason, document.RootElement.GetProperty("reason").GetString());
        foreach (var marker in new[] { "PRIVATE_STAMP", "private-tool", test.TempRoot })
        { Assert.IsFalse(body.Contains(marker)); Assert.IsFalse(string.Join('\n', test.Logs).Contains(marker)); }
        test.AssertClean();
    }

    private static async Task<JsonDocument> Metadata(PdfTestContext test, byte[] bytes)
    {
        var path = Path.Combine(test.Root, Guid.NewGuid().ToString("N") + ".pdf"); await File.WriteAllBytesAsync(path, bytes);
        var result = await test.QpdfAsync("--json=2", "--json-key=pages", "--json-key=qpdf", "--json-stream-data=inline", "--decode-level=all", path);
        Assert.AreEqual(0, result.ExitCode); return JsonDocument.Parse(result.Output);
    }
    private static string StreamText(JsonElement obj) => Encoding.ASCII.GetString(Convert.FromBase64String(obj.GetProperty("stream").GetProperty("data").GetString()!));
    private static string Content(JsonDocument doc, int page) => string.Join('\n', doc.RootElement.GetProperty("pages")[page].GetProperty("contents").EnumerateArray()
        .Select(r => StreamText(doc.RootElement.GetProperty("qpdf")[1].GetProperty("obj:" + r.GetString()))));
    private static List<(double[] Matrix, JsonElement Dictionary)> Images(JsonDocument doc, int page)
    {
        var objects = doc.RootElement.GetProperty("qpdf")[1]; var found = new List<(double[], JsonElement)>();
        JsonElement Resolve(JsonElement element) => element.ValueKind == JsonValueKind.String
            ? objects.GetProperty("obj:" + element.GetString()).GetProperty("value") : element;
        void Walk(string content, JsonElement resources, double[] matrix, int depth)
        {
            Assert.IsTrue(depth < 10); var stack = new Stack<double[]>(); var args = new List<string>();
            foreach (Match match in Regex.Matches(content, @"/[^\s/<>\[\]()]+|[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)|[A-Za-z]+"))
            {
                var token = match.Value;
                if (token[0] == '/' || double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) { args.Add(token); continue; }
                if (token == "q") stack.Push(matrix);
                else if (token == "Q") matrix = stack.Pop();
                else if (token == "cm") matrix = Multiply(args.TakeLast(6).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray(), matrix);
                else if (token == "Do")
                {
                    var xobjects = Resolve(Resolve(resources).GetProperty("/XObject"));
                    var obj = objects.GetProperty("obj:" + xobjects.GetProperty(args[^1]).GetString()); var dict = obj.GetProperty("stream").GetProperty("dict");
                    if (dict.GetProperty("/Subtype").GetString() == "/Image") found.Add((matrix, dict));
                    else Walk(StreamText(obj), dict.GetProperty("/Resources"), Multiply(dict.TryGetProperty("/Matrix", out var m)
                        ? m.EnumerateArray().Select(v => v.GetDouble()).ToArray() : [1, 0, 0, 1, 0, 0], matrix), depth + 1);
                }
                args.Clear();
            }
        }
        var dictionary = objects.GetProperty("obj:" + doc.RootElement.GetProperty("pages")[page].GetProperty("object").GetString()).GetProperty("value");
        Walk(Content(doc, page), dictionary.GetProperty("/Resources"), [1, 0, 0, 1, 0, 0], 0); return found;
    }
    private static double[] Multiply(double[] a, double[] b) => [a[0]*b[0]+a[1]*b[2], a[0]*b[1]+a[1]*b[3],
        a[2]*b[0]+a[3]*b[2], a[2]*b[1]+a[3]*b[3], a[4]*b[0]+a[5]*b[2]+b[4], a[4]*b[1]+a[5]*b[3]+b[5]];
    private static (double X, double Y) Apply(double[] m, double x, double y) => (m[0]*x+m[2]*y+m[4], m[1]*x+m[3]*y+m[5]);
    private static (double X, double Y) Display((double X, double Y) point, PdfPageAttributes p)
    {
        var x = (point.X - p.Crop.Left) * p.UserUnit; var y = (point.Y - p.Crop.Bottom) * p.UserUnit;
        var w = p.Crop.Width * p.UserUnit; var h = p.Crop.Height * p.UserUnit;
        return p.NormalizedRotate switch { 0 => (x, h-y), 90 => (y, x), 180 => (w-x, y), _ => (h-y, w-x) };
    }

    private sealed class StreamingContent : HttpContent
    {
        private readonly HttpContent source;
        internal StreamingContent(HttpContent source)
        {
            this.source = source;
            foreach (var header in source.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => source.CopyToAsync(stream);
    }
}
