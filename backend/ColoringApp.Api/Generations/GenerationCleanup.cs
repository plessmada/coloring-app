using System.Diagnostics;
using ColoringApp.Api.Data;
using ColoringApp.Api.Integrations;
using Microsoft.EntityFrameworkCore;

namespace ColoringApp.Api.Generations;

internal sealed class GenerationCleanup(IDbContextFactory<ApplicationDbContext> factory, GenerationQueue queue,
    PrivateGenerationFiles files, GenerationPollingIntervals polling, ILogger<GenerationCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var clock = Stopwatch.StartNew();
        var sweepInterval = TimeSpan.FromMinutes(5);
        long lastSweepWindow = -1;
        using var ticks = new PeriodicTimer(polling.ExpiredJobCheckInterval);
        do
        {
            try
            {
                foreach (var expired in await queue.ExpireAsync(stoppingToken))
                {
                    logger.LogInformation("Generation {GenerationId} expired: generation_failed", expired.Id);
                    await RemoveRecordingAsync(expired, stoppingToken);
                }

                // Anchor filesystem sweeps to startup so their duration does not shift the next sweep.
                var sweepWindow = clock.Elapsed.Ticks / sweepInterval.Ticks;
                if (sweepWindow != lastSweepWindow)
                {
                    lastSweepWindow = sweepWindow;
                    await SweepAsync(stoppingToken);
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Generation cleanup encountered a technical failure");
            }
        }
        while (!stoppingToken.IsCancellationRequested && await ticks.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RemoveRecordingAsync(FinishedRecording recording, CancellationToken cancellationToken)
    {
        if (!files.TryDelete(GenerationFileKind.Audio, recording.AudioKey))
        {
            logger.LogWarning("Generation {GenerationId} recording deletion deferred", recording.Id);
            return;
        }

        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        await database.Generations.Where(job => job.Id == recording.Id && job.AudioKey == recording.AudioKey && job.CompletedAt != null)
            .ExecuteUpdateAsync(job => job.SetProperty(row => row.AudioKey, (Guid?)null), cancellationToken);
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var terminalRecordings = await database.Generations.Where(job => job.CompletedAt != null && job.AudioKey != null)
            .Select(job => new FinishedRecording(job.Id, job.AudioKey!.Value)).ToListAsync(cancellationToken);
        foreach (var recording in terminalRecordings)
        {
            await RemoveRecordingAsync(recording, cancellationToken);
        }

        await database.Database.ExecuteSqlRawAsync("""
            UPDATE coloring_app.guest_sessions SET token_digest = NULL
            WHERE token_digest IS NOT NULL AND expires_at <= clock_timestamp()
            """, cancellationToken);
        await database.Database.ExecuteSqlRawAsync("""
            DELETE FROM coloring_app.generations
            WHERE status IN ('Failed', 'Rejected') AND audio_key IS NULL
                AND completed_at <= clock_timestamp() - interval '24 hours'
            """, cancellationToken);

        // A fresh upload or image publication can exist before its reference is committed.
        var olderThan = DateTime.UtcNow.AddHours(-1);
        foreach (var kind in Enum.GetValues<GenerationFileKind>())
        {
            foreach (var key in files.FindUnreferencedCandidates(kind, olderThan))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var referenced = kind switch
                {
                    GenerationFileKind.Audio => await database.Generations.AnyAsync(job => job.AudioKey == key, cancellationToken),
                    GenerationFileKind.Image => await database.Generations.AnyAsync(job => job.ImageKey == key, cancellationToken),
                    _ => false // Published results always reference the approved directory.
                };
                if (!referenced && !files.TryDelete(kind, key))
                {
                    logger.LogWarning("Generation {FileKey} unreferenced file deletion deferred", key);
                }
            }
        }
    }
}
