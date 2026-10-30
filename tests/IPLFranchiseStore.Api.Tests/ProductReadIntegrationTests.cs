using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IPLFranchiseStore.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IPLFranchiseStore.Api.Tests;

public sealed class ProductReadIntegrationTests(CartTestDatabase db) : IClassFixture<CartTestDatabase>
{
    [Theory]
    [InlineData("cart", "details")]
    [InlineData("cart", "audits")]
    [InlineData("cart", "stock")]
    [InlineData("cart", "price")]
    [InlineData("audit", "details")]
    [InlineData("audit", "audits")]
    [InlineData("audit", "stock")]
    [InlineData("audit", "price")]
    [InlineData("guard", "details")]
    [InlineData("guard", "audits")]
    [InlineData("guard", "stock")]
    [InlineData("guard", "price")]
    public async Task NonProductDocumentIds_Return404ProblemDetails(string kind, string operation)
    {
        var id = "non-product-" + Guid.NewGuid().ToString("N");
        object document = kind switch
        {
            "cart" => new CustomerCart(id, CosmosStore.StoreId, "isolated-reader", "v1", [], DateTimeOffset.UtcNow),
            "audit" => new InventoryAudit(id, CosmosStore.StoreId, "missing-product", "isolated-admin", DateTimeOffset.UtcNow, "Fixture", "stockAdjustment", 1, 2, "fixture"),
            _ => new ProductGuard(id, CosmosStore.StoreId)
        };
        var saved = await db.Store.Container.CreateItemAsync(document, new PartitionKey(CosmosStore.StoreId));
        using var response = await Send(db.Http, operation, id);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var preserved = await db.Store.Container.ReadItemAsync<JsonElement>(id, new PartitionKey(CosmosStore.StoreId));
        Assert.Equal(saved.ETag, preserved.ETag);
    }

    [Fact]
    public void ProductRequiredFields_ExplainWhyNonProductDeserializationPreviouslyFailed()
    {
        var cart = new CustomerCart("cart-fixture", CosmosStore.StoreId, "fixture", "v1", [], DateTimeOffset.UtcNow);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Product>(JsonSerializer.Serialize(cart, options), options));
    }

    [Theory]
    [InlineData("details")]
    [InlineData("audits")]
    [InlineData("stock")]
    [InlineData("price")]
    public async Task MalformedProduct_IsNotMisreportedAsMissing(string operation)
    {
        var id = "malformed-" + Guid.NewGuid().ToString("N");
        await db.Store.Container.CreateItemAsync(new { id, storeId = CosmosStore.StoreId, type = "product", name = "Malformed" }, new PartitionKey(CosmosStore.StoreId));
        using var response = await Send(db.Http, operation, id);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("database", "details")]
    [InlineData("database", "audits")]
    [InlineData("database", "stock")]
    [InlineData("database", "price")]
    [InlineData("container", "details")]
    [InlineData("container", "audits")]
    [InlineData("container", "stock")]
    [InlineData("container", "price")]
    public async Task MissingStorageResource_Is503NotProduct404(string missing, string operation)
    {
        var settings = new CosmosSettings { Endpoint = db.Settings.Endpoint, Key = db.Settings.Key,
            DatabaseName = missing == "database" ? "IPLFranchiseStoreTests-absent-" + Guid.NewGuid().ToString("N") : db.Settings.DatabaseName,
            ContainerName = missing == "container" ? "absent-container" : db.Settings.ContainerName };
        using var client = CosmosStore.CreateClient(settings);
        var uninitialized = new CosmosStore(client, settings);
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Cosmos:Key"] = "" }));
            host.ConfigureTestServices(services => { services.RemoveAll<CosmosStore>(); services.AddSingleton(uninitialized); });
        });
        using var http = app.CreateClient();
        using var response = await Send(http, operation, "missing-product");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(settings.Key, body);
        Assert.DoesNotContain(settings.DatabaseName, body);
    }

    [Theory]
    [InlineData("details")]
    [InlineData("audits")]
    [InlineData("stock")]
    [InlineData("price")]
    public async Task MissingItemInAvailableContainer_Is404(string operation)
    {
        using var response = await Send(db.Http, operation, "absent-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    private static Task<HttpResponseMessage> Send(HttpClient http, string operation, string id) => operation switch
    {
        "details" => http.GetAsync("/api/v1/products/" + id),
        "audits" => http.GetAsync($"/api/v1/admin/products/{id}/audits"),
        "stock" => http.PostAsJsonAsync($"/api/v1/admin/products/{id}/stock-adjustments", new StockAdjustment(1, "\"fixture-version\"", "test-admin", "Isolated read regression")),
        _ => http.PostAsJsonAsync($"/api/v1/admin/products/{id}/price-updates", new PriceUpdate(100m, "\"fixture-version\"", "test-admin", "Isolated read regression"))
    };
}
