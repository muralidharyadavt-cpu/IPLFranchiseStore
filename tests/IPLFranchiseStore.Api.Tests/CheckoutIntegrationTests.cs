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

public sealed class CheckoutIntegrationTests(CartTestDatabase db) : IClassFixture<CartTestDatabase>
{
    private static readonly PartitionKey Partition = new(CosmosStore.StoreId);
    private readonly string user = "checkout-" + Guid.NewGuid().ToString("N");
    private CheckoutService Service => new(db.Store);
    private static string Uuid() => Guid.NewGuid().ToString("D");
    private static CheckoutCommand Command(CartView cart) => new(Uuid(), cart.Version);
    private async Task<Product> Product(int stock = 50, decimal price = 100.25m, string? description = null)
    {
        var product = new Product
        {
            Id = "test-" + Guid.NewGuid().ToString("N"), StoreId = CosmosStore.StoreId, Name = "Checkout test product",
            Franchise = "MI", Description = description ?? "Isolated checkout test", Stock = stock, Price = price,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        return (await db.Store.Container.CreateItemAsync(product, Partition)).Resource;
    }
    private async Task<Product> ReadProduct(string id) => (await db.Store.Container.ReadItemAsync<Product>(id, Partition)).Resource;
    private async Task<CartView> Cart(string who, params (Product Product, int Quantity)[] lines)
    {
        var service = new CartService(db.Store);
        var cart = await service.GetAsync(who, CancellationToken.None);
        foreach (var (product, quantity) in lines)
        {
            var result = await service.ExecuteAsync(who, "add", new CartCommand(Uuid(), cart.Version, product.Id, quantity), CancellationToken.None);
            Assert.Equal(200, result.StatusCode);
            cart = result.Cart!;
        }
        return cart;
    }
    private async Task<string> StoredCart(string who) => JsonSerializer.Serialize(
        (await db.Store.Container.ReadItemAsync<CustomerCart>(CartService.CartId(who), Partition)).Resource);
    private async Task<List<T>> Query<T>(string type, string field, string value)
    {
        using var iterator = db.Store.Container.GetItemQueryIterator<T>(new QueryDefinition(
            $"SELECT * FROM c WHERE c.type = @type AND c.{field} = @value")
            .WithParameter("@type", type).WithParameter("@value", value),
            requestOptions: new QueryRequestOptions { PartitionKey = Partition });
        var values = new List<T>();
        while (iterator.HasMoreResults) values.AddRange(await iterator.ReadNextAsync());
        return values;
    }
    private Task<List<OrderDocument>> Orders(string who) => Query<OrderDocument>("order", "userId", who);
    private async Task<List<InventoryAudit>> CheckoutAudits(string productId) =>
        (await Query<InventoryAudit>("inventoryAudit", "productId", productId)).Where(a => a.OperationType == "checkout").ToList();
    private async Task<CheckoutRequestRecord> Request(CheckoutCommand command) =>
        (await db.Store.Container.ReadItemAsync<CheckoutRequestRecord>(CheckoutService.RequestDocumentId(Guid.Parse(command.OrderId!)), Partition)).Resource;
    private Task<CheckoutOutcome> Checkout(string who, CheckoutCommand command) => Service.CheckoutAsync(who, command, "test-correlation", CancellationToken.None);
    private async Task<HttpResponseMessage> Post(CheckoutCommand command, string? who = null, HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders/checkout") { Content = JsonContent.Create(command) };
        request.Headers.Add("X-Demo-User-Id", who ?? user);
        return await (http ?? db.Http).SendAsync(request);
    }
    private async Task<HttpResponseMessage> GetOrder(string orderId, string who)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/orders/" + orderId);
        request.Headers.Add("X-Demo-User-Id", who);
        return await db.Http.SendAsync(request);
    }

