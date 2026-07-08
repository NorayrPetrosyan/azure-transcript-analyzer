using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class DynamicAttribute
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "azure-openai";

    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }
}
