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
        var stage = "transcription";
        try
        {
            EnterStage("transcription");
            var transcript = await provider.TranscribeAsync(files.PathFor(GenerationFileKind.Audio, job.AudioKey), cancellationToken);
            if (string.IsNullOrWhiteSpace(transcript.Text))
            {
                return Stop(GenerationErrors.Unintelligible, "empty_transcript");
            }
            if (!transcript.IsPolish)
            {
                return Stop(GenerationErrors.Unintelligible, "non_polish_transcript");
            }
            if (transcript.Text.Length > 12_000 || transcript.Text.Contains('\0'))
            {
                throw new InvalidDataException();
            }

            EnterStage("transcript_moderation");
            var transcriptModeration = await provider.ModerateTextAsync(transcript.Text, cancellationToken);
            if (transcriptModeration.Flagged)
            {
                return Stop(GenerationErrors.Rejected, "moderation_flagged", transcriptModeration.FlaggedCategories);
            }

            EnterStage("assessment");
            var enrichment = await provider.EnrichAsync(transcript.Text, cancellationToken);
            if (enrichment is { Decision: "reject", Prompt: null, Keywords: null })
            {
                return Stop(GenerationErrors.Rejected, enrichment.ReasonCode ?? "transcript_policy");
            }
            if (enrichment is { Decision: "unintelligible", Prompt: null, Keywords: null })
            {
                return Stop(GenerationErrors.Unintelligible, "unintelligible_idea");
            }
            if (enrichment is not { Decision: "accept", Prompt: { Length: <= 4000 } prompt, Keywords: { Length: >= 1 and <= 8 } keywords } ||
                string.IsNullOrWhiteSpace(prompt) || prompt.Contains('\0') ||
                keywords.Any(keyword => string.IsNullOrWhiteSpace(keyword) || keyword.Length > 80 || keyword.Contains('\0')) ||
                keywords.Select(keyword => keyword.Trim().Normalize(NormalizationForm.FormC)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != keywords.Length)
            {
                throw new InvalidDataException();
            }

            var finalPrompt = $"{ImageInstructions}\n\n{prompt.Trim()}";
            EnterStage("prompt_moderation");
            var promptModeration = await provider.ModerateTextAsync(finalPrompt, cancellationToken);
            if (promptModeration.Flagged)
            {
                return Stop(GenerationErrors.Rejected, "moderation_flagged", promptModeration.FlaggedCategories);
            }

            EnterStage("prompt_review");
            var promptRejection = await provider.GetPromptRejectionReasonAsync(transcript.Text, finalPrompt, cancellationToken);
            if (promptRejection is not null)
            {
                return Stop(GenerationErrors.Rejected, promptRejection);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnterStage("image_generation");
            var image = await provider.GenerateImageAsync(finalPrompt, cancellationToken);
            EnterStage("image_verification");
            cancellationToken.ThrowIfCancellationRequested();
            ColoringPagePng.Verify(image);
            EnterStage("image_moderation");
            var imageModeration = await provider.ModerateImageAsync(image, cancellationToken);
            if (imageModeration.Flagged)
            {
                return Stop(GenerationErrors.Rejected, "moderation_flagged", imageModeration.FlaggedCategories);
            }

            return (new ApprovedGeneration(transcript.Text, finalPrompt, keywords.Select(keyword => keyword.Trim()).ToArray(), image), null);
        }
        catch (GenerationPolicyException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Stop(GenerationErrors.Rejected, exception.ReasonCode, exception.ModerationCategories,
                exception.ModerationStage, exception.RequestId);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var reason = exception switch
            {
                InvalidDataException when stage == "image_verification" => "invalid_png",
                InvalidDataException => "invalid_stage_data",
                HttpRequestException => "provider_request_failed",
                OperationCanceledException => "stage_timeout",
                _ => "stage_failed"
            };
            return Stop(GenerationErrors.Technical, reason);
        }

        void EnterStage(string nextStage)
        {
            stage = nextStage;
            logger.LogInformation("Generation {GenerationId} entered {Stage}", job.Id, stage);
        }

        (ApprovedGeneration? Approved, string? ErrorCode) Stop(string errorCode, string reasonCode, string[]? categories = null,
            string? moderationStage = null, string? requestId = null)
        {
            logger.LogInformation(
                "Generation {GenerationId} stopped at {Stage} with {ErrorCode}; reason {ReasonCode}; moderation categories {ModerationCategories}; OpenAI moderation stage {OpenAiModerationStage}; OpenAI request ID {OpenAiRequestId}",
                job.Id, stage, errorCode, reasonCode, categories is { Length: > 0 } ? string.Join(',', categories) : "-",
                moderationStage ?? "-", requestId ?? "-");
            return (null, errorCode);
        }
    }
}
