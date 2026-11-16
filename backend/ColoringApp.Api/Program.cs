using ColoringApp.Api.Data;
using ColoringApp.Api.Health;
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

builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(databaseSettings.ConnectionString));
builder.Services.AddScoped<DatabaseReadiness>();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();
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
app.Run();

public partial class Program;
