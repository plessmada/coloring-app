namespace ColoringApp.Api.Generations;

internal sealed record GenerationPollingIntervals(TimeSpan IdleQueueDelay, TimeSpan ExpiredJobCheckInterval);
