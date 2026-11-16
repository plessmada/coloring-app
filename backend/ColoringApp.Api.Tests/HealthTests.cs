using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace ColoringApp.Api.Tests;

[Collection("Foundation database")]
public sealed class HealthTests(DatabaseFixture fixture)
{
    private static WebApplicationFactory<Program> CreateApi(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        });

    [Fact]
    public async Task Ready_returns_the_contract_after_flyway_migration()
    {
        await using var app = CreateApi(fixture.Database.ConnectionString);
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("ColoringApp", json.RootElement.GetProperty("application").GetString());
        Assert.Equal("ok", json.RootElement.GetProperty("database").GetString());
    }

    [Fact]
    public async Task Unmigrated_database_is_not_initialized_and_the_api_does_not_create_it()
    {
        var settings = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString) { Database = "unmigrated" };
        await using var app = CreateApi(settings.ConnectionString);
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/health/ready");
        await AssertSafeFailure(response, "database_not_initialized");
        Assert.Equal(false, await fixture.Database.ScalarAsync("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'coloring_app')", settings.ConnectionString));
    }

    [Fact]
    public async Task Unavailable_database_leaves_liveness_healthy_and_readiness_safe()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var app = CreateApi($"Host=127.0.0.1;Port={port};Database=private_database;Username=private_user;Password=private_password;Pooling=false");
        using var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);

        using var live = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("no-store", live.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await live.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("ColoringApp", json.RootElement.GetProperty("application").GetString());

        var stopwatch = Stopwatch.StartNew();
        using var ready = await client.GetAsync("/api/health/ready");
        await AssertSafeFailure(ready, "database_unavailable");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Stalled_database_handshake_is_bounded()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var app = CreateApi($"Host=127.0.0.1;Port={port};Username=private_user;Password=private_password;SSL Mode=Disable;Pooling=false");
        using var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync("/api/health/ready");
        await AssertSafeFailure(response, "database_unavailable");
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 2, 6);
    }

    [Fact]
    public void Missing_configuration_fails_startup_without_a_secret()
    {
        using var app = CreateApi("");
        var exception = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Equal("Configure ConnectionStrings:DefaultConnection before starting the API.", exception.Message);
    }

    [Theory]
    [InlineData("Password=private_password")]
    [InlineData("NotASetting=private_password")]
    public void Invalid_configuration_fails_with_a_sanitized_diagnostic(string connectionString)
    {
        using var app = CreateApi(connectionString);
        var exception = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Equal("ConnectionStrings:DefaultConnection must be a valid PostgreSQL connection string.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    private static async Task AssertSafeFailure(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal("Application is not ready.", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(503, json.RootElement.GetProperty("status").GetInt32());
        foreach (var sensitive in new[] { "private_", "127.0.0.1", "Host=", "Password", "Npgsql", "SELECT", "stack", "Exception" })
        {
            Assert.DoesNotContain(sensitive, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
