using System.Text.Json.Serialization;

namespace IPLFranchiseStore.Api;

public sealed record CartLine(string ProductId, int Quantity, decimal AcceptedPrice, int AcceptedStock, string AcceptedProductVersion);
public sealed record CustomerCart(string Id, string StoreId, string UserId, string Version, List<CartLine> Lines, DateTimeOffset UpdatedAt)
{
    public string Type { get; init; } = "cart";
    public string? LastCompletedOrderId { get; init; }
    public DateTimeOffset? LastCompletedAt { get; init; }
    [JsonPropertyName("_etag")]
    public string? ETag { get; init; }
}
public sealed record CartLineView(string ProductId, string? Name, int Quantity, decimal AcceptedPrice, int AcceptedStock,
    string AcceptedProductVersion, decimal? CurrentPrice, int? CurrentStock, string? CurrentProductVersion,
    bool PriceChanged, bool StockChanged, bool VersionChanged, bool InsufficientStock, bool Unavailable,
    bool RequiresAcknowledgement, decimal? CurrentSubtotal);
public sealed record CartView(string UserId, string Version, List<CartLineView> Lines, string Currency,
    decimal? CurrentTotal, bool RequiresAcknowledgement, bool CanCheckout);
public sealed record ObservedProduct(string ProductId, string ExpectedETag);
public sealed record CartCommand(string? RequestId, string? ExpectedCartVersion, string? ProductId = null,
    int? Quantity = null, List<ObservedProduct>? Products = null);
public sealed record CartOutcome(int StatusCode, CartView? Cart = null, string? Error = null);
public sealed record CartRequestRecord(string Id, string StoreId, string RequestId, string UserId, string Operation,
    string PayloadFingerprint, CartOutcome Result, string Status, DateTimeOffset CreatedAt)
{
    public string Type { get; init; } = "cartRequest";
}
public sealed class CartException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
