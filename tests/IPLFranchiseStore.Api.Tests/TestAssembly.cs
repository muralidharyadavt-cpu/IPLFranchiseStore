using Xunit;

// Serialize emulator provisioning/teardown and host-environment tests. Mutation race tests
// still issue concurrent HTTP requests explicitly with Task.WhenAll.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
