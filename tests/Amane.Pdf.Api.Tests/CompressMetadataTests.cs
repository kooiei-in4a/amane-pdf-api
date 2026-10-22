using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class CompressMetadataTests
{
    [TestMethod]
    [TestCategory("DefensiveMetadata")]
    public async Task DefensiveAliasChains_ReleaseSpoolBudget_ForReuse()
    {
        if (!PdfCompressTests.Linux()) return;
        var root = Path.Combine(Path.GetTempPath(), "metadata-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Common PDF validation rejects reference-to-reference objects. This tests
            // defensive resolution and accounting directly, without starting an API host.
            const string json = """{"qpdf":[{"jsonversion":2},{"obj:11 0 R":{"value":"12 0 R"},"obj:12 0 R":{"value":"13 0 R"},"obj:13 0 R":{"value":{"/XObject":{"/Im0":"4 0 R"}}}}]}""";
            var wrapper = Path.Combine(root, "metadata.sh");
            await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nfor arg do last=$arg; done\nprintf '%s' '" + json + "' > \"$last\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var options = new PdfOptions { QpdfPath = wrapper, CompressSpoolLimitBytes = 3 * 4096 };
            using var files = new TemporaryPdfFiles(root);
            await File.WriteAllBytesAsync(files.InputPath, PdfTestContext.Fixture);
            var owner = new PdfCompressProcessor(options, files);
            var metadata = new CompressMetadata(owner);
            for (var batch = 0; batch < 2; batch++)
            {
                await metadata.FetchChainsAsync([new(11, 0)], CancellationToken.None);
                var paths = metadata.Files.ToArray();
                Assert.AreEqual(3, paths.Length);
                Assert.AreEqual(3 * 4096L, owner.Capacity.Used);
                metadata.DeleteExcept(new HashSet<PdfObjectRef>());
                Assert.IsFalse(paths.Any(File.Exists));
                Assert.AreEqual(0L, owner.Capacity.Used);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
