using IPLFranchiseStore.Api;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api.Tests;

public sealed class QueryTestDatabase : IAsyncLifetime
{
    public CartTestDatabase Host { get; } = new();
    public List<Product> Products { get; private set; } = [];
    public List<OrderDocument> Orders { get; } = [];
    public static readonly DateTimeOffset BaseDate = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public async Task InitializeAsync()
    {
        await Host.InitializeAsync();
        var documents = new List<object>();
        for (var index = 0; index < 120; index++)
            documents.Add(new Product
            {
                Id = "catalogue-" + index.ToString("D3"), StoreId = CosmosStore.StoreId,
                Name = "Fixture " + (index % 2 == 0 ? "Jersey " : "Cap ") + (index / 6).ToString("D2"),
                MerchandiseType = index % 2 == 0 ? "Jersey" : "Cap",
                Franchise = new[] { "MI", "CSK", "RCB" }[index % 3], Description = "Persisted query fixture",
                Price = 100m + index / 100m, Stock = new[] { 0, 2, 25 }[index % 3], CreatedAt = BaseDate, UpdatedAt = BaseDate
            });
        for (var index = 0; index < 40; index++)
        {
            var timestamp = index < 37 ? BaseDate.AddHours(index / 3) : BaseDate.AddTicks(index switch { 37 => 1, 38 => 1000000, _ => 1000001 });
            Orders.Add(CreateOrder(index + 1, "history-alice", timestamp));
        }
        for (var index = 0; index < 23; index++) Orders.Add(CreateOrder(index + 1001, "history-bob", BaseDate.AddHours(index / 3)));
        documents.AddRange(Orders);
        foreach (var chunk in documents.Chunk(90))
        {
            var batch = Host.Store.Container.CreateTransactionalBatch(new PartitionKey(CosmosStore.StoreId));
            foreach (var value in chunk)
            {
                if (value is Product product) batch.CreateItem(product);
                else batch.CreateItem((OrderDocument)value);
            }
            using var response = await batch.ExecuteAsync();
            Assert.True(response.IsSuccessStatusCode, "Query fixture batch failed: " + response.StatusCode);
        }
        Products = await Host.Store.GetProductsAsync(CancellationToken.None);
        Assert.Equal(124, Products.Count);
    }

    private static OrderDocument CreateOrder(int index, string userId, DateTimeOffset timestamp)
    {
        var id = Guid.Parse("00000000-0000-4000-8000-" + index.ToString("D12"));
        var price = 123.45m + index;
        var quantity = 1 + index % 3;
        return new OrderDocument(CheckoutService.OrderDocumentId(id), CosmosStore.StoreId, id.ToString("D"), userId,
            timestamp, "INR", price * quantity,
            [new OrderLine("catalogue-000", "Historical purchased name " + index, quantity, price, price * quantity, "\"original-version\"")],
            "original-cart-version", "original-correlation");
    }
    public Task DisposeAsync() => Host.DisposeAsync();
}
