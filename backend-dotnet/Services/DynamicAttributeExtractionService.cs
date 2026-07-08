using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed partial class DynamicAttributeExtractionService(
    IHttpClientFactory httpClientFactory,
    ConfigurationService configuration,
    ILogger<DynamicAttributeExtractionService> logger)
{
    private const string SystemPrompt = """
        You are a clinic call-center transcript analyzer.
        Extract important case-related parameters from the transcript chunk.

        Do not limit yourself to fixed demographic fields. Extract medically or
        operationally relevant information when mentioned, including symptoms,
        reason for call, medications, allergies, doctor/provider name,
        appointment date/time preference, urgent concerns, insurance/OHIP/payment
        issues, callback requests, pharmacy details, follow-up actions, and other
        useful case notes.

        Preserve the transcript language and wording as much as possible.
        Do not invent facts. Do not include API keys or configuration values.

        Return ONLY strict JSON in this shape:
        {
          "attributes": [
            {
              "key": "appointment_request",
              "label": "Appointment request",
              "value": "Caller wants to book a follow-up with Dr. Smith",
              "category": "appointment",
              "confidence": 0.82
            }
          ]
        }

        Use stable snake_case keys. Confidence must be a number between 0 and 1.
        If nothing useful is present, return {"attributes":[]}.
        """;

    public async Task<(IReadOnlyList<DynamicAttribute> Attributes, IReadOnlyList<string> Warnings, OpenAiProcessingMetrics Metrics)> ExtractChunksAsync(
        IReadOnlyList<TranscriptChunk> chunks,
        CancellationToken cancellationToken,
        bool allowOpenAiDynamicExtraction = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var metrics = new OpenAiProcessingMetrics();
        var ruleBasedAttributes = chunks.SelectMany(ExtractRuleBasedAttributes).ToList();

        if (!configuration.EnableDynamicAiExtraction)
        {
            stopwatch.Stop();
            metrics.DurationMs = stopwatch.ElapsedMilliseconds;
            return (Deduplicate(ruleBasedAttributes), ["Dynamic AI extraction is disabled; rule-based dynamic attributes were used"], metrics);
        }

        if (!allowOpenAiDynamicExtraction)
        {
            stopwatch.Stop();
            metrics.DurationMs = stopwatch.ElapsedMilliseconds;
            metrics.FallbackChunkNumbers.AddRange(chunks.Select(chunk => chunk.Index + 1));
            return (
                Deduplicate(ruleBasedAttributes),
                ["Dynamic AI extraction skipped in large transcript safe mode; fixed fields and rule-based attributes were prioritized."],
                metrics);
        }

        if (!configuration.AzureOpenAiConfigured)
        {
            stopwatch.Stop();
            metrics.DurationMs = stopwatch.ElapsedMilliseconds;
            return (Deduplicate(ruleBasedAttributes), ["Azure OpenAI is not configured for dynamic attribute extraction"], metrics);
        }

        using var semaphore = new SemaphoreSlim(configuration.MaxConcurrentChunks);
        var tasks = chunks.Select(chunk => ExtractChunkWithLimitAsync(chunk, semaphore, cancellationToken));
        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();

        var warnings = results
            .Select(result => result.Warning)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Cast<string>()
            .ToList();

        metrics.FailedChunks = results.Count(result => result.Failed);
        metrics.RetriedChunks = results.Count(result => result.Retried);
        metrics.FallbackChunkNumbers.AddRange(results
            .Where(result => result.Failed)
            .Select(result => result.ChunkIndex + 1));
        metrics.DurationMs = stopwatch.ElapsedMilliseconds;

        var merged = Deduplicate(
            ruleBasedAttributes.Concat(results
                .OrderBy(result => result.ChunkIndex)
                .SelectMany(result => result.Attributes)));

        return (merged, warnings, metrics);
    }

    private static IEnumerable<DynamicAttribute> ExtractRuleBasedAttributes(TranscriptChunk chunk)
    {
        foreach (Match match in PharmacyRegex().Matches(chunk.Text))
        {
            var value = CleanupValue(match.Groups["value"].Value);
            if (!IsUsefulPharmacyValue(value))
            {
                continue;
            }

            yield return RuleAttribute(
                "pharmacy",
                "Pharmacy",
                value,
                "pharmacy",
                chunk.Index);
        }

        foreach (Match match in LabValueRegex().Matches(chunk.Text))
        {
            yield return RuleAttribute(
                $"lab_{match.Groups["name"].Value.ToLowerInvariant()}",
                $"{match.Groups["name"].Value.ToUpperInvariant()} lab value",
                $"{match.Groups["name"].Value.ToUpperInvariant()} {match.Groups["value"].Value}",
                "lab",
                chunk.Index);
        }

        foreach (Match match in AppointmentRegex().Matches(chunk.Text))
        {
            yield return RuleAttribute(
                "appointment_request",
                "Appointment request",
                CleanupValue(match.Value),
                "appointment",
                chunk.Index);
        }

        foreach (Match match in CallbackRegex().Matches(chunk.Text))
        {
            yield return RuleAttribute(
                "callback_request",
                "Callback request",
                CleanupValue(match.Value),
                "callback",
                chunk.Index);
        }

        foreach (Match match in AllergyRegex().Matches(chunk.Text))
        {
            yield return RuleAttribute(
                "allergy",
                "Allergy",
                CleanupValue(match.Groups["value"].Value),
                "allergy",
                chunk.Index);
        }

        foreach (Match match in GenericIdRegex().Matches(chunk.Text))
        {
            var key = NormalizeKey(match.Groups["label"].Value);
            yield return RuleAttribute(
                string.IsNullOrWhiteSpace(key) ? "identifier" : key,
                HumanizeKey(match.Groups["label"].Value),
                match.Groups["value"].Value.Trim().TrimEnd('.', ',', ';'),
                "identifier",
                chunk.Index);
        }
    }

    private async Task<(int ChunkIndex, IReadOnlyList<DynamicAttribute> Attributes, string? Warning, bool Retried, bool Failed)> ExtractChunkWithLimitAsync(
        TranscriptChunk chunk,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await ExtractChunkAsync(chunk, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<(int ChunkIndex, IReadOnlyList<DynamicAttribute> Attributes, string? Warning, bool Retried, bool Failed)> ExtractChunkAsync(
        TranscriptChunk chunk,
        CancellationToken cancellationToken)
    {
        var retried = false;
        try
        {
            var (content, warning, emptyContent) = await RequestOpenAiContentAsync(chunk, chunk.Text, cancellationToken);
            if (emptyContent)
            {
                retried = true;
                var retry = await RequestOpenAiContentAsync(chunk, CompactChunkText(chunk.Text), cancellationToken);
                content = retry.Content;
                warning = retry.Warning;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return (chunk.Index, [], warning ?? $"Dynamic AI extraction skipped for chunk {chunk.Index + 1}; no content was returned.", retried, true);
            }

            var attributes = ParseAttributes(content, chunk.Index);
            return (chunk.Index, attributes, null, retried, false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Azure OpenAI dynamic extraction timed out");
            return (chunk.Index, [], $"Dynamic AI extraction skipped for chunk {chunk.Index + 1} due to timeout.", retried, true);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Azure OpenAI dynamic extraction returned invalid JSON");
            return (chunk.Index, [], $"Dynamic AI extraction skipped for chunk {chunk.Index + 1} because OpenAI returned invalid JSON.", retried, true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure OpenAI dynamic extraction failed");
            return (chunk.Index, [], $"Dynamic AI extraction skipped for chunk {chunk.Index + 1}: {ex.Message}", retried, true);
        }
    }

    private async Task<(string? Content, string? Warning, bool EmptyContent)> RequestOpenAiContentAsync(
        TranscriptChunk chunk,
        string chunkText,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.AzureOpenAiTimeoutSeconds));

        try
        {
            var endpoint = configuration.AzureOpenAiEndpoint.TrimEnd('/');
            var deployment = Uri.EscapeDataString(configuration.AzureOpenAiDeployment);
            var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={configuration.AzureOpenAiApiVersion}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("api-key", configuration.AzureOpenAiKey);
            request.Content = JsonContent(new
            {
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = chunkText }
                },
                max_completion_tokens = 1800,
                response_format = new { type = "json_object" }
            });

            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Azure OpenAI dynamic extraction failed. Status={StatusCode}, Deployment={Deployment}, PromptLength={PromptLength}, BodyPreview={BodyPreview}",
                    (int)response.StatusCode,
                    configuration.AzureOpenAiDeployment,
                    chunkText.Length,
                    SafePreview(responseBody));
                return (null, $"Dynamic AI extraction skipped for chunk {chunk.Index + 1} with OpenAI status {(int)response.StatusCode}.", false);
            }

            using var document = JsonDocument.Parse(responseBody);

            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            var finishReason = document.RootElement
                .GetProperty("choices")[0]
                .TryGetProperty("finish_reason", out var finishReasonElement)
                    ? finishReasonElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogWarning(
                    "Azure OpenAI dynamic extraction returned empty content. Status={StatusCode}, Deployment={Deployment}, PromptLength={PromptLength}, FinishReason={FinishReason}, BodyPreview={BodyPreview}",
                    (int)response.StatusCode,
                    configuration.AzureOpenAiDeployment,
                    chunkText.Length,
                    finishReason,
                    SafePreview(responseBody));
                return (null, $"Dynamic AI extraction skipped for chunk {chunk.Index + 1}; OpenAI returned empty content.", true);
            }

            return (content, null, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"Dynamic AI extraction skipped for chunk {chunk.Index + 1} due to timeout.", false);
        }
    }

    private static string CompactChunkText(string text)
    {
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(80);
        var compact = string.Join('\n', lines);
        return compact.Length <= 3000 ? compact : compact[..3000];
    }

    private static string SafePreview(string value)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= 500 ? compact : compact[..500];
    }

    private static IReadOnlyList<DynamicAttribute> ParseAttributes(string content, int chunkIndex)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("attributes", out var attributesElement)
            || attributesElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Dynamic extraction JSON must include an attributes array.");
        }

        var attributes = new List<DynamicAttribute>();
        foreach (var item in attributesElement.EnumerateArray())
        {
            var key = ReadString(item, "key");
            var value = ReadString(item, "value");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            attributes.Add(new DynamicAttribute
            {
                Key = NormalizeKey(key),
                Label = ReadString(item, "label", HumanizeKey(key)),
                Value = value,
                Category = ReadString(item, "category", "case-note"),
                Confidence = ClampConfidence(ReadDouble(item, "confidence", 0.5)),
                Source = "azure-openai",
                ChunkIndex = chunkIndex
            });
        }

        return attributes;
    }

    private static IReadOnlyList<DynamicAttribute> Deduplicate(IEnumerable<DynamicAttribute> attributes)
    {
        var merged = new List<DynamicAttribute>();
        foreach (var attribute in attributes)
        {
            var existing = merged.FirstOrDefault(item =>
                string.Equals(item.Key, attribute.Key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Category, attribute.Category, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeForComparison(item.Value), NormalizeForComparison(attribute.Value), StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                merged.Add(attribute);
                continue;
            }

            existing.Confidence = Math.Max(existing.Confidence, attribute.Confidence);
        }

        return merged;
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback = "")
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? fallback
            : fallback;
    }

    private static double ReadDouble(JsonElement element, string propertyName, double fallback)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;
    }

    private static double ClampConfidence(double value) => Math.Clamp(value, 0, 1);

    private static string NormalizeKey(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().Trim('_');
    }

    private static string HumanizeKey(string key)
    {
        var normalized = NormalizeKey(key).Replace('_', ' ');
        return string.IsNullOrWhiteSpace(normalized)
            ? "Important attribute"
            : char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    private static string NormalizeForComparison(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static DynamicAttribute RuleAttribute(
        string key,
        string label,
        string value,
        string category,
        int chunkIndex) => new()
        {
            Key = NormalizeKey(key),
            Label = label,
            Value = CleanupValue(value),
            Category = category,
            Confidence = 0.7,
            Source = "rule-based",
            ChunkIndex = chunkIndex
        };

    private static string CleanupValue(string value) =>
        Regex.Replace(
            Regex.Replace(value.Trim().TrimEnd('.', ',', ';'), @"^(?:is\s+)?at\s+", "", RegexOptions.IgnoreCase),
            @"\s+",
            " ");

    private static bool IsUsefulPharmacyValue(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Regex.IsMatch(value, @"(?:նախընտրում|ո՞ր|which|preferred|\bեք\b)", RegexOptions.IgnoreCase);

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [GeneratedRegex(@"\b(?:pharmacy|drugstore|դեղատունը|դեղատուն)\s*(?:is|է|:|at|հասցեում է)?\s*(?<value>[A-ZԱ-Ֆ0-9][A-Za-zԱ-Ֆա-ֆ0-9 '&\-]+?)(?:\.|։|,|;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PharmacyRegex();

    [GeneratedRegex(@"\b(?<name>ALT|AST)\s*(?:is|:|=)?\s*(?<value>\d+(?:\.\d+)?(?:\s*(?:U/L|IU/L|mg/dL|mmol/L))?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LabValueRegex();

    [GeneratedRegex(@"\b(?:book|schedule|make|set up|need|wants?)(?:\s+a|\s+an)?\s+(?:follow-up\s+)?appointment\b[^.]*", RegexOptions.IgnoreCase)]
    private static partial Regex AppointmentRegex();

    [GeneratedRegex(@"\b(?:call me back|callback|call back|return my call)\b[^.]*", RegexOptions.IgnoreCase)]
    private static partial Regex CallbackRegex();

    [GeneratedRegex(@"\b(?:allergic to|allergy to|allergies include)\s*(?<value>[A-Za-z][A-Za-z\s,\-]+?)(?:\.|;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AllergyRegex();

    [GeneratedRegex(@"\b(?<label>insurance number|policy number|member id|patient id|case id|national id)\s*(?:is|:|#)?\s*(?<value>[A-Z0-9][A-Z0-9\-]{3,30})\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericIdRegex();
}
