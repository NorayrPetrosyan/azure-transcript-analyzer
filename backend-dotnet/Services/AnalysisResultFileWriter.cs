using System.Security.Cryptography;
using System.Text;
using TranscriptAnalyzer.Models;

namespace TranscriptAnalyzer.Services;

public sealed class AnalysisResultFileWriter(IWebHostEnvironment environment)
{
    private const string ResultsFolderName = "local-results";

    public async Task SaveAsync(
        AnalyzeResponse response,
        string? language,
        int transcriptLength,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(environment.ContentRootPath, ResultsFolderName);
        Directory.CreateDirectory(outputDirectory);

        var timestamp = createdAtUtc.ToString("yyyy-MM-dd'T'HHmmss'Z'");
        var uniqueId = RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
        var fileName = $"{timestamp}-{uniqueId}-analysis.txt";
        var filePath = Path.Combine(outputDirectory, fileName);

        await File.WriteAllTextAsync(
            filePath,
            BuildText(response, language, transcriptLength, createdAtUtc),
            Encoding.UTF8,
            cancellationToken);
    }

    private static string BuildText(
        AnalyzeResponse response,
        string? language,
        int transcriptLength,
        DateTimeOffset createdAtUtc)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Azure AI Transcript Analyzer - Analysis Result");
        builder.AppendLine("============================================");
        builder.AppendLine($"Created At UTC: {createdAtUtc:yyyy-MM-dd HH:mm:ss}Z");
        builder.AppendLine($"Language: {Normalize(language)}");
        builder.AppendLine($"Transcript Length: {transcriptLength}");
        builder.AppendLine($"Role Method: {response.RoleMethod}");
        builder.AppendLine($"Used Chunking: {response.ProcessingInfo.UsedChunking}");
        builder.AppendLine($"Chunk Count: {response.ProcessingInfo.ChunkCount}");
        builder.AppendLine($"Processing Language: {Normalize(response.ProcessingInfo.Language)}");
        builder.AppendLine($"Large Transcript Safe Mode: {response.ProcessingInfo.LargeTranscriptSafeMode}");
        builder.AppendLine($"Large Transcript Threshold Characters: {response.ProcessingInfo.LargeTranscriptThresholdCharacters}");
        builder.AppendLine($"Failed OpenAI Chunks: {response.ProcessingInfo.FailedOpenAiChunks}");
        builder.AppendLine($"OpenAI Failed Chunks: {FormatChunkList(response.ProcessingInfo.OpenAiFailedChunks)}");
        builder.AppendLine($"Dynamic AI Skipped Chunks: {FormatChunkList(response.ProcessingInfo.DynamicAiSkippedChunks)}");
        builder.AppendLine($"Fallback Used: {response.ProcessingInfo.FallbackUsed}");
        builder.AppendLine($"Retried OpenAI Chunks: {response.ProcessingInfo.RetriedOpenAiChunks}");
        builder.AppendLine($"Fallback Chunks: {FormatChunkList(response.ProcessingInfo.UsedFallbackForChunks)}");
        builder.AppendLine($"Total Duration Ms: {response.ProcessingInfo.TotalDurationMs}");
        builder.AppendLine($"Azure OpenAI Duration Ms: {response.ProcessingInfo.AzureOpenAiDurationMs}");
        builder.AppendLine($"Azure Language Duration Ms: {response.ProcessingInfo.AzureLanguageDurationMs}");
        builder.AppendLine($"Local Extraction Duration Ms: {response.ProcessingInfo.LocalExtractionDurationMs}");
        builder.AppendLine();

        builder.AppendLine("Conversation");
        builder.AppendLine("------------");
        if (response.Conversation.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            for (var i = 0; i < response.Conversation.Count; i++)
            {
                var turn = response.Conversation[i];
                builder.AppendLine($"{i + 1}. {turn.Role}: {turn.Text}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Extracted Attributes");
        builder.AppendLine("--------------------");
        AppendAttribute(builder, "Name", response.ExtractedAttributes.Name);
        AppendAttribute(builder, "Address", response.ExtractedAttributes.Address);
        AppendAttribute(builder, "Date Of Birth", response.ExtractedAttributes.DateOfBirth);
        AppendAttribute(builder, "Social Security Number", response.ExtractedAttributes.SocialSecurityNumber);
        AppendAttribute(builder, "Phone Number", response.ExtractedAttributes.PhoneNumber);
        AppendAttribute(builder, "Email", response.ExtractedAttributes.Email);
        AppendAttribute(builder, "Doctor Name", response.ExtractedAttributes.DoctorName);
        AppendList(builder, "Conditions", response.ExtractedAttributes.Conditions);
        AppendList(builder, "Medications", response.ExtractedAttributes.Medications);
        AppendList(builder, "Other", response.ExtractedAttributes.Other);

        builder.AppendLine();
        builder.AppendLine("Dynamic Attributes");
        builder.AppendLine("------------------");
        if (response.DynamicAttributes.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var attribute in response.DynamicAttributes)
            {
                builder.AppendLine($"- {attribute.Label} ({attribute.Key})");
                builder.AppendLine($"  Value: {attribute.Value}");
                builder.AppendLine($"  Category: {attribute.Category}");
                builder.AppendLine($"  Confidence: {attribute.Confidence:0.###}");
                builder.AppendLine($"  Source: {attribute.Source}");
                builder.AppendLine($"  Chunk Index: {attribute.ChunkIndex}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Raw Azure Entities");
        builder.AppendLine("------------------");
        if (response.RawAzureEntities.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var entity in response.RawAzureEntities)
            {
                var subcategory = string.IsNullOrWhiteSpace(entity.Subcategory)
                    ? ""
                    : $" / {entity.Subcategory}";
                builder.AppendLine($"- {entity.Text} [{entity.Category}{subcategory}] confidence={entity.Confidence:0.###}");
            }
        }

        if (!string.IsNullOrWhiteSpace(response.Warning))
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            builder.AppendLine("--------");
            builder.AppendLine(response.Warning);
        }

        return builder.ToString();
    }

    private static void AppendAttribute(StringBuilder builder, string label, string value)
    {
        builder.AppendLine($"{label}: {Normalize(value)}");
    }

    private static void AppendList(StringBuilder builder, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            builder.AppendLine($"{label}: (none)");
            return;
        }

        builder.AppendLine($"{label}:");
        foreach (var value in values)
        {
            builder.AppendLine($"- {value}");
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(not detected)" : value.Trim();

    private static string FormatChunkList(IReadOnlyList<int> chunks) =>
        chunks.Count == 0 ? "(none)" : string.Join(", ", chunks);
}
