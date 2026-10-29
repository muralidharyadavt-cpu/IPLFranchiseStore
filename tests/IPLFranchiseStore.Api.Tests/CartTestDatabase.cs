using IPLFranchiseStore.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace IPLFranchiseStore.Api.Tests;

public sealed class CartTestDatabase : IAsyncLifetime
{
    public CosmosStore Store { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public CosmosSettings Settings { get; private set; } = null!;
    private CosmosClient cosmos = null!;
    private WebApplicationFactory<Program> app = null!;

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>().AddEnvironmentVariables().Build();
        Settings = configuration.GetSection("Cosmos").Get<CosmosSettings>() ?? new();
        Assert.False(string.IsNullOrWhiteSpace(Settings.Key), "Configure Cosmos:Key in API User Secrets or Cosmos__Key.");
        Assert.Equal("localhost", new Uri(Settings.Endpoint).Host);
        Settings.DatabaseName = "IPLFranchiseStoreTests-" + Guid.NewGuid().ToString("N");
        cosmos = CosmosStore.CreateClient(Settings);
        Store = new CosmosStore(cosmos, Settings);
        Start();
        await Store.Container.ReadContainerAsync();
    }

    private void Start()
    {
        app = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:Endpoint"] = Settings.Endpoint, ["Cosmos:Key"] = Settings.Key,
                ["Cosmos:DatabaseName"] = Settings.DatabaseName, ["Cosmos:ContainerName"] = "StoreData"
            }));
        });
        Http = app.CreateClient();
    }

    public async Task RestartAsync()
    {
        Http.Dispose();
        await app.DisposeAsync();
        Start();
    }

    public async Task DisposeAsync()
    {
        Http?.Dispose();
        if (app is not null) await app.DisposeAsync();
        if (cosmos is not null)
        {
            await cosmos.GetDatabase(Settings.DatabaseName).DeleteAsync();
            cosmos.Dispose();
        }
    }
}
