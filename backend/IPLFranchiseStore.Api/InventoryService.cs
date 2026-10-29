using System.Net;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api;

public sealed record StockAdjustment(int? Quantity, string? ExpectedETag, string? Actor, string? Reason);
public sealed record PriceUpdate(decimal? Price, string? ExpectedETag, string? Actor, string? Reason);

public sealed record InventoryAudit(string Id, string StoreId, string ProductId, string Actor,
    DateTimeOffset Timestamp, string Reason, string OperationType, decimal OldValue, decimal NewValue,
    string CorrelationId)
{
    public string Type { get; init; } = "inventoryAudit";
}

public sealed class InventoryException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public class InventoryService(CosmosStore store)
{
    public Task<Product> AdjustStockAsync(string id, StockAdjustment request, string correlationId, CancellationToken ct)
    {
        ValidateAudit(request.ExpectedETag, request.Actor, request.Reason, correlationId);
        if (request.Quantity is null or 0)
            throw new InventoryException(400, "Quantity must be a non-zero integer adjustment.");
        return MutateAsync(id, request.ExpectedETag!, request.Actor!, request.Reason!, correlationId,
            "stockAdjustment", product =>
            {
                var stock = (long)product.Stock + request.Quantity.Value;
                if (stock < 0 || stock > int.MaxValue)
                    throw new InventoryException(409, "Adjustment would exceed the supported inventory range.");
                return (product with { Stock = (int)stock }, product.Stock, (decimal)stock);
            }, ct);
    }

    public Task<Product> UpdatePriceAsync(string id, PriceUpdate request, string correlationId, CancellationToken ct)
    {
        ValidateAudit(request.ExpectedETag, request.Actor, request.Reason, correlationId);
        if (request.Price is null or <= 0 or > 999999999.99m || decimal.Round(request.Price.Value, 2) != request.Price)
            throw new InventoryException(400, "Price must be positive INR, at most 999999999.99, with no fractional paise.");
        return MutateAsync(id, request.ExpectedETag!, request.Actor!, request.Reason!, correlationId,
            "priceUpdate", product =>
            {
                if (product.Price == request.Price.Value)
                    throw new InventoryException(409, "Price is unchanged.");
                return (product with { Price = request.Price.Value }, product.Price, request.Price.Value);
            }, ct);
    }

    private static void ValidateAudit(string? etag, string? actor, string? reason, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(etag) || etag.Length > 256 || etag.Length < 3 ||
            !etag.StartsWith('"') || !etag.EndsWith('"') || etag.Contains('*') || etag.Contains('\r') || etag.Contains('\n'))
            throw new InventoryException(400, "A quoted, explicit expectedETag from the product is required.");
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200)
            throw new InventoryException(400, "Actor is required (maximum 200 characters).");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new InventoryException(400, "Audit reason is required (maximum 500 characters).");
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 200)
            throw new InventoryException(400, "Correlation ID is required (maximum 200 characters).");
    }

    // Overridable only to force an audit ID collision in atomic rollback integration tests.
    protected virtual string NewAuditId() => "audit-" + Guid.NewGuid().ToString("N");

    private async Task<Product> ReadProductAsync(string id, CancellationToken ct)
    {
        var response = await store.Container.ReadItemAsync<Product>(id, new PartitionKey(CosmosStore.StoreId), cancellationToken: ct);
        if (response.Resource.Type != "product")
            throw new InventoryException(404, "Product not found.");
        return response.Resource with { ETag = response.ETag };
    }

    private async Task<Product> MutateAsync(string id, string etag, string actor, string reason, string correlationId,
        string operation, Func<Product, (Product Product, decimal OldValue, decimal NewValue)> change, CancellationToken ct)
    {
        var original = await ReadProductAsync(id, ct);
        if (original.ETag != etag)
            throw new InventoryException(412, "Product changed. Refresh and explicitly resubmit your adjustment.");
        var (updated, oldValue, newValue) = change(original);
        var now = DateTimeOffset.UtcNow;
        updated = updated with { UpdatedAt = now };
        var audit = new InventoryAudit(NewAuditId(), original.StoreId, original.Id, actor.Trim(), now,
            reason.Trim(), operation, oldValue, newValue, correlationId);

        using var response = await store.Container.CreateTransactionalBatch(new PartitionKey(original.StoreId))
            .ReplaceItem(original.Id, updated, new TransactionalBatchItemRequestOptions { IfMatchEtag = etag })
            .CreateItem(audit)
            .ExecuteAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            throw new InventoryException(status switch
            {
                HttpStatusCode.PreconditionFailed => 412,
                HttpStatusCode.NotFound => 404,
                HttpStatusCode.Conflict => 409,
                _ => 503
            }, status == HttpStatusCode.PreconditionFailed
                ? "Product changed. Refresh and explicitly resubmit your adjustment."
                : "Inventory batch was not committed.");
        }
        // Return the batch's committed version, never a later read that could reflect another writer.
        return updated with { ETag = response.GetOperationResultAtIndex<Product>(0).ETag };
    }

    public async Task<List<InventoryAudit>> GetAuditsAsync(string productId, CancellationToken ct)
    {
        await ReadProductAsync(productId, ct);
        using var query = store.Container.GetItemQueryIterator<InventoryAudit>(new QueryDefinition(
            "SELECT * FROM c WHERE c.type = @type AND c.productId = @id ORDER BY c.timestamp")
            .WithParameter("@type", "inventoryAudit").WithParameter("@id", productId),
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(CosmosStore.StoreId) });
        var audits = new List<InventoryAudit>();
        while (query.HasMoreResults) audits.AddRange(await query.ReadNextAsync(ct));
        return audits;
    }
}
