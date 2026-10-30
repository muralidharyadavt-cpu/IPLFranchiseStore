
namespace IPLFranchiseStore.Api;

public static class AdminEndpoints
{
    public static void MapDevelopmentAdmin(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        var group = app.MapGroup("/api/v1/admin/products");
        group.MapPost("/{id}/stock-adjustments", (string id, StockAdjustment request, InventoryService service,
            HttpContext context, CancellationToken ct) => Execute(context, app.Logger, "inventory.stock-adjustment", async () =>
            {
                var product = await service.AdjustStockAsync(id, request, context.TraceIdentifier, ct);
                context.Response.Headers.ETag = product.ETag;
                context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
                return Results.Ok(product);
            }));
        group.MapPost("/{id}/price-updates", (string id, PriceUpdate request, InventoryService service,
            HttpContext context, CancellationToken ct) => Execute(context, app.Logger, "inventory.price-update", async () =>
            {
                var product = await service.UpdatePriceAsync(id, request, context.TraceIdentifier, ct);
                context.Response.Headers.ETag = product.ETag;
                context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
                return Results.Ok(product);
            }));
        group.MapGet("/{id}/audits", (string id, InventoryService service, HttpContext context, CancellationToken ct) =>
            Execute(context, app.Logger, "inventory.audits", async () => Results.Ok(await service.GetAuditsAsync(id, ct))));
    }

    private static async Task<IResult> Execute(HttpContext context, ILogger logger, string operation, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (InventoryException ex) { return Results.Problem(statusCode: ex.StatusCode, title: ex.Message); }
        catch (Exception ex)
        {
            logger.LogError(new EventId(1001, "UnexpectedApiFailure"),
                "API operation {Operation} failed. RequestId {RequestId}; TraceId {TraceId}; ExceptionType {ExceptionType}",
                operation, context.TraceIdentifier, System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier, ex.GetType().FullName);
            return Results.Problem(statusCode: 503, title: "Inventory store unavailable; verify the result before resubmitting.");
        }
    }
}
