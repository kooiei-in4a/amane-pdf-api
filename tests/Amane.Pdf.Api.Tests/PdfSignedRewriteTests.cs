using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfSignedRewriteTests
{
    private static readonly (string Path, int OutputPages)[] Operations =
    [
        ("rotate?angle=90&pages=1", 3),
        ("optimize", 3),
        ("compress?level=standard", 3),
        ("compress?maxBytes=102400", 3),
        ("merge", 6),
        ("split?ranges=1,2", 2),
        ("extract?pages=1", 1),
        ("delete-pages?pages=3", 2),
        ("reorder?pages=2,1,3", 3)
    ];

    private static IEnumerable<(string Path, int OutputPages)> AvailableOperations
        => Operations.Where(operation => OperatingSystem.IsLinux() ||
            !operation.Path.StartsWith("compress?", StringComparison.Ordinal));

    // Synthetic three-page PDFs with a signature widget on page 3. The signed fixtures
    // contain detached CMS signatures made with OpenSSL and a temporary self-signed
    // certificate. Both signatures were verified with pdfsig; no private key is stored.
    [TestMethod]
    [DataRow("signed.pdf")]
    [DataRow("signed-without-type.pdf")]
    public async Task Rewrite_RejectsAppliedSignatureBeforeReturningPdfOrZip(string fixture)
    {
        await using var test = new PdfTestContext();
        var path = FixturePath(fixture);
        Assert.AreEqual(0, (await test.QpdfAsync("--check", path)).ExitCode);
        var signed = await File.ReadAllBytesAsync(path);
        var plain = PdfTestContext.Fixture;

        foreach (var (operation, _) in AvailableOperations)
        {
            using var form = operation == "merge"
                ? PdfTestContext.MergeForm(plain, signed)
                : PdfTestContext.FileForm(signed);
            await AssertRejectedAsync(test, operation, form, signed: true);
        }

        using var reversed = PdfTestContext.MergeForm(signed, plain);
        await AssertRejectedAsync(test, "merge", reversed, signed: true);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Rewrite_AcceptsUnsignedSignatureFieldAndPlainPdf(bool unsignedField)
    {
        await using var test = new PdfTestContext();
        var input = unsignedField
            ? await File.ReadAllBytesAsync(FixturePath("unsigned-signature-field.pdf"))
            : await test.CreateRotationMarkedPdfAsync(3);

        foreach (var (operation, pages) in AvailableOperations)
        {
            using var form = operation == "merge"
                ? PdfTestContext.MergeForm(input, input)
                : PdfTestContext.FileForm(input);
            using var response = await test.Client.PostAsync("/api/pdf/" + operation, form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, operation);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (operation.StartsWith("split?", StringComparison.Ordinal))
            {
                Assert.AreEqual("application/zip", response.Content.Headers.ContentType?.MediaType, operation);
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                Assert.HasCount(pages, zip.Entries);
                foreach (var entry in zip.Entries)
                {
                    await using var pdf = entry.Open();
                    using var buffer = new MemoryStream();
                    await pdf.CopyToAsync(buffer);
                    await test.AssertValidPdfAsync(buffer.ToArray(), 1);
                }
            }
            else
            {
                Assert.AreEqual("application/pdf", response.Content.Headers.ContentType?.MediaType, operation);
                await test.AssertValidPdfAsync(bytes, pages);
                if (operation.StartsWith("reorder?", StringComparison.Ordinal))
                    CollectionAssert.AreEqual(unsignedField ? new[] { 90, 0, 0 } : new[] { 90, 0, 180 },
                        await test.ReadPageRotationsAsync(bytes), operation);
            }
            test.AssertClean();
        }
    }

    [TestMethod]
    public async Task Rewrite_EncryptedPdfKeepsGenericRejection()
    {
        await using var test = new PdfTestContext();
        var signed = await File.ReadAllBytesAsync(FixturePath("signed.pdf"));
        var encrypted = await test.CreateEncryptedPdfAsync("fixture-user", "fixture-owner", input: signed);

        foreach (var (operation, _) in AvailableOperations)
        {
            using var form = operation == "merge"
                ? PdfTestContext.MergeForm(signed, encrypted)
                : PdfTestContext.FileForm(encrypted);
            await AssertRejectedAsync(test, operation, form, signed: false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Rewrite_AcceptsLargeUnsignedPdf(bool wideMetadata)
    {
        await using var test = new PdfTestContext();
        var input = TaggedPdf(wideMetadata);
        if (wideMetadata)
        {
            // Keep the serialized PDF within the split output budget while the JSON exceeds 32 MiB.
            var unpacked = Path.Combine(test.Root, "large-unpacked.pdf");
            var packed = Path.Combine(test.Root, "large-packed.pdf");
            await File.WriteAllBytesAsync(unpacked, input);
            Assert.AreEqual(0, (await test.QpdfAsync("--object-streams=generate", unpacked, packed)).ExitCode);
            input = await File.ReadAllBytesAsync(packed);
        }
        foreach (var (operation, pages) in AvailableOperations)
        {
            using var form = operation == "merge"
                ? PdfTestContext.MergeForm(input, input)
                : PdfTestContext.FileForm(input);
            using var response = await test.Client.PostAsync("/api/pdf/" + operation, form);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, operation);
            if (operation.StartsWith("split?", StringComparison.Ordinal))
            {
                using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync());
                Assert.HasCount(pages, archive.Entries);
            }
            else
            {
                await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), pages);
            }
            test.AssertClean();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Rewrite_RejectsSignatureDictionaryInsideObjectStream(bool denyReopeningStdout)
    {
        if (denyReopeningStdout && !OperatingSystem.IsLinux()) return;
        await using var test = new PdfTestContext();
        var packed = Path.Combine(test.Root, "packed.pdf");
        // Repacking invalidates the CMS signature. The API checks for the signature dictionary.
        Assert.AreEqual(0, (await test.QpdfAsync("--object-streams=generate",
            FixturePath("signed-without-type.pdf"), packed)).ExitCode);
        Assert.IsFalse(await PdfFormDetector.MayHaveSignatureNameAsync(packed, default));
        if (denyReopeningStdout)
        {
            var wrapper = await PdfCompressTests.WrapperAsync(test,
                "for arg do [ \"$arg\" != /dev/stdout ] || exit 2; done\nexec qpdf \"$@\"\n");
            test.Factory.Services.GetRequiredService<IOptions<PdfOptions>>().Value.QpdfPath = wrapper;
        }
        using var form = PdfTestContext.FileForm(await File.ReadAllBytesAsync(packed));
        await AssertRejectedAsync(test, "optimize", form, signed: true);
    }

    [TestMethod]
    public async Task Merge_AcceptsTenPdfsWithLargeObjectStreamsWithinSharedDeadline()
    {
        await using var test = new PdfTestContext();
        var unpacked = Path.Combine(test.Root, "unpacked.pdf");
        var packed = Path.Combine(test.Root, "packed.pdf");
        await File.WriteAllBytesAsync(unpacked, TaggedPdf(wideMetadata: false));
        Assert.AreEqual(0, (await test.QpdfAsync("--object-streams=generate", unpacked, packed)).ExitCode);
        var xref = await test.QpdfAsync("--show-xref", packed);
        Assert.AreEqual(0, xref.ExitCode);
        Assert.IsTrue(xref.Output.Contains(": compressed; stream =", StringComparison.Ordinal));

        var input = await File.ReadAllBytesAsync(packed);
        using var form = PdfTestContext.MergeForm(Enumerable.Repeat(input, 10).ToArray());
        using var response = await test.Client.PostAsync("/api/pdf/merge", form);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await test.AssertValidPdfAsync(await response.Content.ReadAsByteArrayAsync(), 30);
        test.AssertClean();
    }

    private static byte[] TaggedPdf(bool wideMetadata)
    {
        var count = wideMetadata ? 1400 : 60_000;
        var objects = new List<byte[]>
        {
            CompressFixtures.Text("<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>"),
            CompressFixtures.Text("<< /Type /Pages /Count 3 /Kids [3 0 R 4 0 R 5 0 R] >>")
        };
        var page = CompressFixtures.Text("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>");
        objects.AddRange([page, page, page]);
        var tree = new StringBuilder("<< /Type /StructTreeRoot /K [");
        for (var i = 0; i < count; i++) tree.Append(i + 7).Append(" 0 R ");
        objects.Add(CompressFixtures.Text(tree.Append("] >>").ToString()));
        // qpdf represents these binary strings as hex. The resulting JSON exceeds 32 MiB.
        var text = wideMetadata ? new string('\u0001', 13_000) : "Synthetic tagged paragraph.";
        var paragraph = CompressFixtures.Text("<< /Type /StructElem /S /P /P 6 0 R /ActualText (" + text + ") >>");
        for (var i = 0; i < count; i++) objects.Add(paragraph);
        return CompressFixtures.Objects(objects);
    }

    private static string FixturePath(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", file);

    private static async Task AssertRejectedAsync(PdfTestContext test, string operation, HttpContent form, bool signed)
    {
        using var response = await test.Client.PostAsync("/api/pdf/" + operation, form);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode, operation);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType, operation);
        Assert.IsNull(response.Content.Headers.ContentDisposition, operation);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var hasReason = problem.RootElement.TryGetProperty("reason", out var reason);
        Assert.AreEqual(signed, hasReason, operation);
        if (signed)
        {
            Assert.AreEqual("signature-structure", reason.GetString(), operation);
            Assert.AreEqual("このPDFには電子署名や署名関連の設定があるため加工できません。",
                problem.RootElement.GetProperty("title").GetString(), operation);
        }
        test.AssertClean();
    }
}
