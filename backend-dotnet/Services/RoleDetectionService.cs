using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed partial class RoleDetectionService(
    IHttpClientFactory httpClientFactory,
    ConfigurationService configuration,
    ILogger<RoleDetectionService> logger)
{
    private const string SystemPrompt = """
        You are a call-center transcript analyzer.
        Split the transcript into conversation turns with roles "Agent", "Caller", or "Unknown".
        Preserve original wording and language. Return compact JSON only:
        {"turns":[{"role":"Agent","text":"..."},{"role":"Caller","text":"..."}]}
        """;

    public async Task<(IReadOnlyList<ConversationTurn> Turns, string Method, IReadOnlyList<string> Warnings, OpenAiProcessingMetrics Metrics)> DetectAsync(
        string transcript,
        IReadOnlyList<TranscriptChunk> chunks,
        CancellationToken cancellationToken,
        bool allowOpenAiRoleDetection = true)
    {
        var metrics = new OpenAiProcessingMetrics();
        var stopwatch = Stopwatch.StartNew();

        if (ContainsExplicitLabels(transcript))
        {
            stopwatch.Stop();
            metrics.DurationMs = stopwatch.ElapsedMilliseconds;
            return (ParseExplicitLabels(transcript), "labels", [], metrics);
        }

        if (!allowOpenAiRoleDetection)
        {
            stopwatch.Stop();
            metrics.DurationMs = stopwatch.ElapsedMilliseconds;
            return (
                SpeakerFallback(transcript),
                "fallback",
                ["Large transcript safe mode skipped OpenAI role detection; fallback speaker labels were used."],
                metrics);
        }

        if (configuration.AzureOpenAiConfigured)
        {
            using var semaphore = new SemaphoreSlim(configuration.MaxConcurrentChunks);
            var results = await Task.WhenAll(chunks.Select(chunk => DetectChunkWithLimitAsync(chunk, semaphore, cancellationToken)));
            var openAiTurns = new List<ConversationTurn>();
            var warnings = new List<string>();

            foreach (var result in results.OrderBy(result => result.ChunkIndex))
            {
                warnings.AddRange(result.Warnings);
                metrics.FailedChunks += result.Failed ? 1 : 0;
                metrics.RetriedChunks += result.Retried ? 1 : 0;

                if (result.UsedFallback)
                {
                    metrics.FallbackChunkNumbers.Add(result.ChunkIndex + 1);
                }

                openAiTurns.AddRange(result.Turns);
            }

            stopwatch.Stop();
            metrics.DurationMs = stopwatch.ElapsedMilliseconds;
            if (openAiTurns.Count > 0)
            {
                return (DeduplicateAdjacentTurns(openAiTurns), warnings.Count == 0 ? "openai" : "openai-partial", warnings, metrics);
            }
        }

        stopwatch.Stop();
        metrics.DurationMs = stopwatch.ElapsedMilliseconds;
        return (SpeakerFallback(transcript), "fallback", [], metrics);
    }

    private async Task<(int ChunkIndex, IReadOnlyList<ConversationTurn> Turns, IReadOnlyList<string> Warnings, bool Retried, bool Failed, bool UsedFallback)> DetectChunkWithLimitAsync(
        TranscriptChunk chunk,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var warnings = new List<string>();
            var (chunkTurns, chunkWarning, emptyContent) = await TryDetectChunkWithOpenAiAsync(chunk, chunk.Text, cancellationToken);
            var retried = false;

            if (emptyContent)
            {
                retried = true;
                var retry = await TryDetectChunkWithOpenAiAsync(chunk, CompactChunkText(chunk.Text), cancellationToken);
                chunkTurns = retry.Turns;
                chunkWarning = retry.Warning;
            }

            if (chunkTurns.Count > 0 && chunkTurns.All(turn => turn.Role is "Agent" or "Caller"))
            {
                if (retried)
                {
                    warnings.Add($"OpenAI role detection returned empty content for chunk {chunk.Index + 1}; retry succeeded.");
                }

                return (chunk.Index, chunkTurns, warnings, retried, false, false);
            }

            warnings.Add(chunkWarning ?? $"OpenAI role detection failed for chunk {chunk.Index + 1}; fallback speaker labels were used.");
            return (chunk.Index, SpeakerFallback(chunk.Text), warnings, retried, true, true);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static bool ContainsExplicitLabels(string transcript)
    {
        return SplitCandidateTurns(transcript).Any(line => ExplicitLabelRegex().IsMatch(line));
    }

    private static IReadOnlyList<ConversationTurn> ParseExplicitLabels(string transcript)
    {
        var turns = new List<ConversationTurn>();
        foreach (var line in SplitCandidateTurns(transcript))
        {
            var match = ExplicitLabelRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var role = NormalizeExplicitRole(match.Groups[1].Value);
            var text = ExplicitLabelRegex().Replace(line, "").Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                turns.Add(new ConversationTurn { Role = role, Text = text });
            }
        }

        return turns.Count > 0 ? turns : SpeakerFallback(transcript);
    }

    private async Task<(IReadOnlyList<ConversationTurn> Turns, string? Warning, bool EmptyContent)> TryDetectChunkWithOpenAiAsync(
        TranscriptChunk chunk,
        string chunkText,
        CancellationToken cancellationToken)
    {
        if (!configuration.AzureOpenAiConfigured)
        {
            return ([], null, false);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.AzureOpenAiTimeoutSeconds));

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
                    "Azure OpenAI role detection failed. Status={StatusCode}, Deployment={Deployment}, PromptLength={PromptLength}, BodyPreview={BodyPreview}",
                    (int)response.StatusCode,
                    configuration.AzureOpenAiDeployment,
                    chunkText.Length,
                    SafePreview(responseBody));
                return ([], $"OpenAI role detection failed for chunk {chunk.Index + 1} with status {(int)response.StatusCode}; fallback speaker labels were used.", false);
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
                    "Azure OpenAI role detection returned empty content. Status={StatusCode}, Deployment={Deployment}, PromptLength={PromptLength}, FinishReason={FinishReason}, BodyPreview={BodyPreview}",
                    (int)response.StatusCode,
                    configuration.AzureOpenAiDeployment,
                    chunkText.Length,
                    finishReason,
                    SafePreview(responseBody));
                return ([], $"OpenAI role detection returned empty content for chunk {chunk.Index + 1}; fallback speaker labels were used.", true);
            }

            using var roleDocument = JsonDocument.Parse(content);
            var turnsElement = roleDocument.RootElement.TryGetProperty("turns", out var turns)
                ? turns
                : roleDocument.RootElement;

            if (turnsElement.ValueKind != JsonValueKind.Array)
            {
                return ([], $"OpenAI role detection returned invalid JSON for chunk {chunk.Index + 1}; fallback speaker labels were used.", false);
            }

            var parsed = new List<ConversationTurn>();
            foreach (var item in turnsElement.EnumerateArray())
            {
                var role = item.TryGetProperty("role", out var roleElement)
                    ? roleElement.GetString()
                    : "Unknown";
                var text = item.TryGetProperty("text", out var textElement)
                    ? textElement.GetString()
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    parsed.Add(new ConversationTurn
                    {
                        Role = role is "Agent" or "Caller" ? role : "Unknown",
                        Text = text.Trim()
                    });
                }
            }

            return (parsed, null, false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Azure OpenAI role detection timed out");
            return ([], $"OpenAI role detection timed out for chunk {chunk.Index + 1}; fallback speaker labels were used.", false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Azure OpenAI role detection failed");
            return ([], $"OpenAI role detection failed for chunk {chunk.Index + 1}; fallback speaker labels were used. {ex.Message}", false);
        }
    }

    private static string CompactChunkText(string text)
    {
        var lines = SplitCandidateTurns(text).Take(80).ToList();
        var compact = lines.Count > 0 ? string.Join('\n', lines) : text;
        return compact.Length <= 3000 ? compact : compact[..3000];
    }

    private static string SafePreview(string value)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= 500 ? compact : compact[..500];
    }

    private static IReadOnlyList<ConversationTurn> SpeakerFallback(string transcript)
    {
        var parts = SplitCandidateTurns(transcript).ToList();
        if (parts.Count == 0)
        {
            parts = SentenceRegex().Split(transcript)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();
        }

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(transcript))
        {
            parts.Add(transcript.Trim());
        }

        return parts.Select((text, index) => new ConversationTurn
        {
            Role = index % 2 == 0 ? "Speaker 1" : "Speaker 2",
            Text = text
        }).ToList();
    }

    private static IReadOnlyList<ConversationTurn> DeduplicateAdjacentTurns(IEnumerable<ConversationTurn> turns)
    {
        var deduped = new List<ConversationTurn>();
        foreach (var turn in turns)
        {
            var previous = deduped.LastOrDefault();
            if (previous is not null
                && previous.Role == turn.Role
                && string.Equals(previous.Text, turn.Text, StringComparison.Ordinal))
            {
                continue;
            }

            if (deduped.TakeLast(20).Any(recent =>
                    recent.Role == turn.Role
                    && string.Equals(recent.Text, turn.Text, StringComparison.Ordinal)))
            {
                continue;
            }

            deduped.Add(turn);
        }

        return deduped;
    }

    private static IEnumerable<string> SplitCandidateTurns(string transcript)
    {
        var lines = transcript
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count > 1)
        {
            return lines;
        }

        return transcript
            .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(block => !string.IsNullOrWhiteSpace(block));
    }

    private static string NormalizeExplicitRole(string label)
    {
        var normalized = label.Trim().ToLowerInvariant();
        return normalized switch
        {
            "agent" or "representative" or "rep" or "support" or "operator" => "Agent",
            "գործակալ" or "օպերատոր" => "Agent",
            "caller" or "customer" => "Caller",
            "զանգահարող" or "հաճախորդ" => "Caller",
            _ => "Speaker 1"
        };
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [GeneratedRegex(@"^(agent|caller|customer|representative|rep|support|operator|Գործակալ|Զանգահարող|Հաճախորդ|Օպերատոր)\s*[:\-]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitLabelRegex();

    [GeneratedRegex(@"(?<=[.!?։])\s+")]
    private static partial Regex SentenceRegex();
}
