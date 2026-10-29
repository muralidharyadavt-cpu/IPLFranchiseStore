using System.Collections.ObjectModel;
using Microsoft.Azure.Cosmos;

namespace IPLFranchiseStore.Api;

public static class StoreIndexes
{
    public static Collection<CompositePath>[] Required() =>
    [
        new() { new() { Path = "/name", Order = CompositePathSortOrder.Ascending }, new() { Path = "/id", Order = CompositePathSortOrder.Ascending } },
        new() { new() { Path = "/createdAt", Order = CompositePathSortOrder.Descending }, new() { Path = "/id", Order = CompositePathSortOrder.Descending } }
    ];

    public static void AddMissing(IndexingPolicy policy)
    {
        foreach (var required in Required())
            if (!policy.CompositeIndexes.Any(existing => existing.Select(p => (p.Path, p.Order))
                    .SequenceEqual(required.Select(p => (p.Path, p.Order)))))
                policy.CompositeIndexes.Add(required);
    }

    public static async Task EnsureAsync(Container container, CancellationToken ct)
    {
        var response = await container.ReadContainerAsync(cancellationToken: ct);
        var properties = response.Resource;
        var previousCount = properties.IndexingPolicy.CompositeIndexes.Count;
        AddMissing(properties.IndexingPolicy);
        if (previousCount == properties.IndexingPolicy.CompositeIndexes.Count) return;
        // Metadata-only migration: preserve all existing index paths and documents.
        await container.ReplaceContainerAsync(properties,
            new ContainerRequestOptions { IfMatchEtag = properties.ETag }, ct);
        while (true)
        {
            var current = await container.ReadContainerAsync(new ContainerRequestOptions { PopulateQuotaInfo = true }, ct);
            var progress = current.Headers["x-ms-documentdb-collection-index-transformation-progress"];
            if (!int.TryParse(progress, out var percent) || percent >= 100 || percent < 0) return;
            await Task.Delay(250, ct);
        }
    }
}
