using ColoringApp.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ColoringApp.Api.Health;

public sealed class DatabaseReadiness(ApplicationDbContext database, ILogger<DatabaseReadiness> logger)
{
    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var connection = database.Database.GetDbConnection();
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 3;
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = 'coloring_app')";
            return await command.ExecuteScalarAsync(deadline.Token) is true ? null : "database_not_initialized";
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            // Provider exceptions can include infrastructure details; log only the safe outcome.
            logger.LogWarning("Database readiness check failed: database_unavailable");
            return "database_unavailable";
        }
    }
}
