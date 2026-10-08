using System.Text;
using System.Net;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class ProcessFileLimitTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SignalAndLimits_SurviveExec_WithAndWithoutFileLimit(bool fileLimit)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "[ \"$JPEGMEM\" = 600M ] || exit 9\n" +
            "grep -Eq '^Max core file size +0 +0 +bytes' /proc/$$/limits || exit 9\n" +
            "grep -Eq '^Max address space +67108864 +67108864 +bytes' /proc/$$/limits || exit 9\n" +
            (fileLimit ? "grep -Eq '^Max file size +4096 +4096 +bytes' /proc/$$/limits || exit 9\n" : "") +
            "kill -XFSZ $$\n" +
            "exec /bin/sh -c 'kill -XFSZ $$; printf passed'\n");
        var request = fileLimit
            ? ProcessMemoryLimits.CreateRequest("/usr/bin/prlimit", wrapper, [], 64 * 1024 * 1024, 4096,
                new Dictionary<string, string> { ["JPEGMEM"] = "600M" }, 32)
            : ProcessMemoryLimits.CreateRequest("/usr/bin/prlimit", wrapper, [], 64 * 1024 * 1024, "600M", 32);
        var result = await ExternalProcessRunner.RunAsync(request, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("passed", Encoding.ASCII.GetString(result.Stdout!));
        test.AssertClean();
    }

    [TestMethod]
    public async Task RealQpdf_FileLimitKeepsExactAndOneByteOverflow_WithoutSignalTermination()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        using (var files = new TemporaryPdfFiles(test.TempRoot))
        {
            await File.WriteAllBytesAsync(files.InputPath, PdfTestContext.Fixture);
            var options = new PdfOptions();
            Task<ExternalProcessResult> Run(long? size) => ExternalProcessRunner.RunAsync(
                ProcessMemoryLimits.CreateRequest(options.PrlimitPath, options.QpdfPath,
                    [files.InputPath, "--pages", ".", "1", "--", files.OutputPath],
                    options.QpdfAddressSpaceLimitBytes, size,
                    new Dictionary<string, string> { ["JPEGMEM"] = options.QpdfJpegMemory }), CancellationToken.None);
            Assert.AreEqual(0, (await Run(null)).ExitCode);
            var normal = new FileInfo(files.OutputPath).Length;
            Assert.IsTrue(normal > 129);
            foreach (var budget in new[] { normal, normal - 1, 128 })
            {
                var result = await Run(budget + 1);
                if (budget == 128) CollectionAssert.Contains(new[] { 0, 2 }, result.ExitCode);
                else Assert.AreEqual(0, result.ExitCode);
                Assert.AreEqual(budget == 128 ? 129 : normal, new FileInfo(files.OutputPath).Length);
            }
        }
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("cjpeg")]
    [DataRow("djpeg")]
    public async Task RealJpegTool_FileLimitReturnsError_AndLeavesOnlyBoundedOutput(string tool)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        using (var files = new TemporaryPdfFiles(test.TempRoot))
        {
            var pnm = Path.Combine(files.DirectoryPath, "source.ppm");
            var jpeg = Path.Combine(files.DirectoryPath, "source.jpg");
            await File.WriteAllBytesAsync(pnm, [.. Encoding.ASCII.GetBytes("P6\n16 16\n255\n"), .. new byte[768]]);
            var options = new PdfOptions();
            Task<ExternalProcessResult> Run(string executable, string[] args, long size) => ExternalProcessRunner.RunAsync(
                ProcessMemoryLimits.CreateRequest(options.PrlimitPath, executable, args,
                    options.JpegAddressSpaceLimitBytes, size, null), CancellationToken.None);
            Assert.AreEqual(0, (await Run(options.CjpegPath,
                ["-quality", "75", "-maxmemory", "64M", "-strict", "-outfile", jpeg, pnm], 65536)).ExitCode);
            var args = tool == "cjpeg"
                ? new[] { "-quality", "75", "-maxmemory", "64M", "-strict", "-outfile", files.OutputPath, pnm }
                : new[] { "-maxmemory", "64M", "-maxscans", "100", "-strict", "-outfile", files.OutputPath, jpeg };
            var result = await Run(tool == "cjpeg" ? options.CjpegPath : options.DjpegPath, args, 64);
            Assert.AreEqual(1, result.ExitCode); // A normal I/O error, not SIGXFSZ's default termination.
            Assert.AreEqual(64L, new FileInfo(files.OutputPath).Length);
        }
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("pages")]
    [DataRow("objects")]
    public async Task SaturatedMetadata_StopsImageSelection_AndReturnsValidPdf(string kind)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:CompressJsonLimitBytes"] = "4096" });
        var jpeg = await CompressFixtures.JpegAsync(test);
        var input = kind == "pages"
            ? CompressFixtures.MultiPage(jpeg, 100, true, false, false)
            : CompressFixtures.Pdf(jpeg, extra: "/SyntheticPadding (" + new string('x', 8192) + ")");
        using (var files = new TemporaryPdfFiles(test.TempRoot))
        {
            await File.WriteAllBytesAsync(files.InputPath, input);
            var options = new PdfOptions();
            string[] args = kind == "pages"
                ? ["--json=2", "--json-key=pages", files.InputPath, files.OutputPath]
                : ["--json=2", "--json-key=qpdf", "--json-stream-data=none", "--decode-level=none",
                    "--json-object=4,0", files.InputPath, files.OutputPath];
            var result = await ExternalProcessRunner.RunAsync(ProcessMemoryLimits.CreateRequest(
                options.PrlimitPath, options.QpdfPath, args, options.QpdfAddressSpaceLimitBytes, 4096,
                new Dictionary<string, string> { ["JPEGMEM"] = options.QpdfJpegMemory }), CancellationToken.None);
            // qpdf can report success even though FSIZE truncated the JSON.
            CollectionAssert.Contains(new[] { 0, 2 }, result.ExitCode);
            Assert.AreEqual(4096L, new FileInfo(files.OutputPath).Length);
        }
        using var response = await PdfCompressTests.PostAsync(test, input);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), kind == "pages" ? 100 : 1);
        test.AssertClean();
    }
}
