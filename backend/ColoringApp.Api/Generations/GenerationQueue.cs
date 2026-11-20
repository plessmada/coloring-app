using ColoringApp.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ColoringApp.Api.Generations;

internal sealed record ClaimedGeneration(Guid Id, Guid AudioKey, Guid OwnerToken);
internal sealed record FinishedRecording(Guid Id, Guid AudioKey);
internal sealed record ApprovedGeneration(string Transcript, string Prompt, string[] Keywords, byte[] Png);

internal sealed class GenerationQueue(IDbContextFactory<ApplicationDbContext> factory)
{
    public async Task<ClaimedGeneration?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var owner = Guid.NewGuid();
        var claims = await database.Database.SqlQuery<ClaimedGeneration>($"""
            WITH next_job AS (
                SELECT id FROM coloring_app.generations
                WHERE status = 'Pending' AND created_at > clock_timestamp() - interval '30 minutes'
                ORDER BY created_at, id
                LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE coloring_app.generations AS job
            SET status = 'Processing', owner_token = {owner}, started_at = statement_timestamp(),
                lease_until = statement_timestamp() + interval '60 seconds',
                deadline_at = statement_timestamp() + interval '5 minutes'
            FROM next_job
            WHERE job.id = next_job.id
            RETURNING job.id AS "Id", job.audio_key AS "AudioKey", job.owner_token AS "OwnerToken"
            """).ToListAsync(cancellationToken);
        return claims.SingleOrDefault();
    }

    public async Task<bool> RenewAsync(ClaimedGeneration claim, CancellationToken cancellationToken)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        return await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE coloring_app.generations
            SET lease_until = LEAST(deadline_at, clock_timestamp() + interval '60 seconds')
            WHERE id = {claim.Id} AND status = 'Processing' AND owner_token = {claim.OwnerToken}
                AND lease_until > clock_timestamp() AND deadline_at > clock_timestamp()
            """, cancellationToken) == 1;
    }

    public async Task<bool> SucceedAsync(ClaimedGeneration claim, ApprovedGeneration result, Guid imageKey,
        CancellationToken cancellationToken)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        return await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE coloring_app.generations
            SET status = 'Succeeded', completed_at = clock_timestamp(), owner_token = NULL,
                lease_until = NULL, deadline_at = NULL, transcript = {result.Transcript},
                final_prompt = {result.Prompt}, keywords = {result.Keywords}, image_key = {imageKey}
            WHERE id = {claim.Id} AND status = 'Processing' AND owner_token = {claim.OwnerToken}
                AND lease_until > clock_timestamp() AND deadline_at > clock_timestamp()
            """, cancellationToken) == 1;
    }

    public async Task<bool> FailAsync(ClaimedGeneration claim, string errorCode, CancellationToken cancellationToken)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var status = errorCode == GenerationErrors.Rejected ? "Rejected" : "Failed";
        return await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE coloring_app.generations
            SET status = {status}, error_code = {errorCode}, completed_at = clock_timestamp(),
                owner_token = NULL, lease_until = NULL, deadline_at = NULL
            WHERE id = {claim.Id} AND status = 'Processing' AND owner_token = {claim.OwnerToken}
                AND lease_until > clock_timestamp() AND deadline_at > clock_timestamp()
            """, cancellationToken) == 1;
    }

    public async Task<List<FinishedRecording>> ExpireAsync(CancellationToken cancellationToken)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        return await database.Database.SqlQuery<FinishedRecording>($"""
            UPDATE coloring_app.generations
            SET status = 'Failed', error_code = 'generation_failed', completed_at = clock_timestamp(),
                owner_token = NULL, lease_until = NULL, deadline_at = NULL
            WHERE (status = 'Pending' AND created_at <= clock_timestamp() - interval '30 minutes')
               OR (status = 'Processing' AND (lease_until <= clock_timestamp() OR deadline_at <= clock_timestamp()))
            RETURNING id AS "Id", audio_key AS "AudioKey"
            """).ToListAsync(cancellationToken);
    }
}
