namespace ColoringApp.Api.Generations.Endpoints;

internal static class GenerationEndpoints
{
    private const string BasePath = "/api/generations";

    public static void MapGenerationEndpoints(this WebApplication app)
    {
        var endpoints = app.MapGroup(BasePath).AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        endpoints.MapPost("", GenerationSubmissionEndpoint.SubmitAsync);
        endpoints.MapGet("/{id:guid}", GenerationStatusEndpoint.StatusAsync);
        endpoints.MapGet("/{id:guid}/image", GenerationImageEndpoint.ImageAsync);
    }

    public static string StatusUrl(Guid id) => $"{BasePath}/{id}";

    public static IResult Unavailable() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "The generation is unavailable.",
        extensions: new Dictionary<string, object?> { ["code"] = "generation_unavailable" });
}
