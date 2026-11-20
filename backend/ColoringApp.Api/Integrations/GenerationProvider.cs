namespace ColoringApp.Api.Integrations;

// Implementations are registered by the API or explicitly replaced by the test host.
public interface IGenerationProvider
{
    Task<PolishTranscription> TranscribeAsync(string recordingPath, CancellationToken cancellationToken);
    Task<ModerationDecision> ModerateTextAsync(string text, CancellationToken cancellationToken);
    Task<EnrichmentDecision> EnrichAsync(string transcript, CancellationToken cancellationToken);
    Task<string?> GetPromptRejectionReasonAsync(string transcript, string prompt, CancellationToken cancellationToken);
    Task<byte[]> GenerateImageAsync(string prompt, CancellationToken cancellationToken);
    Task<ModerationDecision> ModerateImageAsync(byte[] png, CancellationToken cancellationToken);
}

public sealed record PolishTranscription(string Text, bool IsPolish);
public sealed record ModerationDecision(bool Flagged, string[] FlaggedCategories);
public sealed record EnrichmentDecision(string Decision, string? Prompt, string[]? Keywords, string? ReasonCode = null);
