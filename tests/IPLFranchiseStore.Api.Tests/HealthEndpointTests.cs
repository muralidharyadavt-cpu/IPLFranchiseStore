using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace IPLFranchiseStore.Api.Tests;

public class HealthEndpointTests
{
    [Fact]
    public async Task GetHealth_ReturnsOkWithHealthyJson()
    {
        await using var application = CreateUnconfiguredApp();
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(body);
        Assert.Equal("healthy", body["status"]);
    }

    [Fact]
    public async Task MissingCosmosConfiguration_ReturnsSanitizedUnavailableResponses()
    {
        await using var application = CreateUnconfiguredApp();
        using var client = application.CreateClient();
        foreach (var path in new[] { "/api/v1/health/dependencies", "/api/v1/products" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Exception", body);
            Assert.DoesNotContain("Key", body);
        }
    }

    private static WebApplicationFactory<Program> CreateUnconfiguredApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Cosmos:Key"] = "" })));
}
