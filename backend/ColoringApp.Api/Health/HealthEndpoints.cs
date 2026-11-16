namespace ColoringApp.Api.Health;

public sealed record LivenessResponse(string Status, string Application);
public sealed record ReadinessResponse(string Status, string Application, string Database);

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", (HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(new LivenessResponse("ok", "ColoringApp"));
        }).AllowAnonymous();

        app.MapGet("/api/health/ready", async (HttpResponse response, DatabaseReadiness readiness, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var errorCode = await readiness.CheckAsync(cancellationToken);
            return errorCode is null
                ? Results.Ok(new ReadinessResponse("ok", "ColoringApp", "ok"))
                : Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Application is not ready.",
                    extensions: new Dictionary<string, object?> { ["code"] = errorCode });
        }).AllowAnonymous();
    }
}
