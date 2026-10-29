using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api;

public static class AdminEndpoints
{
    public static void MapDevelopmentAdmin(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        var group = app.MapGroup("/api/v1/admin/products");
        group.MapPost("/{id}/stock-adjustments", (string id, StockAdjustment request, InventoryService service,
            HttpContext context, CancellationToken ct) => Execute(async () =>
            {
                var product = await service.AdjustStockAsync(id, request, context.TraceIdentifier, ct);
                context.Response.Headers.ETag = product.ETag;
                context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
                return Results.Ok(product);
            }));
        group.MapPost("/{id}/price-updates", (string id, PriceUpdate request, InventoryService service,
            HttpContext context, CancellationToken ct) => Execute(async () =>
            {
                var product = await service.UpdatePriceAsync(id, request, context.TraceIdentifier, ct);
                context.Response.Headers.ETag = product.ETag;
                context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
                return Results.Ok(product);
            }));
        group.MapGet("/{id}/audits", (string id, InventoryService service, CancellationToken ct) =>
            Execute(async () => Results.Ok(await service.GetAuditsAsync(id, ct))));
    }

    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (InventoryException ex) { return Results.Problem(statusCode: ex.StatusCode, title: ex.Message); }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { return Results.Problem(statusCode: 404, title: "Product not found."); }
        catch (Exception)
        { return Results.Problem(statusCode: 503, title: "Inventory store unavailable; verify the result before resubmitting."); }
    }
}
