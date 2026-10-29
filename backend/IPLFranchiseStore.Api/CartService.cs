using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api;

public class CartService(CosmosStore store)
{
    private static readonly PartitionKey Partition = new(CosmosStore.StoreId);
    public static string CartId(string userId) => "cart-" + Hash(userId);
    public static string RequestRecordId(string userId, Guid requestId) => "cart-request-" + Hash(userId) + "-" + requestId.ToString("N");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateUser(string userId)
    {
        if (!Regex.IsMatch(userId, "\\A[a-zA-Z0-9_-]{1,64}\\z"))
            throw new CartException(400, "Demo user ID must contain 1–64 letters, digits, underscores or hyphens.");
    }

    private async Task<T?> ReadOptional<T>(string id, CancellationToken ct)
    {
        try { return (await store.Container.ReadItemAsync<T>(id, Partition, cancellationToken: ct)).Resource; }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return default; }
    }

    private async Task<CustomerCart> ReadCart(string userId, CancellationToken ct) =>
        await ReadOptional<CustomerCart>(CartId(userId), ct) ??
        new CustomerCart(CartId(userId), CosmosStore.StoreId, userId, "empty", [], DateTimeOffset.UnixEpoch);

    private async Task<Product?> ReadProduct(string id, CancellationToken ct)
    {
        // Read the discriminator first so an audit/cart document ID is a missing product, not a deserialization error.
        var document = await ReadOptional<JsonElement?>(id, ct);
        if (document is null || !document.Value.TryGetProperty("type", out var type) || type.GetString() != "product") return null;
        return document.Value.Deserialize<Product>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private async Task<Dictionary<string, Product?>> ReadProducts(IEnumerable<string> ids, CancellationToken ct)
    {
        var products = new Dictionary<string, Product?>(StringComparer.Ordinal);
        foreach (var id in ids.Distinct(StringComparer.Ordinal)) products[id] = await ReadProduct(id, ct);
        return products;
    }

    public async Task<CartView> GetAsync(string userId, CancellationToken ct)
    {
        ValidateUser(userId);
        var cart = await ReadCart(userId, ct);
        return View(cart, await ReadProducts(cart.Lines.Select(l => l.ProductId), ct));
    }

    private static CartView View(CustomerCart cart, Dictionary<string, Product?> products)
    {
        var lines = cart.Lines.Select(line =>
        {
            products.TryGetValue(line.ProductId, out var product);
            var priceChanged = product is not null && product.Price != line.AcceptedPrice;
            var stockChanged = product is not null && product.Stock != line.AcceptedStock;
            var versionChanged = product is not null && product.ETag != line.AcceptedProductVersion;
            var insufficient = product is not null && product.Stock < line.Quantity;
            var requiresAck = product is null || versionChanged || priceChanged || stockChanged || insufficient;
            return new CartLineView(line.ProductId, product?.Name, line.Quantity, line.AcceptedPrice, line.AcceptedStock,
                line.AcceptedProductVersion, product?.Price, product?.Stock, product?.ETag, priceChanged, stockChanged,
                versionChanged, insufficient, product is null, requiresAck, product?.Price * line.Quantity);
        }).ToList();
        var requiresAcknowledgement = lines.Any(l => l.RequiresAcknowledgement);
        return new CartView(cart.UserId, cart.Version, lines, "INR",
            lines.Any(l => l.Unavailable) ? null : lines.Sum(l => l.CurrentSubtotal ?? 0m),
            requiresAcknowledgement, lines.Count > 0 && !requiresAcknowledgement);
    }

    public async Task<CartOutcome> ExecuteAsync(string userId, string operation, CartCommand command, CancellationToken ct)
    {
        ValidateUser(userId);
        if (!Guid.TryParseExact(command.RequestId, "D", out var requestId) || requestId == Guid.Empty)
            throw new CartException(400, "A non-empty client-generated UUID requestId in hyphenated format is required.");
        var recordId = RequestRecordId(userId, requestId);
        var fingerprint = Hash(JsonSerializer.Serialize(new { userId, operation, command.ExpectedCartVersion,
            command.ProductId, command.Quantity, Products = command.Products?.OrderBy(p => p?.ProductId, StringComparer.Ordinal).ToArray() }));
        var existing = await ReadOptional<CartRequestRecord>(recordId, ct);
        if (existing is not null) return Replay(existing, fingerprint);

        try
        {
            if (operation is not ("add" or "set" or "remove" or "acknowledge"))
                throw new CartException(400, "Unknown cart operation.");
            if (string.IsNullOrWhiteSpace(command.ExpectedCartVersion))
                throw new CartException(400, "expectedCartVersion from GET cart is required (empty for a new cart).");
            if (operation is "add" or "set" && command.Quantity is null or < 1 or > 10)
                throw new CartException(400, "Quantity must be an integer between 1 and 10.");
            if (operation != "acknowledge" && (string.IsNullOrWhiteSpace(command.ProductId) ||
                command.ProductId.Length > 128 || command.ProductId.IndexOfAny(['/', '\\', '?', '#']) >= 0))
                throw new CartException(400, "A valid productId is required.");
            var cart = await ReadCart(userId, ct);
            if (cart.Version != command.ExpectedCartVersion)
                throw new CartException(412, "Cart changed. Refresh and submit a new requestId after review.");

            // Acquire guard versions BEFORE product reads. A successful batch checks both guard and cart versions.
            var ids = cart.Lines.Select(l => l.ProductId).ToList();
            if (operation != "acknowledge") ids.Add(command.ProductId!);
            var guards = new List<ProductGuard>();
            if (operation is "add" or "set" or "acknowledge")
                foreach (var id in ids.Distinct(StringComparer.Ordinal))
                    guards.Add(await new ProductConcurrency(store).ReadGuardAsync(id, ct));
            var products = await ReadProducts(ids, ct);
            var lines = cart.Lines.ToList();
            var index = lines.FindIndex(l => l.ProductId == command.ProductId);
            if (operation == "remove")
            {
                if (index < 0) throw new CartException(404, "Cart line not found.");
                lines.RemoveAt(index);
            }
            else if (operation is "add" or "set")
            {
                var product = products[command.ProductId!] ?? throw new CartException(404, "Product not found.");
                if (operation == "set" && index < 0) throw new CartException(404, "Cart line not found.");
                var quantity = operation == "add" && index >= 0 ? lines[index].Quantity + command.Quantity!.Value : command.Quantity!.Value;
                if (quantity > 10) throw new CartException(409, "A cart line cannot exceed 10 units.");
                if (quantity > product.Stock) throw new CartException(409, "Insufficient current product stock.");
                if (index < 0)
                {
                    if (lines.Count >= 20) throw new CartException(409, "Demo carts support at most 20 distinct products.");
                    lines.Add(Snapshot(product, quantity));
                }
                else lines[index] = lines[index] with { Quantity = quantity }; // Quantity edits do not accept price/stock changes.
            }
            else
            {
                var observed = command.Products;
                if (observed is null || observed.Count != lines.Count || observed.Any(p => p is null ||
                    string.IsNullOrWhiteSpace(p.ProductId) || string.IsNullOrWhiteSpace(p.ExpectedETag)) ||
                    observed.Select(p => p.ProductId).Distinct(StringComparer.Ordinal).Count() != lines.Count)
                    throw new CartException(400, "Acknowledge must include each cart product and its current expectedETag exactly once.");
                foreach (var line in lines)
                {
                    var product = products[line.ProductId] ?? throw new CartException(404, "Cart product no longer exists.");
                    if (!observed.Any(p => p.ProductId == line.ProductId && p.ExpectedETag == product.ETag))
                        throw new CartException(412, "Product changed since review. Refresh before acknowledging.");
                    if (product.Stock < line.Quantity) throw new CartException(409, "Reduce or remove lines with insufficient stock before acknowledging.");
                }
                lines = lines.Select(l => Snapshot(products[l.ProductId]!, l.Quantity)).ToList();
            }
            var updated = cart with { Lines = lines, Version = Guid.NewGuid().ToString("D"), UpdatedAt = DateTimeOffset.UtcNow };
            var outcome = new CartOutcome(200, View(updated, products));
            var record = MakeRecord(outcome);
            var batch = store.Container.CreateTransactionalBatch(Partition);
            if (cart.ETag is null) batch.CreateItem(updated);
            else batch.ReplaceItem(cart.Id, updated, new TransactionalBatchItemRequestOptions { IfMatchEtag = cart.ETag });
            batch.CreateItem(record);
            foreach (var guard in guards) ProductConcurrency.AddCheck(batch, guard);
            await BeforeCommitAsync(ct);
            using var response = await batch.ExecuteAsync(ct);
            if (response.IsSuccessStatusCode) return outcome;
            // A concurrent duplicate may have won. Always replay before considering a conflict/failure record.
            existing = await ReadOptional<CartRequestRecord>(recordId, ct);
            if (existing is not null) return Replay(existing, fingerprint);
            if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
                throw new CartException(412, "Cart or product changed concurrently. Refresh and use a new requestId after review.");
            if (response.StatusCode == HttpStatusCode.NotFound) throw new CartException(404, "Cart or product is no longer available.");
            throw new CartException(503, "Cart store unavailable. Retry with the same requestId.");
        }
        catch (CartException ex) when (ex.StatusCode != 503)
        {
            // Semantic rejections are durable too. No cart writes occur in this rejection-only batch.
            var outcome = new CartOutcome(ex.StatusCode, Error: ex.Message);
            using var response = await store.Container.CreateTransactionalBatch(Partition).CreateItem(MakeRecord(outcome)).ExecuteAsync(ct);
            if (response.IsSuccessStatusCode) return outcome;
            existing = await ReadOptional<CartRequestRecord>(recordId, ct);
            if (existing is not null) return Replay(existing, fingerprint);
            throw new CartException(503, "Cart result unavailable. Retry with the same requestId.");
        }

        CartRequestRecord MakeRecord(CartOutcome outcome) => new(recordId, CosmosStore.StoreId, requestId.ToString("D"), userId,
            operation, fingerprint, outcome, outcome.StatusCode == 200 ? "succeeded" : "rejected", DateTimeOffset.UtcNow);
    }

    // Test seam for deliberate writes/failures between snapshot validation and transaction execution.
    protected virtual Task BeforeCommitAsync(CancellationToken ct) => Task.CompletedTask;
    private static CartLine Snapshot(Product product, int quantity) => new(product.Id, quantity, product.Price, product.Stock, product.ETag!);
    private static CartOutcome Replay(CartRequestRecord existing, string fingerprint) => existing.PayloadFingerprint == fingerprint
        ? existing.Result : new CartOutcome(409, Error: "requestId was already used with a different operation or payload.");
}
