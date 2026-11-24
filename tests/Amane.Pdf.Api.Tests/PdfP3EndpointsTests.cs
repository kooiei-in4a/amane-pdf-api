using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfP3EndpointsTests
{
    private const string Horizontal = "/api/pdf/ocr?direction=horizontal";
    private const string Japanese = "東京都千代田区";
    private const string SearchTerm = "東京都";

    [TestMethod]
    [DataRow("/api/pdf/flatten", "--check", "output.pdf", 2)]
    [DataRow("/api/pdf/flatten", "--check", "output.pdf", 3)]
    [DataRow("/api/pdf/n-up?pages-per-sheet=2", "--check", "output.pdf", 3)]
    [DataRow(Horizontal, "--show-npages", "ocr-text-0.pdf", 3)]
    [DataRow(Horizontal, "--check", "ocr-text.pdf", 3)]
    [DataRow(Horizontal, "--check", "output.pdf", 3)]
    public async Task GeneratedPdfInspectionFailureReturnsInternalError(
        string route, string command, string generatedFile, int exitCode)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scripts = new OverlayJob();
        var marker = Path.Combine(scripts.Root, "output-checked");
        var qpdf = scripts.Script($$"""
            if [ "$1" = '{{command}}' ] && [ "${2##*/}" = '{{generatedFile}}' ]; then
                [ -s "$2" ] || exit 99
                touch '{{marker}}'
                exit {{exitCode}}
            fi
            exec qpdf "$@"
            """);
        await using var test = new PdfTestContext(new() { ["Pdf:QpdfPath"] = qpdf });
        var input = route.StartsWith("/api/pdf/n-up", StringComparison.Ordinal) ?
            await test.CreatePagedPdfAsync(2) : PdfTestContext.Fixture;
        using var form = PdfTestContext.FileForm(input);
        using var response = await test.Client.PostAsync(route, form);
        Assert.IsTrue(File.Exists(marker), "The generated PDF must reach the injected inspection.");
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("PDF処理に失敗しました。", problem.RootElement.GetProperty("title").GetString());
        Assert.IsTrue(test.Logs.Any(log => log.StartsWith("PDF request ", StringComparison.Ordinal) &&
            log.Contains("Outcome=InternalError ", StringComparison.Ordinal)));
        Assert.Contains("PDF処理で内部障害が発生しました。", test.Logs);
        test.AssertClean();
    }

    [TestMethod]
    public async Task FlattenKeepsJapaneseAppearanceAndAnnotationsInPageContents()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var fixture = FlattenFixture();
        var source = await PdfAsync(test, fixture, "annotations");
        var output = await PostPdfAsync(test, "/api/pdf/flatten", source, "flattened.pdf");
        await test.AssertValidPdfAsync(output, 1);
        var path = Path.Combine(test.Root, "flattened.pdf");
        await File.WriteAllBytesAsync(path, output);
        var (code, json) = await test.QpdfAsync("--json", "--json-stream-data=none", path);
        Assert.AreEqual(0, code);
        Assert.DoesNotContain("\"/Annots\"", json);
        Assert.DoesNotContain("\"/AcroForm\"", json);
        var text = await ExtractTextAsync(path);
        Assert.Contains("Hello PDF", text);
        Assert.Contains("日本語", text);

        // Poppler displays annotation appearances when rendering the input.
        var inputPath = Path.Combine(test.Root, "annotations.pdf");
        CollectionAssert.AreEqual(await RenderWithPopplerAsync(inputPath), await RenderWithPopplerAsync(path));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("missing", "needs-appearance")]
    [DataRow("regenerate", "needs-appearance")]
    [DataRow("signature", "signature-structure")]
    public async Task FlattenRejectsInputsWhichCannotBeSafelyBurnedIn(string variant, string reason)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var fixture = FlattenFixture();
        var objects = OverlayFixtures.Objects(fixture);
        var field = objects["obj:6 0 R"]!["value"]!.AsObject();
        if (variant == "missing") field.Remove("/AP");
        if (variant == "regenerate") objects["obj:5 0 R"]!["value"]!["/NeedAppearances"] = true;
        if (variant == "signature") field["/FT"] = "/Sig";
        using var form = PdfTestContext.FileForm(await PdfAsync(test, fixture, "invalid-appearance"));
        using var response = await test.Client.PostAsync("/api/pdf/flatten", form);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(reason, result.RootElement.GetProperty("reason").GetString());
        test.AssertClean();
    }

    [TestMethod]
    public async Task FlattenKeepsLinksAndRemovesPopupsOfBurnedInComments()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var source = await PdfAsync(test, WithLinkAndPopup(FlattenFixture()), "links");
        var output = await PostPdfAsync(test, "/api/pdf/flatten", source, "flattened.pdf");
        await test.AssertValidPdfAsync(output, 1);
        var path = Path.Combine(test.Root, "flattened.pdf");
        await File.WriteAllBytesAsync(path, output);
        Assert.AreEqual("/Link", string.Join(',', await AnnotationSubtypesAsync(path)));
        Assert.Contains("日本語", await ExtractTextAsync(path));
        CollectionAssert.AreEqual(await RenderWithPopplerAsync(Path.Combine(test.Root, "links.pdf")),
            await RenderWithPopplerAsync(path));
        test.AssertClean();
    }

    [TestMethod]
    public async Task LayoutBurnsInFormValuesAndCommentsAndDropsLinks()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var source = await PdfAsync(test, WithLinkAndPopup(FlattenFixture()), "form-layout");
        var output = await PostPdfAsync(test, "/api/pdf/resize?size=a4", source, "resized.pdf");
        await test.AssertValidPdfAsync(output, 1);
        var path = Path.Combine(test.Root, "resized-form.pdf");
        await File.WriteAllBytesAsync(path, output);
        Assert.IsEmpty(await AnnotationSubtypesAsync(path));
        var (_, json) = await test.QpdfAsync("--json", "--json-stream-data=none", path);
        Assert.DoesNotContain("\"/AcroForm\"", json);
        var text = await ExtractTextAsync(path);
        Assert.Contains("Hello PDF", text);
        Assert.Contains("日本語", text);
        // The 300x200 page fills the A4 landscape width. The comment's red icon is centered at (210, 40).
        var scale = 841.8898 / 300;
        var bottom = (595.2756 - 200 * scale) / 2;
        CollectionAssert.AreEqual(new byte[] { 255, 0, 0 },
            Pixel(await RenderWithPopplerAsync(path), 210 * scale / 841.8898, 1 - (bottom + 40 * scale) / 595.2756));
        test.AssertClean();
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("regenerate")]
    public async Task LayoutRejectsFormsWithoutUsableAppearance(string variant)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var fixture = FlattenFixture();
        var objects = OverlayFixtures.Objects(fixture);
        if (variant == "missing") objects["obj:6 0 R"]!["value"]!.AsObject().Remove("/AP");
        if (variant == "regenerate") objects["obj:5 0 R"]!["value"]!["/NeedAppearances"] = true;
        using var form = PdfTestContext.FileForm(await PdfAsync(test, fixture, "layout-appearance"));
        using var response = await test.Client.PostAsync("/api/pdf/n-up?pages-per-sheet=2", form);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("needs-appearance", result.RootElement.GetProperty("reason").GetString());
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(2, 3)]
    [DataRow(4, 2)]
    [DataRow(1, 5)]
    public async Task LayoutUsesA4AndPositionsSourcePagesInOrder(int perSheet, int count)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var source = await PdfAsync(test, LayoutFixture(), "layout");
        var endpoint = perSheet == 1 ? "/api/pdf/resize?size=a4" : "/api/pdf/n-up?pages-per-sheet=" + perSheet;
        var output = await PostPdfAsync(test, endpoint, source, perSheet == 1 ? "resized.pdf" : "n-up.pdf");
        await test.AssertValidPdfAsync(output, count);
        var path = Path.Combine(test.Root, "arranged.pdf");
        await File.WriteAllBytesAsync(path, output);
        using var fixtures = new OverlayJob();
        using var metadata = await fixtures.Metadata(path);
        var dimensions = Enumerable.Range(0, count).Select(i => metadata.Read(i, default)).ToArray();
        for (var i = 0; i < count; i++)
        {
            var landscape = perSheet == 2 || perSheet == 1 && i is 1 or 3;
            Assert.AreEqual(landscape ? 841.8898 : 595.2756, dimensions[i].DisplayWidth, 0.001);
            Assert.AreEqual(landscape ? 595.2756 : 841.8898, dimensions[i].DisplayHeight, 0.001);
        }

        var first = await RenderWithPopplerAsync(path);
        var columns = perSheet == 1 ? 1 : 2;
        var rows = perSheet == 4 ? 2 : 1;
        for (var cell = 0; cell < perSheet; cell++)
        {
            var x = (cell % columns + 0.5) / columns;
            var y = (cell / columns + 0.5) / rows;
            CollectionAssert.AreEqual(Colors[cell], Pixel(first, x, y), $"cell {cell}");
        }
        // The first portrait page has content outside its CropBox. The output margin stays white.
        CollectionAssert.AreEqual(new byte[] { 255, 255, 255 }, Pixel(first, 0.01, 0.001));
        if (perSheet == 1)
        {
            // Page 2 is a rotated portrait. The blue marker is near the top-left after /Rotate 90.
            var rotated = await RenderWithPopplerAsync(path, 2);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 255 }, Pixel(rotated, 0.12, 0.15));
        }
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OcrAddsSearchableJapaneseWithoutChangingRenderedPage(bool vertical)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var scanned = await ScanAsync(test, vertical);
        var path = Path.Combine(test.Root, "scan.pdf");
        await File.WriteAllBytesAsync(path, scanned);
        Assert.AreEqual("", Normalize(await ExtractTextAsync(path)));
        if (!vertical) scanned = await RotateWithCropAsync(test, path);
        var direction = vertical ? "vertical" : "horizontal";
        var ocr = await PostPdfAsync(test, "/api/pdf/ocr?direction=" + direction, scanned, "ocr.pdf");
        await test.AssertValidPdfAsync(ocr, 1);
        var outputPath = Path.Combine(test.Root, "searchable.pdf");
        await File.WriteAllBytesAsync(outputPath, ocr);
        Assert.Contains(SearchTerm, Normalize(await ExtractTextAsync(outputPath, "-raw")));
        CollectionAssert.AreEqual(await RenderPngAsync(test, scanned), await RenderPngAsync(test, ocr));
        if (!vertical)
            await AssertOcrTextMatchesForegroundAsync(await RenderWithPopplerAsync(Path.Combine(test.Root, "cropped.pdf")),
                outputPath, 1);
        test.AssertClean();
    }

    [TestMethod]
    public async Task OcrKeepsJapanesePhrasesWholeAndLatinWordsApart()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        string[] lines = ["中央図書館の開館時間は午前九時から午後七時までです。", "返却ポストは正面入口の東側にあります。",
            "本製品は Windows 11 と macOS に対応しています。"];
        var scanned = await ScanPageAsync(test, "phrases", lines.Select((text, i) => new
        {
            page = 1, x = 0.06m, y = 0.08m + 0.05m * i, width = 0.9m, height = 0.04m, fontSize = 18, align = "left", text
        }).ToArray());
        var path = Path.Combine(test.Root, "phrases-ocr.pdf");
        await File.WriteAllBytesAsync(path, await PostPdfAsync(test, Horizontal, scanned, "ocr.pdf"));
        // Spaces are significant here: PDF.js and pdftotext both fail to find 開館時間 in "開館 時 間".
        var text = (await ExtractTextAsync(path, "-raw")).Normalize(NormalizationForm.FormKC);
        foreach (var phrase in new[] { "中央図書館", "開館時間", "午前九時", "午後七時", "返却ポスト", "正面入口", "Windows 11" })
            Assert.Contains(phrase, text);
        Assert.DoesNotContain("Windows11", text);
        test.AssertClean();
    }

    [TestMethod]
    public async Task OcrReadsHorizontalHeadingsOnVerticalPages()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        string[] columns = ["中央図書館の開館時間は午前九時からです。", "返却ポストは正面入口の東側にあります。",
            "休館日は毎月第三月曜日です。", "本人確認書類の画像が必要です。", "市民課までお問い合わせください。"];
        var fields = columns.SelectMany((column, c) => column.Select((character, i) => (object)new
        {
            page = 1, x = 0.80m - 0.07m * c, y = 0.12m + 0.03m * i, width = 0.05m, height = 0.03m, fontSize = 18,
            align = "center", text = character.ToString()
        })).Append(new
        {
            page = 1, x = 0.08m, y = 0.04m, width = 0.6m, height = 0.04m, fontSize = 20, align = "left",
            text = "中央図書館だより　第3号"
        }).ToArray();
        var scanned = await ScanPageAsync(test, "vertical-heading", fields);
        var path = Path.Combine(test.Root, "vertical-heading-ocr.pdf");
        await File.WriteAllBytesAsync(path, await PostPdfAsync(test, "/api/pdf/ocr?direction=vertical", scanned, "ocr.pdf"));
        // jpn_vert --psm 5 alone slices the heading into vertical "columns"; page segmentation keeps it readable.
        var text = (await ExtractTextAsync(path, "-raw")).Normalize(NormalizationForm.FormKC);
        foreach (var phrase in new[] { "中央図書館だより", "開館時間", "返却ポスト", "本人確認書類", "市民課" })
            Assert.Contains(phrase, text);
        test.AssertClean();
    }

    [TestMethod]
    public async Task OcrChecksQueryAndPageLimitAndPreservesUnselectedPages()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        using var rejected = PdfTestContext.FileForm(PdfTestContext.Fixture);
        foreach (var endpoint in new[] { "/api/pdf/ocr", "/api/pdf/ocr?direction=auto", Horizontal + "&pages=1&pages=2",
                     Horizontal + "&pages=1,1", "/api/pdf/n-up?pages-per-sheet=3", "/api/pdf/resize?size=A4",
                     "/api/pdf/flatten?mode=all" })
            await test.AssertRejectedBeforeReadingBodyAsync(endpoint, rejected);

        var many = await test.CreatePagedPdfAsync(11);
        using var tooMany = PdfTestContext.FileForm(many);
        using var overLimit = await test.Client.PostAsync(Horizontal, tooMany);
        Assert.AreEqual(HttpStatusCode.BadRequest, overLimit.StatusCode);
        using var badRange = await test.Client.PostAsync(Horizontal + "&pages=12", tooMany);
        Assert.AreEqual(HttpStatusCode.BadRequest, badRange.StatusCode);

        var tokyo = await ScanAsync(test, vertical: false);
        const string osakaSearchTerm = "大阪府";
        var osaka = await ScanPageAsync(test, "osaka",
        [
            new { page = 1, x = 0.52m, y = 0.37m, width = 0.45m, height = 0.12m,
                fontSize = 24, align = "center", text = "大阪府大阪市" }
        ]);
        var osakaPath = Path.Combine(test.Root, "osaka-scan.pdf");
        await File.WriteAllBytesAsync(osakaPath, osaka);
        var croppedOsaka = await RotateWithCropAsync(test, osakaPath);
        using var mergeForm = PdfTestContext.MergeForm(tokyo, tokyo, croppedOsaka);
        using var merge = await test.Client.PostAsync("/api/pdf/merge", mergeForm);
        Assert.AreEqual(HttpStatusCode.OK, merge.StatusCode);
        var input = await merge.Content.ReadAsByteArrayAsync();
        var inputPath = Path.Combine(test.Root, "selected-input.pdf");
        await File.WriteAllBytesAsync(inputPath, input);
        var output = await PostPdfAsync(test, Horizontal + "&pages=3,1", input, "ocr.pdf");
        await test.AssertValidPdfAsync(output, 3);
        var path = Path.Combine(test.Root, "selected.pdf");
        await File.WriteAllBytesAsync(path, output);
        var first = Normalize(await ExtractTextAsync(path, "-f", "1", "-l", "1"));
        Assert.Contains(SearchTerm, first);
        Assert.DoesNotContain(osakaSearchTerm, first);
        var third = Normalize(await ExtractTextAsync(path, "-f", "3", "-l", "3"));
        Assert.Contains(osakaSearchTerm, third);
        Assert.DoesNotContain(SearchTerm, third);
        Assert.AreEqual("", Normalize(await ExtractTextAsync(path, "-f", "2", "-l", "2")));

        var firstPage = await RenderWithPopplerAsync(inputPath, 1);
        var thirdPage = await RenderWithPopplerAsync(inputPath, 3);
        Assert.AreNotEqual(PpmHeader(firstPage).Width, PpmHeader(thirdPage).Width);
        await AssertOcrTextMatchesForegroundAsync(firstPage, path, 1);
        await AssertOcrTextMatchesForegroundAsync(thirdPage, path, 3);
        test.AssertClean();
    }

    [TestMethod]
    public async Task OcrLimitsUploadToTheSharedRenderJobBudget()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:MaxRedactInputBytes"] = "4096",
            ["Pdf:RedactJobLimitBytes"] = "16384"
        });
        using var large = PdfTestContext.FileForm(new byte[16_385]);
        using var response = await test.Client.PostAsync(Horizontal, large);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        test.AssertClean();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OcrFitsTesseractPdfAndTsvIntoASmallJobBudget(bool vertical)
    {
        if (!OperatingSystem.IsLinux()) return;
        // Tesseract writes the text PDF and the TSV in one run. Reserving the PDF first must not take the space
        // that the TSV needs; main succeeded with these limits before the TSV output was added.
        await using var test = new PdfTestContext(new()
        {
            ["Pdf:RedactJobLimitBytes"] = "8388608",
            ["Pdf:MaxRedactInputBytes"] = "4194304"
        });
        var scanned = await ScanAsync(test, vertical);
        var output = await PostPdfAsync(test, "/api/pdf/ocr?direction=" + (vertical ? "vertical" : "horizontal"),
            scanned, "ocr.pdf");
        var path = Path.Combine(test.Root, "small-budget.pdf");
        await File.WriteAllBytesAsync(path, output);
        Assert.Contains(SearchTerm, Normalize(await ExtractTextAsync(path, "-raw")));
        test.AssertClean();
    }

    [TestMethod]
    public async Task OcrSharesRenderSlotTimesOutAndAllowsOtherPdfOperations()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext(new() { ["Pdf:OcrTimeoutSeconds"] = "4" });
        var options = test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value;
        var originalTool = options.TesseractPath;
        using var scripts = new OverlayJob();
        var marker = Path.Combine(scripts.Root, "tesseract.pid");
        options.TesseractPath = scripts.Script($"printf '%s' \"$$\" > '{marker}'\nexec sleep 60");

        using var upload = PdfTestContext.FileForm(PdfTestContext.Fixture);
        var request = test.Client.PostAsync(Horizontal, upload);
        try
        {
            using var until = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!File.Exists(marker))
            {
                Assert.IsFalse(request.IsCompleted, "OCR must reach the Tesseract subprocess.");
                await Task.Delay(25, until.Token);
            }
            using var competing = PdfTestContext.FileForm(PdfTestContext.Fixture);
            using var second = await test.Client.PostAsync(Horizontal, competing);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            using var problem = JsonDocument.Parse(await second.Content.ReadAsByteArrayAsync());
            Assert.AreEqual("PDF処理の同時実行上限に達しました。", problem.RootElement.GetProperty("title").GetString());
            using var drawing = await test.Client.PostAsync("/api/pdf/to-images?format=png&dpi=72", competing);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, drawing.StatusCode);
            await PostPdfAsync(test, "/api/pdf/optimize", PdfTestContext.Fixture, "optimized.pdf");
        }
        finally
        {
            using var response = await request;
            Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
            options.TesseractPath = originalTool;
        }
        Assert.IsFalse(Directory.Exists("/proc/" + await File.ReadAllTextAsync(marker)));
        test.AssertClean();
        await PostPdfAsync(test, Horizontal, PdfTestContext.Fixture, "ocr.pdf");
        test.AssertClean();
    }

    private static readonly byte[][] Colors = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [0, 255, 255], [255, 255, 0]];

    private static JsonObject FlattenFixture()
    {
        var json = JsonNode.Parse("""
            {"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{
              "obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R","/AcroForm":"5 0 R"}},
              "obj:2 0 R":{"value":{"/Type":"/Pages","/Kids":["3 0 R"],"/Count":1}},
              "obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,300,200],
                "/Contents":"4 0 R","/Resources":{"/Font":{"/F1":{"/Type":"/Font","/Subtype":"/Type1","/BaseFont":"/Helvetica"}}},
                "/Annots":["6 0 R","8 0 R","10 0 R"]}},
              "obj:5 0 R":{"value":{"/Fields":["6 0 R"],"/NeedAppearances":false}},
              "obj:6 0 R":{"value":{"/Type":"/Annot","/Subtype":"/Widget","/FT":"/Tx","/T":"u:sample",
                "/V":"u:日本語","/Rect":[30,75,180,110],"/P":"3 0 R","/F":4,"/AP":{"/N":"7 0 R"}}},
              "obj:8 0 R":{"value":{"/Type":"/Annot","/Subtype":"/Ink","/Rect":[30,30,130,60],
                "/InkList":[[35,35,60,52,90,35]],"/F":4,"/AP":{"/N":"9 0 R"}}},
              "obj:10 0 R":{"value":{"/Type":"/Annot","/Subtype":"/Text","/Rect":[200,30,220,50],
                "/Contents":"u:コメント","/F":4,"/AP":{"/N":"11 0 R"}}},
              "trailer":{"value":{"/Root":"1 0 R","/Size":12}}
            }]}
            """)!.AsObject();
        var objects = OverlayFixtures.Objects(json);
        objects["obj:4 0 R"] = Stream("BT /F1 16 Tf 30 155 Td (Hello PDF) Tj ET");
        const string japanese = "日本語";
        var glyphs = japanese.Select(character =>
        {
            Assert.IsTrue(FillTemplateGlyphs.TryGet(character, out var glyph, out _));
            return glyph;
        }).ToArray();
        var form = Appearance(150, 35);
        form["/Resources"] = JsonNode.Parse("""{"/Font":{"/J":"12 0 R"}}""");
        var codes = string.Concat(glyphs.Select(glyph => glyph.ToString("X4")));
        objects["obj:7 0 R"] = Stream($"BT /J 20 Tf 4 8 Td <{codes}> Tj ET", form);
        objects["obj:12 0 R"] = JsonNode.Parse("""
            {"value":{"/Type":"/Font","/Subtype":"/Type0","/BaseFont":"/BIZUDPGothic-Regular",
              "/Encoding":"/Identity-H","/DescendantFonts":["13 0 R"],"/ToUnicode":"16 0 R"}}
            """);
        objects["obj:13 0 R"] = JsonNode.Parse("""
            {"value":{"/Type":"/Font","/Subtype":"/CIDFontType2","/BaseFont":"/BIZUDPGothic-Regular",
              "/CIDSystemInfo":{"/Registry":"u:Adobe","/Ordering":"u:Identity","/Supplement":0},
              "/FontDescriptor":"14 0 R","/CIDToGIDMap":"/Identity","/DW":1000}}
            """);
        objects["obj:14 0 R"] = JsonNode.Parse("""
            {"value":{"/Type":"/FontDescriptor","/FontName":"/BIZUDPGothic-Regular","/Flags":4,
              "/FontBBox":[-500,-500,1500,1500],"/Ascent":880,"/Descent":-120,
              "/CapHeight":700,"/ItalicAngle":0,"/StemV":80,"/FontFile2":"15 0 R"}}
            """);
        var fonts = Environment.GetEnvironmentVariable("Pdf__RenderFontDir") ?? new PdfOptions().RenderFontDir;
        var font = File.ReadAllBytes(Path.Combine(fonts, "BIZUDPGothic-Regular.ttf"));
        objects["obj:15 0 R"] = new JsonObject { ["stream"] = new JsonObject
        {
            ["dict"] = new JsonObject { ["/Length1"] = font.Length }, ["data"] = Convert.ToBase64String(font)
        }};
        var mappings = string.Join('\n', japanese.Select((character, index) => $"<{glyphs[index]:X4}> <{(int)character:X4}>"));
        objects["obj:16 0 R"] = Stream($"""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
            /CMapName /Adobe-Identity-UCS def
            /CMapType 2 def
            1 begincodespacerange <0000> <FFFF> endcodespacerange
            3 beginbfchar
            {mappings}
            endbfchar
            endcmap CMapName currentdict /CMap defineresource pop end end
            """);
        objects["trailer"]!["value"]!["/Size"] = 17;
        objects["obj:9 0 R"] = Stream("0 0 1 RG 2 w 5 5 m 30 22 l 60 5 l S", Appearance(100, 30));
        objects["obj:11 0 R"] = Stream("1 0 0 rg 0 0 20 20 re f", Appearance(20, 20));
        return json;
    }

    // Adds a link without an appearance and a popup belonging to the comment.
    private static JsonObject WithLinkAndPopup(JsonObject fixture)
    {
        var objects = OverlayFixtures.Objects(fixture);
        objects["obj:17 0 R"] = JsonNode.Parse("""
            {"value":{"/Type":"/Annot","/Subtype":"/Link","/Rect":[30,150,130,175],"/Border":[0,0,0],
              "/A":{"/S":"/URI","/URI":"u:https://example.com/"}}}
            """);
        objects["obj:18 0 R"] = JsonNode.Parse("""
            {"value":{"/Type":"/Annot","/Subtype":"/Popup","/Rect":[180,60,290,120],"/Parent":"10 0 R"}}
            """);
        objects["obj:10 0 R"]!["value"]!["/Popup"] = "18 0 R";
        objects["obj:3 0 R"]!["value"]!["/Annots"]!.AsArray().Add("17 0 R");
        objects["obj:3 0 R"]!["value"]!["/Annots"]!.AsArray().Add("18 0 R");
        objects["trailer"]!["value"]!["/Size"] = 19;
        return fixture;
    }

    private static async Task<string[]> AnnotationSubtypesAsync(string path)
    {
        using var fixtures = new OverlayJob();
        using var metadata = await fixtures.Metadata(path);
        return metadata.Pages.SelectMany(page =>
        {
            if (!metadata.Dictionary(page).TryGetProperty("/Annots", out var annotations)) return [];
            return metadata.Resolve(annotations, default).EnumerateArray()
                .Select(annotation => metadata.Resolve(annotation, default).GetProperty("/Subtype").GetString()!);
        }).ToArray();
    }

    private static JsonObject Appearance(int width, int height) => new()
        { ["/Type"] = "/XObject", ["/Subtype"] = "/Form", ["/BBox"] = new JsonArray(0, 0, width, height) };

    private static JsonObject Stream(string contents, JsonObject? dictionary = null)
        => new() { ["stream"] = new JsonObject
            { ["dict"] = dictionary ?? new JsonObject(), ["data"] = Convert.ToBase64String(Encoding.ASCII.GetBytes(contents)) } };

    private static JsonObject LayoutFixture()
    {
        var root = OverlayFixtures.Metadata();
        root.Remove("pages");
        var objects = OverlayFixtures.Objects(root);
        var parent = OverlayFixtures.Parent(root);
        parent.Clear();
        var kids = new JsonArray();
        parent["/Type"] = "/Pages";
        parent["/Count"] = Colors.Length;
        parent["/Kids"] = kids;
        for (var i = 0; i < Colors.Length; i++)
        {
            var page = 3 + 2 * i;
            var rotated = i is 1 or 3;
            kids.Add($"{page} 0 R");
            objects[$"obj:{page} 0 R"] = new JsonObject { ["value"] = new JsonObject
            {
                ["/Type"] = "/Page", ["/Parent"] = "2 0 R", ["/Resources"] = new JsonObject(),
                ["/MediaBox"] = new JsonArray(-40, -20, 230, 310), ["/CropBox"] = new JsonArray(10, 20, 210, 300),
                ["/TrimBox"] = new JsonArray(50, 60, 170, 250),
                ["/Rotate"] = rotated ? 90 : i == 4 ? 270 : 0,
                ["/Contents"] = $"{page + 1} 0 R"
            }};
            var rgb = string.Join(' ', Colors[i].Select(value => value / 255));
            var marker = rotated ? " 0 0 1 rg 30 40 40 40 re f" : "";
            objects[$"obj:{page + 1} 0 R"] = Stream($"1 0 1 rg -40 -20 270 330 re f {rgb} rg 10 20 200 280 re f{marker}");
        }
        // Last page stays portrait despite /Rotate 270.
        objects["obj:11 0 R"]!["value"]!["/CropBox"] = new JsonArray(10, 20, 210, 180);
        objects["trailer"]!["value"]!["/Size"] = 3 + 2 * Colors.Length;
        return root;
    }

    private static async Task<byte[]> ScanAsync(PdfTestContext test, bool vertical)
    {
        var source = JsonNode.Parse(PdfcpuProcessor.StartupBlankJson)!.AsObject();
        OverlayFixtures.Page(source)["/MediaBox"] = new JsonArray(0, 0, 300, 300);
        var blank = await PdfAsync(test, source, vertical ? "blank-vertical" : "blank-horizontal");
        var fields = vertical
            ? Japanese.Select((character, index) => new { page = 1, x = 0.73m, y = 0.12m + 0.09m * index,
                width = 0.15m, height = 0.09m, fontSize = 24, align = "center", text = character.ToString() }).ToArray()
            : [new { page = 1, x = 0.16m, y = 0.17m, width = 0.68m, height = 0.12m,
                fontSize = 24, align = "center", text = Japanese }];
        using var fill = PdfFillTemplateTests.Form(blank, JsonSerializer.SerializeToUtf8Bytes(fields));
        using var filled = await test.Client.PostAsync("/api/pdf/fill-template", fill);
        Assert.AreEqual(HttpStatusCode.OK, filled.StatusCode, await filled.Content.ReadAsStringAsync());
        var png = await RenderPngAsync(test, await filled.Content.ReadAsByteArrayAsync(), 200);
        using var images = new MultipartFormDataContent();
        images.Add(new ByteArrayContent(png), "file", "scan.png");
        using var scanned = await test.Client.PostAsync("/api/pdf/from-images?quality=original", images);
        Assert.AreEqual(HttpStatusCode.OK, scanned.StatusCode, await scanned.Content.ReadAsStringAsync());
        return await scanned.Content.ReadAsByteArrayAsync();
    }

    // Draws text on a blank A4 page with fill-template and scans it at 200 dpi into an image-only PDF.
    private static async Task<byte[]> ScanPageAsync(PdfTestContext test, string name, object[] fields)
    {
        var blank = await PdfAsync(test, JsonNode.Parse(PdfcpuProcessor.StartupBlankJson)!.AsObject(), name + "-blank");
        using var fill = PdfFillTemplateTests.Form(blank, JsonSerializer.SerializeToUtf8Bytes(fields));
        using var filled = await test.Client.PostAsync("/api/pdf/fill-template", fill);
        Assert.AreEqual(HttpStatusCode.OK, filled.StatusCode, await filled.Content.ReadAsStringAsync());
        var png = await RenderPngAsync(test, await filled.Content.ReadAsByteArrayAsync(), 200);
        using var images = new MultipartFormDataContent();
        images.Add(new ByteArrayContent(png), "file", "scan.png");
        using var scanned = await test.Client.PostAsync("/api/pdf/from-images?quality=original", images);
        Assert.AreEqual(HttpStatusCode.OK, scanned.StatusCode, await scanned.Content.ReadAsStringAsync());
        return await scanned.Content.ReadAsByteArrayAsync();
    }

    private static async Task<byte[]> RotateWithCropAsync(PdfTestContext test, string path)
    {
        var turned = Path.Combine(test.Root, "turn.pdf");
        var inverse = Path.Combine(test.Root, "inverse.pdf");
        var rotated = Path.Combine(test.Root, "rotated.pdf");
        var output = Path.Combine(test.Root, "cropped.pdf");
        Assert.AreEqual(0, (await test.QpdfAsync(path, "--rotate=+270", turned)).ExitCode);
        Assert.AreEqual(0, (await test.QpdfAsync(turned, "--flatten-rotation", inverse)).ExitCode);
        Assert.AreEqual(0, (await test.QpdfAsync(inverse, "--rotate=+90", rotated)).ExitCode);
        var (code, json) = await test.QpdfAsync("--json", "--json-key=pages", "--json-key=qpdf", rotated);
        Assert.AreEqual(0, code);
        var metadata = JsonNode.Parse(json)!.AsObject();
        var pageRef = "obj:" + metadata["pages"]![0]!["object"]!.GetValue<string>();
        var page = OverlayFixtures.Objects(metadata)[pageRef]!.DeepClone();
        var width = page["value"]!["/MediaBox"]![2]!.GetValue<double>();
        var height = page["value"]!["/MediaBox"]![3]!.GetValue<double>();
        page["value"]!["/CropBox"] = new JsonArray(10, 14, width - 6, height - 21);
        page["value"]!["/TrimBox"] = new JsonArray(15, 18, width - 9, height - 26);
        var update = new JsonObject { ["qpdf"] = new JsonArray(new JsonObject { ["jsonversion"] = 2 },
            new JsonObject { [pageRef] = page }) };
        var updatePath = Path.Combine(test.Root, "crop.json");
        await File.WriteAllTextAsync(updatePath, update.ToJsonString());
        Assert.AreEqual(0, (await test.QpdfAsync(rotated, "--update-from-json=" + updatePath, output)).ExitCode);
        return await File.ReadAllBytesAsync(output);
    }

    private static async Task<byte[]> RenderPngAsync(PdfTestContext test, byte[] pdf, int dpi = 72)
    {
        using var form = PdfTestContext.FileForm(pdf);
        using var response = await test.Client.PostAsync($"/api/pdf/to-images?format=png&dpi={dpi}", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var image = archive.Entries.Single().Open();
        using var result = new MemoryStream();
        await image.CopyToAsync(result);
        return result.ToArray();
    }

    private static async Task<byte[]> PdfAsync(PdfTestContext test, JsonObject json, string name)
    {
        var source = Path.Combine(test.Root, name + ".json");
        var output = Path.Combine(test.Root, name + ".pdf");
        await File.WriteAllTextAsync(source, json.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        Assert.AreEqual(0, (await test.QpdfAsync("--json-input", source, output)).ExitCode);
        return await File.ReadAllBytesAsync(output);
    }

    private static async Task<byte[]> PostPdfAsync(PdfTestContext test, string endpoint, byte[] pdf, string filename)
    {
        using var form = PdfTestContext.FileForm(pdf);
        using var response = await test.Client.PostAsync(endpoint, form);
        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"{endpoint}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(filename, response.Content.Headers.ContentDisposition?.FileName);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<byte[]> ToolAsync(string exe, string[] args, int limit = 8 * 1024 * 1024)
    {
        var result = await ExternalProcessRunner.RunAsync(new(exe, args, StdoutLimit: limit), default);
        Assert.AreEqual(0, result.ExitCode, exe);
        Assert.IsNotNull(result.Stdout);
        return result.Stdout;
    }

    private static async Task<string> ExtractTextAsync(string path, params string[] options)
        => Encoding.UTF8.GetString(await ToolAsync("pdftotext", [.. options, path, "-"]));

    private static async Task AssertOcrTextMatchesForegroundAsync(byte[] sourceImage, string outputPath, int page)
    {
        var pageNumber = page.ToString(CultureInfo.InvariantCulture);
        var bbox = await ExtractTextAsync(outputPath, "-f", pageNumber, "-l", pageNumber, "-cropbox", "-bbox");
        var words = XDocument.Parse(bbox).Descendants().Where(node => node.Name.LocalName == "word").ToArray();
        static double Number(XElement element, string attribute)
            => double.Parse(element.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);
        var image = Foreground(sourceImage);
        // At 72 dpi the displayed raster dimensions also give the rotated page size in points.
        // pdftotext's page dimensions are unrotated even though the word bounds are rotated.
        var (width, height, _) = PpmHeader(sourceImage);
        Assert.AreEqual(image.Left, words.Min(word => Number(word, "xMin")) / width, 0.05);
        Assert.AreEqual(image.Top, words.Min(word => Number(word, "yMin")) / height, 0.05);
        Assert.AreEqual(image.Right, words.Max(word => Number(word, "xMax")) / width, 0.05);
        Assert.AreEqual(image.Bottom, words.Max(word => Number(word, "yMax")) / height, 0.05);
    }

    private static string Normalize(string text)
        => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));

    private static Task<byte[]> RenderWithPopplerAsync(string path, int page = 1)
        => ToolAsync("pdftoppm", ["-cropbox", "-r", "72", "-f", page.ToString(), "-l", page.ToString(), path]);

    private static (int Width, int Height, int Start) PpmHeader(byte[] ppm)
    {
        var dimensionsEnd = Array.IndexOf(ppm, (byte)'\n', 3);
        var dimensions = Encoding.ASCII.GetString(ppm, 3, dimensionsEnd - 3).Split(' ');
        var start = Array.IndexOf(ppm, (byte)'\n', dimensionsEnd + 1) + 1;
        return (int.Parse(dimensions[0]), int.Parse(dimensions[1]), start);
    }

    private static byte[] Pixel(byte[] ppm, double x, double y)
    {
        var (width, height, start) = PpmHeader(ppm);
        var offset = start + 3 * ((int)(y * height) * width + (int)(x * width));
        return ppm[offset..(offset + 3)];
    }

    private static (double Left, double Top, double Right, double Bottom) Foreground(byte[] ppm)
    {
        var (width, height, start) = PpmHeader(ppm);
        var left = width;
        var upper = height;
        var right = 0;
        var lower = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = start + 3 * (y * width + x);
            if (ppm[offset] > 95 || ppm[offset + 1] > 95 || ppm[offset + 2] > 95) continue;
            left = Math.Min(left, x);
            upper = Math.Min(upper, y);
            right = Math.Max(right, x);
            lower = Math.Max(lower, y);
        }
        return ((double)left / width, (double)upper / height, (double)right / width, (double)lower / height);
    }
}
