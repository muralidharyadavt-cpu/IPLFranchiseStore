using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace IPLFranchiseStore.Api.Tests;

public sealed class AdminSecurityTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task AdminRoutesAreNotMappedOutsideDevelopment(string environment)
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment(environment);
            host.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Cosmos:Key"] = "" }));
        });
        using var http = app.CreateClient();
        foreach (var operation in new[] { "stock-adjustments", "price-updates" })
        {
            using var response = await http.PostAsJsonAsync($"/api/v1/admin/products/mi-jersey/{operation}", new { });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var audits = await http.GetAsync("/api/v1/admin/products/mi-jersey/audits");
        Assert.Equal(HttpStatusCode.NotFound, audits.StatusCode);
        using var cart = await http.GetAsync("/api/v1/demo-users/test-user/cart");
        using var addLine = await http.PostAsJsonAsync("/api/v1/demo-users/test-user/cart/lines", new { });
        Assert.Equal(HttpStatusCode.NotFound, cart.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, addLine.StatusCode);
    }
}
