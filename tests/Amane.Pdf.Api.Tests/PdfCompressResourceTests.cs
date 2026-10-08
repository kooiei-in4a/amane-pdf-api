using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfCompressResourceTests
{
    [TestMethod]
    [DataRow(10, false, false, false, 3)]
    [DataRow(10, true, false, false, 5)]
    [DataRow(300, true, false, false, 5)]
    [DataRow(300, true, true, false, 6)]
    [DataRow(10, true, true, true, 9)]
    public async Task MultiPageMetadata_IsFetchedPerLevel_IncludingInheritedResourcesAndImageDependencies(
        int pages, bool indirect, bool inherited, bool dependencies, int maximumStarts)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var counter = Path.Combine(test.Root, "metadata-starts");
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "for arg do case \"$arg\" in --json-key=pages|--json-stream-data=none) " +
            $"printf 'metadata\\n' >> '{counter}';; esac; done\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.MultiPage(jpeg, pages, indirect, inherited, dependencies));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(dependencies ? pages.ToString() : "1", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        Assert.IsTrue((await File.ReadAllLinesAsync(counter)).Length <= maximumStarts, "Metadata process starts grew with individual references.");
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), pages);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(21, 30, true)]
    [DataRow(30, 30, false)]
    [DataRow(31, 30, false)]
    public void SoftDeadlineMustPrecedeHardDeadline(int soft, int hard, bool valid)
        => Assert.AreEqual(valid, PdfCompressProcessor.IsValid(new() { CompressSoftTimeoutSeconds = soft, QpdfTimeoutSeconds = hard }));

    [TestMethod]
    [DataRow("standard", 1925, 1400)]
    [DataRow("strong", 1375, 1000)]
    public async Task JpegWithDataAfterEoi_RecompressesPrimaryImage(string level, int width, int height)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test);
        byte[] source = [.. jpeg, .. jpeg, 0xff, 0xdc, 0, 1, 0xff];
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(source), level);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        var path = Path.Combine(test.Root, "trailing-output.pdf");
        await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
        var pages = await test.QpdfAsync("--json=2", "--json-key=pages", path);
        using var document = System.Text.Json.JsonDocument.Parse(pages.Output);
        var image = document.RootElement.GetProperty("pages")[0].GetProperty("images")[0];
        Assert.AreEqual(width, image.GetProperty("width").GetInt32());
        Assert.AreEqual(height, image.GetProperty("height").GetInt32());
        var extracted = Path.Combine(test.Root, "trailing-output.jpg");
        // Inspect inline base64 rather than converting binary stdout through the runner.
        var json = await test.QpdfAsync("--json=2", "--json-stream-data=inline", "--decode-level=none", path);
        using var rawDocument = System.Text.Json.JsonDocument.Parse(json.Output);
        var raw = rawDocument.RootElement.GetProperty("qpdf")[1].GetProperty("obj:" + image.GetProperty("object").GetString())
            .GetProperty("stream").GetProperty("data").GetString()!;
        await File.WriteAllBytesAsync(extracted, Convert.FromBase64String(raw));
        Assert.AreEqual(new JpegHeader(width, height, 8, 3), JpegHeader.Read(extracted, source.Length, 100_000_000, CancellationToken.None));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("marker")]
    [DataRow("dimensions")]
    [DataRow("components")]
    public async Task NewJpegReinspection_StillRejectsInvalidBodyDimensionsOrComponents(string kind)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var input = await CompressFixtures.JpegAsync(test);
        var fake = await CompressFixtures.JpegAsync(test, width: kind == "dimensions" ? 64 : 1925,
            height: kind == "dimensions" ? 64 : 1400, gray: kind == "components");
        if (kind == "marker") fake = [.. fake[..^2], 0xff, 0xdc, 0, 4, 0, 1, 0xff, 0xd9];
        fake = [.. fake, 0xff, 0xdc, 0, 1]; // Ignoring a trailer must not weaken the body checks.
        var path = Path.Combine(test.Root, "fake-cjpeg.jpg");
        await File.WriteAllBytesAsync(path, fake);
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            $"previous=; for arg do if [ \"$previous\" = -outfile ]; then cp '{path}' \"$arg\"; exit $?; fi; previous=$arg; done\nexit 127\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.CjpegPath = wrapper;
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(input));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("cycle")]
    [DataRow("null")]
    [DataRow("wrong-N")]
    [DataRow("fractional-N")]
    [DataRow("indirect")]
    public async Task ColorSpaceReferences_ResolveSafely_AndRequireMatchingIntegerN(string kind)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test);
        byte[][] extras = kind switch
        {
            "cycle" => [CompressFixtures.Text("7 0 R")],
            "null" => [CompressFixtures.Text("null")],
            "wrong-N" => [CompressFixtures.Stream("/N 1", new byte[128])],
            "fractional-N" => [CompressFixtures.Stream("/N 3.5", new byte[128])],
            _ => [CompressFixtures.Text("[/ICCBased 8 0 R]"),
                CompressFixtures.Stream("/N 9 0 R", new byte[128]), CompressFixtures.Text("3")]
        };
        var color = kind is "wrong-N" or "fractional-N" ? "[/ICCBased 7 0 R]" : "7 0 R";
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(jpeg, color: color, extras: extras));
        // qpdf's common input check rejects the cyclic PDF before candidate selection.
        Assert.AreEqual(kind == "cycle" ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
        if (kind == "cycle") Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
        else Assert.AreEqual(kind == "indirect" ? "1" : "0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PdfAndJpegDimensionOrComponentMismatch_IsExcluded(bool components)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var response = await PdfCompressTests.PostAsync(test,
            CompressFixtures.Pdf(jpeg, width: components ? 2200 : 2199, gray: components));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        test.AssertClean();
    }

    [TestMethod]
    public async Task JobCapacityLimit_SkipsImages_AndIncreasingItProcessesTheSamePdf()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var jpeg = await CompressFixtures.JpegAsync(test);
        var input = CompressFixtures.Many(jpeg, 3);
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.CompressJobLimitBytes = CompressCapacity.Allocated(2L * input.Length + 4 * 1024 * 1024) + 8192;
        using (var limited = await PdfCompressTests.PostAsync(test, input))
        {
            Assert.AreEqual(HttpStatusCode.OK, limited.StatusCode);
            Assert.AreEqual("0", limited.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        }
        options.CompressJobLimitBytes = 124 * 1024 * 1024;
        using var raised = await PdfCompressTests.PostAsync(test, input);
        Assert.AreEqual(HttpStatusCode.OK, raised.StatusCode);
        Assert.AreEqual("3", raised.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        test.AssertClean();
    }

    [TestMethod]
    public async Task BatchStdoutOverflow_FallsBackOncePerImage_AndCleansPartialRaw()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:CompressStdoutLimitBytes"] = "1500" });
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Many(jpeg, 4));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("4", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        test.AssertClean();
    }

    [TestMethod]
    public async Task MaxImagesAndJpegAddressSpace_AreAppliedToRealProcessing()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:CompressMaxImages"] = "2" });
        var jpeg = await CompressFixtures.JpegAsync(test);
        using (var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Many(jpeg, 3)))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("2", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        }
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.JpegAddressSpaceLimitBytes = 8 * 1024 * 1024;
        using (var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(jpeg)))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        }
        options.JpegAddressSpaceLimitBytes = 64 * 1024 * 1024;
        using var raised = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(jpeg));
        Assert.AreEqual("1", raised.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(126, 500)]
    [DataRow(127, 500)]
    [DataRow(153, 200)]
    [DataRow(2, 200)]
    public async Task ImageExitCodes_AreDistinctFromFinalFailures(int exit, int expected)
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var wrapper = await PdfCompressTests.WrapperAsync(test, $"echo 'PDF-CONTENT-SENTINEL' >&2\nexit {exit}\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.CjpegPath = wrapper;
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(jpeg));
        Assert.AreEqual((HttpStatusCode)expected, response.StatusCode);
        if (expected == 200) Assert.AreEqual("0", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        else Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
        Assert.IsFalse(test.Logs.Any(log => log.Contains("PDF-CONTENT-SENTINEL")));
        test.AssertClean();
    }

    [TestMethod]
    public async Task RealAsFsizeAndPrivatePermissions_AreAppliedToJpegCommands()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "grep -Eq '^Max address space +67108864 +67108864 +bytes' /proc/$$/limits || exit 127\n" +
            "grep -Eq '^Max file size +[0-9]+ +[0-9]+ +bytes' /proc/$$/limits || exit 127\n" +
            "previous=; for arg do if [ \"$previous\" = -outfile ]; then [ \"$(stat -c %a \"$arg\")\" = 600 ] || exit 127; " +
            "[ \"$(stat -c %a \"$(dirname \"$arg\")\")\" = 700 ] || exit 127; fi; previous=$arg; done\n" +
            "case \"$1\" in -scale) exec djpeg \"$@\";; *) exec cjpeg \"$@\";; esac\n");
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.DjpegPath = wrapper; options.CjpegPath = wrapper;
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(jpeg));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("1", response.Headers.GetValues("X-Pdf-Images-Recompressed").Single());
        test.AssertClean();
    }

    [TestMethod]
    public async Task InputMemoryLimit_KeepsCommon422_AndOutputCheckFailureIs500()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfAddressSpaceLimitBytes"] = (64 * 1024 * 1024).ToString() });
        using (var response = await PdfCompressTests.PostAsync(test, QpdfMemoryLimitTests.FlateImagePdf()))
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
            Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("reason"));
        }
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "if [ \"$1\" = --check ]; then case \"$2\" in */output.pdf) exit 3;; esac; fi\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        using var final = await PdfCompressTests.PostAsync(test, PdfTestContext.Fixture);
        Assert.AreEqual(HttpStatusCode.InternalServerError, final.StatusCode);
        Assert.IsFalse(final.Headers.Contains("X-Pdf-Images-Recompressed"));
        test.AssertClean();
    }

    [TestMethod]
    public async Task UnexpectedMetadata_Is500()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "case \"$1\" in --json=2) for arg do last=$arg; done; printf '{bad metadata' > \"$last\"; exit 0;; esac\nexec qpdf \"$@\"\n");
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        options.QpdfPath = wrapper;
        using var response = await PdfCompressTests.PostAsync(test, PdfTestContext.Fixture);
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
        test.AssertClean();
    }

    [TestMethod]
    public async Task ExtractedPathMustMatchObjectAndStayInsidePrivateJob()
    {
        if (!PdfCompressTests.Linux()) return;
        await using var test = new PdfTestContext();
        var outside = Path.Combine(test.Root, "outside.jpg");
        await File.WriteAllTextAsync(outside, "untouched");
        var json = System.Text.Json.JsonSerializer.Serialize(new { qpdf = new object[] { new { jsonversion = 2 },
            new Dictionary<string, object> { ["obj:4 0 R"] = new { stream = new { datafile = outside } } } } });
        var wrapper = await PdfCompressTests.WrapperAsync(test,
            "for arg do if [ \"$arg\" = --json-stream-data=file ]; then printf '%s' '" + json + "'; exit 0; fi; done\nexec qpdf \"$@\"\n");
        test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var response = await PdfCompressTests.PostAsync(test, CompressFixtures.Pdf(jpeg));
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("X-Pdf-Images-Recompressed"));
        Assert.AreEqual("untouched", await File.ReadAllTextAsync(outside));
        test.AssertClean();
    }

    [TestMethod]
    public async Task ApplicationStopping_TerminatesImageTreeAndCleansJob()
    {
        if (!PdfCompressTests.Linux()) return;
        using var blocker = new BlockingQpdf("-scale");
        await using var test = new PdfTestContext(new() { ["Pdf:DjpegPath"] = blocker.Executable, ["Pdf:CompressImageTimeoutSeconds"] = "15" });
        var jpeg = await CompressFixtures.JpegAsync(test);
        using var form = PdfTestContext.FileForm(CompressFixtures.Pdf(jpeg));
        var pending = test.Client.PostAsync("/api/pdf/compress?level=standard", form);
        await blocker.WaitForJobsAsync(1);
        test.Factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        try { using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        await blocker.AssertStoppedAsync();
        // TestServer observes Abort before ExecuteAsync's finally finishes deleting the job.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Directory.Exists(test.TempRoot) && Directory.EnumerateFileSystemEntries(test.TempRoot).Any())
            await Task.Delay(20, cleanup.Token);
        test.AssertClean();
    }
}
