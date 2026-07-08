namespace TranscriptAnalyzer.Models;

public sealed class OpenAiProcessingMetrics
{
    public int FailedChunks { get; set; }

    public int RetriedChunks { get; set; }

    public List<int> FallbackChunkNumbers { get; } = [];

    public long DurationMs { get; set; }
}
