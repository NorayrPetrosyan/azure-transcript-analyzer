using System.Text;
using System.Text.Json;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class AzureLanguageService(
    IHttpClientFactory httpClientFactory,
    ConfigurationService configuration,
    ILogger<AzureLanguageService> logger)
{
    private static readonly Dictionary<string, string> CategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Person"] = nameof(ExtractedAttributes.Name),
        ["Address"] = nameof(ExtractedAttributes.Address),
        ["PhoneNumber"] = nameof(ExtractedAttributes.PhoneNumber),
        ["Email"] = nameof(ExtractedAttributes.Email),
        ["USSocialSecurityNumber"] = nameof(ExtractedAttributes.SocialSecurityNumber),
        ["EUPassportNumber"] = nameof(ExtractedAttributes.SocialSecurityNumber)
    };

    public async Task<(IReadOnlyList<ExtractedAttributes> Attributes, IReadOnlyList<RawAzureEntity> RawEntities, IReadOnlyList<string> Warnings)> AnalyzeChunksAsync(
        IReadOnlyList<TranscriptChunk> chunks,
        string? language,
        CancellationToken cancellationToken)
    {
        if (!configuration.AzureLanguageConfigured)
        {
            return ([], [], ["Azure AI Language is not configured"]);
        }

        using var semaphore = new SemaphoreSlim(configuration.MaxConcurrentChunks);
        var tasks = chunks.Select(chunk => AnalyzeChunkWithLimitAsync(chunk, language, semaphore, cancellationToken));
        var results = await Task.WhenAll(tasks);

        var orderedResults = results.OrderBy(result => result.ChunkIndex).ToList();
        var attributes = orderedResults.Select(result => result.Attributes).ToList();
        var rawEntities = orderedResults.SelectMany(result => result.RawEntities).ToList();
        var warnings = orderedResults
            .Select(result => result.Warning)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Cast<string>()
            .ToList();

        return (attributes, rawEntities, warnings);
    }

    private async Task<(int ChunkIndex, ExtractedAttributes Attributes, IReadOnlyList<RawAzureEntity> RawEntities, string? Warning)> AnalyzeChunkWithLimitAsync(
        TranscriptChunk chunk,
        string? language,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await AnalyzeChunkAsync(chunk, language, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<(int ChunkIndex, ExtractedAttributes Attributes, IReadOnlyList<RawAzureEntity> RawEntities, string? Warning)> AnalyzeChunkAsync(
        TranscriptChunk chunk,
        string? language,
        CancellationToken cancellationToken)
    {
        if (!configuration.AzureLanguageConfigured)
        {
            return (chunk.Index, new ExtractedAttributes(), [], "Azure AI Language is not configured");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.AzureLanguageTimeoutSeconds));

            var endpoint = configuration.AzureLanguageEndpoint.TrimEnd('/');
            var url = $"{endpoint}/language/:analyze-text?api-version=2023-04-01";
            var document = new Dictionary<string, object?>
            {
                ["id"] = chunk.Index.ToString(),
                ["text"] = chunk.Text.Length > 5120 ? chunk.Text[..5120] : chunk.Text
            };

            var azureLanguage = NormalizeAzureLanguage(language);
            if (!string.IsNullOrWhiteSpace(azureLanguage))
            {
                document["language"] = azureLanguage;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Ocp-Apim-Subscription-Key", configuration.AzureLanguageKey);
            request.Content = JsonContent(new
            {
                kind = "PiiEntityRecognition",
                parameters = new { modelVersion = "latest" },
                analysisInput = new { documents = new[] { document } }
            });

            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Azure Language failed with status {StatusCode}", response.StatusCode);
                return (chunk.Index, new ExtractedAttributes(), [], $"Azure AI Language failed for chunk {chunk.Index + 1} with status {(int)response.StatusCode}");
            }

            using var responseDocument = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(timeoutCts.Token),
                cancellationToken: timeoutCts.Token);

            var result = responseDocument.RootElement
                .GetProperty("results")
                .GetProperty("documents")[0];

            var attrs = new ExtractedAttributes();
            var raw = new List<RawAzureEntity>();
            var bestConfidence = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(ExtractedAttributes.Name)] = -1,
                [nameof(ExtractedAttributes.Address)] = -1,
                [nameof(ExtractedAttributes.PhoneNumber)] = -1,
                [nameof(ExtractedAttributes.Email)] = -1,
                [nameof(ExtractedAttributes.SocialSecurityNumber)] = -1
            };

            foreach (var entity in result.GetProperty("entities").EnumerateArray())
            {
                var text = entity.GetProperty("text").GetString() ?? "";
                var category = entity.GetProperty("category").GetString() ?? "";
                var subcategory = entity.TryGetProperty("subcategory", out var subcategoryElement)
                    ? subcategoryElement.GetString()
                    : null;
                var confidence = entity.TryGetProperty("confidenceScore", out var confidenceElement)
                    ? confidenceElement.GetDouble()
                    : 0;

                raw.Add(new RawAzureEntity
                {
                    Text = text,
                    Category = category,
                    Subcategory = subcategory,
                    Confidence = Math.Round(confidence, 3)
                });

                if (confidence < 0.6 || !CategoryMap.TryGetValue(category, out var field))
                {
                    continue;
                }

                if (field == "Other")
                {
                    var label = $"{category}: {text}";
                    if (!attrs.Other.Contains(label, StringComparer.OrdinalIgnoreCase))
                    {
                        attrs.Other.Add(label);
                    }

                    continue;
                }

                if (confidence <= bestConfidence[field])
                {
                    continue;
                }

                bestConfidence[field] = confidence;
                SetField(attrs, field, text);
            }

            return (chunk.Index, attrs, raw, null);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Azure Language analysis timed out");
            return (chunk.Index, new ExtractedAttributes(), [], $"Azure AI Language timed out for chunk {chunk.Index + 1}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure Language analysis failed");
            return (chunk.Index, new ExtractedAttributes(), [], $"Azure AI Language failed for chunk {chunk.Index + 1}: {ex.Message}");
        }
    }

    private static string? NormalizeAzureLanguage(string? language)
    {
        return language?.Trim().ToLowerInvariant() switch
        {
            "en" => "en",
            "hy" => "hy",
            _ => null
        };
    }

    private static void SetField(ExtractedAttributes attrs, string field, string value)
    {
        switch (field)
        {
            case nameof(ExtractedAttributes.Name):
                attrs.Name = value;
                break;
            case nameof(ExtractedAttributes.Address):
                attrs.Address = value;
                break;
            case nameof(ExtractedAttributes.PhoneNumber):
                attrs.PhoneNumber = value;
                break;
            case nameof(ExtractedAttributes.Email):
                attrs.Email = value;
                break;
            case nameof(ExtractedAttributes.SocialSecurityNumber):
                attrs.SocialSecurityNumber = value;
                break;
        }
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
