using System.Runtime.InteropServices;
using ColoringApp.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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

        await using var app = new BrowserApiFactory(database.ConnectionString);
        app.UseKestrel(5214);
        using var client = app.CreateClient();
        Console.WriteLine("Browser-test API is ready at http://localhost:5214");
        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Disposal stops Kestrel and removes only this host's disposable database resources.
        }
    }

    private sealed class BrowserApiFactory(string connectionString) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        }
    }
}
