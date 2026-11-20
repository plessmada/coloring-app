using ColoringApp.Api.Integrations;

namespace ColoringApp.Api.Generations.Endpoints;

internal static class GenerationImageEndpoint
{
    public static async Task<IResult> ImageAsync(Guid id, HttpContext context, GuestGenerationReader reader,
        PrivateGenerationFiles files)
    {
        var generation = await reader.FindOwnedAsync(id, context.Request, context.RequestAborted);
        if (generation is not { Status: GenerationStatus.Succeeded, ImageKey: { } key } || files.OpenImage(key) is not { } image)
        {
            return GenerationEndpoints.Unavailable();
        }

        var download = string.Equals(context.Request.Query["download"], "true", StringComparison.OrdinalIgnoreCase);
        return Results.File(image, "image/png", download ? $"kolorowanka-{id:N}.png" : null);
    }
}
