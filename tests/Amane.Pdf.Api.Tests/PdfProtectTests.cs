using System.Net;
using Amane.Pdf.Api;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfProtectTests
{
    [TestMethod]
    public async Task Job_UsesPrivateFiles_IndependentOwnerPassword_AndSafeArgv()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Linuxのprocess argvとUnix権限を検証します。");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "amane-qpdf-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wrapper = Path.Combine(root, "qpdf-wrapper.sh");
            await File.WriteAllTextAsync(wrapper, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{root}/argv'\ncp \"${{1#--job-json-file=}}\" '{root}/inspection.json'\nexec qpdf \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var files = new TemporaryPdfFiles(Path.Combine(root, "jobs"));
            await using (var input = TemporaryPdfFiles.CreatePrivateFile(files.InputPath))
            {
                await input.WriteAsync(PdfTestContext.Fixture);
            }
            var processor = new QpdfProcessor(Options.Create(new PdfOptions { QpdfPath = wrapper }));
            await processor.ProtectAsync(files, "argv-fixture-password", CancellationToken.None);
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(files.DirectoryPath));
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.JobPath));
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files.InputPath));
            using var job = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "inspection.json")));
            var owner = job.RootElement.GetProperty("encrypt").GetProperty("ownerPassword").GetString();
            Assert.IsNotNull(owner);
            Assert.AreEqual(64, owner.Length);
            Assert.AreNotEqual("argv-fixture-password", owner);
            var argv = await File.ReadAllTextAsync(Path.Combine(root, "argv"));
            Assert.IsFalse(argv.Contains("argv-fixture-password", StringComparison.Ordinal));
            Assert.IsFalse(argv.Contains(owner, StringComparison.Ordinal));
            StringAssert.StartsWith(argv, "--job-json-file=");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("sample.pdf")]
    [DataRow("../../untrusted.pdf")]
    public async Task Protect_EncryptsWithAes256_AndCleansFiles(string fileName)
    {
        await using var test = new PdfTestContext();
        const string password = "fixture-password-!\"\\";
        using var form = PdfTestContext.Form(PdfTestContext.Fixture, password, fileName);
        using var response = await test.Client.PostAsync("/api/pdf/protect", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var output = Path.Combine(test.Root, "protected.pdf");
        await File.WriteAllBytesAsync(output, await response.Content.ReadAsByteArrayAsync());
        var passwordFile = Path.Combine(test.Root, "password.json");
        await File.WriteAllTextAsync(passwordFile, password);
        Assert.AreEqual(0, (await test.QpdfAsync("--is-encrypted", output)).ExitCode);
        Assert.AreEqual(0, (await test.QpdfAsync("--password-file=" + passwordFile, "--check", output)).ExitCode);
        var encryption = await test.QpdfAsync("--password-file=" + passwordFile, "--show-encryption", output);
        Assert.AreEqual(0, encryption.ExitCode);
        StringAssert.Contains(encryption.Output, "AESv3");
        StringAssert.Contains(encryption.Output, "R = 6");
        await File.WriteAllTextAsync(passwordFile, "incorrect-fixture-password");
        Assert.AreEqual(2, (await test.QpdfAsync("--password-file=" + passwordFile, "--check", output)).ExitCode);
        test.AssertClean();
        Assert.IsFalse(test.Logs.Any(log => log.Contains(password, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FailedQpdf_DoesNotExposeData_AndCleansFiles()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/missing/qpdf" });
        using var form = PdfTestContext.Form(PdfTestContext.Fixture, "failure-test-password");
        using var response = await test.Client.PostAsync("/api/pdf/protect", form);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains(test.Root, StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("failure-test-password", StringComparison.Ordinal));
        test.AssertClean();
    }

    [TestMethod]
    public async Task CancelledJob_CleansFiles()
    {
        await using var test = new PdfTestContext();
        using (var files = new TemporaryPdfFiles(test.TempRoot))
        {
            await File.WriteAllBytesAsync(files.InputPath, PdfTestContext.Fixture);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var processor = new QpdfProcessor(Options.Create(new PdfOptions()));
            await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProtectAsync(files, "cancel-fixture-password", cancelled.Token));
        }
        test.AssertClean();
    }
}
