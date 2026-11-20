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
        Assert.Equal(1L, await fixture.Database.ScalarAsync("SELECT count(*) FROM public.flyway_schema_history WHERE version = '2' AND success"));
        Assert.Equal(1L, await fixture.Database.ScalarAsync("SELECT count(*) FROM public.flyway_schema_history WHERE version = '3' AND success"));
        Assert.Equal(2L, await fixture.Database.ScalarAsync("""
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'coloring_app' AND table_name IN ('guest_sessions', 'generations')
            """));
    }

    [Fact]
    public async Task Upgrade_through_v2_and_v3_preserves_existing_data_checksums_and_queued_recording()
    {
        var stagedMigrations = Directory.CreateTempSubdirectory("coloring-upgrade-");
        try
        {
            CopyMigration("V1__create_application_schema.sql", stagedMigrations);
            Assert.Equal(0, await fixture.Database.RunFlywayAsync("migrate", stagedMigrations.FullName, "upgrade_test"));
            var connection = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString) { Database = "upgrade_test" }.ConnectionString;
            const string v1Checksum = "SELECT checksum FROM public.flyway_schema_history WHERE version = '1' AND success";
            const string v2Checksum = "SELECT checksum FROM public.flyway_schema_history WHERE version = '2' AND success";
            var originalV1 = await fixture.Database.ScalarAsync(v1Checksum, connection);
            Assert.NotNull(originalV1);
            await fixture.Database.ScalarAsync("CREATE TABLE public.existing_data (value int); INSERT INTO public.existing_data VALUES (42)", connection);

            CopyMigration("V2__create_guest_generations.sql", stagedMigrations);
            Assert.Equal(0, await fixture.Database.RunFlywayAsync("migrate", stagedMigrations.FullName, "upgrade_test"));
            var originalV2 = await fixture.Database.ScalarAsync(v2Checksum, connection);
            Assert.NotNull(originalV2);
            var session = Guid.NewGuid();
            var job = Guid.NewGuid();
            await fixture.Database.ScalarAsync($"""
                INSERT INTO coloring_app.guest_sessions (id, created_at, expires_at)
                VALUES ('{session}', statement_timestamp(), statement_timestamp() + interval '24 hours');
                INSERT INTO coloring_app.generations (id, guest_session_id, status, created_at, audio_key, audio_is_silent)
                VALUES ('{job}', '{session}', 'Pending', statement_timestamp(), '{job}', true);
                """, connection);

            Assert.Equal(0, await fixture.Database.RunFlywayAsync("migrate", database: "upgrade_test"));
            Assert.Equal(0, await fixture.Database.RunFlywayAsync("validate", database: "upgrade_test"));
            Assert.Equal(originalV1, await fixture.Database.ScalarAsync(v1Checksum, connection));
            Assert.Equal(originalV2, await fixture.Database.ScalarAsync(v2Checksum, connection));
            Assert.Equal(42, await fixture.Database.ScalarAsync("SELECT value FROM public.existing_data", connection));
            Assert.Equal(job, await fixture.Database.ScalarAsync(
                $"SELECT audio_key FROM coloring_app.generations WHERE id = '{job}' AND status = 'Pending'", connection));
            Assert.Equal(0L, await fixture.Database.ScalarAsync("""
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = 'coloring_app' AND table_name = 'generations' AND column_name = 'audio_is_silent'
                """, connection));
        }
        finally
        {
            stagedMigrations.Delete(recursive: true);
        }
    }

    private static void CopyMigration(string name, DirectoryInfo destination)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "database", "migrations", name);
        File.Copy(source, Path.Combine(destination.FullName, name));
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
