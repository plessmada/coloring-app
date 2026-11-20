using ColoringApp.Testing;
using Xunit;

namespace ColoringApp.Api.Tests;

public sealed class DatabaseFixture : IAsyncLifetime
{
    public PostgresSandbox Database { get; } = new();

    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await Database.StartAsync(deadline.Token);
        Assert.Equal(0, await Database.RunFlywayAsync("migrate"));
        await Database.ScalarAsync("CREATE DATABASE unmigrated");
        await Database.ScalarAsync("CREATE DATABASE checksum_test");
        await Database.ScalarAsync("CREATE DATABASE upgrade_test");
    }

    public Task DisposeAsync() => Database.DisposeAsync().AsTask();
}

[CollectionDefinition("Foundation database")]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
