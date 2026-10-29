using System.Net.Http.Json;
using IPLFranchiseStore.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace IPLFranchiseStore.Api.Tests;

public sealed class CosmosIntegrationTests
{
    [Fact]
    [Trait("Category", "CosmosEmulator")]
    public async Task Seed_PreservesInventoryAndProductsAcrossRestarts_WithStorePartitionKey()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().AddEnvironmentVariables().Build();
        var settings = configuration.GetSection("Cosmos").Get<CosmosSettings>() ?? new();
        Assert.True(!string.IsNullOrWhiteSpace(settings.Key),
            "Configure Cosmos:Key in API User Secrets or Cosmos__Key before running emulator tests.");
        Assert.Equal("localhost", new Uri(settings.Endpoint).Host);
        settings.DatabaseName = "IPLFranchiseStoreTests-" + Guid.NewGuid().ToString("N");
        using var cosmos = CosmosStore.CreateClient(settings);
        var store = new CosmosStore(cosmos, settings);
        try
        {
            using (var firstApp = CreateApp())
            using (var http = firstApp.CreateClient())
            {
                var products = await http.GetFromJsonAsync<List<Product>>("/api/v1/products");
                Assert.NotNull(products);
                Assert.Equal(4, products.Count);
                Assert.All(products, p => Assert.Equal(CosmosStore.StoreId, p.StoreId));
                Assert.Equal(new[] { 499m, 4999m, 1999m, 299m }, products.Select(p => p.Price));
            }
            var properties = await store.Container.ReadContainerAsync();
            Assert.Equal("/storeId", properties.Resource.PartitionKeyPath);
            var original = (await store.Container.ReadItemAsync<Product>("mi-jersey", new PartitionKey(CosmosStore.StoreId))).Resource;
            var changed = original with { Stock = 17, Name = "Persisted jersey", UpdatedAt = original.UpdatedAt.AddMinutes(1) };
            await store.Container.ReplaceItemAsync(changed, changed.Id, new PartitionKey(changed.StoreId));

            // A fresh host runs the real startup initializer a second time against the same database.
            using var restartedApp = CreateApp();
            using var restartedHttp = restartedApp.CreateClient();
            var persisted = await restartedHttp.GetFromJsonAsync<List<Product>>("/api/v1/products");
            Assert.NotNull(persisted);
            Assert.Equal(4, persisted.Count);
            Assert.Equal(changed, Assert.Single(persisted, p => p.Id == changed.Id));
            Assert.Equal(System.Net.HttpStatusCode.OK,
                (await restartedHttp.GetAsync("/api/v1/health/dependencies")).StatusCode);
        }
        finally
        {
            // Only this test's unique database is removed; the application's database is never touched.
            try { await cosmos.GetDatabase(settings.DatabaseName).DeleteAsync(); }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }

        WebApplicationFactory<Program> CreateApp() => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cosmos:Endpoint"] = settings.Endpoint,
                    ["Cosmos:Key"] = settings.Key,
                    ["Cosmos:DatabaseName"] = settings.DatabaseName,
                    ["Cosmos:ContainerName"] = settings.ContainerName
                })));
    }
}