    [Fact]
    public async Task OneProductCheckout_CommitsOrderStockAuditResultAndCompletedCart()
    {
        var product = await Product(5);
        var cart = await Cart(user, (product, 2));
        var command = Command(cart);
        using var response = await Post(command);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/orders/" + command.OrderId, response.Headers.Location!.OriginalString);
        var order = (await response.Content.ReadFromJsonAsync<OrderDocument>())!;
        Assert.Equal(user, order.UserId);
        Assert.Equal(command.OrderId, order.OrderId);
        Assert.Equal(200.50m, order.Total);
        Assert.Equal("INR", order.Currency);
        Assert.Equal(TimeSpan.Zero, order.CreatedAt.Offset);
        var line = Assert.Single(order.Lines);
        Assert.Equal(product.Id, line.ProductId);
        Assert.Equal(product.Name, line.Name);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(product.Price, line.UnitPrice);
        Assert.Equal(order.Total, line.LineTotal);
        var updated = await ReadProduct(product.Id);
        Assert.Equal(3, updated.Stock);
        Assert.NotEqual(product.ETag, updated.ETag);
        Assert.Equal(product.Price, updated.Price);
        var audit = Assert.Single(await CheckoutAudits(product.Id));
        Assert.Equal(5m, audit.OldValue);
        Assert.Equal(3m, audit.NewValue);
        Assert.Equal(user, audit.Actor);
        Assert.Equal(order.CreatedAt, audit.Timestamp);
        Assert.Equal(order.CreatedAt, updated.UpdatedAt);
        Assert.Equal("Checkout " + command.OrderId, audit.Reason);
        Assert.Equal(order.CorrelationId, audit.CorrelationId);
        var completed = (await db.Store.Container.ReadItemAsync<CustomerCart>(CartService.CartId(user), Partition)).Resource;
        Assert.Empty(completed.Lines);
        Assert.Equal(command.OrderId, completed.LastCompletedOrderId);
        Assert.Equal(order.CreatedAt, completed.LastCompletedAt);
        Assert.NotEqual(cart.Version, completed.Version);
        var record = await Request(command);
        Assert.Equal("succeeded", record.Status);
        Assert.Equal(201, record.Result.StatusCode);
        Assert.Equal(order.OrderId, record.Result.Order!.OrderId);
        Assert.Single(await Orders(user));
        var secondPurchase = await Checkout(user, new CheckoutCommand(Uuid(), completed.Version));
        Assert.Equal(409, secondPurchase.StatusCode);
        Assert.Equal(3, (await ReadProduct(product.Id)).Stock);
        Assert.Single(await Orders(user));
    }

