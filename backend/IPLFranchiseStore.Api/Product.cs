namespace IPLFranchiseStore.Api;

public sealed record Product
{
    public required string Id { get; init; }
    public required string StoreId { get; init; }
    public string Type { get; init; } = "product";
    public required string Name { get; init; }
    public required string Franchise { get; init; }
    public required string Description { get; init; }
    // Decimal amount in INR, without currency conversion or floating-point rounding.
    public required decimal Price { get; init; }
    public string Currency { get; init; } = "INR";
    public required int Stock { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
