using System.Text;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class ProcessFileLimitTests
{
    [TestMethod]
    [DataRow(4096, 4096, true)]
    [DataRow(4097, 4097, false)]
    [DataRow(8192, 4097, false)]
    public async Task BoundedOutput_ChecksActualLengthEvenWhenTheToolExitsZero(int requested, int length, bool succeeded)
    {
        if (!PdfCompressTests.Linux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var result = await BoundedOutput.RunAsync(fsize => ProcessMemoryLimits.CreateRequest(
            "/usr/bin/prlimit", "/bin/sh",
            ["-c", $"head -c {requested} /dev/zero > \"$1\" || :; exit 0", "writer", files.OutputPath],
            64 * 1024 * 1024, fsize, null), files.OutputPath, 4096, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual((long)length, result.Length);
        Assert.AreEqual(length > 4096, result.ExceedsBudget);
        Assert.AreEqual(succeeded, result.Succeeded);
        Assert.IsFalse(result.InternalFailure);
    }

    [TestMethod]
    [DataRow("exit 126", true, true)]
    [DataRow("exit 127", true, true)]
    [DataRow("kill -TERM $$", false, true)]
    [DataRow("exit 2", false, false)]
    public async Task BoundedOutput_ClassifiesProcessExits(string command, bool startFailed, bool internalFailure)
    {
        if (!PdfCompressTests.Linux()) return;
        using var files = new TemporaryPdfFiles(Path.GetTempPath());
        var result = await BoundedOutput.RunAsync(fsize => ProcessMemoryLimits.CreateRequest(
            "/usr/bin/prlimit", "/bin/sh", ["-c", command], 64 * 1024 * 1024, fsize, null),
            files.OutputPath, 4096, CancellationToken.None);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual(startFailed, result.StartFailed);
        Assert.AreEqual(internalFailure, result.InternalFailure);
        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.ExceedsBudget);
    }

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

    [TestMethod]
    [DataRow("pages.json", 0, "1")]
    [DataRow("pages.json", 1, "0")]
    [DataRow("metadata.json", 0, "1")]
    [DataRow("metadata.json", 1, "0")]
    public async Task CompressMetadata_AcceptsExactBudget_AndStopsAtOneByteOver(string outputName, int extra, string adopted)
    {
        if (!PdfCompressTests.Linux()) return;
        const int budget = 4096;
        await using var test = new PdfTestContext(new() { ["Pdf:CompressJsonLimitBytes"] = budget.ToString() });
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.QpdfPath = await PadMetadataAsync(test, outputName, budget + extra);
        var jpeg = await CompressFixtures.JpegAsync(test, width: 640, height: 480);
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(jpeg, width: 640, height: 480));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(adopted, response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task RedactMetadata_AcceptsExactBudget_AndRejectsOneByteOver(int extra)
    {
        if (!PdfCompressTests.Linux()) return;
        const int budget = 65536;
        await using var test = new PdfTestContext(new() { ["Pdf:RedactJsonLimitBytes"] = budget.ToString() });
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.QpdfPath = await PadMetadataAsync(test, "redact-inspect.json", budget + extra);
        using var response = await PdfRedactTests.Post(test, PdfTestContext.Fixture);
        if (extra == 0)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
        }
        else await PdfRedactTests.Problem(test, response, 422, "too-complex");
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(0, "1")]
    [DataRow(1, "0")]
    public async Task CompressCjpeg_UsesSentinel_AndAdoptsOnlyWithinBudget(int extra, string adopted)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        const int width = 640, height = 480;
        var jpeg = await CompressFixtures.JpegAsync(test, width: width, height: height);
        var budget = jpeg.Length * 9 / 10;
        var encoded = Path.Combine(test.Root, "encoded.jpg");
        var result = await ExternalProcessRunner.RunAsync(new("cjpeg",
            ["-quality", "75", "-optimize", "-outfile", encoded, Path.Combine(test.Root, "source.pnm")]),
            CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode);
        var candidate = await File.ReadAllBytesAsync(encoded);
        var padding = budget + extra - candidate.Length;
        Assert.IsTrue(padding is >= 4 and <= 65537);
        var comment = new byte[padding];
        comment[0] = 0xff;
        comment[1] = 0xfe;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(comment.AsSpan(2), (ushort)(padding - 2));
        await File.WriteAllBytesAsync(encoded, [.. candidate[..2], .. comment, .. candidate[2..]]);
        var wrapper = await PdfCompressTests.WrapperAsync(test, $$"""
            grep -Eq '^Max file size +{{budget + 1}} +{{budget + 1}} +bytes' /proc/$$/limits || exit 127
            while [ "$1" != -outfile ]; do [ "$#" -gt 0 ] || exit 127; shift; done
            exec cat '{{encoded}}' > "$2"
            """ + "\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.CjpegPath = wrapper;
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(jpeg, width: width, height: height));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(adopted, response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
        test.AssertClean();
    }

    private static Task<string> PadMetadataAsync(PdfTestContext test, string outputName, int length)
        => PdfCompressTests.WrapperAsync(test, $$"""
            for output do :; done
            case "$1:$output" in --json=2:*/{{outputName}})
                qpdf "$@" || exit $?
                size=$(stat -c %s "$output") || exit 127
                padding=$(({{length}} - size))
                [ "$padding" -ge 0 ] || exit 127
                printf '%*s' "$padding" '' >> "$output"
                exit $?
            ;; esac
            exec qpdf "$@"
            """ + "\n");

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task RedactOutput_AcceptsExactBudget_AndRejectsOneByteOver(int extra)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var input = PdfTestContext.Fixture;
        long length;
        using (var calibration = await PdfRedactTests.Post(test, input))
        {
            Assert.AreEqual(HttpStatusCode.OK, calibration.StatusCode);
            length = (await calibration.Content.ReadAsByteArrayAsync()).LongLength;
        }
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.RedactOutputLimitBytes = length - extra;
        using var response = await PdfRedactTests.Post(test, input);
        if (extra == 0)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var output = await response.Content.ReadAsByteArrayAsync();
            Assert.AreEqual(length, output.LongLength);
            await test.AssertValidPdfAsync(output, 1);
        }
        else await PdfRedactTests.Problem(test, response, 422, "output-too-large");
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(6)]
    public async Task CompressOutput_AcceptsExactBudget_AndRejectsOverflowEvenWhenQpdfCheckReturnsZero(int extra)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var input = PdfTestContext.Fixture;
        var limit = input.Length + 4 * 1024 * 1024; // The unchanged final-output headroom.
        var source = Path.Combine(test.Root, "expanded.pdf");
        var calibration = Path.Combine(test.Root, "calibration.pdf");
        var observation = Path.Combine(test.Root, "final-state.txt");
        // An uncompressed catalog string controls the real output length. The
        // largest case loses five bytes from the EOF marker at budget + 1.
        var padding = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await File.WriteAllBytesAsync(source, CompressFixtures.Objects([
                CompressFixtures.Text("<< /Type /Catalog /Pages 2 0 R /SyntheticPadding (" + new string('x', padding) + ") >>"),
                CompressFixtures.Text("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                CompressFixtures.Text("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>")
            ]));
            Assert.AreEqual(0, (await test.QpdfAsync(source, "--object-streams=disable", "--compression-level=9", calibration)).ExitCode);
            var length = new FileInfo(calibration).Length;
            if (length == limit + extra) break;
            padding = checked(padding + (int)(limit + extra - length));
        }
        Assert.AreEqual((long)limit + extra, new FileInfo(calibration).Length);
        Assert.AreEqual(0, (await test.QpdfAsync("--check", calibration)).ExitCode);
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "case \"$1\" in */input.pdf)\n" +
            "for arg do last=$arg; done\n" +
            $"qpdf '{source}' --object-streams=disable --compression-level=9 \"$last\"\n" +
            "code=$?\nsize=$(stat -c %s \"$last\")\n" +
            "qpdf --check \"$last\" >/dev/null 2>&1\ncheck=$?\n" +
            $"printf '%s %s %s\\n' \"$code\" \"$size\" \"$check\" > '{observation}'\n" +
            "exit \"$code\";; esac\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var response = await PdfCompressTests.PostAsync(test, input);
        Assert.AreEqual($"0 {limit + Math.Min(extra, 1)} 0", (await File.ReadAllTextAsync(observation)).Trim());
        if (extra == 0)
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
            await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 1);
        }
        else
        {
            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
            Assert.IsFalse(response.Content.Headers.Contains("Content-Disposition"));
            Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains(test.Root));
        }
        test.AssertClean();
    }
}
