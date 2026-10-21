using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfSplitFileNameTests
{
    [TestMethod]
    [DataRow("report.v2.PDF", "report.v2")]
    [DataRow("report.pdf.pdf", "report.pdf")]
    [DataRow("日本語 report (v2).pdf", "日本語 report (v2)")]
    [DataRow("../secret/あいうえお.pdf", "あいうえお")]
    [DataRow("C:\\fakepath\\report.pdf", "report")]
    [DataRow("/tmp/../report.pdf", "report")]
    [DataRow("bad<>:\"|?*\u0000\u001f\u007f\u0085.pdf", "bad___________")]
    [DataRow("report. .pdf", "report")]
    [DataRow("CON.pdf", "_CON")]
    [DataRow("con.notes.pdf", "_con.notes")]
    [DataRow("PrN.pdf", "_PrN")]
    [DataRow("aux.pdf", "_aux")]
    [DataRow("NUL.pdf", "_NUL")]
    [DataRow("COM1.pdf", "_COM1")]
    [DataRow("lpt9.pdf", "_lpt9")]
    [DataRow("COM¹.pdf", "_COM¹")]
    [DataRow("LPT².pdf", "_LPT²")]
    [DataRow("COM³.pdf", "_COM³")]
    [DataRow("COM10.pdf", "COM10")]
    [DataRow("", "document")]
    [DataRow(".pdf", "document")]
    [DataRow(" ... .pdf", "document")]
    [DataRow("\u3000.pdf", "document")]
    [DataRow("../", "document")]
    public void BaseName_UsesPortableSafeNames(string input, string expected)
        => Assert.AreEqual(expected, PdfSplitFileName.NormalizeBaseName(input));

    [TestMethod]
    public void UnicodeLimit_PreservesScalarsAndSuffixAndRechecksTrailingCharacters()
    {
        foreach (var (input, expected) in new[]
        {
            (new string('あ', 61), new string('あ', 60)),
            (new string('a', 178) + "😀", new string('a', 178)),
            (new string('a', 176) + "😀z", new string('a', 176) + "😀"),
            (new string('a', 179) + ".z", new string('a', 179)),
            (new string('a', 179) + " z", new string('a', 179)),
            (new string('a', 181), new string('a', 180)),
            ("CON." + new string('a', 176), "_CON." + new string('a', 175)),
            ("CON." + new string('a', 174) + "é", "_CON." + new string('a', 174)),
            ("CON" + new string(' ', 177) + "x", "_CON"),
            ("CON." + new string('あ', 59), "_CON." + new string('あ', 58)),
            ("x\ud800y", "x�y")
        })
        {
            var name = PdfSplitFileName.NormalizeBaseName(input + ".pdf");
            Assert.AreEqual(expected, name);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(name) <= 180);
            Assert.AreEqual(name, PdfSplitFileName.NormalizeBaseName(name + ".pdf"));
            Assert.AreEqual(name + "_2147483646-2147483647.pdf",
                new SplitPart(1, int.MaxValue - 1, int.MaxValue).GetEntryName(name));
        }
    }

    [TestMethod]
    [DataRow("filename=\"a\\\"b.pdf\"", "a_b")]
    [DataRow("filename=\"C:\\\\fakepath\\\\a\\\"b.pdf\"", "a_b")]
    [DataRow("filename=\"a%22b.pdf\"", "a%22b")]
    [DataRow("filename*=UTF-8''%22a%22.pdf", "_a_")]
    [DataRow("filename*=UTF-8''%22a.pdf%22", "_a.pdf_")]
    [DataRow("filename*=UTF-8''a%5C%22b.pdf", "_b")]
    [DataRow("filename=\"report.v2.PDF\"", "report.v2")]
    [DataRow("filename*=UTF-8''..%252Freport.pdf", "..%2Freport")]
    [DataRow("filename=\"upload.pdf\"", "upload")]
    [DataRow("filename=\"\"", "document")]
    [DataRow("filename=\"../folder/report.pdf\"", "report")]
    [DataRow("filename=\"C:\\fakepath\\report.pdf\"", "report")]
    [DataRow("filename=\"CON.notes.pdf\"", "_CON.notes")]
    [DataRow("filename*=UTF-8''%E3%81%82%E3%81%84.pdf", "あい")]
    [DataRow("filename=upload.pdf; filename*=UTF-8''%E3%81%82%E3%81%84.pdf", "あい")]
    [DataRow("filename=upload.pdf; filename*=unknown''test.pdf", "upload")]
    [DataRow("filename*=UTF-8''bad%00%1F%7F%3C%3E%3A%22%7C%3F%2A.pdf", "bad__________")]
    [DataRow("filename*=UTF-8''..%2F..%5Creport.pdf", "report")]
    [DataRow("filename*=UTF-8''%E6%97%A5%E6%9C%AC%E8%AA%9E%20report.pdf", "日本語 report")]
    [DataRow("filename=\"=?utf-8?B?44GC44GELnBkZg==?=\"", "あい")]
    public async Task MultipartNames_AppearSafelyInActualZip(string parameters, string expected)
    {
        await using var test = new PdfTestContext();
        using var response = await PostHeaderAsync(test, await test.CreatePagedPdfAsync(2), parameters);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        CollectionAssert.AreEqual(new[] { expected + "_1.pdf", expected + "_2.pdf" },
            zip.Entries.Select(e => e.FullName).ToArray());
        foreach (var entry in zip.Entries)
        {
            Assert.IsFalse(entry.FullName.Contains('/'));
            Assert.IsFalse(entry.FullName.Contains('\\'));
        }
        test.AssertClean();
    }

    [TestMethod]
    public async Task BrowserRawUtf8Filename_UsesJapaneseNamesAndUtf8ZipFlag()
    {
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(2);
        // Browser-style wire bytes: raw UTF-8 filename, with neither filename* nor MIME encoding.
        var header = Encoding.UTF8.GetBytes("--split-browser\r\nContent-Disposition: form-data; name=\"file\"; " +
            "filename=\"あいうえお.pdf\"\r\nContent-Type: application/pdf\r\n\r\n");
        using var content = new ByteArrayContent(header.Concat(input)
            .Concat(Encoding.ASCII.GetBytes("\r\n--split-browser--\r\n")).ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=split-browser");
        using var response = await test.Client.PostAsync("/api/pdf/split?every=1", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(bytes));
        CollectionAssert.AreEqual(new[] { "あいうえお_1.pdf", "あいうえお_2.pdf" },
            zip.Entries.Select(entry => entry.FullName).ToArray());
        Assert.AreEqual(0x800, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) & 0x800);
        Assert.IsFalse(test.Logs.Any(log => log.Contains("あいうえお", StringComparison.Ordinal)));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("filename*=unknown''test.pdf")]
    public async Task MissingUsableFilename_KeepsExisting400(string parameters)
    {
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = "/must-not-run/qpdf" });
        using var response = await PostHeaderAsync(test, PdfTestContext.Fixture, parameters);
        await PdfSplitTests.AssertProblemAsync(test, response, 400);
    }

    [TestMethod]
    public void MaximumUnicodeNameAnd500Entries_FitExistingZipOverhead()
    {
        var options = new PdfOptions { MaxSplitParts = 500 };
        Assert.IsTrue(PdfSplitCapacity.IsValid(options));
        var parts = new PdfSplitPlan(1, null, options.MaxSplitParts).CreateParts(500);
        var baseName = PdfSplitFileName.NormalizeBaseName(new string('あ', 60) + "追加.pdf");
        Assert.AreEqual(new string('あ', 60), baseName);
        var payload = PdfTestContext.Fixture;
        var capacity = new PdfSplitCapacity(0, options.MaxSplitOutputBytes, options.MaxSplitJobBytes);
        using var output = new MemoryStream();
        using var bounded = new PdfSplitZipStream(output, capacity, CancellationToken.None);
        using (var archive = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var part in parts)
            {
                capacity.AcceptPart(payload.Length);
                var entry = archive.CreateEntry(part.GetEntryName(baseName), CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using (var stream = entry.Open()) stream.Write(payload);
                capacity.ReleasePart();
            }
        capacity.CheckZipLength(output.Length);
        var bytes = output.ToArray();
        using var zip = new ZipArchive(new MemoryStream(bytes));
        CollectionAssert.AreEqual(Enumerable.Range(1, 500).Select(i => $"{baseName}_{i}.pdf").ToArray(),
            zip.Entries.Select(e => e.FullName).ToArray());
        Assert.IsTrue(bytes.LongLength - zip.Entries.Sum(e => e.Length) <= PdfSplitCapacity.SplitZipOverheadBytes);
        Assert.AreEqual(0x800, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) & 0x800);
    }

    [TestMethod]
    public async Task OriginalName_StaysOutOfPathsCommandsLogsAndFailures()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux is required."); return; }
        const string marker = "利用者固有名-SPLIT-NAME-SENTINEL";
        await using var test = new PdfTestContext();
        var input = await test.CreatePagedPdfAsync(2);
        var captured = Path.Combine(test.Root, "arguments.txt");
        var wrapper = await PdfSplitTests.WrapperAsync(test,
            $"printf '%s\\n' \"$@\" >> '{captured}'\n" +
            $"find '{test.TempRoot}' -type f >> '{captured}'\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using (var success = await PdfSplitTests.PostAsync(test, input, fileName: marker + ".pdf"))
            Assert.AreEqual(HttpStatusCode.OK, success.StatusCode);
        Assert.IsFalse((await File.ReadAllTextAsync(captured)).Contains(marker, StringComparison.Ordinal));
        using var failure = await PdfSplitTests.PostAsync(test, Encoding.ASCII.GetBytes("invalid PDF"), fileName: marker + ".pdf");
        await PdfSplitTests.AssertProblemAsync(test, failure, 422);
        Assert.IsFalse((await failure.Content.ReadAsStringAsync()).Contains(marker, StringComparison.Ordinal));
        Assert.IsFalse(test.Logs.Any(log => log.Contains(marker, StringComparison.Ordinal)));
        test.AssertClean();
    }

    private static async Task<HttpResponseMessage> PostHeaderAsync(PdfTestContext test, byte[] input, string parameters)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(input);
        content.Headers.TryAddWithoutValidation("Content-Disposition", "form-data; name=\"file\"; " + parameters);
        form.Add(content);
        return await test.Client.PostAsync("/api/pdf/split?every=1", form);
    }
}
