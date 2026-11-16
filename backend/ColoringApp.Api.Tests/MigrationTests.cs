using Npgsql;
using Xunit;

namespace ColoringApp.Api.Tests;

[Collection("Foundation database")]
public sealed class MigrationTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task Initialization_validation_and_rerun_preserve_the_schema_and_history()
    {
        var historyBefore = await fixture.Database.ScalarAsync("SELECT row_to_json(h)::text FROM public.flyway_schema_history h WHERE version = '1' AND success");
        var schemaBefore = await fixture.Database.ScalarAsync("SELECT oid FROM pg_namespace WHERE nspname = 'coloring_app'");
        Assert.NotNull(historyBefore);
        Assert.NotNull(schemaBefore);
        Assert.Equal(0, await fixture.Database.RunFlywayAsync("validate"));
        Assert.Equal(0, await fixture.Database.RunFlywayAsync("migrate"));
        Assert.Equal(1L, await fixture.Database.ScalarAsync("SELECT count(*) FROM public.flyway_schema_history WHERE version = '1'"));
        Assert.Equal(historyBefore, await fixture.Database.ScalarAsync("SELECT row_to_json(h)::text FROM public.flyway_schema_history h WHERE version = '1' AND success"));
        Assert.Equal(schemaBefore, await fixture.Database.ScalarAsync("SELECT oid FROM pg_namespace WHERE nspname = 'coloring_app'"));
        Assert.Equal(0L, await fixture.Database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'coloring_app'"));
    }

    [Fact]
    public async Task Changed_applied_checksum_fails_validation_and_migration_without_repair()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"coloring-migrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var original = Path.Combine(AppContext.BaseDirectory, "database", "migrations", "V1__create_application_schema.sql");
            var copy = Path.Combine(temporaryDirectory, Path.GetFileName(original));
            File.Copy(original, copy);
            Assert.Equal(0, await fixture.Database.RunFlywayAsync("migrate", temporaryDirectory, "checksum_test"));
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString) { Database = "checksum_test" }.ConnectionString;
            const string historyQuery = "SELECT row_to_json(h)::text FROM public.flyway_schema_history h WHERE version = '1' AND success";
            var historyBefore = await fixture.Database.ScalarAsync(historyQuery, connectionString);
            await File.AppendAllTextAsync(copy, "\nCREATE SCHEMA checksum_must_not_apply;\n");
            Assert.NotEqual(0, await fixture.Database.RunFlywayAsync("validate", temporaryDirectory, "checksum_test"));
            Assert.NotEqual(0, await fixture.Database.RunFlywayAsync("migrate", temporaryDirectory, "checksum_test"));
            Assert.Equal(historyBefore, await fixture.Database.ScalarAsync(historyQuery, connectionString));
            Assert.Equal(false, await fixture.Database.ScalarAsync("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'checksum_must_not_apply')", connectionString));
            Assert.Equal("CREATE SCHEMA coloring_app;\n", await File.ReadAllTextAsync(original));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, true);
        }
    }
}
