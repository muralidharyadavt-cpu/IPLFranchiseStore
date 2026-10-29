using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api;

public sealed record PageResult<T>(List<T> Items, string? NextContinuationToken, int PageSize, long? TotalCount);
public sealed class QueryException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
public sealed record PageOptions(int PageSize, bool IncludeTotalCount)
{
    public static PageOptions Parse(string? pageSize, string? includeTotalCount)
    {
        var size = 10;
        if (pageSize is not null && (!int.TryParse(pageSize, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > 50))
            throw new QueryException(400, "pageSize must be an integer between 1 and 50 (default 10).");
        var count = false;
        if (includeTotalCount is not null && !bool.TryParse(includeTotalCount, out count))
            throw new QueryException(400, "includeTotalCount must be true or false.");
        return new(size, count);
    }
}

public sealed class CosmosPaging(CosmosStore store, CosmosSettings settings, IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("IPLFranchiseStore.CosmosPaging.v1");
    private sealed record Cursor(int Version, string Fingerprint, string SdkToken);

    public static string? TextFilter(string? value, string name, int maximum = 128)
    {
        if (value is null) return null;
        value = value.Trim();
        if (value.Length == 0 || value.Length > maximum) throw new QueryException(400, $"{name} must contain 1–{maximum} characters.");
        return value;
    }

    public async Task<PageResult<T>> ReadAsync<T>(string scope, string where, List<(string Name, object Value)> parameters,
        string orderBy, PageOptions options, string? token, CancellationToken ct)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            scope, settings.Endpoint, settings.DatabaseName, settings.ContainerName, CosmosStore.StoreId,
            sdk = typeof(CosmosClient).Assembly.GetName().Version?.ToString(), where, orderBy,
            options.PageSize, options.IncludeTotalCount, parameters = parameters.Select(p => new { p.Name, p.Value })
        }))));
        string? rawToken = null;
        if (token is not null)
        {
            try
            {
                if (token.Length is 0 or > 16384) throw new FormatException();
                var cursor = JsonSerializer.Deserialize<Cursor>(protector.Unprotect(token));
                if (cursor is null || cursor.Version != 1 || cursor.Fingerprint != fingerprint || string.IsNullOrWhiteSpace(cursor.SdkToken))
                    throw new FormatException();
                rawToken = cursor.SdkToken;
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
            { throw new QueryException(400, "Invalid or mismatched continuationToken. Restart this query from its first page."); }
        }
        QueryDefinition Query(string selection, bool sorted)
        {
            var query = new QueryDefinition($"SELECT {selection} FROM c WHERE {where}" + (sorted ? " ORDER BY " + orderBy : ""));
            foreach (var (name, value) in parameters) query.WithParameter(name, value);
            return query;
        }
        try
        {
            using var iterator = store.Container.GetItemQueryIterator<T>(Query("*", true), rawToken,
                new QueryRequestOptions { PartitionKey = new PartitionKey(CosmosStore.StoreId), MaxItemCount = options.PageSize,
                    ResponseContinuationTokenLimitInKb = 2 });
            // A Cosmos service page may be shorter than requested or empty. Its token is authoritative.
            var page = await iterator.ReadNextAsync(ct);
            var next = string.IsNullOrEmpty(page.ContinuationToken) ? null
                : protector.Protect(JsonSerializer.Serialize(new Cursor(1, fingerprint, page.ContinuationToken)));
            long? totalCount = null;
            if (options.IncludeTotalCount)
            {
                using var counts = store.Container.GetItemQueryIterator<long>(Query("VALUE COUNT(1)", false),
                    requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(CosmosStore.StoreId) });
                long total = 0;
                while (counts.HasMoreResults) total += (await counts.ReadNextAsync(ct)).Sum();
                totalCount = total;
            }
            return new(page.ToList(), next, options.PageSize, totalCount);
        }
        catch (CosmosException ex) when (rawToken is not null && ex.StatusCode == HttpStatusCode.BadRequest)
        { throw new QueryException(400, "ContinuationToken is no longer valid. Restart this query from its first page."); }
    }
}
