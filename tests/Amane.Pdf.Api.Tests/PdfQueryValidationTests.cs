namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class PdfQueryValidationTests
{
    // Each endpoint with a valid query. Keys are written in mixed case on purpose: the shared
    // contract is case-insensitive. Endpoints without a query use a bare "?" (empty query).
    private static readonly string[] Endpoints = [
        "protect?", "restrict?", "unlock?", "optimize?", "merge?", "split?EVERY=1",
        "rotate?Angle=90&PAGES=1", "extract?Pages=1", "delete-pages?PAGES=1", "reorder?Pages=1"
    ];
    private static readonly string[] LinuxEndpoints = [
        "redact?", "compress?LEVEL=standard", "clean?", "from-images?Quality=standard",
        "page-numbers?Position=bottom-center&FORMAT=number", "fill-template?",
        "watermark?Style=corner&COLOR=gray", "stamp-image?PAGE=1&X=0.1&Y=0.1&Width=0.2"
    ];

    private static IEnumerable<string> All() => Endpoints.Concat(OperatingSystem.IsLinux() ? LinuxEndpoints : []);

    private static string With(string endpoint, string extra)
        => "/api/pdf/" + endpoint + (endpoint.EndsWith('?') ? "" : "&") + extra;

    [TestMethod]
    public async Task AllPdfEndpointsRejectUnknownQueryKeysBeforeReadingUpload()
    {
        await using var test = new PdfTestContext();
        using var body = PdfTestContext.FileForm(PdfTestContext.Fixture);
        foreach (var endpoint in All())
            await test.AssertRejectedBeforeReadingBodyAsync(With(endpoint, "unknown=1"), body);
    }

    [TestMethod]
    public async Task AllPdfEndpointsRejectRepeatedAllowedKeysBeforeReadingUpload()
    {
        await using var test = new PdfTestContext();
        using var body = PdfTestContext.FileForm(PdfTestContext.Fixture);
        foreach (var endpoint in All().Where(endpoint => !endpoint.EndsWith('?')))
        {
            // Repeat the first allowed key with its own value, in different case.
            var first = endpoint[(endpoint.IndexOf('?') + 1)..].Split('&')[0];
            await test.AssertRejectedBeforeReadingBodyAsync(With(endpoint, first.ToLowerInvariant()), body);
        }
    }

    [TestMethod]
    public async Task AllPdfEndpointsAcceptAllowedKeysInAnyCaseAndBareQuestionMark()
    {
        await using var test = new PdfTestContext();
        using var body = PdfTestContext.FileForm(PdfTestContext.Fixture);
        foreach (var endpoint in All())
            await test.AssertQueryAcceptedAsync("/api/pdf/" + endpoint, body);
    }
}
