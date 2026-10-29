using Microsoft.AspNetCore.Mvc;

namespace IPLFranchiseStore.Api;

public static class QueryEndpoints
{
    private static string? Parameter(HttpRequest request, string name) => request.Query.TryGetValue(name, out var value) ? value.ToString() : null;

    public static void MapQueries(this WebApplication app)
    {
        app.MapGet("/api/v1/products", (HttpRequest request, HttpResponse response, IServiceProvider services, CancellationToken ct) => Execute(async () =>
        {
            var supported = new HashSet<string>(["paged", "search", "type", "merchandiseType", "franchise", "pageSize", "continuationToken", "includeTotalCount"], StringComparer.OrdinalIgnoreCase);
            foreach (var query in request.Query)
            {
                if (!supported.Contains(query.Key)) throw new QueryException(400, $"Unsupported catalogue parameter '{query.Key}'. Use search, type/merchandiseType, franchise or paging parameters.");
                if (query.Value.Count != 1) throw new QueryException(400, $"Catalogue parameter '{query.Key}' must appear exactly once.");
            }
            var paged = Parameter(request, "paged");
            var usesPaging = new[] { "search", "type", "merchandiseType", "franchise", "pageSize", "continuationToken", "includeTotalCount" }.Any(request.Query.ContainsKey);
            if (paged is not null && !bool.TryParse(paged, out _)) throw new QueryException(400, "paged must be true or false.");
            if (paged is not null && !bool.Parse(paged) && usesPaging) throw new QueryException(400, "Catalogue parameters require paged mode.");
            if (!usesPaging && (paged is null || !bool.Parse(paged)))
            {
                try
                {
                    var page = await services.GetRequiredService<CatalogueService>().SearchAsync(null, null, null, "50", null, null, ct);
                    response.Headers["Deprecation"] = "true";
                    response.Headers["X-Page-Size"] = "50";
                    response.Headers["Link"] = "</api/v1/products?paged=true&pageSize=50>; rel=\"alternate\"";
                    if (page.NextContinuationToken is not null)
                        response.Headers.Append("Link", $"</api/v1/products?paged=true&pageSize=50&continuationToken={Uri.EscapeDataString(page.NextContinuationToken)}>; rel=\"next\"");
                    return Results.Ok(page.Items);
                }
                catch (Exception) { return Results.Json(new { error = "Product store unavailable." }, statusCode: 503); }
            }
            return Results.Ok(await services.GetRequiredService<CatalogueService>().SearchAsync(
                Parameter(request, "search"), Parameter(request, "type"), Parameter(request, "franchise"),
                Parameter(request, "pageSize"), Parameter(request, "continuationToken"), Parameter(request, "includeTotalCount"), ct, Parameter(request, "merchandiseType")));
        }));

        app.MapGet("/api/v1/products/{id}", (string id, IServiceProvider services, CancellationToken ct) => Execute(async () =>
            Results.Ok(await services.GetRequiredService<CatalogueService>().GetAsync(id, services.GetRequiredService<CosmosStore>(), ct))));

        if (app.Environment.IsDevelopment())
            app.MapGet("/api/v1/orders", (HttpRequest request, [FromHeader(Name = "X-Demo-User-Id")] string? userId,
                IServiceProvider services, CancellationToken ct) => Execute(async () => Results.Ok(await services.GetRequiredService<OrderHistoryService>().GetAsync(userId ?? "",
                    Parameter(request, "dateFrom"), Parameter(request, "dateTo"), Parameter(request, "pageSize"),
                    Parameter(request, "continuationToken"), Parameter(request, "includeTotalCount"), ct))));
    }

    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (QueryException ex) { return Results.Problem(statusCode: ex.StatusCode, title: ex.Message); }
        catch (Exception) { return Results.Problem(statusCode: 503, title: "Query store unavailable. Check database availability and index provisioning."); }
    }
}
