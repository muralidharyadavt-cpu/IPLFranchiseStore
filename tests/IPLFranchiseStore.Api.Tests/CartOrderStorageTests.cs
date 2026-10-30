using System.Net;
using System.Net.Http.Json;
using IPLFranchiseStore.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IPLFranchiseStore.Api.Tests;

public sealed class CartOrderStorageTests(CartTestDatabase db) : IClassFixture<CartTestDatabase>
{
    [Theory]
    [InlineData("database", "cart")]
    [InlineData("database", "order")]
    [InlineData("database", "checkout")]
    [InlineData("container", "cart")]
    [InlineData("container", "order")]
    [InlineData("container", "checkout")]
    public async Task MissingInfrastructure_ReturnsSanitized503(string missing, string operation)
    {
        var settings = new CosmosSettings { Endpoint = db.Settings.Endpoint, Key = db.Settings.Key,
            DatabaseName = missing == "database" ? "IPLFranchiseStoreTests-absent-" + Guid.NewGuid().ToString("N") : db.Settings.DatabaseName,
            ContainerName = missing == "container" ? "absent-" + Guid.NewGuid().ToString("N") : "StoreData" };
        using var client = CosmosStore.CreateClient(settings);
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            // Skip provisioning: point only this host's services at a resource that never existed.
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Cosmos:Key"] = "" }));
            host.ConfigureTestServices(services => { services.RemoveAll<CosmosStore>(); services.AddSingleton(new CosmosStore(client, settings)); });
        });
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add("X-Demo-User-Id", "isolated-reader");
        using var response = operation switch
        {
            "cart" => await http.GetAsync("/api/v1/demo-users/isolated-reader/cart"),
            "order" => await http.GetAsync("/api/v1/orders/" + Guid.NewGuid().ToString("D")),
            _ => await http.PostAsJsonAsync("/api/v1/orders/checkout", new CheckoutCommand(Guid.NewGuid().ToString("D"), "empty"))
        };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(settings.Key, body);
        Assert.DoesNotContain(settings.DatabaseName, body);
        Assert.DoesNotContain(settings.ContainerName, body);
    }

    [Theory]
    [InlineData("cart")]
    [InlineData("order")]
    public async Task MissingDocument_PreservesExistingContract(string operation)
    {
        var user = "absent-" + Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, operation == "cart"
            ? $"/api/v1/demo-users/{user}/cart" : "/api/v1/orders/" + Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-Demo-User-Id", user);
        using var response = await db.Http.SendAsync(request);
        Assert.Equal(operation == "cart" ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        if (operation == "cart")
        {
            var cart = await response.Content.ReadFromJsonAsync<CartView>();
            Assert.Equal("empty", cart!.Version);
            Assert.Empty(cart.Lines);
            Assert.False(cart.CanCheckout);
        }
        else Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
}
