using System.Runtime.InteropServices;
using ColoringApp.Api.Integrations;
using ColoringApp.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ColoringApp.BrowserTests.Host;

public static class BrowserTestHost
{
    public static async Task Main()
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; shutdown.Cancel(); };
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
        await using var database = new PostgresSandbox();
        using var startupDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        startupDeadline.CancelAfter(TimeSpan.FromMinutes(3));
        await database.StartAsync(startupDeadline.Token);
        if (await database.RunFlywayAsync("migrate") != 0)
        {
            throw new InvalidOperationException("Browser-test database migration failed.");
        }

        using var files = new GenerationTestEnvironment();
        var provider = new ControlledGenerationProvider();
        var api = StartApi(database.ConnectionString, files.StorageRoot, provider);
        // Only this executable exposes controls, on a separate loopback-only listener.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:5215");
        await using var controls = builder.Build();
        controls.MapPost("/scenario/{scenario}", (string scenario) => provider.Select(scenario));
        controls.MapGet("/jobs/{id:guid}", async (Guid id) => new
        {
            Status = await database.ScalarAsync($"SELECT status FROM coloring_app.generations WHERE id = '{id}'"),
            Calls = provider.Calls(id),
            AudioExists = File.Exists(Path.Combine(files.StorageRoot, "recordings", $"{id:N}.webm")),
            ImageExists = await database.ScalarAsync($"SELECT image_key FROM coloring_app.generations WHERE id = '{id}'") is Guid key &&
                File.Exists(Path.Combine(files.StorageRoot, "approved", $"{key:N}.png")),
            StagedCount = files.CountFiles("staged"),
            ApprovedCount = files.CountFiles("approved")
        });
        controls.MapPost("/expire-lease/{id:guid}", async (Guid id) =>
        {
            await database.ScalarAsync($"UPDATE coloring_app.generations SET lease_until = clock_timestamp() - interval '1 second' WHERE id = '{id}'");
        });
        controls.MapPost("/restart", async () =>
        {
            await api.DisposeAsync();
            api = StartApi(database.ConnectionString, files.StorageRoot, provider);
        });

        try
        {
            await controls.StartAsync(startupDeadline.Token);
            Console.WriteLine("Browser-test API is ready at http://localhost:5214");
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Disposal stops Kestrel and removes only this host's disposable database and files.
        }
        finally
        {
            await api.DisposeAsync();
        }
    }

    private static BrowserApiFactory StartApi(string connectionString, string storageRoot, IGenerationProvider provider)
    {
        var api = new BrowserApiFactory(connectionString, storageRoot, provider);
        api.UseKestrel(5214);
        using var client = api.CreateClient();
        return api;
    }

    private sealed class BrowserApiFactory(string connectionString, string storageRoot, IGenerationProvider provider)
        : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("GenerationStorage:Root", storageRoot);
            builder.UseSetting("Frontend:Origin", "http://localhost:4201");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(provider);
                services.AddSingleton(GenerationTestEnvironment.Polling);
            });
        }
    }
}
