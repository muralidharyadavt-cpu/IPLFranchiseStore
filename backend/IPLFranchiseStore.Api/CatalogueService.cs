namespace IPLFranchiseStore.Api;

public sealed class CatalogueService(CosmosPaging paging)
{
    public async Task<Product> GetAsync(string id, CosmosStore store, CancellationToken ct)
    {
        if (id.Length > 256 || id.IndexOfAny(['/', '\\', '?', '#']) >= 0) throw new QueryException(404, "Product not found.");
        var product = await store.ReadProductAsync(id, ct) ?? throw new QueryException(404, "Product not found.");
        return ProductTaxonomy.Enrich(product);
    }

    public async Task<PageResult<Product>> SearchAsync(string? search, string? type, string? franchise,
        string? pageSize, string? continuationToken, string? includeTotalCount, CancellationToken ct, string? merchandiseType = null)
    {
        search = CosmosPaging.TextFilter(search, "search");
        type = CosmosPaging.TextFilter(type, "type", 64);
        franchise = CosmosPaging.TextFilter(franchise, "franchise", 64);
        merchandiseType = CosmosPaging.TextFilter(merchandiseType, "merchandiseType", 64);
        var options = PageOptions.Parse(pageSize, includeTotalCount);
        var where = "c.type = @documentType";
        var parameters = new List<(string, object)> { ("@documentType", "product") };
        if (search is not null) { where += " AND CONTAINS(c.name, @search, true)"; parameters.Add(("@search", search)); }
        if (type is not null)
        {
            if (type.Equals("product", StringComparison.OrdinalIgnoreCase))
            { where += " AND c.type = @legacyType"; parameters.Add(("@legacyType", "product")); }
            else AddMerchandiseFilter(type, "type");
        }
        if (merchandiseType is not null) AddMerchandiseFilter(merchandiseType, "merchandiseType");
        if (franchise is not null)
        {
            var aliases = ProductTaxonomy.FranchiseValues(franchise);
            where += " AND (" + string.Join(" OR ", aliases.Select((_, index) => $"STRINGEQUALS(c.franchise, @franchise{index}, true)")) + ")";
            for (var index = 0; index < aliases.Length; index++) parameters.Add(($"@franchise{index}", aliases[index]));
        }
        var page = await paging.ReadAsync<Product>("catalogue-v2-merchandise", where, parameters, "c.name ASC, c.id ASC", options, continuationToken, ct);
        return page with { Items = page.Items.Select(ProductTaxonomy.Enrich).ToList() };

        void AddMerchandiseFilter(string value, string parameter)
        {
            var canonical = ProductTaxonomy.NormalizeMerchandiseType(value);
            if (canonical is null) { where += $" AND IS_NULL(@{parameter})"; parameters.Add(("@" + parameter, value)); return; }
            var ids = canonical == "Other" ? ProductTaxonomy.KnownLegacyIds() : ProductTaxonomy.LegacyIds(canonical);
            where += $" AND (STRINGEQUALS(c.merchandiseType, @{parameter}, true) OR " +
                $"((NOT IS_DEFINED(c.merchandiseType) OR IS_NULL(c.merchandiseType)) AND " +
                (canonical == "Other" ? "NOT " : "") + $"ARRAY_CONTAINS(@{parameter}LegacyIds, c.id)))";
            parameters.Add(("@" + parameter, canonical));
            parameters.Add(("@" + parameter + "LegacyIds", ids));
        }
    }
}
