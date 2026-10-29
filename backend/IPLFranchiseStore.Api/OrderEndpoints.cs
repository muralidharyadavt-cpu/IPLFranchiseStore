using Microsoft.AspNetCore.Mvc;

namespace IPLFranchiseStore.Api;

public static class OrderEndpoints
{
    public static void MapDemoOrders(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        app.MapPost("/api/v1/orders/checkout", ([FromHeader(Name = "X-Demo-User-Id")] string? userId,
            CheckoutCommand command, CheckoutService service, HttpContext context, CancellationToken ct) => Execute(async () =>
            {
                var outcome = await service.CheckoutAsync(userId ?? "", command, context.TraceIdentifier, ct);
                return outcome.StatusCode == 201 ? Results.Created($"/api/v1/orders/{outcome.Order!.OrderId}", outcome.Order)
                    : Problem(outcome.StatusCode, outcome.Error!, outcome.Failures);
            }));
        app.MapGet("/api/v1/orders/{orderId}", (string orderId,
            [FromHeader(Name = "X-Demo-User-Id")] string? userId, CheckoutService service, CancellationToken ct) =>
            Execute(async () => Results.Ok(await service.GetAsync(userId ?? "", orderId, ct))));
    }

    private static IResult Problem(int status, string title, List<CheckoutFailure>? failures) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["failures"] = failures ?? [] });
    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (CheckoutException ex) { return Problem(ex.StatusCode, ex.Message, ex.Failures); }
        catch (Exception) { return Problem(503, "Order store unavailable. Retry checkout with the identical orderId and payload.", null); }
    }
}
