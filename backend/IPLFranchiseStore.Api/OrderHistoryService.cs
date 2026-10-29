using System.Globalization;
using System.Text.Json;

namespace IPLFranchiseStore.Api;

public sealed class OrderHistoryService(CosmosPaging paging)
{
    public Task<PageResult<OrderDocument>> GetAsync(string userId, string? dateFrom, string? dateTo,
        string? pageSize, string? continuationToken, string? includeTotalCount, CancellationToken ct)
    {
        try { CartService.ValidateUser(userId); }
        catch (CartException ex) { throw new QueryException(400, ex.Message); }
        var from = ParseUtc(dateFrom, "dateFrom");
        var to = ParseUtc(dateTo, "dateTo");
        if (from.HasValue && to.HasValue && from > to) throw new QueryException(400, "dateFrom must not be after dateTo.");
        var options = PageOptions.Parse(pageSize, includeTotalCount);
        var where = "c.type = @type AND c.userId = @userId";
        var parameters = new List<(string, object)> { ("@type", "order"), ("@userId", userId) };
        // Match the existing persisted DateTimeOffset serializer (+00:00, trimmed fractional seconds).
        if (from.HasValue) { where += " AND c.createdAt >= @from"; parameters.Add(("@from", JsonSerializer.SerializeToElement(from.Value).GetString()!)); }
        if (to.HasValue) { where += " AND c.createdAt <= @to"; parameters.Add(("@to", JsonSerializer.SerializeToElement(to.Value).GetString()!)); }
        return paging.ReadAsync<OrderDocument>("order-history-v1", where, parameters, "c.createdAt DESC, c.id DESC", options, continuationToken, ct);
    }

    private static DateTimeOffset? ParseUtc(string? value, string parameter)
    {
        if (value is null) return null;
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        if (!(value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal)) ||
            !DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
            throw new QueryException(400, $"{parameter} must be an ISO 8601 UTC timestamp with Z or +00:00 (up to seven fractional digits).");
        return parsed;
    }
}
