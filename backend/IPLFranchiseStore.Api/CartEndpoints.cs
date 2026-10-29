using Microsoft.AspNetCore.Mvc;

namespace IPLFranchiseStore.Api;

public static class CartEndpoints
{
    public static void MapDemoCarts(this WebApplication app)
    {
        // Caller-supplied demo identity is not authentication. Do not expose this in Production or Staging.
        if (!app.Environment.IsDevelopment()) return;
        var group = app.MapGroup("/api/v1/demo-users/{userId}/cart");
        group.MapGet("/", (string userId, CartService service, CancellationToken ct) =>
            Execute(async () => Results.Ok(await service.GetAsync(userId, ct))));
        group.MapPost("/lines", (string userId, CartCommand command, CartService service, CancellationToken ct) =>
            ExecuteCommand(userId, "add", command, service, ct));
        group.MapPut("/lines/{productId}", (string userId, string productId, CartCommand command, CartService service, CancellationToken ct) =>
            ExecuteCommand(userId, "set", command with { ProductId = productId }, service, ct));
        group.MapDelete("/lines/{productId}", (string userId, string productId, [FromBody] CartCommand command, CartService service, CancellationToken ct) =>
            ExecuteCommand(userId, "remove", command with { ProductId = productId }, service, ct));
        group.MapPost("/acknowledgements", (string userId, CartCommand command, CartService service, CancellationToken ct) =>
            ExecuteCommand(userId, "acknowledge", command, service, ct));
    }

    private static Task<IResult> ExecuteCommand(string userId, string operation, CartCommand command, CartService service, CancellationToken ct) =>
        Execute(async () =>
        {
            var result = await service.ExecuteAsync(userId, operation, command, ct);
            return result.StatusCode == 200 ? Results.Ok(result.Cart) : Results.Problem(statusCode: result.StatusCode, title: result.Error);
        });

    private static async Task<IResult> Execute(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (CartException ex) { return Results.Problem(statusCode: ex.StatusCode, title: ex.Message); }
        catch (Exception) { return Results.Problem(statusCode: 503, title: "Cart store unavailable. Retry commands with the same requestId."); }
    }
}
