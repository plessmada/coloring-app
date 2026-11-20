using ColoringApp.Api.Authentication;
using ColoringApp.Api.Data;
using ColoringApp.Api.Integrations;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace ColoringApp.Api.Generations.Endpoints;

internal static class GenerationSubmissionEndpoint
{
    private const int RequestLimit = RecordingUpload.AudioLimit + 16 * 1024;

    public static async Task<IResult> SubmitAsync(HttpContext context, IConfiguration configuration,
        ApplicationDbContext database, GuestAccess access, PrivateGenerationFiles files,
        ILogger<Program> logger)
    {
        var request = context.Request;
        var expectedOrigin = configuration["Frontend:Origin"] ?? "http://localhost:4200";
        if (request.Headers.Origin.Count != 1 || !string.Equals(request.Headers.Origin[0], expectedOrigin, StringComparison.Ordinal))
        {
            return Problem(403, "origin_forbidden", "The request origin is not allowed.");
        }

        if (request.ContentLength > RequestLimit)
        {
            return TooLarge();
        }
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = RequestLimit;
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) ||
            !string.Equals(type.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Unsupported();
        }
        var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
        {
            return Invalid();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var cancellationToken = deadline.Token;
        var id = Guid.NewGuid();
        var savedFile = false;
        var submissionAttempted = false;
        try
        {
            var reader = new MultipartReader(boundary, request.Body);
            var section = await RecordingUpload.ReadSectionAsync(reader, cancellationToken);
            if (section is null || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.IsFileDisposition() || HeaderUtilities.RemoveQuotes(disposition.Name).Value != "audio")
            {
                return Invalid();
            }

            var recordingLength = await RecordingUpload.SaveAsync(section.Body, files, id, cancellationToken);
            savedFile = true;
            if (recordingLength > RecordingUpload.AudioLimit)
            {
                return TooLarge();
            }
            if (recordingLength == 0 || await RecordingUpload.ReadSectionAsync(reader, cancellationToken) is not null)
            {
                return Invalid();
            }

            var now = (await database.Database.SqlQueryRaw<DateTime>("""SELECT clock_timestamp() AS "Value" """).ToListAsync(cancellationToken)).Single();
            var (session, token) = await access.GetForSubmissionAsync(request, now, cancellationToken);
            database.Generations.Add(new Generation
            {
                Id = id,
                GuestSessionId = session.Id,
                CreatedAt = now,
                Status = GenerationStatus.Pending,
                AudioKey = id
            });

            // A lost commit response leaves the recording for reference-aware cleanup.
            submissionAttempted = true;
            await database.SaveChangesAsync(cancellationToken);
            if (token is not null)
            {
                GuestAccess.SetCookie(context, token, session.ExpiresAt);
            }
            return Results.Accepted(GenerationEndpoints.StatusUrl(id), new SubmissionResponse(id, "Pending"));
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge();
        }
        catch (InvalidDataException)
        {
            return Invalid();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            OperationCanceledException or NpgsqlException or DbUpdateException)
        {
            logger.LogWarning("Generation {GenerationId} submission failed: generation_failed", id);
            return Problem(503, GenerationErrors.Technical, "The recording could not be submitted.");
        }
        finally
        {
            if (savedFile && !submissionAttempted && !files.TryDelete(GenerationFileKind.Audio, id))
            {
                logger.LogWarning("Generation {GenerationId} recording deletion deferred", id);
            }
        }
    }

    private static IResult Invalid() => Problem(400, "invalid_recording", "The recording is invalid.");
    private static IResult TooLarge() => Problem(413, "recording_too_large", "The recording exceeds the allowed size.");
    private static IResult Unsupported() => Problem(415, "unsupported_media", "The upload requires multipart form data.");
    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    private sealed record SubmissionResponse(Guid Id, string Status);
}
