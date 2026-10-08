using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfSplitTests
{
    [TestMethod]
    [DataRow("every=3", "part-001_p1-3.pdf,part-002_p4.pdf", "0,90,180;270")]
    [DataRow("ranges=4,1-2", "part-001_p4.pdf,part-002_p1-2.pdf", "270;0,90")]
    [DataRow("EVERY=2", "part-001_p1-2.pdf,part-002_p3-4.pdf", "0,90;180,270")]
    public async Task Split_ReturnsCompleteStoredZipWithRequestedPagesAndNames(string query, string names, string rotations)
    {
        await using var test = new PdfTestContext();
        var input = await test.CreateRotationMarkedPdfAsync(4);
        using var response = await PostAsync(test, input, query);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("split.zip", response.Content.Headers.ContentDisposition?.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.AreEqual((long)bytes.Length, response.Content.Headers.ContentLength);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        CollectionAssert.AreEqual(names.Split(','), zip.Entries.Select(entry => entry.FullName).ToArray());
        var expected = rotations.Split(';');
        for (var i = 0; i < zip.Entries.Count; i++)
        {
            using var stream = zip.Entries[i].Open();
            using var data = new MemoryStream();
            await stream.CopyToAsync(data);
            var pages = expected[i].Split(',').Select(int.Parse).ToArray();
            await test.AssertValidPdfAsync(data.ToArray(), pages.Length);
            CollectionAssert.AreEqual(pages, await test.ReadPageRotationsAsync(data.ToArray()));
        }
        AssertStoredDosHeaders(bytes, zip.Entries.Count);
        test.AssertClean();
        AssertNoExposure(test, Encoding.Latin1.GetString(bytes));
    }

    internal static void AssertStoredDosHeaders(byte[] bytes, int count)
    {
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            Assert.AreEqual(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            Assert.AreEqual((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 8))); // Stored
            Assert.AreEqual((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 10))); // DOS time
            Assert.AreEqual((ushort)33, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 12))); // 1980-01-01
            offset += 30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 26)) +
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28)) +
                (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 18));
        }
        Assert.AreEqual(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("every=2&Every=3")]
    [DataRow("every=2&extra=1")]
    [DataRow("ranges=1")]
    [DataRow("ranges=1,1")]
    [DataRow("every=01")]
    [DataRow("every=100000")]
    public async Task BadQuery_Returns400BeforeUploadAndQpdf(string query)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var response = await PostAsync(test, PdfTestContext.Fixture, query);
        await AssertProblemAsync(test, response, 400);
    }

    [TestMethod]
    [DataRow("every=4")]
    [DataRow("every=99999")]
    [DataRow("ranges=1,5")]
    public async Task InvalidPageCount_Returns400(string query)
    {
        await using var test = new PdfTestContext();
        using var response = await PostAsync(test, await test.CreatePagedPdfAsync(4), query);
        await AssertProblemAsync(test, response, 400);
    }

    [TestMethod]
    public async Task MaximumPartsIsAppliedAfterCountingPages()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:MaxSplitParts"] = "2" });
        var input = await test.CreatePagedPdfAsync(3);
        using var response = await PostAsync(test, input);
        await AssertProblemAsync(test, response, 400);
    }

    [TestMethod]
    public async Task MultipartRejectsPasswordAdditionalFieldAndMultipleFiles()
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        foreach (var kind in new[] { "password", "extra", "files", "missing" })
        {
            using var form = kind switch
            {
                "password" => PdfTestContext.Form(PdfTestContext.Fixture),
                "files" => PdfTestContext.MergeForm(PdfTestContext.Fixture, PdfTestContext.Fixture),
                "missing" => PdfTestContext.FileForm(),
                _ => PdfTestContext.FileForm(PdfTestContext.Fixture)
            };
            if (kind == "extra") form.Add(new StringContent("unexpected"), "extra");
            using var response = await test.Client.PostAsync("/api/pdf/split?every=1", form);
            await AssertProblemAsync(test, response, 400);
        }
    }

    [TestMethod]
    public async Task InputFailuresKeepCommon422WithoutReason()
    {
        await using var test = new PdfTestContext();
        var encrypted = await test.CreateEncryptedPdfAsync("synthetic-password", "synthetic-owner");
        foreach (var source in new[] { Array.Empty<byte>(), Encoding.ASCII.GetBytes("PDF-CONTENT-SENTINEL"),
            Encoding.ASCII.GetBytes("%PDF-1.4\nPDF-CONTENT-SENTINEL\n%%EOF"),
            Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(PdfTestContext.Fixture).Replace("/Length 41", "/Length 39")), encrypted })
        {
            using var response = await PostAsync(test, source);
            await AssertProblemAsync(test, response, 422);
        }
    }

    [TestMethod]
    public async Task FileLimit_Returns413BeforeQpdf()
    {
        await using var test = new PdfTestContext(new()
        { ["Pdf:MaxFileBytes"] = (PdfTestContext.Fixture.Length - 1).ToString(), ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var response = await PostAsync(test, PdfTestContext.Fixture);
        await AssertProblemAsync(test, response, 413);
    }

    [TestMethod]
    public async Task RealFsizeAndSizeComparison_CoverExactOneByteAndLargeOverflow()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux prlimit is required."); return; }
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(2);
        using var baseline = await PostAsync(test, input);
        Assert.AreEqual(HttpStatusCode.OK, baseline.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await baseline.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        var total = archive.Entries.Sum(entry => entry.Length);
        var last = archive.Entries.Last().Length;
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        foreach (var reduction in new[] { 0L, 1L, last - 128 })
        {
            options.MaxSplitOutputBytes = total - reduction;
            using var response = await PostAsync(test, input);
            if (reduction == 0) { Assert.AreEqual(HttpStatusCode.OK, response.StatusCode); test.AssertClean(); }
            else await AssertProblemAsync(test, response, 422, "output-too-large");
        }
    }

    [TestMethod]
    [DataRow(0, 500)]
    [DataRow(2, 500)]
    [DataRow(3, 422)]
    [DataRow(153, 500)]
    public async Task SplitExitWithoutOutput_IsNotMisclassifiedAsCapacity(int exit, int status)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux is required."); return; }
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(2);
        var wrapper = await WrapperAsync(test, $"if [ \"$2\" = --pages ]; then printf 'PDF-CONTENT-SENTINEL' >&2; exit {exit}; fi\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await PostAsync(test, input);
        await AssertProblemAsync(test, response, status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealPrlimitMissingOrNonExecutablePath_Is500(bool nonExecutable)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux is required."); return; }
        await using var test = new PdfTestContext();
        var path = Path.Combine(test.Root, "not-executable");
        if (nonExecutable)
        {
            await File.WriteAllTextAsync(path, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = path;
        using var response = await PostAsync(test, PdfTestContext.Fixture);
        await AssertProblemAsync(test, response, 500);
    }

    [TestMethod]
    [DataRow("corrupt")]
    [DataRow("encrypted")]
    [DataRow("warning")]
    public async Task InvalidGeneratedPart_Is500(string kind)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux is required."); return; }
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(2);
        var encrypted = Path.Combine(test.Root, "encrypted-part.pdf");
        if (kind == "encrypted") await File.WriteAllBytesAsync(encrypted, await test.CreateEncryptedPdfAsync("fixture", "owner"));
        var action = kind switch
        {
            "corrupt" => "for arg do last=$arg; done; printf '%s' '%PDF-1.4 invalid' > \"$last\"; exit 0",
            "encrypted" => $"for arg do last=$arg; done; cp '{encrypted}' \"$last\"; exit 0",
            _ => "exec qpdf \"$@\""
        };
        var wrapper = await WrapperAsync(test,
            "if [ \"$1\" = --check ]; then case \"$2\" in */part-*.pdf) exit 3;; esac; fi\n" +
            $"if [ \"$2\" = --pages ]; then {action}; fi\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await PostAsync(test, input);
        await AssertProblemAsync(test, response, 500);
    }

    [TestMethod]
    public async Task JobBudgetExhaustion_DoesNotStartTheNextPart()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux is required."); return; }
        await using var test = new PdfTestContext(new()
        { ["Pdf:MaxFileBytes"] = "4096", ["Pdf:MaxSplitJobBytes"] = (1048576 + 4 * 4096).ToString() });
        var input = await test.CreatePagedPdfAsync(2);
        var counter = Path.Combine(test.Root, "split-starts");
        var wrapper = await WrapperAsync(test,
            $"if [ \"$2\" = --pages ]; then printf 'part\\n' >> '{counter}'; fi\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await PostAsync(test, input);
        await AssertProblemAsync(test, response, 422, "output-too-large");
        Assert.AreEqual(1, (await File.ReadAllLinesAsync(counter)).Length);
    }

    [TestMethod]
    public async Task SplitCommands_UsePrivateFilesCanonicalRangesAndRealLimits()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux is required."); return; }
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(2);
        var wrapper = await WrapperAsync(test,
            "if [ \"$2\" = --pages ]; then\n" +
            "[ \"$3\" = . ] && [ \"$5\" = -- ] && [ \"$JPEGMEM\" = 600M ] || exit 127\n" +
            "grep -Eq '^Max address space +570425344 +570425344 +bytes' /proc/$$/limits || exit 127\n" +
            "grep -Eq '^Max file size +[0-9]+ +[0-9]+ +bytes' /proc/$$/limits || exit 127\n" +
            "[ \"$(stat -c %a \"$6\")\" = 600 ] && [ \"$(stat -c %a \"$(dirname \"$6\")\")\" = 700 ] || exit 127\n" +
            "fi\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await PostAsync(test, input);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SplitLaunchFailure_PrecedesEvenAnOversizedExistingOutput(bool nonExecutable)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux is required."); return; }
        await using var test = new PdfTestContext();
        using var files = new TemporaryPdfFiles(test.Root);
        var path = Path.Combine(test.Root, "qpdf-launch-failure");
        if (nonExecutable)
        {
            await File.WriteAllTextAsync(path, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await File.WriteAllBytesAsync(files.SplitPartPath(1), [1, 2]);
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>();
        options.Value.QpdfPath = path;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QpdfProcessor(options).SplitPartAsync(
            files.InputPath, "1", files.SplitPartPath(1), 1, CancellationToken.None));
    }

    [TestMethod]
    public async Task PageCountParsing_AcceptsIntMaxValueBeforePlanRejectsTooManyParts()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux is required."); return; }
        await using var test = new PdfTestContext();
        using var files = new TemporaryPdfFiles(test.Root);
        var path = await WrapperAsync(test, "printf '2147483647\\n'\n");
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>();
        options.Value.QpdfPath = path;
        Assert.AreEqual(int.MaxValue, await new QpdfProcessor(options).GetPageCountAsync(files, CancellationToken.None));
    }

    internal static async Task<string> WrapperAsync(PdfTestContext test, string source)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var path = Path.Combine(test.Root, "split-qpdf.sh");
        await File.WriteAllTextAsync(path, "#!/bin/sh\n" + source);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
    internal static async Task<HttpResponseMessage> PostAsync(PdfTestContext test, byte[] input, string query = "every=1",
        CancellationToken token = default)
    {
        using var form = PdfTestContext.FileForm(input, "../../private-name.pdf");
        return await test.Client.PostAsync("/api/pdf/split?" + query, form, token);
    }
    internal static async Task AssertProblemAsync(PdfTestContext test, HttpResponseMessage response, int status, string? reason = null)
    {
        Assert.AreEqual((HttpStatusCode)status, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.IsNull(response.Content.Headers.ContentDisposition);
        Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.AreEqual(status, problem.RootElement.GetProperty("status").GetInt32());
        if (reason is null) Assert.IsFalse(problem.RootElement.TryGetProperty("reason", out _));
        else
        {
            Assert.AreEqual(reason, problem.RootElement.GetProperty("reason").GetString());
            Assert.AreEqual(PdfSplitOutputTooLargeException.Title, problem.RootElement.GetProperty("title").GetString());
        }
        AssertNoExposure(test, body);
        test.AssertClean();
    }
    internal static void AssertNoExposure(PdfTestContext test, string body)
    {
        foreach (var text in new[] { "PDF-CONTENT-SENTINEL", "private-name.pdf", test.TempRoot, "split-qpdf.sh" })
        {
            Assert.IsFalse(body.Contains(text, StringComparison.Ordinal));
            Assert.IsFalse(test.Logs.Any(log => log.Contains(text, StringComparison.Ordinal)));
        }
    }
}
