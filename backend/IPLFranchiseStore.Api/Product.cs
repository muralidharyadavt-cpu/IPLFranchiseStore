using System.Text.Json.Serialization;

namespace IPLFranchiseStore.Api;

public sealed record Product
{
    [JsonPropertyName("_etag")]
    public string? ETag { get; init; }
    public required string Id { get; init; }
    public required string StoreId { get; init; }
    public string Type { get; init; } = "product";
    public string? MerchandiseType { get; init; }
    public required string Name { get; init; }
    public required string Franchise { get; init; }
    public string FranchiseId => ProductTaxonomy.FranchiseId(Franchise);
    public string FranchiseName => ProductTaxonomy.FranchiseName(Franchise);
    public bool IsLimitedEdition => ProductTaxonomy.EffectiveType(this) == ProductTaxonomy.SignedPhotograph;
    public required string Description { get; init; }
    // Decimal amount in INR, without currency conversion or floating-point rounding.
    public required decimal Price { get; init; }
    public string Currency { get; init; } = "INR";
    public required int Stock { get; init; }
    // Additive API field; old documents/clients do not need to provide or understand it.
    public string StockStatus => Stock <= 0 ? "outOfStock" : Stock <= 5 ? "lowStock" : "inStock";
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
