using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IPLFranchiseStore.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api.Tests;

public sealed class QueryIntegrationTests(QueryTestDatabase db) : IClassFixture<QueryTestDatabase>
{
    private Task<HttpResponseMessage> Get(string uri, string? user = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (user is not null) request.Headers.Add("X-Demo-User-Id", user);
        return db.Host.Http.SendAsync(request);
    }
    private async Task<PageResult<T>> Page<T>(string uri, string? user = null)
    {
        using var response = await Get(uri, user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PageResult<T>>())!;
    }
    private static string WithToken(string uri, string token) => uri + "&continuationToken=" + Uri.EscapeDataString(token);
    private async Task<List<T>> Enumerate<T>(string uri, long expectedCount, string? user = null)
    {
        var items = new List<T>();
        var tokens = new HashSet<string>();
        string? token = null;
        do
        {
            var page = await Page<T>(token is null ? uri : WithToken(uri, token), user);
            Assert.InRange(page.Items.Count, 0, page.PageSize);
            Assert.Equal(expectedCount, page.TotalCount);
            items.AddRange(page.Items);
            token = page.NextContinuationToken;
            if (token is not null) Assert.True(tokens.Add(token), "Pagination repeated a token.");
        } while (token is not null);
        return items;
    }
    private async Task BadRequest(string uri, string? user = null)
    {
        using var response = await Get(uri, user);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal(400, problem!.Status);
    }
    private static List<Product> SortProducts(IEnumerable<Product> products) => products.OrderBy(p => p.Name, StringComparer.Ordinal)
        .ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
    private static List<OrderDocument> SortOrders(IEnumerable<OrderDocument> orders) => orders.OrderByDescending(o => o.CreatedAt)
        .ThenByDescending(o => o.Id, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task DetailsReturnPersistedMetadataAndMissingReturnsProblem()
    {
        using var response = await Get("/api/v1/products/csk-cap");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var product = (await response.Content.ReadFromJsonAsync<Product>())!;
        Assert.Equal("Cap", product.MerchandiseType);
        Assert.Equal("Chennai Super Kings", product.FranchiseName);
        Assert.Equal(499m, product.Price);
        Assert.NotNull(product.ETag);
        using var missing = await Get("/api/v1/products/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task LegacyArrayIsBoundedAndLinksToCompleteEnvelopeEnumeration()
    {
        using var response = await Get("/api/v1/products");
        Assert.Equal("true", response.Headers.GetValues("Deprecation").Single());
        var products = (await response.Content.ReadFromJsonAsync<List<Product>>())!;
        Assert.InRange(products.Count, 0, 50);
        var link = response.Headers.GetValues("Link").Single(v => v.Contains("rel=\"next\""));
        var next = link[(link.IndexOf('<') + 1)..link.IndexOf('>')];
        var ids = products.Select(p => p.Id).ToList();
        while (true)
        {
            var page = await Page<Product>(next);
            ids.AddRange(page.Items.Select(p => p.Id));
            if (page.NextContinuationToken is null) break;
            next = WithToken("/api/v1/products?paged=true&pageSize=50", page.NextContinuationToken);
        }
        Assert.Equal(SortProducts(db.Products).Select(p => p.Id), ids);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CatalogueFilterCombinations_AreAndCombinedAndCaseInsensitive(bool search, bool type, bool franchise)
    {
        var uri = "/api/v1/products?paged=true&pageSize=7&includeTotalCount=true";
        var expected = db.Products.AsEnumerable();
        if (search) { uri += "&search=fIxTuRe"; expected = expected.Where(p => p.Name.Contains("fIxTuRe", StringComparison.OrdinalIgnoreCase)); }
        if (type) { uri += "&type=cAp"; expected = expected.Where(p => p.MerchandiseType == "Cap"); }
        if (franchise) { uri += "&franchise=mI"; expected = expected.Where(p => p.Franchise.Equals("mi", StringComparison.OrdinalIgnoreCase)); }
        var sorted = SortProducts(expected);
        var actual = await Enumerate<Product>(uri, sorted.Count);
        Assert.Equal(sorted.Select(p => p.Id), actual.Select(p => p.Id));
        Assert.Equal(actual.Count, actual.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task CatalogueMultiPageEnumeration_OverOneHundredPersistedProducts_HasNoDuplicatesOrOmissions()
    {
        var expected = SortProducts(db.Products);
        var actual = await Enumerate<Product>("/api/v1/products?paged=true&pageSize=11&includeTotalCount=true", expected.Count);
        Assert.Equal(124, actual.Count);
        Assert.Equal(expected.Select(p => p.Id), actual.Select(p => p.Id));
        foreach (var group in actual.GroupBy(p => p.Name).Where(g => g.Count() > 1))
            Assert.Equal(group.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal), group.Select(p => p.Id));
    }

    [Theory]
    [InlineData("search=NoSuchProduct")]
    [InlineData("type=order")]
    [InlineData("franchise=NoSuchFranchise")]
    [InlineData("search=Jersey&type=order&franchise=MI")]
    [InlineData("search=%27%20OR%201%3D1")]
    public async Task NoMatchingProducts_ReturnsEmptyPageAndZeroCount(string filters)
    {
        var page = await Page<Product>("/api/v1/products?includeTotalCount=true&" + filters);
        Assert.Empty(page.Items);
        Assert.Null(page.NextContinuationToken);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task DefaultsAndMaximumPageSize_AreAppliedBySdk()
    {
        var defaultProducts = await Page<Product>("/api/v1/products?paged=true");
        Assert.Equal(10, defaultProducts.PageSize);
        Assert.InRange(defaultProducts.Items.Count, 1, 10);
        Assert.Null(defaultProducts.TotalCount);
        var maximum = await Page<Product>("/api/v1/products?pageSize=50");
        Assert.Equal(50, maximum.PageSize);
        Assert.InRange(maximum.Items.Count, 1, 50);
        var orders = await Page<OrderDocument>("/api/v1/orders", "history-alice");
        Assert.Equal(10, orders.PageSize);
        Assert.InRange(orders.Items.Count, 1, 10);
        Assert.Null(orders.TotalCount);
        var maximumOrders = await Page<OrderDocument>("/api/v1/orders?pageSize=50", "history-alice");
        Assert.Equal(50, maximumOrders.PageSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("51")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("2147483648")]
    public async Task InvalidPageSizes_Return400ForBothEndpoints(string size)
    {
        await BadRequest("/api/v1/products?pageSize=" + Uri.EscapeDataString(size));
        await BadRequest("/api/v1/orders?pageSize=" + Uri.EscapeDataString(size), "history-alice");
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("")]
    [InlineData("e30=")]
    public async Task InvalidContinuationTokens_AreSanitizedProblemDetails(string token)
    {
        await BadRequest("/api/v1/products?paged=true&continuationToken=" + Uri.EscapeDataString(token));
        await BadRequest("/api/v1/orders?continuationToken=" + Uri.EscapeDataString(token), "history-alice");
    }

    [Theory]
    [InlineData("search=Fixture&pageSize=3")]
    [InlineData("type=product&pageSize=3")]
    [InlineData("franchise=MI&pageSize=3")]
    [InlineData("pageSize=4")]
    [InlineData("pageSize=3&includeTotalCount=true")]
    public async Task CatalogueTokens_AreBoundToFiltersAndPagingOptions(string differentQuery)
    {
        var page = await Page<Product>("/api/v1/products?pageSize=3");
        Assert.NotNull(page.NextContinuationToken);
        await BadRequest(WithToken("/api/v1/products?" + differentQuery, page.NextContinuationToken));
    }

    [Fact]
    public async Task TokenTamperingCrossEndpointAndCrossUserReplay_AreRejected()
    {
        var products = await Page<Product>("/api/v1/products?pageSize=2");
        var token = products.NextContinuationToken!;
        var tampered = token[..10] + (token[10] == 'A' ? 'B' : 'A') + token[11..];
        await BadRequest(WithToken("/api/v1/products?pageSize=2", tampered));
        await BadRequest(WithToken("/api/v1/orders?pageSize=2", token), "history-alice");
        var orders = await Page<OrderDocument>("/api/v1/orders?pageSize=2", "history-alice");
        await BadRequest(WithToken("/api/v1/products?pageSize=2", orders.NextContinuationToken!));
        await BadRequest(WithToken("/api/v1/orders?pageSize=2", orders.NextContinuationToken!), "history-bob");
    }

    [Fact]
    public async Task ContinuationTokenSurvivesApiRecreation_WithSameLocalKeyRing()
    {
        var first = await Page<Product>("/api/v1/products?pageSize=3");
        await db.Host.RestartAsync();
        var second = await Page<Product>(WithToken("/api/v1/products?pageSize=3", first.NextContinuationToken!));
        Assert.Equal(SortProducts(db.Products).Skip(3).Take(3).Select(p => p.Id), second.Items.Select(p => p.Id));
    }

    [Fact]
    public async Task LegacyArrayContractAndExistingFields_RemainCompatible_WithAdditiveStockStatus()
    {
        using var response = await Get("/api/v1/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.InRange(json.GetArrayLength(), 0, 50);
        string[] oldFields = ["_etag", "id", "storeId", "type", "name", "franchise", "description", "price", "currency", "stock", "createdAt", "updatedAt"];
        foreach (var product in json.EnumerateArray())
        {
            foreach (var field in oldFields) Assert.True(product.TryGetProperty(field, out _), "Missing old product field " + field);
            Assert.Equal("product", product.GetProperty("type").GetString());
            Assert.Equal("INR", product.GetProperty("currency").GetString());
            var stock = product.GetProperty("stock").GetInt32();
            Assert.Equal(stock <= 0 ? "outOfStock" : stock <= 5 ? "lowStock" : "inStock", product.GetProperty("stockStatus").GetString());
        }
        var oldClient = json.Deserialize<List<LegacyProduct>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(json.GetArrayLength(), oldClient.Count);
        Assert.All(oldClient, p => { Assert.False(string.IsNullOrWhiteSpace(p.Name)); Assert.True(p.Price > 0); });
    }

    [Theory]
    [InlineData("history-alice")]
    [InlineData("history-bob")]
    public async Task OrderHistoryIsUserScopedNewestFirst_WithStableTimestampTiesAndFullEnumeration(string user)
    {
        var expected = SortOrders(db.Orders.Where(o => o.UserId == user));
        var actual = await Enumerate<OrderDocument>("/api/v1/orders?pageSize=4&includeTotalCount=true", expected.Count, user);
        Assert.Equal(expected.Select(o => o.Id), actual.Select(o => o.Id));
        Assert.Equal(actual.Count, actual.Select(o => o.Id).Distinct().Count());
        Assert.All(actual, order => Assert.Equal(user, order.UserId));
        Assert.All(actual, order => Assert.Equal(TimeSpan.Zero, order.CreatedAt.Offset));
    }

    [Theory]
    [InlineData("2026-10-01T02:00:00Z", "2026-10-01T04:00:00+00:00")]
    [InlineData("2026-10-01T02:00:00Z", "2026-10-01T02:00:00Z")]
    [InlineData("2026-10-01T00:00:00.0000001Z", "2026-10-01T00:00:00.1Z")]
    [InlineData(null, "2026-10-01T00:00:00Z")]
    [InlineData("2026-10-01T10:00:00Z", null)]
    public async Task UtcDateFiltersRespectInclusiveBoundariesAndSubsecondPrecision(string? from, string? to)
    {
        var uri = "/api/v1/orders?pageSize=2&includeTotalCount=true";
        var expected = db.Orders.Where(o => o.UserId == "history-alice");
        if (from is not null) { uri += "&dateFrom=" + Uri.EscapeDataString(from); expected = expected.Where(o => o.CreatedAt >= DateTimeOffset.Parse(from)); }
        if (to is not null) { uri += "&dateTo=" + Uri.EscapeDataString(to); expected = expected.Where(o => o.CreatedAt <= DateTimeOffset.Parse(to)); }
        var sorted = SortOrders(expected);
        var actual = await Enumerate<OrderDocument>(uri, sorted.Count, "history-alice");
        Assert.Equal(sorted.Select(o => o.Id), actual.Select(o => o.Id));
    }

    [Theory]
    [InlineData("dateFrom=not-a-date")]
    [InlineData("dateFrom=2026-10-01")]
    [InlineData("dateTo=2026-10-01T00%3A00%3A00")]
    [InlineData("dateFrom=2026-10-01T00%3A00%3A00%2B05%3A30")]
    [InlineData("dateTo=2026-02-30T00%3A00%3A00Z")]
    [InlineData("dateFrom=2026-10-02T00%3A00%3A00Z&dateTo=2026-10-01T00%3A00%3A00Z")]
    public Task InvalidDates_Return400ProblemDetails(string query) => BadRequest("/api/v1/orders?" + query, "history-alice");

    [Fact]
    public async Task HistoryTokensAreBoundToDates_AndUnknownUserGetsNoOtherUsersOrders()
    {
        var first = await Page<OrderDocument>("/api/v1/orders?pageSize=2", "history-alice");
        await BadRequest(WithToken("/api/v1/orders?pageSize=2&dateFrom=2026-10-01T00%3A00%3A00Z", first.NextContinuationToken!), "history-alice");
        var unknown = await Page<OrderDocument>("/api/v1/orders?includeTotalCount=true", "history-unknown");
        Assert.Empty(unknown.Items);
        Assert.Equal(0, unknown.TotalCount);
        Assert.Null(unknown.NextContinuationToken);
        await BadRequest("/api/v1/orders");
    }

    [Fact]
    public async Task OrderHistoryPreservesHistoricalNamesAndMoneySnapshots()
    {
        var actual = await Enumerate<OrderDocument>("/api/v1/orders?pageSize=9&includeTotalCount=true", 40, "history-alice");
        foreach (var order in actual)
        {
            var expected = db.Orders.Single(o => o.Id == order.Id);
            Assert.Equal(expected.Total, order.Total);
            Assert.Equal(expected.Lines, order.Lines);
            Assert.Equal(expected.PurchasedCartVersion, order.PurchasedCartVersion);
            Assert.NotEqual(db.Products.Single(p => p.Id == "catalogue-000").Name, order.Lines[0].Name);
        }
    }

    [Fact]
    public async Task IndexMigrationIsAdditiveIdempotentAndPreservesExistingProductVersions()
    {
        var settings = new CosmosSettings
        {
            Endpoint = db.Host.Settings.Endpoint, Key = db.Host.Settings.Key,
            DatabaseName = "IPLFranchiseStoreTests-" + Guid.NewGuid().ToString("N")
        };
        using var client = CosmosStore.CreateClient(settings);
        var store = new CosmosStore(client, settings);
        try
        {
            var database = await client.CreateDatabaseIfNotExistsAsync(settings.DatabaseName);
            var properties = new ContainerProperties(settings.ContainerName, "/storeId");
            properties.IndexingPolicy.CompositeIndexes.Add(new()
            {
                new() { Path = "/franchise", Order = CompositePathSortOrder.Ascending },
                new() { Path = "/id", Order = CompositePathSortOrder.Ascending }
            });
            await database.Database.CreateContainerAsync(properties);
            var original = db.Products.First() with { Id = "mi-jersey", Stock = 17, Price = 123.45m };
            var saved = (await store.Container.CreateItemAsync(original, new PartitionKey(CosmosStore.StoreId))).Resource;
            await store.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            var metadata = (await store.Container.ReadContainerAsync()).Resource;
            Assert.Equal(3, metadata.IndexingPolicy.CompositeIndexes.Count);
            foreach (var required in StoreIndexes.Required()) Assert.Contains(metadata.IndexingPolicy.CompositeIndexes,
                index => index.Select(p => (p.Path, p.Order)).SequenceEqual(required.Select(p => (p.Path, p.Order))));
            var persisted = (await store.Container.ReadItemAsync<Product>(saved.Id, new PartitionKey(CosmosStore.StoreId))).Resource;
            Assert.Equal(saved, persisted);
            var service = new CatalogueService(new CosmosPaging(store, settings, new EphemeralDataProtectionProvider()));
            var page = await service.SearchAsync(null, null, null, "10", null, "true", CancellationToken.None);
            Assert.Equal(4, page.TotalCount);
            Assert.Contains(page.Items, p => p.Id == saved.Id && p.Stock == 17 && p.Price == 123.45m);
        }
        finally { await client.GetDatabase(settings.DatabaseName).DeleteAsync(); }
    }

    private sealed record LegacyProduct(string Id, string Name, string Type, string Franchise, decimal Price, int Stock);

    [Theory]
    [InlineData("Cap", "Chennai Super Kings")]
    [InlineData("cAP", "cSk")]
    [InlineData("CAP", "chennai super kings")]
    public async Task MerchandiseTypeAndFranchiseAliasesReturnCskCap(string type, string franchise)
    {
        var page = await Page<Product>("/api/v1/products?search=CSK&type=" + Uri.EscapeDataString(type) +
            "&franchise=" + Uri.EscapeDataString(franchise) + "&includeTotalCount=true");
        var product = Assert.Single(page.Items);
        Assert.Equal("csk-cap", product.Id);
        Assert.Equal("product", product.Type);
        Assert.Equal("Cap", product.MerchandiseType);
        Assert.Equal("CSK", product.Franchise);
        Assert.Equal("CSK", product.FranchiseId);
        Assert.Equal("Chennai Super Kings", product.FranchiseName);
        Assert.Equal(499m, product.Price);
        Assert.Equal(100, product.Stock);
        Assert.Equal("inStock", product.StockStatus);
    }

    [Fact]
    public async Task PartialJerSearchIncludesMiJerseyAndAllFiltersUseAnd()
    {
        var expected = SortProducts(db.Products.Where(p => p.MerchandiseType == "Jersey" && p.FranchiseId == "MI" &&
            p.Name.Contains("jer", StringComparison.OrdinalIgnoreCase)));
        var jerseys = await Enumerate<Product>("/api/v1/products?search=jEr&type=Jersey&franchise=Mumbai%20Indians&pageSize=3&includeTotalCount=true", expected.Count);
        Assert.Contains(jerseys, product => product.Id == "mi-jersey");
        Assert.Equal(expected.Select(p => p.Id), jerseys.Select(p => p.Id));
        var conflict = await Page<Product>("/api/v1/products?search=jer&type=Cap&franchise=MI&includeTotalCount=true");
        Assert.Empty(conflict.Items);
        Assert.Equal(0, conflict.TotalCount);
        var conflictingAliases = await Page<Product>("/api/v1/products?type=Jersey&merchandiseType=Cap&includeTotalCount=true");
        Assert.Empty(conflictingAliases.Items);
    }

    [Theory]
    [InlineData("type=Cap")]
    [InlineData("merchandiseType=cAP")]
    [InlineData("type=product&merchandiseType=Cap")]
    public async Task MerchandiseFilteredPaginationUsesPersistedClassificationWithoutDuplicates(string filter)
    {
        var expected = SortProducts(db.Products.Where(p => p.MerchandiseType == "Cap" && p.FranchiseId == "CSK"));
        var actual = await Enumerate<Product>("/api/v1/products?" + filter +
            "&franchise=Chennai%20Super%20Kings&pageSize=2&includeTotalCount=true", expected.Count);
        Assert.Equal(21, actual.Count);
        Assert.Equal(expected.Select(p => p.Id), actual.Select(p => p.Id));
        Assert.Equal(actual.Count, actual.Select(p => p.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("type=Signed%20Photograph", "kkr-signed-photo")]
    [InlineData("merchandiseType=SIGNED%20PHOTO", "kkr-signed-photo")]
    [InlineData("type=Flag&franchise=Royal%20Challengers%20Bengaluru", "rcb-flag")]
    [InlineData("type=Flag&franchise=Royal%20Challengers%20Bangalore", "rcb-flag")]
    public async Task RemainingSeedTypesAndFullFranchiseNamesAreSupported(string filter, string id)
    {
        var page = await Page<Product>("/api/v1/products?" + filter + "&includeTotalCount=true");
        var product = Assert.Single(page.Items);
        Assert.Equal(id, product.Id);
        Assert.Equal(id == "kkr-signed-photo", product.IsLimitedEdition);
    }

    [Theory]
    [InlineData("type=Unsupported")]
    [InlineData("merchandiseType=Unsupported")]
    [InlineData("franchise=Unsupported")]
    [InlineData("merchandiseType=product")]
    [InlineData("type=Other")]
    public async Task UnsupportedNonemptyValuesConsistentlyReturnAnEmptyPage(string filter)
    {
        var page = await Page<Product>("/api/v1/products?" + filter + "&includeTotalCount=true");
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Null(page.NextContinuationToken);
    }

    [Theory]
    [InlineData("type=")]
    [InlineData("merchandiseType=%20")]
    [InlineData("franchise=")]
    [InlineData("category=Cap")]
    [InlineData("q=jer")]
    [InlineData("franchiseId=CSK")]
    [InlineData("offset=10")]
    [InlineData("type=Cap&type=Jersey")]
    [InlineData("paged=false&type=Cap")]
    public Task AmbiguousUnsupportedOrMalformedParametersReturn400(string filter) => BadRequest("/api/v1/products?" + filter);

    [Fact]
    public async Task MerchandiseTokensCannotBeUsedForDifferentTypesOrFranchises()
    {
        var first = await Page<Product>("/api/v1/products?type=Cap&franchise=CSK&pageSize=2");
        Assert.NotNull(first.NextContinuationToken);
        await BadRequest(WithToken("/api/v1/products?type=Jersey&franchise=CSK&pageSize=2", first.NextContinuationToken));
        await BadRequest(WithToken("/api/v1/products?type=Cap&franchise=MI&pageSize=2", first.NextContinuationToken));
        await BadRequest(WithToken("/api/v1/products?merchandiseType=Jersey&franchise=CSK&pageSize=2", first.NextContinuationToken));
    }

    [Fact]
    public async Task ExplicitStoredMerchandiseTypeTakesPrecedenceOverProductName()
    {
        var name = "Taxonomy-probe-" + Guid.NewGuid().ToString("N") + " Jersey";
        var product = db.Products.First() with { Id = "taxonomy-probe-" + Guid.NewGuid().ToString("N"), Name = name, MerchandiseType = "Cap" };
        var partition = new PartitionKey(CosmosStore.StoreId);
        try
        {
            await db.Host.Store.Container.CreateItemAsync(product, partition);
            var cap = await Page<Product>("/api/v1/products?search=" + Uri.EscapeDataString(name) + "&type=Cap");
            Assert.Equal(product.Id, Assert.Single(cap.Items).Id);
            var jersey = await Page<Product>("/api/v1/products?search=" + Uri.EscapeDataString(name) + "&type=Jersey");
            Assert.Empty(jersey.Items);
        }
        finally { await db.Host.Store.Container.DeleteItemAsync<Product>(product.Id, partition); }
    }

    [Fact]
    public async Task DeprecatedTypeProductRetainsLegacyDiscriminatorFiltering()
    {
        var actual = await Enumerate<Product>("/api/v1/products?type=product&pageSize=50&includeTotalCount=true", db.Products.Count);
        Assert.Equal(SortProducts(db.Products).Select(p => p.Id), actual.Select(p => p.Id));
    }

    [Fact]
    public async Task OtherIncludesExplicitAndUnclassifiedLegacyProductsWithoutGuessingFromNames()
    {
        var prefix = "other-probe-" + Guid.NewGuid().ToString("N");
        var source = db.Products.First();
        Product[] probes =
        [
            source with { Id = prefix + "-explicit", Name = prefix + " Other", MerchandiseType = "other" },
            source with { Id = prefix + "-legacy", Name = prefix + " Cap", MerchandiseType = null },
            source with { Id = prefix + "-cap", Name = prefix + " Jersey", MerchandiseType = "Cap" }
        ];
        var partition = new PartitionKey(CosmosStore.StoreId);
        var created = new List<string>();
        try
        {
            foreach (var probe in probes) { await db.Host.Store.Container.CreateItemAsync(probe, partition); created.Add(probe.Id); }
            var other = await Page<Product>("/api/v1/products?search=" + prefix + "&type=Other&includeTotalCount=true");
            Assert.Equal(2, other.TotalCount);
            Assert.Equal(new[] { prefix + "-explicit", prefix + "-legacy" }, other.Items.Select(p => p.Id).OrderBy(id => id));
            Assert.All(other.Items, p => Assert.Equal("Other", p.MerchandiseType));
            var cap = await Page<Product>("/api/v1/products?search=" + prefix + "&type=Cap");
            Assert.Equal(prefix + "-cap", Assert.Single(cap.Items).Id);
        }
        finally { foreach (var id in created) await db.Host.Store.Container.DeleteItemAsync<Product>(id, partition); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySeedJsonWithoutMerchandiseMetadataWorksWithoutAnyDocumentMigration(bool explicitNull)
    {
        var settings = new CosmosSettings { Endpoint = db.Host.Settings.Endpoint, Key = db.Host.Settings.Key,
            DatabaseName = "IPLFranchiseStoreTests-" + Guid.NewGuid().ToString("N") };
        using var client = CosmosStore.CreateClient(settings);
        var store = new CosmosStore(client, settings);
        var partition = new PartitionKey(CosmosStore.StoreId);
        var versions = new Dictionary<string, string>();
        try
        {
            var database = await client.CreateDatabaseIfNotExistsAsync(settings.DatabaseName);
            await database.Database.CreateContainerAsync(new ContainerProperties(settings.ContainerName, "/storeId"));
            foreach (var id in new[] { "mi-jersey", "csk-cap", "rcb-flag", "kkr-signed-photo" })
            {
                var json = JsonSerializer.SerializeToElement(db.Products.Single(p => p.Id == id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var legacy = json.Deserialize<Dictionary<string, JsonElement>>()!;
                foreach (var key in new[] { "merchandiseType", "franchiseId", "franchiseName", "isLimitedEdition", "stockStatus", "_etag" }) legacy.Remove(key);
                if (explicitNull) legacy["merchandiseType"] = JsonSerializer.SerializeToElement<string?>(null);
                var response = await store.Container.CreateItemAsync(legacy, partition);
                versions[id] = response.ETag;
            }
            await store.InitializeAsync(CancellationToken.None);
            var catalogue = new CatalogueService(new CosmosPaging(store, settings, new EphemeralDataProtectionProvider()));
            var searched = await catalogue.SearchAsync("jer", null, null, "10", null, "true", CancellationToken.None);
            Assert.Equal("mi-jersey", Assert.Single(searched.Items).Id);
            foreach (var franchise in new[] { "Chennai Super Kings", "CSK", "chennai super kings" })
            {
                var page = await catalogue.SearchAsync(null, "cAp", franchise, "10", null, "true", CancellationToken.None);
                Assert.Equal("csk-cap", Assert.Single(page.Items).Id);
                Assert.Equal("Cap", page.Items[0].MerchandiseType);
            }
            foreach (var (id, etag) in versions)
            {
                var persisted = await store.Container.ReadItemAsync<JsonElement>(id, partition);
                Assert.Equal(etag, persisted.ETag);
                if (explicitNull) Assert.Equal(JsonValueKind.Null, persisted.Resource.GetProperty("merchandiseType").ValueKind);
                else Assert.False(persisted.Resource.TryGetProperty("merchandiseType", out _));
            }
            var legacyProducts = await store.GetProductsAsync(CancellationToken.None);
            Assert.Equal(4, legacyProducts.Count);
            Assert.All(legacyProducts, p => Assert.NotNull(p.MerchandiseType));
        }
        finally { await client.GetDatabase(settings.DatabaseName).DeleteAsync(); }
    }
}
