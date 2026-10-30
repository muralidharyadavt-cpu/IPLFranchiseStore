using IPLFranchiseStore.Api;
using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(services => services.GetRequiredService<IConfiguration>()
    .GetSection("Cosmos").Get<CosmosSettings>() ?? new());
builder.Services.AddSingleton<CosmosClient>(services => CosmosStore.CreateClient(services.GetRequiredService<CosmosSettings>()));
builder.Services.AddSingleton<CosmosStore>();
builder.Services.AddSingleton<InventoryService>();
builder.Services.AddSingleton<CartService>();
builder.Services.AddSingleton<CheckoutService>();
builder.Services.AddDataProtection().SetApplicationName("IPLFranchiseStore");
builder.Services.AddSingleton<CosmosPaging>();
builder.Services.AddSingleton<CatalogueService>();
builder.Services.AddSingleton<OrderHistoryService>();
builder.Services.AddProblemDetails();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
var app = builder.Build();
app.UseStatusCodePages();
var settings = app.Services.GetRequiredService<CosmosSettings>();

// Missing credentials or an unavailable emulator must not prevent liveness/failure reporting.
if (!string.IsNullOrWhiteSpace(settings.Key))
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.Services.GetRequiredService<CosmosStore>().InitializeAsync(timeout.Token);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Cosmos operation {Operation} failed. ExceptionType {ExceptionType}. Check emulator availability, certificate trust and local configuration.",
            "initialize", ex.GetType().FullName);
    }
}

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/v1/health/dependencies", async (IServiceProvider services, HttpContext context, CancellationToken cancellationToken) =>
{
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await services.GetRequiredService<CosmosStore>().Container.ReadContainerAsync(cancellationToken: timeout.Token);
        return Results.Ok(new { status = "healthy", cosmos = "available" });
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Cosmos operation {Operation} failed. RequestId {RequestId}; TraceId {TraceId}; ExceptionType {ExceptionType}",
            "health.dependencies", context.TraceIdentifier, System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier, ex.GetType().FullName);
        return Results.Json(new { status = "unhealthy", cosmos = "unavailable" }, statusCode: 503);
    }
});
app.MapQueries();

app.MapDevelopmentAdmin();
app.MapDemoCarts();
app.MapDemoOrders();
app.Run();

public partial class Program;
