using ColoringApp.Api.Authentication;
using ColoringApp.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ColoringApp.Api.Generations;

internal sealed class GuestGenerationReader(ApplicationDbContext database, GuestAccess access)
{
    public Task<OwnedGeneration?> FindOwnedAsync(Guid id, HttpRequest request, CancellationToken cancellationToken)
    {
        if (access.ReadDigest(request) is not { } digest)
        {
            return Task.FromResult<OwnedGeneration?>(null);
        }

        return database.Generations.AsNoTracking()
            .Where(job => job.Id == id && job.UserId == null &&
                job.GuestSession.TokenDigest == digest && job.GuestSession.ExpiresAt > DateTime.UtcNow)
            .Select(job => new OwnedGeneration(job.Id, job.Status, job.ImageKey, job.ErrorCode))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public sealed record OwnedGeneration(Guid Id, GenerationStatus Status, Guid? ImageKey, string? ErrorCode);
}
