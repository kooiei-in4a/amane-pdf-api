using System.Net;
using System.Text;
using System.Text.Json;
using Amane.Pdf.Api;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfRestrictTests
{
    private const string Owner = "restrict-owner-日本語-!\"\\";
    private const string User = "restrict-user-é🔒";

    private static MultipartFormDataContent Form(byte[]? pdf = null, string? user = null,
        string print = "none", string copy = "allow", string edit = "allow", string owner = Owner,
        string? omit = null, string? replace = null, HttpContent? replacement = null)
    {
        var form = new MultipartFormDataContent();
        void Add(string name, HttpContent content, string? filename = null)
        {
            if (name == omit) { content.Dispose(); return; }
            if (name == replace) { content.Dispose(); content = replacement!; }
            if (filename is null) form.Add(content, name);
            else form.Add(content, name, filename);
        }
        Add("file", new ByteArrayContent(pdf ?? PdfTestContext.Fixture), "sample.pdf");
        Add("ownerPassword", new StringContent(owner));
        if (user is not null) Add("userPassword", new StringContent(user));
        Add("print", new StringContent(print));
        Add("copy", new StringContent(copy));
        Add("edit", new StringContent(edit));
        return form;
    }

    private static async Task AssertSafeAsync(PdfTestContext test, HttpResponseMessage response, params string[] extraSecrets)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            foreach (var secret in new[] { Owner, User }.Concat(extraSecrets).Where(s => s.Length > 0))
                Assert.IsFalse(body.Contains(secret, StringComparison.Ordinal));
        }
        foreach (var secret in new[] { Owner, User }.Concat(extraSecrets).Where(s => s.Length > 0))
            Assert.IsFalse(test.Logs.Any(log => log.Contains(secret, StringComparison.Ordinal)));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("none", "allow", "allow")]
    [DataRow("low", "deny", "deny")]
    [DataRow("full", "deny", "allow")]
    [DataRow("full", "allow", "deny")]
    [DataRow("none", "deny", "deny")]
    public async Task Restrict_MapsPermissions_PreservesPagesAndContent_WithoutOpenPassword(string print, string copy, string edit)
    {
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(3);
        using var form = Form(input, print: print, copy: copy, edit: edit);
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("attachment; filename=restricted.pdf", response.Content.Headers.ContentDisposition?.ToString());
        var output = Path.Combine(test.Root, "restricted.pdf");
        await File.WriteAllBytesAsync(output, await response.Content.ReadAsByteArrayAsync());
        var passwordFile = Path.Combine(test.Root, "owner.json");
        await File.WriteAllTextAsync(passwordFile, Owner);
        var auth = "--password-file=" + passwordFile;
        var encryption = await test.QpdfAsync(auth, "--show-encryption", output);
        Assert.AreEqual(0, encryption.ExitCode);
        StringAssert.Contains(encryption.Output, "R = 6");
        StringAssert.Contains(encryption.Output, "AESv3");
        StringAssert.Contains(encryption.Output, "Supplied password is owner password");
        StringAssert.Contains(encryption.Output, "extract for accessibility: allowed");
        StringAssert.Contains(encryption.Output, "extract for any purpose: " + (copy == "allow" ? "allowed" : "not allowed"));
        StringAssert.Contains(encryption.Output, "print low resolution: " + (print != "none" ? "allowed" : "not allowed"));
        StringAssert.Contains(encryption.Output, "print high resolution: " + (print == "full" ? "allowed" : "not allowed"));
        foreach (var permission in new[] { "document assembly", "forms", "annotations", "other", "anything" })
            StringAssert.Contains(encryption.Output, "modify " + permission + ": " + (edit == "allow" ? "allowed" : "not allowed"));
        Assert.AreEqual(3, (await test.QpdfAsync("--requires-password", output)).ExitCode);
        Assert.AreEqual(0, (await test.QpdfAsync("--password=", "--check", output)).ExitCode);
        Assert.AreEqual("3", (await test.QpdfAsync(auth, "--show-npages", output)).Output.Trim());
        // Compare page content streams after decrypting, so encryption randomness is irrelevant.
        var source = Path.Combine(test.Root, "source.pdf");
        var decrypted = Path.Combine(test.Root, "decrypted.pdf");
        await File.WriteAllBytesAsync(source, input);
        Assert.AreEqual(0, (await test.QpdfAsync(auth, "--decrypt", output, decrypted)).ExitCode);
        var before = JsonDocument.Parse((await test.QpdfAsync("--json", "--json-stream-data=inline", source)).Output);
        var after = JsonDocument.Parse((await test.QpdfAsync("--json", "--json-stream-data=inline", decrypted)).Output);
        using (before)
        using (after)
        {
            static string[] Streams(JsonDocument doc) => doc.RootElement.GetProperty("qpdf")[1].EnumerateObject()
                .Where(obj => obj.Value.TryGetProperty("stream", out _))
                .Select(obj => obj.Value.GetProperty("stream").GetProperty("data").GetString()!).Order().ToArray();
            CollectionAssert.AreEqual(Streams(before), Streams(after));
        }
        await AssertSafeAsync(test, response);
    }

    [TestMethod]
    [DataRow(User, Owner)]
    [DataRow(" padded-user ", " padded-owner ")]
    [DataRow("e\u0301", "é")]
    [DataRow("ASCII-user", "ASCII-owner")]
    public async Task Restrict_AuthenticatesDistinctUtf8Passwords_WithoutTrimmingOrNormalization(string user, string owner)
    {
        await using var test = new PdfTestContext();
        using var form = Form(user: user, owner: owner);
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var output = Path.Combine(test.Root, "restricted.pdf");
        await File.WriteAllBytesAsync(output, await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(0, (await test.QpdfAsync("--requires-password", output)).ExitCode);
        var passwordFile = Path.Combine(test.Root, "password.json");
        foreach (var password in new[] { user, owner, "wrong-restrict-password" })
        {
            await File.WriteAllTextAsync(passwordFile, password);
            Assert.AreEqual(password == "wrong-restrict-password" ? 2 : 0,
                (await test.QpdfAsync("--password-file=" + passwordFile, "--check", output)).ExitCode);
        }
        await AssertSafeAsync(test, response, user, owner);
    }

    [TestMethod]
    [DataRow("file")]
    [DataRow("ownerPassword")]
    [DataRow("print")]
    [DataRow("copy")]
    [DataRow("edit")]
    public async Task MissingRequiredField_Is400(string field)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/missing/qpdf" });
        using var form = Form(omit: field);
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSafeAsync(test, response);
    }

    [TestMethod]
    [DataRow("file")]
    [DataRow("ownerPassword")]
    [DataRow("userPassword")]
    [DataRow("print")]
    [DataRow("copy")]
    [DataRow("edit")]
    [DataRow("unknown")]
    public async Task DuplicateOrUnknownField_Is400(string field)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/missing/qpdf" });
        using var form = Form(user: User);
        if (field == "file") form.Add(new ByteArrayContent(PdfTestContext.Fixture), field, "other.pdf");
        else form.Add(new StringContent("unexpected"), field);
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSafeAsync(test, response);
    }

    [TestMethod]
    [DataRow("print", "FULL")]
    [DataRow("print", "")]
    [DataRow("print", "full ")]
    [DataRow("print", "123456789")]
    [DataRow("copy", "ALLOW")]
    [DataRow("copy", "")]
    [DataRow("copy", "123456789")]
    [DataRow("edit", "DENY")]
    [DataRow("edit", "")]
    [DataRow("edit", "123456789")]
    [DataRow("ownerPassword", "")]
    [DataRow("userPassword", "")]
    [DataRow("ownerPassword", "control\npassword")]
    [DataRow("userPassword", "control\u007fpassword")]
    [DataRow("ownerPassword", "control\u0085password")]
    [DataRow("ownerPassword", Owner)]
    public async Task InvalidField_Is400(string field, string value)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/missing/qpdf" });
        using var form = Form(user: field == "ownerPassword" && value == Owner ? Owner : User,
            replace: field, replacement: new StringContent(value));
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSafeAsync(test, response, value);
    }

    [TestMethod]
    [DataRow("ownerPassword", false)]
    [DataRow("ownerPassword", true)]
    [DataRow("userPassword", false)]
    [DataRow("userPassword", true)]
    public async Task InvalidPasswordBytes_Is400(string field, bool invalidUtf8)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/missing/qpdf" });
        using var form = Form(user: User, replace: field,
            replacement: new ByteArrayContent(invalidUtf8 ? [0xc3, 0x28] : Encoding.UTF8.GetBytes(new string('é', 64))));
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSafeAsync(test, response);
    }

    [TestMethod]
    [DataRow("file")]
    [DataRow("ownerPassword")]
    [DataRow("userPassword")]
    [DataRow("print")]
    [DataRow("copy")]
    [DataRow("edit")]
    public async Task WrongPartKind_Is400(string field)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/missing/qpdf" });
        using var form = Form(user: User);
        var part = form.Single(p => p.Headers.ContentDisposition?.Name?.Trim('"') == field);
        if (field == "file")
        {
            part.Headers.ContentDisposition!.FileName = null;
            part.Headers.ContentDisposition.FileNameStar = null;
        }
        else part.Headers.ContentDisposition!.FileName = "field.txt";
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSafeAsync(test, response);
    }

    [TestMethod]
    public async Task AllAllowedAndQuery_Are400BeforeQpdf()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/missing/qpdf" });
        using var form = Form(print: "full");
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertSafeAsync(test, response);
        using var valid = Form();
        using var queryResponse = await test.Client.PostAsync("/api/pdf/restrict?print=none", valid);
        Assert.AreEqual(HttpStatusCode.BadRequest, queryResponse.StatusCode);
        await AssertSafeAsync(test, queryResponse);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("damaged")]
    [DataRow("warning")]
    [DataRow("protect")]
    [DataRow("restrict")]
    public async Task InvalidOrEncryptedPdf_IsCommon422(string kind)
    {
        await using var test = new PdfTestContext();
        byte[] input;
        if (kind is "protect" or "restrict")
        {
            using var source = kind == "protect" ? PdfTestContext.Form(PdfTestContext.Fixture, User) : Form();
            using var result = await test.Client.PostAsync("/api/pdf/" + kind, source);
            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
            input = await result.Content.ReadAsByteArrayAsync();
            await AssertSafeAsync(test, result);
        }
        else input = kind switch
        {
            "empty" => [],
            "warning" => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(PdfTestContext.Fixture).Replace("/Length 41", "/Length 39", StringComparison.Ordinal)),
            _ => Encoding.UTF8.GetBytes("damaged-pdf-sentinel")
        };
        using var form = Form(input);
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.IsFalse(problem.RootElement.TryGetProperty("reason", out _));
        Assert.AreEqual("このPDFは処理できません。PDFの破損・パスワード設定や、画像が大きすぎないか確認してください。", problem.RootElement.GetProperty("title").GetString());
        await AssertSafeAsync(test, response, "damaged-pdf-sentinel");
    }

    [TestMethod]
    public async Task OversizedFileAndEpilogue_Are413()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString() });
        using var form = Form([.. PdfTestContext.Fixture, 0]);
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertSafeAsync(test, response);
        using var normal = Form();
        var bytes = await normal.ReadAsByteArrayAsync();
        using var epilogue = new ByteArrayContent([.. bytes, .. new byte[70 * 1024]]);
        epilogue.Headers.ContentType = normal.Headers.ContentType;
        using var extraResponse = await test.Client.PostAsync("/api/pdf/restrict", epilogue);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, extraResponse.StatusCode);
        await AssertSafeAsync(test, extraResponse);
    }

    [TestMethod]
    public async Task Password127BytesAndFileAtLimit_AreAccepted()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString() });
        var owner = new string('é', 63) + "x";
        var user = new string('u', 127);
        using var form = Form(user: user, owner: owner);
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await AssertSafeAsync(test, response, user, owner);
    }

    [TestMethod]
    public async Task EpilogueWithoutContentLength_IsCountedBeforeQpdf()
    {
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxFileBytes"] = PdfTestContext.Fixture.Length.ToString(), ["Pdf:QpdfPath"] = "/missing/qpdf"
        });
        using var form = Form();
        // Keep individual MIME headers/epilogue below MultipartReader's own limits.
        // The combined envelope crosses MaxRequestBytes only in the epilogue.
        foreach (var part in form.Take(4)) part.Headers.Add("X-Padding", new string('H', 15000));
        var data = await form.ReadAsByteArrayAsync();
        var context = new DefaultHttpContext { RequestServices = test.Factory.Services };
        context.Request.ContentType = form.Headers.ContentType!.ToString();
        context.Request.Body = new MemoryStream([.. data, .. new byte[8 * 1024]]);
        context.Response.Body = new MemoryStream();
        await PdfRestrictEndpoint.HandleAsync(context, test.Factory.Services.GetRequiredService<QpdfProcessor>(),
            test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>());
        Assert.AreEqual(413, context.Response.StatusCode);
        test.AssertClean();
        Assert.IsFalse(test.Logs.Any(log => log.Contains(Owner, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Cancellation_StopsProcessTreeAndCleansFiles()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Linux process treeを検証します。"); return; }
        using var blocking = new BlockingQpdf();
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = blocking.Executable });
        using var form = Form(user: User);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = test.Client.PostAsync("/api/pdf/restrict", form, cancellation.Token);
        try
        {
            await blocking.WaitForJobsAsync(1);
            cancellation.Cancel();
            try { using var response = await request; }
            catch (OperationCanceledException) { }
        }
        finally
        {
            cancellation.Cancel();
            try { using var response = await request; }
            catch (OperationCanceledException) { }
        }
        await blocking.AssertStoppedAsync();
        for (var attempt = 0; attempt < 100 && Directory.Exists(test.TempRoot) && Directory.EnumerateFileSystemEntries(test.TempRoot).Any(); attempt++)
            await Task.Delay(20);
        test.AssertClean();
        foreach (var secret in new[] { Owner, User })
            Assert.IsFalse(test.Logs.Any(log => log.Contains(secret, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Job_UsesPrivateFiles_ExplicitPermissions_AndSafeArgv()
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Unix権限を検証します。"); return; }
        await using var test = new PdfTestContext();
        var wrapper = Path.Combine(test.Root, "wrapper.sh");
        await File.WriteAllTextAsync(wrapper, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{test.Root}/argv'\ncp \"${{1#--job-json-file=}}\" '{test.Root}/inspection.json'\nexec qpdf \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var restriction = new PdfRestriction(Owner, User, "low", "deny", "deny");
        Assert.IsFalse(restriction.ToString().Contains(Owner, StringComparison.Ordinal));
        Assert.IsFalse(restriction.ToString().Contains(User, StringComparison.Ordinal));
        using (var files = new TemporaryPdfFiles(test.TempRoot))
        {
            await using (var stream = TemporaryPdfFiles.CreatePrivateFile(files.InputPath)) { await stream.WriteAsync(PdfTestContext.Fixture); }
            var processor = new QpdfProcessor(Options.Create(new PdfOptions { QpdfPath = wrapper }));
            await processor.RestrictAsync(files, restriction, CancellationToken.None);
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(files.DirectoryPath));
            foreach (var path in new[] { files.JobPath, files.InputPath })
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            using var job = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(test.Root, "inspection.json")));
            Assert.AreEqual("unicode", job.RootElement.GetProperty("passwordMode").GetString());
            var encrypt = job.RootElement.GetProperty("encrypt");
            Assert.AreEqual(Owner, encrypt.GetProperty("ownerPassword").GetString());
            Assert.AreEqual(User, encrypt.GetProperty("userPassword").GetString());
            var flags = encrypt.GetProperty("256bit");
            Assert.AreEqual(3, flags.EnumerateObject().Count());
            Assert.AreEqual("low", flags.GetProperty("print").GetString());
            Assert.AreEqual("n", flags.GetProperty("extract").GetString());
            Assert.AreEqual("none", flags.GetProperty("modify").GetString());
            var argv = await File.ReadAllTextAsync(Path.Combine(test.Root, "argv"));
            StringAssert.StartsWith(argv, "--job-json-file=");
            Assert.IsFalse(argv.Contains(Owner, StringComparison.Ordinal));
            Assert.IsFalse(argv.Contains(User, StringComparison.Ordinal));
        }
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailureAndTimeout_KeepCommonResponsesAndCleanFiles(bool timeout)
    {
        if (OperatingSystem.IsWindows()) { Assert.Inconclusive("Linux process treeを検証します。"); return; }
        using var blocking = new BlockingQpdf();
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:QpdfPath"] = timeout ? blocking.Executable : "/missing/qpdf",
            ["Pdf:QpdfTimeoutSeconds"] = "1"
        });
        using var form = Form(user: User);
        using var response = await test.Client.PostAsync("/api/pdf/restrict", form);
        Assert.AreEqual(timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.InternalServerError, response.StatusCode);
        await blocking.AssertStoppedAsync();
        await AssertSafeAsync(test, response);
    }
}
