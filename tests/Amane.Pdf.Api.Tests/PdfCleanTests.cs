using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfCleanTests
{
    internal static async Task<byte[]> FixtureAsync(PdfTestContext test)
    {
        var output = Path.Combine(test.Root, "clean-source.pdf");
        var source = Path.Combine(test.Root, "clean-source.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "clean-source.json"), source, overwrite: true);
        Assert.AreEqual(0, (await test.QpdfAsync("--json-input", source, output)).ExitCode);
        return await File.ReadAllBytesAsync(output);
    }
    internal static async Task<HttpResponseMessage> PostAsync(PdfTestContext test, byte[] input, CancellationToken token = default)
    {
        using var form = PdfTestContext.FileForm(input, "../../private-name.pdf");
        return await test.Client.PostAsync("/api/pdf/clean", form, token);
    }
    internal static async Task ProblemAsync(PdfTestContext test, HttpResponseMessage response, int status, string? reason = null)
    {
        Assert.AreEqual(status, (int)response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.AreEqual(reason, json.RootElement.TryGetProperty("reason", out var value) ? value.GetString() : null);
        foreach (var secret in new[] { test.Root, "private-name", "SENTINEL", "stderr", "stack" })
            Assert.IsFalse(body.Contains(secret, StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(response.Content.Headers.ContentDisposition);
        test.AssertClean();
        Assert.IsFalse(test.Logs.Any(log => log.Contains("SENTINEL", StringComparison.Ordinal) || log.Contains(test.Root, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Clean_RemovesMetadataAllAttachmentPathsAuthorsAndPopups_PreservesPageStreamsAndOrdinaryData()
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var input = await FixtureAsync(test);
        using var response = await PostAsync(test, input);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("cleaned.pdf", response.Content.Headers.ContentDisposition?.FileName);
        var output = await response.Content.ReadAsByteArrayAsync();
        Assert.AreEqual(output.LongLength, response.Content.Headers.ContentLength);
        await test.AssertValidPdfAsync(output, 1);
        var path = Path.Combine(test.Root, "cleaned.pdf");
        await File.WriteAllBytesAsync(path, output);
        var expanded = Path.Combine(test.Root, "expanded.pdf");
        Assert.AreEqual(0, (await test.QpdfAsync("--qdf", "--object-streams=disable", "--decode-level=all", path, expanded)).ExitCode);
        var text = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(expanded));
        foreach (var marker in new[] { "ANNOTATION_PAYLOAD_MARKER", "ANNOTATION_FILENAME_MARKER", "NAMETREE_PAYLOAD_MARKER", "NAMETREE_FILENAME_MARKER",
            "GOTOE_PAYLOAD_MARKER", "RICHMEDIA_PAYLOAD_MARKER", "RELATED_FILE_PAYLOAD_MARKER", "DOCUMENT_XMP_MARKER", "AUTHOR_MARKER", "PRODUCER_MARKER",
            "ATTACHMENT_DESCRIPTION_MARKER", "ATTACHMENT_RICH_DESCRIPTION_MARKER", "ATTACHMENT_AUTHOR_MARKER", "ATTACHMENT_SUBJECT_MARKER", "ATTACHMENT_NODE",
            "PARENTLESS_POPUP_MARKER", "PARENT_REFERENCED_POPUP_MARKER", "/EF ", "/RF ", "/AF " })
            Assert.IsFalse(text.Contains(marker, StringComparison.Ordinal), marker);
        foreach (var marker in new[] { "GOTOE_FILENAME_RETAINED", "RICHMEDIA_FILENAME_RETAINED", "COMMENT_AUTHOR_RETAINED", "REPLY_AUTHOR_RETAINED",
            "PAGE_XMP_RETAINED", "/OBJR", "/Dests", "NON_DICTIONARY_RETAINED", "FORM_VALUE_RETAINED", "OUTLINE_RETAINED", "JAVASCRIPT_RETAINED", "/Collection" }) Assert.IsTrue(text.Contains(marker, StringComparison.Ordinal), marker);
        var original = Path.Combine(test.Root, "original.pdf");
        await File.WriteAllBytesAsync(original, input);
        Assert.AreEqual(await ContentHashAsync(test, original), await ContentHashAsync(test, path));
        using var before = JsonDocument.Parse((await test.QpdfAsync("--json=2", "--json-key=qpdf", original)).Output);
        using var after = JsonDocument.Parse((await test.QpdfAsync("--json=2", "--json-key=qpdf", path)).Output);
        var inputObjects = before.RootElement.GetProperty("qpdf")[1];
        var outputObjects = after.RootElement.GetProperty("qpdf")[1];
        var inputTrailer = inputObjects.GetProperty("trailer").GetProperty("value");
        var outputTrailer = outputObjects.GetProperty("trailer").GetProperty("value");
        static JsonElement Page(JsonElement objects) => objects.EnumerateObject().Select(item => item.Value)
            .Where(item => item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("/Type", out var type) && type.GetString() == "/Page").Single().GetProperty("value");
        foreach (var key in new[] { "/MediaBox", "/CropBox", "/Rotate" })
            Assert.AreEqual(Page(inputObjects).GetProperty(key).GetRawText(), Page(outputObjects).GetProperty(key).GetRawText());
        Assert.AreEqual(inputTrailer.GetProperty("/ID")[0].GetString(), outputTrailer.GetProperty("/ID")[0].GetString());
        var info = outputObjects.GetProperty("obj:" + outputTrailer.GetProperty("/Info").GetString()).GetProperty("value");
        CollectionAssert.AreEqual(new[] { "/ModDate" }, info.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("u:D:20260102000000Z", info.GetProperty("/ModDate").GetString());
        using var second = await PostAsync(test, output);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        test.AssertClean();
    }

    private static async Task<string> ContentHashAsync(PdfTestContext test, string path)
    {
        using var json = JsonDocument.Parse((await test.QpdfAsync("--json=2", "--json-key=pages", "--json-key=qpdf", path)).Output);
        var reference = json.RootElement.GetProperty("pages")[0].GetProperty("contents")[0].GetString()!.Split(' ')[0];
        var stream = await test.QpdfAsync("--show-object=" + reference, "--filtered-stream-data", path);
        Assert.AreEqual(0, stream.ExitCode);
        var decoded = stream.Output;
        foreach (var item in json.RootElement.GetProperty("qpdf")[1].EnumerateObject())
            if (item.Value.TryGetProperty("stream", out var image) &&
                image.GetProperty("dict").TryGetProperty("/Subtype", out var subtype) && subtype.GetString() == "/Image")
            {
                var pixels = await test.QpdfAsync("--show-object=" + item.Name[4..].Split(' ')[0], "--filtered-stream-data", path);
                Assert.AreEqual(0, pixels.ExitCode);
                decoded += pixels.Output;
            }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(decoded)));
    }

    [TestMethod]
    public async Task MultiPageRotationAndOrder_ArePreserved()
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var input = await test.CreateRotationMarkedPdfAsync(4);
        using var response = await PostAsync(test, input);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var output = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(output, 4);
        CollectionAssert.AreEqual(new[] { 0, 90, 180, 270 }, await test.ReadPageRotationsAsync(output));
        test.AssertClean();
    }

    [TestMethod]
    public async Task IncrementalUpdate_DropsOldInfoBytes()
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var original = await FixtureAsync(test);
        var text = Encoding.Latin1.GetString(original);
        var previous = text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0];
        using var metadata = JsonDocument.Parse((await test.QpdfAsync("--json=2", "--json-key=qpdf", Path.Combine(test.Root, "clean-source.pdf"))).Output);
        var root = metadata.RootElement.GetProperty("qpdf")[1].GetProperty("trailer").GetProperty("value").GetProperty("/Root").GetString();
        var newObject = "1000 0 obj\n<< /ModDate (D:20260102000000Z) >>\nendobj\n";
        var update = newObject + $"xref\n1000 1\n{original.Length:0000000000} 00000 n \ntrailer\n<< /Size 1001 /Root {root} /Info 1000 0 R /Prev {previous} >>\nstartxref\n{original.Length + newObject.Length}\n%%EOF\n";
        var input = original.Concat(Encoding.ASCII.GetBytes(update)).ToArray();
        using var response = await PostAsync(test, input);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync()).Contains("AUTHOR_MARKER", StringComparison.Ordinal));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("text")]
    [DataRow("warning")]
    [DataRow("password")]
    [DataRow("owner")]
    public async Task InvalidOrEncryptedInput_UsesExisting422(string kind)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var input = kind switch
        {
            "empty" => [], "text" => "SENTINEL"u8.ToArray(),
            "warning" => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(PdfTestContext.Fixture).Replace("/Length 41", "/Length 39", StringComparison.Ordinal)),
            _ => await test.CreateEncryptedPdfAsync(kind == "owner" ? "" : "fixture-password", "fixture-owner")
        };
        using var response = await PostAsync(test, input);
        await ProblemAsync(test, response, 422);
    }

    [TestMethod]
    public async Task UnexpectedFieldsQueryDuplicateMissingAndOversize_AreRejectedBeforeQpdf()
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run", ["Pdf:MaxFileBytes"] = "1024" });
        using (var missing = PdfTestContext.FileForm())
        using (var response = await test.Client.PostAsync("/api/pdf/clean", missing)) await ProblemAsync(test, response, 400);
        using (var password = PdfTestContext.Form(PdfTestContext.Fixture))
        using (var response = await test.Client.PostAsync("/api/pdf/clean", password)) await ProblemAsync(test, response, 400);
        using (var duplicate = PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture))
        using (var response = await test.Client.PostAsync("/api/pdf/clean", duplicate)) await ProblemAsync(test, response, 400);
        using (var query = PdfTestContext.FileForm(PdfTestContext.Fixture))
        using (var response = await test.Client.PostAsync("/api/pdf/clean?level=standard", query)) await ProblemAsync(test, response, 400);
        using (var response = await PostAsync(test, new byte[1025])) await ProblemAsync(test, response, 413);
    }

    [TestMethod]
    [DataRow("json")]
    [DataRow("pdf")]
    [DataRow("job")]
    public async Task ActualLimits_ReturnTooComplex(string limit)
    {
        if (!Linux()) return;
        var settings = new Dictionary<string, string?>();
        if (limit == "json") settings["Pdf:CleanJsonLimitBytes"] = "128";
        if (limit == "pdf") settings["Pdf:CleanOutputLimitBytes"] = "128";
        if (limit == "job") { settings["Pdf:MaxFileBytes"] = "4096"; settings["Pdf:CleanJobLimitBytes"] = "4096"; }
        await using var test = new PdfTestContext(settings);
        using var response = await PostAsync(test, PdfTestContext.Fixture);
        await ProblemAsync(test, response, 422, "too-complex");
    }

    [TestMethod]
    [DataRow("exit2")]
    [DataRow("exit3")]
    [DataRow("exit126")]
    [DataRow("exit127")]
    [DataRow("malformed")]
    [DataRow("malformed-output")]
    [DataRow("empty")]
    [DataRow("broken-output")]
    [DataRow("warning-output")]
    [DataRow("warning-input-count")]
    [DataRow("pages")]
    [DataRow("residual")]
    public async Task UnexpectedFailuresAndResiduals_AreSanitized500(string fault)
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var wrapper = Path.Combine(test.Root, "qpdf.sh");
        var body = fault == "malformed"
            ? "case \"$1\" in --json=2) for last do :; done; printf '{' > \"$last\"; exit 0;; esac\n"
            : fault == "malformed-output"
            ? "case \"$1\" in --json=2) for arg do if [ \"$arg\" = --json-key=attachments ]; then for last do :; done; printf '{' > \"$last\"; exit 0; fi; done;; esac\n"
            : fault is "empty" or "broken-output"
            ? "case \"$1\" in --job-json-file=*) job=${1#--job-json-file=}; dir=${job%/*}; printf '" + (fault == "empty" ? "" : "broken") + "' > \"$dir/output.pdf\"; exit 0;; esac\n"
            : fault == "warning-output"
            ? "case \"$1:$2\" in --check:*/output.pdf) exit 3;; esac\n"
            : fault == "warning-input-count"
            ? "case \"$1\" in --show-npages) exit 3;; esac\n"
            : fault == "pages"
            ? "case \"$1:$2\" in --show-npages:*/output.pdf) printf '2\\n'; exit 0;; esac\n"
            : fault == "residual"
            ? "case \"$1\" in --job-json-file=*) job=${1#--job-json-file=}; dir=${job%/*}; exec qpdf \"$dir/input.pdf\" \"$dir/output.pdf\";; esac\n"
            : $"case \"$1\" in --job-json-file=*) echo 'SENTINEL stderr' >&2; exit {fault[4..]};; esac\n";
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\n" + body + "exec qpdf \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await PostAsync(test, await FixtureAsync(test));
        await ProblemAsync(test, response, 500);
    }

    [TestMethod]
    public async Task JobAndArguments_UseFixedOptionsPrivatePathsPermissionsAndOutputCleanup()
    {
        if (!Linux()) return;
        await using var test = new PdfTestContext();
        var wrapper = Path.Combine(test.Root, "qpdf.sh");
        await File.WriteAllTextAsync(wrapper, $$"""
        #!/bin/sh
        printf '%s\n' "$@" >> '{{test.Root}}/arguments'
        case "$1" in
        --job-json-file=*)
          job=${1#--job-json-file=}; dir=${job%/*}
          cp "$job" '{{test.Root}}/captured-job.json'
          cp "$dir/update.json" '{{test.Root}}/captured-update.json'
          stat -c '%a' "$dir" "$dir/input.pdf" "$dir/output.pdf" "$dir/update.json" "$job" > '{{test.Root}}/permissions'
          cat /proc/$$/limits > '{{test.Root}}/limits'
          ;;
        --is-encrypted)
          case "$2" in */output.pdf)
            dir=${2%/*}
            test ! -e "$dir/input.pdf" && test ! -e "$dir/update.json" && test ! -e "$dir/job.json" && test ! -e "$dir/input-objects.json" || exit 9
          ;; esac
          ;;
        esac
        exec qpdf "$@"
        """ + "\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await PostAsync(test, await FixtureAsync(test));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var job = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(test.Root, "captured-job.json")));
        CollectionAssert.AreEquivalent(new[] { "inputFile", "outputFile", "removeInfo", "removeMetadata", "updateFromJson" }, job.RootElement.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.AreEqual("", job.RootElement.GetProperty("removeInfo").GetString());
        Assert.AreEqual("", job.RootElement.GetProperty("removeMetadata").GetString());
        var args = await File.ReadAllTextAsync(Path.Combine(test.Root, "arguments"));
        foreach (var forbidden in new[] { "MARKER", "private-name", "removeAttachment", "preserve-unreferenced" }) Assert.IsFalse(args.Contains(forbidden, StringComparison.Ordinal));
        Assert.AreEqual(1, args.Split("--json-key=attachments", StringSplitOptions.None).Length - 1);
        CollectionAssert.AreEqual(new[] { "700", "600", "600", "600", "600" }, await File.ReadAllLinesAsync(Path.Combine(test.Root, "permissions")));
        var limits = await File.ReadAllTextAsync(Path.Combine(test.Root, "limits"));
        Assert.IsTrue(limits.Contains("570425344", StringComparison.Ordinal));
        Assert.IsTrue(limits.Contains("56623104", StringComparison.Ordinal));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("pdf")]
    [DataRow("json")]
    public async Task FsizeBoundary_RejectsEqualityAndOneBelow_AcceptsOneAbove(string kind)
    {
        if (!Linux()) return;
        long size;
        await using (var calibration = new PdfTestContext())
        {
            using var response = await PostAsync(calibration, PdfTestContext.Fixture);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var output = await response.Content.ReadAsByteArrayAsync();
            size = output.Length;
            if (kind == "json")
            {
                var path = Path.Combine(calibration.Root, "output.pdf");
                await File.WriteAllBytesAsync(path, output);
                var result = await calibration.QpdfAsync("--json=2", "--json-key=qpdf", "--json-key=attachments", "--json-stream-data=none", "--decode-level=none", path);
                Assert.AreEqual(0, result.ExitCode);
                size = Encoding.UTF8.GetByteCount(result.Output);
            }
        }
        foreach (var delta in new[] { -1, 0, 1 })
        {
            await using var test = new PdfTestContext(new() { [kind == "pdf" ? "Pdf:CleanOutputLimitBytes" : "Pdf:CleanJsonLimitBytes"] = (size + delta).ToString(System.Globalization.CultureInfo.InvariantCulture) });
            using var response = await PostAsync(test, PdfTestContext.Fixture);
            if (delta <= 0) await ProblemAsync(test, response, 422, "too-complex");
            else { Assert.AreEqual(HttpStatusCode.OK, response.StatusCode); test.AssertClean(); }
        }
    }

    [TestMethod]
    public async Task Success_UsesNineCalls_OneJob_CombinedOutputJson_AndOnlyPrivatePaths()
    {
        if (!Linux()) return;
        using var recorder = new BlockingQpdf("never-match");
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = recorder.Executable });
        using var response = await PostAsync(test, await FixtureAsync(test));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { "--is-encrypted", "--check", "--show-npages", "--json=2", "job", "--is-encrypted", "--check", "--show-npages", "--json=2" },
            recorder.Calls.Select(call => call.StartsWith("--job-json-file=", StringComparison.Ordinal) ? "job" : call).ToArray());
        test.AssertClean();
    }

    [SupportedOSPlatformGuard("linux")]
    internal static bool Linux()
    {
        if (OperatingSystem.IsLinux()) return true;
        Assert.Inconclusive("cleanはLinux限定です。");
        return false;
    }
}