    [Fact]
    public async Task MultipleProducts_CheckoutHasCorrectTotalsAndOneAuditPerProduct()
    {
        var first = await Product(10, 1999m);
        var second = await Product(20, 499.50m);
        var cart = await Cart(user, (first, 2), (second, 3));
        var result = await Checkout(user, Command(cart));
        Assert.Equal(201, result.StatusCode);
        Assert.Equal(5496.50m, result.Order!.Total);
        Assert.Equal(2, result.Order.Lines.Count);
        Assert.Equal(8, (await ReadProduct(first.Id)).Stock);
        Assert.Equal(17, (await ReadProduct(second.Id)).Stock);
        Assert.Single(await CheckoutAudits(first.Id));
        Assert.Single(await CheckoutAudits(second.Id));
        Assert.Empty((await new CartService(db.Store).GetAsync(user, CancellationToken.None)).Lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneUnfulfillableLine_LeavesAllProductsAndCartUnchanged(bool missing)
    {
        var first = await Product(5);
        var second = await Product(1);
        var cart = await Cart(user, (first, 1), (second, 1));
        if (missing) await db.Store.Container.DeleteItemAsync<Product>(second.Id, Partition);
        else await new InventoryService(db.Store).AdjustStockAsync(second.Id,
            new StockAdjustment(-1, second.ETag, "admin", "Unfulfillable checkout test"), "test", CancellationToken.None);
        var beforeCart = await StoredCart(user);
        var beforeFirst = await ReadProduct(first.Id);
        var beforeSecond = missing ? null : await ReadProduct(second.Id);
        var command = Command(cart);
        using var response = await Post(command);
        Assert.Equal(missing ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(problem.GetProperty("failures").EnumerateArray(), failure => failure.GetProperty("productId").GetString() == second.Id);
        Assert.Equal(beforeFirst, await ReadProduct(first.Id));
        if (!missing) Assert.Equal(beforeSecond, await ReadProduct(second.Id));
        Assert.Equal(beforeCart, await StoredCart(user));
        Assert.Empty(await Orders(user));
        Assert.Empty(await CheckoutAudits(first.Id));
        Assert.Empty(await CheckoutAudits(second.Id));
        Assert.Equal("rejected", (await Request(command)).Status);
    }

    [Fact]
    public async Task IndependentClientsCompetingForFinalUnit_ExactlyOneOrderWins()
    {
        var product = await Product(1);
        var otherUser = user + "-other";
        var first = await Cart(user, (product, 1));
        var second = await Cart(otherUser, (product, 1));
        using var firstClient = CosmosStore.CreateClient(db.Settings);
        using var secondClient = CosmosStore.CreateClient(db.Settings);
        var gate = new TwoWriterGate();
        var one = new InterleavedCheckout(new CosmosStore(firstClient, db.Settings), gate.Arrive);
        var two = new InterleavedCheckout(new CosmosStore(secondClient, db.Settings), gate.Arrive);
        var results = await Task.WhenAll(one.CheckoutAsync(user, Command(first), "one", CancellationToken.None),
            two.CheckoutAsync(otherUser, Command(second), "two", CancellationToken.None));
        Assert.Single(results, result => result.StatusCode == 201);
        Assert.Single(results, result => result.StatusCode == 412);
        Assert.Equal(0, (await ReadProduct(product.Id)).Stock);
        Assert.Equal(1, (await Orders(user)).Count + (await Orders(otherUser)).Count);
        Assert.Single(await CheckoutAudits(product.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentClients_CheckoutSameCart_DeductOnce(bool sameOrderId)
    {
        var product = await Product(10);
        var cart = await Cart(user, (product, 2));
        var first = Command(cart);
        var second = sameOrderId ? first : Command(cart);
        using var firstClient = CosmosStore.CreateClient(db.Settings);
        using var secondClient = CosmosStore.CreateClient(db.Settings);
        var gate = new TwoWriterGate();
        var one = new InterleavedCheckout(new CosmosStore(firstClient, db.Settings), gate.Arrive);
        var two = new InterleavedCheckout(new CosmosStore(secondClient, db.Settings), gate.Arrive);
        var results = await Task.WhenAll(one.CheckoutAsync(user, first, "one", CancellationToken.None),
            two.CheckoutAsync(user, second, "two", CancellationToken.None));
        Assert.Equal(sameOrderId ? 2 : 1, results.Count(r => r.StatusCode == 201));
        if (sameOrderId)
        {
            Assert.Equal(results[0].Order!.OrderId, results[1].Order!.OrderId);
            Assert.Equal(results[0].Order!.CreatedAt, results[1].Order!.CreatedAt);
        }
        else Assert.Single(results, r => r.StatusCode == 412);
        Assert.Equal(8, (await ReadProduct(product.Id)).Stock);
        Assert.Single(await Orders(user));
        Assert.Single(await CheckoutAudits(product.Id));
    }

    [Fact]
    public async Task RecreatedApi_ReplaysOriginalOrderEvenAfterCartChanges()
    {
        var product = await Product(10);
        var cart = await Cart(user, (product, 1));
        var command = Command(cart);
        using var original = await Post(command);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var originalBody = await original.Content.ReadAsStringAsync();
        await Cart(user, (await ReadProduct(product.Id), 2));
        await db.RestartAsync();
        using var replay = await Post(command);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal(9, (await ReadProduct(product.Id)).Stock);
        Assert.Equal(2, Assert.Single((await new CartService(db.Store).GetAsync(user, CancellationToken.None)).Lines).Quantity);
        Assert.Single(await Orders(user));
    }

    [Fact]
    public async Task OrderIdReuseByOtherPayloadOrUser_IsConflict_AndGetIsUserScoped()
    {
        var product = await Product();
        var cart = await Cart(user, (product, 1));
        var command = Command(cart);
        Assert.Equal(201, (await Checkout(user, command)).StatusCode);
        Assert.Equal(409, (await Checkout(user, command with { ExpectedCartVersion = "different-cart" })).StatusCode);
        Assert.Equal(409, (await Checkout(user + "-other", command)).StatusCode);
        using var own = await GetOrder(command.OrderId!, user);
        using var other = await GetOrder(command.OrderId!, user + "-other");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Single(await Orders(user));
        Assert.Equal(49, (await ReadProduct(product.Id)).Stock);
    }

    [Fact]
    public async Task PriceIncreaseRequiresAcknowledgement_ThenAcceptedPriceIsPurchased()
    {
        var product = await Product(10);
        var cart = await Cart(user, (product, 2));
        await new InventoryService(db.Store).UpdatePriceAsync(product.Id,
            new PriceUpdate(125.75m, product.ETag, "admin", "Price increase test"), "price-test", CancellationToken.None);
        var rejected = await Checkout(user, Command(cart));
        Assert.Equal(412, rejected.StatusCode);
        Assert.Equal("acknowledgementRequired", Assert.Single(rejected.Failures!).Code);
        Assert.Equal(10, (await ReadProduct(product.Id)).Stock);
        Assert.Empty(await Orders(user));
        var cartService = new CartService(db.Store);
        var current = await cartService.GetAsync(user, CancellationToken.None);
        var ack = await cartService.ExecuteAsync(user, "acknowledge", new CartCommand(Uuid(), current.Version,
            Products: current.Lines.Select(l => new ObservedProduct(l.ProductId, l.CurrentProductVersion!)).ToList()), CancellationToken.None);
        Assert.Equal(200, ack.StatusCode);
        var purchased = await Checkout(user, Command(ack.Cart!));
        Assert.Equal(201, purchased.StatusCode);
        Assert.Equal(251.50m, purchased.Order!.Total);
        Assert.Equal(125.75m, Assert.Single(purchased.Order.Lines).UnitPrice);
    }

    [Fact]
    public async Task StockChangeBeforeCheckout_RequiresAcknowledgementEvenIfStockIsSufficient()
    {
        var product = await Product(10);
        var cart = await Cart(user, (product, 1));
        await new InventoryService(db.Store).AdjustStockAsync(product.Id,
            new StockAdjustment(-1, product.ETag, "admin", "Stock snapshot test"), "stock", CancellationToken.None);
        var result = await Checkout(user, Command(cart));
        Assert.Equal(412, result.StatusCode);
        Assert.Equal("acknowledgementRequired", Assert.Single(result.Failures!).Code);
        Assert.Equal(9, (await ReadProduct(product.Id)).Stock);
        Assert.Empty(await Orders(user));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task InvalidPersistedLineQuantity_RejectsWholeCheckout(int quantity)
    {
        var product = await Product(20);
        var cart = new CustomerCart(CartService.CartId(user), CosmosStore.StoreId, user, Uuid(),
            [new CartLine(product.Id, quantity, product.Price, product.Stock, product.ETag!)], DateTimeOffset.UtcNow);
        await db.Store.Container.CreateItemAsync(cart, Partition);
        var before = await StoredCart(user);
        var result = await Checkout(user, new CheckoutCommand(Uuid(), cart.Version));
        Assert.Equal(400, result.StatusCode);
        Assert.Equal(product.Id, Assert.Single(result.Failures!).ProductId);
        Assert.Equal(product, await ReadProduct(product.Id));
        Assert.Equal(before, await StoredCart(user));
        Assert.Empty(await Orders(user));
        Assert.Empty(await CheckoutAudits(product.Id));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("stock")]
    [InlineData("acknowledgement")]
    public async Task ConcurrentAdminOrAcknowledgementWrite_InvalidatesCheckoutBatch(string operation)
    {
        var product = await Product(10);
        var cart = await Cart(user, (product, 2));
        var otherUser = user + "-other";
        var otherCart = await Cart(otherUser, (product, 1));
        var beforeCart = await StoredCart(user);
        using var independentClient = CosmosStore.CreateClient(db.Settings);
        var independentStore = new CosmosStore(independentClient, db.Settings);
        var checkout = new InterleavedCheckout(db.Store, async () =>
        {
            if (operation == "price")
                await new InventoryService(independentStore).UpdatePriceAsync(product.Id,
                    new PriceUpdate(200m, product.ETag, "admin", "Concurrent price test"), "race", CancellationToken.None);
            else if (operation == "stock")
                await new InventoryService(independentStore).AdjustStockAsync(product.Id,
                    new StockAdjustment(-1, product.ETag, "admin", "Concurrent stock test"), "race", CancellationToken.None);
            else
            {
                var ack = await new CartService(independentStore).ExecuteAsync(otherUser, "acknowledge", new CartCommand(Uuid(), otherCart.Version,
                    Products: [new ObservedProduct(product.Id, product.ETag!)]), CancellationToken.None);
                Assert.Equal(200, ack.StatusCode);
            }
        });
        var result = await checkout.CheckoutAsync(user, Command(cart), "checkout-race", CancellationToken.None);
        Assert.Equal(412, result.StatusCode);
        Assert.Equal(beforeCart, await StoredCart(user));
        Assert.Equal(operation == "stock" ? 9 : 10, (await ReadProduct(product.Id)).Stock);
        Assert.Equal(operation == "price" ? 200m : product.Price, (await ReadProduct(product.Id)).Price);
        Assert.Empty(await Orders(user));
        Assert.Empty(await CheckoutAudits(product.Id));
    }

    [Fact]
    public async Task CheckoutInvalidatesAnAlreadyValidatedAcknowledgement_FromIndependentClient()
    {
        var product = await Product(10);
        var cart = await Cart(user, (product, 2));
        var otherUser = user + "-other";
        var otherCart = await Cart(otherUser, (product, 1));
        var beforeOtherCart = await StoredCart(otherUser);
        using var independentClient = CosmosStore.CreateClient(db.Settings);
        var independentStore = new CosmosStore(independentClient, db.Settings);
        var ackService = new InterleavedCart(independentStore, async () => Assert.Equal(201, (await Checkout(user, Command(cart))).StatusCode));
        var ack = await ackService.ExecuteAsync(otherUser, "acknowledge", new CartCommand(Uuid(), otherCart.Version,
            Products: [new ObservedProduct(product.Id, product.ETag!)]), CancellationToken.None);
        Assert.Equal(412, ack.StatusCode);
        Assert.Equal(beforeOtherCart, await StoredCart(otherUser));
        Assert.True((await new CartService(independentStore).GetAsync(otherUser, CancellationToken.None)).RequiresAcknowledgement);
        Assert.Equal(8, (await ReadProduct(product.Id)).Stock);
        Assert.Single(await Orders(user));
    }

    [Fact]
    public async Task AdminWriterConditionalGuard_IsInSameBatchAsProductAndAudit()
    {
        var product = await Product(10);
        var cart = await Cart(user, (product, 1));
        using var independentClient = CosmosStore.CreateClient(db.Settings);
        var independentStore = new CosmosStore(independentClient, db.Settings);
        var admin = new InterleavedInventory(db.Store, () =>
        {
            var ack = new CartService(independentStore).ExecuteAsync(user, "acknowledge", new CartCommand(Uuid(), cart.Version,
                Products: [new ObservedProduct(product.Id, product.ETag!)]), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(200, ack.StatusCode);
        });
        var exception = await Assert.ThrowsAsync<InventoryException>(() => admin.AdjustStockAsync(product.Id,
            new StockAdjustment(-1, product.ETag, "admin", "Guard atomicity test"), "test", CancellationToken.None));
        Assert.Equal(412, exception.StatusCode);
        Assert.Equal(product, await ReadProduct(product.Id));
        Assert.Empty(await Query<InventoryAudit>("inventoryAudit", "productId", product.Id));
    }

    [Fact]
    public async Task HistoricalOrderPricesRemainUnchangedAfterAdminPriceUpdate()
    {
        var product = await Product(10);
        var cart = await Cart(user, (product, 1));
        var result = await Checkout(user, Command(cart));
        var updated = await ReadProduct(product.Id);
        await new InventoryService(db.Store).UpdatePriceAsync(product.Id,
            new PriceUpdate(200m, updated.ETag, "admin", "After checkout test"), "price", CancellationToken.None);
        var historical = await Service.GetAsync(user, result.Order!.OrderId, CancellationToken.None);
        Assert.Equal(product.Price, Assert.Single(historical.Lines).UnitPrice);
        Assert.Equal(product.Price, historical.Total);
        Assert.Equal(result.Order.CreatedAt, historical.CreatedAt);
    }

    [Fact]
    public async Task ForcedLastOperationFailure_RollsBackEveryProductOrderAuditAndCartWrite()
    {
        var first = await Product(10);
        var second = await Product(20);
        var cart = await Cart(user, (first, 2), (second, 3));
        var before = await StoredCart(user);
        var markerId = "failure-marker-" + Uuid();
        await db.Store.Container.CreateItemAsync(new { id = markerId, storeId = CosmosStore.StoreId }, Partition);
        var checkout = new FailingBatchCheckout(db.Store, markerId);
        var command = Command(cart);
        var result = await checkout.CheckoutAsync(user, command, "forced-failure", CancellationToken.None);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(first, await ReadProduct(first.Id));
        Assert.Equal(second, await ReadProduct(second.Id));
        Assert.Equal(before, await StoredCart(user));
        Assert.Empty(await Orders(user));
        Assert.Empty(await CheckoutAudits(first.Id));
        Assert.Empty(await CheckoutAudits(second.Id));
        Assert.Equal("rejected", (await Request(command)).Status);
        using var missingOrder = await db.Store.Container.ReadItemStreamAsync(CheckoutService.OrderDocumentId(Guid.Parse(command.OrderId!)), Partition);
        Assert.Equal(HttpStatusCode.NotFound, missingOrder.StatusCode);
    }

    [Fact]
    public async Task CommitThenLostSuccessResponse_RetryReturnsOriginalOrderWithoutSecondDeduction()
    {
        var product = await Product(10);
        var cart = await Cart(user, (product, 2));
        var command = Command(cart);
        await using var lostResponseApp = LostResponseApp();
        using var lostHttp = lostResponseApp.CreateClient();
        using var lost = await Post(command, http: lostHttp);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, lost.StatusCode); // Success response failed AFTER commit.
        Assert.Equal("succeeded", (await Request(command)).Status);
        var persisted = await Service.GetAsync(user, command.OrderId!, CancellationToken.None);
        using var retry = await Post(command);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var returned = (await retry.Content.ReadFromJsonAsync<OrderDocument>())!;
        Assert.Equal(persisted.OrderId, returned.OrderId);
        Assert.Equal(persisted.CreatedAt, returned.CreatedAt);
        Assert.Equal(persisted.CorrelationId, returned.CorrelationId);
        Assert.Equal(8, (await ReadProduct(product.Id)).Stock);
        Assert.Single(await Orders(user));
        Assert.Single(await CheckoutAudits(product.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderLimitsRejectBeforeAnyBusinessWrites(bool byteLimit)
    {
        var products = new List<Product>();
        for (var index = 0; index < (byteLimit ? 1 : 21); index++)
            products.Add(await Product(10, description: byteLimit ? new string('x', CheckoutService.MaximumDocumentBytes + 1024) : null));
        var cart = new CustomerCart(CartService.CartId(user), CosmosStore.StoreId, user, Uuid(),
            products.Select(p => new CartLine(p.Id, 1, p.Price, p.Stock, p.ETag!)).ToList(), DateTimeOffset.UtcNow);
        await db.Store.Container.CreateItemAsync(cart, Partition);
        var before = await StoredCart(user);
        var result = await Checkout(user, new CheckoutCommand(Uuid(), cart.Version));
        Assert.Equal(413, result.StatusCode);
        Assert.Equal(before, await StoredCart(user));
        foreach (var product in products)
        {
            Assert.Equal(product, await ReadProduct(product.Id));
            Assert.Empty(await CheckoutAudits(product.Id));
        }
        Assert.Empty(await Orders(user));
    }

    [Fact]
    public async Task MaximumSupportedTwentyLineOrder_CommitsInOneBatch()
    {
        var products = new List<Product>();
        for (var index = 0; index < CheckoutService.MaximumOrderLines; index++) products.Add(await Product(10));
        var cart = new CustomerCart(CartService.CartId(user), CosmosStore.StoreId, user, Uuid(),
            products.Select(p => new CartLine(p.Id, 1, p.Price, p.Stock, p.ETag!)).ToList(), DateTimeOffset.UtcNow);
        await db.Store.Container.CreateItemAsync(cart, Partition);
        var result = await Checkout(user, new CheckoutCommand(Uuid(), cart.Version));
        Assert.Equal(201, result.StatusCode);
        Assert.Equal(20, result.Order!.Lines.Count);
        Assert.Equal(2005m, result.Order.Total);
        foreach (var product in products)
        {
            Assert.Equal(9, (await ReadProduct(product.Id)).Stock);
            Assert.Single(await CheckoutAudits(product.Id));
        }
    }

    [Fact]
    public async Task EmptyCartMissingIdentityInvalidUuidAndStaleCartAreRejected()
    {
        using var empty = await Post(new CheckoutCommand(Uuid(), "empty"));
        Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
        using var missingIdentity = await db.Http.PostAsJsonAsync("/api/v1/orders/checkout", new CheckoutCommand(Uuid(), "empty"));
        Assert.Equal(HttpStatusCode.BadRequest, missingIdentity.StatusCode);
        using var badId = await Post(new CheckoutCommand("not-a-uuid", "empty"));
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        var product = await Product();
        await Cart(user, (product, 1));
        using var stale = await Post(new CheckoutCommand(Uuid(), "outdated"));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(product, await ReadProduct(product.Id));
        Assert.Empty(await Orders(user));
    }

    private WebApplicationFactory<Program> LostResponseApp() => new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
    {
        host.UseEnvironment("Development");
        host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cosmos:Endpoint"] = db.Settings.Endpoint, ["Cosmos:Key"] = db.Settings.Key,
            ["Cosmos:DatabaseName"] = db.Settings.DatabaseName, ["Cosmos:ContainerName"] = "StoreData"
        }));
        host.ConfigureTestServices(services =>
        {
            services.RemoveAll<CheckoutService>();
            services.AddSingleton<CheckoutService>(sp => new LostResponseCheckout(sp.GetRequiredService<CosmosStore>()));
        });
    });

    private sealed class TwoWriterGate
    {
        private int count;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Arrive()
        {
            if (Interlocked.Increment(ref count) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }
    private sealed class InterleavedCheckout(CosmosStore store, Func<Task> action) : CheckoutService(store)
    { protected override Task BeforeCommitAsync(CancellationToken ct) => action(); }
    private sealed class InterleavedCart(CosmosStore store, Func<Task> action) : CartService(store)
    { protected override Task BeforeCommitAsync(CancellationToken ct) => action(); }
    private sealed class InterleavedInventory(CosmosStore store, Action action) : InventoryService(store)
    {
        protected override string NewAuditId() { action(); return "test-audit-" + Uuid(); }
    }
    private sealed class FailingBatchCheckout(CosmosStore store, string markerId) : CheckoutService(store)
    {
        protected override Task<TransactionalBatchResponse> ExecuteBatchAsync(TransactionalBatch batch, CancellationToken ct) =>
            batch.CreateItem(new { id = markerId, storeId = CosmosStore.StoreId }).ExecuteAsync(ct);
    }
    private sealed class LostResponseCheckout(CosmosStore store) : CheckoutService(store)
    {
        protected override Task AfterCommitAsync(OrderDocument order, CancellationToken ct) =>
            throw new IOException("Test: successful commit followed by failure to deliver success response.");
    }
}
