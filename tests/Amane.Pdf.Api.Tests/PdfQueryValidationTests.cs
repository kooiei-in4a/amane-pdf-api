namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfQueryValidationTests
{
    [TestMethod]
    public async Task AllPdfEndpointsRejectUnknownQueryKeysBeforeReadingUpload()
    {
        await using var test = new PdfTestContext();
        using var body = PdfTestContext.FileForm(PdfTestContext.Fixture);
        string[] endpoints = [
            "protect", "restrict", "unlock", "optimize", "merge", "split?every=1",
            "rotate?angle=90&pages=1", "extract?pages=1", "delete-pages?pages=1", "reorder?pages=1"
        ];
        string[] linuxEndpoints = [
            "redact", "compress?level=standard", "clean", "from-images?quality=standard",
            "page-numbers?position=bottom-center&format=number", "fill-template",
            "watermark?style=corner&color=gray", "stamp-image?page=1&x=0.1&y=0.1&width=0.2"
        ];

        foreach (var endpoint in endpoints.Concat(OperatingSystem.IsLinux() ? linuxEndpoints : []))
        {
            var separator = endpoint.Contains('?') ? '&' : '?';
            await test.AssertRejectedBeforeReadingBodyAsync($"/api/pdf/{endpoint}{separator}unknown=1", body);
        }
    }
}
