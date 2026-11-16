using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ColoringApp.Testing;

// Shared by integration tests and the browser-test host; never reads developer credentials.
public sealed class PostgresSandbox : IAsyncDisposable
{
    public const string PostgresImage = "postgres:18.7";
    public const string FlywayImage = "redgate/flyway:13.12.2";
    private readonly INetwork network = new NetworkBuilder().Build();
    private readonly string password = Guid.NewGuid().ToString("N");
    private PostgreSqlContainer? postgres;
    public string ConnectionString => postgres!.GetConnectionString();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await network.CreateAsync(cancellationToken);
        postgres = new PostgreSqlBuilder(PostgresImage)
            .WithDatabase("foundation_tests")
            .WithUsername("foundation_tests")
            .WithPassword(password)
            .WithNetwork(network)
            .WithNetworkAliases("postgres")
            .Build();
        await postgres.StartAsync(cancellationToken);
    }

    public async Task<long> RunFlywayAsync(string operation, string? migrations = null, string database = "foundation_tests")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var runner = new ContainerBuilder(FlywayImage)
            .WithNetwork(network)
            .WithEnvironment("FLYWAY_URL", $"jdbc:postgresql://postgres:5432/{database}")
            .WithEnvironment("FLYWAY_USER", "foundation_tests")
            .WithEnvironment("FLYWAY_PASSWORD", password)
            .WithResourceMapping(FilePath.Of(Path.Combine(AppContext.BaseDirectory, "database", "flyway.conf")), FilePath.Of("/flyway/conf/flyway.conf"))
            .WithResourceMapping(DirectoryPath.Of(migrations ?? Path.Combine(AppContext.BaseDirectory, "database", "migrations")), DirectoryPath.Of("/flyway/sql/"))
            .WithCommand(operation)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Flyway", options => options.WithMode(WaitStrategyMode.OneShot)))
            .Build();
        await runner.StartAsync(deadline.Token);
        return await runner.GetExitCodeAsync(deadline.Token);
    }

    public async Task<object?> ScalarAsync(string sql, string? connectionString = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(connectionString ?? ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(deadline.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (postgres is not null)
        {
            await postgres.DisposeAsync();
        }
        await network.DisposeAsync();
    }
}
