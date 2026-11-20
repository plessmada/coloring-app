using System.Text;
using ColoringApp.Api.Integrations;

namespace ColoringApp.Api.Generations;

internal sealed class GenerationProcessor(IGenerationProvider provider, PrivateGenerationFiles files,
    ILogger<GenerationProcessor> logger)
{
    private const string ImageInstructions =
        "Create a child-friendly, portrait coloring page. Use clean black outlines on a pure white background. " +
        "Keep the interiors blank. No shading, gray tones, colors, captions or text.";

    public async Task<(ApprovedGeneration? Approved, string? ErrorCode)> ProcessAsync(ClaimedGeneration job, CancellationToken cancellationToken)
    {
        Stage(job, "transcription");
        var transcript = await provider.TranscribeAsync(files.PathFor(GenerationFileKind.Audio, job.AudioKey), cancellationToken);
        if (!transcript.IsPolish || string.IsNullOrWhiteSpace(transcript.Text))
        {
            return (null, GenerationErrors.Unintelligible);
        }
        if (transcript.Text.Length > 12_000 || transcript.Text.Contains('\0'))
        {
            throw new InvalidDataException();
        }

        Stage(job, "transcript_moderation");
        if (await provider.IsTextFlaggedAsync(transcript.Text, cancellationToken))
        {
            return (null, GenerationErrors.Rejected);
        }

        Stage(job, "assessment");
        var enrichment = await provider.EnrichAsync(transcript.Text, cancellationToken);
        if (enrichment is { Decision: "reject", Prompt: null, Keywords: null })
        {
            return (null, GenerationErrors.Rejected);
        }
        if (enrichment is { Decision: "unintelligible", Prompt: null, Keywords: null })
        {
            return (null, GenerationErrors.Unintelligible);
        }
        if (enrichment is not { Decision: "accept", Prompt: { Length: <= 4000 } prompt, Keywords: { Length: >= 1 and <= 8 } keywords } ||
            string.IsNullOrWhiteSpace(prompt) || prompt.Contains('\0') ||
            keywords.Any(keyword => string.IsNullOrWhiteSpace(keyword) || keyword.Length > 80 || keyword.Contains('\0')) ||
            keywords.Select(keyword => keyword.Trim().Normalize(NormalizationForm.FormC)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != keywords.Length)
        {
            throw new InvalidDataException();
        }

        var finalPrompt = $"{ImageInstructions}\n\n{prompt.Trim()}";
        Stage(job, "prompt_moderation");
        if (await provider.IsTextFlaggedAsync(finalPrompt, cancellationToken) ||
            !await provider.IsPromptAllowedAsync(transcript.Text, finalPrompt, cancellationToken))
        {
            return (null, GenerationErrors.Rejected);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Stage(job, "image_generation");
        var image = await provider.GenerateImageAsync(finalPrompt, cancellationToken);
        Stage(job, "image_verification");
        cancellationToken.ThrowIfCancellationRequested();
        ColoringPagePng.Verify(image);
        if (await provider.IsImageFlaggedAsync(image, cancellationToken))
        {
            return (null, GenerationErrors.Rejected);
        }

        return (new ApprovedGeneration(transcript.Text, finalPrompt, keywords.Select(keyword => keyword.Trim()).ToArray(), image), null);
    }

    private void Stage(ClaimedGeneration job, string stage)
    {
        logger.LogInformation("Generation {GenerationId} entered {Stage}", job.Id, stage);
    }
}
