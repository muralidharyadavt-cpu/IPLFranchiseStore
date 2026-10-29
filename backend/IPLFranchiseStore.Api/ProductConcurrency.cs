using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api;

// A shared write fence is needed because Cosmos conditional batch reads do not check If-Match.
// Every application inventory writer and snapshot-accepting cart command participates.
public sealed record ProductGuard(string Id, string StoreId)
{
    public string Type { get; init; } = "productGuard";
    [JsonPropertyName("_etag")]
    public string? ETag { get; init; }
}

public sealed class ProductConcurrency(CosmosStore store)
{
    public async Task<ProductGuard> ReadGuardAsync(string productId, CancellationToken ct)
    {
        var id = "product-guard-" + productId;
        var partition = new PartitionKey(CosmosStore.StoreId);
        try { return (await store.Container.ReadItemAsync<ProductGuard>(id, partition, cancellationToken: ct)).Resource; }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            try { await store.Container.CreateItemAsync(new ProductGuard(id, CosmosStore.StoreId), partition, cancellationToken: ct); }
            catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict) { }
            return (await store.Container.ReadItemAsync<ProductGuard>(id, partition, cancellationToken: ct)).Resource;
        }
    }

    public static void AddCheck(TransactionalBatch batch, ProductGuard guard) => batch.ReplaceItem(guard.Id, guard,
        new TransactionalBatchItemRequestOptions { IfMatchEtag = guard.ETag });
}
