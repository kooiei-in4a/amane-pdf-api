using Microsoft.AspNetCore.Mvc.Testing;

namespace Amane.Pdf.Api.Tests;

[TestClass]
public sealed class HealthEndpointTests
{
    [TestMethod]
    public async Task Healthz_ReturnsHealthy()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", body);
    }
}
