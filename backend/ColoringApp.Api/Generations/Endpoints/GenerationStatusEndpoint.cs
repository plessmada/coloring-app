using System.Text.Json.Serialization;

namespace ColoringApp.Api.Generations.Endpoints;

internal static class GenerationStatusEndpoint
{
    public static async Task<IResult> StatusAsync(Guid id, HttpContext context, GuestGenerationReader reader)
    {
        var generation = await reader.FindOwnedAsync(id, context.Request, context.RequestAborted);
        if (generation is null)
        {
            return GenerationEndpoints.Unavailable();
        }

        var imageUrl = generation.ImageKey is not null ? $"{GenerationEndpoints.StatusUrl(id)}/image" : null;
        return Results.Ok(new StatusResponse(generation.Id, generation.Status.ToString(), imageUrl, generation.ErrorCode));
    }

    private sealed record StatusResponse(Guid Id, string Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode);
}
