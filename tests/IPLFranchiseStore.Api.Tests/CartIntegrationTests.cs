using System.Net;
using System.Net.Http.Json;
using IPLFranchiseStore.Api;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api.Tests;

public sealed class CartIntegrationTests(CartTestDatabase db) : IClassFixture<CartTestDatabase>
{
    private readonly string user = "demo-" + Guid.NewGuid().ToString("N");
    private static readonly PartitionKey Partition = new(CosmosStore.StoreId);
    private static string RequestId() => Guid.NewGuid().ToString("D");
    private CartService Service => new(db.Store);
    private string Root(string? who = null) => $"/api/v1/demo-users/{who ?? user}/cart";
    private Task<CartView?> Get(string? who = null) => db.Http.GetFromJsonAsync<CartView>(Root(who));
    private Task<HttpResponseMessage> Add(Product product, int quantity = 1, string version = "empty", string? requestId = null, string? who = null) =>
        db.Http.PostAsJsonAsync(Root(who) + "/lines", new CartCommand(requestId ?? RequestId(), version, product.Id, quantity));
    private Task<HttpResponseMessage> Set(Product product, int quantity, string version, string? requestId = null) =>
        db.Http.PutAsJsonAsync(Root() + "/lines/" + product.Id, new CartCommand(requestId ?? RequestId(), version, Quantity: quantity));
    private async Task<HttpResponseMessage> Remove(Product product, string version, string requestId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Root() + "/lines/" + product.Id)
        { Content = JsonContent.Create(new CartCommand(requestId, version)) };
        return await db.Http.SendAsync(request);
    }
    private Task<HttpResponseMessage> Acknowledge(CartView cart, string? requestId = null) =>
        db.Http.PostAsJsonAsync(Root() + "/acknowledgements", AckCommand(cart, requestId));
    private static CartCommand AckCommand(CartView cart, string? requestId = null) =>
        new(requestId ?? RequestId(), cart.Version, Products: cart.Lines.Select(l => new ObservedProduct(l.ProductId, l.CurrentProductVersion!)).ToList());
    private async Task<Product> NewProduct(int stock = 50)
    {
        var product = new Product
        {
            Id = "test-" + Guid.NewGuid().ToString("N"), StoreId = CosmosStore.StoreId,
            Name = "Cart test product", Franchise = "MI", Description = "Test only", Price = 100.25m,
            Stock = stock, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        return (await db.Store.Container.CreateItemAsync(product, Partition)).Resource;
    }
    private async Task<Product> ReadProduct(string id) => (await db.Store.Container.ReadItemAsync<Product>(id, Partition)).Resource;
    private async Task<Product> ChangeProduct(Product product, bool price) => price
        ? await new InventoryService(db.Store).UpdatePriceAsync(product.Id,
            new PriceUpdate(125.50m, product.ETag, "test-admin", "Cart price test"), "cart-test", CancellationToken.None)
        : await new InventoryService(db.Store).AdjustStockAsync(product.Id,
            new StockAdjustment(-1, product.ETag, "test-admin", "Cart stock test"), "cart-test", CancellationToken.None);
    private async Task<CartRequestRecord> Record(string requestId) =>
        (await db.Store.Container.ReadItemAsync<CartRequestRecord>(CartService.RequestRecordId(user, Guid.Parse(requestId)), Partition)).Resource;
    private static async Task<CartView> Success(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CartView>())!;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task AddValidQuantity_PersistsSnapshotsAndLeavesProductUntouched(int quantity)
    {
        var product = await NewProduct();
        using var response = await Add(product, quantity);
        var cart = await Success(response);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(quantity, line.Quantity);
        Assert.Equal(product.Price, line.AcceptedPrice);
        Assert.Equal(product.Stock, line.AcceptedStock);
        Assert.Equal(product.ETag, line.AcceptedProductVersion);
        Assert.Equal(product.Price * quantity, cart.CurrentTotal);
        Assert.False(cart.RequiresAcknowledgement);
        Assert.True(cart.CanCheckout);
        Assert.Equal(product, await ReadProduct(product.Id));
        Assert.Equal(cart.Version, (await Get())!.Version);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(11, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(11, true)]
    public async Task InvalidQuantity_Returns400ProblemDetails_AndDurableRejection(int quantity, bool set)
    {
        var product = await NewProduct();
        var version = "empty";
        if (set) { using var added = await Add(product); version = (await Success(added)).Version; }
        var requestId = RequestId();
        using var response = await (set ? Set(product, quantity, version, requestId) : Add(product, quantity, version, requestId));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("rejected", (await Record(requestId)).Status);
        Assert.Equal(version, (await Get())!.Version);
    }

    [Fact]
    public async Task InsufficientStock_AndAddingBeyondLineLimit_Return409()
    {
        var scarce = await NewProduct(1);
        using var failed = await Add(scarce, 2);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Empty((await Get())!.Lines);
        var product = await NewProduct();
        using var success = await Add(product, 10);
        var cart = await Success(success);
        using var overflow = await Add(product, 1, cart.Version);
        Assert.Equal(HttpStatusCode.Conflict, overflow.StatusCode);
        Assert.Equal(10, Assert.Single((await Get())!.Lines).Quantity);
    }

    [Fact]
    public async Task FractionalQuantity_Returns400ProblemDetails_WithoutCartMutation()
    {
        var product = await NewProduct();
        using var response = await db.Http.PostAsJsonAsync(Root() + "/lines", new
        {
            requestId = RequestId(), expectedCartVersion = "empty", productId = product.Id, quantity = 1.5m
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Empty((await Get())!.Lines);
    }

    [Fact]
    public async Task SetRejectsInsufficientStock_AndMissingRequestIdIsBadRequest()
    {
        var product = await NewProduct(1);
        using var add = await Add(product);
        var cart = await Success(add);
        using var set = await Set(product, 2, cart.Version);
        Assert.Equal(HttpStatusCode.Conflict, set.StatusCode);
        using var missingId = await db.Http.PostAsJsonAsync(Root() + "/lines",
            new CartCommand(null, cart.Version, product.Id, 1));
        Assert.Equal(HttpStatusCode.BadRequest, missingId.StatusCode);
        Assert.Equal(cart.Version, (await Get())!.Version);
    }

    [Fact]
    public async Task AddSetRemove_AndIndependentUsers()
    {
        var product = await NewProduct();
        using var first = await Add(product);
        var one = await Success(first);
        using var additional = await Add(product, 2, one.Version);
        var three = await Success(additional);
        Assert.Equal(3, Assert.Single(three.Lines).Quantity);
        using var set = await Set(product, 5, three.Version);
        var five = await Success(set);
        Assert.Equal(5, Assert.Single(five.Lines).Quantity);
        var otherUser = user + "-other";
        using var other = await Add(product, 2, who: otherUser);
        Assert.Equal(2, Assert.Single((await Success(other)).Lines).Quantity);
        using var remove = await Remove(product, five.Version, RequestId());
        Assert.Empty((await Success(remove)).Lines);
        Assert.Equal(2, Assert.Single((await Get(otherUser))!.Lines).Quantity);
        Assert.Equal(product, await ReadProduct(product.Id));
    }

    [Fact]
    public async Task DuplicateAdd_ReplaysOriginalEvenAfterLaterCartChanges()
    {
        var product = await NewProduct();
        var requestId = RequestId();
        using var first = await Add(product, requestId: requestId);
        var originalBody = await first.Content.ReadAsStringAsync();
        var original = await Success(first);
        using var second = await Set(product, 4, original.Version);
        var changed = await Success(second);
        using var replay = await Add(product, requestId: requestId);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal(changed.Version, (await Get())!.Version);
        Assert.Equal(4, Assert.Single((await Get())!.Lines).Quantity);
        var record = await Record(requestId);
        Assert.Equal(user, record.UserId);
        Assert.Equal("add", record.Operation);
        Assert.Equal("succeeded", record.Status);
        Assert.Equal(64, record.PayloadFingerprint.Length);
        Assert.Equal(original.Version, record.Result.Cart!.Version);
    }

    [Fact]
    public async Task DuplicateRemoval_ReplaysOriginalAfterLineIsReadded()
    {
        var product = await NewProduct();
        using var add = await Add(product);
        var added = await Success(add);
        var requestId = RequestId();
        using var remove = await Remove(product, added.Version, requestId);
        var originalBody = await remove.Content.ReadAsStringAsync();
        var empty = await Success(remove);
        using var readd = await Add(product, 2, empty.Version);
        await Success(readd);
        using var replay = await Remove(product, added.Version, requestId);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal(2, Assert.Single((await Get())!.Lines).Quantity);
    }

    [Fact]
    public async Task ReusingRequestIdWithDifferentPayloadOrOperation_Returns409()
    {
        var product = await NewProduct();
        var requestId = RequestId();
        using var add = await Add(product, requestId: requestId);
        var cart = await Success(add);
        using var changedQuantity = await Add(product, 2, requestId: requestId);
        using var changedOperation = await Remove(product, cart.Version, requestId);
        Assert.Equal(HttpStatusCode.Conflict, changedQuantity.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedOperation.StatusCode);
        Assert.Equal(1, Assert.Single((await Get())!.Lines).Quantity);
    }

    [Fact]
    public async Task ConcurrentDuplicates_ApplyOnceAndReturnIdenticalResults()
    {
        var product = await NewProduct();
        var requestId = RequestId();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Add(product, requestId: requestId)));
        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
            Assert.Single(bodies.Distinct());
            Assert.Equal(1, Assert.Single((await Get())!.Lines).Quantity);
            Assert.Equal("succeeded", (await Record(requestId)).Status);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task RestartedApi_ReplaysPersistedResultAfterLaterChanges()
    {
        var product = await NewProduct();
        var requestId = RequestId();
        using var first = await Add(product, requestId: requestId);
        var originalBody = await first.Content.ReadAsStringAsync();
        var original = await Success(first);
        using var change = await Set(product, 5, original.Version);
        var changed = await Success(change);
        await db.RestartAsync();
        using var replay = await Add(product, requestId: requestId);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal(changed.Version, (await Get())!.Version);
        Assert.Equal(5, Assert.Single((await Get())!.Lines).Quantity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductChanges_FlagWarningAndRequireExplicitAcknowledgement(bool price)
    {
        var product = await NewProduct();
        using var added = await Add(product, 2);
        await Success(added);
        var changed = await ChangeProduct(product, price);
        var cart = (await Get())!;
        var line = Assert.Single(cart.Lines);
        Assert.Equal(price, line.PriceChanged);
        Assert.Equal(!price, line.StockChanged);
        Assert.True(line.VersionChanged);
        Assert.True(cart.RequiresAcknowledgement);
        Assert.False(cart.CanCheckout);
        Assert.Equal(changed.Price * 2, cart.CurrentTotal);
        Assert.Equal(product.Price, line.AcceptedPrice);
        Assert.Equal(product.Stock, line.AcceptedStock);
        using var quantityChange = await Set(product, 3, cart.Version);
        Assert.True((await Success(quantityChange)).RequiresAcknowledgement);
        cart = (await Get())!;
        using var ack = await Acknowledge(cart);
        var accepted = await Success(ack);
        Assert.False(accepted.RequiresAcknowledgement);
        Assert.True(accepted.CanCheckout);
        Assert.Equal(changed.ETag, Assert.Single(accepted.Lines).AcceptedProductVersion);
        Assert.Equal(changed, await ReadProduct(product.Id));
    }

    [Fact]
    public async Task Acknowledgement_RejectsStaleObservedProductVersion()
    {
        var product = await NewProduct();
        using var added = await Add(product);
        var cart = await Success(added);
        await ChangeProduct(product, true);
        using var ack = await Acknowledge(cart);
        Assert.Equal(HttpStatusCode.PreconditionFailed, ack.StatusCode);
        Assert.Equal(cart.Version, (await Get())!.Version);
        Assert.True((await Get())!.RequiresAcknowledgement);
    }

    [Fact]
    public async Task ProductChangesAfterAckValidation_RollBackCartAndSuccessResult()
    {
        var product = await NewProduct();
        using var added = await Add(product);
        var cart = await Success(added);
        var command = AckCommand(cart);
        var racing = new ControlledCartService(db.Store, async () => { await ChangeProduct(product, true); });
        var outcome = await racing.ExecuteAsync(user, "acknowledge", command, CancellationToken.None);
        Assert.Equal(412, outcome.StatusCode);
        var persisted = (await Get())!;
        Assert.Equal(cart.Version, persisted.Version);
        Assert.Equal(product.ETag, Assert.Single(persisted.Lines).AcceptedProductVersion);
        Assert.True(persisted.RequiresAcknowledgement);
        var record = await Record(command.RequestId!);
        Assert.Equal("rejected", record.Status);
        Assert.Null(record.Result.Cart);
        Assert.Equal(412, (await Service.ExecuteAsync(user, "acknowledge", command, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task AddSnapshotRace_RollsBackNewCartAndSuccessRecord()
    {
        var product = await NewProduct();
        var command = new CartCommand(RequestId(), "empty", product.Id, 1);
        var racing = new ControlledCartService(db.Store, async () => { await ChangeProduct(product, false); });
        var outcome = await racing.ExecuteAsync(user, "add", command, CancellationToken.None);
        Assert.Equal(412, outcome.StatusCode);
        Assert.Empty((await Get())!.Lines);
        using var storedCart = await db.Store.Container.ReadItemStreamAsync(CartService.CartId(user), Partition);
        Assert.Equal(HttpStatusCode.NotFound, storedCart.StatusCode);
        Assert.Equal("rejected", (await Record(command.RequestId!)).Status);
    }

    [Fact]
    public async Task ForcedIdempotencyRecordCollision_RollsBackCartCreation()
    {
        var product = await NewProduct();
        var command = new CartCommand(RequestId(), "empty", product.Id, 1);
        var racing = new ControlledCartService(db.Store, async () =>
        {
            await db.Store.Container.CreateItemAsync(new CartRequestRecord(
                CartService.RequestRecordId(user, Guid.Parse(command.RequestId!)), CosmosStore.StoreId,
                command.RequestId!, user, "add", "forced-different-fingerprint", new CartOutcome(409, Error: "forced conflict"),
                "rejected", DateTimeOffset.UtcNow), Partition);
        });
        var outcome = await racing.ExecuteAsync(user, "add", command, CancellationToken.None);
        Assert.Equal(409, outcome.StatusCode);
        using var cart = await db.Store.Container.ReadItemStreamAsync(CartService.CartId(user), Partition);
        Assert.Equal(HttpStatusCode.NotFound, cart.StatusCode);
        Assert.Equal("forced-different-fingerprint", (await Record(command.RequestId!)).PayloadFingerprint);
    }

    [Fact]
    public async Task DifferentConcurrentRequests_DoNotOverwriteCart_AndRejectionsAreDurable()
    {
        var product = await NewProduct();
        using var first = await Add(product);
        var initial = await Success(first);
        var command = new CartCommand(RequestId(), initial.Version, product.Id, 3);
        var racing = new ControlledCartService(db.Store, async () =>
        {
            var winner = await Service.ExecuteAsync(user, "set", command with { RequestId = RequestId(), Quantity = 2 }, CancellationToken.None);
            Assert.Equal(200, winner.StatusCode);
        });
        var failed = await racing.ExecuteAsync(user, "set", command, CancellationToken.None);
        Assert.Equal(412, failed.StatusCode);
        Assert.Equal(2, Assert.Single((await Get())!.Lines).Quantity);
        Assert.Equal("rejected", (await Record(command.RequestId!)).Status);
        Assert.Equal(failed, await Service.ExecuteAsync(user, "set", command, CancellationToken.None));
    }

    [Fact]
    public async Task MissingLineOrProduct_Return404_AndMalformedUuidReturns400()
    {
        var missing = new CartCommand(RequestId(), "empty", "missing-product", 1);
        Assert.Equal(404, (await Service.ExecuteAsync(user, "add", missing, CancellationToken.None)).StatusCode);
        Assert.Equal(404, (await Service.ExecuteAsync(user, "remove", missing with { RequestId = RequestId() }, CancellationToken.None)).StatusCode);
        var ex = await Assert.ThrowsAsync<CartException>(() => Service.ExecuteAsync(user, "add", missing with { RequestId = "not-a-uuid" }, CancellationToken.None));
        Assert.Equal(400, ex.StatusCode);
        Assert.Empty((await Get())!.Lines);
    }

    [Fact]
    public async Task InsufficientStockCannotBeAcknowledged_AndFailedResultReplaysAfterRestock()
    {
        var product = await NewProduct(2);
        using var add = await Add(product, 2);
        await Success(add);
        var changed = await ChangeProduct(product, false);
        var cart = (await Get())!;
        Assert.True(Assert.Single(cart.Lines).InsufficientStock);
        var command = AckCommand(cart);
        var failed = await Service.ExecuteAsync(user, "acknowledge", command, CancellationToken.None);
        Assert.Equal(409, failed.StatusCode);
        await new InventoryService(db.Store).AdjustStockAsync(changed.Id,
            new StockAdjustment(10, changed.ETag, "test-admin", "Restock test"), "test-restock", CancellationToken.None);
        Assert.Equal(failed, await Service.ExecuteAsync(user, "acknowledge", command, CancellationToken.None));
        Assert.True((await Get())!.RequiresAcknowledgement);
    }

    private sealed class ControlledCartService(CosmosStore store, Func<Task> beforeCommit) : CartService(store)
    {
        protected override Task BeforeCommitAsync(CancellationToken ct) => beforeCommit();
    }
}
