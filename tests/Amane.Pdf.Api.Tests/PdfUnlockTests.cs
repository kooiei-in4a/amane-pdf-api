using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amane.Pdf.Api;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfUnlockTests
{
    internal const string UserPassword = "unlock-user-fixture";
    internal const string OwnerPassword = "unlock-owner-fixture";
    internal const string Sentinel = "PDF-CONTENT-SENTINEL";

    [TestMethod]
    [DataRow("aes256", false)]
    [DataRow("aes256", true)]
    [DataRow("aes128", false)]
    [DataRow("aes128", true)]
    [DataRow("rc4-128", false)]
    [DataRow("rc4-128", true)]
    [DataRow("rc4-40", false)]
    [DataRow("rc4-40", true)]
    public async Task Unlock_WithUserOrOwnerPassword_RemovesEncryptionAndPreservesPages(string algorithm, bool owner)
    {
        await using var test = new PdfTestContext();
        var input = await test.CreateRotationMarkedPdfAsync(3);
        var encrypted = await test.CreateEncryptedPdfAsync(UserPassword, OwnerPassword, algorithm, input);
        using var form = PdfTestContext.Form(encrypted, owner ? OwnerPassword : UserPassword, "../../private-name.pdf");
        using var response = await test.Client.PostAsync("/api/pdf/unlock", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("unlocked.pdf", response.Content.Headers.ContentDisposition?.FileName);
        var output = await response.Content.ReadAsByteArrayAsync();
        await test.AssertValidPdfAsync(output, 3);
        CollectionAssert.AreEqual(new[] { 0, 90, 180 }, await test.ReadPageRotationsAsync(output));
        var path = Path.Combine(test.Root, "unlocked.pdf");
        await File.WriteAllBytesAsync(path, output);
        Assert.AreEqual(2, (await test.QpdfAsync("--is-encrypted", path)).ExitCode);
        Assert.AreEqual(2, (await test.QpdfAsync("--requires-password", path)).ExitCode);
        using var document = JsonDocument.Parse((await test.QpdfAsync("--json", path)).Output);
        Assert.IsFalse(document.RootElement.GetProperty("encrypt").GetProperty("encrypted").GetBoolean());
        Assert.IsFalse(document.RootElement.GetProperty("qpdf")[1].GetProperty("trailer").GetProperty("value")
            .TryGetProperty("/Encrypt", out _));
        AssertNoExposure(test);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("日本語の検証用é🔒", false)]
    [DataRow("日本語の検証用é🔒", true)]
    [DataRow("  spaces-are-preserved  ", false)]
    [DataRow("json-fixture-!\"\\$;`literal`", false)]
    public async Task Unlock_PreservesUnicodeSpacesAndJsonCharacters(string password, bool owner)
    {
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync(owner ? UserPassword : password, owner ? password : OwnerPassword);
        using var form = PdfTestContext.Form(encrypted, password);
        using var response = await test.Client.PostAsync("/api/pdf/unlock", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
        AssertNoExposure(test, password);
        test.AssertClean();
    }

    [TestMethod]
    public async Task Unlock_127Utf8Bytes_IsAcceptedWithoutTrimming()
    {
        var password = new string('a', 123) + "🔒";
        Assert.AreEqual(127, Encoding.UTF8.GetByteCount(password));
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync(password, OwnerPassword);
        using var form = PdfTestContext.Form(encrypted, password);
        using var response = await test.Client.PostAsync("/api/pdf/unlock", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    public async Task Unlock_WrongPassword_ReturnsFixedReason()
    {
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync(UserPassword, OwnerPassword);
        using var form = PdfTestContext.Form(encrypted, "wrong-unlock-fixture");
        await AssertProblemAsync(test, form, 422, "wrong-password", "パスワードが正しくありません。");
    }

    [TestMethod]
    [DataRow(OwnerPassword)]
    [DataRow("wrong-unlock-fixture")]
    public async Task Unlock_OwnerOnlyRestrictions_AreRejectedBeforePasswordAuthentication(string password)
    {
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync("", OwnerPassword);
        using var form = PdfTestContext.Form(encrypted, password);
        await AssertProblemAsync(test, form, 422, "no-open-password", "開くためのパスワードが設定されていないPDFは解除できません。");
    }

    [TestMethod]
    [DataRow("plain", "not-encrypted")]
    [DataRow("empty", "invalid-pdf")]
    [DataRow("text", "invalid-pdf")]
    [DataRow("broken", "invalid-pdf")]
    [DataRow("warning", "invalid-pdf")]
    [DataRow("truncated", "invalid-pdf")]
    public async Task Unlock_UnencryptedOrInvalidInput_ReturnsCorrectReason(string kind, string reason)
    {
        await using var test = new PdfTestContext();
        var input = kind switch
        {
            "plain" => PdfTestContext.Fixture,
            "empty" => [],
            "text" => Encoding.UTF8.GetBytes(Sentinel),
            "broken" => Encoding.UTF8.GetBytes("%PDF-1.4\n" + Sentinel + "\n%%EOF"),
            "warning" => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(PdfTestContext.Fixture).Replace("/Length 41", "/Length 39", StringComparison.Ordinal)),
            _ => (await test.CreateEncryptedPdfAsync(UserPassword, OwnerPassword))[..128]
        };
        using var form = PdfTestContext.Form(input, UserPassword);
        await AssertProblemAsync(test, form, 422, reason);
    }

    [TestMethod]
    [DataRow("Root")]
    [DataRow("Pages")]
    public async Task Unlock_BrokenReferencesFailingAfterAuthentication_Return422InvalidPdf(string reference)
    {
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync(UserPassword, OwnerPassword);
        var text = Encoding.Latin1.GetString(encrypted);
        var damaged = Regex.Replace(text, @"/" + reference + @" \d+ 0 R", "/" + reference + " 0 0 R");
        Assert.AreNotEqual(text, damaged);
        using var files = new TemporaryPdfFiles(test.Root);
        await File.WriteAllBytesAsync(files.InputPath, Encoding.Latin1.GetBytes(damaged));
        Assert.AreEqual(0, (await test.QpdfAsync("--requires-password", files.InputPath)).ExitCode);
        var authenticated = await PdfTestContext.RunQpdfJobAsync(files.JobPath, PasswordJob(files.InputPath, "requiresPassword"));
        if (reference == "Root") Assert.AreEqual(2, authenticated);
        else
        {
            // qpdf 11.9 detects broken Pages during --check; 12.3 detects it during authentication.
            Assert.IsTrue(authenticated is 2 or 3);
            if (authenticated == 3)
                Assert.AreEqual(2, await PdfTestContext.RunQpdfJobAsync(files.UnlockCheckJobPath, PasswordJob(files.InputPath, "check")));
        }
        using var form = PdfTestContext.Form(Encoding.Latin1.GetBytes(damaged), UserPassword);
        await AssertProblemAsync(test, form, 422, "invalid-pdf");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Unlock_AuthenticatedStructureErrorOrWarning_IsRejected(bool warning)
    {
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync(UserPassword, OwnerPassword);
        byte[] damaged;
        if (warning)
        {
            var text = Encoding.Latin1.GetString(encrypted);
            damaged = Encoding.Latin1.GetBytes(Regex.Replace(text, @"(startxref\s+)(\d+)", match =>
                match.Groups[1].Value + (int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 1)));
        }
        else
        {
            damaged = (byte[])encrypted.Clone();
            var stream = Regex.Match(Encoding.Latin1.GetString(damaged), @"(?<!end)stream\r?\n");
            Assert.IsTrue(stream.Success);
            // Flip an IV byte: the decoded Flate header becomes invalid, without altering authentication.
            damaged[stream.Index + stream.Length] ^= 0xff;
        }
        using var files = new TemporaryPdfFiles(test.Root);
        await File.WriteAllBytesAsync(files.InputPath, damaged);
        Assert.AreEqual(3, await PdfTestContext.RunQpdfJobAsync(files.JobPath, PasswordJob(files.InputPath, "requiresPassword")));
        Assert.AreEqual(warning ? 3 : 2, await PdfTestContext.RunQpdfJobAsync(files.UnlockCheckJobPath, PasswordJob(files.InputPath, "check")));
        using var form = PdfTestContext.Form(damaged, UserPassword);
        await AssertProblemAsync(test, form, 422, "invalid-pdf");
    }

    [TestMethod]
    [DataRow("file")]
    [DataRow("password")]
    [DataRow("duplicate-file")]
    [DataRow("duplicate-password")]
    [DataRow("unexpected")]
    public async Task Unlock_MissingDuplicateOrUnexpectedFields_Return400BeforeQpdf(string kind)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var form = PdfTestContext.Form(kind == "file" ? null : PdfTestContext.Fixture, kind == "password" ? null : UserPassword);
        if (kind == "duplicate-file") form.Add(new ByteArrayContent(PdfTestContext.Fixture), "file", "second.pdf");
        if (kind == "duplicate-password") form.Add(new StringContent(UserPassword), "password");
        if (kind == "unexpected") form.Add(new StringContent("unexpected"), "extra");
        await AssertProblemAsync(test, form, 400);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("control\0fixture")]
    [DataRow("line\nfixture")]
    [DataRow("128-bytes")]
    [DataRow("invalid-utf8")]
    public async Task Unlock_InvalidPassword_Return400BeforeQpdf(string kind)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var form = PdfTestContext.Form(PdfTestContext.Fixture, password: null);
        var bytes = kind switch
        {
            "128-bytes" => Encoding.UTF8.GetBytes(new string('a', 124) + "🔒"),
            "invalid-utf8" => new byte[] { 0xff },
            _ => Encoding.UTF8.GetBytes(kind)
        };
        form.Add(new ByteArrayContent(bytes), "password");
        await AssertProblemAsync(test, form, 400);
    }

    [TestMethod]
    [DataRow("application/json", "{}")]
    [DataRow("multipart/form-data", "broken")]
    [DataRow("multipart/form-data; boundary=missing", "broken")]
    public async Task Unlock_MalformedMultipart_Return400(string contentType, string body)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var content = new StringContent(body);
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        await AssertProblemAsync(test, content, 400);
    }

    [TestMethod]
    [DataRow("--requires-password", 9, 500)]
    [DataRow("--job-json-file=*/job.json", 9, 500)]
    [DataRow("--job-json-file=*/unlock-check.json", 2, 422)]
    [DataRow("--job-json-file=*/unlock-check.json", 3, 422)]
    [DataRow("--job-json-file=*/unlock-check.json", 9, 500)]
    [DataRow("--job-json-file=*/unlock-decrypt.json", 2, 500)]
    [DataRow("--job-json-file=*/unlock-decrypt.json", 3, 422)]
    [DataRow("--job-json-file=*/unlock-decrypt.json", 9, 500)]
    [DataRow("--is-encrypted", 0, 500)]
    [DataRow("--is-encrypted", 3, 500)]
    [DataRow("--is-encrypted", 9, 500)]
    [DataRow("--check", 2, 500)]
    [DataRow("--check", 3, 500)]
    [DataRow("--check", 9, 500)]
    public async Task Unlock_ExitCodesAreMappedByStage_WithoutExposingOutput(string pattern, int exitCode, int status)
    {
        if (!RequireLinux()) return;
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync(UserPassword, OwnerPassword);
        var wrapper = CreateShim(test.Root, $"case \"$1\" in\n{pattern}) printf '%s\\n' '{UserPassword} {OwnerPassword} {Sentinel}' '{test.Root}'; printf '%s\\n' '{UserPassword} {Sentinel}' >&2; exit {exitCode};;\nesac\nexec qpdf \"$@\"");
        await using var api = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
        using var form = PdfTestContext.Form(encrypted, UserPassword);
        await AssertProblemAsync(api, form, status, status == 422 ? "invalid-pdf" : null);
        AssertNoExposure(api, test.Root);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("encrypted")]
    [DataRow("broken")]
    [DataRow("warning")]
    [DataRow("missing")]
    public async Task Unlock_InvalidOutput_Returns500InsteadOf422(string kind)
    {
        if (!RequireLinux()) return;
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync(UserPassword, OwnerPassword);
        var replacement = Path.Combine(test.Root, "replacement.pdf");
        await File.WriteAllBytesAsync(replacement, kind switch
        {
            "empty" => [],
            "encrypted" => encrypted,
            "warning" => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(PdfTestContext.Fixture).Replace("/Length 41", "/Length 39", StringComparison.Ordinal)),
            _ => Encoding.UTF8.GetBytes(Sentinel)
        });
        var mutation = kind == "missing" ? "rm \"$output\"" : $"cp '{replacement}' \"$output\"";
        var wrapper = CreateShim(test.Root, "case \"$1\" in\n--job-json-file=*/unlock-decrypt.json)\n" +
            "qpdf \"$@\" || exit $?\njob=${1#--job-json-file=}\noutput=${job%/*}/output.pdf\n" + mutation +
            "\nexit 0;;\nesac\nexec qpdf \"$@\"");
        await using var api = new PdfTestContext(new() { ["Pdf:QpdfPath"] = wrapper });
        using var form = PdfTestContext.Form(encrypted, UserPassword);
        await AssertProblemAsync(api, form, 500, title: "PDF処理に失敗しました。");
    }

    [TestMethod]
    public async Task Unlock_MissingExecutable_ReturnsSanitized500AndCleansFiles()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/missing/qpdf" });
        using var form = PdfTestContext.Form(PdfTestContext.Fixture, UserPassword);
        await AssertProblemAsync(test, form, 500);
    }

    [TestMethod]
    public async Task Unlock_AllJobsArePrivate_AndNoPasswordOrFilenameAppearsInAnyArgv()
    {
        if (!RequireLinux()) return;
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync(UserPassword, OwnerPassword, "aes128");
        var inspection = Path.Combine(test.Root, "inspection");
        Directory.CreateDirectory(inspection, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var wrapper = CreateShim(test.Root, $"printf '%s\\n' \"$@\" >> '{test.Root}/argv'\n" +
            $"case \"$1\" in\n--job-json-file=*) job=${{1#--job-json-file=}}; cp \"$job\" '{inspection}/'\"${{job##*/}}\";;\nesac\nexec qpdf \"$@\"");
        using (var files = new TemporaryPdfFiles(test.TempRoot))
        {
            await using (var stream = TemporaryPdfFiles.CreatePrivateFile(files.InputPath))
            {
                await stream.WriteAsync(encrypted);
            }
            await new QpdfProcessor(Options.Create(new PdfOptions { QpdfPath = wrapper })).UnlockAsync(files, OwnerPassword, CancellationToken.None);
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(files.DirectoryPath));
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.InputPath));
            foreach (var path in new[] { files.JobPath, files.UnlockCheckJobPath, files.UnlockDecryptJobPath })
            {
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(inspection, Path.GetFileName(path))));
                Assert.AreEqual(OwnerPassword, document.RootElement.GetProperty("password").GetString());
                Assert.AreEqual("unicode", document.RootElement.GetProperty("passwordMode").GetString());
            }
        }
        var argv = await File.ReadAllTextAsync(Path.Combine(test.Root, "argv"));
        Assert.AreEqual(3, argv.Split('\n').Count(line => line.StartsWith("--job-json-file=", StringComparison.Ordinal)));
        Assert.IsFalse(argv.Contains(UserPassword, StringComparison.Ordinal));
        Assert.IsFalse(argv.Contains(OwnerPassword, StringComparison.Ordinal));
        Assert.IsFalse(argv.Contains("private-name.pdf", StringComparison.Ordinal));
        test.AssertClean();
    }

    internal static Dictionary<string, object> PasswordJob(string input, string operation) => new()
    {
        ["inputFile"] = input, ["password"] = UserPassword, ["passwordMode"] = "unicode", [operation] = ""
    };

    internal static async Task AssertProblemAsync(PdfTestContext test, HttpContent form, int status,
        string? reason = null, string? title = null)
    {
        using var response = await test.Client.PostAsync("/api/pdf/unlock", form);
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.AreEqual(status, problem.RootElement.GetProperty("status").GetInt32());
        if (reason is null) Assert.IsFalse(problem.RootElement.TryGetProperty("reason", out _));
        else Assert.AreEqual(reason, problem.RootElement.GetProperty("reason").GetString());
        if (title is not null) Assert.AreEqual(title, problem.RootElement.GetProperty("title").GetString());
        AssertNoExposure(test, body: body);
        test.AssertClean();
    }

    internal static void AssertNoExposure(PdfTestContext test, string? extra = null, string body = "")
    {
        foreach (var value in new[] { UserPassword, OwnerPassword, Sentinel, test.Root, "private-name.pdf", "wrong-unlock-fixture", extra })
        {
            if (string.IsNullOrEmpty(value)) continue;
            Assert.IsFalse(body.Contains(value, StringComparison.Ordinal));
            Assert.IsFalse(test.Logs.Any(log => log.Contains(value, StringComparison.Ordinal)));
        }
        Assert.IsFalse(body.Contains("stderr", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("stack", StringComparison.OrdinalIgnoreCase));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal static string CreateShim(string root, string body)
    {
        var path = Path.Combine(root, "unlock-qpdf.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    internal static bool RequireLinux()
    {
        if (OperatingSystem.IsLinux()) return true;
        Assert.Inconclusive("Linuxのprocess argv、Unix権限、process treeを検証します。");
        return false;
    }
}
