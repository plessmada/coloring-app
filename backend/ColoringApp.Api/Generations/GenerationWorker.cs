using System.Diagnostics;
using ColoringApp.Api.Integrations;

namespace ColoringApp.Api.Generations;

internal sealed class GenerationWorker(GenerationQueue queue, GenerationCleanup cleanup, GenerationProcessor processor,
    PrivateGenerationFiles files, GenerationPollingIntervals polling, ILogger<GenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await queue.ClaimAsync(stoppingToken);
                if (claimed is not null)
                {
                    await ProcessAsync(claimed, stoppingToken);
                    continue;
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Generation worker encountered a technical failure");
            }

            await Task.Delay(polling.IdleQueueDelay, stoppingToken);
        }
    }

    private async Task ProcessAsync(ClaimedGeneration job, CancellationToken stoppingToken)
    {
        var timer = Stopwatch.StartNew();
        using var jobLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        jobLifetime.CancelAfter(TimeSpan.FromMinutes(5));
        using var heartbeatLifetime = CancellationTokenSource.CreateLinkedTokenSource(jobLifetime.Token);
        var heartbeat = RenewLeaseAsync(job, jobLifetime, heartbeatLifetime.Token);
        var cancellationToken = jobLifetime.Token;
        var errorCode = GenerationErrors.Technical;
        var completed = false;
        try
        {
            var outcome = await processor.ProcessAsync(job, cancellationToken);
            errorCode = outcome.ErrorCode ?? GenerationErrors.Technical;
            cancellationToken.ThrowIfCancellationRequested();

            if (outcome.Approved is { } approved)
            {
                var key = Guid.NewGuid();
                await files.PublishAsync(key, approved.Png, cancellationToken);
                // Keep the complete file on an ambiguous commit. Reconciliation checks references.
                completed = await queue.SucceedAsync(job, approved, key, cancellationToken);
                if (!completed)
                {
                    files.TryDelete(GenerationFileKind.Image, key);
                }
                else
                {
                    logger.LogInformation("Generation {GenerationId} succeeded in {ElapsedMs} ms", job.Id, timer.ElapsedMilliseconds);
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            errorCode = GenerationErrors.Technical;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The expiry sweep resolves interrupted jobs using database time.
        }

        try
        {
            if (!completed && !cancellationToken.IsCancellationRequested)
            {
                completed = await queue.FailAsync(job, errorCode, cancellationToken);
                if (completed)
                {
                    logger.LogInformation("Generation {GenerationId} ended with {ErrorCode} in {ElapsedMs} ms",
                        job.Id, errorCode, timer.ElapsedMilliseconds);
                }
            }
        }
        finally
        {
            await heartbeatLifetime.CancelAsync();
            await heartbeat;
        }

        if (completed)
        {
            await cleanup.RemoveRecordingAsync(new FinishedRecording(job.Id, job.AudioKey), stoppingToken);
        }
    }

    private async Task RenewLeaseAsync(ClaimedGeneration job, CancellationTokenSource lifetime, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!await queue.RenewAsync(job, cancellationToken))
                {
                    await lifetime.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Work finished or the application is shutting down.
        }
        catch (Exception)
        {
            logger.LogWarning("Generation {GenerationId} lease renewal failed", job.Id);
            await lifetime.CancelAsync();
        }
    }
}
