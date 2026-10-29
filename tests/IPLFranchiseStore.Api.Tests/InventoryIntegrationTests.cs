using System.Net;
using System.Net.Http.Json;
using IPLFranchiseStore.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IPLFranchiseStore.Api.Tests;

public sealed class InventoryIntegrationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> app = null!;
    private HttpClient http = null!;
    private CosmosStore store = null!;
    private readonly string databaseName = "IPLFranchiseStoreTests-" + Guid.NewGuid().ToString("N");
    private static readonly PartitionKey Partition = new(CosmosStore.StoreId);

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddUserSecrets<Program>().AddEnvironmentVariables().Build();
        var settings = config.GetSection("Cosmos").Get<CosmosSettings>() ?? new();
        Assert.False(string.IsNullOrWhiteSpace(settings.Key), "Configure Cosmos:Key in API User Secrets or Cosmos__Key.");
        Assert.Equal("localhost", new Uri(settings.Endpoint).Host);
        app = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:Endpoint"] = settings.Endpoint, ["Cosmos:Key"] = settings.Key,
                ["Cosmos:DatabaseName"] = databaseName, ["Cosmos:ContainerName"] = "StoreData"
            }));
        });
        http = app.CreateClient();
        store = app.Services.GetRequiredService<CosmosStore>();
        // Fail early if startup initialization did not succeed.
        await store.Container.ReadContainerAsync();
    }

    public async Task DisposeAsync()
    {
        if (store is not null) await app.Services.GetRequiredService<CosmosClient>().GetDatabase(databaseName).DeleteAsync();
        http?.Dispose();
        if (app is not null) await app.DisposeAsync();
    }

    private async Task<Product> NewProduct(int stock = 1)
    {
        var product = new Product
        {
            Id = "test-" + Guid.NewGuid().ToString("N"), StoreId = CosmosStore.StoreId,
            Name = "Isolated test product", Franchise = "MI", Description = "Test only", Price = 100m,
            Stock = stock, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        var response = await store.Container.CreateItemAsync(product, Partition);
        return response.Resource with { ETag = response.ETag };
    }

    private async Task<Product> Read(string id) => (await store.Container.ReadItemAsync<Product>(id, Partition)).Resource;
    private async Task<List<InventoryAudit>> Audits(string id) =>
        (await http.GetFromJsonAsync<List<InventoryAudit>>($"/api/v1/admin/products/{id}/audits"))!;
    private Task<HttpResponseMessage> Stock(Product p, int quantity, string? reason = "Manual recount") =>
        http.PostAsJsonAsync($"/api/v1/admin/products/{p.Id}/stock-adjustments",
            new StockAdjustment(quantity, p.ETag, "test-admin", reason));
    private Task<HttpResponseMessage> Price(Product p, decimal price) =>
        http.PostAsJsonAsync($"/api/v1/admin/products/{p.Id}/price-updates",
            new PriceUpdate(price, p.ETag, "test-admin", "Price correction"));

    [Fact]
    public async Task StockOneToZero_ThenFurtherDecrementConflicts_WithoutExtraAudit()
    {
        var product = await NewProduct();
        using var success = await Stock(product, -1);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var zero = (await success.Content.ReadFromJsonAsync<Product>())!;
        Assert.Equal(0, zero.Stock);
        Assert.NotEqual(product.ETag, zero.ETag);
        Assert.Equal(zero.ETag, success.Headers.ETag!.ToString());
        using var failed = await Stock(zero, -1);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal(zero, await Read(product.Id));
        Assert.Single(await Audits(product.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentMutationsWithSameETag_ExactlyOneWins(bool price)
    {
        var product = await NewProduct(2);
        var responses = await Task.WhenAll(price ? new[] { Price(product, 110.25m), Price(product, 120.75m) }
            : new[] { Stock(product, -1), Stock(product, -1) });
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
            var persisted = await Read(product.Id);
            Assert.Equal(price ? 2 : 1, persisted.Stock);
            if (price) Assert.Contains(persisted.Price, new[] { 110.25m, 120.75m });
            Assert.Single(await Audits(product.Id));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulMutation_HasExactlyOneCorrectAudit(bool price)
    {
        var product = await NewProduct();
        var before = DateTimeOffset.UtcNow;
        using var response = await (price ? Price(product, 1999.99m) : Stock(product, 3));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<Product>())!;
        Assert.Equal(updated, await Read(product.Id));
        var audit = Assert.Single(await Audits(product.Id));
        Assert.Equal(product.Id, audit.ProductId);
        Assert.Equal(product.StoreId, audit.StoreId);
        Assert.Equal("test-admin", audit.Actor);
        Assert.Equal(price ? "Price correction" : "Manual recount", audit.Reason);
        Assert.Equal(price ? "priceUpdate" : "stockAdjustment", audit.OperationType);
        Assert.Equal(price ? 100m : 1m, audit.OldValue);
        Assert.Equal(price ? 1999.99m : 4m, audit.NewValue);
        Assert.Equal(TimeSpan.Zero, audit.Timestamp.Offset);
        Assert.InRange(audit.Timestamp, before, DateTimeOffset.UtcNow);
        Assert.Equal(updated.UpdatedAt, audit.Timestamp);
        Assert.Equal(product.CreatedAt, updated.CreatedAt);
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), audit.CorrelationId);
        Assert.False(string.IsNullOrWhiteSpace(audit.Id));
    }

    [Fact]
    public async Task StaleETag_Returns412ProblemDetails_AndNoAdditionalAudit()
    {
        var product = await NewProduct();
        using var first = await Price(product, 101m);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var updated = await Read(product.Id);
        using var stale = await Stock(product, 1);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("application/problem+json", stale.Content.Headers.ContentType!.MediaType);
        var problem = await stale.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(412, problem!.Status);
        Assert.Equal(updated, await Read(product.Id));
        Assert.Single(await Audits(product.Id));
    }

    [Theory]
    [InlineData("zeroQuantity")]
    [InlineData("missingQuantity")]
    [InlineData("fractionalQuantity")]
    [InlineData("overflowQuantity")]
    [InlineData("emptyReason")]
    [InlineData("emptyActor")]
    [InlineData("missingETag")]
    [InlineData("wildcardETag")]
    [InlineData("missingPrice")]
    [InlineData("negativePrice")]
    [InlineData("zeroPrice")]
    [InlineData("fractionalPaise")]
    [InlineData("excessivePrice")]
    public async Task InvalidInputs_Return400AndDoNotWrite(string scenario)
    {
        var product = await NewProduct();
        var body = new Dictionary<string, object?>
        {
            ["quantity"] = 1, ["price"] = 120.50m, ["expectedETag"] = product.ETag,
            ["actor"] = "test-admin", ["reason"] = "Correction"
        };
        var price = scenario.Contains("Price") || scenario == "fractionalPaise";
        switch (scenario)
        {
            case "zeroQuantity": body["quantity"] = 0; break;
            case "missingQuantity": body.Remove("quantity"); break;
            case "fractionalQuantity": body["quantity"] = 0.5m; break;
            case "overflowQuantity": body["quantity"] = 2147483648L; break;
            case "emptyReason": body["reason"] = " "; break;
            case "emptyActor": body["actor"] = " "; break;
            case "missingETag": body.Remove("expectedETag"); break;
            case "wildcardETag": body["expectedETag"] = "*"; break;
            case "missingPrice": body.Remove("price"); break;
            case "negativePrice": body["price"] = -1; break;
            case "zeroPrice": body["price"] = 0; break;
            case "fractionalPaise": body["price"] = 1.001m; break;
            case "excessivePrice": body["price"] = 1000000000m; break;
        }
        using var response = await http.PostAsJsonAsync($"/api/v1/admin/products/{product.Id}/{(price ? "price-updates" : "stock-adjustments")}", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(product, await Read(product.Id));
        Assert.Empty(await Audits(product.Id));
    }

    [Fact]
    public async Task MissingProduct_Returns404()
    {
        var product = await NewProduct();
        var missing = product with { Id = "missing-product" };
        using var stock = await Stock(missing, 1);
        using var price = await Price(missing, 99m);
        using var audits = await http.GetAsync("/api/v1/admin/products/missing-product/audits");
        Assert.Equal(HttpStatusCode.NotFound, stock.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, price.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, audits.StatusCode);
        Assert.Empty(await Audits(product.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedAuditCreateConflict_RollsBackProductReplacement(bool price)
    {
        var product = await NewProduct();
        var existing = new InventoryAudit("audit-collision", product.StoreId, product.Id, "test-admin",
            DateTimeOffset.UtcNow, "Preexisting test audit", "stockAdjustment", 0, 1, "test-correlation");
        await store.Container.CreateItemAsync(existing, Partition);
        var service = new ControlledInventoryService(store, () => existing.Id);
        var exception = await Assert.ThrowsAsync<InventoryException>(() => price
            ? service.UpdatePriceAsync(product.Id, new PriceUpdate(125.25m, product.ETag, "test-admin", "Force rollback"),
                "test-batch-failure", CancellationToken.None)
            : service.AdjustStockAsync(product.Id, new StockAdjustment(1, product.ETag, "test-admin", "Force rollback"),
                "test-batch-failure", CancellationToken.None));
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(product, await Read(product.Id));
        Assert.Equal(existing, Assert.Single(await Audits(product.Id)));
    }

    [Fact]
    public async Task ConcurrentWriteBetweenReadAndBatch_IsRejectedByCosmosETag_NoAudit()
    {
        var product = await NewProduct();
        Product? otherWrite = null;
        var service = new ControlledInventoryService(store, () =>
        {
            // Deliberately interleave another writer AFTER the service point read/validation.
            var response = store.Container.ReplaceItemAsync(product with { Stock = 2 }, product.Id, Partition,
                new ItemRequestOptions { IfMatchEtag = product.ETag }).GetAwaiter().GetResult();
            otherWrite = response.Resource;
            return "audit-must-not-exist";
        });
        var exception = await Assert.ThrowsAsync<InventoryException>(() => service.AdjustStockAsync(product.Id,
            new StockAdjustment(-1, product.ETag, "test-admin", "Force stale batch"), "test-race", CancellationToken.None));
        Assert.Equal(412, exception.StatusCode);
        Assert.Equal(otherWrite, await Read(product.Id));
        Assert.Empty(await Audits(product.Id));
    }

    private sealed class ControlledInventoryService(CosmosStore store, Func<string> auditId) : InventoryService(store)
    {
        protected override string NewAuditId() => auditId();
    }
}
