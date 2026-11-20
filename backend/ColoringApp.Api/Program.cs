using ColoringApp.Api.Authentication;
using ColoringApp.Api.Data;
using ColoringApp.Api.Generations;
using ColoringApp.Api.Generations.Endpoints;
using ColoringApp.Api.Health;
using ColoringApp.Api.Integrations;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection before starting the API.");
}

NpgsqlConnectionStringBuilder databaseSettings;
try
{
    databaseSettings = new NpgsqlConnectionStringBuilder(connectionString)
    {
        Timeout = 3,
        CommandTimeout = 3,
        CancellationTimeout = 1,
        IncludeErrorDetail = false,
        LogParameters = false
    };
    if (string.IsNullOrWhiteSpace(databaseSettings.Host))
    {
        throw new ArgumentException("A PostgreSQL host is required.");
    }
}
catch (ArgumentException)
{
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be a valid PostgreSQL connection string.");
}

builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseNpgsql(databaseSettings.ConnectionString));
builder.Services.AddScoped<DatabaseReadiness>();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddScoped<GuestAccess>();
builder.Services.AddScoped<GuestGenerationReader>();
builder.Services.AddSingleton<PrivateGenerationFiles>();
builder.Services.AddSingleton(new GenerationPollingIntervals(
    IdleQueueDelay: TimeSpan.FromSeconds(10),
    ExpiredJobCheckInterval: TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<GenerationQueue>();
builder.Services.AddSingleton<GenerationCleanup>();
builder.Services.AddSingleton<GenerationProcessor>();
builder.Services.AddHostedService(services => services.GetRequiredService<GenerationCleanup>());
builder.Services.AddHostedService<GenerationWorker>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto);
builder.Services.AddTransient<OpenAiResponseSizeLimitHandler>();
builder.Services.AddHttpClient<IGenerationProvider, OpenAiGenerationProvider>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    // Avoid reconnect retries on stale pooled connections.
    client.DefaultRequestHeaders.ConnectionClose = true;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler<OpenAiResponseSizeLimitHandler>()
    .RemoveAllLoggers();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHttpsRedirection();
}

app.MapHealthEndpoints();
app.MapGenerationEndpoints();
app.Run();

public partial class Program;
