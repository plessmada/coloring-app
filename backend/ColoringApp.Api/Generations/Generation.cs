using ColoringApp.Api.Authentication;

namespace ColoringApp.Api.Generations;

internal enum GenerationStatus
{
    Pending,
    Processing,
    Succeeded,
    Rejected,
    Failed
}

internal static class GenerationErrors
{
    public const string Technical = "generation_failed";
    public const string Unintelligible = "unintelligible_audio";
    public const string Rejected = "content_rejected";
}

internal sealed class Generation
{
    public Guid Id { get; init; }
    public Guid GuestSessionId { get; init; }
    public GuestSession GuestSession { get; init; } = null!;
    public Guid? UserId { get; init; }
    public GenerationStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public Guid? AudioKey { get; init; }
    public Guid? OwnerToken { get; init; }
    public DateTime? LeaseUntil { get; init; }
    public DateTime? DeadlineAt { get; init; }
    public string? ErrorCode { get; init; }
    public string? Transcript { get; init; }
    public string? FinalPrompt { get; init; }
    public string[]? Keywords { get; init; }
    public Guid? ImageKey { get; init; }
}
