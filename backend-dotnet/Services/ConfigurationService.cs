namespace TranscriptAnalyzer.Services;

public sealed class ConfigurationService(IConfiguration configuration)
{
    public string AzureLanguageEndpoint =>
        Read("AzureLanguage:Endpoint", "AZURE_LANGUAGE_ENDPOINT");

    public string AzureLanguageKey =>
        Read("AzureLanguage:Key", "AZURE_LANGUAGE_KEY");

    public string AzureOpenAiEndpoint =>
        Read("AzureOpenAI:Endpoint", "AZURE_OPENAI_ENDPOINT");

    public string AzureOpenAiKey =>
        Read("AzureOpenAI:Key", "AZURE_OPENAI_KEY");

    public string AzureOpenAiDeployment =>
        Read("AzureOpenAI:Deployment", "AZURE_OPENAI_DEPLOYMENT");

    public string AzureOpenAiApiVersion =>
        Read("AzureOpenAI:ApiVersion", "AZURE_OPENAI_API_VERSION", "2024-10-21");

    public bool EnableDynamicAiExtraction =>
        ReadBool("Processing:EnableDynamicAiExtraction", "ENABLE_DYNAMIC_AI_EXTRACTION", true);

    public bool EnableDynamicAiExtractionInLargeTranscriptSafeMode =>
        ReadBool("Processing:EnableDynamicAiExtractionInLargeTranscriptSafeMode", "ENABLE_DYNAMIC_AI_EXTRACTION_IN_LARGE_TRANSCRIPT_SAFE_MODE", false);

    public bool EnableLargeTranscriptSafeMode =>
        ReadBool("Processing:EnableLargeTranscriptSafeMode", "ENABLE_LARGE_TRANSCRIPT_SAFE_MODE", true);

    public int LargeTranscriptThresholdCharacters =>
        Math.Max(1000, ReadInt("Processing:LargeTranscriptThresholdCharacters", "LARGE_TRANSCRIPT_THRESHOLD_CHARACTERS", 20000));

    public int TranscriptChunkSize =>
        ReadInt("TranscriptChunking:ChunkMaxCharacters", "CHUNK_MAX_CHARACTERS",
            ReadInt("TranscriptChunking:ChunkSize", "TRANSCRIPT_CHUNK_SIZE", 4000));

    public int TranscriptChunkOverlap =>
        ReadInt("TranscriptChunking:ChunkOverlapLines", "CHUNK_OVERLAP_LINES",
            ReadInt("TranscriptChunking:OverlapLines", "TRANSCRIPT_CHUNK_OVERLAP_LINES", 6));

    public int MaxConcurrentChunks =>
        Math.Clamp(ReadInt("Processing:MaxConcurrentChunks", "MAX_CONCURRENT_CHUNKS",
            ReadInt("TranscriptChunking:MaxConcurrentChunks", "MAX_CONCURRENT_CHUNKS", 3)), 1, 8);

    public int ExternalRequestTimeoutSeconds =>
        Math.Clamp(ReadInt("ExternalApis:RequestTimeoutSeconds", "EXTERNAL_REQUEST_TIMEOUT_SECONDS", 60), 5, 300);

    public int AzureOpenAiTimeoutSeconds =>
        Math.Clamp(ReadInt("ExternalApis:AzureOpenAITimeoutSeconds", "AZURE_OPENAI_TIMEOUT_SECONDS", ExternalRequestTimeoutSeconds), 5, 300);

    public int AzureLanguageTimeoutSeconds =>
        Math.Clamp(ReadInt("ExternalApis:AzureLanguageTimeoutSeconds", "AZURE_LANGUAGE_TIMEOUT_SECONDS", ExternalRequestTimeoutSeconds), 5, 300);

    public bool AzureLanguageConfigured =>
        LooksConfigured(AzureLanguageEndpoint) && LooksConfigured(AzureLanguageKey);

    public bool AzureOpenAiConfigured =>
        LooksConfigured(AzureOpenAiEndpoint)
        && LooksConfigured(AzureOpenAiKey)
        && LooksConfigured(AzureOpenAiDeployment);

    private string Read(string configKey, string environmentKey, string fallback = "")
    {
        return configuration[environmentKey]
            ?? configuration[configKey]
            ?? fallback;
    }

    private int ReadInt(string configKey, string environmentKey, int fallback)
    {
        var raw = Read(configKey, environmentKey);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    private bool ReadBool(string configKey, string environmentKey, bool fallback)
    {
        var raw = Read(configKey, environmentKey);
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    private static bool LooksConfigured(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && !value.Contains('<');
    }
}
