using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api;

public class CheckoutService(CosmosStore store)
{
    public const int MaximumOrderLines = 20;
    // Leave ample room below Cosmos's 2 MB wire payload limit for batch metadata/headers.
    public const int MaximumDocumentBytes = 1024 * 1024;
    private static readonly PartitionKey Partition = new(CosmosStore.StoreId);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static string OrderDocumentId(Guid id) => "order-" + id.ToString("N");
    public static string RequestDocumentId(Guid id) => "checkout-request-" + id.ToString("N");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateUser(string userId)
    {
        try { CartService.ValidateUser(userId); }
        catch (CartException ex) { throw new CheckoutException(400, ex.Message); }
    }

    private async Task<T?> ReadOptional<T>(string id, CancellationToken ct)
    {
        try { return (await store.Container.ReadItemAsync<T>(id, Partition, cancellationToken: ct)).Resource; }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return default; }
    }

    public async Task<OrderDocument> GetAsync(string userId, string orderId, CancellationToken ct)
    {
        ValidateUser(userId);
        if (!Guid.TryParseExact(orderId, "D", out var id) || id == Guid.Empty)
            throw new CheckoutException(400, "A non-empty UUID orderId is required.");
        var order = await ReadOptional<OrderDocument>(OrderDocumentId(id), ct);
        if (order is null || order.UserId != userId) throw new CheckoutException(404, "Order not found.");
        return order;
    }

    public async Task<CheckoutOutcome> CheckoutAsync(string userId, CheckoutCommand command, string correlationId, CancellationToken ct)
    {
        ValidateUser(userId);
        if (!Guid.TryParseExact(command.OrderId, "D", out var orderId) || orderId == Guid.Empty)
            throw new CheckoutException(400, "A client-generated non-empty UUID orderId in hyphenated format is required.");
        var canonicalId = orderId.ToString("D");
        var requestId = RequestDocumentId(orderId);
        var fingerprint = Hash(JsonSerializer.Serialize(new { userId, orderId = canonicalId, command.ExpectedCartVersion }));
        var existing = await ReadOptional<CheckoutRequestRecord>(requestId, ct);
        if (existing is not null) return Replay(existing, userId, fingerprint);

        try
        {
            if (string.IsNullOrWhiteSpace(command.ExpectedCartVersion))
                throw new CheckoutException(400, "expectedCartVersion from GET cart is required.");
            if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 200)
                throw new CheckoutException(400, "A correlation ID of at most 200 characters is required.");
            var cart = await ReadOptional<CustomerCart>(CartService.CartId(userId), ct);
            if (cart is null || cart.Lines.Count == 0) throw new CheckoutException(409, "An empty cart cannot be checked out.");
            if (cart.Version != command.ExpectedCartVersion)
                throw new CheckoutException(412, "Cart changed. Review it and submit a new orderId.");
            // Three writes per product (product, audit, guard), plus cart, order and request result.
            if (cart.Lines.Count > MaximumOrderLines || 3 * cart.Lines.Count + 3 > 100)
                throw new CheckoutException(413, "Orders support at most 20 distinct product lines.");
            if (cart.Lines.Select(l => l.ProductId).Distinct(StringComparer.Ordinal).Count() != cart.Lines.Count)
                throw new CheckoutException(400, "Cart contains duplicate product lines.");

            var failures = new List<CheckoutFailure>();
            var guards = new List<ProductGuard>();
            var products = new List<Product>();
            foreach (var line in cart.Lines)
            {
                if (line.Quantity is < 1 or > 10 || string.IsNullOrWhiteSpace(line.ProductId) || line.ProductId.Length > 128 ||
                    line.ProductId.IndexOfAny(['/', '\\', '?', '#']) >= 0)
                {
                    failures.Add(new(line.ProductId, "invalidLine", "Line requires a valid product ID and quantity between 1 and 10."));
                    continue;
                }
                // Read guard first: its session token also precedes the product read in this partition.
                var guard = await new ProductConcurrency(store).ReadGuardAsync(line.ProductId, ct);
                var document = await ReadOptional<JsonElement?>(line.ProductId, ct);
                if (document is null || !document.Value.TryGetProperty("type", out var type) || type.GetString() != "product")
                {
                    failures.Add(new(line.ProductId, "missingProduct", "Product no longer exists."));
                    continue;
                }
                var product = document.Value.Deserialize<Product>(JsonOptions)!;
                if (product.StoreId != CosmosStore.StoreId || product.Currency != "INR" || product.Price is <= 0 or > 999999999.99m ||
                    decimal.Round(product.Price, 2) != product.Price || string.IsNullOrEmpty(product.ETag) || product.ETag.Length > 256)
                    failures.Add(new(line.ProductId, "invalidProduct", "Product has invalid price, currency or version data."));
                else if (product.Stock < line.Quantity)
                    failures.Add(new(line.ProductId, "insufficientStock", "Insufficient stock for this quantity."));
                else if (line.AcceptedProductVersion != product.ETag || line.AcceptedPrice != product.Price || line.AcceptedStock != product.Stock)
                    failures.Add(new(line.ProductId, "acknowledgementRequired", "Product price, stock or version changed; review and acknowledge the cart."));
                guards.Add(guard);
                products.Add(product);
            }
            if (failures.Count > 0)
            {
                var status = failures.Any(f => f.Code is "invalidLine" or "invalidProduct") ? 400
                    : failures.Any(f => f.Code == "missingProduct") ? 404
                    : failures.Any(f => f.Code == "insufficientStock") ? 409 : 412;
                throw new CheckoutException(status, "Checkout rejected; no products were purchased.", failures);
            }

            var now = DateTimeOffset.UtcNow;
            var lines = cart.Lines.Zip(products, (line, product) => new OrderLine(product.Id, product.Name, line.Quantity,
                product.Price, product.Price * line.Quantity, product.ETag!)).ToList();
            var order = new OrderDocument(OrderDocumentId(orderId), CosmosStore.StoreId, canonicalId, userId, now,
                "INR", lines.Sum(l => l.LineTotal), lines, cart.Version, correlationId);
            var outcome = new CheckoutOutcome(201, order);
            var request = MakeRecord(outcome);
            var completedCart = cart with
            {
                Lines = [], Version = Guid.NewGuid().ToString("D"), UpdatedAt = now,
                LastCompletedOrderId = canonicalId, LastCompletedAt = now
            };
            var updatedProducts = products.Select((p, index) => p with
                { Stock = p.Stock - cart.Lines[index].Quantity, UpdatedAt = now }).ToList();
            var audits = products.Select((p, index) => new InventoryAudit(
                "checkout-audit-" + orderId.ToString("N") + "-" + Hash(p.Id), CosmosStore.StoreId, p.Id,
                userId, now, "Checkout " + canonicalId, "checkout", p.Stock, updatedProducts[index].Stock, correlationId)).ToList();

            // Measure every serialized document; reserve another 1 MB for at most 63 operation envelopes.
            long bytes = 0;
            foreach (var value in new object[] { completedCart, order, request }.Concat(updatedProducts).Concat(audits).Concat(guards))
                bytes += JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), JsonOptions).Length;
            if (bytes > MaximumDocumentBytes)
                throw new CheckoutException(413, "Order transaction exceeds the supported 1 MiB document budget.");

            var batch = store.Container.CreateTransactionalBatch(Partition)
                .ReplaceItem(cart.Id, completedCart, new TransactionalBatchItemRequestOptions { IfMatchEtag = cart.ETag })
                .CreateItem(order)
                .CreateItem(request);
            for (var index = 0; index < products.Count; index++)
            {
                batch.ReplaceItem(products[index].Id, updatedProducts[index],
                    new TransactionalBatchItemRequestOptions { IfMatchEtag = products[index].ETag });
                batch.CreateItem(audits[index]);
                ProductConcurrency.AddCheck(batch, guards[index]);
            }
            await BeforeCommitAsync(ct);
            using var response = await ExecuteBatchAsync(batch, ct);
            if (response.IsSuccessStatusCode)
            {
                await AfterCommitAsync(order, ct);
                return outcome;
            }
            existing = await ReadOptional<CheckoutRequestRecord>(requestId, ct);
            if (existing is not null) return Replay(existing, userId, fingerprint);
            var conflictingLines = new List<CheckoutFailure>();
            for (var index = 3; index < Math.Min(response.Count, 3 + products.Count * 3); index++)
                if ((int)response[index].StatusCode >= 400 && (int)response[index].StatusCode != 424)
                    conflictingLines.Add(new(products[(index - 3) / 3].Id, "concurrentChange", "Product or guard changed during checkout."));
            var statusCode = response.StatusCode switch
            {
                HttpStatusCode.PreconditionFailed => 412,
                HttpStatusCode.Conflict => 409,
                HttpStatusCode.NotFound => 404,
                HttpStatusCode.RequestEntityTooLarge => 413,
                _ => 503
            };
            throw new CheckoutException(statusCode, statusCode == 503
                ? "Checkout result unavailable. Retry the identical request with the same orderId."
                : "Checkout transaction was not committed. Review the cart before a new attempt.", conflictingLines);
        }
        catch (CheckoutException ex) when (ex.StatusCode != 503)
        {
            // Rejected requests are durable, but never change inventory/cart or create orders/audits.
            var outcome = new CheckoutOutcome(ex.StatusCode, Error: ex.Message, Failures: ex.Failures);
            using var response = await store.Container.CreateTransactionalBatch(Partition).CreateItem(MakeRecord(outcome)).ExecuteAsync(ct);
            if (response.IsSuccessStatusCode) return outcome;
            existing = await ReadOptional<CheckoutRequestRecord>(requestId, ct);
            if (existing is not null) return Replay(existing, userId, fingerprint);
            throw new CheckoutException(503, "Checkout result unavailable. Retry the identical request with the same orderId.");
        }

        CheckoutRequestRecord MakeRecord(CheckoutOutcome result) => new(requestId, CosmosStore.StoreId, canonicalId,
            userId, fingerprint, result.StatusCode == 201 ? "succeeded" : "rejected", result, DateTimeOffset.UtcNow);
    }

    protected virtual Task BeforeCommitAsync(CancellationToken ct) => Task.CompletedTask;
    protected virtual Task<TransactionalBatchResponse> ExecuteBatchAsync(TransactionalBatch batch, CancellationToken ct) => batch.ExecuteAsync(ct);
    protected virtual Task AfterCommitAsync(OrderDocument order, CancellationToken ct) => Task.CompletedTask;
    private static CheckoutOutcome Replay(CheckoutRequestRecord record, string userId, string fingerprint) =>
        record.UserId == userId && record.PayloadFingerprint == fingerprint ? record.Result
            : new CheckoutOutcome(409, Error: "orderId is already bound to a different user or request payload.");
}
