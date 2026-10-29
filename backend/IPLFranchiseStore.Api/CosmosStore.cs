using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api;

public sealed class CosmosSettings
{
    public string Endpoint { get; set; } = "https://localhost:8081";
    public string Key { get; set; } = "";
    public string DatabaseName { get; set; } = "IPLFranchiseStore";
    public string ContainerName { get; set; } = "StoreData";
}

public sealed class CosmosStore(CosmosClient client, CosmosSettings settings)
{
    public const string StoreId = "ipl-store";
    public const string PartitionKeyPath = "/storeId";
    public Container Container => client.GetContainer(settings.DatabaseName, settings.ContainerName);

    public static CosmosClient CreateClient(CosmosSettings settings) => new(
        settings.Endpoint, settings.Key, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
            RequestTimeout = TimeSpan.FromSeconds(10),
            MaxRetryAttemptsOnRateLimitedRequests = 2
        });

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var database = await client.CreateDatabaseIfNotExistsAsync(settings.DatabaseName,
            cancellationToken: cancellationToken);
        var properties = new ContainerProperties(settings.ContainerName, PartitionKeyPath);
        StoreIndexes.AddMissing(properties.IndexingPolicy);
        var container = await database.Database.CreateContainerIfNotExistsAsync(properties, cancellationToken: cancellationToken);
        if (container.Resource.PartitionKeyPath != PartitionKeyPath)
            throw new InvalidOperationException("StoreData must use partition key /storeId.");
        await StoreIndexes.EnsureAsync(Container, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        Product[] products =
        [
            NewProduct("mi-jersey", "MI Jersey", "MI", "Mumbai Indians jersey", 1999m, 50),
            NewProduct("csk-cap", "CSK Cap", "CSK", "Chennai Super Kings cap", 499m, 100),
            NewProduct("rcb-flag", "RCB Flag", "RCB", "Royal Challengers Bengaluru flag", 299m, 200),
            NewProduct("kkr-signed-photo", "KKR Signed Photo", "KKR", "Kolkata Knight Riders signed photo", 4999m, 2)
        ];
        foreach (var product in products)
        {
            try
            {
                await Container.CreateItemAsync(product, new PartitionKey(StoreId), cancellationToken: cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Create-only: existing inventory and all other product fields are preserved.
            }
        }

        Product NewProduct(string id, string name, string franchise, string description, decimal price, int stock) =>
            new() { Id = id, StoreId = StoreId, Name = name, Franchise = franchise,
                MerchandiseType = ProductTaxonomy.LegacyType(id),
                Description = description, Price = price, Stock = stock, CreatedAt = now, UpdatedAt = now };
    }

    public async Task<List<Product>> GetProductsAsync(CancellationToken cancellationToken)
    {
        using var iterator = Container.GetItemQueryIterator<Product>(
            new QueryDefinition("SELECT * FROM c WHERE c.type = @type ORDER BY c.id").WithParameter("@type", "product"),
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(StoreId) });
        var products = new List<Product>();
        while (iterator.HasMoreResults)
            products.AddRange(await iterator.ReadNextAsync(cancellationToken));
        return products.Select(ProductTaxonomy.Enrich).ToList();
    }
}
