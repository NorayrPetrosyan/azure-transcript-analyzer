using System.Diagnostics;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class TranscriptAnalysisService(
    TranscriptChunkingService chunking,
    RoleDetectionService roleDetection,
    AzureLanguageService azureLanguage,
    DynamicAttributeExtractionService dynamicAttributeExtraction,
    ConfigurationService configuration,
    RegexExtractionService regexExtraction,
    AnalysisResultFileWriter resultFileWriter)
{
    public async Task<AnalyzeResponse> AnalyzeAsync(
        string transcript,
        string? language,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var chunks = chunking.Split(transcript);
        var warnings = new List<string>();
        var useLargeTranscriptSafeMode = configuration.EnableLargeTranscriptSafeMode
            && transcript.Length >= configuration.LargeTranscriptThresholdCharacters;

        var (conversation, roleMethod, roleWarnings, roleMetrics) = await roleDetection.DetectAsync(
            transcript,
            chunks,
            cancellationToken,
            allowOpenAiRoleDetection: !useLargeTranscriptSafeMode);
        warnings.AddRange(roleWarnings);

        if (useLargeTranscriptSafeMode)
        {
            warnings.Add($"Large transcript safe mode enabled for transcript length {transcript.Length}; local extraction and chunked Azure Language were prioritized.");
        }

        var localStopwatch = Stopwatch.StartNew();
        var regexAttributes = regexExtraction.Extract(transcript, conversation);
        localStopwatch.Stop();

        var azureTask = AnalyzeAzureLanguageWithTimingAsync();
        var allowDynamicOpenAi = !useLargeTranscriptSafeMode
            || configuration.EnableDynamicAiExtractionInLargeTranscriptSafeMode;
        var dynamicTask = dynamicAttributeExtraction.ExtractChunksAsync(
            chunks,
            cancellationToken,
            allowOpenAiDynamicExtraction: allowDynamicOpenAi);

        await Task.WhenAll(azureTask, dynamicTask);

        var (azureAttributes, rawEntities, azureWarnings, azureLanguageDurationMs) = await azureTask;
        warnings.AddRange(azureWarnings);

        var (dynamicAttributes, dynamicWarnings, dynamicMetrics) = await dynamicTask;
        warnings.AddRange(dynamicWarnings);

        var extractedAttributes = regexExtraction.Merge([.. azureAttributes, regexAttributes]);
        totalStopwatch.Stop();

        var response = new AnalyzeResponse
        {
            Conversation = conversation,
            ExtractedAttributes = extractedAttributes,
            DynamicAttributes = dynamicAttributes,
            RawAzureEntities = rawEntities,
            Warning = BuildUserFriendlyWarning(warnings, roleMetrics, dynamicMetrics),
            RoleMethod = roleMethod,
            ProcessingInfo = new ProcessingInfo
            {
                UsedChunking = chunks.Count > 1,
                ChunkCount = chunks.Count,
                Language = string.IsNullOrWhiteSpace(language) ? "" : language.Trim().ToLowerInvariant(),
                LargeTranscriptSafeMode = useLargeTranscriptSafeMode,
                LargeTranscriptThresholdCharacters = configuration.LargeTranscriptThresholdCharacters,
                FailedOpenAiChunks = roleMetrics.FailedChunks + dynamicMetrics.FailedChunks,
                OpenAiFailedChunks = roleMetrics.FallbackChunkNumbers.Distinct().Order().ToList(),
                DynamicAiSkippedChunks = dynamicMetrics.FallbackChunkNumbers.Distinct().Order().ToList(),
                FallbackUsed = roleMethod.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                    || roleMetrics.FallbackChunkNumbers.Count > 0
                    || dynamicMetrics.FallbackChunkNumbers.Count > 0,
                RetriedOpenAiChunks = roleMetrics.RetriedChunks + dynamicMetrics.RetriedChunks,
                UsedFallbackForChunks = roleMetrics.FallbackChunkNumbers
                    .Concat(dynamicMetrics.FallbackChunkNumbers)
                    .Distinct()
                    .Order()
                    .ToList(),
                TotalDurationMs = totalStopwatch.ElapsedMilliseconds,
                AzureOpenAiDurationMs = roleMetrics.DurationMs + dynamicMetrics.DurationMs,
                AzureLanguageDurationMs = azureLanguageDurationMs,
                LocalExtractionDurationMs = localStopwatch.ElapsedMilliseconds
            }
        };

        await resultFileWriter.SaveAsync(
            response,
            language,
            transcript.Length,
            DateTimeOffset.UtcNow,
            cancellationToken);

        return response;

        async Task<(IReadOnlyList<ExtractedAttributes> Attributes, IReadOnlyList<RawAzureEntity> RawEntities, IReadOnlyList<string> Warnings, long DurationMs)> AnalyzeAzureLanguageWithTimingAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var (attributes, rawEntitiesResult, azureWarningsResult) = await azureLanguage.AnalyzeChunksAsync(chunks, language, cancellationToken);
            stopwatch.Stop();
            return (attributes, rawEntitiesResult, azureWarningsResult, stopwatch.ElapsedMilliseconds);
        }
    }

    private static string? BuildUserFriendlyWarning(
        IReadOnlyList<string> warnings,
        OpenAiProcessingMetrics roleMetrics,
        OpenAiProcessingMetrics dynamicMetrics)
    {
        var summarized = new List<string>();

        if (roleMetrics.FallbackChunkNumbers.Count > 0)
        {
            summarized.Add($"OpenAI returned empty or invalid role content for {roleMetrics.FallbackChunkNumbers.Distinct().Count()} chunks; fallback speaker labels were used.");
        }

        if (dynamicMetrics.FallbackChunkNumbers.Count > 0)
        {
            var skippedCount = dynamicMetrics.FallbackChunkNumbers.Distinct().Count();
            summarized.Add(skippedCount == 1
                ? "Dynamic AI extraction was skipped; conservative fallback extraction was used."
                : $"Dynamic AI extraction skipped {skippedCount} chunks; conservative fallback extraction was used.");
        }

        foreach (var warning in warnings)
        {
            if (string.IsNullOrWhiteSpace(warning))
            {
                continue;
            }

            if ((warning.Contains("chunk", StringComparison.OrdinalIgnoreCase)
                    && (warning.Contains("OpenAI", StringComparison.OrdinalIgnoreCase)
                        || warning.Contains("Dynamic AI extraction skipped", StringComparison.OrdinalIgnoreCase)))
                || (dynamicMetrics.FallbackChunkNumbers.Count > 0
                    && warning.Contains("Dynamic AI extraction skipped", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!summarized.Contains(warning, StringComparer.OrdinalIgnoreCase))
            {
                summarized.Add(warning);
            }
        }

        return summarized.Count > 0 ? string.Join("; ", summarized) : null;
    }
}
