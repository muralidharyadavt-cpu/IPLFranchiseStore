using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using IPLFranchiseStore.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IPLFranchiseStore.Api.Tests;

public sealed class EndpointDiagnosticsTests(CartTestDatabase db) : IClassFixture<CartTestDatabase>
{
    [Theory]
    [InlineData("/api/v1/products", "catalogue.legacy")]
    [InlineData("/api/v1/products?paged=true", "catalogue.list")]
    [InlineData("/api/v1/products/csk-cap", "catalogue.details")]
    [InlineData("/api/v1/admin/products/csk-cap/audits", "inventory.audits")]
    [InlineData("/api/v1/admin/products/csk-cap/stock-adjustments", "inventory.stock-adjustment")]
    [InlineData("/api/v1/admin/products/csk-cap/price-updates", "inventory.price-update")]
    [InlineData("/api/v1/demo-users/diagnostics/cart", "cart.read")]
    [InlineData("/api/v1/demo-users/diagnostics/cart/lines", "cart.add")]
    [InlineData("/api/v1/orders/checkout", "orders.checkout")]
    [InlineData("/api/v1/orders/11111111-1111-4111-8111-111111111111", "orders.details")]
    [InlineData("/api/v1/orders", "orders.history")]
    public async Task UnexpectedFailures_AreSanitizedAndLogOnlySafeStructuredContext(string path, string operation)
    {
        // An invalid container lookup throws a real SDK argument exception inside each handler.
        // No network call, programming-error concealment or fake HTTP failure is required.
        var settings = new CosmosSettings { Endpoint = "https://localhost:8081", Key = Convert.ToBase64String(new byte[64]), DatabaseName = "" };
        using var client = CosmosStore.CreateClient(settings);
        var invalidStore = new CosmosStore(client, settings);
        var failure = Record.Exception(() => _ = invalidStore.Container);
        Assert.IsAssignableFrom<ArgumentException>(failure);
        var capture = new CaptureLogs();
        await using var app = App(invalidStore, capture);
        using var http = app.CreateClient();
        const string sentinel = "DO-NOT-LOG-request-body-or-auth-token";
        var isPost = path.EndsWith("adjustments") || path.EndsWith("updates") || path.EndsWith("lines") || path.EndsWith("checkout");
        using var request = new HttpRequestMessage(isPost ? HttpMethod.Post : HttpMethod.Get, path);
        request.Headers.Add("X-Demo-User-Id", "diagnostics");
        request.Headers.Add("Authorization", "Bearer " + sentinel);
        if (isPost) request.Content = JsonContent.Create(new
        {
            quantity = 1, price = 100m, expectedETag = "\"expected\"", actor = sentinel, reason = sentinel,
            requestId = Guid.NewGuid().ToString("D"), expectedCartVersion = "empty", productId = "csk-cap", orderId = Guid.NewGuid().ToString("D")
        });
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(sentinel, body);
        Assert.DoesNotContain(settings.Key, body);
        Assert.DoesNotContain(failure!.GetType().Name, body);
        var entry = Assert.Single(capture.Entries);
        Assert.Equal(operation, entry.Values["Operation"]);
        Assert.Equal(failure.GetType().FullName, entry.Values["ExceptionType"]);
        Assert.False(string.IsNullOrEmpty(entry.Values["RequestId"]?.ToString()));
        Assert.False(string.IsNullOrEmpty(entry.Values["TraceId"]?.ToString()));
        Assert.DoesNotContain(sentinel, entry.Message);
        Assert.DoesNotContain(settings.Key, entry.Message);
        Assert.Null(entry.Exception); // Exception message/SDK diagnostics are deliberately not logged.
        if (operation == "catalogue.legacy")
        {
            var legacy = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            Assert.Equal("Product store unavailable.", legacy!["error"]);
            Assert.Single(legacy);
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        }
        else Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(412)]
    public async Task ExpectedBusinessErrorsKeepTheirStatus_WithoutUnexpectedFailureLogs(int status)
    {
        var product = (await db.Store.ReadProductAsync("csk-cap", CancellationToken.None))!;
        var capture = new CaptureLogs();
        await using var app = App(db.Store, capture);
        using var http = app.CreateClient();
        var id = status == 404 ? "missing-" + Guid.NewGuid().ToString("N") : product.Id;
        using var response = await http.PostAsJsonAsync($"/api/v1/admin/products/{id}/stock-adjustments",
            new StockAdjustment(status == 400 ? 0 : status == 409 ? -101 : 1,
                status == 412 ? "\"stale\"" : product.ETag, "isolated-admin", "Verify business status"));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Empty(capture.Entries);
        Assert.Equal(product, await db.Store.ReadProductAsync(product.Id, CancellationToken.None));
    }

    private static WebApplicationFactory<Program> App(CosmosStore store, CaptureLogs capture) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Cosmos:Key"] = "" }));
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<CosmosStore>(); services.AddSingleton(store);
                services.AddLogging(logging => logging.AddProvider(capture));
            });
        });

    private sealed record Entry(Dictionary<string, object?> Values, string Message, Exception? Exception);
    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void Dispose() { }
        private sealed class CaptureLogger(CaptureLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != 1001) return;
                var values = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(p => p.Key, p => p.Value);
                owner.Entries.Enqueue(new Entry(values, formatter(state, exception), exception));
            }
        }
    }
}
