namespace IPLFranchiseStore.Api;

public sealed record CheckoutCommand(string? OrderId, string? ExpectedCartVersion);
public sealed record OrderLine(string ProductId, string Name, int Quantity, decimal UnitPrice, decimal LineTotal,
    string ProductVersion);
public sealed record OrderDocument(string Id, string StoreId, string OrderId, string UserId, DateTimeOffset CreatedAt,
    string Currency, decimal Total, List<OrderLine> Lines, string PurchasedCartVersion, string CorrelationId)
{
    public string Type { get; init; } = "order";
}
public sealed record CheckoutFailure(string ProductId, string Code, string Reason);
public sealed record CheckoutOutcome(int StatusCode, OrderDocument? Order = null, string? Error = null,
    List<CheckoutFailure>? Failures = null);
public sealed record CheckoutRequestRecord(string Id, string StoreId, string OrderId, string UserId,
    string PayloadFingerprint, string Status, CheckoutOutcome Result, DateTimeOffset CreatedAt)
{
    public string Type { get; init; } = "checkoutRequest";
    public string Operation { get; init; } = "checkout";
}
public sealed class CheckoutException(int statusCode, string message, List<CheckoutFailure>? failures = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public List<CheckoutFailure> Failures { get; } = failures ?? [];
}
