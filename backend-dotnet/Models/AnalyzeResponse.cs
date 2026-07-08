using System.Text.Json.Serialization;

namespace TranscriptAnalyzer.Models;

public sealed class AnalyzeResponse
{
    [JsonPropertyName("conversation")]
    public IReadOnlyList<ConversationTurn> Conversation { get; init; } = [];

    [JsonPropertyName("extractedAttributes")]
    public ExtractedAttributes ExtractedAttributes { get; init; } = new();

    [JsonPropertyName("dynamicAttributes")]
    public IReadOnlyList<DynamicAttribute> DynamicAttributes { get; init; } = [];

    [JsonPropertyName("rawAzureEntities")]
    public IReadOnlyList<RawAzureEntity> RawAzureEntities { get; init; } = [];

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }

    [JsonPropertyName("roleMethod")]
    public string RoleMethod { get; init; } = "fallback";

    [JsonPropertyName("processingInfo")]
    public ProcessingInfo ProcessingInfo { get; init; } = new();
}
