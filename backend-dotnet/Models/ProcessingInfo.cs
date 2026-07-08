using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class ProcessingInfo
{
    [JsonPropertyName("usedChunking")]
    public bool UsedChunking { get; init; }

    [JsonPropertyName("chunkCount")]
    public int ChunkCount { get; init; }

    [JsonPropertyName("language")]
    public string Language { get; init; } = "";

    [JsonPropertyName("largeTranscriptSafeMode")]
    public bool LargeTranscriptSafeMode { get; init; }

    [JsonPropertyName("largeTranscriptThresholdCharacters")]
    public int LargeTranscriptThresholdCharacters { get; init; }

    [JsonPropertyName("failedOpenAiChunks")]
    public int FailedOpenAiChunks { get; set; }

    [JsonPropertyName("openAiFailedChunks")]
    public IReadOnlyList<int> OpenAiFailedChunks { get; init; } = [];

    [JsonPropertyName("dynamicAiSkippedChunks")]
    public IReadOnlyList<int> DynamicAiSkippedChunks { get; init; } = [];

    [JsonPropertyName("fallbackUsed")]
    public bool FallbackUsed { get; init; }

    [JsonPropertyName("retriedOpenAiChunks")]
    public int RetriedOpenAiChunks { get; set; }

    [JsonPropertyName("usedFallbackForChunks")]
    public IReadOnlyList<int> UsedFallbackForChunks { get; init; } = [];

    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; init; }

    [JsonPropertyName("azureOpenAiDurationMs")]
    public long AzureOpenAiDurationMs { get; set; }

    [JsonPropertyName("azureLanguageDurationMs")]
    public long AzureLanguageDurationMs { get; set; }

    [JsonPropertyName("localExtractionDurationMs")]
    public long LocalExtractionDurationMs { get; set; }
}
